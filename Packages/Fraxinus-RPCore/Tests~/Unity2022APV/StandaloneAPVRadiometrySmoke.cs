// Copy this and APVAnalyticGradientSky.shader / APVAnalyticLambert.shader
// into an Editor assembly in the disposable .tmp/apv-unity project.
// APV_RADIOMETRY_SMOKE=1 Unity.exe -batchmode -force-d3d11 -projectPath ...
//     -executeMethod APVStandaloneRadiometrySmoke.Run -logFile ...
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

public static class APVStandaloneRadiometrySmoke
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const string ReportPath = "apv-standalone-radiometry-result.json";
    static readonly List<string> Errors = new List<string>();
    static readonly List<string> Failures = new List<string>();
    static readonly List<string> Checks = new List<string>();
    static readonly List<Measurement> Measurements = new List<Measurement>();
    static readonly List<UnityEngine.Object> Objects = new List<UnityEngine.Object>();
    static readonly Stack<IEnumerator> Routines = new Stack<IEnumerator>();
    static readonly Vector3[] Directions =
    {
        Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
        new Vector3(1, 1, 1).normalized, new Vector3(-1, 1, 1).normalized,
        new Vector3(1, -1, 1).normalized, new Vector3(1, 1, -1).normalized,
        new Vector3(-1, -1, 1).normalized, new Vector3(-1, 1, -1).normalized,
        new Vector3(1, -1, -1).normalized, new Vector3(-1, -1, -1).normalized
    };
    static object s_Baker;
    static NativeArray<Vector3> s_Positions;
    static Type s_BakerType, s_SettingsType;
    static SphericalHarmonicsL2 s_Last;
    static SphericalHarmonicsL2[] s_LastSet;
    static float s_LastValidity;
    static double s_Started;
    static bool s_Finished;
    static Report s_Report;

    [Serializable]
    sealed class Measurement
    {
        public string name;
        public Vector3 l0;
        public float validity;
        public Vector3[] evaluated;
        public float[] coefficients;
    }

    [Serializable]
    sealed class Report
    {
        public bool passed, nativeBakeObserved, resourcesDisposed;
        public string unityVersion, device, graphicsApi, error;
        public double elapsedSeconds;
        public string[] checks, failures, errors;
        public Measurement[] measurements;
    }

    public static void Run()
    {
        if (!Application.isBatchMode || Environment.GetEnvironmentVariable("APV_RADIOMETRY_SMOKE") != "1")
            throw new InvalidOperationException("Run radiometry smoke only in a disposable batch project with APV_RADIOMETRY_SMOKE=1.");
        s_Report = new Report { unityVersion = Application.unityVersion,
            device = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString() };
        s_Started = EditorApplication.timeSinceStartup;
        Application.logMessageReceived += OnLog;
        try
        {
            Require(SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback &&
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A compute-capable graphics device with GPU readback is required.");
            Require(!Lightmapping.isRunning && !AdaptiveProbeVolumes.isRunning, "A bake is already running.");
            s_BakerType = FindType("UnityEngine.Rendering.StandaloneAPVLightingBaker");
            s_SettingsType = FindType("UnityEngine.Rendering.StandaloneAPVBakeSettings");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Mixed-light expectations below use Baked Indirect. The empty
            // scene's project default may be Subtractive, which legitimately
            // adds Mixed direct lighting and invalidates those fixtures.
            Lightmapping.lightingSettings = Track(new LightingSettings
            {
                name = "Radiometry Baked Indirect settings",
                mixedBakeMode = MixedLightingMode.IndirectOnly
            });
            GraphicsSettings.lightsUseLinearIntensity = true;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.skybox = null;
            Routines.Push(RunCases());
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    static IEnumerator RunCases()
    {
        // A constant radiance field must evaluate to that same color for every
        // normal after cosine convolution / PI, both with and without geometry.
        var ambient = new Color(0.5f, 0.25f, 0.75f, 1);
        RenderSettings.ambientLight = ambient;
        yield return Bake("empty constant environment", 1, 0.6f);
        var expectedAmbient = RGB(ambient.linear) * 0.6f;
        Near(L0(s_Last), expectedAmbient, 0.00001f, "Empty environment L0 normalization");
        foreach (var direction in Directions)
            Near(Evaluate(s_Last, direction), expectedAmbient, 0.00001f, "Empty environment directional evaluation");
        Checks.Add("Empty constant environment evaluates to linear ambient color times environment intensity");

        RenderSettings.ambientLight = Color.black;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        Require(shader != null, "URP Lit shader is missing.");
        var enclosureMaterial = Track(new Material(shader) { name = "Radiometry constant emitter" });
        var emission = new Color(0.125f, 0.3f, 0.8f, 1);
        enclosureMaterial.SetColor("_BaseColor", Color.black);
        // URP's Meta pass adds a rough-specular diffuse term. Smoothness one
        // removes it so this enclosure emits but cannot reflect itself.
        enclosureMaterial.SetFloat("_Smoothness", 1);
        enclosureMaterial.EnableKeyword("_EMISSION");
        enclosureMaterial.SetColor("_EmissionColor", emission);
        enclosureMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
        enclosureMaterial.doubleSidedGI = true;
        var enclosure = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
        enclosure.name = "Radiometry enclosing constant radiance";
        enclosure.transform.position = new Vector3(0, 1, 0);
        enclosure.transform.localScale = Vector3.one * 20;
        enclosure.GetComponent<MeshRenderer>().sharedMaterial = enclosureMaterial;
        GameObjectUtility.SetStaticEditorFlags(enclosure, StaticEditorFlags.ContributeGI);
        yield return Bake("GPU constant emissive enclosure, zero bounces", 0, 0);
        Near(s_LastValidity, 0, 0.00001f, "Double-sided interior must not count as invalid backfaces");
        // The Meta atlas stores half floats; 0.3 and 0.8 are rounded before
        // integration. Allow that storage error without hiding energy errors.
        Near(L0(s_Last), RGB(emission), 0.0005f, "GPU constant radiance L0 normalization");
        foreach (var direction in Directions)
            Near(Evaluate(s_Last, direction), RGB(emission), 0.04f,
                "GPU constant radiance should be isotropic at " + direction);
        var visibleEmission = s_Last;
        yield return Bake("GPU constant emissive enclosure, one bounce", 1, 0);
        NearCoefficients(s_Last, visibleEmission, 0.0001f,
            "Visible emission must be counted once and remain independent of reflected bounce budget");
        Checks.Add("Visible emission preserves linear energy at zero bounces and is not duplicated by indirect integration");
        enclosureMaterial.doubleSidedGI = false;
        yield return Bake("GPU single-sided interior backfaces", 1, 0);
        Near(s_LastValidity, 1, 0.00001f, "Single-sided interior must count as invalid backfaces");
        Checks.Add("Backface validity distinguishes one-sided and double-sided GI materials");
        // Force the GPU path with negligible solid angle so primary misses
        // see the same flat environment as the empty-scene reference. This
        // catches counting that sky both in the direct sky sampler and in the
        // new direct-emission visibility rays.
        enclosure.transform.position = Vector3.one * 10000;
        enclosure.transform.localScale = Vector3.one;
        enclosureMaterial.SetColor("_EmissionColor", Color.black);
        enclosureMaterial.DisableKeyword("_EMISSION");
        enclosureMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        RenderSettings.ambientLight = ambient;
        yield return Bake("GPU constant environment with distant geometry", 0, 0.6f, expectedGeometry: true);
        Near(L0(s_Last), expectedAmbient, 0.0005f, "GPU environment must be integrated exactly once");
        foreach (var direction in Directions)
            Near(Evaluate(s_Last, direction), expectedAmbient, 0.02f, "GPU constant environment remains isotropic");
        Checks.Add("Direct-emission rays do not double count the primary environment");
        RenderSettings.ambientLight = Color.black;
        enclosure.GetComponent<MeshRenderer>().enabled = false;
        enclosure.SetActive(false);
        yield return RunBackfaceTerminationCases(shader);
        yield return RunGeometricNormalSkyCases();

        var sun = Track(new GameObject("Radiometry directional light")).AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.lightmapBakeType = LightmapBakeType.Baked;
        sun.color = new Color(1, 0.75f, 0.5f, 1);
        sun.intensity = 2;
        // LightTransport folds punctual intensity into the linear RGB payload
        // and carries the PI conversion in w. The resulting direct-radiance
        // energy is still color * intensity * PI before Unity SH conversion.
        var energy = RGB(sun.color.linear) * (sun.intensity * Mathf.PI);
        // The L2 projection of the normalized clamped cosine is a zonal
        // polynomial. It is independent of the implementation's SH basis/order.
        // Oblique controls exercise cross terms missed by six cardinal axes.
        for (int axis = 0; axis < 8; axis++)
        {
            var toLight = Directions[axis];
            sun.transform.rotation = Quaternion.LookRotation(-toLight,
                Mathf.Abs(toLight.y) > 0.9f ? Vector3.forward : Vector3.up);
            yield return Bake("Baked directional projection " + toLight, 1, 0);
            Near(L0(s_Last), energy / (4 * Mathf.PI), 0.0001f, "Baked directional mean energy at " + toLight);
            foreach (var normal in Directions)
            {
                float cosine = Vector3.Dot(normal, toLight);
                // Native APV windows direct radiance before cosine convolution.
                // The standalone path applies the same band factors in the
                // transport shader, so include them in the analytic reference.
                float kernel = (0.25f + 0.5f * 0.922066f * cosine +
                    (5.0f / 32.0f) * 0.731864f * (3 * cosine * cosine - 1)) / Mathf.PI;
                Near(Evaluate(s_Last, normal), energy * kernel, 0.0002f,
                    "Baked directional L2 projection, light=" + toLight + ", normal=" + normal);
            }
        }
        Checks.Add("Baked delta lights project with correct absolute energy and axis/rotation symmetry");

        // Shadow softness redistributes angular samples but must not change
        // total incident energy. The empty-scene shortcut projects the exact
        // light direction, so its L0 must remain the hard-light mean.
        sun.shadows = LightShadows.Soft;
        sun.shadowAngle = 60.0f;
        var softAxis = new Vector3(1, 1, 1).normalized;
        sun.transform.rotation = Quaternion.LookRotation(-softAxis,
            Mathf.Abs(softAxis.y) > 0.9f ? Vector3.forward : Vector3.up);
        yield return Bake("Soft directional CPU mean energy", 1, 0, expectedGeometry: false);
        Near(L0(s_Last), energy / (4 * Mathf.PI), 0.0001f,
            "Shadow softness must not rescale directional mean energy");
        foreach (var normal in Directions)
        {
            float cosine = Vector3.Dot(normal, softAxis);
            float kernel = (0.25f + 0.5f * 0.922066f * cosine +
                (5.0f / 32.0f) * 0.731864f * (3 * cosine * cosine - 1)) / Mathf.PI;
            Near(Evaluate(s_Last, normal), energy * kernel, 0.00025f,
                "Soft directional CPU direct-window projection at normal " + normal);
        }
        Checks.Add("Soft directional CPU shortcut preserves mean energy and the direct SH window");

        // A zero-width soft light is the hard-light limit. None shadows must
        // also leave the angular setting inactive, as Unity does for lights
        // that do not cast shadows.
        sun.shadowAngle = 0.0f;
        yield return Bake("Zero-width soft directional limit", 1, 0);
        var zeroWidth = s_Last;
        sun.shadows = LightShadows.None;
        sun.shadowAngle = 60.0f;
        yield return Bake("Non-shadow-casting directional ignores shadow angle", 1, 0);
        NearCoefficients(s_Last, zeroWidth, 0.0001f,
            "A directional light without shadows must use the hard-light transport path");
        Checks.Add("Zero-width soft lights and non-shadow-casting lights reproduce hard directional SH");
        sun.shadows = LightShadows.None;
        sun.shadowAngle = 0.0f;

        sun.enabled = false;
        yield return RunPunctualAndLightExtractionCases(shader);
        yield return RunSoftDirectionalShadowCases(shader);
        sun.enabled = true;

        sun.color = Color.white;
        sun.intensity = 1;
        sun.transform.rotation = Quaternion.Euler(90, 0, 0);
        yield return Bake("Baked empty directional direct reference", 1, 0);
        var direct = s_Last;
        sun.lightmapBakeType = LightmapBakeType.Mixed;
        yield return Bake("Mixed empty excludes direct", 1, 0);
        NearCoefficients(s_Last, default, 0.00001f, "Mixed lights must not bake direct illumination at probes");

        var floorMaterial = Track(new Material(shader) { name = "Radiometry white diffuse receiver" });
        floorMaterial.SetColor("_BaseColor", Color.white);
        floorMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        AddFloor(floorMaterial);
        yield return Bake("Mixed zero reflected bounces", 0, 0);
        NearCoefficients(s_Last, default, 0.00001f, "Zero bounce budget must exclude Mixed reflected lighting");
        yield return Bake("Mixed first reflected bounce", 1, 0);
        var mixed = s_Last;
        Check(L0(mixed).x > 0.01f && L0(mixed).y > 0.01f && L0(mixed).z > 0.01f,
            "Mixed lighting lost the first reflected bounce: L0=" + L0(mixed));
        Near(L0(mixed), Vector3.one * 0.5f, 0.04f,
            "Large white floor under unit URP sun must return approximately half-sphere unit radiance");
        sun.lightmapBakeType = LightmapBakeType.Baked;
        yield return Bake("Baked direct plus reflected bounce", 1, 0);
        NearCoefficients(s_Last + direct * -1, mixed, 0.0001f,
            "Baked minus unoccluded direct must match Mixed indirect on the same diffuse floor");
        Checks.Add("Mixed contributes the first reflected bounce and matches Baked with probe direct removed");

        sun.lightmapBakeType = LightmapBakeType.Mixed;
        sun.intensity = 2;
        yield return Bake("Mixed twice intensity", 1, 0);
        NearCoefficients(s_Last, mixed * 2, 0.0001f, "Mixed indirect must scale linearly with light intensity");
        sun.intensity = 1;
        foreach (float multiplier in new[] { 0.0f, 0.5f, 3.0f })
        {
            sun.bounceIntensity = multiplier;
            sun.lightmapBakeType = LightmapBakeType.Mixed;
            yield return Bake("Mixed bounce intensity " + multiplier, 1, 0);
            NearCoefficients(s_Last, mixed * multiplier, 0.0001f,
                "Mixed bounce intensity must multiply reflected energy: " + multiplier);
            sun.lightmapBakeType = LightmapBakeType.Baked;
            yield return Bake("Baked bounce intensity " + multiplier, 1, 0);
            NearCoefficients(s_Last, direct + mixed * multiplier, 0.0001f,
                "Baked bounce intensity must multiply only reflected energy: " + multiplier);
        }
        Checks.Add("Indirect multiplier 0/0.5/3 changes reflected energy while preserving Baked direct illumination");
        sun.bounceIntensity = 1;
        sun.lightmapBakeType = LightmapBakeType.Mixed;
        var positions = new[] { new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 1), new Vector3(0, 2, -1) };
        yield return Bake("Probe batches of one", 1, 0, positions, 1);
        var singleBatchResults = s_LastSet;
        yield return Bake("Probe batch of four", 1, 0, positions, 4);
        for (int probe = 0; probe < positions.Length; probe++)
            NearCoefficients(s_LastSet[probe], singleBatchResults[probe], 0.0001f,
                "Changing batch size must not change a probe's integration sequence: probe " + probe);
        Checks.Add("Identical probe positions produce identical coefficients with batch sizes one and four");
        sun.lightmapBakeType = LightmapBakeType.Realtime;
        yield return Bake("Realtime contributes no baked energy", 1, 0);
        NearCoefficients(s_Last, default, 0.00001f, "Realtime lights must not contribute to APV bake");
        Checks.Add("Mixed indirect scales linearly; Realtime lights are excluded");
    }

    static IEnumerator RunSoftDirectionalShadowCases(Shader shader)
    {
        var blockerMaterial = Track(new Material(shader) { name = "Radiometry soft-sun blocker" });
        blockerMaterial.SetColor("_BaseColor", Color.black);
        blockerMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        var blocker = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
        blocker.name = "Radiometry soft-sun blocker geometry";
        blocker.transform.position = new Vector3(0, 2, 0);
        blocker.transform.localScale = new Vector3(20, 0.1f, 20);
        blocker.GetComponent<MeshRenderer>().sharedMaterial = blockerMaterial;
        GameObjectUtility.SetStaticEditorFlags(blocker, StaticEditorFlags.ContributeGI);

        var sun = Track(new GameObject("Radiometry soft directional light")).AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.lightmapBakeType = LightmapBakeType.Baked;
        sun.color = Color.white;
        sun.intensity = 1;
        // The light points down, so its incoming direction at the probe is up.
        sun.transform.rotation = Quaternion.Euler(90, 0, 0);
        sun.shadows = LightShadows.Soft;
        sun.shadowAngle = 60;

        yield return Bake("Soft directional shadow blocked", 0, 0);
        Near(L0(s_Last), Vector3.zero, 0.0001f,
            "Soft directional shadows must reject all samples behind an occluder");

        // Shadow-disabled transport must agree with the empty-scene CPU path,
        // even when identical geometry remains in the acceleration structure.
        sun.shadows = LightShadows.None;
        blocker.SetActive(false);
        yield return Bake("Directional without shadows CPU reference", 0, 0,
            expectedGeometry: false);
        var cpuNoShadow = s_Last;
        blocker.SetActive(true);
        yield return Bake("Directional without shadows ignores blocker", 0, 0);
        NearCoefficients(s_Last, cpuNoShadow, 0.0001f,
            "Shadow-disabled directional lights must skip visibility rays");
        Checks.Add("Soft directional shadow rays block on geometry while LightShadows.None matches the CPU empty path");

        // The direct soft-shadow integration is seeded by probe index, so changing
        // probe batching must preserve every probe's angular SH result.
        // Keep a harmless far-away mesh in the acceleration structure so the
        // following checks exercise the GPU sampler rather than the
        // empty-scene shortcut.
        blocker.transform.position = new Vector3(20, 2, 20);
        blocker.SetActive(true);
        sun.shadows = LightShadows.Soft;
        Near(L0(cpuNoShadow), Vector3.one * 0.25f, 0.0001f,
            "CPU directional mean energy must equal intensity divided by four");
        var positions = new[] { new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 1) };
        yield return Bake("Soft directional probes batch one", 0, 0, positions, 1);
        var singleBatch = s_LastSet;
        foreach (var probe in singleBatch)
            Near(L0(probe), L0(cpuNoShadow), 0.0001f,
                "Unoccluded soft directional GPU mean energy must match the CPU reference");
        yield return Bake("Soft directional probes batch three", 0, 0, positions, 3);
        for (int probe = 0; probe < positions.Length; ++probe)
            NearCoefficients(s_LastSet[probe], singleBatch[probe], 0.0001f,
                "Soft directional probe result changed with batch size: probe " + probe);
        Checks.Add("Soft directional GPU mean energy matches CPU and is deterministic across batch sizes");

        // Mixed lights deposit no direct probe term. Their first reflected
        // contribution still uses the soft light samples and remains finite.
        sun.lightmapBakeType = LightmapBakeType.Mixed;
        var floorMaterial = Track(new Material(shader) { name = "Radiometry soft-sun receiver" });
        floorMaterial.SetColor("_BaseColor", Color.white);
        floorMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        var floor = AddFloor(floorMaterial);
        yield return Bake("Soft mixed zero bounce", 0, 0);
        NearCoefficients(s_Last, default, 0.00001f,
            "Mixed soft directional light must not contribute direct probe energy");
        yield return Bake("Soft mixed first bounce", 1, 0);
        Check(L0(s_Last).x > 0.01f && L0(s_Last).y > 0.01f && L0(s_Last).z > 0.01f,
            "Soft mixed directional light lost the reflected bounce: L0=" + L0(s_Last));
        // The shadow disk affects visibility. The diffuse cosine retains the
        // original sun direction, so a normal-facing near-infinite floor
        // returns half-sphere unit radiance for both hard and soft shadows.
        Near(L0(s_Last), Vector3.one * 0.5f, 0.04f,
            "Soft mixed directional diffuse energy must preserve the unjittered cosine");
        Checks.Add("Soft Mixed lights retain indirect-only semantics and first-bounce energy");

        blocker.SetActive(false);
        // Keep the entire shadow disk above the receiver's horizon. With
        // sin(elevation)=0.4 and radius=20.1 degrees in radians, its smallest
        // unnormalized Y is 0.4-sqrt(1-0.4^2)*radius > 0. Thus there is no
        // self-occlusion and hard/soft estimates share identical primary rays.
        // Their coefficients must agree without Monte Carlo tolerance.
        var grazingDirection = new Vector3(Mathf.Sqrt(1 - 0.4f * 0.4f), 0.4f, 0);
        sun.transform.rotation = Quaternion.LookRotation(-grazingDirection, Vector3.up);
        sun.shadowAngle = 20.1f;
        sun.shadows = LightShadows.Hard;
        yield return Bake("Grazing mixed hard sun without blockers", 1, 0);
        var grazingHard = s_Last;
        Near(L0(grazingHard), Vector3.one * 0.2f, 0.02f,
            "Unoccluded grazing floor energy must follow the original directional cosine");
        sun.shadows = LightShadows.Soft;
        yield return Bake("Grazing mixed soft sun without blockers", 1, 0);
        NearCoefficients(s_Last, grazingHard, 0.00002f,
            "Soft shadow jitter must not change the cosine of an unoccluded directional light");

        // A sun just below the plane's horizon has no diffuse contribution.
        // Some jittered shadow directions point upward, but they must not
        // turn a back-facing receiver into a sunlit surface.
        var belowHorizon = new Vector3(1, -0.01f, 0).normalized;
        sun.transform.rotation = Quaternion.LookRotation(-belowHorizon, Vector3.up);
        yield return Bake("Soft mixed sun below receiver horizon", 1, 0);
        NearCoefficients(s_Last, default, 0.00001f,
            "A soft directional light must not inject energy across the receiver horizon");
        Checks.Add("Unoccluded soft directional reflection uses the original cosine, including grazing and back-facing receivers");
        floor.SetActive(false);
        blocker.SetActive(false);
        sun.gameObject.SetActive(false);
    }

    static IEnumerator RunGeometricNormalSkyCases()
    {
        var skyShader = Shader.Find("Hidden/APV/AnalyticGradientSky");
        var diffuseShader = Shader.Find("Hidden/APV/AnalyticLambert");
        Require(skyShader != null && diffuseShader != null,
            "Copy APVAnalyticGradientSky.shader and APVAnalyticLambert.shader into the disposable project.");
        var previousSkybox = RenderSettings.skybox;
        float previousAmbientIntensity = RenderSettings.ambientIntensity;
        var sky = Track(new Material(skyShader) { name = "Analytic linear sky A + B dot up" });
        sky.SetVector("_RadianceA", new Vector4(0.4f, 0.4f, 0.4f, 0));
        sky.SetVector("_RadianceB", new Vector4(0.3f, 0, -0.3f, 0));
        RenderSettings.skybox = sky;
        RenderSettings.ambientIntensity = 1;

        // The fixture Meta pass supplies known linear reflectance without
        // URP's rough-specular term or a gamma-encoded color property.
        const float reflectance = 0.5f;
        var material = Track(new Material(diffuseShader) { name = "Analytic rho = 0.5 diffuse floor" });
        material.SetFloat("_Reflectance", reflectance);
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        var floor = AddFloor(material);
        var mesh = floor.GetComponent<MeshFilter>().sharedMesh;
        // A native reference can use the same fixture: its material atlas
        // needs a nondegenerate lightmap chart even for constant reflectance.
        mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
        mesh.uv2 = mesh.uv;

        // The geometric Lambert integral of L(w)=A+B*w.y is rho*(A+2B/3).
        // This square's reflected radiance is constant. Its contribution to
        // probe L0 is that radiance times the exact solid angle divided by 4pi.
        const double halfExtent = 100, height = 1;
        float floorFraction = (float)(Math.Atan(halfExtent * halfExtent /
            (height * Math.Sqrt(height * height + 2 * halfExtent * halfExtent))) / Math.PI);
        Vector3 expected = new Vector3(0.6f, 0.4f, 0.2f) * (reflectance * floorFraction);
        yield return Bake("Analytic gradient, aligned normals, direct only", 0, 1, expectedGeometry: true);
        var alignedDirect = s_Last;
        yield return Bake("Analytic gradient, aligned normals, one reflected bounce", 1, 1, expectedGeometry: true);
        var alignedIndirect = Difference(s_Last, alignedDirect);
        Near(L0(alignedIndirect), expected, 0.004f,
            "Aligned plane reflected energy must match the analytic sky integral and finite solid angle");

        // Change only authored vertex normals, leaving the physical plane,
        // material, sky, probe and random seed fixed. Primary sky cancels
        // when the zero-bounce reference is subtracted from the one-bounce bake.
        var tilted = new Vector3(Mathf.Sqrt(0.75f), 0.5f, 0);
        mesh.normals = new[] { tilted, tilted, tilted, tilted };
        yield return Bake("Analytic gradient, vertex normals tilted 60 degrees, direct only", 0, 1, expectedGeometry: true);
        var tiltedDirect = s_Last;
        yield return Bake("Analytic gradient, vertex normals tilted 60 degrees, one reflected bounce", 1, 1, expectedGeometry: true);
        var tiltedIndirect = Difference(s_Last, tiltedDirect);
        NearCoefficients(tiltedDirect, alignedDirect, 0.0001f,
            "Changing vertex normals must not change primary sky visibility");
        Near(L0(tiltedIndirect), expected, 0.004f,
            "Tilted authored normals must preserve the geometric Lambert sky integral");
        NearCoefficients(tiltedIndirect, alignedIndirect, 0.0001f,
            "Geometric-plane diffuse continuation must not depend on authored vertex normals");
        Checks.Add("Analytic gradient sky gives rho*(A+2B/3) reflected radiance and the square's exact solid-angle L0");
        Checks.Add("Changing only interpolated normals preserves primary visibility and geometric-plane diffuse transport");
        floor.SetActive(false);
        RenderSettings.skybox = previousSkybox;
        RenderSettings.ambientIntensity = previousAmbientIntensity;
    }

    static SphericalHarmonicsL2 Difference(SphericalHarmonicsL2 value, SphericalHarmonicsL2 baseline)
    {
        for (int channel = 0; channel < 3; channel++)
            for (int coefficient = 0; coefficient < 9; coefficient++)
                value[channel, coefficient] -= baseline[channel, coefficient];
        return value;
    }

    static IEnumerator RunBackfaceTerminationCases(Shader shader)
    {
        var receiverMaterial = Track(new Material(shader) { name = "Radiometry backface-path receiver" });
        receiverMaterial.SetColor("_BaseColor", Color.white);
        receiverMaterial.SetFloat("_Smoothness", 1);
        receiverMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        receiverMaterial.doubleSidedGI = false;
        var floor = AddFloor(receiverMaterial);

        // A real miss must still collect the environment. This positive
        // control prevents a blanket removal of indirect sky from passing
        // the sealed fixture below.
        RenderSettings.ambientLight = new Color(0.3f, 0.6f, 1, 1);
        yield return Bake("Open floor under environment, zero bounces", 0, 1, expectedGeometry: true);
        var openDirect = s_Last;
        yield return Bake("Open floor under environment, one bounce", 1, 1, expectedGeometry: true);
        Check(L0(s_Last).z - L0(openDirect).z > 0.2f,
            "A real reflected miss must retain sky radiance: reflected L0=" + (L0(s_Last) - L0(openDirect)));

        var roofMaterial = Track(new Material(shader) { name = "Radiometry single-sided backface roof" });
        roofMaterial.SetColor("_BaseColor", Color.black);
        roofMaterial.SetFloat("_Smoothness", 1);
        roofMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        roofMaterial.doubleSidedGI = false;
        var roof = AddFloor(roofMaterial);
        roof.name = "Radiometry upward-facing roof";
        roof.transform.position = Vector3.up * 2;

        var wallMaterial = Track(new Material(roofMaterial) { name = "Radiometry black closing walls" });
        wallMaterial.doubleSidedGI = true;
        var walls = new GameObject[4];
        var wallPositions = new[] { new Vector3(100, 1, 0), new Vector3(-100, 1, 0),
            new Vector3(0, 1, 100), new Vector3(0, 1, -100) };
        for (int i = 0; i < walls.Length; ++i)
        {
            walls[i] = AddFloor(wallMaterial);
            walls[i].name = "Radiometry closing wall " + i;
            walls[i].transform.position = wallPositions[i];
            walls[i].transform.rotation = i < 2 ? Quaternion.Euler(0, 0, 90) : Quaternion.Euler(90, 0, 0);
        }

        // Every sky visibility ray is blocked. Downward primary rays hit the
        // white floor's front face; their diffuse continuations hit either
        // the forbidden roof backface or a black wall. Neither is a real
        // environment miss. Upward primary backfaces legitimately contribute
        // to validity, but must not contribute any radiance at any bounce.
        for (int bounces = 0; bounces <= 2; ++bounces)
        {
            yield return Bake("Sealed floor and backface roof, bounces " + bounces,
                bounces, 1, expectedGeometry: true);
            NearCoefficients(s_Last, default, 0.00001f,
                "Forbidden backfaces must terminate without injecting sky, bounces=" + bounces);
            Check(s_LastValidity > 0.4f && s_LastValidity < 0.6f,
                "Sealed fixture must retain primary roof-backface validity: " + s_LastValidity);
        }
        Checks.Add("True reflected misses collect sky while subsequent single-sided backfaces terminate black inside closed geometry");
        floor.SetActive(false);
        roof.SetActive(false);
        foreach (var wall in walls) wall.SetActive(false);
        RenderSettings.ambientLight = Color.black;
    }

    static IEnumerator RunPunctualAndLightExtractionCases(Shader shader)
    {
        // This black cube forces acceleration-structure/GPU execution without
        // blocking the vertical probe-to-light segment or emitting any light.
        // A zero bounce budget excludes all reflected contributions.
        var black = Track(new Material(shader) { name = "Radiometry GPU route black material" });
        black.SetColor("_BaseColor", Color.black);
        black.SetColor("_EmissionColor", Color.black);
        black.DisableKeyword("_EMISSION");
        black.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        var marker = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
        marker.name = "Radiometry unobstructing GPU geometry";
        marker.transform.position = new Vector3(20, 1, 20);
        var geometry = marker.GetComponent<MeshRenderer>();
        geometry.sharedMaterial = black;
        geometry.enabled = false;
        GameObjectUtility.SetStaticEditorFlags(marker, StaticEditorFlags.ContributeGI);

        var light = Track(new GameObject("Radiometry punctual light")).AddComponent<Light>();
        light.type = LightType.Point;
        light.lightmapBakeType = LightmapBakeType.Baked;
        light.color = Color.white;
        light.intensity = 1;
        light.range = 8;
        light.useColorTemperature = false;
        // URP attenuation = (1 - (distance/range)^4)^2 / distance^2.
        // These fixed fractions check the near field (where the old code
        // incorrectly clamped distance^2 to one), rolloff, and the range edge.
        var distances = new[] { 0.5f, 2.0f, 4.0f, 8.0f, 10.0f };
        var attenuations = new[] { 3.99987793f, 65025.0f / 262144.0f, 225.0f / 4096.0f, 0.0f, 0.0f };
        for (int i = 0; i < distances.Length; i++)
        {
            light.transform.position = new Vector3(0, 1 + distances[i], 0);
            yield return BakeCPUAndGPU("Point distance " + distances[i], geometry,
                Vector3.one * (0.25f * attenuations[i]));
        }
        Checks.Add("Point distance 0.5/2/4/8/10 with range 8 matches analytic URP attenuation on CPU and GPU");

        light.type = LightType.Spot;
        light.transform.position = new Vector3(0, 3, 0);
        light.spotAngle = 90;
        light.innerSpotAngle = 30;
        // The cosine midpoint has angular interpolation 1/2, so squaring
        // gives exactly 1/4. An angular smoothstep would incorrectly give 1/2.
        float midpoint = Mathf.Acos((Mathf.Cos(15 * Mathf.Deg2Rad) + Mathf.Cos(45 * Mathf.Deg2Rad)) * 0.5f) * Mathf.Rad2Deg;
        var angles = new[] { 0.0f, midpoint, 45.0f, 60.0f };
        var angularAttenuations = new[] { 1.0f, 0.25f, 0.0f, 0.0f };
        for (int i = 0; i < angles.Length; i++)
        {
            var forward = Quaternion.AngleAxis(angles[i], Vector3.forward) * Vector3.down;
            light.transform.rotation = Quaternion.LookRotation(forward, Vector3.forward);
            yield return BakeCPUAndGPU("Spot off-axis degrees " + angles[i], geometry,
                Vector3.one * (0.25f * attenuations[1] * angularAttenuations[i]));
        }
        Checks.Add("Spot core/cosine midpoint/outer edge/outside follow squared URP cone attenuation on CPU and GPU");

        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(90, 0, 0);
        light.gameObject.SetActive(false);
        yield return BakeCPUAndGPU("Inactive light GameObject", geometry, Vector3.zero);
        light.gameObject.SetActive(true);
        var parent = Track(new GameObject("Radiometry inactive light ancestor"));
        light.transform.SetParent(parent.transform, true);
        parent.SetActive(false);
        Require(light.gameObject.activeSelf && !light.gameObject.activeInHierarchy,
            "Inactive ancestor control must leave the light's own activeSelf true.");
        yield return BakeCPUAndGPU("Inactive light ancestor", geometry, Vector3.zero);
        parent.SetActive(true);
        light.enabled = false;
        yield return BakeCPUAndGPU("Disabled light component", geometry, Vector3.zero);
        light.enabled = true;
        yield return BakeCPUAndGPU("Re-enabled light component", geometry, Vector3.one * 0.25f);
        Checks.Add("Inactive objects, inactive ancestors and disabled light components contribute zero; re-enabling restores illumination");

        light.color = new Color(0.5f, 0.25f, 0.75f, 1);
        light.intensity = 0.5f;
        // Known sRGB transfer values at these inputs avoid reusing the baker's
        // color conversion implementation to generate the test expectation.
        var linearRadiance = new Vector3(0.21404114f, 0.05087609f, 0.52252155f) * 0.5f;
        yield return BakeCPUAndGPU("Linear light intensity uses linear color times intensity", geometry,
            linearRadiance * 0.25f, true);
        // Gamma-space multiplication gives (0.25, 0.125, 0.375), then transfer.
        var gammaRadiance = new Vector3(0.05087609f, 0.01434987f, 0.11601613f);
        yield return BakeCPUAndGPU("Nonlinear light intensity converts color times intensity", geometry,
            gammaRadiance * 0.25f, false);
        Checks.Add("lightsUseLinearIntensity true/false matches analytic sRGB conversion order on CPU and GPU");

        light.useColorTemperature = true;
        foreach (float kelvin in new[] { 3000.0f, 10000.0f })
        {
            light.colorTemperature = kelvin;
            var temperature = RGB(Mathf.CorrelatedColorTemperatureToRGB(kelvin));
            Check((temperature - Vector3.one).sqrMagnitude > 0.05f,
                "Temperature fixture must produce a measurable non-white tint: " + kelvin);
            yield return BakeCPUAndGPU("Enabled color temperature " + kelvin, geometry,
                Vector3.Scale(linearRadiance, temperature) * 0.25f);
        }
        light.useColorTemperature = false;
        yield return BakeCPUAndGPU("Disabled retained color temperature", geometry, linearRadiance * 0.25f);
        Checks.Add("Enabled 3000K/10000K multiplies linear light color; disabling temperature ignores its retained value");
        light.gameObject.SetActive(false);
        marker.SetActive(false);
        GraphicsSettings.lightsUseLinearIntensity = true;
    }

    static IEnumerator BakeCPUAndGPU(string name, MeshRenderer geometry, Vector3 expectedMean, bool linearIntensity = true)
    {
        geometry.enabled = false;
        GraphicsSettings.lightsUseLinearIntensity = linearIntensity;
        yield return Bake(name + " CPU empty scene", 0, 0, expectedGeometry: false);
        var cpu = s_Last;
        Near(L0(cpu), expectedMean, 0.00002f, name + " analytic CPU mean energy");
        geometry.enabled = true;
        // URP may reset this global during rendering between editor updates.
        // Reapply immediately before Initialize extracts the light data.
        GraphicsSettings.lightsUseLinearIntensity = linearIntensity;
        yield return Bake(name + " GPU with black geometry", 0, 0, expectedGeometry: true);
        Near(L0(s_Last), expectedMean, 0.00002f, name + " analytic GPU mean energy");
        NearCoefficients(s_Last, cpu, 0.00002f, name + " CPU/GPU coefficients");
        geometry.enabled = false;
    }

    static IEnumerator Bake(string name, int bounces, float environmentIntensity, Vector3[] positions = null, int batchSize = 1,
        bool? expectedGeometry = null)
    {
        var settings = Activator.CreateInstance(s_SettingsType);
        Set(settings, "samples", 4096);
        Set(settings, "directSamples", 4096);
        Set(settings, "bounces", bounces);
        Set(settings, "probesPerBatch", batchSize);
        Set(settings, "environmentIntensity", environmentIntensity);
        s_Baker = Activator.CreateInstance(s_BakerType, Flags, null, new[] { settings }, null);
        s_Positions = new NativeArray<Vector3>(positions ?? new[] { new Vector3(0, 1, 0) }, Allocator.Persistent);
        InvokeBaker("Initialize", false, s_Positions);
        if (expectedGeometry.HasValue)
        {
            var geometryField = s_BakerType.GetField("m_HasGeometry", Flags);
            Require(geometryField != null && (bool)geometryField.GetValue(s_Baker) == expectedGeometry.Value,
                "Fixture did not exercise the expected " + (expectedGeometry.Value ? "GPU geometry" : "CPU empty scene") + " path: " + name);
        }
        Require(GetBakerProperty<ulong>("stepCount") == (ulong)s_Positions.Length, "Radiometry bake probe count mismatch.");
        while (GetBakerProperty<ulong>("currentStep") < GetBakerProperty<ulong>("stepCount"))
        {
            Require((bool)InvokeBaker("Step"), "Lighting baker failed during case: " + name);
            yield return null;
        }
        var irradiance = GetBakerProperty<NativeArray<SphericalHarmonicsL2>>("irradiance");
        s_Last = irradiance[0];
        s_LastSet = irradiance.ToArray();
        s_LastValidity = GetBakerProperty<NativeArray<float>>("validity")[0];
        var coefficients = new float[27];
        var evaluated = new Vector3[Directions.Length];
        for (int channel = 0; channel < 3; channel++)
            for (int coefficient = 0; coefficient < 9; coefficient++)
            {
                float value = s_Last[channel, coefficient];
                Require(!float.IsNaN(value) && !float.IsInfinity(value), "GPU returned non-finite SH for " + name);
                coefficients[channel * 9 + coefficient] = value;
            }
        for (int i = 0; i < Directions.Length; i++) evaluated[i] = Evaluate(s_Last, Directions[i]);
        Measurements.Add(new Measurement { name = name, l0 = L0(s_Last), validity = s_LastValidity,
            evaluated = evaluated, coefficients = coefficients });
        DisposeBaker();
        Require(Errors.Count == 0, "Unity logged errors during " + name + ": " + string.Join("\n", Errors));
    }

    static GameObject AddFloor(Material material)
    {
        var go = Track(new GameObject("Radiometry diffuse floor"));
        var mesh = Track(new Mesh { name = "Radiometry floor mesh" });
        mesh.vertices = new[] { new Vector3(-100, 0, -100), new Vector3(-100, 0, 100),
            new Vector3(100, 0, 100), new Vector3(100, 0, -100) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        mesh.uv = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
        return go;
    }

    // Uncompressed Unity-format SH includes cosine convolution / PI and basis
    // constants already. L1 serialization swizzles Y/Z/X to X/Y/Z. L2 runtime
    // decoding folds its z term into L0; do not apply orthonormal bases again.
    // Evaluate before APV's compression/quantization to isolate transport units.
    static Vector3 Evaluate(SphericalHarmonicsL2 sh, Vector3 normal)
    {
        var result = Vector3.zero;
        for (int channel = 0; channel < 3; channel++)
            result[channel] = sh[channel, 0] + sh[channel, 1] * normal.y + sh[channel, 2] * normal.z + sh[channel, 3] * normal.x
                + sh[channel, 4] * normal.x * normal.y + sh[channel, 5] * normal.y * normal.z
                + sh[channel, 6] * (3 * normal.z * normal.z - 1) + sh[channel, 7] * normal.x * normal.z
                + sh[channel, 8] * (normal.x * normal.x - normal.y * normal.y);
        return result;
    }

    static Vector3 L0(SphericalHarmonicsL2 sh) => new Vector3(sh[0, 0], sh[1, 0], sh[2, 0]);
    static Vector3 RGB(Color color) => new Vector3(color.r, color.g, color.b);
    static void NearCoefficients(SphericalHarmonicsL2 actual, SphericalHarmonicsL2 expected, float tolerance, string message)
    {
        for (int channel = 0; channel < 3; channel++)
            for (int coefficient = 0; coefficient < 9; coefficient++)
                Near(actual[channel, coefficient], expected[channel, coefficient], tolerance,
                    message + " [" + channel + "," + coefficient + "]");
    }
    static void Near(Vector3 actual, Vector3 expected, float tolerance, string message)
    { for (int channel = 0; channel < 3; channel++) Near(actual[channel], expected[channel], tolerance, message + " channel " + channel); }
    static void Near(float actual, float expected, float tolerance, string message)
        => Check(Mathf.Abs(actual - expected) <= tolerance * Mathf.Max(1, Mathf.Abs(expected)),
            message + ": actual=" + actual + ", expected=" + expected);
    static void Check(bool condition, string message) { if (!condition) Failures.Add(message); }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    static void Tick()
    {
        try
        {
            Require(EditorApplication.timeSinceStartup - s_Started < 300, "Radiometry GPU regression timed out.");
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
        }
        catch (Exception cleanup) { error = error == null ? cleanup : new AggregateException(error, cleanup); }
        s_Report.elapsedSeconds = EditorApplication.timeSinceStartup - s_Started;
        s_Report.passed = error == null && Failures.Count == 0 && Errors.Count == 0 && !s_Report.nativeBakeObserved && s_Report.resourcesDisposed;
        s_Report.error = error?.ToString();
        s_Report.errors = Errors.ToArray();
        s_Report.failures = Failures.ToArray();
        s_Report.checks = Checks.ToArray();
        s_Report.measurements = Measurements.ToArray();
        Application.logMessageReceived -= OnLog;
        File.WriteAllText(ReportPath, JsonUtility.ToJson(s_Report, true));
        Debug.Log("APV_RADIOMETRY_SMOKE " + (s_Report.passed ? "PASS" : "FAIL") + " report=" + Path.GetFullPath(ReportPath));
        if (error != null) Debug.LogException(error);
        if (Application.isBatchMode) EditorApplication.Exit(s_Report.passed ? 0 : 1);
    }
    static T Track<T>(T value) where T : UnityEngine.Object { Objects.Add(value); return value; }
    static void OnLog(string message, string stack, LogType type)
    { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Errors.Add(message); }
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
