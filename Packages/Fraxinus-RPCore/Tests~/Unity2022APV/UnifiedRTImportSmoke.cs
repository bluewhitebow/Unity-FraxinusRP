// Copy into an Editor assembly in a disposable Unity 2022 project.
// APV_IMPORT_SMOKE=1 Unity.exe -batchmode -force-d3d11 -projectPath ...
//     -executeMethod APVUnifiedRTImportSmoke.Run -logFile ...
// Requires a real graphics device; do not pass -nographics or -quit.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class APVUnifiedRTImportSmoke
{
    const string ReportPath = "apv-unified-rt-import-result.json";
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly List<string> Errors = new List<string>();
    static readonly List<string> ExpectedErrors = new List<string>();
    static readonly List<string> Checks = new List<string>();
    static readonly List<uint> Values = new List<uint>();
    static readonly Stack<IEnumerator> Routines = new Stack<IEnumerator>();
    static readonly MethodInfo GetMessages = typeof(ShaderUtil).GetMethod(
        "GetComputeShaderMessages", Flags, null, new[] { typeof(ComputeShader) }, null);
    static string s_Folder, s_ShaderPath, s_ShaderName, s_ExpectedMissing;
    static double s_Started;
    static bool s_Finished;
    static Report s_Report;

    [Serializable]
    sealed class Report
    {
        public bool passed;
        public string unityVersion, device, graphicsApi, generatedFolder, error;
        public double elapsedSeconds;
        public string[] checks, errors, expectedImportErrors;
        public uint[] gpuValues;
    }

    public static void Run()
    {
        if (!Application.isBatchMode || Environment.GetEnvironmentVariable("APV_IMPORT_SMOKE") != "1")
            throw new InvalidOperationException("Run import smoke only in a disposable batch project with APV_IMPORT_SMOKE=1.");

        s_Report = new Report { unityVersion = Application.unityVersion,
            device = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString() };
        s_Started = EditorApplication.timeSinceStartup;
        Application.logMessageReceived += OnLog;
        try
        {
            Require(SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback &&
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,
                "A compute-capable graphics device with GPU readback is required.");
            Require(GetMessages != null, "Compute shader compiler diagnostics are unavailable.");
            Require(!Lightmapping.isRunning, "Native lightmapping is already running.");
            s_Folder = "Assets/Generated/APVImportSmoke/" + Guid.NewGuid().ToString("N");
            s_ShaderName = "APVImportDependency_" + Path.GetFileName(s_Folder);
            s_ShaderPath = s_Folder + "/" + s_ShaderName + ".urtshader";
            s_Report.generatedFolder = s_Folder;
            Directory.CreateDirectory(s_Folder + "/Includes");
            Routines.Push(RunCases());
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    static IEnumerator RunCases()
    {
        string parentPath = s_Folder + "/Includes/Parent.hlsl";
        string childPath = s_Folder + "/Includes/NestedChild.hlsl";
        // This is the only write to the .urtshader. Subsequent repairs and edits
        // must be discovered from include dependencies, without ImportAsset.
        File.WriteAllText(s_ShaderPath,
            "#pragma only_renderers d3d11 vulkan metal\n" +
            "#define UNIFIED_RT_GROUP_SIZE_X 1\n#define UNIFIED_RT_GROUP_SIZE_Y 1\n" +
            "#include \"Packages/com.fraxinus.rendering.light-transport/Runtime/UnifiedRayTracing/Common.hlsl\"\n" +
            "#include \"" + parentPath + "\"\n" +
            "RWStructuredBuffer<uint> _ImportResult;\n" +
            "void RayGenExecute(UnifiedRT::DispatchInfo info) { _ImportResult[info.globalThreadIndex] = APV_IMPORT_VALUE; }\n");
        s_ExpectedMissing = "Parent.hlsl";
        Refresh();
        yield return ExpectMissingInclude(s_ExpectedMissing);
        Checks.Add("Import reports the initially missing parent include");

        s_ExpectedMissing = "NestedChild.hlsl";
        File.WriteAllText(parentPath, "#include \"NestedChild.hlsl\"\n");
        Refresh();
        yield return ExpectMissingInclude(s_ExpectedMissing);
        Checks.Add("Creating the parent automatically reimports the shader and discovers its missing relative child");

        s_ExpectedMissing = null;
        File.WriteAllText(childPath, "#define APV_IMPORT_VALUE 17u\n");
        Refresh();
        yield return WaitForValidShader();
        yield return CheckGPUValue(17);
        Checks.Add("Creating the nested include repairs the failed import using Refresh only; GPU writes 17");

        // Different length as well as contents prevents a coarse file timestamp
        // from hiding the update on an unusually fast or network filesystem.
        File.WriteAllText(childPath, "// Updated nested dependency\n#define APV_IMPORT_VALUE 93u\n");
        Refresh();
        yield return WaitForValidShader();
        yield return CheckGPUValue(93);
        Checks.Add("Editing only the nested include rebuilds GPU code; GPU writes 93");

        CheckCoreShader("VirtualOffset/TraceVirtualOffset.urtshader", false);
        CheckCoreShader("DynamicGI/DynamicGISkyOcclusion.urtshader", true);
        CheckCoreShader("RenderingLayerMask/TraceRenderingLayerMask.urtshader", false);
        CheckCoreShader("StandaloneAPV.urtshader", false);
        Require(Errors.Count == 0, string.Join("\n", Errors));
    }

    static void Refresh()
        => AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

    static IEnumerator ExpectMissingInclude(string filename)
    {
        double deadline = EditorApplication.timeSinceStartup + 30;
        while (true)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(s_ShaderPath);
            bool observed = ExpectedErrors.Exists(message => message.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (string message in ShaderErrors(shader))
            {
                if (message.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!ExpectedErrors.Contains(message)) ExpectedErrors.Add(message);
                    observed = true;
                }
            }
            if (observed)
            {
                // Let queued import diagnostics arrive while the expected
                // failure is still selected in the log filter.
                yield return null;
                yield break;
            }
            Require(EditorApplication.timeSinceStartup < deadline,
                "Import never reported the expected missing include: " + filename);
            yield return null;
        }
    }

    static IEnumerator WaitForValidShader()
    {
        double deadline = EditorApplication.timeSinceStartup + 30;
        while (true)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(s_ShaderPath);
            if (shader != null && ShaderErrors(shader).Count == 0 && shader.HasKernel("MainRayGenShader"))
            {
                Require(shader.IsSupported(shader.FindKernel("MainRayGenShader")), "Imported kernel is unsupported.");
                yield break;
            }
            Require(EditorApplication.timeSinceStartup < deadline,
                "Include changes did not repair the .urtshader import: " + string.Join("\n", ShaderErrors(shader)));
            yield return null;
        }
    }

    static IEnumerator CheckGPUValue(uint expected)
    {
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(s_ShaderPath);
        int kernel = shader.FindKernel("MainRayGenShader");
        using (var dimensions = new ComputeBuffer(3, sizeof(uint)))
        using (var output = new ComputeBuffer(1, sizeof(uint)))
        {
            dimensions.SetData(new uint[] { 1, 1, 1 });
            output.SetData(new uint[] { 0 });
            shader.SetBuffer(kernel, "g_Dimensions", dimensions);
            shader.SetBuffer(kernel, "_ImportResult", output);
            shader.Dispatch(kernel, 1, 1, 1);
            var request = AsyncGPUReadback.Request(output);
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!request.done)
            {
                Require(EditorApplication.timeSinceStartup < deadline, "GPU readback timed out.");
                yield return null;
            }
            Require(!request.hasError, "GPU readback failed.");
            uint actual = request.GetData<uint>()[0];
            Values.Add(actual);
            Require(actual == expected, "Shader used stale include contents: expected " + expected + ", received " + actual);
        }
    }

    static void CheckCoreShader(string relativePath, bool sky)
    {
        string path = "Packages/com.unity.render-pipelines.core/Editor/Lighting/APV/" + relativePath;
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        Require(shader != null, "Core compute shader did not import: " + path);
        foreach (string kernel in sky ? new[] { "MainRayGenShader", "ComputeIndirectDispatchDims", "EncodeShadingDirection" }
                                     : new[] { "MainRayGenShader", "ComputeIndirectDispatchDims" })
        {
            Require(shader.HasKernel(kernel), path + " is missing " + kernel);
            Require(shader.IsSupported(shader.FindKernel(kernel)), path + " has an unsupported kernel: " + kernel);
        }
        Require(ShaderErrors(shader).Count == 0, path + ": " + string.Join("\n", ShaderErrors(shader)));
        Checks.Add(relativePath + " has supported kernels and no compiler errors");
    }

    static List<string> ShaderErrors(ComputeShader shader)
    {
        var errors = new List<string>();
        if (shader == null) return errors;
        foreach (object message in (IEnumerable)GetMessages.Invoke(null, new object[] { shader }))
        {
            if (string.Equals(Read(message, "severity").ToString(), "Error", StringComparison.OrdinalIgnoreCase))
                errors.Add(Read(message, "message").ToString());
        }
        return errors;
    }

    static object Read(object target, string name)
    {
        var field = target.GetType().GetField(name, Flags);
        return field != null ? field.GetValue(target) : target.GetType().GetProperty(name, Flags).GetValue(target);
    }

    static void Tick()
    {
        try
        {
            Require(EditorApplication.timeSinceStartup - s_Started < 180, "Import regression timed out.");
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

    static void Finish(Exception error)
    {
        if (s_Finished) return;
        s_Finished = true;
        EditorApplication.update -= Tick;
        try { while (Routines.Count != 0) (Routines.Pop() as IDisposable)?.Dispose(); }
        catch (Exception cleanup) { error = error == null ? cleanup : new AggregateException(error, cleanup); }
        s_Report.elapsedSeconds = EditorApplication.timeSinceStartup - s_Started;
        s_Report.passed = error == null && Errors.Count == 0;
        s_Report.error = error?.ToString();
        s_Report.errors = Errors.ToArray();
        s_Report.expectedImportErrors = ExpectedErrors.ToArray();
        s_Report.checks = Checks.ToArray();
        s_Report.gpuValues = Values.ToArray();
        Application.logMessageReceived -= OnLog;
        File.WriteAllText(ReportPath, JsonUtility.ToJson(s_Report, true));
        Debug.Log("APV_IMPORT_SMOKE " + (s_Report.passed ? "PASS" : "FAIL") + " report=" + Path.GetFullPath(ReportPath));
        if (error != null) Debug.LogException(error);
        EditorApplication.Exit(s_Report.passed ? 0 : 1);
    }

    static void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        bool fixture = message.IndexOf(s_ShaderName ?? "APVImportDependency_", StringComparison.Ordinal) >= 0;
        bool expected = s_ExpectedMissing != null && fixture &&
            (message.IndexOf(s_ExpectedMissing, StringComparison.OrdinalIgnoreCase) >= 0 ||
             message.IndexOf("was not imported correctly", StringComparison.OrdinalIgnoreCase) >= 0);
        if (expected) ExpectedErrors.Add(message);
        else Errors.Add(message);
    }

    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
