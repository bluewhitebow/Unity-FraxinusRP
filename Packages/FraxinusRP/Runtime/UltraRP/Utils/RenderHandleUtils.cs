using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class RTHandleResourcePool
    {
        protected Dictionary<int, SortedList<int, (RTHandle resource, int frameIndex)>> _resourcePool = new Dictionary<int, SortedList<int, (RTHandle resource, int frameIndex)>>();
        protected List<int> _removeList = new List<int>(32);

        protected static int s_currentStaleResourceCount = 0;
        // Keep stale resources alive for 3 frames
        protected static int s_staleResourceLifetime = 3;
        // Store max 32 rtHandles
        // 1080p * 32bpp * 32 = 265.4mb
        protected static int s_staleResourceMaxCapacity = 32;

        internal int staleResourceCapacity
        {
            get { return s_staleResourceMaxCapacity; }
            set
            {
                if (s_staleResourceMaxCapacity != value)
                {
                    s_staleResourceMaxCapacity = value;
                    Cleanup();
                }
            }
        }

        internal bool AddResourceToPool(in TextureDesc texDesc, RTHandle resource, int currentFrameIndex)
        {
            if (s_currentStaleResourceCount >= s_staleResourceMaxCapacity)
                return false;

            int hashCode = GetHashCodeWithNameHash(texDesc);
            if (!_resourcePool.TryGetValue(hashCode, out var list))
            {
                // Init list with max capacity to avoid runtime GC.Alloc when calling list.Add(resize list)
                list = new SortedList<int, (RTHandle resource, int frameIndex)>(s_staleResourceMaxCapacity);
                _resourcePool.Add(hashCode, list);
            }

            list.Add(resource.GetInstanceID(), (resource, currentFrameIndex));
            s_currentStaleResourceCount++;

            return true;
        }

        internal bool TryGetResource(in TextureDesc texDesc, out RTHandle resource, bool usepool = true)
        {
            int hashCode = GetHashCodeWithNameHash(texDesc);
            if (usepool && _resourcePool.TryGetValue(hashCode, out SortedList<int, (RTHandle resource, int frameIndex)> list) && list.Count > 0)
            {
                resource = list.Values[list.Count - 1].resource;
                list.RemoveAt(list.Count - 1); // O(1) since it's the last element.
                s_currentStaleResourceCount--;
                return true;
            }

            resource = null;
            return false;
        }

        internal void Cleanup()
        {
            foreach (var kvp in _resourcePool)
            {
                foreach (var res in kvp.Value)
                    res.Value.resource.Release();
            }

            _resourcePool.Clear();

            s_currentStaleResourceCount = 0;
        }

        static protected bool ShouldReleaseResource(int lastUsedFrameIndex, int currentFrameIndex)
        {
            // We need to have a delay of a few frames before releasing resources for good.
            // Indeed, when having multiple off-screen cameras, they are rendered in a separate SRP render call and thus with a different frame index than main camera
            // This causes texture to be deallocated/reallocated every frame if the two cameras don't need the same buffers.
            return (lastUsedFrameIndex + s_staleResourceLifetime) < currentFrameIndex;
        }

        internal void PurgeUnusedResources(int currentFrameIndex)
        {
            // Update the frame index for the lambda. Static because we don't want to capture.
            _removeList.Clear();

            foreach (var kvp in _resourcePool)
            {
                // WARNING: No foreach here. Sorted list GetEnumerator generates garbage...
                var list = kvp.Value;
                var keys = list.Keys;
                var values = list.Values;
                for (int i = 0; i < list.Count; ++i)
                {
                    var value = values[i];
                    if (ShouldReleaseResource(value.frameIndex, currentFrameIndex))
                    {
                        value.resource.Release();
                        _removeList.Add(keys[i]);
                        s_currentStaleResourceCount--;
                    }
                }

                foreach (var key in _removeList)
                    list.Remove(key);
            }
        }

        internal void LogDebugInfo()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("RTHandleResourcePool for frame {0}, Total stale resources {1}", Time.frameCount, s_currentStaleResourceCount);
            sb.AppendLine();

            foreach (var kvp in _resourcePool)
            {
                var list = kvp.Value;
                var keys = list.Keys;
                var values = list.Values;
                for (int i = 0; i < list.Count; ++i)
                {
                    var value = values[i];
                    sb.AppendFormat("Resrouce in pool: Name {0} Last active frame index {1} Size {2} x {3} x {4}",
                        value.resource.name,
                        value.frameIndex,
                        value.resource.rt.descriptor.width,
                        value.resource.rt.descriptor.height,
                        value.resource.rt.descriptor.volumeDepth
                        );
                    sb.AppendLine();
                }
            }

            Debug.Log(sb.ToString());
        }

        internal int GetHashCodeWithNameHash(in TextureDesc texDesc)
        {
            int hashCode = texDesc.GetHashCode();
            hashCode = hashCode * 23 + texDesc.name.GetHashCode();

            return hashCode;
        }

        internal static TextureDesc CreateTextureDesc(RenderTextureDescriptor desc,
            TextureSizeMode textureSizeMode = TextureSizeMode.Explicit, int anisoLevel = 1, float mipMapBias = 0,
            FilterMode filterMode = FilterMode.Point, TextureWrapMode wrapMode = TextureWrapMode.Clamp, string name = "")
        {
            TextureDesc rgDesc = new TextureDesc(desc.width, desc.height);
            rgDesc.sizeMode = textureSizeMode;
            rgDesc.slices = desc.volumeDepth;
            rgDesc.depthBufferBits = (DepthBits)desc.depthBufferBits;
            rgDesc.colorFormat = desc.graphicsFormat;
            rgDesc.filterMode = filterMode;
            rgDesc.wrapMode = wrapMode;
            rgDesc.dimension = desc.dimension;
            rgDesc.enableRandomWrite = desc.enableRandomWrite;
            rgDesc.useMipMap = desc.useMipMap;
            rgDesc.autoGenerateMips = desc.autoGenerateMips;
            rgDesc.isShadowMap = desc.shadowSamplingMode != ShadowSamplingMode.None;
            rgDesc.anisoLevel = anisoLevel;
            rgDesc.mipMapBias = mipMapBias;
            rgDesc.msaaSamples = (MSAASamples)desc.msaaSamples;
            rgDesc.bindTextureMS = desc.bindMS;
            rgDesc.useDynamicScale = desc.useDynamicScale;
            rgDesc.memoryless = RenderTextureMemoryless.None;
            rgDesc.vrUsage = VRTextureUsage.None;
            rgDesc.name = name;

            return rgDesc;
        }
    }
}