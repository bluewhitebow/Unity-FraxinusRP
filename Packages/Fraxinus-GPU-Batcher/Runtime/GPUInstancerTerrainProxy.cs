using System;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    [ExecuteInEditMode]
    public class GPUInstancerTerrainProxy : MonoBehaviour
    {
        [SerializeField]
        public string detailProfileId;
        [SerializeField]
        public string treeProfileId;

        [NonSerialized]
        private GPUInstancerDetailManager _runtimeDetailManager;
        [NonSerialized]
        private GPUInstancerTreeManager _runtimeTreeManager;

        /// <summary>Runtime manager resolved from <see cref="detailProfileId"/>.</summary>
        public GPUInstancerDetailManager runtimeDetailManager => _runtimeDetailManager;

        /// <summary>Runtime manager resolved from <see cref="treeProfileId"/>.</summary>
        public GPUInstancerTreeManager runtimeTreeManager => _runtimeTreeManager;

        public bool beingDestroyed;
        public int terrainSelectedToolIndex = -1;

        private void OnEnable()
        {
            beingDestroyed = false;
            // ExecuteInEditMode invokes OnEnable while an inspector is being
            // rebuilt.  Profile managers are runtime engines, so defer lazy
            // creation to play mode (editor simulation explicitly asks the
            // Terrain binder for a manager when needed).
            if (Application.isPlaying)
                GPUInstancerTerrainBinder.Bind(this);
        }

        private void OnDisable()
        {
            // Editor profile previews can bind a transient manager even while
            // the scene is not playing. Detach each channel independently so
            // disabling a proxy never tears down its sibling Tree/Detail
            // binding.
            if (Application.isPlaying || _runtimeDetailManager != null ||
                !string.IsNullOrEmpty(detailProfileId))
                GPUInstancerTerrainBinder.Unbind(this, true);
            if (Application.isPlaying || _runtimeTreeManager != null ||
                !string.IsNullOrEmpty(treeProfileId))
                GPUInstancerTerrainBinder.Unbind(this, false);
            _runtimeDetailManager = null;
            _runtimeTreeManager = null;
        }

        private void OnDestroy()
        {
            beingDestroyed = true;
            // OnDisable is normally invoked first, but Unity does not promise
            // that ordering for all scene/domain teardown paths. Repeat the
            // channel-specific checks because OnDestroy may be delivered
            // without OnDisable (or after its transient references were
            // cleared) while IDs still identify a live binding.
            if (Application.isPlaying || _runtimeDetailManager != null ||
                !string.IsNullOrEmpty(detailProfileId))
                GPUInstancerTerrainBinder.Unbind(this, true);
            if (Application.isPlaying || _runtimeTreeManager != null ||
                !string.IsNullOrEmpty(treeProfileId))
                GPUInstancerTerrainBinder.Unbind(this, false);
            _runtimeDetailManager = null;
            _runtimeTreeManager = null;
        }

        /// <summary>
        /// Assigns a transient manager resolved by the Terrain binder.
        /// </summary>
        public void SetRuntimeManager(GPUInstancerTerrainManager manager, bool detail)
        {
            if (detail)
                _runtimeDetailManager = manager as GPUInstancerDetailManager;
            else
                _runtimeTreeManager = manager as GPUInstancerTreeManager;
        }

        /// <summary>
        /// Updates the persisted profile ID used by editor tooling.
        /// </summary>
        public void SetProfileId(GPUInstancerProfile profile, bool detail)
        {
            string value = profile != null ? profile.id : null;
            if (detail)
                detailProfileId = value;
            else
                treeProfileId = value;
        }

#if UNITY_EDITOR
        private long _lastDetailChangeTicks;
        private static readonly long _waitForTicks = 4000000;
        private bool _isBeingModified;

        private void Update()
        {
            GPUInstancerDetailManager detail = _runtimeDetailManager;
            if (!_isBeingModified && detail != null && detail.editorLoopState != null && detail.keepSimulationLive && !detail.editorLoopState.simulateAtEditor)
                detail.editorLoopState.StartSimulation();
        }

        private void OnTerrainChanged(TerrainChangedFlags flags)
        {
            if (Application.isPlaying)
                return;
            if ((flags & TerrainChangedFlags.RemoveDirtyDetailsImmediately) != 0 || (flags & TerrainChangedFlags.Heightmap) != 0 || (flags & TerrainChangedFlags.FlushEverythingImmediately) != 0)
            {
                _lastDetailChangeTicks = DateTime.Now.Ticks;
                GPUInstancerDetailManager detail = _runtimeDetailManager;
                if (detail != null && detail.editorLoopState != null && detail.editorLoopState.simulateAtEditor && detail.keepSimulationLive && detail.updateSimulation)
                {
                    _isBeingModified = true;
                    EditorApplication.update -= RestartDetailSimulation;
                    EditorApplication.update += RestartDetailSimulation;
                }
            }
        }

        private void RestartDetailSimulation()
        {
            GPUInstancerDetailManager detail = _runtimeDetailManager;
            if (Application.isPlaying || detail == null || detail.editorLoopState == null)
            {
                EditorApplication.update -= RestartDetailSimulation;
                _isBeingModified = false;
                return;
            }
            if (DateTime.Now.Ticks - _lastDetailChangeTicks < _waitForTicks)
                return;
            detail.editorLoopState.StopSimulation();
            if (detail.terrain != null)
                detail.terrain.detailObjectDistance = 0;
            detail.editorLoopState.StartSimulation();
            _isBeingModified = false;
            EditorApplication.update -= RestartDetailSimulation;
        }
#endif
    }
}
