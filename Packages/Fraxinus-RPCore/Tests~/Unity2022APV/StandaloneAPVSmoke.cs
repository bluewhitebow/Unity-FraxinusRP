// Copy into an Editor assembly, then run with -batchmode -executeMethod APVStandaloneSmoke.Run.
// Requires a real graphics device; do not pass -nographics or -quit.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.IO.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class APVStandaloneSmoke
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const string ReportPath = "apv-standalone-result.json";
    static readonly List<string> Errors = new List<string>();
    static readonly List<string> Checks = new List<string>();
    static Report s_Report;
    static ProbeVolumeBakingSet s_Set;
    static Camera s_Camera;
    static Light s_Sun;
    static LightBakingOutput s_InitialSunOutput;
    static RenderTexture s_Target;
    static object s_Owner, s_BakeData, s_Volume;
    static Type s_BakingProvider, s_RuntimeProvider;
    static RenderPipelineAsset s_PreviousGraphics, s_PreviousQuality;
    static LightingSettings s_PreviousLighting;
    static double s_Started;
    static int s_Frames;
    static IEnumerator s_ClearRegression;
    static StandaloneAPVBakeConfiguration s_Config;
    static LightmapData[] s_PreviousLightmaps;
    static Scene s_ForeignScene;
    static bool s_Finished, s_NativeBakeObserved, s_StandaloneStarted;
    static string s_Folder;
    static bool s_RebakeStreaming;
    static bool s_PublicationFailure;
    static string s_ExpectedPublicationPath;
    static FileStream s_PublicationLock;
    static readonly List<string> ExpectedPublicationErrors = new List<string>();
    static int s_PayloadImports;
    static double s_LastPayloadImport;
    static readonly Dictionary<string, int> PayloadImportCounts = new Dictionary<string, int>();
    const string PrepareFailureMessage = "APV smoke intentional preparation failure after reentry checks";
    static bool s_ExpectPrepareFailure, s_PrepareFailureObserved;

    sealed class PrepareReentryBaker : AdaptiveProbeVolumes.VirtualOffsetBaker
    {
        internal object owner;
        internal bool checkedReentry, disposed;
        public override ulong currentStep => 0;
        public override ulong stepCount => 0;
        public override NativeArray<Vector3> offsets => default;
        public override void Initialize(ProbeVolumeBakingSet set, NativeArray<Vector3> positions)
        {
            owner = Read(s_BakingProvider, "activeOrNull");
            Require(ReferenceEquals(Invoke(s_BakingProvider, "Get", null, false), owner), "Get changed the preparing owner.");
            Require(!AdaptiveProbeVolumes.BakeStandaloneAsync(), "A preparing bake accepted a reentrant start.");
            Require(ReferenceEquals(Read(s_BakingProvider, "s_Running"), owner), "Reentry released the preparing owner.");
            checkedReentry = true;
            throw new InvalidOperationException(PrepareFailureMessage);
        }
        public override bool Step() => throw new InvalidOperationException("A failed preparation reached Step.");
        public override void Dispose() => disposed = true;
    }

    static void CheckPreparationReentry()
    {
        var previous = AdaptiveProbeVolumes.GetVirtualOffsetBakerOverride();
        var baker = new PrepareReentryBaker();
        s_ExpectPrepareFailure = true;
        s_PrepareFailureObserved = false;
        try
        {
            AdaptiveProbeVolumes.SetVirtualOffsetBakerOverride(baker);
            Require(!AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Intentional preparation failure reported success.");
            Require(!AdaptiveProbeVolumes.lastBakeSucceeded, "Preparation failure left the previous bake result marked successful.");
            Require(baker.checkedReentry && baker.disposed && s_PrepareFailureObserved, "Preparation regression did not exercise reentry and disposal.");
            Require(!AdaptiveProbeVolumes.isRunning && Read(s_BakingProvider, "s_Running") == null, "Preparation failure retained the running owner.");
            Require(!(bool)Read(baker.owner, "m_StandalonePrepareActive") &&
                (int)Read(baker.owner, "m_StandaloneProgressTaskID") == -1, "Preparation failure retained session state.");
            var data = Read(baker.owner, "s_BakeData");
            Require(Read(data, "virtualOffsetJob") == null && Read(data, "lightingJob") == null &&
                !((NativeArray<Vector3>)Read(data, "sortedPositions")).IsCreated, "Preparation failure retained owned resources.");
            Checks.Add("Preparation rejects reentry, retains provider ownership and cleans a failing VO initializer before a normal rebake");
        }
        finally
        {
            AdaptiveProbeVolumes.SetVirtualOffsetBakerOverride(previous);
            s_ExpectPrepareFailure = false;
        }
    }

    [Serializable]
    sealed class Report
    {
        public bool passed;
        public string unityVersion, device, graphicsApi;
        public int probes, deletedFiles;
        public int evaluatedSHProbes, evaluatedSHDirections, nonzeroSHProbes;
        public float minimumL1Energy, minimumL2Energy;
        public bool nativeBakeObserved, resourcesDisposed, diskStreaming;
        public int completedDirectRebakes, completedImmediateClearRebakes, maxActiveStreamingRequests, maxQueuedStreamingRequests;
        public int maxOpenPayloadFiles, pendingOpensBeforeClose, payloadImports;
        public bool publicationRollbackVerified, publicationRecoveryBakeVerified, dilation;
        public string publicationLockedPath;
        public string[] expectedPublicationErrors;
        public List<RebakeRound> directRebakes = new List<RebakeRound>();
        public double elapsedSeconds;
        public string[] checks, errors;
        public string error;
    }

    [Serializable]
    sealed class RebakeRound
    {
        public int round, payloadFiles, payloadImports;
        public bool closeBeforeOpenCompletion, lightingPayloadChanged, importSettled, exclusiveFileAccess;
        public double seconds;
    }

    public static void Run()
    {
        s_RebakeStreaming = Environment.GetEnvironmentVariable("APV_REBAKE_STREAMING") == "1";
        s_PublicationFailure = Environment.GetEnvironmentVariable("APV_PUBLICATION_FAILURE") == "1";
        s_Report = new Report { unityVersion = Application.unityVersion, device = SystemInfo.graphicsDeviceName,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            diskStreaming = s_PublicationFailure || s_RebakeStreaming || Environment.GetEnvironmentVariable("APV_DISK_STREAMING") == "1",
            dilation = Environment.GetEnvironmentVariable("APV_DILATION") == "1" };
        s_Started = EditorApplication.timeSinceStartup;
        Application.logMessageReceived += OnLog;
        try
        {
            Require(SystemInfo.supportsComputeShaders && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,
                "Run this smoke test with a real compute-capable graphics device.");
            Require(!Lightmapping.isRunning, "A native lightmap bake is already active.");
            s_BakingProvider = FindType("UnityEngine.Rendering.ProbeVolumeBakingProvider");
            s_RuntimeProvider = FindType("UnityEngine.Rendering.ProbeReferenceVolumeProvider");
            s_PreviousGraphics = GraphicsSettings.defaultRenderPipeline;
            s_PreviousQuality = QualitySettings.renderPipeline;
            Lightmapping.TryGetLightingSettings(out s_PreviousLighting);
            s_PreviousLightmaps = LightmapSettings.lightmaps;
            SetupScene();
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    static void SetupScene()
    {
        const string root = "Assets/APVAuxiliarySmokeGenerated";
        if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder("Assets", "APVAuxiliarySmokeGenerated");
        string run = "Run_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder(root, run);
        s_Folder = root + "/" + run;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(renderer, s_Folder + "/Renderer.asset");
        var pipeline = UniversalRenderPipelineAsset.Create(renderer);
        var pipelineData = new SerializedObject(pipeline);
        pipelineData.FindProperty("m_RendererDataList").GetArrayElementAtIndex(0).objectReferenceValue = renderer;
        pipelineData.FindProperty("m_LightProbeSystem").intValue = 1;
        pipelineData.FindProperty("m_ProbeVolumeMemoryBudget").intValue = 512;
        pipelineData.FindProperty("m_SupportProbeVolumeGPUStreaming").boolValue = true;
        pipelineData.FindProperty("m_SupportProbeVolumeDiskStreaming").boolValue = s_Report.diskStreaming;
        pipelineData.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.CreateAsset(pipeline, s_Folder + "/Pipeline.asset");
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        s_Camera = new GameObject("Auxiliary smoke camera").AddComponent<Camera>();
        s_Camera.transform.position = new Vector3(0, 2, -6);
        s_Camera.transform.LookAt(new Vector3(0, 1, 0));
        s_Target = new RenderTexture(64, 64, 24) { name = "Auxiliary smoke target" };
        s_Target.Create();
        s_Camera.targetTexture = s_Target;
        bool useTextures = Environment.GetEnvironmentVariable("APV_TEXTURES") == "1";
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetColor("_BaseColor", new Color(0.65f, 0.12f, 0.08f, 1.0f));
        if (useTextures)
            material.SetTexture("_BaseMap", CreateMaterialTexture("GeometryTexture", false));
        AssetDatabase.CreateAsset(material, s_Folder + "/Geometry.mat");
        AddCube("Ground", new Vector3(0, -0.15f, 0), new Vector3(4, 0.3f, 4), material);
        AddCube("Central occluder", new Vector3(0, 0.6f, 0), new Vector3(1.2f, 1.2f, 1.2f), material);
        var emissionMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        emissionMaterial.SetColor("_BaseColor", new Color(0.08f, 0.12f, 0.65f, 1.0f));
        emissionMaterial.EnableKeyword("_EMISSION");
        emissionMaterial.SetColor("_EmissionColor", Color.white * Mathf.Max(0.0f, ParseFloatEnv("APV_EMISSION", 0.0f)));
        emissionMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
        if (useTextures)
        {
            emissionMaterial.SetTexture("_BaseMap", CreateMaterialTexture("CutoutTexture", true));
            emissionMaterial.SetFloat("_AlphaClip", 1);
            emissionMaterial.SetFloat("_Cutoff", 0.5f);
            emissionMaterial.EnableKeyword("_ALPHATEST_ON");
            emissionMaterial.SetOverrideTag("RenderType", "TransparentCutout");
            Checks.Add("APV_TEXTURES: opaque textured ground/occluder and a checkerboard alpha-cutout panel exercise lighting, virtual offset and sky-visibility material paths");
        }
        AssetDatabase.CreateAsset(emissionMaterial, s_Folder + "/Emission.mat");
        AddCube("Emission panel", new Vector3(0, 1.8f, 0), new Vector3(0.8f, 0.1f, 0.8f), emissionMaterial);
        var sun = new GameObject("Baked sun").AddComponent<Light>();
        s_Sun = sun;
        sun.type = LightType.Directional;
        sun.lightmapBakeType = LightmapBakeType.Baked;
        sun.intensity = Mathf.Max(0.0f, ParseFloatEnv("APV_LIGHT_INTENSITY", 0.0f));
        sun.color = Color.white;
        sun.transform.rotation = Quaternion.Euler(45.0f, -35.0f, 0.0f);
        var probes = new GameObject("APV auxiliary volume").AddComponent<ProbeVolume>();
        probes.mode = ProbeVolume.Mode.Local;
        probes.transform.position = new Vector3(0, 1.5f, 0);
        probes.size = new Vector3(4, 3, 4);
        probes.fillEmptySpaces = true;

        // The APV prepare path uses this setting to allow sky occlusion. No native
        // lightmapping API is invoked: only the package's compute jobs are stepped.
        var lighting = new LightingSettings { bakedGI = true, realtimeGI = false, autoGenerate = false,
            lightmapper = LightingSettings.Lightmapper.ProgressiveGPU, directSampleCount = 16,
            indirectSampleCount = 16, environmentSampleCount = 16, lightProbeSampleCountMultiplier = 1,
            maxBounces = 1, lightmapResolution = 1, mixedBakeMode = MixedLightingMode.IndirectOnly };
        AssetDatabase.CreateAsset(lighting, s_Folder + "/Lighting.lighting");
        Lightmapping.lightingSettings = lighting;
        s_Set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
        Invoke(s_Set, "SetDefaults");
        s_Set.minDistanceBetweenProbes = 1;
        s_Set.simplificationLevels = 1;
        s_Set.skyOcclusion = true;
        s_Set.skyOcclusionBakingSamples = 32;
        s_Set.skyOcclusionBakingBounces = 1;
        s_Set.skyOcclusionShadingDirection = true;
        var data = new SerializedObject(s_Set);
        data.FindProperty("settings.virtualOffsetSettings.useVirtualOffset").boolValue = true;
        data.FindProperty("settings.virtualOffsetSettings.searchMultiplier").floatValue = 0.2f;
        if (s_Report.dilation)
        {
            data.FindProperty("settings.dilationSettings.enableDilation").boolValue = true;
            data.FindProperty("settings.dilationSettings.dilationDistance").floatValue = 1;
            data.FindProperty("settings.dilationSettings.dilationIterations").intValue = 2;
        }
        data.FindProperty("useRenderingLayers").boolValue = true;
        data.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.CreateAsset(s_Set, s_Folder + "/BakingSet.asset");
        Require(EditorSceneManager.SaveScene(scene, s_Folder + "/Scene.unity"), "Failed to save smoke scene.");
        Require(s_Set.TryAddScene(AssetDatabase.AssetPathToGUID(scene.path)), "Failed to register scene in Baking Set.");
        Invoke(typeof(ProbeVolumeBakingSet), "OnSceneSaving", scene, null);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Checks.Add("Minimal scene and Baking Set created with virtual offset, sky occlusion, sky direction and rendering layers enabled");
    }

    static Texture2D CreateMaterialTexture(string name, bool cutout)
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, false)
        { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
        var pixels = new Color[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                bool alternate = ((x + y) & 1) != 0;
                var color = alternate ? new Color(0.2f, 0.7f, 0.9f, 1) : new Color(0.85f, 0.45f, 0.2f, 1);
                if (cutout && alternate) color.a = 0;
                pixels[y * 4 + x] = color;
            }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        AssetDatabase.CreateAsset(texture, s_Folder + "/" + name + ".asset");
        return texture;
    }

    static float ParseFloatEnv(string name, float fallback)
    {
        return float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    static void AddCube(string name, Vector3 position, Vector3 scale, Material material)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.position = position;
        cube.transform.localScale = scale;
        cube.GetComponent<Renderer>().sharedMaterial = material;
        cube.GetComponent<Renderer>().renderingLayerMask = 1;
        GameObjectUtility.SetStaticEditorFlags(cube, StaticEditorFlags.ContributeGI);
    }

    static void Tick()
    {
        try
        {
            Require(EditorApplication.timeSinceStartup - s_Started < 360, "Standalone APV smoke timed out.");
            if (Lightmapping.isRunning)
                s_NativeBakeObserved = true;
            Require(!s_NativeBakeObserved, "Native lightmapping bake unexpectedly started.");
            if (s_ClearRegression != null)
            {
                Require(Errors.Count == 0, string.Join("\n", Errors));
                if (!s_ClearRegression.MoveNext()) Finish(null);
                return;
            }
            if (!s_StandaloneStarted)
            {
                s_Camera.Render();
                s_Volume = Read(s_RuntimeProvider, "instance");
                if (!(bool)Read(s_Volume, "isInitialized") || ++s_Frames < 5) return;
                CheckShaders();
                s_Config = StandaloneAPVBakeConfiguration.Default;
                s_Config.samples = (int)Mathf.Clamp(ParseFloatEnv("APV_SAMPLES", s_Config.samples), 1, 4096);
                s_Config.directSamples = (int)Mathf.Clamp(ParseFloatEnv("APV_DIRECT_SAMPLES", s_Config.directSamples), 1, 4096);
                s_Config.bounces = (int)Mathf.Clamp(ParseFloatEnv("APV_BOUNCES", s_Config.bounces), 0, 4);
                s_InitialSunOutput = s_Sun.bakingOutput;
                if (Environment.GetEnvironmentVariable("APV_PREPARE_REENTRY") == "1") CheckPreparationReentry();
                Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Standalone APV bake did not start.");
                s_StandaloneStarted = true;
                s_Owner = Read(s_BakingProvider, "activeOrNull");
                s_BakeData = Read(s_Owner, "s_BakeData");
                s_Report.probes = (int)Read(s_BakeData, "probeCount");
                Require(s_Report.probes > 0, "Placement produced no probes.");
                Checks.Add("Standalone APV state machine started without Unity Lightmapping");
                if (string.Equals(Environment.GetEnvironmentVariable("APV_CANCEL"), "1", StringComparison.Ordinal))
                {
                    Require(AdaptiveProbeVolumes.Cancel(), "Standalone APV cancellation was rejected.");
                    Require(!AdaptiveProbeVolumes.isRunning, "Standalone APV remained running after cancellation.");
                    Require(!AdaptiveProbeVolumes.lastBakeSucceeded, "Cancelled standalone APV bake was marked successful.");
                    RequireLightOutput(s_Sun, s_InitialSunOutput, "Cancellation changed previously unbaked light metadata");
                    Checks.Add("Standalone APV cancellation completed without native Lightmapping cleanup");
                    Finish(null);
                }
                return;
            }
            if (AdaptiveProbeVolumes.isRunning) return;
            Require(AdaptiveProbeVolumes.lastBakeSucceeded, "Standalone APV became idle without a successful finalization.");
            Require(!s_NativeBakeObserved, "Native lightmapping bake unexpectedly started.");
            CheckShaders();
            Require(Errors.Count == 0, string.Join("\n", Errors));
            Checks.Add("Standalone APV bake completed and cleaned its GPU resources");
            CheckBakedSHNonnegative();
            CheckPublishedLightOutput("Completed first bake");
            var sceneData = FindSceneData(s_Sun.gameObject.scene);
            Require(sceneData != null, "Completed bake has no per-scene light metadata owner.");
            sceneData.enabled = false;
            RequireLightOutput(s_Sun, s_InitialSunOutput, "Disabling per-scene data did not restore original light output");
            sceneData.enabled = true;
            CheckPublishedLightOutput("Re-enabled per-scene data");
            Checks.Add("Successful standalone bake publishes Baked light output; per-scene disable restores previous output and re-enable reapplies it");
            if (s_PublicationFailure)
            {
                s_ClearRegression = PublicationFailureRegression();
                return;
            }
            if (s_RebakeStreaming)
            {
                s_ClearRegression = DirectStreamingRebakeRegression();
                return;
            }
            if (string.Equals(Environment.GetEnvironmentVariable("APV_PERSISTENCE_SMOKE"), "1", StringComparison.Ordinal))
            {
                CheckMetadataPersistence();
                Finish(null);
                return;
            }
            if (string.Equals(Environment.GetEnvironmentVariable("APV_CLEAR"), "1", StringComparison.Ordinal))
            {
                s_ClearRegression = ClearRegression();
                return;
            }
            Finish(null);
        }
        catch (Exception exception) { Finish(exception); }
    }

    static void CheckBakedSHNonnegative()
    {
        var cells = (IDictionary)Read(s_Owner, "m_BakedCells");
        var arrays = new List<SphericalHarmonicsL2[]>();
        int total = 0;
        foreach (DictionaryEntry cell in cells)
        {
            var sh = (SphericalHarmonicsL2[])Read(cell.Value, "sh");
            Require(sh != null, "Published cell has no SH coefficients: " + cell.Key);
            arrays.Add(sh);
            total += sh.Length;
        }
        Require(total > 0, "Completed bake retained no published SH to evaluate.");
        var normals = new List<Vector3> { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2) normals.Add(new Vector3(x, y, z).normalized);
        var sun = GameObject.Find("Baked sun").GetComponent<Light>();
        normals.Add(sun.transform.forward); normals.Add(-sun.transform.forward);
        int budget = Math.Min(total, 374), sampled = 0, flatIndex = 0, nextSample = 0;
        float minimumL1 = float.PositiveInfinity, minimumL2 = float.PositiveInfinity;
        foreach (var probes in arrays)
            for (int probe = 0; probe < probes.Length && sampled < budget; probe++, flatIndex++)
            {
                if (flatIndex < nextSample) continue;
                var sh = probes[probe];
                bool nonzero = false;
                for (int channel = 0; channel < 3; channel++)
                {
                    for (int coefficient = 0; coefficient < 9; coefficient++)
                        Require(!float.IsNaN(sh[channel, coefficient]) && !float.IsInfinity(sh[channel, coefficient]),
                            "Published SH is non-finite at probe " + flatIndex + ", channel " + channel + ", coefficient " + coefficient);
                    float dc = sh[channel, 0], tolerance = 0.000001f * Mathf.Max(1, Mathf.Abs(dc));
                    Require(dc >= 0, "Published SH has negative mean energy at probe " + flatIndex);
                    nonzero |= dc > 0.000001f;
                    // SetSHCoefficients already rearranged L1 from Y/Z/X to X/Y/Z.
                    // Match DecodeSH.hlsl, including its stored L2 half-amplitude
                    // and z-polynomial fold into DC. BakingCell.DecompressSH is
                    // deliberately not used: its L2 normalization differs.
                    var l1 = new Vector3(sh[channel, 1] - 0.5f, sh[channel, 2] - 0.5f, sh[channel, 3] - 0.5f) * (4 * dc);
                    float scale = 3.5777088f * dc;
                    float xy = (sh[channel, 4] - 0.5f) * scale, yz = (sh[channel, 5] - 0.5f) * scale;
                    float zz = (sh[channel, 6] - 0.5f) * scale, zx = (sh[channel, 7] - 0.5f) * scale;
                    float xxMinusYY = (sh[channel, 8] - 0.5f) * scale;
                    foreach (var n in normals)
                    {
                        float energyL1 = dc + Vector3.Dot(l1, n);
                        float energyL2 = energyL1 - zz + xy * n.x * n.y + yz * n.y * n.z
                            + 3 * zz * n.z * n.z + zx * n.z * n.x + xxMinusYY * (n.x * n.x - n.y * n.y);
                        if (float.IsNaN(energyL1) || float.IsInfinity(energyL1) || energyL1 < -tolerance)
                            throw new InvalidOperationException("Runtime L1 evaluates negative/non-finite: probe=" + flatIndex + ", channel=" + channel + ", normal=" + n + ", value=" + energyL1);
                        if (float.IsNaN(energyL2) || float.IsInfinity(energyL2) || energyL2 < -tolerance)
                            throw new InvalidOperationException("Runtime L2 evaluates negative/non-finite: probe=" + flatIndex + ", channel=" + channel + ", normal=" + n + ", value=" + energyL2);
                        minimumL1 = Mathf.Min(minimumL1, energyL1);
                        minimumL2 = Mathf.Min(minimumL2, energyL2);
                    }
                }
                if (nonzero) s_Report.nonzeroSHProbes++;
                sampled++;
                nextSample = (int)((long)sampled * total / budget);
            }
        Require(sampled == budget, "Published SH evaluation did not sample the requested probe count.");
        if (sun.intensity > 0 || ParseFloatEnv("APV_EMISSION", 0) > 0)
            Require(s_Report.nonzeroSHProbes > 0, "Lit fixture published only zero-energy probes; nonnegativity would be inconclusive.");
        s_Report.evaluatedSHProbes = sampled;
        s_Report.evaluatedSHDirections = normals.Count;
        s_Report.minimumL1Energy = minimumL1;
        s_Report.minimumL2Energy = minimumL2;
        Checks.Add("Published compressed SH evaluates finite and nonnegative with runtime L1/L2 decoding on " + sampled + " probes and " + normals.Count + " normals (before storage quantization)");
    }

    static void CheckMetadataPersistence()
    {
        Require(Lightmapping.lightingDataAsset == null, "Persistence fixture must have no native lighting data.");
        string path = s_Sun.gameObject.scene.path;
        int previousLightID = s_Sun.GetInstanceID();
        s_Camera.targetTexture = null; // Keep the transient RenderTexture out of scene serialization.
        EditorSceneManager.MarkSceneDirty(s_Sun.gameObject.scene);
        Require(EditorSceneManager.SaveScene(s_Sun.gameObject.scene), "Could not save baked metadata fixture.");
        AssetDatabase.SaveAssets();
        Invoke(s_BakingProvider, "DisposeActive");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        s_Sun = GameObject.Find("Baked sun").GetComponent<Light>();
        s_Camera = GameObject.Find("Auxiliary smoke camera").GetComponent<Camera>();
        s_Camera.targetTexture = s_Target;
        Require(s_Sun.GetInstanceID() != previousLightID, "Reload reused the original native Light.");
        Require(Lightmapping.lightingDataAsset == null, "Native lighting data unexpectedly restored light output.");
        Require(s_Set.HasBakedData(), "Scene reload lost the standalone APV payload.");
        CheckPublishedLightOutput("Scene unload/reopen without LDA");
        var sceneData = FindSceneData(s_Sun.gameObject.scene);
        Require(sceneData != null && sceneData.bakingSet == s_Set, "Reloaded metadata lost its baking-set binding.");
        Checks.Add("Real completed APV bake persists Baked light output across scene save/unload/reopen with no native LDA");
        var previousFiles = BakedFiles(s_Set);
        Require((bool)Invoke(s_BakingProvider, "ClearBakedData", s_Set), "Clear after metadata reload failed.");
        AssertCleared(previousFiles);
        Checks.Add("Clear after scene reload restores the original serialized light output and deletes standalone payload files");
    }

    static IEnumerator ClearRegression()
    {
        // Exercise the button with a different active scene, an absent cached
        // baker, disabled APV, live cells, and real serialized payload files.
        var targetScene = SceneManager.GetActiveScene();
        var targetData = FindSceneData(targetScene);
        Require(targetData != null && targetData.bakingSet == s_Set, "Baked scene has no APV binding.");
        var targetFiles = BakedFiles(s_Set);
        Require(targetFiles.Count >= 4, "Bake did not publish the expected cell files.");
        foreach (var path in targetFiles) Require(File.Exists(path), "Missing baked file: " + path);
        s_Camera.Render();
        Require((int)Read(Read(s_Volume, "cells"), "Count") > 0, "Runtime cells were not loaded before clear.");

        s_ForeignScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var foreignProbe = new GameObject("Other set probe volume").AddComponent<ProbeVolume>();
        foreignProbe.mode = ProbeVolume.Mode.Local;
        foreignProbe.size = Vector3.one;
        var foreignLight = new GameObject("Other set light metadata sentinel").AddComponent<Light>();
        foreignLight.type = LightType.Directional;
        foreignLight.intensity = 0;
        foreignLight.lightmapBakeType = LightmapBakeType.Mixed;
        foreignLight.bakingOutput = new LightBakingOutput { isBaked = true, lightmapBakeType = LightmapBakeType.Mixed,
            mixedLightingMode = MixedLightingMode.Shadowmask, probeOcclusionLightIndex = 17, occlusionMaskChannel = 2 };
        var foreignLightOutput = foreignLight.bakingOutput;
        var foreignSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
        Invoke(foreignSet, "SetDefaults");
        AssetDatabase.CreateAsset(foreignSet, s_Folder + "/OtherSet.asset");
        EditorSceneManager.SaveScene(s_ForeignScene, s_Folder + "/OtherScene.unity");
        Require(foreignSet.TryAddScene(AssetDatabase.AssetPathToGUID(s_ForeignScene.path)), "Cannot add foreign scene.");
        Invoke(typeof(ProbeVolumeBakingSet), "OnSceneSaving", s_ForeignScene, null);
        var foreignData = FindSceneData(s_ForeignScene);
        Require(foreignData != null, "No foreign per-scene data.");
        Set(foreignData, "serializedBakingSet", foreignSet);
        Set(foreignProbe, "mightNeedRebaking", false);
        EditorSceneManager.SaveScene(s_ForeignScene);
        AssetDatabase.SaveAssets();
        var foreignSerialized = File.ReadAllText(AssetDatabase.GetAssetPath(foreignSet));

        var lightmap = new Texture2D(2, 2) { name = "Unrelated lightmap" };
        AssetDatabase.CreateAsset(lightmap, s_Folder + "/UnrelatedLightmap.asset");
        LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = lightmap } };
        var lightingData = Lightmapping.lightingDataAsset;
        Require(SceneManager.GetActiveScene() == s_ForeignScene, "Foreign scene must be active for selection regression.");
        Invoke(s_Volume, "SetActiveBakingSet", s_Set);
        Invoke(s_BakingProvider, "DisposeActive");
        Require(Read(s_BakingProvider, "activeOrNull") == null, "Baker cache was not released.");
        Invoke(s_Volume, "SetEnableStateFromSRP", false);
        Require(!(bool)Read(s_Volume, "enabledBySRP"), "APV must be disabled for clear regression.");
        var workflow = Activator.CreateInstance(FindType("UnityEngine.Rendering.ProbeVolumeBakingWorkflow"), true);
        Invoke(Read(workflow, "m_ActiveSet"), "Set", s_Set);
        Require((bool)Invoke(workflow, "ClearBakedData"), "Selected-set UI clear failed.");
        AssertCleared(targetFiles);
        Require(targetData.bakingSet == null && targetScene.isDirty, "Target scene binding was not cleared and marked dirty.");
        Require(foreignData.bakingSet == foreignSet, "Foreign scene binding was cleared.");
        RequireLightOutput(foreignLight, foreignLightOutput, "Clearing the selected set changed foreign light metadata");
        Require(!(bool)Read(foreignProbe, "mightNeedRebaking"), "Foreign probe volume was invalidated.");
        Require(File.ReadAllText(AssetDatabase.GetAssetPath(foreignSet)) == foreignSerialized, "Foreign Baking Set was modified.");
        Require(LightmapSettings.lightmaps.Length == 1 && LightmapSettings.lightmaps[0].lightmapColor == lightmap &&
            File.Exists(AssetDatabase.GetAssetPath(lightmap)) && Lightmapping.lightingDataAsset == lightingData,
            "APV clear modified regular lightmap data.");
        Require((bool)Invoke(s_BakingProvider, "ClearBakedData", s_Set), "Repeated clear failed.");
        AssertCleared(targetFiles);
        RequireLightOutput(foreignLight, foreignLightOutput, "Repeated clear changed foreign light metadata");
        Checks.Add("UI clears the selected set with no cached baker and disabled SRP; files, references and size are cleared; other set and lightmaps survive; repeated clear succeeds");
        SceneManager.SetActiveScene(targetScene);
        EditorSceneManager.CloseScene(s_ForeignScene, true);
        s_ForeignScene = default;
        LightmapSettings.lightmaps = new LightmapData[0];
        Invoke(s_Volume, "SetEnableStateFromSRP", true);
        for (int frame = 0; frame < 5; frame++) { s_Camera.Render(); yield return null; }

        Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Rebake after clear did not start.");
        while (AdaptiveProbeVolumes.isRunning) yield return null;
        Require(AdaptiveProbeVolumes.lastBakeSucceeded, "Rebake after clear became idle without successful finalization.");
        targetFiles = BakedFiles(s_Set);
        Require(targetFiles.Count >= 4 && (int)Read(Read(s_Set, "cellDescs"), "Count") > 0, "Rebake did not restore cell data.");
        CheckPublishedLightOutput("Rebake after clear");
        Checks.Add("A complete standalone rebake publishes cell data after clear");

        var completedLightOutput = s_Sun.bakingOutput;
        Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Bake for metadata cancellation regression did not start.");
        Require(AdaptiveProbeVolumes.Cancel() && !AdaptiveProbeVolumes.isRunning, "Metadata cancellation regression did not stop.");
        Require(!AdaptiveProbeVolumes.lastBakeSucceeded, "Metadata cancellation was marked successful.");
        RequireLightOutput(s_Sun, completedLightOutput, "Cancelling a rebake modified the previous successful light output");
        Require(s_Set.HasBakedData(), "Cancelling a rebake discarded previous successful APV data.");
        Checks.Add("Cancelling a rebake preserves previously published light metadata and APV data");

        Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Bake for cancellation regression did not start.");
        s_Owner = Read(s_BakingProvider, "activeOrNull");
        int progress = (int)Read(s_Owner, "m_StandaloneProgressTaskID");
        Require(progress >= 0, "Running bake has no progress task.");
        Require((bool)Invoke(s_BakingProvider, "ClearBakedData", s_Set), "Clear during bake failed.");
        Require(!AdaptiveProbeVolumes.isRunning && (int)Read(s_Owner, "m_StandaloneProgressTaskID") == -1,
            "Clear did not stop bake and release progress immediately: running=" + AdaptiveProbeVolumes.isRunning +
            ", task=" + Read(s_Owner, "m_StandaloneProgressTaskID"));
        Require(!AdaptiveProbeVolumes.lastBakeSucceeded, "Clear during bake was marked successful.");
        AssertCleared(targetFiles);
        // Unity's Progress.Remove uses deferred removal by default. The baker
        // releases its task immediately; the editor registry updates next frame.
        for (int frame = 0; frame < 5; frame++) { s_Camera.Render(); yield return null; }
        Require(!Progress.Exists(progress), "Progress indicator survived editor updates after clear.");
        AssertCleared(targetFiles);
        Checks.Add("Clear during bake removes progress and prevents delayed cell publication");

        Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Rebake after cancellation did not start.");
        while (AdaptiveProbeVolumes.isRunning) yield return null;
        Require(AdaptiveProbeVolumes.lastBakeSucceeded, "Rebake after cancellation became idle without successful finalization.");
        targetFiles = BakedFiles(s_Set);
        Require(targetFiles.Count >= 4, "Rebake after cancellation produced no files.");
        CheckPublishedLightOutput("Rebake after cancellation");
        Invoke(s_BakingProvider, "DisposeActive");
        // No rendering between Cleanup and Clear: the volume is truly uninitialized.
        Invoke(s_RuntimeProvider, "Cleanup");
        Require(!(bool)Read(s_Volume, "isInitialized"), "Runtime remained initialized.");
        Require((bool)Invoke(s_BakingProvider, "ClearBakedData", s_Set), "Clear with uninitialized runtime failed.");
        AssertCleared(targetFiles);
        Checks.Add("Clear deletes real baked files with an uninitialized runtime and no cached baker");
        Require(Errors.Count == 0, string.Join("\n", Errors));
    }

    static IEnumerator PublicationFailureRegression()
    {
        var streamed = WaitForStreamedCell();
        while (streamed.MoveNext()) yield return streamed.Current;
        Invoke(s_Volume, "CancelStreamingRequests");
        foreach (var asset in StreamableAssets(s_Set)) Invoke(asset, "Dispose");
        var previous = ValidatePublishedPayload();
        var guids = new Dictionary<string, string>();
        var metaHashes = new Dictionary<string, string>();
        foreach (string path in previous.Keys)
        {
            guids.Add(path, AssetDatabase.AssetPathToGUID(path));
            metaHashes.Add(path, Hash(File.ReadAllBytes(path + ".meta")));
        }
        var oldMetadata = SnapshotPublishedMetadata();
        var oldLightOutput = s_Sun.bakingOutput;
        var sceneData = FindSceneData(s_Sun.gameObject.scene);
        Require(sceneData != null && sceneData.bakingSet == s_Set, "Publication fixture has no scene binding.");
        var runtimeSettings = SnapshotRuntimeSettings();

        // Support data is published after the SH, shared and brick payloads.
        // Read sharing permits transaction backups, but denies File.Replace.
        string lockedPath = (string)Invoke(Read(Read(s_Set, "apvBakedData"), "cellSupportDataAsset"), "GetAssetPath");
        Require(previous.ContainsKey(lockedPath), "No later support payload exists for controlled publication failure.");
        s_Report.publicationLockedPath = lockedPath;
        // Change the requested grid as well as radiometry so rollback must
        // restore layout descriptors, not merely an identical metadata object.
        s_Set.minDistanceBetweenProbes = 0.75f;
        s_Set.probeOffset = new Vector3(0.125f, 0, 0.25f);
        s_Sun.intensity = 2;
        s_PublicationLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        s_ExpectedPublicationPath = Path.GetFullPath(lockedPath).Replace('\\', '/');
        try
        {
            Debug.Log("APV_PUBLICATION_FAILURE_BEGIN locked=" + lockedPath);
            Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Bake for publication failure did not start.");
            s_Owner = Read(s_BakingProvider, "activeOrNull");
            s_BakeData = Read(s_Owner, "s_BakeData");
            int progressTask = (int)Read(s_Owner, "m_StandaloneProgressTaskID");
            while (AdaptiveProbeVolumes.isRunning)
            {
                s_Camera.Render();
                ObserveStreamingRequests();
                Require(Errors.Count == 0, string.Join("\n", Errors));
                yield return null;
            }
            Require(!AdaptiveProbeVolumes.lastBakeSucceeded, "Publication failure was reported as a successful bake.");
            Require(ExpectedPublicationErrors.Count > 0, "Locked later payload did not produce the expected publication I/O failure.");
            Require((int)Read(s_Owner, "m_StandaloneProgressTaskID") == -1, "Publication failure left the progress task attached.");
            Require(Read(s_BakeData, "lightingJob") == null && Read(s_BakeData, "virtualOffsetJob") == null &&
                Read(s_BakeData, "skyOcclusionJob") == null,
                "Publication failure retained baking jobs or a worker.");
            for (int frame = 0; frame < 5; ++frame) yield return null;
            Require(!Progress.Exists(progressTask), "Publication failure left an editor progress entry.");
            Require(s_Set.HasBakedData() && sceneData.bakingSet == s_Set, "Publication failure detached the previous Baking Set.");
            Require(SnapshotPublishedMetadata() == oldMetadata,
                "Failed publication changed bakedData, cellDescs or perSceneCellLists instead of restoring the previous layout.");
            RequireLightOutput(s_Sun, oldLightOutput, "Publication failure changed previously published light output");
            Require(SnapshotRuntimeSettings() == runtimeSettings, "Publication failure changed disk streaming, memory budget or SH bands.");
            var restored = ValidatePublishedPayload();
            Require(restored.Count == previous.Count, "Rollback changed the previous payload set.");
            foreach (var pair in previous)
            {
                Require(restored.TryGetValue(pair.Key, out string current) && current == pair.Value, "Rollback changed previous payload bytes: " + pair.Key);
                Require(guids[pair.Key] == AssetDatabase.AssetPathToGUID(pair.Key) &&
                    metaHashes[pair.Key] == Hash(File.ReadAllBytes(pair.Key + ".meta")), "Rollback changed payload .meta or GUID: " + pair.Key);
            }
            // The deny-replace lock still allows real reads of the restored
            // generation. Verify it can reach GPU residency before unlocking.
            streamed = WaitForStreamedCell();
            while (streamed.MoveNext()) yield return streamed.Current;
            s_Report.publicationRollbackVerified = true;
            Checks.Add("Real mid-publication failure restored every payload hash/.meta/GUID, baked layout, cell descriptors, scene cell lists, light output and runtime settings; driver stopped and old cells streamed again");
        }
        finally
        {
            s_ExpectedPublicationPath = null;
            s_PublicationLock?.Dispose();
            s_PublicationLock = null;
        }

        Debug.Log("APV_PUBLICATION_RECOVERY_BAKE_BEGIN");
        Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Unlocked recovery bake did not start.");
        s_Owner = Read(s_BakingProvider, "activeOrNull");
        s_BakeData = Read(s_Owner, "s_BakeData");
        while (AdaptiveProbeVolumes.isRunning)
        {
            s_Camera.Render();
            ObserveStreamingRequests();
            Require(Errors.Count == 0, string.Join("\n", Errors));
            yield return null;
        }
        Require(AdaptiveProbeVolumes.lastBakeSucceeded, "Publication recovery became idle without successful finalization.");
        var settle = WaitForPayloadImports();
        while (settle.MoveNext()) yield return settle.Current;
        var recovered = ValidatePublishedPayload();
        bool changed = false;
        foreach (var pair in recovered)
            if (pair.Key.EndsWith("CellData.bytes", StringComparison.OrdinalIgnoreCase) &&
                previous.TryGetValue(pair.Key, out string oldHash) && oldHash != pair.Value) changed = true;
        Require(changed, "Unlocked recovery did not publish changed SH.");
        Require((float)Read(s_Set, "bakedMinDistanceBetweenProbes") == s_Set.minDistanceBetweenProbes &&
            (Vector3)Read(s_Set, "bakedProbeOffset") == s_Set.probeOffset, "Recovery bake did not publish the new requested grid.");
        CheckPublishedLightOutput("Unlocked publication recovery bake");
        CheckBakedSHNonnegative();
        Require(SnapshotRuntimeSettings() == runtimeSettings, "Successful publication/dilation did not restore disk streaming, memory budget or SH bands.");
        if (s_Report.dilation)
        {
            var dilationSettings = Read(Read(s_Set, "settings"), "dilationSettings");
            Require((bool)Read(s_Set, "hasDilation") && (int)Read(dilationSettings, "dilationIterations") == 2,
                "APV_DILATION did not enable two real dilation iterations.");
            Checks.Add("Unlocked recovery runs dilation with distance 1 and two iterations, restoring runtime streaming/budget/bands afterwards");
        }
        streamed = WaitForStreamedCell();
        while (streamed.MoveNext()) yield return streamed.Current;
        s_Report.publicationRecoveryBakeVerified = true;
        Checks.Add("After unlocking the payload, a complete standalone rebake publishes the requested new grid and streams successfully without Clear");
        Debug.Log("APV_PUBLICATION_RECOVERY_BAKE_COMPLETE");
    }

    static string SnapshotRuntimeSettings() => Read(s_Volume, "forceNoDiskStreaming") + "|" +
        Read(s_Volume, "memoryBudget") + "|" + Read(s_Volume, "shBands");

    static string SnapshotPublishedMetadata()
    {
        // Native TextAsset instance IDs may change during reimport. Payload
        // identity is checked separately through GUIDs and .meta hashes.
        string data = Regex.Replace(JsonUtility.ToJson(Read(s_Set, "bakedData")),
            "\"instanceID\"\\s*:\\s*-?\\d+", "\"instanceID\":0");
        var cells = new List<string>();
        foreach (DictionaryEntry cell in (IDictionary)Read(s_Set, "cellDescs"))
            cells.Add(cell.Key + "=" + JsonUtility.ToJson(cell.Value));
        cells.Sort(StringComparer.Ordinal);
        var scenes = new List<string>();
        foreach (DictionaryEntry scene in (IDictionary)Read(s_Set, "perSceneCellLists"))
        {
            var indices = new List<int>();
            foreach (int index in (IEnumerable)scene.Value) indices.Add(index);
            scenes.Add(scene.Key + "=" + string.Join(",", indices));
        }
        scenes.Sort(StringComparer.Ordinal);
        return data + "\n" + string.Join("\n", cells) + "\n" + string.Join("\n", scenes);
    }

    static IEnumerator DirectStreamingRebakeRegression()
    {
        Require((bool)Read(s_Volume, "diskStreamingEnabled"), "Direct rebake regression requires actual runtime disk streaming.");
        var previous = ValidatePublishedPayload();
        int rounds = (int)Mathf.Clamp(ParseFloatEnv("APV_REBAKE_ROUNDS", 3), 3, 8);
        for (int round = 1; round <= rounds; ++round)
        {
            var ready = WaitForStreamedCell();
            while (ready.MoveNext()) yield return ready.Current;
            var oldAssets = StreamableAssets(s_Set);
            var record = new RebakeRound { round = round, closeBeforeOpenCompletion = (round % 2) == 0 };
            s_Report.directRebakes.Add(record);
            int importsBefore = s_PayloadImports;
            PayloadImportCounts.Clear();
            double started = EditorApplication.timeSinceStartup;

            // Exercise the same native FileHandles as real disk streaming. Odd
            // rounds keep them open. Even rounds close immediately after OpenFile,
            // without completing the opening job or waiting for the close fence.
            PrepareStreamingHandles(oldAssets, record.closeBeforeOpenCompletion);
            var guids = new Dictionary<string, string>();
            foreach (string path in previous.Keys) guids.Add(path, AssetDatabase.AssetPathToGUID(path));
            s_Sun.intensity = 0.5f + round * 0.75f;
            Debug.Log("APV_DIRECT_REBAKE_BEGIN round=" + round + " closeImmediately=" + record.closeBeforeOpenCompletion);
            Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Direct rebake " + round + " did not start.");
            s_Owner = Read(s_BakingProvider, "activeOrNull");
            s_BakeData = Read(s_Owner, "s_BakeData");
            while (AdaptiveProbeVolumes.isRunning)
            {
                s_Camera.Render();
                ObserveStreamingRequests();
                // Keep the old payload's handle lifecycle exercised up to the
                // publication boundary, even if bake preparation unloaded cells.
                PrepareStreamingHandles(oldAssets, record.closeBeforeOpenCompletion);
                Require(Errors.Count == 0, string.Join("\n", Errors));
                yield return null;
            }
            foreach (var asset in oldAssets)
                Require(!(bool)Invoke(asset, "IsOpen"), "Rebake abandoned an open previous payload handle: " + Invoke(asset, "GetAssetPath"));
            Require(Errors.Count == 0 && s_Set.HasBakedData(), "Direct rebake failed to retain a complete Baking Set.");
            CheckPublishedLightOutput("Direct rebake " + round);
            CheckBakedSHNonnegative();

            var settle = WaitForPayloadImports();
            while (settle.MoveNext()) yield return settle.Current;
            record.importSettled = true;
            var current = ValidatePublishedPayload();
            Require(current.Count == previous.Count, "Direct rebake changed the expected payload file count.");
            foreach (var entry in current)
            {
                Require(guids.TryGetValue(entry.Key, out var guid) && guid == AssetDatabase.AssetPathToGUID(entry.Key),
                    "Direct rebake replaced or lost a payload GUID: " + entry.Key);
                if (entry.Key.EndsWith("CellData.bytes", StringComparison.OrdinalIgnoreCase) && previous[entry.Key] != entry.Value)
                    record.lightingPayloadChanged = true;
            }
            Require(record.lightingPayloadChanged, "Rebake did not publish changed SH after changing the Baked sun intensity.");

            // Stream the NEW payload all the way into a live cell, then release
            // its handles and verify no previous close/open leaked an OS lock.
            ready = WaitForStreamedCell();
            while (ready.MoveNext()) yield return ready.Current;
            Invoke(s_Volume, "CancelStreamingRequests");
            foreach (var asset in StreamableAssets(s_Set)) Invoke(asset, "Dispose");
            double deadline = EditorApplication.timeSinceStartup + 10;
            string blocked;
            while (!CanOpenPayloadsExclusively(current.Keys, out blocked))
            {
                Require(EditorApplication.timeSinceStartup < deadline, "Payload retained an OS file lock after streaming disposal: " + blocked);
                yield return null;
            }
            record.exclusiveFileAccess = true;
            record.payloadFiles = current.Count;
            record.payloadImports = s_PayloadImports - importsBefore;
            record.seconds = EditorApplication.timeSinceStartup - started;
            ++s_Report.completedDirectRebakes;
            previous = current;
            Checks.Add("Direct rebake " + round + " completed without Clear; " +
                (record.closeBeforeOpenCompletion ? "immediate OpenFile/CloseFile" : "open native streaming handles") +
                "; changed SH, complete imported payloads, stable GUIDs, streamed cells and released OS locks verified");
            Debug.Log("APV_DIRECT_REBAKE_COMPLETE round=" + round + " files=" + record.payloadFiles);
        }
        Require(s_Report.completedDirectRebakes == rounds, "Not all direct rebakes completed.");
        // Reproduce Clear -> immediate Bake while the native file lifecycle has
        // just been exercised. There is intentionally no yield or manual handle
        // disposal between Clear and Bake: deletion can otherwise hide a leaked
        // Windows delete-pending handle until the same pathname is recreated.
        var reload = WaitForStreamedCell();
        while (reload.MoveNext()) yield return reload.Current;
        var clearedFiles = BakedFiles(s_Set);
        PrepareStreamingHandles(StreamableAssets(s_Set), true);
        PayloadImportCounts.Clear();
        Debug.Log("APV_IMMEDIATE_CLEAR_REBAKE_BEGIN");
        Require((bool)Invoke(s_BakingProvider, "ClearBakedData", s_Set), "Streaming Clear before immediate rebake failed.");
        AssertCleared(clearedFiles);
        Require(AdaptiveProbeVolumes.BakeStandaloneAsync(s_Config), "Immediate rebake after streaming Clear did not start.");
        s_Owner = Read(s_BakingProvider, "activeOrNull");
        s_BakeData = Read(s_Owner, "s_BakeData");
        while (AdaptiveProbeVolumes.isRunning)
        {
            s_Camera.Render();
            ObserveStreamingRequests();
            Require(Errors.Count == 0, string.Join("\n", Errors));
            yield return null;
        }
        var finalImports = WaitForPayloadImports();
        while (finalImports.MoveNext()) yield return finalImports.Current;
        ValidatePublishedPayload();
        CheckPublishedLightOutput("Immediate rebake after streaming Clear");
        CheckBakedSHNonnegative();
        reload = WaitForStreamedCell();
        while (reload.MoveNext()) yield return reload.Current;
        Invoke(s_Volume, "CancelStreamingRequests");
        foreach (var asset in StreamableAssets(s_Set)) Invoke(asset, "Dispose");
        double finalDeadline = EditorApplication.timeSinceStartup + 10;
        string finalBlocked;
        while (!CanOpenPayloadsExclusively(BakedFiles(s_Set), out finalBlocked))
        {
            Require(EditorApplication.timeSinceStartup < finalDeadline, "Immediate clear/rebake retained an OS lock: " + finalBlocked);
            yield return null;
        }
        s_Report.completedImmediateClearRebakes = 1;
        Checks.Add("Real streamed payload -> immediate OpenFile/CloseFile -> Clear -> Bake in the same editor tick recreated complete readable/imported payloads without delete-pending locks");
        Debug.Log("APV_IMMEDIATE_CLEAR_REBAKE_COMPLETE");
        Checks.Add("Open jobs still pending at CloseFile were observed " + s_Report.pendingOpensBeforeClose + " times (timing dependent; zero is reported, not treated as covered)");
        Require(Errors.Count == 0, string.Join("\n", Errors));
    }

    static IEnumerator WaitForStreamedCell()
    {
        // Evict GPU residency without clearing APV assets, so a previously
        // uploaded cell cannot satisfy this check without fresh disk reads.
        Invoke(s_Volume, "UnloadAllCells");
        double deadline = EditorApplication.timeSinceStartup + 30;
        for (;;)
        {
            s_Camera.Render();
            ObserveStreamingRequests();
            int uploaded = 0;
            foreach (DictionaryEntry cell in (IDictionary)Read(s_Volume, "cells"))
                if ((bool)Read(cell.Value, "loaded") && (bool)Read(Read(cell.Value, "indexInfo"), "indexUpdated")) ++uploaded;
            Require(Errors.Count == 0, string.Join("\n", Errors));
            if (uploaded > 0) yield break;
            Require(EditorApplication.timeSinceStartup < deadline, "Disk streaming never completed a live cell upload.");
            yield return null;
        }
    }

    static void ObserveStreamingRequests()
    {
        int active = (int)Read(Read(s_Volume, "m_ActiveStreamingRequests"), "Count");
        int queued = (int)Read(Read(s_Volume, "m_StreamingQueue"), "Count");
        s_Report.maxActiveStreamingRequests = Math.Max(s_Report.maxActiveStreamingRequests, active);
        s_Report.maxQueuedStreamingRequests = Math.Max(s_Report.maxQueuedStreamingRequests, queued);
    }

    static void PrepareStreamingHandles(List<object> assets, bool closeImmediately)
    {
        // CloseFile owns the open/close jobs, not the streaming read commands.
        // Drain readers before manually closing files, just like the owner must.
        if (closeImmediately) Invoke(s_Volume, "CancelStreamingRequests");
        int open = 0;
        foreach (var asset in assets)
        {
            var handle = (FileHandle)Invoke(asset, "OpenFile");
            Require(handle.IsValid(), "Streaming payload did not open: " + Invoke(asset, "GetAssetPath"));
            ++open;
            if (closeImmediately)
            {
                if (!handle.JobHandle.IsCompleted) ++s_Report.pendingOpensBeforeClose;
                Invoke(asset, "CloseFile");
            }
            else
            {
                handle.JobHandle.Complete();
                Require(handle.Status == FileStatus.Open, "Streaming payload open failed: " + Invoke(asset, "GetAssetPath"));
            }
        }
        s_Report.maxOpenPayloadFiles = Math.Max(s_Report.maxOpenPayloadFiles, open);
    }

    static List<object> StreamableAssets(ProbeVolumeBakingSet set)
    {
        var assets = new List<object>();
        var data = Read(set, "apvBakedData");
        void Add(object owner, string field)
        {
            var asset = Read(owner, field);
            if (asset != null && (bool)Invoke(asset, "IsValid")) assets.Add(asset);
        }
        Add(data, "cellBricksDataAsset"); Add(data, "cellSharedDataAsset"); Add(data, "cellSupportDataAsset");
        foreach (DictionaryEntry scenario in (IDictionary)Read(data, "scenarios"))
        {
            Add(scenario.Value, "cellDataAsset"); Add(scenario.Value, "cellOptionalDataAsset"); Add(scenario.Value, "cellProbeOcclusionDataAsset");
        }
        return assets;
    }

    static Dictionary<string, string> ValidatePublishedPayload()
    {
        var hashes = new Dictionary<string, string>();
        foreach (var asset in StreamableAssets(s_Set))
        {
            string path = (string)Invoke(asset, "GetAssetPath");
            long expected = 0;
            foreach (DictionaryEntry descriptor in (IDictionary)Read(asset, "streamableCellDescs"))
                expected = Math.Max(expected, (int)Read(descriptor.Value, "offset") +
                    (long)(int)Read(descriptor.Value, "elementCount") * (int)Read(asset, "elementSize"));
            Require(File.Exists(path) && new FileInfo(path).Length == expected,
                "Payload is absent or truncated relative to its cell descriptors: " + path + ", expected=" + expected);
            byte[] bytes = File.ReadAllBytes(path);
            var imported = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            Require(imported != null && imported.bytes.Length == bytes.Length && Hash(imported.bytes) == Hash(bytes),
                "Imported payload differs from the complete disk bytes: " + path);
            Require(File.Exists(path + ".meta") && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)), "Payload lost its GUID: " + path);
            hashes.Add(path, Hash(bytes));
        }
        Require(hashes.Count >= 4, "Too few published payload files to cover APV disk streaming.");
        return hashes;
    }

    static string Hash(byte[] bytes)
    {
        using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
    }

    static bool CanOpenPayloadsExclusively(IEnumerable<string> paths, out string blocked)
    {
        foreach (string path in paths)
            try { using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (IOException) { blocked = path; return false; }
            catch (UnauthorizedAccessException) { blocked = path; return false; }
        blocked = null;
        return true;
    }

    static IEnumerator WaitForPayloadImports()
    {
        double deadline = EditorApplication.timeSinceStartup + 30;
        int stableFrames = 0;
        while (stableFrames < 8 || EditorApplication.timeSinceStartup - s_LastPayloadImport < 0.25)
        {
            Require(Errors.Count == 0, string.Join("\n", Errors));
            Require(EditorApplication.timeSinceStartup < deadline, "AssetDatabase did not settle after APV publication; possible import loop.");
            stableFrames = EditorApplication.isUpdating ? 0 : stableFrames + 1;
            yield return null;
        }
    }

    internal static void TrackPayloadImports(string[] imported)
    {
        if ((!s_RebakeStreaming && !s_PublicationFailure) || s_Finished || string.IsNullOrEmpty(s_Folder)) return;
        foreach (string path in imported)
        {
            if (!path.StartsWith(s_Folder + "/", StringComparison.Ordinal) || !path.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase)) continue;
            ++s_PayloadImports;
            s_LastPayloadImport = EditorApplication.timeSinceStartup;
            PayloadImportCounts.TryGetValue(path, out int count);
            PayloadImportCounts[path] = ++count;
            if (count == 32) Errors.Add("APV payload imported at least 32 times in one bake; possible infinite import loop: " + path);
        }
    }

    static ProbeVolumePerSceneData FindSceneData(Scene scene)
    {
        foreach (var data in UnityEngine.Object.FindObjectsByType<ProbeVolumePerSceneData>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (data.gameObject.scene == scene) return data;
        return null;
    }

    static HashSet<string> BakedFiles(ProbeVolumeBakingSet set)
    {
        var paths = new HashSet<string>();
        var data = Read(set, "apvBakedData");
        void Add(object owner, string field)
        {
            var asset = Read(owner, field);
            if (asset != null && (bool)Invoke(asset, "IsValid")) paths.Add((string)Invoke(asset, "GetAssetPath"));
        }
        Add(data, "cellBricksDataAsset");
        Add(data, "cellSharedDataAsset");
        Add(data, "cellSupportDataAsset");
        foreach (DictionaryEntry scenario in (IDictionary)Read(data, "scenarios"))
        {
            Add(scenario.Value, "cellDataAsset");
            Add(scenario.Value, "cellOptionalDataAsset");
            Add(scenario.Value, "cellProbeOcclusionDataAsset");
        }
        return paths;
    }

    static void AssertCleared(HashSet<string> previousFiles)
    {
        RequireLightOutput(s_Sun, s_InitialSunOutput, "APV clear did not restore original light baking output");
        foreach (var path in previousFiles)
            Require(!File.Exists(path) && !File.Exists(path + ".meta"), "Baked file survived clear: " + path);
        Require((int)Read(Read(s_Set, "cellDescs"), "Count") == 0 && (int)Read(Read(s_Set, "cellDataMap"), "Count") == 0,
            "Baking Set retained cell descriptors or CPU data.");
        Require(BakedFiles(s_Set).Count == 0 && !s_Set.HasBakedData(), "Baking Set retained payload references.");
        var data = Read(s_Set, "apvBakedData");
        Require((long)Invoke(data, "GetDiskSizeOfSharedData") == 0 &&
            (long)Invoke(data, "GetDiskSizeOfScenarioData", Read(s_Set, "lightingScenario")) == 0, "UI disk-size summary is not zero.");
        s_Report.deletedFiles = Math.Max(s_Report.deletedFiles, previousFiles.Count);
    }

    static void CheckPublishedLightOutput(string stage)
    {
        var expected = new LightBakingOutput { isBaked = true, lightmapBakeType = LightmapBakeType.Baked,
            mixedLightingMode = MixedLightingMode.IndirectOnly, probeOcclusionLightIndex = -1, occlusionMaskChannel = -1 };
        RequireLightOutput(s_Sun, expected, stage + " did not publish Baked light metadata");
    }

    static void RequireLightOutput(Light light, LightBakingOutput expected, string message)
    {
        Require(light != null, message + ": light was destroyed");
        var actual = light.bakingOutput;
        Require(actual.isBaked == expected.isBaked && actual.lightmapBakeType == expected.lightmapBakeType &&
            actual.mixedLightingMode == expected.mixedLightingMode && actual.probeOcclusionLightIndex == expected.probeOcclusionLightIndex &&
            actual.occlusionMaskChannel == expected.occlusionMaskChannel,
            message + ": actual=" + JsonUtility.ToJson(actual) + ", expected=" + JsonUtility.ToJson(expected));
    }

    static void CheckShaders()
    {
        string prefix = "Packages/com.unity.render-pipelines.core/Editor/Lighting/APV/";
        foreach (string relative in new[] { "VirtualOffset/TraceVirtualOffset.urtshader", "DynamicGI/DynamicGISkyOcclusion.urtshader", "RenderingLayerMask/TraceRenderingLayerMask.urtshader", "StandaloneAPV.urtshader" })
        {
            string path = prefix + relative;
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Require(shader != null && shader.HasKernel("MainRayGenShader"), "Missing imported compute kernel: " + path);
            if (relative.Contains("SkyOcclusion")) Require(shader.HasKernel("EncodeShadingDirection"), "Missing sky encoding kernel.");
            var method = typeof(ShaderUtil).GetMethod("GetComputeShaderMessages", Flags, null, new[] { typeof(ComputeShader) }, null);
            Require(method != null, "Shader compiler diagnostics are unavailable.");
            foreach (object message in (IEnumerable)method.Invoke(null, new object[] { shader }))
                Require(!string.Equals(Read(message, "severity").ToString(), "Error", StringComparison.OrdinalIgnoreCase), path + ": " + Read(message, "message"));
        }
    }

    static void Finish(Exception error)
    {
        if (s_Finished) return;
        s_Finished = true;
        EditorApplication.update -= Tick;
        s_ExpectedPublicationPath = null;
        s_PublicationLock?.Dispose();
        s_PublicationLock = null;
        try
        {
            if (s_BakingProvider != null)
            {
                var owner = Read(s_BakingProvider, "activeOrNull");
                if (owner != null) s_BakeData = Read(owner, "s_BakeData");
                Invoke(s_BakingProvider, "DisposeActive");
            }
            s_Report.resourcesDisposed = s_BakeData == null || (Read(s_BakeData, "virtualOffsetJob") == null &&
                Read(s_BakeData, "skyOcclusionJob") == null && Read(s_BakeData, "layerMaskJob") == null &&
                Read(s_BakeData, "lightingJob") == null);
            Require(s_Report.resourcesDisposed, "Baker retained jobs after disposal.");
            if (s_ForeignScene.IsValid() && s_ForeignScene.isLoaded) EditorSceneManager.CloseScene(s_ForeignScene, true);
            LightmapSettings.lightmaps = s_PreviousLightmaps ?? new LightmapData[0];
            if (s_Camera != null) s_Camera.targetTexture = null;
            if (s_Target != null) { s_Target.Release(); UnityEngine.Object.DestroyImmediate(s_Target); }
            Lightmapping.lightingSettings = s_PreviousLighting;
            GraphicsSettings.defaultRenderPipeline = s_PreviousGraphics;
            QualitySettings.renderPipeline = s_PreviousQuality;
        }
        catch (Exception cleanup) { error = error == null ? cleanup : new AggregateException(error, cleanup); }
        s_Report.nativeBakeObserved = s_NativeBakeObserved;
        s_Report.elapsedSeconds = EditorApplication.timeSinceStartup - s_Started;
        s_Report.payloadImports = s_PayloadImports;
        s_Report.expectedPublicationErrors = ExpectedPublicationErrors.ToArray();
        s_Report.passed = error == null && !s_NativeBakeObserved && Errors.Count == 0;
        s_Report.error = error?.ToString();
        s_Report.errors = Errors.ToArray();
        s_Report.checks = Checks.ToArray();
        Application.logMessageReceived -= OnLog;
        File.WriteAllText(ReportPath, JsonUtility.ToJson(s_Report, true));
        Debug.Log("APV_STANDALONE_SMOKE " + (s_Report.passed ? "PASS" : "FAIL") + " probes=" + s_Report.probes + " report=" + Path.GetFullPath(ReportPath));
        if (error != null) Debug.LogException(error);
        if (Application.isBatchMode) EditorApplication.Exit(s_Report.passed ? 0 : 1);
    }

    static void OnLog(string message, string stack, LogType type)
    {
        if (s_ExpectPrepareFailure && type == LogType.Exception && message.Contains(PrepareFailureMessage))
        {
            s_PrepareFailureObserved = true;
            return;
        }
        if (s_ExpectedPublicationPath != null && (type == LogType.Error || type == LogType.Exception))
        {
            string diagnostic = (message + "\n" + stack).Replace('\\', '/');
            bool ioError = message.StartsWith("IOException", StringComparison.Ordinal) ||
                message.StartsWith("System.IO.IOException", StringComparison.Ordinal) ||
                message.StartsWith("UnauthorizedAccessException", StringComparison.Ordinal) ||
                message.StartsWith("System.UnauthorizedAccessException", StringComparison.Ordinal);
            string relativePath = s_Report.publicationLockedPath.Replace('\\', '/');
            bool expectedPath = diagnostic.IndexOf(s_ExpectedPublicationPath, StringComparison.OrdinalIgnoreCase) >= 0 ||
                diagnostic.IndexOf(relativePath, StringComparison.OrdinalIgnoreCase) >= 0;
            if (ioError && expectedPath &&
                diagnostic.Contains("APVBakingFileTransaction.Publish"))
            {
                ExpectedPublicationErrors.Add(message);
                return;
            }
        }
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert ||
            message.IndexOf("AccessDenied", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("infinite import loop", StringComparison.OrdinalIgnoreCase) >= 0)
            Errors.Add(message);
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static Type FindType(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        { Type type = assembly.GetType(name, false); if (type != null) return type; }
        throw new TypeLoadException(name);
    }
    static object Read(object target, string name)
    {
        Require(target != null, "Cannot read " + name + " on a null target.");
        for (Type type = target as Type ?? target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target is Type ? null : target);
            PropertyInfo property = type.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(target is Type ? null : target);
        }
        throw new MissingMemberException(target.ToString(), name);
    }
    static void Set(object target, string name, object value)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            field.SetValue(target, value);
            return;
        }
        throw new MissingFieldException(target.ToString(), name);
    }
    static object Invoke(object target, string name, params object[] args)
    {
        Require(target != null, "Cannot invoke " + name + " on a null target.");
        for (Type type = target as Type ?? target.GetType(); type != null; type = type.BaseType)
            foreach (MethodInfo method in type.GetMethods(Flags | BindingFlags.DeclaredOnly))
            {
                if (method.Name != name || method.GetParameters().Length != args.Length) continue;
                try { return method.Invoke(target is Type ? null : target, args); }
                catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
            }
        throw new MissingMethodException(target.ToString(), name);
    }
}

sealed class APVStandalonePayloadImportObserver : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        => APVStandaloneSmoke.TrackPayloadImports(imported);
}
