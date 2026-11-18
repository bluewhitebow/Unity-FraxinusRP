using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Abstract class that render decals using <see cref="DecalDrawCallChunk"/>.
    /// Supports rendering with <see cref="CommandBuffer"/> and graphics draw calls.
    /// </summary>
    internal abstract class DecalDrawSystem
    {
        readonly static internal uint maxBatchSize = 250;
        static readonly bool s_SupportsInstancing = SystemInfo.supportsInstancing;

        protected DecalEntityManager _entityManager;
        private Matrix4x4[] _worldToDecals;
        private Matrix4x4[] _normalToDecals;
        private float[] _decalLayerMasks;
        private ProfilingSampler _sampler;

        public Material overrideMaterial { get; set; }

        public DecalDrawSystem(string sampler, DecalEntityManager entityManager)
        {
            _entityManager = entityManager;

            _worldToDecals = new Matrix4x4[maxBatchSize];
            _normalToDecals = new Matrix4x4[maxBatchSize];
            _decalLayerMasks = new float[maxBatchSize];

            _sampler = new ProfilingSampler(sampler);
        }

        public void Execute(CommandBuffer cmd)
        {
            Execute(CommandBufferHelpers.GetRasterCommandBuffer(cmd));
        }

        internal void Execute(RasterCommandBuffer cmd)
        {
            using (new ProfilingScope(cmd, _sampler))
            {
                for (int i = 0; i < _entityManager.chunkCount; ++i)
                {
                    Execute(
                        cmd,
                        _entityManager.entityChunks[i],
                        _entityManager.cachedChunks[i],
                        _entityManager.drawCallChunks[i],
                        _entityManager.entityChunks[i].count);
                }
            }
        }

        protected virtual Material GetMaterial(DecalEntityChunk decalEntityChunk) => decalEntityChunk.material;

        protected abstract int GetPassIndex(DecalCachedChunk decalCachedChunk);

        private void Execute(RasterCommandBuffer cmd, DecalEntityChunk decalEntityChunk, DecalCachedChunk decalCachedChunk, DecalDrawCallChunk decalDrawCallChunk, int count)
        {
            decalCachedChunk.currentJobHandle.Complete();
            decalDrawCallChunk.currentJobHandle.Complete();

            Material material = GetMaterial(decalEntityChunk);
            int passIndex = GetPassIndex(decalCachedChunk);

            if (count == 0 || passIndex == -1 || material == null)
                return;

            if (s_SupportsInstancing && material.enableInstancing)
            {
                DrawInstanced(cmd, decalEntityChunk, decalCachedChunk, decalDrawCallChunk, passIndex);
            }
            else
            {
                Draw(cmd, decalEntityChunk, decalCachedChunk, decalDrawCallChunk, passIndex);
            }
        }

        private void Draw(RasterCommandBuffer cmd, DecalEntityChunk decalEntityChunk, DecalCachedChunk decalCachedChunk, DecalDrawCallChunk decalDrawCallChunk, int passIndex)
        {
            var mesh = _entityManager.decalProjectorMesh;
            var material = GetMaterial(decalEntityChunk);
            decalCachedChunk.propertyBlock.SetVector("unity_LightData", new Vector4(1, 1, 1, 0)); // GetMainLight requires z component to be set

            int subCallCount = decalDrawCallChunk.subCallCount;
            for (int i = 0; i < subCallCount; ++i)
            {
                var subCall = decalDrawCallChunk.subCalls[i];

                for (int j = subCall.start; j < subCall.end; ++j)
                {
                    decalCachedChunk.propertyBlock.SetMatrix("_NormalToWorld", decalDrawCallChunk.normalToDecals[j]);
                    decalCachedChunk.propertyBlock.SetFloat("_DecalLayerMaskFromDecal", decalDrawCallChunk.renderingLayerMasks[j]);
                    cmd.DrawMesh(mesh, decalDrawCallChunk.decalToWorlds[j], material, 0, passIndex, decalCachedChunk.propertyBlock);
                }
            }
        }

        private void DrawInstanced(RasterCommandBuffer cmd, DecalEntityChunk decalEntityChunk, DecalCachedChunk decalCachedChunk, DecalDrawCallChunk decalDrawCallChunk, int passIndex)
        {
            var mesh = _entityManager.decalProjectorMesh;
            var material = GetMaterial(decalEntityChunk);
            decalCachedChunk.propertyBlock.SetVector("unity_LightData", new Vector4(1, 1, 1, 0)); // GetMainLight requires z component to be set

            int subCallCount = decalDrawCallChunk.subCallCount;
            for (int i = 0; i < subCallCount; ++i)
            {
                var subCall = decalDrawCallChunk.subCalls[i];

                var decalToWorldSlice = decalDrawCallChunk.decalToWorlds.Reinterpret<Matrix4x4>();
                NativeArray<Matrix4x4>.Copy(decalToWorldSlice, subCall.start, _worldToDecals, 0, subCall.count);

                var normalToWorldSlice = decalDrawCallChunk.normalToDecals.Reinterpret<Matrix4x4>();
                NativeArray<Matrix4x4>.Copy(normalToWorldSlice, subCall.start, _normalToDecals, 0, subCall.count);

                var decalLayerMaskSlice = decalDrawCallChunk.renderingLayerMasks.Reinterpret<float>();
                NativeArray<float>.Copy(decalLayerMaskSlice, subCall.start, _decalLayerMasks, 0, subCall.count);

                decalCachedChunk.propertyBlock.SetMatrixArray("_NormalToWorld", _normalToDecals);
                decalCachedChunk.propertyBlock.SetFloatArray("_DecalLayerMaskFromDecal", _decalLayerMasks);
                cmd.DrawMeshInstanced(mesh, 0, material, passIndex, _worldToDecals, subCall.end - subCall.start, decalCachedChunk.propertyBlock);
            }
        }

        public void Execute(in CameraData cameraData)
        {
            using (new ProfilingScope(_sampler))
            {
                for (int i = 0; i < _entityManager.chunkCount; ++i)
                {
                    Execute(
                        cameraData,
                        _entityManager.entityChunks[i],
                        _entityManager.cachedChunks[i],
                        _entityManager.drawCallChunks[i],
                        _entityManager.entityChunks[i].count);
                }
            }
        }

        private void Execute(in CameraData cameraData, DecalEntityChunk decalEntityChunk, DecalCachedChunk decalCachedChunk, DecalDrawCallChunk decalDrawCallChunk, int count)
        {
            decalCachedChunk.currentJobHandle.Complete();
            decalDrawCallChunk.currentJobHandle.Complete();

            Material material = GetMaterial(decalEntityChunk);
            int passIndex = GetPassIndex(decalCachedChunk);

            if (count == 0 || passIndex == -1 || material == null)
                return;

            if (s_SupportsInstancing && material.enableInstancing)
            {
                DrawInstanced(cameraData, decalEntityChunk, decalCachedChunk, decalDrawCallChunk);
            }
            else
            {
                Draw(cameraData, decalEntityChunk, decalCachedChunk, decalDrawCallChunk);
            }
        }

        private void Draw(in CameraData cameraData, DecalEntityChunk decalEntityChunk, DecalCachedChunk decalCachedChunk, DecalDrawCallChunk decalDrawCallChunk)
        {
            var mesh = _entityManager.decalProjectorMesh;
            var material = GetMaterial(decalEntityChunk);
            int subCallCount = decalDrawCallChunk.subCallCount;
            for (int i = 0; i < subCallCount; ++i)
            {
                var subCall = decalDrawCallChunk.subCalls[i];

                for (int j = subCall.start; j < subCall.end; ++j)
                {
                    decalCachedChunk.propertyBlock.SetMatrix("_NormalToWorld", decalDrawCallChunk.normalToDecals[j]);
                    decalCachedChunk.propertyBlock.SetFloat("_DecalLayerMaskFromDecal", decalDrawCallChunk.renderingLayerMasks[j]);
                    // RENDERGRAPH TODO: schedule drawmesh through commandBuffer?
                    Graphics.DrawMesh(mesh, decalDrawCallChunk.decalToWorlds[j], material, decalCachedChunk.layerMasks[j], cameraData.camera, 0, decalCachedChunk.propertyBlock);
                }
            }
        }

        private void DrawInstanced(in CameraData cameraData, DecalEntityChunk decalEntityChunk, DecalCachedChunk decalCachedChunk, DecalDrawCallChunk decalDrawCallChunk)
        {
            var mesh = _entityManager.decalProjectorMesh;
            var material = GetMaterial(decalEntityChunk);
            decalCachedChunk.propertyBlock.SetVector("unity_LightData", new Vector4(1, 1, 1, 0)); // GetMainLight requires z component to be set

            int subCallCount = decalDrawCallChunk.subCallCount;
            for (int i = 0; i < subCallCount; ++i)
            {
                var subCall = decalDrawCallChunk.subCalls[i];

                var decalToWorldSlice = decalDrawCallChunk.decalToWorlds.Reinterpret<Matrix4x4>();
                NativeArray<Matrix4x4>.Copy(decalToWorldSlice, subCall.start, _worldToDecals, 0, subCall.count);

                var normalToWorldSlice = decalDrawCallChunk.normalToDecals.Reinterpret<Matrix4x4>();
                NativeArray<Matrix4x4>.Copy(normalToWorldSlice, subCall.start, _normalToDecals, 0, subCall.count);

                var decalLayerMaskSlice = decalDrawCallChunk.renderingLayerMasks.Reinterpret<float>();
                NativeArray<float>.Copy(decalLayerMaskSlice, subCall.start, _decalLayerMasks, 0, subCall.count);

                decalCachedChunk.propertyBlock.SetMatrixArray("_NormalToWorld", _normalToDecals);
                decalCachedChunk.propertyBlock.SetFloatArray("_DecalLayerMaskFromDecal", _decalLayerMasks);
                // RENDERGRAPH TODO: schedule drawmesh through commandBuffer?
                Graphics.DrawMeshInstanced(mesh, 0, material,
                    _worldToDecals, subCall.count, decalCachedChunk.propertyBlock, ShadowCastingMode.On, true, 0, cameraData.camera);
            }
        }
    }
}
