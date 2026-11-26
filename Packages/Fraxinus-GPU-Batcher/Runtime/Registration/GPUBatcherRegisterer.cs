// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    /// <summary>MonoBehaviour wrapper around GPUBatcherRegistrationTracker. Subclasses implement OnInitialize() and OnShutdown().</summary>
    [ExecuteAlways]
    public abstract class GPUBatcherRegisterer : MonoBehaviour
    {
        private GPUBatcherRegistrationTracker _registration;
        private bool _pendingCameraInitialization;

        public GPUBatcherRegistrationTracker registration
        {
            get { EnsureRegistration(); return _registration; }
        }

        protected GPUBatcherRenderer GPUBatcherSystem => _registration?.gpuBatcherSystem;
        protected bool _registered => _registration?.isRegistered ?? false;
        internal bool isRegistered => _registered;

        private void EnsureRegistration()
        {
            if (_registration != null) return;
            _registration = new GPUBatcherRegistrationTracker(GetType().Name);
            _registration.initializeCallback = () => OnInitialize();
            _registration.shutdownCallback = () => OnShutdown();
            _registration.chunkWritten += OnChunkWritten;
        }

        #region Unity Lifecycle

        protected virtual void OnEnable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorApplication.delayCall -= DelayedInitialize;
                EditorApplication.delayCall += DelayedInitialize;
                return;
            }
#endif
            Initialize();
        }

        protected virtual void OnDisable()
        {
            Shutdown();
        }

#if UNITY_EDITOR
        private void DelayedInitialize()
        {
            if (this == null || !isActiveAndEnabled || _registered) return;
            Initialize();
        }
