// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;

namespace Fraxinus.GPUBatcher.Editor
{
    internal class GPUBatcherRendererWindow : EditorWindow
    {
        private const string k_configExpandedKey = "GPUBatcherRenderer_ConfigExpanded";

        private Vector2 _scrollPos;
        private double _lastRepaintTime;
        private const double k_repaintInterval = 0.25; // 4x per second

        private SerializedObject _configSO;
        private string _versionLabel;
        private GPUBatcherConfig _cachedConfig;
        private bool _prototypesFoldout;

        private bool _meshMaterialFoldout;
        private bool _combinedMeshesFoldout;
        private bool _memoryBreakdownFoldout;
        private bool _fadeStatesFoldout;
        private bool _cameraBuffersFoldout;
        private bool _shadowBuffersFoldout;
        private readonly List<Fraxinus.GPUBatcher.GPUBatcherRenderer.ViewBufferEntry> _viewBufferEntries = new();
        private bool _drawRangesFoldout;
        private bool _occlusionDebugFoldout;
        private float _hzbPreviewSize = 256f;
        private Dictionary<int, bool> _perProtoFoldouts = new();
        private Dictionary<int, bool> _perProtoSigFoldouts = new();
        private Dictionary<string, bool> _rangeFoldouts = new();
        private static GUIStyle s_titleStyle;

        [MenuItem("Tools/Fraxinus GPU Batcher/System Window")]
        public static void ShowWindow()
        {
            var window = GetWindow<GPUBatcherRendererWindow>();
            window.titleContent = new GUIContent("Fraxinus GPU Batcher");
            window.minSize = new Vector2(300, 400);
        }

