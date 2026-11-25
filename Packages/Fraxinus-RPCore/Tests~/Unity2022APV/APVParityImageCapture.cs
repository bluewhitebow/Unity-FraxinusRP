// Temporary diagnostic. Copy into Assets/Editor in either project, then use
// -batchmode -executeMethod APVParityImageCapture.Run (no -quit / -nographics).
// In an interactive editor, write a label to Temp/APVParityImageCapture.request;
// the file is consumed on the next editor update without opening a scene.
// Optional: APV_CAPTURE_OUTPUT=absolute output directory, APV_CAPTURE_LABEL=name.
// No scene, pipeline asset, or baked lighting data is saved by this tool.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class APVParityImageCapture
{
    const int Width = 1920, Height = 1080;
    const string RequestPath = "Temp/APVParityImageCapture.request";
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    // Both saved Sponza scenes currently have this exact root camera pose.
    // Values taken from URP6 Assets/Scenes/Sponza.unity, MainCamera fileID 277919361.
    static readonly Vector3 Position = new Vector3(12.851f, 0.61f, -0.395f);
    static readonly Quaternion Rotation = new Quaternion(-0.09204021f, -0.6984036f, -0.09132655f, 0.7038612f);
    static Camera s_Camera;
    static GameObject s_CameraObject;
    static UniversalAdditionalCameraData s_Data;
    static UniversalRenderPipelineAsset s_Pipeline;
    static RenderTexture s_Target;
    static string s_Output, s_Label;
    static float s_PreviousRenderScale;
    static DebugLightingMode s_PreviousLighting;
    static DebugMaterialMode s_PreviousMaterial;
    static DebugLightingFeatureFlags s_PreviousFeatures;
    static DebugPostProcessingMode s_PreviousPost;
    static bool s_DebugSaved, s_Active;
    static double s_Started;
    static int s_Frames, s_StableFrames, s_LastLoaded = -1;
    static int s_EnabledProbeVolumes, s_BoundDebugLighting;
    static bool s_DebugKeyword;
    static string s_LastState;

    static APVParityImageCapture()
    {
        EditorApplication.update += PollRequest;
        AssemblyReloadEvents.beforeAssemblyReload += OnEditorTeardown;
        EditorApplication.quitting += OnEditorTeardown;
    }

    static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
    static string RequestFilePath => Path.Combine(ProjectRoot, RequestPath);

    public static void Run()
    {
        Start(Environment.GetEnvironmentVariable("APV_CAPTURE_LABEL") ?? "gi-nopost");
    }

    static void PollRequest()
    {
        if (s_Active || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            !File.Exists(RequestFilePath) || EditorApplication.timeSinceStartup < s_Started + 0.25)
            return;

        string label;
        try
        {
            label = File.ReadAllText(RequestFilePath).Trim();
            File.Delete(RequestFilePath);
        }
        catch (Exception exception)
        {
            Debug.LogError("APV_CAPTURE_REQUEST_FAIL " + exception);
            return;
        }

        Start(string.IsNullOrEmpty(label) ? "gi-nopost" : label);
    }

    static void Start(string requestedLabel)
    {
        if (s_Active) throw new InvalidOperationException("A parity capture is already running.");
        s_Output = Path.GetFullPath(Environment.GetEnvironmentVariable("APV_CAPTURE_OUTPUT") ?? Path.Combine(ProjectRoot, ".tmp/apv-images"));
        s_Label = requestedLabel;
        foreach (char invalid in Path.GetInvalidFileNameChars()) s_Label = s_Label.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(s_Label)) s_Label = "gi-nopost";
        Directory.CreateDirectory(s_Output);
        s_Active = true;
        s_Started = EditorApplication.timeSinceStartup;
        s_Frames = s_StableFrames = 0;
        s_LastLoaded = -1;
        s_LastState = null;
        s_EnabledProbeVolumes = s_BoundDebugLighting = 0;
        s_DebugKeyword = false;
        try
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                throw new InvalidOperationException("GI parity requires a Linear color-space project.");
            var activeScene = SceneManager.GetActiveScene();
            bool batch = Application.isBatchMode;
            if (!batch && !string.Equals(activeScene.name, "Sponza", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Interactive capture refused: active scene must be Sponza; no scene was opened.");
            if (batch)
                EditorSceneManager.OpenScene("Assets/Scenes/Sponza.unity", OpenSceneMode.Single);
            Camera sourceCamera = null;
            foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (camera.name == "MainCamera") { sourceCamera = camera; break; }
            if (sourceCamera == null) throw new InvalidOperationException("Sponza MainCamera was not found.");
            // A camera's native target buffers are not restored by JSON overwrite.
            // Own a separate camera so capture never changes the scene camera.
            s_CameraObject = new GameObject("APV GI parity camera") { hideFlags = HideFlags.HideAndDontSave };
            s_Camera = s_CameraObject.AddComponent<Camera>();
            s_Camera.CopyFrom(sourceCamera);
            s_Camera.enabled = false;
            s_Camera.targetTexture = null;
            s_Data = s_CameraObject.AddComponent<UniversalAdditionalCameraData>();
            var sourceData = sourceCamera.GetComponent<UniversalAdditionalCameraData>();
            if (sourceData != null) EditorUtility.CopySerialized(sourceData, s_Data);
            s_Pipeline = (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
            if (s_Pipeline == null) throw new InvalidOperationException("The active pipeline is not URP.");
            s_PreviousRenderScale = s_Pipeline.renderScale;
            s_Pipeline.renderScale = 1;
            s_Camera.transform.SetPositionAndRotation(Position, Rotation);
            s_Camera.usePhysicalProperties = false;
            s_Camera.orthographic = false;
            s_Camera.fieldOfView = 45;
            s_Camera.nearClipPlane = 0.3f;
            s_Camera.farClipPlane = 1000;
            s_Camera.rect = new Rect(0, 0, 1, 1);
            s_Camera.aspect = Width / (float)Height;
            s_Camera.ResetProjectionMatrix();
            s_Camera.ResetWorldToCameraMatrix();
            s_Camera.allowHDR = true;
            s_Camera.allowMSAA = false;
            s_Camera.allowDynamicResolution = false;
            s_Camera.cullingMask = -1;
            s_Data.renderPostProcessing = false;
            s_Data.antialiasing = AntialiasingMode.None;
            s_Data.dithering = false;
            s_Data.stopNaN = false;
            s_Data.allowXRRendering = false;
            s_Data.renderType = CameraRenderType.Base;
            s_Data.cameraStack?.Clear();
            s_Data.volumeLayerMask = 1;
            s_Data.volumeTrigger = s_Camera.transform;
            s_Target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { name = "APV GI parity linear HDR", antiAliasing = 1, useMipMap = false };
            if (!s_Target.Create()) throw new InvalidOperationException("Failed to allocate linear HDR render target.");
            s_Camera.targetTexture = s_Target;
            // Initialize URP/APV before setting debugger state and submitting requests.
            var previousTarget = RenderTexture.active;
            try { s_Camera.Render(); }
            finally { RenderTexture.active = previousTarget; }
            var debug = UniversalRenderPipelineDebugDisplaySettings.Instance;
            s_PreviousLighting = debug.lightingSettings.lightingDebugMode;
            s_PreviousFeatures = debug.lightingSettings.lightingFeatureFlags;
            s_PreviousMaterial = debug.materialSettings.materialDebugMode;
            s_PreviousPost = debug.renderingSettings.postProcessingDebugMode;
            s_DebugSaved = true;
            debug.materialSettings.materialDebugMode = DebugMaterialMode.None;
            debug.lightingSettings.lightingDebugMode = DebugLightingMode.GlobalIllumination;
            debug.lightingSettings.lightingFeatureFlags = DebugLightingFeatureFlags.None;
            debug.renderingSettings.postProcessingDebugMode = DebugPostProcessingMode.Disabled;
            RenderPipelineManager.endCameraRendering += EndCamera;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    static object Read(object target, string name)
    {
        if (target == null) return null;
        bool isType = target is Type;
        for (Type type = isType ? (Type)target : target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, Flags);
            if (field != null) return field.GetValue(isType ? null : target);
            var property = type.GetProperty(name, Flags);
            if (property != null) return property.GetValue(isType ? null : target);
        }
        return null;
    }

    static object RuntimeAPV()
    {
        var assembly = typeof(Volume).Assembly;
        var provider = assembly.GetType("UnityEngine.Rendering.ProbeReferenceVolumeProvider") ??
            assembly.GetType("UnityEngine.Rendering.ProbeReferenceVolume");
        return Read(provider, "instance");
    }

    static object Invoke(object target, string name)
    {
        if (target == null) return null;
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            foreach (var method in type.GetMethods(Flags))
                if (method.Name == name && method.GetParameters().Length == 0)
                    return method.Invoke(target, null);
        }
        return null;
    }

    static int Count(object collection)
    {
        if (collection == null) return -1;
        object count = Read(collection, "Count") ?? Read(collection, "size");
        return count == null ? -1 : Convert.ToInt32(count);
    }

    static void EndCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera != s_Camera) return;
        s_EnabledProbeVolumes = Shader.GetGlobalInt("_EnableProbeVolumes");
        s_BoundDebugLighting = Mathf.RoundToInt(Shader.GetGlobalFloat("_DebugLightingMode"));
        s_DebugKeyword = Shader.IsKeywordEnabled("DEBUG_DISPLAY");
    }

    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        try
        {
            if (EditorApplication.timeSinceStartup - s_Started > 180)
                throw new TimeoutException("APV capture did not become ready: " + s_LastState);
            var previousTarget = RenderTexture.active;
            try
            {
                RenderPipeline.SubmitRenderRequest(s_Camera, new UniversalRenderPipeline.SingleCameraRequest { destination = s_Target });
            }
            finally { RenderTexture.active = previousTarget; }
            s_Frames++;
            var apv = RuntimeAPV();
            int loaded = Count(Read(apv, "m_LoadedCells"));
            int queued = Count(Read(apv, "m_StreamingQueue"));
            int streaming = Count(Read(apv, "m_ActiveStreamingRequests"));
            bool initialized = Equals(Read(apv, "isInitialized"), true);
            bool enabled = Equals(Read(apv, "enabledBySRP"), true);
            object loadedState = Invoke(apv, "DataHasBeenLoaded");
            bool loadedReady = loaded > 0 || (loaded < 0 && Equals(loadedState, true));
            bool queuesReady = (queued < 0 || queued == 0) && (streaming < 0 || streaming == 0);
            bool ready = initialized && enabled && loadedReady && queuesReady;
            s_StableFrames = ready && loaded == s_LastLoaded ? s_StableFrames + 1 : 0;
            s_LastLoaded = loaded;
            s_LastState = "frame=" + s_Frames + " initialized=" + initialized + " enabledBySRP=" + enabled +
                " loaded=" + loaded + " queued=" + queued + " streaming=" + streaming +
                " _EnableProbeVolumes=" + s_EnabledProbeVolumes + " debugKeyword=" + s_DebugKeyword +
                " debugLighting=" + s_BoundDebugLighting;
            if (s_Frames % 30 == 0) Debug.Log("APV_CAPTURE_WAIT " + s_LastState);
            if (s_Frames < 32 || s_StableFrames < 8) return;
            if (s_EnabledProbeVolumes == 0)
                throw new InvalidOperationException("APV data loaded but _EnableProbeVolumes is zero during capture.");
            if (!s_DebugKeyword || s_BoundDebugLighting != (int)DebugLightingMode.GlobalIllumination)
                throw new InvalidOperationException("The requested GlobalIllumination debug view is not bound: " + s_LastState);
            WriteImages();
            File.WriteAllText(Path.Combine(s_Output, s_Label + ".txt"), Describe());
            Finish(null);
        }
        catch (Exception exception) { Finish(exception); }
    }

    static void WriteImages()
    {
        var previous = RenderTexture.active;
        Texture2D hdr = null, preview = null;
        try
        {
            RenderTexture.active = s_Target;
            hdr = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            hdr.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            hdr.Apply(false, false);
            Color[] linear = hdr.GetPixels();
            var rgb = new Color32[linear.Length];
            float maximum = 0;
            for (int i = 0; i < linear.Length; i++)
            {
                var value = linear[i];
                if (float.IsNaN(value.r) || float.IsInfinity(value.r) ||
                    float.IsNaN(value.g) || float.IsInfinity(value.g) ||
                    float.IsNaN(value.b) || float.IsInfinity(value.b))
                    throw new InvalidOperationException("APV capture contains a non-finite RGB value at pixel " + i + ".");
                maximum = Mathf.Max(maximum, Mathf.Max(value.r, Mathf.Max(value.g, value.b)));
                rgb[i] = new Color(Mathf.LinearToGammaSpace(Mathf.Max(0, value.r)),
                    Mathf.LinearToGammaSpace(Mathf.Max(0, value.g)), Mathf.LinearToGammaSpace(Mathf.Max(0, value.b)), 1);
            }
            if (maximum <= 1e-6f)
                throw new InvalidOperationException("Sponza GI capture is black despite the loaded APV state.");
            File.WriteAllBytes(Path.Combine(s_Output, s_Label + ".exr"),
                hdr.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
            preview = new Texture2D(Width, Height, TextureFormat.RGB24, false, true);
            preview.SetPixels32(rgb);
            preview.Apply(false, false);
            File.WriteAllBytes(Path.Combine(s_Output, s_Label + ".png"), preview.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous;
            if (hdr != null) UnityEngine.Object.DestroyImmediate(hdr);
            if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
        }
    }

    static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Unity=" + Application.unityVersion + " project=" + Application.dataPath);
        sb.AppendLine("Graphics=" + SystemInfo.graphicsDeviceType + " device=" + SystemInfo.graphicsDeviceName);
        sb.AppendLine("Image=" + Width + "x" + Height + " EXR=linear float32 PNG=sRGB preview clipped at 1, no tonemapping");
        sb.AppendLine("RenderTarget=" + s_Target.graphicsFormat + " sRGB=" + s_Target.sRGB);
        sb.AppendLine("Pipeline=" + AssetDatabase.GetAssetPath(s_Pipeline) + " renderScale=" + s_Pipeline.renderScale);
        sb.AppendLine("Camera=" + s_Camera.name + " position=" + s_Camera.transform.position.ToString("F8") +
            " rotation=" + s_Camera.transform.rotation.ToString("F8") + " fov=" + s_Camera.fieldOfView + " aspect=" + s_Camera.aspect);
        sb.AppendLine("Projection=" + s_Camera.projectionMatrix.ToString("F8"));
        sb.AppendLine("WorldToCamera=" + s_Camera.worldToCameraMatrix.ToString("F8"));
        sb.AppendLine("PostProcessing=" + s_Data.renderPostProcessing + " AA=" + s_Data.antialiasing +
            " volumeMask=" + s_Data.volumeLayerMask.value + " volumeTrigger=" + s_Data.volumeTrigger.name);
        sb.AppendLine("LightingDebug=" + UniversalRenderPipelineDebugDisplaySettings.Instance.lightingSettings.lightingDebugMode);
        sb.AppendLine(s_LastState);
        var apv = RuntimeAPV();
        sb.AppendLine("RuntimeAPV=" + apv?.GetType().FullName);
        foreach (string property in new[] { "shBands", "memoryBudget", "lightingScenario", "currentBakingSet", "forceNoDiskStreaming" })
            sb.AppendLine("APV." + property + "=" + Read(apv, property));
        sb.AppendLine("Ambient=" + RenderSettings.ambientMode + " intensity=" + RenderSettings.ambientIntensity +
            " sky=" + AssetDatabase.GetAssetPath(RenderSettings.skybox));
        foreach (var volume in UnityEngine.Object.FindObjectsOfType<Volume>())
        {
            sb.AppendLine("Volume=" + volume.name + " enabled=" + volume.enabled + " active=" + volume.gameObject.activeInHierarchy +
                " global=" + volume.isGlobal + " layer=" + volume.gameObject.layer + " priority=" + volume.priority +
                " weight=" + volume.weight + " blendDistance=" + volume.blendDistance + " profile=" + AssetDatabase.GetAssetPath(volume.sharedProfile));
            if (volume.sharedProfile != null)
                foreach (var component in volume.sharedProfile.components)
                    if (component != null && component.active) sb.AppendLine("ProfileComponent=" + component.GetType().FullName + " " + JsonUtility.ToJson(component));
        }
        var components = Read(VolumeManager.instance.stack, "components") as IDictionary;
        if (components != null)
            foreach (DictionaryEntry item in components)
                if (item.Value is VolumeComponent component && component.active)
                    sb.AppendLine("ResolvedVolumeComponent=" + component.GetType().FullName + " " + JsonUtility.ToJson(component));
        return sb.ToString();
    }

    static void OnEditorTeardown()
    {
        if (s_Active)
            Finish(new OperationCanceledException("Editor reloaded or closed during APV capture."), false);
    }

    static void Finish(Exception error, bool exitBatch = true)
    {
        EditorApplication.update -= Tick;
        RenderPipelineManager.endCameraRendering -= EndCamera;

        // Each cleanup step must run even if a previous step throws.
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception cleanup) { error = error == null ? cleanup : new AggregateException(error, cleanup); }
        }

        Cleanup(() =>
        {
            if (s_Camera != null) s_Camera.targetTexture = null;
        });
        Cleanup(() =>
        {
            if (s_Target != null && RenderTexture.active == s_Target) RenderTexture.active = null;
        });
        // Destroy the owner of any native Camera.SetTargetBuffers references
        // before releasing the texture those buffers refer to.
        Cleanup(() => { if (s_CameraObject != null) UnityEngine.Object.DestroyImmediate(s_CameraObject); });
        Cleanup(() => { if (s_Target != null) s_Target.Release(); });
        Cleanup(() => { if (s_Target != null) UnityEngine.Object.DestroyImmediate(s_Target); });
        Cleanup(() =>
        {
            if (s_DebugSaved)
            {
                var debug = UniversalRenderPipelineDebugDisplaySettings.Instance;
                debug.lightingSettings.lightingDebugMode = s_PreviousLighting;
                debug.lightingSettings.lightingFeatureFlags = s_PreviousFeatures;
                debug.materialSettings.materialDebugMode = s_PreviousMaterial;
                debug.renderingSettings.postProcessingDebugMode = s_PreviousPost;
            }
        });
        Cleanup(() => { if (s_Pipeline != null) s_Pipeline.renderScale = s_PreviousRenderScale; });
        s_Camera = null;
        s_CameraObject = null;
        s_Data = null;
        s_Target = null;
        s_Pipeline = null;
        s_Active = s_DebugSaved = false;
        if (error != null)
        {
            File.WriteAllText(Path.Combine(s_Output, s_Label + "-error.txt"), error + "\n" + s_LastState);
            Debug.LogError("APV_CAPTURE_FAIL " + error);
        }
        else Debug.Log("APV_CAPTURE_PASS " + Path.Combine(s_Output, s_Label) + " " + s_LastState);
        if (exitBatch && Application.isBatchMode) EditorApplication.Exit(error == null ? 0 : 1);
    }
}
