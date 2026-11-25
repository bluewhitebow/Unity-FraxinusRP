using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.RenderGraphModule
{
    abstract class IRenderGraphResourcePool
    {
        public abstract void PurgeUnusedResources(int currentFrameIndex);
        public abstract void Cleanup();
        public abstract void CheckFrameAllocation(bool onException, int frameIndex);
        public abstract void QuarantineAllResources(int currentFrameIndex);
        public abstract void DeferAllResources(int currentFrameIndex, GraphicsFence completionFence);
        public abstract bool HasUnfencedResources();
        public abstract void PromoteUnfencedResources(GraphicsFence completionFence);
        // Exception cleanup may happen after an async compute submission.  A
        // resource returned from that path must remain unavailable until the
        // queue has passed its completion fence.  Keep the original overload
        // for callers which do not have an in-flight fence.
        public virtual void CheckFrameAllocation(bool onException, int frameIndex, GraphicsFence completionFence, bool hasCompletionFence)
        {
            CheckFrameAllocation(onException, frameIndex);
        }
        public abstract void LogResources(RenderGraphLogger logger);
    }

    abstract class RenderGraphResourcePool<Type> : IRenderGraphResourcePool where Type : class
    {
        // Dictionary tracks resources by hash and stores resources with same hash in a List (list instead of a stack because we need to be able to remove stale allocations, potentially in the middle of the stack).
        // The list needs to be sorted otherwise you could get inconsistent resource usage from one frame to another.

        protected Dictionary<int, SortedList<int, (Type resource, int frameIndex)>> m_ResourcePool = new Dictionary<int, SortedList<int, (Type resource, int frameIndex)>>();

        // This list allows us to determine if all resources were correctly released in the frame when validity checks are enabled.
        // This is useful to warn in case of user error or avoid leaks when a render graph execution error occurs for example.
        List<(int, Type)> m_FrameAllocatedResources = new List<(int, Type)>();

        // Resources recovered while an async queue may still be using them are
        // quarantined here.  They are moved to the regular pool only after the
        // completion fence passes, so a subsequent graph cannot alias an
        // in-flight RTHandle/GraphicsBuffer.
        struct DeferredResource
        {
            public int hash;
            public Type resource;
            public int frameIndex;
            public GraphicsFence fence;
        }

        List<DeferredResource> m_DeferredResources = new List<DeferredResource>();

        // A completion fence is normally available when exception cleanup
        // runs.  If creating/submitting the async marker itself fails, however,
        // there is no trustworthy GPU completion point.  Keep those resources
        // out of the reusable pool until a caller can provide such a fence.
        // This is deliberately a separate list instead of using a default
        // GraphicsFence: querying a default fence can report passed on some
        // backends and would make an in-flight allocation immediately reusable.
        struct UnfencedResource
        {
            public int hash;
            public Type resource;
            public int frameIndex;
        }

        List<UnfencedResource> m_UnfencedResources = new List<UnfencedResource>();
        const int kStaleResourceLifetime = 10;

        // Release the GPU resource itself
        protected abstract void ReleaseInternalResource(Type res);
        protected abstract string GetResourceName(in Type res);
        protected abstract long GetResourceSize(in Type res);
        protected abstract string GetResourceTypeName();
        protected abstract int GetSortIndex(Type res);

        static bool HasFencePassed(in GraphicsFence fence)
        {
            try
            {
                return fence.passed;
            }
            catch
            {
                // A fence can be queried before the graphics device has been
                // initialized (or on a backend without fence support).  Keep
                // the resource quarantined in that case; releasing it early
                // is worse than retaining it until Cleanup.
                return false;
            }
        }

        void ProcessDeferredResources()
        {
            for (int i = m_DeferredResources.Count - 1; i >= 0; --i)
            {
                var deferred = m_DeferredResources[i];
                if (!HasFencePassed(deferred.fence))
                    continue;

                // Remove before returning the resource to the pool.  This
                // makes a re-entrant allocation unable to process the same
                // deferred entry twice.
                m_DeferredResources.RemoveAt(i);
                try
                {
                    ReleaseResource(deferred.hash, deferred.resource, deferred.frameIndex);
                }
                catch (System.Exception e)
                {
                    // Pool bookkeeping must not break a later render graph.
                    // The resource is no longer in the deferred list, so make
                    // sure its native allocation is still reclaimed.
                    try
                    {
                        ReleaseInternalResource(deferred.resource);
                    }
                    catch (System.Exception releaseException)
                    {
                        Debug.LogException(releaseException);
                    }
                    Debug.LogException(e);
                }
            }
        }

        public void ReleaseResource(int hash, Type resource, int currentFrameIndex)
        {
            if (!m_ResourcePool.TryGetValue(hash, out var list))
            {
                list = new SortedList<int, (Type, int)>();
                m_ResourcePool.Add(hash, list);
            }

            list.Add(GetSortIndex(resource), (resource, currentFrameIndex));
        }

        public void ReleaseResourceAfterFence(int hash, Type resource, int currentFrameIndex, GraphicsFence completionFence)
        {
            if (resource == null)
                return;

            // An exception can observe a resource after normal bookkeeping
            // already returned the same object to the reusable pool. Remove
            // that entry before publishing the deferred copy, otherwise the
            // next graph could alias it while the GPU is still using it.
            foreach (var kvp in m_ResourcePool)
            {
                var list = kvp.Value;
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (object.ReferenceEquals(list.Values[i].resource, resource))
                        list.RemoveAt(i);
                }
            }

            // If this resource was conservatively quarantined before a fence
            // became available, upgrade that entry in place rather than
            // retaining it twice (which could otherwise release the native
            // allocation twice when the fence passes).
            for (int i = 0; i < m_UnfencedResources.Count; ++i)
            {
                if (object.ReferenceEquals(m_UnfencedResources[i].resource, resource))
                {
                    m_UnfencedResources.RemoveAt(i);
                    break;
                }
            }

            // Avoid retaining the same native resource twice if an exception
            // is observed by both the pass and graph-level cleanup paths.
            for (int i = 0; i < m_DeferredResources.Count; ++i)
            {
                if (m_DeferredResources[i].hash == hash &&
                    object.ReferenceEquals(m_DeferredResources[i].resource, resource))
                    return;
            }

            m_DeferredResources.Add(new DeferredResource
            {
                hash = hash,
                resource = resource,
                frameIndex = currentFrameIndex,
                fence = completionFence,
            });
        }

        public override bool HasUnfencedResources()
        {
            return m_UnfencedResources.Count != 0;
        }

        // A successfully submitted marker on the Background queue is ordered
        // after the work which caused these resources to be quarantined. Move
        // them behind that fence so normal pool processing can eventually make
        // them reusable instead of retaining them for the pool's lifetime.
        public override void PromoteUnfencedResources(GraphicsFence completionFence)
        {
            for (int i = m_UnfencedResources.Count - 1; i >= 0; --i)
            {
                var unfenced = m_UnfencedResources[i];

                bool alreadyDeferred = false;
                for (int deferredIndex = 0; deferredIndex < m_DeferredResources.Count; ++deferredIndex)
                {
                    if (object.ReferenceEquals(m_DeferredResources[deferredIndex].resource, unfenced.resource))
                    {
                        alreadyDeferred = true;
                        break;
                    }
                }

                if (!alreadyDeferred)
                {
                    // Add first and remove second. If list growth throws, the
                    // unfenced entry remains the owner and cannot be reused.
                    m_DeferredResources.Add(new DeferredResource
                    {
                        hash = unfenced.hash,
                        resource = unfenced.resource,
                        frameIndex = unfenced.frameIndex,
                        fence = completionFence,
                    });
                }

                m_UnfencedResources.RemoveAt(i);
            }
        }

        // Keep a resource unavailable when exception cleanup has no valid
        // completion fence.  The caller must have detached the resource from
        // its RenderGraphResource wrapper; this pool entry is then the owner
        // of the managed/native handle until it can be upgraded with a fence
        // or the pool itself is discarded.
        public void QuarantineResource(int hash, Type resource, int currentFrameIndex)
        {
            if (resource == null)
                return;

            for (int i = 0; i < m_UnfencedResources.Count; ++i)
            {
                if (object.ReferenceEquals(m_UnfencedResources[i].resource, resource))
                    return;
            }

            for (int i = 0; i < m_DeferredResources.Count; ++i)
            {
                if (object.ReferenceEquals(m_DeferredResources[i].resource, resource))
                    return;
            }

            // A partially unwound pass can reach this method after its normal
            // release bookkeeping already put the object in the pool.  Remove
            // that exact object before quarantining it so it cannot be aliased
            // by a later allocation.
            foreach (var kvp in m_ResourcePool)
            {
                var list = kvp.Value;
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (object.ReferenceEquals(list.Values[i].resource, resource))
                        list.RemoveAt(i);
                }
            }

            m_UnfencedResources.Add(new UnfencedResource
            {
                hash = hash,
                resource = resource,
                frameIndex = currentFrameIndex,
            });
        }

        // Move every currently reusable allocation into the no-fence
        // quarantine.  This is required when an exception occurs after an
        // async submission but marker creation fails: resources released by
        // earlier passes are already in m_ResourcePool and would otherwise be
        // aliased by the next graph even though no completion point exists.
        public override void QuarantineAllResources(int currentFrameIndex)
        {
            foreach (var kvp in m_ResourcePool)
            {
                var list = kvp.Value;
                foreach (var value in list.Values)
                {
                    var resource = value.resource;
                    if (resource == null)
                        continue;

                    bool alreadyQuarantined = false;
                    for (int i = 0; i < m_UnfencedResources.Count; ++i)
                    {
                        if (object.ReferenceEquals(m_UnfencedResources[i].resource, resource))
                        {
                            alreadyQuarantined = true;
                            break;
                        }
                    }
                    if (!alreadyQuarantined)
                    {
                        for (int i = 0; i < m_DeferredResources.Count; ++i)
                        {
                            if (object.ReferenceEquals(m_DeferredResources[i].resource, resource))
                            {
                                alreadyQuarantined = true;
                                break;
                            }
                        }
                    }

                    if (!alreadyQuarantined)
                    {
                        m_UnfencedResources.Add(new UnfencedResource
                        {
                            hash = kvp.Key,
                            resource = resource,
                            frameIndex = currentFrameIndex,
                        });
                    }
                }
            }

            m_ResourcePool.Clear();
        }

        // Move currently reusable allocations behind a completion fence during
        // exception recovery.  A resource may already have reached its normal
        // last-use list before a later pass throws; its wrapper is then reset
        // and the live-resource scan cannot find it.  Deferring the pool entries
        // here ensures the fence protects those allocations as well.
        public override void DeferAllResources(int currentFrameIndex, GraphicsFence completionFence)
        {
            foreach (var kvp in m_ResourcePool)
            {
                var list = kvp.Value;
                foreach (var value in list.Values)
                {
                    var resource = value.resource;
                    if (resource == null)
                        continue;

                    bool alreadyTracked = false;
                    for (int i = 0; i < m_DeferredResources.Count; ++i)
                    {
                        if (object.ReferenceEquals(m_DeferredResources[i].resource, resource))
                        {
                            alreadyTracked = true;
                            break;
                        }
                    }
                    if (!alreadyTracked)
                    {
                        for (int i = 0; i < m_UnfencedResources.Count; ++i)
                        {
                            if (object.ReferenceEquals(m_UnfencedResources[i].resource, resource))
                            {
                                alreadyTracked = true;
                                break;
                            }
                        }
                    }

                    if (!alreadyTracked)
                    {
                        m_DeferredResources.Add(new DeferredResource
                        {
                            hash = kvp.Key,
                            resource = resource,
                            frameIndex = currentFrameIndex,
                            fence = completionFence,
                        });
                    }
                }
            }

            m_ResourcePool.Clear();
        }

        public bool TryGetResource(int hashCode, out Type resource)
        {
            // Deferred resources only exist after an execution exception.
            // Keep the normal allocation path to a single predictable branch;
            // ProcessDeferredResources contains fence queries and exception
            // handling which prevents the larger method from being inlined.
            if (m_DeferredResources.Count != 0)
                ProcessDeferredResources();

            if (m_ResourcePool.TryGetValue(hashCode, out SortedList<int, (Type resource, int frameIndex)> list) && list.Count > 0)
            {
                var index = list.Count - 1;
                resource = list.Values[index].resource;
                list.RemoveAt(index); // O(1) since it's the last element.
                return true;
            }

            resource = null;
            return false;
        }

        public override void Cleanup()
        {
            // Cleanup is terminal: after RenderGraph unregisters itself there
            // is no later EndFrame/TryGetResource call which could poll these
            // lists.  Retaining an unpassed (or unfenced) entry here therefore
            // becomes a permanent native-resource leak.  Match the reference
            // pool's teardown contract and release every owned resource. Unity
            // keeps the native allocation alive for already submitted GPU work
            // when RTHandle/GraphicsBuffer ownership is released.
            for (int i = m_DeferredResources.Count - 1; i >= 0; --i)
            {
                var deferred = m_DeferredResources[i];
                try
                {
                    ReleaseInternalResource(deferred.resource);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
                m_DeferredResources.RemoveAt(i);
            }

            for (int i = m_UnfencedResources.Count - 1; i >= 0; --i)
            {
                var unfenced = m_UnfencedResources[i];
                try
                {
                    ReleaseInternalResource(unfenced.resource);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
                m_UnfencedResources.RemoveAt(i);
            }

            foreach (var kvp in m_ResourcePool)
            {
                foreach (var res in kvp.Value)
                {
                    ReleaseInternalResource(res.Value.resource);
                }
            }

            m_ResourcePool.Clear();
            m_FrameAllocatedResources.Clear();
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public void RegisterFrameAllocation(int hash, Type value)
        {
            if (RenderGraph.enableValidityChecks && hash != -1)
                m_FrameAllocatedResources.Add((hash, value));
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public void UnregisterFrameAllocation(int hash, Type value)
        {
            if (RenderGraph.enableValidityChecks && hash != -1)
                m_FrameAllocatedResources.Remove((hash, value));
        }

        public override void CheckFrameAllocation(bool onException, int frameIndex)
        {
            CheckFrameAllocation(onException, frameIndex, default(GraphicsFence), false);
        }

        public override void CheckFrameAllocation(bool onException, int frameIndex, GraphicsFence completionFence, bool hasCompletionFence)
        {
            if (m_DeferredResources.Count != 0)
                ProcessDeferredResources();
#if DEVELOPMENT_BUILD || UNITY_EDITOR // conditional not working with override
            if (RenderGraph.enableValidityChecks)
            {
                // In case of exception we need to release all resources to the pool to avoid leaking.
                // If it's not an exception then it's a user error so we need to log the problem.
                if (m_FrameAllocatedResources.Count != 0)
                {
                    string logMessage = "";
                    if (!onException)
                        logMessage = $"RenderGraph: Not all resources of type {GetResourceTypeName()} were released. This can be caused by a resources being allocated but never read by any pass.";

                    foreach (var value in m_FrameAllocatedResources)
                    {
                        if (!onException)
                            logMessage = $"{logMessage}\n\t{GetResourceName(value.Item2)}";
                        if (onException && hasCompletionFence)
                            ReleaseResourceAfterFence(value.Item1, value.Item2, frameIndex, completionFence);
                        else if (onException)
                            // No completion point means the GPU may still be
                            // using the allocation (for example when the
                            // async marker submission failed).  Do not put it
                            // in the reusable pool; detach it into the
                            // no-fence quarantine instead.
                            QuarantineResource(value.Item1, value.Item2, frameIndex);
                        else
                            ReleaseResource(value.Item1, value.Item2, frameIndex);
                    }

                    Debug.LogWarning(logMessage);
                }

                // If an error occurred during execution, it's expected that textures are not all released so we clear the tracking list.
                m_FrameAllocatedResources.Clear();
            }
#endif
        }

        struct ResourceLogInfo
        {
            public string name;
            public long size;
        }

        public override void LogResources(RenderGraphLogger logger)
        {
            List<ResourceLogInfo> allocationList = new List<ResourceLogInfo>();
            foreach (var kvp in m_ResourcePool)
            {
                foreach (var res in kvp.Value)
                {
                    allocationList.Add(new ResourceLogInfo { name = GetResourceName(res.Value.resource), size = GetResourceSize(res.Value.resource) });
                }
            }

            logger.LogLine($"== {GetResourceTypeName()} Resources ==");

            allocationList.Sort((a, b) => a.size < b.size ? 1 : -1);
            int index = 0;
            float total = 0;
            foreach (var element in allocationList)
            {
                float size = element.size / (1024.0f * 1024.0f);
                total += size;
                logger.LogLine($"[{index++:D2}]\t[{size:0.00} MB]\t{element.name}");
            }

            logger.LogLine($"\nTotal Size [{total:0.00}]");
        }

        static List<int> s_ToRemoveList = new List<int>(32);

        public override void PurgeUnusedResources(int currentFrameIndex)
        {
            if (m_DeferredResources.Count != 0)
                ProcessDeferredResources();

            foreach (var kvp in m_ResourcePool)
            {
                s_ToRemoveList.Clear();

                var list = kvp.Value;
                var keys = list.Keys;
                var values = list.Values;
                for (int i = 0; i < list.Count; ++i)
                {
                    var value = values[i];
                    var key = keys[i];

                    // When a resource hasn't been used for a few frames, we release it for good to reduce memory footprint.
                    // We wait a few frames because when having multiple off-screen cameras,
                    // they are rendered in a separate SRP render call and thus with a different frame index than main camera
                    // This causes texture to be deallocated/reallocated every frame if the two cameras don't need the same buffers.
                    if (value.frameIndex + kStaleResourceLifetime < currentFrameIndex)
                    {
                        ReleaseInternalResource(value.resource);
                        // Adding stale resource to the remove list
                        s_ToRemoveList.Add(key);
                    }
                }

                // Removing the stale resource from the pool
                for (int j = 0; j < s_ToRemoveList.Count; ++j)
                    list.Remove(s_ToRemoveList[j]);
            }
        }
    }
}
