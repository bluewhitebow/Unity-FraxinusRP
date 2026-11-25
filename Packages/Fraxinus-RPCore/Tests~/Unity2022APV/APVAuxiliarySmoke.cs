// Copy into an Editor assembly, then run with -batchmode -executeMethod APVAuxiliarySmoke.Run.
// Requires a real graphics device; do not pass -nographics or -quit.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class APVAuxiliarySmoke
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const string ReportPath = "apv-auxiliary-only-result.json";
    static readonly string[] StageFields = { "virtualOffsetJob", "skyOcclusionJob", "layerMaskJob" };
    static readonly List<string> Errors = new List<string>();
    static readonly List<string> Checks = new List<string>();
    static Report s_Report;
    static ProbeVolumeBakingSet s_Set;
    static Camera s_Camera;
    static RenderTexture s_Target;
    static object s_Owner, s_BakeData, s_Volume;
    static Type s_BakingProvider, s_RuntimeProvider;
    static RenderPipelineAsset s_PreviousGraphics, s_PreviousQuality;
    static LightingSettings s_PreviousLighting;
    static double s_Started;
    static int s_Frames, s_Stage, s_StageSteps;
    static bool s_Finished, s_NativeBakeObserved;
    static string s_Folder;

    [Serializable]
    sealed class Report
    {
        public bool passed;
        public string unityVersion, device, graphicsApi, backend;
        public int probes, virtualOffsetCount, skyOcclusionCount, skyDirectionCount, encodedDirectionCount, renderingLayerCount;
        public int virtualOffsetSteps, skyOcclusionSteps, renderingLayerSteps, movedProbes, nonzeroSkyProbes, assignedLayerProbes;
        public float maxOffsetLength, minSkyL0, maxSkyL0, maxDirectionLength;
        public bool nativeBakeObserved, resourcesDisposed;
        public double elapsedSeconds;
        public string[] checks, errors;
        public string error;
    }

    public static void Run()
    {
        s_Report = new Report { unityVersion = Application.unityVersion, device = SystemInfo.graphicsDeviceName,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(), minSkyL0 = float.MaxValue };
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
        pipelineData.FindProperty("m_SupportProbeVolumeDiskStreaming").boolValue = false;
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
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        AssetDatabase.CreateAsset(material, s_Folder + "/Geometry.mat");
        AddCube("Ground", new Vector3(0, -0.15f, 0), new Vector3(4, 0.3f, 4), material);
        AddCube("Central occluder", new Vector3(0, 0.6f, 0), new Vector3(1.2f, 1.2f, 1.2f), material);
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
            Require(EditorApplication.timeSinceStartup - s_Started < 180, "Auxiliary GPU smoke timed out.");
            if (Lightmapping.isRunning)
                s_NativeBakeObserved = true;
            Require(!s_NativeBakeObserved && !Lightmapping.isRunning, "Native lightmapping unexpectedly started.");
            if (s_Owner == null)
            {
                s_Camera.Render();
                s_Volume = Read(s_RuntimeProvider, "instance");
                if (!(bool)Read(s_Volume, "isInitialized") || ++s_Frames < 5) return;
                CheckShaders();
                s_Owner = Invoke(s_BakingProvider, "PrepareBake");
                Require(s_Owner != null, "Provider rejected auxiliary preparation.");
                Require((bool)Invoke(s_Owner, "PrepareBaking"), "APV owner failed to prepare bake data.");
                s_BakeData = Read(s_Owner, "s_BakeData");
                s_Report.probes = (int)Read(s_BakeData, "probeCount");
                Require(s_Report.probes > 0, "Placement produced no probes.");
                Require(s_Set.skyOcclusion && s_Set.skyOcclusionShadingDirection, "Preparation disabled sky outputs.");
                var tracing = Read(s_Owner, "s_TracingContext");
                s_Report.backend = Read(Read(tracing, "m_Context"), "BackendType").ToString();
                Require(s_Report.backend == "Compute", "Expected package compute backend, got " + s_Report.backend);
                foreach (string field in StageFields)
                {
                    object job = Read(s_BakeData, field);
                    Require((ulong)Read(job, "stepCount") > 0, field + " was disabled.");
                    object accel = Invoke(Read(job, "m_AccelerationStructure"), "GetAccelerationStructure");
                    Require(accel.GetType().Name == "ComputeRayTracingAccelStruct", field + " did not use GPU compute BVH.");
                }
                Checks.Add("All three prepared jobs own a ComputeRayTracingAccelStruct and nonzero work");
                return;
            }

            object current = Read(s_BakeData, StageFields[s_Stage]);
            Require((bool)Invoke(current, "Step"), StageFields[s_Stage] + " Step failed.");
            s_StageSteps++;
            if ((ulong)Read(current, "currentStep") < (ulong)Read(current, "stepCount")) return;
            ValidateOutput(current);
            if (s_Stage == 0) Invoke(s_BakeData, "ApplyVirtualOffset");
            if (s_Stage == 1)
            {
                Invoke(current, "Encode", s_Volume);
                var encoded = Read(current, "encodedDirections");
                s_Report.encodedDirectionCount = RequireLength(encoded, "encoded sky directions");
                for (int i = 0; i < s_Report.encodedDirectionCount; i++)
                    Require((uint)Element(encoded, i) <= 255, "Invalid encoded sky direction index.");
            }
            Checks.Add(StageFields[s_Stage] + " completed " + s_StageSteps + " Step calls with valid nonempty output");
            s_StageSteps = 0;
            if (++s_Stage < StageFields.Length) return;
            CheckShaders();
            Require(Errors.Count == 0, string.Join("\n", Errors));
            Finish(null);
        }
        catch (Exception exception) { Finish(exception); }
    }

    static void ValidateOutput(object job)
    {
        if (s_Stage == 0)
        {
            object array = Read(job, "offsets");
            s_Report.virtualOffsetCount = RequireLength(array, "virtual offsets");
            s_Report.virtualOffsetSteps = s_StageSteps;
            for (int i = 0; i < s_Report.virtualOffsetCount; i++)
            {
                var v = (Vector3)Element(array, i);
                Require(Finite(v) && v.magnitude <= 4, "Nonfinite or unbounded virtual offset.");
                s_Report.maxOffsetLength = Mathf.Max(s_Report.maxOffsetLength, v.magnitude);
                if (v.sqrMagnitude > 1e-10f) s_Report.movedProbes++;
            }
        }
        else if (s_Stage == 1)
        {
            object array = Read(job, "occlusion"), directions = Read(job, "shadingDirections");
            s_Report.skyOcclusionCount = RequireLength(array, "sky SH");
            s_Report.skyDirectionCount = RequireLength(directions, "sky directions");
            s_Report.skyOcclusionSteps = s_StageSteps;
            for (int i = 0; i < s_Report.skyOcclusionCount; i++)
            {
                var v = (Vector4)Element(array, i);
                var d = (Vector3)Element(directions, i);
                Require(Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w), "Sky SH contains NaN/Infinity.");
                // Sphere visibility SH: L0 <= 4*pi*Y00; L1 windowing can only reduce magnitude.
                Require(v.x >= -0.0001f && v.x <= 3.546f && Mathf.Abs(v.y) <= 6.15f && Mathf.Abs(v.z) <= 6.15f && Mathf.Abs(v.w) <= 6.15f,
                    "Sky SH exceeds sphere-visibility bounds.");
                Require(Finite(d) && d.magnitude <= 1.001f, "Sky shading direction exceeds averaged unit-vector bounds.");
                s_Report.minSkyL0 = Mathf.Min(s_Report.minSkyL0, v.x);
                s_Report.maxSkyL0 = Mathf.Max(s_Report.maxSkyL0, v.x);
                s_Report.maxDirectionLength = Mathf.Max(s_Report.maxDirectionLength, d.magnitude);
                if (v.x > 0.0001f) s_Report.nonzeroSkyProbes++;
            }
            Require(s_Report.nonzeroSkyProbes > 0, "All sky outputs are zero despite open sky.");
            Require(s_Report.maxDirectionLength > 0.0001f, "Sky directions were never written.");
        }
        else
        {
            object array = Read(job, "renderingLayerMasks");
            s_Report.renderingLayerCount = RequireLength(array, "rendering layers");
            s_Report.renderingLayerSteps = s_StageSteps;
            for (int i = 0; i < s_Report.renderingLayerCount; i++)
            {
                uint value = (uint)Element(array, i);
                Require(value == 1 || value == 2 || value == 4 || value == 8 || value == 15, "Invalid rendering region mask " + value);
                if (value != 15) s_Report.assignedLayerProbes++;
            }
            Require(s_Report.assignedLayerProbes > 0, "No probe hit the GI geometry during layer tracing.");
        }
    }

    static int RequireLength(object array, string name)
    {
        Require((bool)Read(array, "IsCreated"), name + " NativeArray was not created.");
        int length = (int)Read(array, "Length");
        Require(length == s_Report.probes && length > 0, name + " length differs from probe count.");
        return length;
    }

    static void CheckShaders()
    {
        string prefix = "Packages/com.unity.render-pipelines.core/Editor/Lighting/APV/";
        foreach (string relative in new[] { "VirtualOffset/TraceVirtualOffset.urtshader", "DynamicGI/DynamicGISkyOcclusion.urtshader", "RenderingLayerMask/TraceRenderingLayerMask.urtshader" })
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
        try
        {
            // Provider owns the baker; its Dispose releases all three GPU jobs and tracing resources.
            if (s_BakingProvider != null) Invoke(s_BakingProvider, "DisposeActive");
            s_Report.resourcesDisposed = s_BakeData == null || Read(s_BakeData, "virtualOffsetJob") == null;
            Require(s_Report.resourcesDisposed, "Baker retained its auxiliary jobs after disposal.");
            if (s_Camera != null) s_Camera.targetTexture = null;
            if (s_Target != null) { s_Target.Release(); UnityEngine.Object.DestroyImmediate(s_Target); }
            Lightmapping.lightingSettings = s_PreviousLighting;
            GraphicsSettings.defaultRenderPipeline = s_PreviousGraphics;
            QualitySettings.renderPipeline = s_PreviousQuality;
        }
        catch (Exception cleanup) { error = error == null ? cleanup : new AggregateException(error, cleanup); }
        s_Report.nativeBakeObserved = s_NativeBakeObserved;
        s_Report.elapsedSeconds = EditorApplication.timeSinceStartup - s_Started;
        s_Report.passed = error == null && !s_NativeBakeObserved && Errors.Count == 0;
        s_Report.error = error?.ToString();
        s_Report.errors = Errors.ToArray();
        s_Report.checks = Checks.ToArray();
        Application.logMessageReceived -= OnLog;
        File.WriteAllText(ReportPath, JsonUtility.ToJson(s_Report, true));
        Debug.Log("APV_AUXILIARY_GPU_SMOKE " + (s_Report.passed ? "PASS" : "FAIL") + " probes=" + s_Report.probes + " report=" + Path.GetFullPath(ReportPath));
        if (error != null) Debug.LogException(error);
        if (Application.isBatchMode) EditorApplication.Exit(s_Report.passed ? 0 : 1);
    }

    static void OnLog(string message, string stack, LogType type)
    { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Errors.Add(message); }
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
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
    static object Element(object array, int index) => array.GetType().GetProperty("Item").GetValue(array, new object[] { index });
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

