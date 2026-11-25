using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    internal struct GPUCacheSettingSRP
    {
        /// <summary>
        ///   <para>Format of the cache these settings are applied to.</para>
        /// </summary>
        public GraphicsFormat format;
        /// <summary>
        ///   <para>Size in MegaBytes of the cache created with these settings.</para>
        /// </summary>
        public uint sizeInMegaBytes;
    }

    [Serializable]
    internal sealed class VirtualTexturingSettingsSRP
    {
        public int streamingCpuCacheSizeInMegaBytes = 256;
        public int streamingMipPreloadTexturesPerFrame = 0;
        public int streamingPreloadMipCount = 1;
        public List<GPUCacheSettingSRP> streamingGpuCacheSettings = new List<GPUCacheSettingSRP>() { new GPUCacheSettingSRP() { format = GraphicsFormat.None, sizeInMegaBytes = 128 } };
    }
}

