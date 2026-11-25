// Copy into an Editor assembly in the disposable .tmp/apv-unity project.
// APV_MATERIAL_SMOKE=1 Unity.exe -batchmode -force-d3d11 -projectPath ...
//     -executeMethod APVStandaloneMaterialSmoke.Run -logFile ...
// Requires a real graphics device; do not pass -nographics or -quit.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class APVStandaloneMaterialSmoke
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const string ReportPath = "apv-standalone-material-result.json";
    static readonly List<string> Errors = new List<string>();
    static readonly List<string> Checks = new List<string>();
    static readonly List<Measurement> Measurements = new List<Measurement>();
    static readonly List<UnityEngine.Object> Objects = new List<UnityEngine.Object>();
    static readonly Stack<IEnumerator> Routines = new Stack<IEnumerator>();
    static object s_Baker;
    static NativeArray<Vector3> s_Positions;
    static Type s_BakerType, s_SettingsType;
    static Vector3 s_Last;
    static Color s_PreviousAmbient;
    static double s_Started;
    static bool s_Finished;
    static Report s_Report;

    [Serializable]
    sealed class Measurement
    {
        public string name;
        public Vector3 irradianceL0;
    }

    [Serializable]
    sealed class Report
    {
        public bool passed, nativeBakeObserved, resourcesDisposed;
        public string unityVersion, device, graphicsApi, error;
        public double elapsedSeconds;
        public string[] checks, errors;
        public Measurement[] measurements;
    }

    public static void Run()
    {
        // Never replace or save scenes in a user's interactive editor.
        if (!Application.isBatchMode || Environment.GetEnvironmentVariable("APV_MATERIAL_SMOKE") != "1")
            throw new InvalidOperationException("Run material smoke only in a disposable batch project with APV_MATERIAL_SMOKE=1.");

        s_Report = new Report { unityVersion = Application.unityVersion,
            device = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString() };
        s_Started = EditorApplication.timeSinceStartup;
        s_PreviousAmbient = RenderSettings.ambientLight;
        Application.logMessageReceived += OnLog;
        try
        {
            Require(SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback &&
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A compute-capable graphics device with GPU readback is required.");
            Require(!Lightmapping.isRunning && !AdaptiveProbeVolumes.isRunning, "A bake is already running.");
            s_BakerType = FindType("UnityEngine.Rendering.StandaloneAPVLightingBaker");
            s_SettingsType = FindType("UnityEngine.Rendering.StandaloneAPVBakeSettings");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Mixed lights must contribute only indirect light in this fixture.
            // A project default of Subtractive would add white direct lighting
            // and obscure the material-channel assertions.
            Lightmapping.lightingSettings = Track(new LightingSettings
            {
                name = "Material smoke Baked Indirect settings",
                mixedBakeMode = MixedLightingMode.IndirectOnly
            });
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.skybox = null;
            RenderSettings.ambientLight = Color.black;
            Routines.Push(RunCases());
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    static IEnumerator RunCases()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        Require(shader != null, "URP Lit shader is missing.");
        var floorMaterial = Track(new Material(shader) { name = "Material smoke floor" });
        floorMaterial.SetColor("_BaseColor", Color.white);
        floorMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        var floor = AddPlane("Receiver", 0, floorMaterial, new Vector2(0.25f, 0.5f));
        var sun = Track(new GameObject("Material smoke sun")).AddComponent<Light>();
        sun.type = LightType.Directional;
        // Probe direct lighting is white and would hide the material-channel
        // assertions below. Mixed isolates the first reflected contribution.
        sun.lightmapBakeType = LightmapBakeType.Mixed;
        // New Light components default to None, which intentionally bypasses
        // visibility and would make the opaque shadow control inconclusive.
        sun.shadows = LightShadows.Hard;
        sun.color = Color.white;
        sun.intensity = 1;
        sun.transform.rotation = Quaternion.Euler(90, 0, 0);

        yield return Bake("untextured white reference");
        Vector3 white = s_Last;
        Require(white.x > 0.1f && white.y > 0.1f && white.z > 0.1f, "Directional light did not illuminate the receiver.");

        var baseMap = Texture("Non-readable red/blue base map", Color.red, Color.blue, false);
        Require(!baseMap.isReadable, "Base-map fixture must be non-readable.");
        floorMaterial.SetTexture("_BaseMap", baseMap);
        yield return Bake("base map red UV");
        Dominant(s_Last, 0, "Base map red texels did not change baked SH.");
        Near(s_Last.x, white.x, 0.06f, "Red texture changed the expected red-channel energy.");
        Checks.Add("Non-readable base map changes actual GPU irradiance SH");

        Vector3 albedoRed = s_Last;
        SetLightmapUV(floor, new[] { new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f),
            new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f) });
        yield return Bake("albedo UV0 red with independent UV2 blue");
        Dominant(s_Last, 0, "Lightmap UV2 replaced the albedo material UV0.");
        Require((s_Last - albedoRed).sqrMagnitude < 1e-8f, "Adding lightmap UV2 changed albedo irradiance.");
        SetLightmapUV(floor, new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right });
        yield return Bake("albedo remains red after changing only UV2 chart");
        Require((s_Last - albedoRed).sqrMagnitude < 1e-8f, "Changing only lightmap UV2 changed albedo irradiance.");
        SetLightmapUV(floor, Array.Empty<Vector2>());
        Checks.Add("Albedo uses original UV0 and remains unchanged when an independent UV2 channel is added or modified");

        SetUV(floor, new Vector2(0.75f, 0.5f));
        yield return Bake("base map blue UV");
        Dominant(s_Last, 2, "Mesh UV changes were ignored by the baker.");
        SetUV(floor, new Vector2(0.25f, 0.5f));
        floorMaterial.SetTextureOffset("_BaseMap", new Vector2(0.5f, 0));
        yield return Bake("base map offset blue");
        Dominant(s_Last, 2, "Base map offset was ignored.");
        floorMaterial.SetTextureOffset("_BaseMap", Vector2.zero);
        floorMaterial.SetTextureScale("_BaseMap", new Vector2(3, 1));
        yield return Bake("base map tiling blue");
        Dominant(s_Last, 2, "Base map tiling was ignored.");
        floorMaterial.SetTextureScale("_BaseMap", Vector2.one);
        floorMaterial.SetTextureOffset("_BaseMap", new Vector2(1, 0));
        yield return Bake("base map repeat red");
        Dominant(s_Last, 0, "Repeat wrapping was not applied after the UV transform.");
        baseMap.wrapMode = TextureWrapMode.Clamp;
        yield return Bake("base map clamp blue");
        Dominant(s_Last, 2, "Clamp wrapping was not applied after the UV transform.");
        floorMaterial.SetTextureOffset("_BaseMap", new Vector2(-0.5f, 0));
        baseMap.wrapMode = TextureWrapMode.Repeat;
        yield return Bake("base map negative repeat blue");
        Dominant(s_Last, 2, "Negative Repeat UVs did not wrap -0.25 to 0.75.");
        baseMap.wrapMode = TextureWrapMode.Mirror;
        yield return Bake("base map negative mirror red");
        Dominant(s_Last, 0, "Mirror UVs did not reflect -0.25 to 0.25.");
        floorMaterial.SetTextureOffset("_BaseMap", new Vector2(-2, 0));
        baseMap.wrapMode = TextureWrapMode.MirrorOnce;
        yield return Bake("base map negative mirror once blue");
        Dominant(s_Last, 2, "MirrorOnce UVs did not reflect and clamp -1.75 to 1.");
        Checks.Add("Mesh UVs, material tiling/offset and Repeat/Clamp/Mirror/MirrorOnce wrapping affect baked SH, including negative UVs");

        floorMaterial.SetTextureOffset("_BaseMap", Vector2.zero);
        var graySrgb = Texture("Non-readable sRGB gray", Color.gray, Color.gray, false);
        floorMaterial.SetTexture("_BaseMap", graySrgb);
        yield return Bake("sRGB gray base map");
        float expectedLinear = Mathf.GammaToLinearSpace(0.5f);
        for (int channel = 0; channel < 3; channel++)
            Require(Mathf.Abs(s_Last[channel] / white[channel] - expectedLinear) < 0.035f,
                "sRGB texture must be decoded exactly once before SH integration: " + s_Last + " / " + white);
        var grayLinear = Texture("Non-readable linear gray", Color.gray, Color.gray, true);
        floorMaterial.SetTexture("_BaseMap", grayLinear);
        yield return Bake("linear gray base map");
        for (int channel = 0; channel < 3; channel++)
            Require(Mathf.Abs(s_Last[channel] / white[channel] - 0.5f) < 0.035f,
                "Linear texture must retain its linear values: " + s_Last + " / " + white);
        Checks.Add("sRGB and linear texture sampling produce the expected radiometric SH ratios");

        sun.enabled = false;
        sun.intensity = 0;
        floorMaterial.SetTexture("_BaseMap", null);
        floorMaterial.SetColor("_BaseColor", Color.black);
        floorMaterial.EnableKeyword("_EMISSION");
        floorMaterial.SetColor("_EmissionColor", Color.white);
        floorMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
        yield return Bake("constant white emission reference");
        Vector3 emissionWhite = s_Last;
        Require(emissionWhite.x > 0.1f, "Emissive receiver did not illuminate the probe.");
        // URP MaterialPostprocessor.UpgradeV3 explicitly converts legacy emission
        // only because the old shader lacked [HDR]. Current [HDR] color values
        // are already linear and must not receive another Color.linear conversion.
        int emissionProperty = shader.FindPropertyIndex("_EmissionColor");
        Require(emissionProperty >= 0 && (shader.GetPropertyFlags(emissionProperty) & ShaderPropertyFlags.HDR) != 0,
            "The HDR emission control requires a shader with an HDR color property.");
        floorMaterial.SetColor("_EmissionColor", new Color(0.5f, 2, 4, 1));
        yield return Bake("HDR emission keeps linear color and intensity");
        Near(s_Last, Vector3.Scale(emissionWhite, new Vector3(0.5f, 2, 4)), 0.035f,
            "HDR emission color was converted again or its intensity was clamped.");
        Checks.Add("HDR emission preserves linear channel ratios 0.5/2/4, including intensity above one");
        floorMaterial.SetColor("_EmissionColor", Color.white);
        var uvEmissionMap = Texture("Non-readable red/blue emission UV map", Color.red, Color.blue, false);
        floorMaterial.SetTexture("_EmissionMap", uvEmissionMap);
        yield return Bake("emission UV0 red reference without UV2");
        Dominant(s_Last, 0, "Emission UV0 red reference did not illuminate the probe.");
        Vector3 emissionRed = s_Last;
        SetLightmapUV(floor, new[] { new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f),
            new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f) });
        yield return Bake("emission UV0 red with independent UV2 blue");
        Dominant(s_Last, 0, "Lightmap UV2 replaced the emission material UV0.");
        Require((s_Last - emissionRed).sqrMagnitude < 1e-8f, "Adding lightmap UV2 changed emission irradiance.");
        SetLightmapUV(floor, new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right });
        yield return Bake("emission remains red after changing only UV2 chart");
        Require((s_Last - emissionRed).sqrMagnitude < 1e-8f, "Changing only lightmap UV2 changed emission irradiance.");
        SetLightmapUV(floor, Array.Empty<Vector2>());
        Checks.Add("Emission uses original UV0 and remains unchanged when an independent UV2 channel is added or modified");

        var emissionMap = Texture("Non-readable green/red emission map", Color.green, Color.red, false);
        floorMaterial.SetTexture("_EmissionMap", emissionMap);
        floorMaterial.DisableKeyword("_EMISSION");
        yield return Bake("disabled emission keyword with retained color texture and GI flags");
        Require(s_Last.magnitude < 0.0001f,
            "A disabled URP emission keyword must produce zero emission even when color, texture and baked GI flags remain assigned.");
        Checks.Add("Disabling the URP emission keyword removes actual GPU emission despite retained material data");
        floorMaterial.EnableKeyword("_EMISSION");
        yield return Bake("emission map green");
        Dominant(s_Last, 1, "Emission texture did not change baked SH.");
        Near(s_Last.y, emissionWhite.y, 0.06f, "Emission texture changed expected green-channel energy.");
        // URP Lit samples emission with the base-map UV transform.
        floorMaterial.SetTextureOffset("_BaseMap", new Vector2(0.5f, 0));
        yield return Bake("emission map transformed red");
        Dominant(s_Last, 0, "Emission did not use URP Lit's transformed base-map UVs.");
        Checks.Add("Non-readable emission texture and its material UV transform change baked SH");

        floorMaterial.SetTexture("_EmissionMap", null);
        floorMaterial.SetTextureOffset("_BaseMap", Vector2.zero);
        var blockerMaterial = Track(new Material(shader) { name = "Material smoke cutout" });
        blockerMaterial.SetColor("_BaseColor", Color.black);
        blockerMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        var blocker = AddPlane("Cutout occluder", 0.5f, blockerMaterial, new Vector2(0.25f, 0.5f));
        var transparentMap = Texture("Non-readable transparent map", new Color(1, 1, 1, 0), new Color(1, 1, 1, 0), false);
        blockerMaterial.SetTexture("_BaseMap", transparentMap);
        SetClip(blockerMaterial, true);
        yield return Bake("cutout primary ray transmits emission");
        Near(s_Last, emissionWhite, 0.06f, "Alpha-cutout primary rays did not reach the emissive surface behind them.");
        SetClip(blockerMaterial, false);
        blockerMaterial.SetColor("_BaseColor", new Color(0, 0, 0, 0.1f));
        yield return Bake("opaque low alpha blocks primary rays");
        Require(s_Last.magnitude < emissionWhite.magnitude * 0.02f,
            "Opaque materials with alpha below one must still block primary rays.");
        Checks.Add("Cutout primary rays pass through transparent texels; opaque low-alpha materials remain opaque");

        blocker.enabled = false;
        sun.enabled = true;
        sun.intensity = 1;
        floorMaterial.SetColor("_BaseColor", Color.white);
        floorMaterial.SetColor("_EmissionColor", Color.black);
        floorMaterial.DisableKeyword("_EMISSION");
        floorMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        yield return Bake("unblocked shadow reference");
        Vector3 unblocked = s_Last;
        blocker.transform.position = new Vector3(0, 2, 0);
        blocker.enabled = true;
        blockerMaterial.SetColor("_BaseColor", Color.black);
        SetClip(blockerMaterial, true);
        yield return Bake("cutout shadow ray transmits sunlight");
        Near(s_Last, unblocked, 0.06f, "Alpha-cutout shadow rays did not transmit the directional light.");
        SetClip(blockerMaterial, false);
        yield return Bake("opaque shadow control");
        Require(s_Last.magnitude < unblocked.magnitude * 0.02f,
            "Opaque control failed to block sunlight; the cutout shadow assertion would be inconclusive.");
        sun.shadows = LightShadows.None;
        yield return Bake("disabled shadows ignore opaque control");
        Near(s_Last, unblocked, 0.06f, "Disabling light shadows did not restore the unblocked reflected sunlight.");
        Checks.Add("Cutout shadow rays transmit sunlight; opaque geometry blocks it only when light shadows are enabled");
    }

    static IEnumerator Bake(string name)
    {
        var settings = Activator.CreateInstance(s_SettingsType);
        Set(settings, "samples", 2048);
        Set(settings, "directSamples", 2048);
        Set(settings, "bounces", 1);
        Set(settings, "probesPerBatch", 1);
        Set(settings, "environmentIntensity", 0.0f);
        s_Baker = Activator.CreateInstance(s_BakerType, Flags, null, new[] { settings }, null);
        s_Positions = new NativeArray<Vector3>(new[] { new Vector3(0, 1, 0) }, Allocator.Persistent);
        InvokeBaker("Initialize", false, s_Positions);
        Require(GetBakerProperty<ulong>("stepCount") == 1, "Material bake must contain exactly one probe.");
        while (GetBakerProperty<ulong>("currentStep") < GetBakerProperty<ulong>("stepCount"))
        {
            Require((bool)InvokeBaker("Step"), "Lighting baker failed during case: " + name);
            yield return null;
        }
        var sh = GetBakerProperty<NativeArray<SphericalHarmonicsL2>>("irradiance")[0];
        for (int channel = 0; channel < 3; channel++)
            for (int coefficient = 0; coefficient < 9; coefficient++)
                Require(!float.IsNaN(sh[channel, coefficient]) && !float.IsInfinity(sh[channel, coefficient]),
                    "GPU returned non-finite SH for " + name);
        s_Last = new Vector3(sh[0, 0], sh[1, 0], sh[2, 0]);
        Measurements.Add(new Measurement { name = name, irradianceL0 = s_Last });
        DisposeBaker();
        Require(Errors.Count == 0, "Unity logged errors during " + name + ": " + string.Join("\n", Errors));
    }

    static MeshRenderer AddPlane(string name, float height, Material material, Vector2 uv)
    {
        var go = Track(new GameObject(name));
        go.transform.position = new Vector3(0, height, 0);
        var mesh = Track(new Mesh { name = name + " mesh" });
        mesh.vertices = new[] { new Vector3(-20, 0, -20), new Vector3(-20, 0, 20),
            new Vector3(20, 0, 20), new Vector3(20, 0, -20) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        mesh.uv = new[] { uv, uv, uv, uv };
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
        return renderer;
    }

    static void SetUV(MeshRenderer renderer, Vector2 uv)
        => renderer.GetComponent<MeshFilter>().sharedMesh.uv = new[] { uv, uv, uv, uv };

    static void SetLightmapUV(MeshRenderer renderer, Vector2[] uv)
        => renderer.GetComponent<MeshFilter>().sharedMesh.uv2 = uv;

    static Texture2D Texture(string name, Color left, Color right, bool linear)
    {
        var texture = Track(new Texture2D(16, 4, TextureFormat.RGBA32, false, linear)
        { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat });
        var pixels = new Color[64];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 16; x++) pixels[y * 16 + x] = x < 8 ? left : right;
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return texture;
    }

    static void SetClip(Material material, bool clip)
    {
        material.SetFloat("_Surface", 0);
        material.SetFloat("_AlphaClip", clip ? 1 : 0);
        material.SetFloat("_Cutoff", 0.5f);
        material.SetOverrideTag("RenderType", clip ? "TransparentCutout" : "Opaque");
        if (clip) material.EnableKeyword("_ALPHATEST_ON");
        else material.DisableKeyword("_ALPHATEST_ON");
    }

    static void Dominant(Vector3 value, int channel, string message)
    {
        Require(value[channel] > 0.1f, message + " Insufficient signal: " + value);
        for (int other = 0; other < 3; other++)
            if (other != channel) Require(value[channel] > value[other] * 8 + 0.01f, message + " SH L0: " + value);
    }

    static void Near(Vector3 actual, Vector3 expected, float tolerance, string message)
    { for (int channel = 0; channel < 3; channel++) Near(actual[channel], expected[channel], tolerance, message); }

    static void Near(float actual, float expected, float tolerance, string message)
        => Require(Mathf.Abs(actual - expected) <= Mathf.Max(0.005f, Mathf.Abs(expected) * tolerance),
            message + " Actual=" + actual + ", expected=" + expected);

    static void Tick()
    {
        try
        {
            Require(EditorApplication.timeSinceStartup - s_Started < 300, "Material GPU regression timed out.");
            s_Report.nativeBakeObserved |= Lightmapping.isRunning;
            Require(!s_Report.nativeBakeObserved, "Native lightmapping unexpectedly started.");
            while (Routines.Count != 0)
            {
                var routine = Routines.Peek();
                if (!routine.MoveNext()) { (Routines.Pop() as IDisposable)?.Dispose(); continue; }
                if (routine.Current is IEnumerator nested) { Routines.Push(nested); continue; }
                return;
            }
            Finish(null);
        }
        catch (Exception exception) { Finish(exception); }
    }

    static void DisposeBaker()
    {
        ((IDisposable)s_Baker)?.Dispose();
        s_Baker = null;
        if (s_Positions.IsCreated) s_Positions.Dispose();
        s_Positions = default;
    }

    static void Finish(Exception error)
    {
        if (s_Finished) return;
        s_Finished = true;
        EditorApplication.update -= Tick;
        try
        {
            DisposeBaker();
            s_Report.resourcesDisposed = s_Baker == null && !s_Positions.IsCreated;
            foreach (var item in Objects) if (item != null) UnityEngine.Object.DestroyImmediate(item);
            Objects.Clear();
            RenderSettings.ambientLight = s_PreviousAmbient;
        }
        catch (Exception cleanup) { error = error == null ? cleanup : new AggregateException(error, cleanup); }
        s_Report.elapsedSeconds = EditorApplication.timeSinceStartup - s_Started;
        s_Report.passed = error == null && Errors.Count == 0 && !s_Report.nativeBakeObserved && s_Report.resourcesDisposed;
        s_Report.error = error?.ToString();
        s_Report.errors = Errors.ToArray();
        s_Report.checks = Checks.ToArray();
        s_Report.measurements = Measurements.ToArray();
        Application.logMessageReceived -= OnLog;
        File.WriteAllText(ReportPath, JsonUtility.ToJson(s_Report, true));
        Debug.Log("APV_MATERIAL_SMOKE " + (s_Report.passed ? "PASS" : "FAIL") + " report=" + Path.GetFullPath(ReportPath));
        if (error != null) Debug.LogException(error);
        if (Application.isBatchMode) EditorApplication.Exit(s_Report.passed ? 0 : 1);
    }

    static T Track<T>(T value) where T : UnityEngine.Object { Objects.Add(value); return value; }
    static void OnLog(string message, string stack, LogType type)
    { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Errors.Add(message); }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static Type FindType(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        { var type = assembly.GetType(name, false); if (type != null) return type; }
        throw new TypeLoadException(name);
    }
    static object InvokeBaker(string name, params object[] arguments)
    {
        var parameterTypes = Array.ConvertAll(arguments, argument => argument.GetType());
        var method = s_BakerType.GetMethod(name, Flags, null, parameterTypes, null);
        if (method == null) throw new MissingMethodException(s_BakerType.FullName, name);
        return method.Invoke(s_Baker, arguments);
    }
    static T GetBakerProperty<T>(string name)
    {
        var property = s_BakerType.GetProperty(name, Flags);
        if (property == null) throw new MissingMemberException(s_BakerType.FullName, name);
        return (T)property.GetValue(s_Baker);
    }
    static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Flags);
        if (field == null) throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }
}
