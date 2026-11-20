// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;

namespace Fraxinus.GPUBatcher.Editor
{
    internal class GPUBatcherRendererWindow : EditorWindow
    {
        private const string ConfigExpandedKey = "GPUBatcherRenderer_ConfigExpanded";

        private Vector2 _scrollPos;
        private double _lastRepaintTime;
        private const double RepaintInterval = 0.25; // 4x per second

        private SerializedObject _configSO;
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
        private static GUIStyle _titleStyle;

        [MenuItem("Tools/Fraxinus GPU Batcher/System Window")]
        public static void ShowWindow()
        {
            var window = GetWindow<GPUBatcherRendererWindow>();
            window.titleContent = new GUIContent("Fraxinus GPU Batcher");
            window.minSize = new Vector2(300, 400);
        }

        private void OnEnable()
        {
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
            if (GPUBatcherRenderer.HasInstance &&
                EditorApplication.timeSinceStartup - _lastRepaintTime > RepaintInterval)
            {
                _lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void RefreshConfigSO()
        {
            var config = GPUBatcherConfig.Instance;
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
            RefreshConfigSO();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // system status
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("Fraxinus GPU Batcher", _titleStyle);
            EditorGUILayout.LabelField($"Version {GPUBatcherVersion.Version}", EditorStyles.miniLabel);
            DrawSystemStatus();
            EditorGUILayout.EndVertical();

#if UNITY_6000_0_OR_NEWER
            if (EditorGraphicsSettings.batchRendererGroupShaderStrippingMode != BatchRendererGroupStrippingMode.KeepAll)
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
            EditorGUILayout.LabelField("System Status", EditorStyles.boldLabel);

            bool hasInstance = GPUBatcherRenderer.HasInstance;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Instance:", GUILayout.Width(80));

            var prevColor = GUI.color;
            GUI.color = hasInstance ? Color.green : Color.gray;
            EditorGUILayout.LabelField(hasInstance ? "Active" : "Not Created");
            GUI.color = prevColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (!hasInstance)
            {
                if (GUILayout.Button("Create Instance"))
                {
                    // accessing Instance auto-creates it
                    var _ = GPUBatcherRenderer.Instance;
                    Repaint();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                if (GUILayout.Button("Full Refresh"))
                {
                    GPUBatcherRenderer.Instance.Reinitialize();
                    Repaint();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Runtime Refresh"))
                {
                    GPUBatcherRenderer.Instance.RuntimeRefresh();
                    Repaint();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Rebuild Batches"))
                {
                    GPUBatcherRenderer.Instance.MarkBatchesDirtyForFullRebuild();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawConfigSection()
        {
            GPUBatcherInspectorGUI.DrawSectionHeader("Configuration");

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Config Asset", _cachedConfig, typeof(GPUBatcherConfig), false);

            bool expanded = SessionState.GetBool(ConfigExpandedKey, true);
            expanded = EditorGUILayout.Foldout(expanded, "Settings", true);
            SessionState.SetBool(ConfigExpandedKey, expanded);
            if (!expanded)
                return;

            EditorGUI.indentLevel++;
            GPUBatcherConfigEditor.DrawConfigInspector(_configSO, _cachedConfig);
            EditorGUI.indentLevel--;
        }

        private void DrawRuntimeStats()
        {
            GPUBatcherInspectorGUI.DrawSectionHeader("Runtime Statistics");
            const int sectionSpacing = 8;

            float savedLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 240f;

            if (!GPUBatcherRenderer.HasInstance)
            {
                EditorGUILayout.HelpBox("System not initialized.", MessageType.Info);
                EditorGUIUtility.labelWidth = savedLabelWidth;
                return;
            }

            var system = GPUBatcherRenderer.Instance;

            EditorGUI.indentLevel++;

            // registered meshes & materials
            int meshCount = system.StatRegisteredMeshes;
            int matCount = system.StatRegisteredMaterials;
            _meshMaterialFoldout = EditorGUILayout.Foldout(_meshMaterialFoldout,
                $"Registered Assets: {meshCount} meshes, {matCount} materials", true, EditorStyles.foldoutHeader);

            if (_meshMaterialFoldout)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.indentLevel++;

                var meshRefs = system.MeshRefCounts;
                if (system.RegisteredMeshes != null)
                {
                    EditorGUILayout.LabelField("Meshes", EditorStyles.miniBoldLabel);
                    foreach (var kvp in system.RegisteredMeshes)
                    {
                        string meshName = kvp.Key != null ? kvp.Key.name : "(null)";
                        int rc = meshRefs != null && meshRefs.TryGetValue(kvp.Value, out var c) ? c : 0;
                        EditorGUILayout.LabelField($"  id={kvp.Value.value}  ref={rc}  {meshName}");
                    }
                }

                var matRefs = system.MaterialRefCounts;
                if (system.RegisteredMaterials != null)
                {
                    EditorGUILayout.LabelField("Materials", EditorStyles.miniBoldLabel);
                    foreach (var kvp in system.RegisteredMaterials)
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
            var combineCache = system.MeshCombineCache;
            int combinedCount = combineCache?.Count ?? 0;
            if (combinedCount > 0)
            {
                _combinedMeshesFoldout = EditorGUILayout.Foldout(_combinedMeshesFoldout,
                    $"Combined Meshes: {combinedCount}", true, EditorStyles.foldoutHeader);

                if (_combinedMeshesFoldout)
                {
                    // instances rendered per combined mesh, summed over the protos that use it
                    var meshInstances = new Dictionary<Mesh, long>();
                    var seen = new HashSet<Mesh>();
                    for (int p = 0; p < system.PrototypeCount; p++)
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

            int totalSlots = system.PrototypeCount;
            int activeCount = system.ActivePrototypeCount;
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
                    int lodCount = sig.LODCount;
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

            // draw commands
            EditorGUILayout.Space(sectionSpacing);
            EditorGUILayout.LabelField("Draw Commands", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.LabelField("Batches", system.StatBatchCount.ToString());
            EditorGUILayout.LabelField("Camera", system.StatCameraDrawCommands.ToString());
            EditorGUILayout.LabelField("Shadow", system.StatShadowDrawCommands.ToString());
            EditorGUILayout.LabelField("Camera Buckets", system.StatCameraBuckets.ToString());
            EditorGUILayout.LabelField("Shadow Buckets", system.StatShadowBuckets.ToString());

            EditorGUI.EndDisabledGroup();

            // draw ranges debug
            //EditorGUILayout.Space(3);
            _drawRangesFoldout = EditorGUILayout.Foldout(_drawRangesFoldout, "Draw Ranges (Debug)", true);
            if (_drawRangesFoldout)
            {
                EditorGUI.indentLevel++;
                int camRanges = system.DebugCameraDrawRangeCount;
                EditorGUILayout.LabelField("Camera Ranges", camRanges.ToString());
                for (int r = 0; r < camRanges; r++)
                    DrawRangeWithCommands(system, false, r, $"cam_{r}");
                int shadowRanges = system.DebugShadowDrawRangeCount;
                EditorGUILayout.LabelField("Shadow Ranges", shadowRanges.ToString());
                for (int r = 0; r < shadowRanges; r++)
                    DrawRangeWithCommands(system, true, r, $"shd_{r}");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(sectionSpacing);
            EditorGUILayout.LabelField("Instance and Culling Stats", EditorStyles.boldLabel);
            system.ComputeRenderedTriangles = EditorGUILayout.Toggle("Compute Per-View Stats", system.ComputeRenderedTriangles);
            EditorGUI.BeginDisabledGroup(true);
            if (system.ComputeRenderedTriangles)
            {
                long registered = system.StatInstances;
                long usedChunks = system.StatChunksUsed;
                long totalCamTris = 0, totalCamVerts = 0, totalCamInst = 0;
                long totalShdTris = 0, totalShdVerts = 0, totalShdInst = 0;
                foreach (var kvp in system.PerCameraMeshStatsMap)
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
                foreach (var kvp in system.PerLightShadowStatsMap)
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

            if (system.ChunkSystemInitialized)
            {
                EditorGUILayout.Space(sectionSpacing);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.LabelField("Chunk System", EditorStyles.boldLabel);
                EditorGUI.BeginDisabledGroup(true);

                EditorGUILayout.LabelField("Instances", system.StatInstances.ToString("N0"));

                int chunkCapacity = system.StatChunksUsed + system.StatChunksFree;
                string chunkInfo = $"{system.StatChunksUsed} / {chunkCapacity} total ({system.StatChunksFree} free) (HWM: {system.StatChunkHighWaterMark})";
                EditorGUILayout.LabelField("Chunks", chunkInfo);

                EditorGUILayout.LabelField("Pools",
                    $"{system.StatPoolsUsed} / {system.PoolCapacity} total ({system.StatPoolsFree} free, {system.StatPoolSize}/pool)");

                // color code utilization
                float util = system.StatUtilization;
                int used = system.StatInstances;
                int wasted = system.StatWastedSlots;
                var prevColor = GUI.color;
                if (util < 50f) GUI.color = Color.red;
                else if (util < 70f) GUI.color = Color.yellow;
                else if (util < 85f) GUI.color = new Color(0.5f, 1f, 0.5f);
                else GUI.color = Color.green;
                EditorGUILayout.LabelField("Pool Utilization", $"{util:F1}% ({used} used, {wasted} wasted)");
                GUI.color = prevColor;

                // occlusion
                int occTotal = system.StatOcclusionPass + system.StatOcclusionFail;
                if (occTotal > 0)
                {
                    //EditorGUILayout.Space(3);
                    EditorGUILayout.LabelField("Chunk Culling", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField("Visible", system.StatOcclusionPass.ToString());

                    prevColor = GUI.color;
                    GUI.color = system.StatOcclusionFail > 0 ? Color.green : Color.white;
                    EditorGUILayout.LabelField("Occluded", system.StatOcclusionFail.ToString());
                    GUI.color = prevColor;
                }

                // per-view pools
                EditorGUILayout.Space(sectionSpacing);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.LabelField("Per-View Buffers", EditorStyles.boldLabel);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Camera Sets",
                    $"{system.StatCameraPoolActive} / {system.StatCameraPoolTotal}");
                EditorGUILayout.LabelField("Shadow Sets",
                    $"{system.StatShadowPoolActive} / {system.StatShadowPoolTotal}");
                EditorGUILayout.LabelField("Fade Views", system.StatFadeStateViews.ToString());

                // active game cameras
                var activeCams = system.ActiveGameCameras;
                if (activeCams != null && activeCams.Count > 0)
                {
                    EditorGUILayout.Space(3);
                    EditorGUILayout.LabelField("Active Game Cameras", EditorStyles.miniBoldLabel);
                    EditorGUI.EndDisabledGroup();

                    var refCam = system.FreezeCullingReferenceCamera;
                    for (int ci = 0; ci < activeCams.Count; ci++)
                    {
                        var cam = activeCams[ci];
                        if (cam == null) continue;
                        bool isRef = (refCam != null && cam == refCam) ||
                                     (refCam == null && cam == Camera.main);
                        string label = $"{cam.name}  depth={cam.depth}";
                        if (isRef) label += "  [REF]";

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(label);
                        if (!isRef && GUILayout.Button("Set as Ref", GUILayout.Width(80)))
                        {
                            system.FreezeCullingReferenceCamera = cam;
                        }
                        if (isRef && refCam != null && GUILayout.Button("Clear Ref", GUILayout.Width(80)))
                        {
                            system.FreezeCullingReferenceCamera = null;
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUI.BeginDisabledGroup(true);
                }
            }
            else
            {
                EditorGUILayout.LabelField("Chunk System", "Not initialized");
            }

            // occlusion debug
            if (_cachedConfig != null && _cachedConfig.UseGPUOcclusionCulling)
            {
                EditorGUILayout.Space(sectionSpacing);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.LabelField("Occlusion Debug", EditorStyles.boldLabel);
                _occlusionDebugFoldout = EditorGUILayout.Foldout(_occlusionDebugFoldout,
                    "HZB Atlases", true, EditorStyles.foldoutHeader);
                EditorGUI.BeginDisabledGroup(true);

                if (_occlusionDebugFoldout)
                {
                    EditorGUI.EndDisabledGroup();
                    _hzbPreviewSize = EditorGUILayout.Slider("Preview Size", _hzbPreviewSize, 128f, 512f);

                    var cams = system.ActiveGameCameras;
                    if (cams != null && cams.Count > 0)
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
            EditorGUILayout.Space(sectionSpacing);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField("Crossfade", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);

            if (system.StatAnyAnimatedCrossfade)
            {
                string animInfo = $"ON ({system.StatAnimatedProtoCount} protos, {system.StatFadeStateViews} views, {system.StatTotalFadeStates} states, {system.StatLodFadeDuration}s)";
                if (system.StatSkipIntermediateLODs)
                    animInfo += " [Skip LODs]";
                EditorGUILayout.LabelField("Animated Crossfade", animInfo);
            }
            else
            {
                EditorGUILayout.LabelField("Animated Crossfade", "OFF");
            }

            // debug flags
            if (system.StatDebugDisableCPUChunkCulling || system.StatDebugDisableCulling || system.StatDebugDisableRendering)
            {
                EditorGUI.EndDisabledGroup();
                if (system.StatDebugDisableCPUChunkCulling)
                    EditorGUILayout.HelpBox("DEBUG: CPU chunk culling DISABLED", MessageType.Warning);
                if (system.StatDebugDisableCulling)
                    EditorGUILayout.HelpBox("DEBUG: Culling DISABLED (reusing stale results)", MessageType.Warning);
                if (system.StatDebugDisableRendering)
                    EditorGUILayout.HelpBox("DEBUG: Rendering DISABLED (culling still runs)", MessageType.Warning);
                EditorGUI.BeginDisabledGroup(true);
            }

            EditorGUILayout.Space(sectionSpacing);

            // memory
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField("GPU Memory", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);

            // guard against accessing memory during reinitialize
            long memoryBytes = 0;
            string memoryStr;
            GPUBatcherRenderer.MemoryBreakdown breakdown = default;
            try
            {
                breakdown = system.GetMemoryBreakdown();
                memoryBytes = breakdown.Total;
                memoryStr = GPUBatcherConfig.FormatBytes(memoryBytes);
            }
            catch (System.Exception)
            {
                memoryStr = "Calculating...";
            }

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
                EditorGUILayout.LabelField("Prototype Data", GPUBatcherConfig.FormatBytes(breakdown.PrototypeData));
                EditorGUILayout.LabelField("Instance Data", GPUBatcherConfig.FormatBytes(breakdown.InstanceData));
                EditorGUILayout.LabelField("Matrix Data", GPUBatcherConfig.FormatBytes(breakdown.MatrixData));
                EditorGUILayout.LabelField("Chunk Data", GPUBatcherConfig.FormatBytes(breakdown.ChunkData));
                EditorGUILayout.LabelField("Pool Ref Data", GPUBatcherConfig.FormatBytes(breakdown.PoolRefData));
                DrawViewBufferFoldout(system, ref _cameraBuffersFoldout, "Camera View Buffers", breakdown.CameraViewBuffers, ViewBufferKind.Camera);
                DrawViewBufferFoldout(system, ref _shadowBuffersFoldout, "Shadow View Buffers", breakdown.ShadowViewBuffers, ViewBufferKind.Shadow);
                DrawViewBufferFoldout(system, ref _fadeStatesFoldout, "FadeState Buffers", breakdown.FadeStateBuffers, ViewBufferKind.FadeState);
                EditorGUILayout.LabelField("Bucket Mapping", GPUBatcherConfig.FormatBytes(breakdown.BucketMapping));
                EditorGUILayout.LabelField("Staging Buffers", GPUBatcherConfig.FormatBytes(breakdown.StagingBuffers));
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
