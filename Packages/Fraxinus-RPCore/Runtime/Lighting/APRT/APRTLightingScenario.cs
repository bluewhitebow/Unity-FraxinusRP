using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    // Scenarios share probe placement and store independent baked SH outputs.
    // Captured inputs retain bake provenance and support older preset assets.
    [Serializable]
    internal sealed class APRTLightingScenario
    {
        [SerializeField] internal string name;
        [SerializeField] internal Vector4[] colors = new Vector4[4];
        [SerializeField] internal Vector4[] directions = new Vector4[4];
        [SerializeField] internal Vector4[] skyRadiance = new Vector4[9];
        [SerializeField] internal Vector4[] bakedDirectionalSH = Array.Empty<Vector4>();
        [SerializeField] internal Vector4[] bakedSkyIrradianceSH = Array.Empty<Vector4>();
        [SerializeField] internal int bakedLayoutHash;
        [SerializeField] internal List<APRTStreamableAsset> streamableAssets = new();

        internal bool HasValidBakedLighting(int probeCount, int layoutHash)
        {
            if (!IsValid() || bakedLayoutHash != layoutHash) return false;
            if (streamableAssets != null && streamableAssets.Count > 0)
            {
                bool metadataOnly = (bakedDirectionalSH == null || bakedDirectionalSH.Length == 0) &&
                    (bakedSkyIrradianceSH == null || bakedSkyIrradianceSH.Length == 0);
                if (!metadataOnly) return false;
                long count = 0;
                var indices = new HashSet<int>();
                foreach (var asset in streamableAssets)
                {
                    if (asset == null || !indices.Add(asset.cellIndex) ||
                        !asset.IsValid(asset.cellIndex, asset.probeCount, layoutHash, APRTStreamableDataFlags.Scenario)) return false;
                    count += asset.probeCount;
                }
                return count == probeCount;
            }
            if (bakedDirectionalSH == null ||
                bakedSkyIrradianceSH == null || bakedDirectionalSH.LongLength != (long)probeCount * 4 ||
                bakedSkyIrradianceSH.LongLength != (long)probeCount * 4) return false;
            foreach (var coefficient in bakedDirectionalSH)
                if (!Finite(coefficient)) return false;
            foreach (var coefficient in bakedSkyIrradianceSH)
                if (!Finite(coefficient)) return false;
            return true;
        }

        internal bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(name) || colors == null || colors.Length != 4 ||
                directions == null || directions.Length != 4 || skyRadiance == null || skyRadiance.Length != 9)
                return false;
            for (int i = 0; i < colors.Length; ++i)
            {
                var color = colors[i];
                var direction = directions[i];
                if (!Finite(color) || color.x < 0 || color.y < 0 || color.z < 0 || color.w < 0 || color.w > 1 ||
                    !Finite(direction) || (color.w >= .5f &&
                        (new Vector3(direction.x, direction.y, direction.z)).sqrMagnitude < .0001f))
                    return false;
            }
            foreach (var coefficient in skyRadiance)
                if (!Finite(coefficient)) return false;
            return true;
        }

        internal APRTLightingScenario Clone() => new APRTLightingScenario
        {
            name = name,
            colors = colors == null ? null : (Vector4[])colors.Clone(),
            directions = directions == null ? null : (Vector4[])directions.Clone(),
            skyRadiance = skyRadiance == null ? null : (Vector4[])skyRadiance.Clone(),
            bakedDirectionalSH = bakedDirectionalSH == null ? null : (Vector4[])bakedDirectionalSH.Clone(),
            bakedSkyIrradianceSH = bakedSkyIrradianceSH == null ? null : (Vector4[])bakedSkyIrradianceSH.Clone(),
            bakedLayoutHash = bakedLayoutHash,
            streamableAssets = CloneStreamableAssets()
        };

        List<APRTStreamableAsset> CloneStreamableAssets()
        {
            var result = new List<APRTStreamableAsset>();
            if (streamableAssets != null) foreach (var asset in streamableAssets) result.Add(asset?.Clone());
            return result;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    }
}
