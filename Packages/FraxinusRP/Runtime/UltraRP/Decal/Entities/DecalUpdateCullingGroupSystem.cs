using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Contains culling results.
    /// </summary>
    internal class DecalCulledChunk : DecalChunk
    {
        public Vector3 cameraPosition;
        public ulong sceneCullingMask;
        public int cullingMask;

        public CullingGroup cullingGroups;
        public int[] visibleDecalIndexArray;
        public NativeArray<int> visibleDecalIndices;
        public int visibleDecalCount;

        public override void RemoveAtSwapBack(int entityIndex)
        {
            RemoveAtSwapBack(ref visibleDecalIndexArray, entityIndex, count);
            RemoveAtSwapBack(ref visibleDecalIndices, entityIndex, count);
            count--;
        }

        public override void SetCapacity(int newCapacity)
        {
            ArrayExtensions.ResizeArray(ref visibleDecalIndexArray, newCapacity);
            visibleDecalIndices.ResizeArray(newCapacity);
            if (cullingGroups == null)
                cullingGroups = new CullingGroup();
            capacity = newCapacity;
        }

        public override void Dispose()
        {
            if (capacity == 0)
                return;

            visibleDecalIndices.Dispose();
            visibleDecalIndexArray = null;
            count = 0;
            capacity = 0;
            cullingGroups.Dispose();
            cullingGroups = null;
        }
    }

    /// <summary>
    /// Issues culling job with <see cref="CullingGroup"/>.
    /// </summary>
    internal class DecalUpdateCullingGroupSystem
    {
        /// <summary>
        /// Provides acces to the bounding distance.
        /// </summary>
        public float boundingDistance
        {
            get { return _boundingDistance[0]; }
            set { _boundingDistance[0] = value; }
        }

        private float[] _boundingDistance = new float[1];
        private Camera _camera;
        private DecalEntityManager _entityManager;
        private ProfilingSampler _sampler;

        public DecalUpdateCullingGroupSystem(DecalEntityManager entityManager, float drawDistance)
        {
            _entityManager = entityManager;
            _boundingDistance[0] = drawDistance;
            _sampler = new ProfilingSampler("DecalUpdateCullingGroupsSystem.Execute");
        }

        public void Execute(Camera camera)
        {
            using (new ProfilingScope(_sampler))
            {
                _camera = camera;
                for (int i = 0; i < _entityManager.chunkCount; ++i)
                    Execute(_entityManager.cachedChunks[i], _entityManager.culledChunks[i], _entityManager.culledChunks[i].count);
            }
        }

        public void Execute(DecalCachedChunk cachedChunk, DecalCulledChunk culledChunk, int count)
        {
            cachedChunk.currentJobHandle.Complete();

            CullingGroup cullingGroup = culledChunk.cullingGroups;
            cullingGroup.targetCamera = _camera;
            cullingGroup.SetDistanceReferencePoint(_camera.transform.position);
            cullingGroup.SetBoundingDistances(_boundingDistance);
            cachedChunk.boundingSpheres.CopyTo(cachedChunk.boundingSphereArray);
            cullingGroup.SetBoundingSpheres(cachedChunk.boundingSphereArray);
            cullingGroup.SetBoundingSphereCount(count);

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
