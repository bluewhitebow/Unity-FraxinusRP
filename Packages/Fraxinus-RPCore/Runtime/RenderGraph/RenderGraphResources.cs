using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.RenderGraphModule
{
    // RendererList is a different case so not represented here.
    internal enum RenderGraphResourceType
    {
        Texture = 0,
        Buffer,
        AccelerationStructure,
        Count
    }

    internal struct ResourceHandle : IEquatable<ResourceHandle>
    {
        // Note on handles validity.
        // PassData classes used during render graph passes are pooled and because of that, when users don't fill them completely,
        // they can contain stale handles from a previous render graph execution that could still be considered valid if we only checked the index.
        // In order to avoid using those, we incorporate the execution index in a 16 bits hash to make sure the handle is coming from the current execution.
        // If not, it's considered invalid.
        // We store this validity mask in the upper 16 bits of the index.
        const uint kValidityMask = 0xFFFF0000;
        const uint kIndexMask = 0xFFFF;

        uint m_Value;
        int m_Version; // A freshly created resource always starts at version 0 the first write should bring it to v1
        
        static uint s_CurrentValidBit = 1 << 16;
        static uint s_SharedResourceValidBit = 0x7FFF << 16;

        public int index
        { 
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)(m_Value & kIndexMask); }
        }
        public int iType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (int)type; }
        }
        public int version
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return m_Version; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { m_Version = value; } 
        }
        public RenderGraphResourceType type { get; private set; }

        internal ResourceHandle(int value, RenderGraphResourceType type, bool shared)
        {
            Debug.Assert(value <= 0xFFFF);
            m_Value = ((uint)value & kIndexMask) | (shared ? s_SharedResourceValidBit : s_CurrentValidBit);
            this.type = type;
            this.m_Version = -1;
        }

        internal ResourceHandle(in ResourceHandle h, int version)
        {
            this.m_Value = h.m_Value;
            this.type = h.type;
            this.m_Version = version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid()
        {
            var validity = m_Value & kValidityMask;
            return validity != 0 && (validity == s_CurrentValidBit || validity == s_SharedResourceValidBit);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNull()
        {
            if (index == 0)
            {
                // Make sure everything is zero
                Debug.Assert(m_Value == 0);
                Debug.Assert(m_Version == 0);
                return true;
            }
            return false;
        }

        static public void NewFrame(int executionIndex)
        {
            uint previousValidBit = s_CurrentValidBit;
            // Scramble frame count to avoid collision when wrapping around.
            s_CurrentValidBit = (uint)(((executionIndex >> 16) ^ (executionIndex & 0xffff) * 58546883) << 16);
            // In case the current valid bit is 0, even though perfectly valid, 0 represents an invalid handle, hence we'll
            // trigger an invalid state incorrectly. To account for this, we actually skip 0 as a viable s_CurrentValidBit and
            // start from 1 again.
            // In the same spirit, s_SharedResourceValidBit is reserved for shared textures so we should never use it otherwise
            // resources could be considered valid at frame N+1 (because shared) even though they aren't.
            if (s_CurrentValidBit == 0 || s_CurrentValidBit == s_SharedResourceValidBit)
            {
                // We need to make sure we don't pick the same value twice.
                uint value = 1;
                while (previousValidBit == (value << 16))
                    value++;
                s_CurrentValidBit = (value << 16);
            }
        }

        public bool IsVersioned
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return m_Version >= 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ResourceHandle hdl)
        {
            return hdl.m_Value == this.m_Value && hdl.m_Version == this.m_Version && hdl.type == this.type;
        }
    }

    class IRenderGraphResource
    {
        public bool imported;
        public bool shared;
        public bool sharedExplicitRelease;
        public bool requestFallBack;
        public bool forceRelease;
        public uint writeCount;
        public uint readCount;
        public int cachedHash;
        public int transientPassIndex;
        public int sharedResourceLastFrameUsed;
        public int version;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void Reset(IRenderGraphResourcePool _ = null)
        {
            imported = false;
            shared = false;
            sharedExplicitRelease = false;
            cachedHash = -1;
            transientPassIndex = -1;
            sharedResourceLastFrameUsed = -1;
            requestFallBack = false;
            forceRelease = false;
            writeCount = 0;
            readCount = 0;
            version = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual string GetName()
        {
            return "";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual bool IsCreated()
        {
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void IncrementWriteCount()
        {
            writeCount++;
        }

        // readCount is currently not used in the HDRP Compiler.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void IncrementReadCount()
        {
            readCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual int NewVersion()
        {
            version++;
            return version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual bool NeedsFallBack()
        {
            return requestFallBack && writeCount == 0;
        }

        public virtual void CreatePooledGraphicsResource() { }
        public virtual void CreateGraphicsResource() { }
        public virtual void UpdateGraphicsResource() { }
        public virtual void ReleasePooledGraphicsResource(int frameIndex) { }
        // Variant used while recovering from an execution error.  The
        // resource can be quarantined behind an async-compute fence instead of
        // being made immediately reusable by another render graph.
        public virtual void ReleasePooledGraphicsResource(int frameIndex, GraphicsFence completionFence, bool deferUntilFence)
        {
            ReleasePooledGraphicsResource(frameIndex);
        }

        // Emergency path used when an execution exception leaves no valid GPU
        // completion fence.  Implementations that own a pool should detach the
        // native resource into a quarantine list instead of making it
        // immediately reusable.
        public virtual void QuarantinePooledGraphicsResource(int frameIndex) { }
        public virtual void ReleaseGraphicsResource() { }
        public virtual void LogCreation(RenderGraphLogger logger) { }
        public virtual void LogRelease(RenderGraphLogger logger) { }
        public virtual int GetSortIndex() { return 0; }
        public virtual int GetDescHashCode() { return 0; }
    }

    [DebuggerDisplay("Resource ({GetType().Name}:{GetName()})")]
    abstract class RenderGraphResource<DescType, ResType>
        : IRenderGraphResource
        where DescType : struct
        where ResType : class
    {
        public DescType desc;
        public bool validDesc; // Does the descriptor contain valid data (this is not always the case for imported resources)
        public ResType graphicsResource;

        protected RenderGraphResourcePool<ResType> m_Pool;

        protected RenderGraphResource()
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Reset(IRenderGraphResourcePool pool = null)
        {
            base.Reset();
            m_Pool = pool as RenderGraphResourcePool<ResType>;
            graphicsResource = null;
            validDesc = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsCreated()
        {
            return graphicsResource != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void ReleaseGraphicsResource()
        {
            graphicsResource = null;
        }

        public override void CreatePooledGraphicsResource()
        {
            Debug.Assert(m_Pool != null, "RenderGraphResource: CreatePooledGraphicsResource should only be called for regular pooled resources");

            int hashCode = GetDescHashCode();

            if (graphicsResource != null)
                throw new InvalidOperationException($"RenderGraphResource: Trying to create an already created resource ({GetName()}). Resource was probably declared for writing more than once in the same pass.");

            // If the pool doesn't have any available resource that we can use, we will create one
            // In any case, we will update the graphicsResource name based on the RenderGraph resource name
            // Store the hash before invoking user/graphics allocation code. If
            // allocation or the pool's update callback throws after assigning
            // graphicsResource, exception cleanup can still return the object
            // to the right pool instead of attempting to release it with hash
            // -1 (or leaking it).
            cachedHash = hashCode;

            if (!m_Pool.TryGetResource(hashCode, out graphicsResource))
            {
                CreateGraphicsResource();
            }
            else
            {
                UpdateGraphicsResource();
            }
            m_Pool.RegisterFrameAllocation(cachedHash, graphicsResource);
        }

        public override void ReleasePooledGraphicsResource(int frameIndex)
        {
            ReleasePooledGraphicsResource(frameIndex, default(GraphicsFence), false);
        }

        public override void ReleasePooledGraphicsResource(int frameIndex, GraphicsFence completionFence, bool deferUntilFence)
        {
            if (graphicsResource == null)
                throw new InvalidOperationException($"RenderGraphResource: Tried to release a resource ({GetName()}) that was never created. Check that there is at least one pass writing to it first.");

            // Shared resources don't use the pool
            if (m_Pool != null)
            {
                if (deferUntilFence)
                    m_Pool.ReleaseResourceAfterFence(cachedHash, graphicsResource, frameIndex, completionFence);
                else
                    m_Pool.ReleaseResource(cachedHash, graphicsResource, frameIndex);
                if (cachedHash != -1)
                    m_Pool.UnregisterFrameAllocation(cachedHash, graphicsResource);
            }

            Reset();
        }

        public override void QuarantinePooledGraphicsResource(int frameIndex)
        {
            if (graphicsResource == null)
                throw new InvalidOperationException($"RenderGraphResource: Tried to quarantine a resource ({GetName()}) that was never created. Check that there is at least one pass writing to it first.");

            // Shared/imported resources do not have a pool owner.  They remain
            // owned by the caller; only regular pooled allocations can be
            // safely detached into the pool's quarantine list.
            if (m_Pool != null)
            {
                m_Pool.QuarantineResource(cachedHash, graphicsResource, frameIndex);
                if (cachedHash != -1)
                    m_Pool.UnregisterFrameAllocation(cachedHash, graphicsResource);
            }

            // The pool now owns the resource reference.  Reset the wrapper so
            // stale handles cannot expose it to a subsequent graph recording.
            Reset();
        }
    }
}