#endif

        #endregion

        #region Core Lifecycle

        public void Initialize()
        {
            if (_registered) return;
            if (Rendering.D3D11SourceSupport.requiresValidation && !SupportsD3D11Registration())
            {
                // Awake can precede SRP selection. Query the material pass layout once more
                // after the first game camera has rendered with the active pipeline.
                if (!_pendingCameraInitialization)
                {
                    _pendingCameraInitialization = true;
                    RenderPipelineManager.endCameraRendering += InitializeAfterCamera;
                }
                return;
            }
            InitializeRegistration();
        }

        private void InitializeAfterCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!camera || camera.cameraType != CameraType.Game) return;
            CancelCameraInitialization();
            if (isActiveAndEnabled && !_registered && SupportsD3D11Registration())
                InitializeRegistration();
        }

        private void CancelCameraInitialization()
        {
            if (!_pendingCameraInitialization) return;
            RenderPipelineManager.endCameraRendering -= InitializeAfterCamera;
            _pendingCameraInitialization = false;
        }

        private void InitializeRegistration()
        {
            CancelCameraInitialization();
            EnsureRegistration();
            _registration.Initialize();
        }

        public void Shutdown()
        {
            CancelCameraInitialization();
            if (_registration == null) return;
            _registration.Shutdown();
        }

        #endregion

        #region Abstract Hooks

        protected abstract void OnInitialize();

        protected abstract void OnShutdown();

        protected virtual bool SupportsD3D11Registration() => false;

        // fires sync during immediate writes, deferred after CommitDeferredWrites
        protected virtual void OnChunkWritten(int chunkId) { }

        #endregion

        #region prototype registration (tracked)

        protected RenderSignature ExtractRenderSignature(GameObject prefab)
            => _registration.ExtractRenderSignature(prefab);

        protected int RegisterRenderSignatureFromPrefab(GameObject prefab)
            => _registration.RegisterRenderSignatureFromPrefab(prefab);

        protected int[] RegisterRenderSignaturesFromPrefab(GameObject prefab)
            => _registration.RegisterRenderSignaturesFromPrefab(prefab);

        protected int RegisterRenderSignature(RenderSignature signature)
            => _registration.RegisterRenderSignature(signature);

        protected void UnregisterRenderSignature(int globalIdx)
            => _registration.UnregisterRenderSignature(globalIdx);

        #endregion

        #region chunk lifecycle (tracked)

        protected int MaxInstancesPerChunk => _registration?.maxInstancesPerChunk ?? 2048;

        protected ChunkLink AllocChunk() => _registration.AllocChunk();

        protected void WriteChunk(ChunkLink link, NativeArray<InstanceData> instances, Bounds bounds,
                                  float cullDistance = 0f, float instanceCullDistance = float.MaxValue)
            => _registration.WriteChunk(link, instances, bounds, cullDistance, instanceCullDistance);

        protected InstanceLink GetInstanceLink(ChunkLink link, int localIndex)
            => _registration.GetInstanceLink(link, localIndex);

        protected int GetTotalChunkInstanceCount(ChunkLink link)
            => _registration.GetTotalChunkInstanceCount(link);

        protected int GetOverflowChunkCount(ChunkLink link)
            => _registration.GetOverflowChunkCount(link);

        protected void GetPerChunkInstanceCounts(ChunkLink link, List<int> outCounts)
            => _registration.GetPerChunkInstanceCounts(link, outCounts);

        protected void ReleaseChunk(ChunkLink link) => _registration.ReleaseChunk(link);

        protected void RecalculateChunkBounds(ChunkLink link, Bounds newBounds)
            => _registration.RecalculateChunkBounds(link, newBounds);

        protected void SetChunkCullDistances(ChunkLink link, float cullDistance, float instanceCullDistance)
            => _registration.SetChunkCullDistances(link, cullDistance, instanceCullDistance);

        public void SetChunkEnabled(ChunkLink link, bool enabled)
            => _registration.SetChunkEnabled(link, enabled);

        public bool GetChunkEnabled(ChunkLink link)
            => _registration.GetChunkEnabled(link);

        public void SetRenderEnabled(bool enabled)
            => _registration.SetRenderEnabled(enabled);

        #endregion

        #region instance lifecycle

        protected InstanceLink AddInstance(ChunkLink chunk, InstanceData instance)
            => _registration.AddInstance(chunk, instance);

        protected InstanceLink MoveInstanceToChunk(InstanceLink source, ChunkLink destChunk, InstanceData instanceData)
            => _registration.MoveInstanceToChunk(source, destChunk, instanceData);

        protected InstanceLink[] AddInstances(ChunkLink chunk, NativeArray<InstanceData> instances, int count)
            => _registration.AddInstances(chunk, instances, count);

        #endregion

        #region tracked staging

        public void StagePosition(InstanceLink link, float3 position)
            => _registration.StagePosition(link, position);

        public void StageRotation(InstanceLink link, quaternion rotation)
            => _registration.StageRotation(link, rotation);

        public void StageScale(InstanceLink link, float3 scale)
            => _registration.StageScale(link, scale);

        public void StageMoveAndRotate(InstanceLink link, float3 position, quaternion rotation)
            => _registration.StageMoveAndRotate(link, position, rotation);

        public void StageMove(InstanceLink link, float3 pos, quaternion rot, float3 scale)
            => _registration.StageMove(link, pos, rot, scale);

        public void StageColor(InstanceLink link, Color32 color)
            => _registration.StageColor(link, color);

        public void StageRemove(InstanceLink link)
            => _registration.StageRemove(link);

        public void StagePosition(int globalSlot, float3 position)
            => _registration.StagePosition(globalSlot, position);

        public void StageRotation(int globalSlot, quaternion rotation)
            => _registration.StageRotation(globalSlot, rotation);

        public void StageScale(int globalSlot, float3 scale)
            => _registration.StageScale(globalSlot, scale);

        public void StageMoveAndRotate(int globalSlot, float3 position, quaternion rotation)
            => _registration.StageMoveAndRotate(globalSlot, position, rotation);

        public void StageMove(int globalSlot, float3 pos, quaternion rot, float3 scale)
            => _registration.StageMove(globalSlot, pos, rot, scale);

        public void StageColor(int globalSlot, Color32 color)
            => _registration.StageColor(globalSlot, color);

        public void StageSetEnabled(InstanceLink link, bool enabled)
            => _registration.StageSetEnabled(link, enabled);

        public void StageSetEnabled(int globalSlot, bool enabled)
            => _registration.StageSetEnabled(globalSlot, enabled);

        public void StageRemove(int chunkId, int globalSlot)
            => _registration.StageRemove(chunkId, globalSlot);

        // unsafe direct pass-through - no buffering, no conflict resolution
        public void SetPositionUnsafe(int globalSlot, float3 position)
            => _registration.SetPositionUnsafe(globalSlot, position);

        public void SetRotationUnsafe(int globalSlot, quaternion rotation)
            => _registration.SetRotationUnsafe(globalSlot, rotation);

        public void SetScaleUnsafe(int globalSlot, float3 scale)
            => _registration.SetScaleUnsafe(globalSlot, scale);

        public void MoveAndRotateUnsafe(int globalSlot, float3 position, quaternion rotation)
            => _registration.MoveAndRotateUnsafe(globalSlot, position, rotation);

        public void MoveUnsafe(int globalSlot, float3 pos, quaternion rot, float3 scale)
            => _registration.MoveUnsafe(globalSlot, pos, rot, scale);

        public void SetColorUnsafe(int globalSlot, Color32 color)
            => _registration.SetColorUnsafe(globalSlot, color);

        public void RemoveUnsafe(int chunkId, int globalSlot)
            => _registration.RemoveUnsafe(chunkId, globalSlot);

        public void SetEnabledUnsafe(int globalSlot, bool enabled)
            => _registration.SetEnabledUnsafe(globalSlot, enabled);

        public void SetPositionUnsafe(InstanceLink link, float3 position)
            => _registration.SetPositionUnsafe(link, position);

        public void SetRotationUnsafe(InstanceLink link, quaternion rotation)
            => _registration.SetRotationUnsafe(link, rotation);

        public void SetScaleUnsafe(InstanceLink link, float3 scale)
            => _registration.SetScaleUnsafe(link, scale);

        public void MoveAndRotateUnsafe(InstanceLink link, float3 position, quaternion rotation)
            => _registration.MoveAndRotateUnsafe(link, position, rotation);

        public void MoveUnsafe(InstanceLink link, float3 pos, quaternion rot, float3 scale)
            => _registration.MoveUnsafe(link, pos, rot, scale);

        public void SetColorUnsafe(InstanceLink link, Color32 color)
            => _registration.SetColorUnsafe(link, color);

        public void RemoveUnsafe(InstanceLink link)
            => _registration.RemoveUnsafe(link);

        public void SetEnabledUnsafe(InstanceLink link, bool enabled)
            => _registration.SetEnabledUnsafe(link, enabled);

        public void SampleLightProbeUnsafe(int globalSlot, float3 position)
            => _registration.SampleLightProbeUnsafe(globalSlot, position);

        public void SampleLightProbeUnsafe(InstanceLink link, float3 position)
            => _registration.SampleLightProbeUnsafe(link, position);

        #endregion

