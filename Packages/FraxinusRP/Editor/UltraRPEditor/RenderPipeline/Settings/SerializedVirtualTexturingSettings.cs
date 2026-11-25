using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    public sealed class SerializedVirtualTexturingSettings
    {
        public SerializedProperty root;

        public SerializedProperty streamingCpuCacheSizeInMegaBytes;
        public SerializedProperty streamingGpuCacheSettings;
        public SerializedProperty streamingMipPreloadTexturesPerFrame;
        public SerializedProperty streamingPreloadMipCount;

        public SerializedVirtualTexturingSettings(SerializedProperty root)
        {
            this.root = root;

            streamingCpuCacheSizeInMegaBytes = root.Find((VirtualTexturingSettingsSRP s) => s.streamingCpuCacheSizeInMegaBytes);
            streamingGpuCacheSettings = root.Find((VirtualTexturingSettingsSRP s) => s.streamingGpuCacheSettings);
            streamingMipPreloadTexturesPerFrame = root.Find((VirtualTexturingSettingsSRP s) => s.streamingMipPreloadTexturesPerFrame);
            streamingPreloadMipCount = root.Find((VirtualTexturingSettingsSRP s) => s.streamingPreloadMipCount);
        }
    }
}