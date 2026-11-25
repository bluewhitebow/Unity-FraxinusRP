using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class TileClusterDebugPass
    {
        private class PassData
        {
            internal Material material;
            internal ShaderVariablesLightList lightCBuffer;
            internal Vector2 viewportScale;
            internal bool useDeferredClassifyBuffers;
            internal BufferHandle deferredDispatchBuffer;
            internal BufferHandle deferredTileListBuffer;
        }

        static readonly int k_gTileListId = Shader.PropertyToID("g_TileList");
        static readonly int k_gDispatchIndirectBufferId = Shader.PropertyToID("g_DispatchIndirectBuffer");
        static readonly int k_DeferredClassifyTilesNumId = Shader.PropertyToID("_DeferredClassifyTilesNum");

        private Material _material;
        private RTHandle _renderTarget;
        private ShaderVariablesLightList _lightCBuffer;
        private DebugTileClusterMode _debugMode;
        private int _clusterDebugID;

        private ProfilingSampler _profilingSampler;

        public TileClusterDebugPass(Material mat)
        {
            _profilingSampler = new ProfilingSampler(nameof(TileClusterDebugPass));
            _material = mat;
            _debugMode = DebugTileClusterMode.None;
            _clusterDebugID = 0;
        }

        public void Dispose()
        {

        }

        internal void Setup(CameraData cameraData, DebugTileClusterMode debugMode, int clusterDebugID, ShaderVariablesLightList lightCBuffer)
        {
            _debugMode = debugMode;
            _lightCBuffer = lightCBuffer;
            _clusterDebugID = clusterDebugID;
        }


        internal void RenderTileClusterDebug(RenderGraph renderGraph, ContextContainer frameData, CameraData cameraData, DebugTileClusterMode debugMode, int clusterDebugID, DebugClusterCategory debugCategory)
        {
            if (!frameData.Contains<GPULightsOutPassData>())
                return;

            if (debugMode == DebugTileClusterMode.DeferredClassifyTiles)
            {
                if (!frameData.Contains<DeferredClassifyDebugBuffersData>())
                    return;
                var dbg = frameData.Get<DeferredClassifyDebugBuffersData>();
                if (!dbg.isValid || !dbg.dispatchIndirectBuffer.IsValid() || !dbg.tileListBuffer.IsValid())
                    return;
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>("Tile Cluster Debug", out var passData, _profilingSampler))
            {
                var gpuLightsOutData = frameData.Get<GPULightsOutPassData>();

                LightCategory _DebugCategory = LightCategory.Punctual;
                switch (debugCategory)
                {
                    case DebugClusterCategory.PunctualLights:
                        _DebugCategory = LightCategory.Punctual;
                        break;
                    case DebugClusterCategory.ReflectionProbes:
                        _DebugCategory = LightCategory.Env;
                        break;
                }

                int clusterIdForShader = clusterDebugID;
                if (debugMode == DebugTileClusterMode.DeferredClassifyTiles)
                    clusterIdForShader = Mathf.Clamp(clusterDebugID, 0, (int)ShadingModels.CurModelsNum - 1);

                _material.SetFloat("_DebugTileClusterMode", (float)debugMode);
                _material.SetInteger("_ClusterDebugID", clusterIdForShader);
                _material.SetFloat("_YFilp", cameraData.cameraType == CameraType.Game ? 1.0f : 0.0f);
                _material.SetInteger("_DebugCategory", (int)_DebugCategory);

                passData.material = _material;
                passData.lightCBuffer = gpuLightsOutData.lightListCB;
                passData.viewportScale = Vector2.one;
                passData.useDeferredClassifyBuffers = false;
                passData.deferredDispatchBuffer = BufferHandle.nullHandle;
                passData.deferredTileListBuffer = BufferHandle.nullHandle;

                if (debugMode == DebugTileClusterMode.DeferredClassifyTiles && frameData.Contains<DeferredClassifyDebugBuffersData>())
                {
                    var dbg = frameData.Get<DeferredClassifyDebugBuffersData>();
                    if (dbg.isValid)
                    {
                        passData.useDeferredClassifyBuffers = true;
                        passData.deferredDispatchBuffer = dbg.dispatchIndirectBuffer;
                        passData.deferredTileListBuffer = dbg.tileListBuffer;

                        uint tilesNum = (uint)(Mathf.Max(0, dbg.numTilesX) * Mathf.Max(0, dbg.numTilesY));
                        _material.SetInteger(k_DeferredClassifyTilesNumId, (int)tilesNum);

                        builder.UseBuffer(dbg.dispatchIndirectBuffer, AccessFlags.Read);
                        builder.UseBuffer(dbg.tileListBuffer, AccessFlags.Read);
                    }
                }

                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }

        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer unsafeCmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            ConstantBuffer.Push(unsafeCmd, data.lightCBuffer, data.material, Shader.PropertyToID("ShaderVariablesLightList"));

            if (data.useDeferredClassifyBuffers)
            {
                data.material.SetBuffer(k_gTileListId, (GraphicsBuffer)data.deferredTileListBuffer);
                data.material.SetBuffer(k_gDispatchIndirectBufferId, (GraphicsBuffer)data.deferredDispatchBuffer);
            }

            Blitter.BlitTexture(unsafeCmd, data.viewportScale, data.material, 0);
        }

    }
}
