using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// System used for skipping culling. It is used with <see cref="Graphics.DrawMesh"/> as it already handles culling.
    /// </summary>
    internal class DecalSkipCulledSystem
    {
        private DecalEntityManager _entityManager;
        private ProfilingSampler _sampler;
        private Camera _camera;


        public DecalSkipCulledSystem(DecalEntityManager entityManager)
        {
            _entityManager = entityManager;
            _sampler = new ProfilingSampler("DecalSkipCulledSystem.Execute");
        }

        public void Execute(Camera camera)
        {
            using (new ProfilingScope(_sampler))
            {
                _camera = camera;
                for (int i = 0; i < _entityManager.chunkCount; ++i)
                    Execute(_entityManager.culledChunks[i], _entityManager.culledChunks[i].count);
            }
        }

        private void Execute(DecalCulledChunk culledChunk, int count)
        {
            if (count == 0)
                return;

            culledChunk.currentJobHandle.Complete();

            for (int i = 0; i < count; ++i)
                culledChunk.visibleDecalIndices[i] = i;
            culledChunk.visibleDecalCount = count;
            culledChunk.cameraPosition = _camera.transform.position;
            culledChunk.cullingMask = _camera.cullingMask;
#if UNITY_EDITOR
            culledChunk.sceneCullingMask = GetSceneCullingMaskFromCamera(_camera);
#endif
        }

        internal static UInt64 GetSceneCullingMaskFromCamera(Camera camera)
        {
#if UNITY_EDITOR
            if (camera.overrideSceneCullingMask != 0)
                return camera.overrideSceneCullingMask;

            if (camera.scene.IsValid())
                return UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(camera.scene);

            switch (camera.cameraType)
            {
                case CameraType.SceneView:
                    return UnityEditor.SceneManagement.SceneCullingMasks.MainStageSceneViewObjects;
                default:
                    return UnityEditor.SceneManagement.SceneCullingMasks.GameViewObjects;
            }
#else
            return 0;
#endif
        }
    }
}