        private void OnEnable()
        {
            if (!GPUBatcherRenderer.canSubmitDraws) return;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GPUBatcherRendererWindow).Assembly);
            _versionLabel = package == null ? null : $"Version {package.version}";
            RefreshConfigSO();
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (GPUBatcherRenderer.hasInstance &&
                EditorApplication.timeSinceStartup - _lastRepaintTime > k_repaintInterval)
            {
                _lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void RefreshConfigSO()
        {
            var config = GPUBatcherConfig.instance;
            if (config != null && (config != _cachedConfig || _configSO == null))
            {
                _cachedConfig = config;
                _configSO = new SerializedObject(config);
            }
            else if (config == null)
            {
                _cachedConfig = null;
                _configSO = null;
            }
        }

        private void OnGUI()
        {
            if (!GPUBatcherRenderer.canSubmitDraws)
            {
                EditorGUILayout.HelpBox(Rendering.BRGIndirectSubmission.hasNativeApi
                    ? "The current graphics device must support compute shaders, GPU instancing, indirect arguments and raw BRG buffers."
                    : "This editor version does not provide the native BRG indirect draw API used by GPU Batcher.", MessageType.Info);
                return;
            }

            RefreshConfigSO();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // system status
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            s_titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("Fraxinus GPU Batcher", s_titleStyle);
            if (_versionLabel != null)
                EditorGUILayout.LabelField(_versionLabel, EditorStyles.miniLabel);
            DrawSystemStatus();
            GPUBatcherInspectorGUI.DrawRenderingDebuggerButton();
            EditorGUILayout.EndVertical();

#if UNITY_6000_0_OR_NEWER || TUANJIE_1_6_OR_NEWER
#if UNITY_6000_0_OR_NEWER
            bool keepBrgVariants = EditorGraphicsSettings.batchRendererGroupShaderStrippingMode == BatchRendererGroupStrippingMode.KeepAll;
#else
            bool keepBrgVariants = EditorGraphicsSettings.batchRendererGroupStrippingMode == BrgStrippingMode.KeepAll;
#endif
            if (!keepBrgVariants)
            {
                EditorGUILayout.HelpBox(
                    "BatchRendererGroup Variants is not set to \"Keep All\" in Project Settings > Graphics > Shader Stripping. " +
                    "GPU Batcher rendering will fail in builds.",
                    MessageType.Error);
            }
#endif

            EditorGUILayout.Space(4);

            if (_configSO != null && _cachedConfig != null)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                _configSO.Update();
                DrawConfigSection();
                _configSO.ApplyModifiedProperties();
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(4);

            // runtime stats
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawRuntimeStats();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndScrollView();
        }

        private void DrawSystemStatus()
        {
            EditorGUILayout.LabelField("System Tools", EditorStyles.boldLabel);

            bool hasInstance = GPUBatcherRenderer.hasInstance;

            EditorGUILayout.BeginHorizontal();
            if (!hasInstance)
            {
                if (GUILayout.Button("Create Instance"))
                {
                    // accessing Instance auto-creates it
                    var _ = GPUBatcherRenderer.instance;
                    Repaint();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                if (GUILayout.Button("Full Refresh"))
                {
                    GPUBatcherRenderer.instance.Reinitialize();
                    Repaint();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Runtime Refresh"))
                {
                    GPUBatcherRenderer.instance.RuntimeRefresh();
                    Repaint();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Rebuild Batches"))
                {
                    GPUBatcherRenderer.instance.MarkBatchesDirtyForFullRebuild();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawConfigSection()
        {
            GPUBatcherInspectorGUI.DrawSectionHeader("Configuration");

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Config Asset", _cachedConfig, typeof(GPUBatcherConfig), false);

            bool expanded = SessionState.GetBool(k_configExpandedKey, true);
            expanded = EditorGUILayout.Foldout(expanded, "Settings", true);
            SessionState.SetBool(k_configExpandedKey, expanded);
            if (!expanded)
                return;

            EditorGUI.indentLevel++;
            GPUBatcherConfigEditor.DrawConfigInspector(_configSO, _cachedConfig);
            EditorGUI.indentLevel--;
        }

        private void DrawRuntimeStats()
        {
            GPUBatcherInspectorGUI.DrawSectionHeader("Detailed Inspection");
            const int k_sectionSpacing = 8;

            float savedLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 240f;

            var system = GPUBatcherRenderer.hasInstance ? GPUBatcherRenderer.instance : null;
            if (system == null || !system.isInitialized)
            {
                EditorGUILayout.HelpBox("System not initialized.", MessageType.Info);
                EditorGUIUtility.labelWidth = savedLabelWidth;
                return;
            }

            EditorGUI.indentLevel++;

            // registered meshes & materials
            _meshMaterialFoldout = EditorGUILayout.Foldout(_meshMaterialFoldout,
                "Registered Asset References", true, EditorStyles.foldoutHeader);

            if (_meshMaterialFoldout)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.indentLevel++;

                var meshRefs = system.meshRefCounts;
                if (system.registeredMeshes != null)
                {
                    EditorGUILayout.LabelField("Meshes", EditorStyles.miniBoldLabel);
                    foreach (var kvp in system.registeredMeshes)
                    {
                        string meshName = kvp.Key != null ? kvp.Key.name : "(null)";
                        int rc = meshRefs != null && meshRefs.TryGetValue(kvp.Value, out var c) ? c : 0;
                        EditorGUILayout.LabelField($"  id={kvp.Value.value}  ref={rc}  {meshName}");
                    }
                }

                var matRefs = system.materialRefCounts;
                if (system.registeredMaterials != null)
                {
                    EditorGUILayout.LabelField("Materials", EditorStyles.miniBoldLabel);
                    foreach (var kvp in system.registeredMaterials)
                    {
                        string matName = kvp.Key != null ? kvp.Key.name : "(null)";
                        int rc = matRefs != null && matRefs.TryGetValue(kvp.Value, out var c) ? c : 0;
                        EditorGUILayout.LabelField($"  id={kvp.Value.value,-4} ref={rc,-4} {matName}");
                    }
                }

                EditorGUI.indentLevel--;
                EditorGUI.EndDisabledGroup();
            }

            // combined meshes
            var combineCache = system.meshCombineCache;
            int combinedCount = combineCache?.count ?? 0;
            if (combinedCount > 0)
            {
                _combinedMeshesFoldout = EditorGUILayout.Foldout(_combinedMeshesFoldout,
                    $"Combined Meshes: {combinedCount}", true, EditorStyles.foldoutHeader);

                if (_combinedMeshesFoldout)
                {
                    // instances rendered per combined mesh, summed over the protos that use it
                    var meshInstances = new Dictionary<Mesh, long>();
                    var seen = new HashSet<Mesh>();
                    for (int p = 0; p < system.prototypeCount; p++)
                    {
                        var psig = system.GetPrototype(p);
                        if (psig?.asset?.lodMeshes == null) continue;
                        long inst = system.GetPrototypeInstanceCount(p);
                        if (inst == 0) continue;
                        seen.Clear();
                        var lodMeshes = psig.asset.lodMeshes;
                        for (int l = 0; l < lodMeshes.Length; l++)
                        {
                            var m = lodMeshes[l];
                            if (m == null || !combineCache.IsCombinedMesh(m) || !seen.Add(m)) continue;
                            meshInstances.TryGetValue(m, out long cur);
                            meshInstances[m] = cur + inst;
                        }
                    }

                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUI.indentLevel++;
                    foreach (var (mesh, refCount) in combineCache.GetEntries())
                    {
                        string name = mesh != null ? mesh.name : "(null)";
                        int verts = mesh != null ? mesh.vertexCount : 0;
                        int submeshes = mesh != null ? mesh.subMeshCount : 0;
                        long inst = 0;
                        if (mesh != null) meshInstances.TryGetValue(mesh, out inst);
                        EditorGUILayout.LabelField($"  {name}", $"ref={refCount}, {inst:N0} inst, {verts} verts, {submeshes} submeshes");
                    }
                    EditorGUI.indentLevel--;
                    EditorGUI.EndDisabledGroup();
                }
            }

            int totalSlots = system.prototypeCount;
            int activeCount = system.activePrototypeCount;
            int vacantCount = totalSlots - activeCount;
            int idleCount = 0;
            for (int i = 0; i < totalSlots; i++)
            {
                if (!system.IsPrototypeRemoved(i) && system.GetPrototypeRefCount(i) == 0)
                    idleCount++;
            }
            // prototypes - asset identity
            string protoLabel = $"Prototypes: {activeCount}";
            if (vacantCount > 0) protoLabel += $" ({vacantCount} vacant)";

            EditorGUILayout.BeginHorizontal();
            _prototypesFoldout = EditorGUILayout.Foldout(_prototypesFoldout, protoLabel, true, EditorStyles.foldoutHeader);
            if (idleCount > 0 && GUILayout.Button(new GUIContent("Purge Now", "Force-purge all refcount-0 prototypes immediately, bypassing the grace period."), GUILayout.Width(100)))
            {
                int purged = system.PurgeUnusedPrototypes();
                Debug.Log($"GPU Batcher: Purged {purged} unused prototypes.");
                _perProtoFoldouts.Clear();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (_prototypesFoldout && totalSlots > 0)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < totalSlots; i++)
                {
                    var sig = system.GetPrototype(i);
                    if (sig == null) continue;

                    var asset = sig.asset;
                    string meshName = asset?.mesh != null ? asset.mesh.name : "(no mesh)";
                    string matName = asset?.materials != null && asset.materials.Length > 0 && asset.materials[0] != null
                        ? asset.materials[0].name : "(no material)";
                    int refCount = system.GetPrototypeRefCount(i);
                    int instances = system.GetPrototypeInstanceCount(i);
                    string status = refCount > 0 ? $"ref={refCount}" : "idle";
                    int lodCount = sig.lodCount;
                    bool hasCrossfade = sig.fadeMode != UnityEngine.LODFadeMode.None;
                    string foldLabel = $"[{i}] {meshName} | {matName}  ({status}, {instances:N0} inst, {lodCount} LODs{(hasCrossfade ? ", crossfade" : "")})";

                    _perProtoFoldouts.TryGetValue(i, out bool expanded);
                    expanded = EditorGUILayout.Foldout(expanded, foldLabel, true);
                    _perProtoFoldouts[i] = expanded;

                    if (expanded)
                    {
                        EditorGUI.indentLevel++;
                        var prevLabelWidth = EditorGUIUtility.labelWidth;
                        EditorGUIUtility.labelWidth = 180;
                        EditorGUILayout.LabelField("Ref Count", refCount > 0 ? refCount.ToString() : "0 (idle)");
                        EditorGUILayout.LabelField("Instances", $"{instances:N0}  (HWM {system.GetPrototypeInstanceCountHWM(i):N0})");
                        string fadeLabel = sig.fadeMode == UnityEngine.LODFadeMode.None
                            ? "None"
                            : (sig.animateCrossFading ? $"{sig.fadeMode} (animated)" : sig.fadeMode.ToString());
                        EditorGUILayout.LabelField("LODs", $"{lodCount}   Fade: {fadeLabel}");
                        EditorGUILayout.LabelField("Signature Key", $"0x{sig.GetSignatureKey():X16}");

                        // show mesh/material per LOD
                        EditorGUI.BeginDisabledGroup(true);
                        var lodScreenSizes = asset?.lodScreenSizes;
                        uint percentageFadeMask = asset?.percentageFadeMask ?? 0;
                        for (int lod = 0; lod < lodCount; lod++)
                        {
                            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                            EditorGUILayout.LabelField($"LOD {lod}", EditorStyles.boldLabel);

                            var lodMesh = asset?.GetLODMeshRaw(lod);
                            var lodMats = asset?.GetLODMaterialsRaw(lod);
                            int submeshCount = lodMats?.Length ?? 0;
                            int vertCount = lodMesh != null ? lodMesh.vertexCount : 0;
                            var lodShadowMode = (asset?.lodShadowCastingModes != null && lod < asset.lodShadowCastingModes.Length)
                                ? asset.lodShadowCastingModes[lod] : UnityEngine.Rendering.ShadowCastingMode.On;

                            bool isLastLod = (lod == lodCount - 1);
                            string kindLabel = isLastLod ? "cull" : "transition";
                            string transitionLabel;
                            if (lodScreenSizes != null && lod < lodScreenSizes.Length)
                            {
                                float ss = lodScreenSizes[lod];
                                bool isPercent = (percentageFadeMask & (1u << lod)) != 0;
                                transitionLabel = isPercent
                                    ? $"{kindLabel}<{ss * 100f:F1}% screen"
                                    : $"{kindLabel}<{ss:F4} screen";
                            }
                            else transitionLabel = $"{kindLabel}=n/a";

                            bool showFadeWidth = hasCrossfade && !sig.animateCrossFading;
                            string fadeLabelLod = showFadeWidth
                                ? $", {(isLastLod ? "cullFadeWidth" : "fadeWidth")}={sig.GetLODFadeWidth(lod):F2}"
                                : string.Empty;

                            EditorGUILayout.ObjectField("Mesh", lodMesh, typeof(Mesh), false);
                            EditorGUILayout.LabelField(" ", $"{transitionLabel}{fadeLabelLod}");
                            EditorGUILayout.LabelField(" ", $"{vertCount} verts, {submeshCount} submeshes, shadow={lodShadowMode}");
                            if (lodMats != null)
                            {
                                for (int m = 0; m < lodMats.Length; m++)
                                    EditorGUILayout.ObjectField($"  Material [{m}]", lodMats[m], typeof(Material), false);
                            }

                            EditorGUILayout.EndVertical();
                        }
                        EditorGUI.EndDisabledGroup();

                        // render signature
                        _perProtoSigFoldouts.TryGetValue(i, out bool sigExpanded);
                        sigExpanded = EditorGUILayout.Foldout(sigExpanded, "Render Signature", true);
                        _perProtoSigFoldouts[i] = sigExpanded;

                        if (sigExpanded)
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.LabelField("Motion Vectors", sig.motionVectorMode.ToString());
                            EditorGUILayout.LabelField("Shadow",
                                $"maxCascade={sig.maxShadowCascade} | minShadowLOD={sig.minShadowLOD} | maxDist={sig.maxShadowDistance}");
                            EditorGUILayout.LabelField("Animated Crossfade", sig.animateCrossFading.ToString());
                            EditorGUILayout.LabelField("LOD Fade Duration",
                                sig.isDefaultCrossfadeDuration
                                    ? $"default ({sig.lodFadeDuration:F2}s)"
                                    : $"{sig.lodFadeDuration:F2}s");
                            EditorGUILayout.LabelField("Prototype Group", sig.prototypeGroup != null
                                ? $"{sig.prototypeGroup.name} (bias={sig.prototypeGroup.lodBias:F2}, density={sig.prototypeGroup.density:F2})"
                                : "None");
                            if (!sig.enableDistanceDensity)
                            {
                                EditorGUILayout.LabelField("Distance Density", "None");
                            }
                            else if (sig.distanceDensityMode == DistanceDensityMode.ScreenSize)
                            {
                                EditorGUILayout.LabelField("Distance Density",
                                    $"ScreenSize  start={sig.densityStartScreenSize:F3}  min={sig.densityMinFraction:F2}  fadeWidth={sig.densityFadeWidth:F3}");
                            }
                            else
                            {
                                EditorGUILayout.LabelField("Distance Density",
                                    $"RawDistance  start={sig.densityStartDistance:F1}m  end={sig.densityEndDistance:F1}m  min={sig.densityMinFraction:F2}  fadeWidth={sig.densityFadeWidth:F3}");
                            }
                            EditorGUILayout.LabelField("Layer", $"{sig.layer}  renderLayerMask=0x{sig.renderingLayerMask:X8}");
                            if (sig.overrideMask != 0)
                                EditorGUILayout.LabelField("Overrides", sig.overrideMask.ToString());

                            // bucket caps
                            int hwm = system.GetPrototypeInstanceCountHWM(i);
                            int slotsPerBucket = System.Math.Max(hwm, 64);
                            int fadeMultiplier = hasCrossfade ? 2 : 1;
                            int cameraBuckets = lodCount * fadeMultiplier;
                            int shadowBuckets = lodCount * sig.maxShadowCascade * fadeMultiplier;
                            EditorGUILayout.LabelField("Camera Bucket Cap", $"{slotsPerBucket * cameraBuckets:N0}");
                            EditorGUILayout.LabelField("", $"{slotsPerBucket:N0}/bucket x {lodCount} LODs x {fadeMultiplier} {(hasCrossfade ? "(crossfade)" : "(no crossfade)")}", EditorStyles.miniLabel);
                            EditorGUILayout.LabelField("Shadow Bucket Cap", $"{slotsPerBucket * shadowBuckets:N0}");
                            EditorGUILayout.LabelField("", $"{slotsPerBucket:N0}/bucket x {lodCount} LODs x {sig.maxShadowCascade} cascades x {fadeMultiplier}", EditorStyles.miniLabel);
                            EditorGUI.indentLevel--;
                        }

                        EditorGUIUtility.labelWidth = prevLabelWidth;
                        EditorGUI.indentLevel--;
                    }
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(k_sectionSpacing);

            // draw ranges debug
            _drawRangesFoldout = EditorGUILayout.Foldout(_drawRangesFoldout, "Draw Ranges (Debug)", true);
            if (_drawRangesFoldout)
            {
                EditorGUI.indentLevel++;
                int camRanges = system.debugCameraDrawRangeCount;
                EditorGUILayout.LabelField("Camera Ranges", camRanges.ToString());
                for (int r = 0; r < camRanges; r++)
                    DrawRangeWithCommands(system, false, r, $"cam_{r}");
                int shadowRanges = system.debugShadowDrawRangeCount;
                EditorGUILayout.LabelField("Shadow Ranges", shadowRanges.ToString());
                for (int r = 0; r < shadowRanges; r++)
                    DrawRangeWithCommands(system, true, r, $"shd_{r}");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(k_sectionSpacing);
            EditorGUILayout.LabelField("Per-View GPU Readback", EditorStyles.boldLabel);
            system.computeRenderedTriangles = EditorGUILayout.Toggle(
                new GUIContent("Compute Per-View Stats", "Read the final indirect arguments from the GPU. Compare the same Game camera with GPU Occlusion Culling on and off."),
                system.computeRenderedTriangles);
            EditorGUI.BeginDisabledGroup(true);
            if (system.computeRenderedTriangles)
            {
                long registered = system.statInstances;
                long usedChunks = system.statChunksUsed;
                long totalCamTris = 0, totalCamVerts = 0, totalCamInst = 0;
                long totalShdTris = 0, totalShdVerts = 0, totalShdInst = 0;
                foreach (var kvp in system.perCameraMeshStatsMap)
                {
                    var s = kvp.Value;
                    totalCamTris += s.cameraTris; totalCamVerts += s.cameraVerts; totalCamInst += s.cameraInstances;

#if UNITY_6000_3_OR_NEWER
                    var obj = EditorUtility.EntityIdToObject(kvp.Key);
#else
                    var obj = EditorUtility.InstanceIDToObject(kvp.Key);
#endif
                    string camName = obj != null ? obj.name : $"Camera {kvp.Key}";
                    EditorGUILayout.LabelField($"{camName} (cam)",
                        $"{s.cameraInstances:N0} inst ({System.Math.Max(0, registered - s.cameraInstances):N0} culled), {s.cameraGpuChunks:N0} chunks ({System.Math.Max(0, usedChunks - s.cameraGpuChunks):N0} culled), {s.cameraTris:N0} tris, {s.cameraVerts:N0} verts");
                }
                foreach (var kvp in system.perLightShadowStatsMap)
                {
                    var s = kvp.Value;
                    totalShdTris += s.tris; totalShdVerts += s.verts; totalShdInst += s.instances;

#if UNITY_6000_3_OR_NEWER
                    var lightObj = EditorUtility.EntityIdToObject(kvp.Key);
#else
                    var lightObj = EditorUtility.InstanceIDToObject(kvp.Key);
#endif
                    string lightName = lightObj != null ? lightObj.name : $"Light {kvp.Key}";
                    EditorGUILayout.LabelField($"{lightName} (shadow)",
                        $"{s.instances:N0} inst ({System.Math.Max(0, registered - s.instances):N0} culled), {s.gpuChunks:N0} chunks ({System.Math.Max(0, usedChunks - s.gpuChunks):N0} culled), {s.tris:N0} tris, {s.verts:N0} verts");
                    if (s.cascadeInstances != null)
                    {
                        int last = s.cascadeInstances.Length - 1;
                        while (last > 0 && s.cascadeInstances[last] == 0) last--;
                        var sb = new System.Text.StringBuilder(64);
                        for (int c = 0; c <= last; c++)
                        {
                            if (c > 0) sb.Append("   ");
                            sb.Append('c').Append(c).Append(' ').Append(s.cascadeInstances[c].ToString("N0"));
                        }
                        EditorGUILayout.LabelField("    cascades", sb.ToString());
                    }
                }
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Total Instances", $"{totalCamInst:N0} cam, {totalShdInst:N0} shadow");
                EditorGUILayout.LabelField("Total Triangles", $"{totalCamTris:N0} cam, {totalShdTris:N0} shadow");
                EditorGUILayout.LabelField("Total Vertices", $"{totalCamVerts:N0} cam, {totalShdVerts:N0} shadow");
            }

            if (system.chunkSystemInitialized)
            {
                // per-view pools
                EditorGUILayout.Space(k_sectionSpacing);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.LabelField("Per-View Buffers", EditorStyles.boldLabel);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Camera Sets",
                    $"{system.statCameraPoolActive} / {system.statCameraPoolTotal}");
                EditorGUILayout.LabelField("Shadow Sets",
                    $"{system.statShadowPoolActive} / {system.statShadowPoolTotal}");
                EditorGUILayout.LabelField("Fade Views", system.statFadeStateViews.ToString());
            }
            else
            {
                EditorGUILayout.LabelField("Chunk System", "Not initialized");
            }

            // occlusion debug
            if (_cachedConfig != null && _cachedConfig.useGPUOcclusionCulling)
            {
                EditorGUILayout.Space(k_sectionSpacing);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.LabelField("Occlusion Debug", EditorStyles.boldLabel);
                _occlusionDebugFoldout = EditorGUILayout.Foldout(_occlusionDebugFoldout,
                    "HZB Atlases", true, EditorStyles.foldoutHeader);
                EditorGUI.BeginDisabledGroup(true);

                if (_occlusionDebugFoldout)
                {
                    EditorGUI.EndDisabledGroup();
                    _hzbPreviewSize = EditorGUILayout.Slider("Preview Size", _hzbPreviewSize, 128f, 512f);

                    var cams = GPUBatcherDebugCameras.renderedGameCameras;
                    if (cams.Count > 0)
                    {
                        for (int ci = 0; ci < cams.Count; ci++)
                        {
                            var cam = cams[ci];
                            if (cam == null) continue;

                            var atlas = system.GetCameraHZBAtlas(cam);
                            EditorGUILayout.LabelField($"{cam.name} (ID: {GPUBatcherHelpers.EntityIDString(cam)})",
                                atlas != null ? $"{atlas.width}x{atlas.height}" : "no atlas",
                                EditorStyles.miniBoldLabel);

                            if (atlas != null)
                            {
                                float aspect = (float)atlas.width / atlas.height;
                                float w = _hzbPreviewSize * aspect;
                                float h = _hzbPreviewSize;
                                var rect = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
                                EditorGUI.DrawPreviewTexture(rect, atlas);
                            }
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("No active game cameras.");
                    }
                    EditorGUI.BeginDisabledGroup(true);
                }
            }

            // crossfade info
            EditorGUILayout.Space(k_sectionSpacing);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField("Crossfade", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);

            if (system.statAnyAnimatedCrossfade)
            {
                string animInfo = $"ON ({system.statAnimatedProtoCount} protos, {system.statFadeStateViews} views, {system.statTotalFadeStates} states, {system.statLodFadeDuration}s)";
                if (system.statSkipIntermediateLODs)
                    animInfo += " [Skip LODs]";
                EditorGUILayout.LabelField("Animated Crossfade", animInfo);
            }
            else
            {
                EditorGUILayout.LabelField("Animated Crossfade", "OFF");
            }

            EditorGUILayout.Space(k_sectionSpacing);

            // memory
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField("GPU Memory", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);

            var breakdown = system.GetMemoryBreakdown();
            long memoryBytes = breakdown.total;
            string memoryStr = GPUBatcherConfig.FormatBytes(memoryBytes);

            var memColor = GUI.color;
            if (memoryBytes > 256 * 1024 * 1024) GUI.color = Color.red;
            else if (memoryBytes > 128 * 1024 * 1024) GUI.color = Color.yellow;
            else if (memoryBytes > 64 * 1024 * 1024) GUI.color = new Color(1f, 0.6f, 0f);
            else GUI.color = Color.green;

            _memoryBreakdownFoldout = EditorGUILayout.Foldout(_memoryBreakdownFoldout, $"Allocated: {memoryStr}", true);
            GUI.color = memColor;

            if (_memoryBreakdownFoldout && memoryBytes > 0)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Chunk High-Water Mark", system.statChunkHighWaterMark.ToString());
                EditorGUILayout.LabelField("Instance Slot Capacity", (system.poolCapacity * system.statPoolSize).ToString("N0"));
                EditorGUILayout.LabelField("Unused Instance Slots", system.statWastedSlots.ToString("N0"));
                EditorGUILayout.LabelField("Prototype Data", GPUBatcherConfig.FormatBytes(breakdown.prototypeData));
                EditorGUILayout.LabelField("Instance Data", GPUBatcherConfig.FormatBytes(breakdown.instanceData));
                EditorGUILayout.LabelField("Matrix Data", GPUBatcherConfig.FormatBytes(breakdown.matrixData));
                EditorGUILayout.LabelField("Chunk Data", GPUBatcherConfig.FormatBytes(breakdown.chunkData));
                EditorGUILayout.LabelField("Pool Ref Data", GPUBatcherConfig.FormatBytes(breakdown.poolRefData));
                DrawViewBufferFoldout(system, ref _cameraBuffersFoldout, "Camera View Buffers", breakdown.cameraViewBuffers, ViewBufferKind.Camera);
                DrawViewBufferFoldout(system, ref _shadowBuffersFoldout, "Shadow View Buffers", breakdown.shadowViewBuffers, ViewBufferKind.Shadow);
                DrawViewBufferFoldout(system, ref _fadeStatesFoldout, "FadeState Buffers", breakdown.fadeStateBuffers, ViewBufferKind.FadeState);
                EditorGUILayout.LabelField("Bucket Mapping", GPUBatcherConfig.FormatBytes(breakdown.bucketMapping));
                EditorGUILayout.LabelField("Staging Buffers", GPUBatcherConfig.FormatBytes(breakdown.stagingBuffers));
                EditorGUI.indentLevel--;
            }

            EditorGUI.EndDisabledGroup();
            EditorGUI.indentLevel--;

            EditorGUIUtility.labelWidth = savedLabelWidth;
        }

        private enum ViewBufferKind { Camera, Shadow, FadeState }

        private void DrawViewBufferFoldout(Fraxinus.GPUBatcher.GPUBatcherRenderer system, ref bool foldout, string label, long totalBytes, ViewBufferKind kind)
        {
            foldout = EditorGUILayout.Foldout(foldout, $"{label}  {GPUBatcherConfig.FormatBytes(totalBytes)}", true);
            if (!foldout) return;

            switch (kind)
            {
                case ViewBufferKind.Camera: system.GetCameraBufferEntries(_viewBufferEntries); break;
                case ViewBufferKind.Shadow: system.GetShadowBufferEntries(_viewBufferEntries); break;
                case ViewBufferKind.FadeState: system.GetFadeStateEntries(_viewBufferEntries); break;
            }

            if (_viewBufferEntries.Count == 0)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("(none)");
                EditorGUI.indentLevel--;
                return;
            }

            EditorGUI.indentLevel++;
            foreach (var entry in _viewBufferEntries)
                EditorGUILayout.LabelField(entry.label, GPUBatcherConfig.FormatBytes(entry.bytes));
            EditorGUI.indentLevel--;
        }

        private void DrawRangeWithCommands(Fraxinus.GPUBatcher.GPUBatcherRenderer system, bool isShadow, int rangeIndex, string key)
        {
            _rangeFoldouts.TryGetValue(key, out bool expanded);
            expanded = EditorGUILayout.Foldout(expanded,
                $"Range [{rangeIndex}]  {system.DebugDescribeDrawRange(isShadow, rangeIndex)}", true);
            _rangeFoldouts[key] = expanded;
            if (expanded)
            {
                EditorGUI.indentLevel++;
                system.DebugGetDrawRangeSpan(isShadow, rangeIndex, out int begin, out int count);
                for (int c = begin; c < begin + count; c++)
                    EditorGUILayout.LabelField($"  cmd[{c}]", system.DebugDescribeDrawCommand(isShadow, c), EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
        }
    }
}
