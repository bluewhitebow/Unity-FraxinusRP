using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Buffers written by deferred cluster classify (<see cref="DeferredPass"/> + DeferredLit compute)
    /// for optional debug visualization of tile lists.
    /// </summary>
    public class DeferredClassifyDebugBuffersData : ContextItem
    {
        internal BufferHandle dispatchIndirectBuffer;
        internal BufferHandle tileListBuffer;
        internal int numTilesX;
        internal int numTilesY;
        internal bool isValid;

        public override void Reset()
        {
            dispatchIndirectBuffer = BufferHandle.nullHandle;
            tileListBuffer = BufferHandle.nullHandle;
            numTilesX = 0;
            numTilesY = 0;
            isValid = false;
        }
    }
}
