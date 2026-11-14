using FraxinusEngine.Base;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal static class RenderingLayerUtils
    {
        public enum Event
        {
            DepthNormalPrePass,
            Opaque,
        }

        public enum MaskSize
        {
            Bits8,
            Bits16,
            Bits24,
            Bits32,
        }


        public static void CombineRendererEvents(bool isDeferred, int msaaSampleCount, Event rendererEvent, ref Event combinedEvent)
        {
            // Rendering layers can not use MSAA resolve, because it encodes integer
            if (msaaSampleCount > 1 && !isDeferred)
                combinedEvent = Event.DepthNormalPrePass;
            else // Otherwise we combine them by selecting the min of the two...
                combinedEvent = Combine(combinedEvent, rendererEvent);
        }

        public static GraphicsFormat GetFormat(MaskSize maskSize)
        {
            switch (maskSize)
            {
                case MaskSize.Bits8:
                    return GraphicsFormat.R8_UNorm;
                case MaskSize.Bits16:
                    return GraphicsFormat.R16_UNorm;
                case MaskSize.Bits24:
                case MaskSize.Bits32:
                    return GraphicsFormat.R32_SFloat;
                default:
                    throw new NotImplementedException();
            }
        }

        public static uint ToValidRenderingLayers(uint renderingLayers)
        {
            if (UltraRenderPipelineGlobalSettings.instance)
            {
                uint validRenderingLayers = RenderingLayerMask.GetDefinedRenderingLayersCombinedMaskValue();
                return validRenderingLayers & renderingLayers;
            }
            return renderingLayers;
        }

        public static void SetupProperties(CommandBuffer cmd, MaskSize maskSize) 
        { 
            SetupProperties(CommandBufferHelpers.GetRasterCommandBuffer(cmd), maskSize);
        }

        internal static void SetupProperties(RasterCommandBuffer cmd, MaskSize maskSize)
        {
            int bits = GetBits(maskSize);

            // Pre-computes properties used for packing/unpacking
            uint maxInt = bits != 32 ? (1u << bits) - 1u : uint.MaxValue;
            float rcpMaxInt = Unity.Mathematics.math.rcp(maxInt);
            cmd.SetGlobalInteger(ShaderPropertyId.renderingLayerMaxInt, (int)maxInt);
            cmd.SetGlobalFloat(ShaderPropertyId.renderingLayerRcpMaxInt, rcpMaxInt);
        }

        public static bool RequireRenderingLayers(UltraRenderer ultraRenderer, List<BaseNode> renderNodes, int msaaSampleCount, out Event combinedEvent, out MaskSize combinedMaskSize)
        {
            RenderingMode renderingMode = ultraRenderer.renderingMode;
            bool accurateGBufferNormals = ultraRenderer.accurateGbufferNormals;
            return RequireRenderingLayers(renderingMode, renderNodes, accurateGBufferNormals, msaaSampleCount, out combinedEvent, out combinedMaskSize);
        }

        internal static bool RequireRenderingLayers(RenderingMode renderingMode, List<BaseNode> renderNodes, bool accurateGbufferNormals, int msaaSampleCount, out Event combinedEvent, out MaskSize combinedMaskSize)
        {
            combinedEvent = Event.Opaque;
            combinedMaskSize = MaskSize.Bits8;

            bool isDeferred = (renderingMode & (RenderingMode.Deferred | RenderingMode.DeferredPlus | RenderingMode.DeferredCluster)) != 0;
            bool result = false;

            if (UltraRenderPipelineGlobalSettings.instance)
            {
                int count = RenderingLayerMask.GetRenderingLayerCount();
                MaskSize maskSize = GetMaskSize(count);
                combinedMaskSize = Combine(combinedMaskSize, maskSize);
            }

            return result;
        }

        static MaskSize GetMaskSize(int bits)
        {
            int bytes = (bits + 7) / 8;
            switch (bytes)
            {
                case 0:
                    return MaskSize.Bits8;
                case 1:
                    return MaskSize.Bits8;
                case 2:
                    return MaskSize.Bits16;
                case 3:
                    return MaskSize.Bits24;
                case 4:
                    return MaskSize.Bits32;
                default:
                    return MaskSize.Bits32;
            }
        }

        static int GetBits(MaskSize maskSize)
        {
            switch (maskSize)
            {
                case MaskSize.Bits8:
                    return 8;
                case MaskSize.Bits16:
                    return 16;
                case MaskSize.Bits24:
                    return 24;
                case MaskSize.Bits32:
                    return 32;
                default:
                    throw new NotImplementedException();
            }
        }

        static Event Combine(Event a, Event b)
        {
            return (Event)Mathf.Min((int)a, (int)b);
        }

        static MaskSize Combine(MaskSize a, MaskSize b)
        {
            return (MaskSize)Mathf.Max((int)a, (int)b);
        }
    }
}
