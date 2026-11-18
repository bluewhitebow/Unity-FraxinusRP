using System;

namespace Fraxinus.GPUBatcher
{
    public class GPUInstancerTreePrototype : GPUInstancerPrototype
    {
        public int prototypeIndex;
        public bool isApplyRotation = true;
        public bool isApplyPrefabScale = true;
        public bool isApplyTerrainHeight = true;
    }
}