#if UNITY_EDITOR
        #region Chunk Gizmos

        private static GUIStyle s_gizmoLabelStyle;

        protected virtual void OnDrawGizmosSelected()
        {
            var config = GPUBatcherConfig.instance;
            if (config == null || !config.showChunkBounds) return;
            if (!_registered || GPUBatcherSystem == null) return;
            DrawChunkBoundsGizmos(_registration, _registration.primaryChunkIds);
        }

        /// <summary>Draws wire cube gizmos with metadata labels for the given chunk IDs.</summary>
        public static void DrawChunkBoundsGizmos(GPUBatcherRegistrationTracker tracker, IEnumerable<int> chunkIds)
        {
            if (tracker?.gpuBatcherSystem == null) return;
            var brgSystem = tracker.gpuBatcherSystem;

            if (s_gizmoLabelStyle == null)
            {
                s_gizmoLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white },
                    fontSize = 10,
                    fontStyle = FontStyle.Bold
                };
            }

            int maxPerChunk = tracker.maxInstancesPerChunk;

            foreach (int primaryId in chunkIds)
            {
                var info = brgSystem.GetChunkDebugInfo(primaryId);
                if (info.bounds.size == UnityEngine.Vector3.zero) continue;

                float fill = maxPerChunk > 0 ? (float)info.instanceCount / maxPerChunk : 0f;
                Gizmos.color = Color.Lerp(new Color(1f, 0.2f, 0.2f, 0.6f), new Color(0.2f, 1f, 0.2f, 0.6f), math.saturate(fill));
                Gizmos.DrawWireCube(info.bounds.center, info.bounds.size);

                var sb = new System.Text.StringBuilder();
                AppendChunkLabel(sb, primaryId, info, maxPerChunk);

                var overflows = tracker.GetOverflowChunkIds(primaryId);
                if (overflows != null)
                {
                    int totalCount = info.instanceCount;
                    for (int i = 0; i < overflows.Count; i++)
                    {
                        var ov = brgSystem.GetChunkDebugInfo(overflows[i]);
                        totalCount += ov.instanceCount;
                        sb.Append($"\n--- OVERFLOW {i} ---");
                        AppendChunkLabel(sb, overflows[i], ov, maxPerChunk);
                    }
                    sb.Append($"\ntotal: {totalCount}");
                }

                Handles.Label(info.bounds.center, sb.ToString(), s_gizmoLabelStyle);
            }
        }

        private static void AppendChunkLabel(System.Text.StringBuilder sb, int chunkId, GPUBatcherRenderer.ChunkDebugInfo info, int maxPerChunk)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"id: {chunkId}");
            sb.Append($"\n{info.instanceCount}/{maxPerChunk}");
            sb.Append($"\npools: {info.poolCount}");
            sb.Append($"\nshadow cascade: {info.maxShadowCascade}");
            if (info.cullDistance > 0f) sb.Append($"\ncull: {info.cullDistance:F0}m");
        }

        #endregion
#endif
    }
}
