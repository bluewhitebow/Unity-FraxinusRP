using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    [Serializable]
    internal sealed class APRTBakedCell
    {
        public int index;
        public Vector3Int position;
        public int probeCount;
        public TextAsset asset;
        [SerializeReference] internal APRTStreamableAsset streamableAsset;
        public Vector3[] probePositions = Array.Empty<Vector3>();
        // World axes: +X, -X, +Y, -Y, +Z, -Z. RGB is mean hit albedo; W is sky visibility.
        public Vector4[] axisData = Array.Empty<Vector4>();
        public Vector4[] visibilityMoments = Array.Empty<Vector4>();
        public float[] probeValidity = Array.Empty<float>();
        // Per probe, four rows (L0, X, Y, Z) of nine coefficients map the
        // environment's radiance polynomial to visible irradiance / pi.
        public float[] skyIrradianceTransfer = Array.Empty<float>();
        // Optional colored transfer, with the same 4 x 9 layout. Includes the
        // direct scalar transfer in RGB plus one static diffuse sky reflection.
        public Vector3[] skyIrradianceTransferRGB = Array.Empty<Vector3>();
        public Vector3Int[] brickPositions = Array.Empty<Vector3Int>();
        public int[] brickSubdivisions = Array.Empty<int>();
        // 4 x 4 x 4 probes per brick, x fastest. Indices refer to this cell's probePositions.
        public int[] brickProbeIndices = Array.Empty<int>();
    }

    [Serializable]
    internal sealed class APRTBakedData : ProbeVolumeBakedData
    {
        internal const int k_CurrentVersion = 1;
        internal const int k_SkyIrradianceTransferCoefficientCount = 36;
        [SerializeField] internal int formatVersion;
        // Existing version-one arrays use the diffuse transfer convention.
        [SerializeField] internal APRTLightingModel lightingModel = APRTLightingModel.DiffuseTransfer;
        [SerializeField] internal List<APRTBakedCell> cells = new();
        [SerializeField] internal int sampleCount;
        [SerializeField] internal bool hasLeakReductionData;
        // Optional extension of version one: old captures retain their original
        // directional response, but need a new bake for directional sky transfer.
        [SerializeField] internal bool hasSkyIrradianceTransfer;
        // Optional version-one extension. The scalar direct matrix is retained
        // for compatibility and comparison; runtime uses this combined matrix.
        [SerializeField] internal bool hasSkyBounceTransfer;
        [SerializeField] internal Cubemap sceneSkyTexture;
        [SerializeField] internal bool regularGrid;
        [SerializeField] internal Vector3 regularGridOrigin;
        [SerializeField] internal Quaternion regularGridRotation = Quaternion.identity;
        [SerializeField] internal Vector3Int regularGridDimensions;
        [SerializeField] internal float regularGridSpacing;
        // APRTV's sampler uses its configured bounds, which may extend beyond
        // the last captured probe. Zero preserves the captured-lattice extent.
        [SerializeField] internal Vector3 regularGridSamplingSize;
        [SerializeField] internal Vector3 regularGridSamplingOrigin;
        [SerializeField] internal int[] regularGridProbeIndices = Array.Empty<int>();
        [SerializeField] internal Matrix4x4[] samplingDomains = Array.Empty<Matrix4x4>();
        [SerializeField] internal int bakedRegionSettingsHash;
        [SerializeField] internal List<APRTLightingScenario> lightingScenarios = new();
        [NonSerialized] internal int lightingScenarioRevision;
        public override string backendId => APRTProbeReferenceVolumeBackend.backendId;
        internal int totalProbeCount
        {
            get
            {
                long count = 0;
                if (cells != null) foreach (var cell in cells) count += cell?.probeCount ?? 0;
                return (int)Math.Min(int.MaxValue, count);
            }
        }

        // Hash the exact ordered placement, not the capture's changing material
        // transport. Frozen scenarios can bake different geometry and lighting.
        IEnumerable<int> LayoutValues()
        {
            yield return formatVersion;
            yield return (int)lightingModel;
            yield return bakedMinDistanceBetweenProbes.GetHashCode();
            yield return bakedSimplificationLevels;
            for (int i = 0; i < 3; ++i) yield return minCellPosition[i];
            for (int i = 0; i < 3; ++i) yield return maxCellPosition[i];
            for (int i = 0; i < 3; ++i) yield return bakedProbeOffset[i].GetHashCode();
            for (int i = 0; i < 3; ++i) yield return globalBounds.center[i].GetHashCode();
            for (int i = 0; i < 3; ++i) yield return globalBounds.size[i].GetHashCode();
            yield return regularGrid ? 1 : 0;
            for (int i = 0; i < 3; ++i) yield return regularGridOrigin[i].GetHashCode();
            for (int i = 0; i < 4; ++i) yield return regularGridRotation[i].GetHashCode();
            for (int i = 0; i < 3; ++i) yield return regularGridDimensions[i];
            yield return regularGridSpacing.GetHashCode();
            for (int i = 0; i < 3; ++i) yield return regularGridSamplingSize[i].GetHashCode();
            for (int i = 0; i < 3; ++i) yield return regularGridSamplingOrigin[i].GetHashCode();
            yield return regularGridProbeIndices?.Length ?? -1;
            if (regularGridProbeIndices != null) foreach (int index in regularGridProbeIndices) yield return index;
            yield return samplingDomains?.Length ?? -1;
            if (samplingDomains != null) foreach (var matrix in samplingDomains)
                for (int i = 0; i < 16; ++i) yield return matrix[i].GetHashCode();
            yield return cells?.Count ?? -1;
            if (cells == null) yield break;
            foreach (var cell in cells)
            {
                yield return cell == null ? 0 : 1;
                if (cell == null) continue;
                yield return cell.index;
                for (int i = 0; i < 3; ++i) yield return cell.position[i];
                yield return cell.probeCount;
                yield return cell.probePositions?.Length ?? -1;
                if (cell.probePositions != null) foreach (var position in cell.probePositions)
                    for (int i = 0; i < 3; ++i) yield return position[i].GetHashCode();
                yield return cell.brickPositions?.Length ?? -1;
                if (cell.brickPositions != null) foreach (var position in cell.brickPositions)
                    for (int i = 0; i < 3; ++i) yield return position[i];
                yield return cell.brickSubdivisions?.Length ?? -1;
                if (cell.brickSubdivisions != null) foreach (int level in cell.brickSubdivisions) yield return level;
                yield return cell.brickProbeIndices?.Length ?? -1;
                if (cell.brickProbeIndices != null) foreach (int index in cell.brickProbeIndices) yield return index;
            }
        }

        internal int GetLayoutHash()
        {
            unchecked
            {
                int hash = 17;
                foreach (int value in LayoutValues()) hash = hash * 23 + value;
                return hash;
            }
        }

        internal bool HasSameProbeLayout(APRTBakedData other)
        {
            if (other == null) return false;
            using var first = LayoutValues().GetEnumerator();
            using var second = other.LayoutValues().GetEnumerator();
            while (first.MoveNext())
                if (!second.MoveNext() || first.Current != second.Current) return false;
            return !second.MoveNext();
        }

        internal bool HasBakedLightingScenario(string scenario)
        {
            if (!HasValidSharedData()) return false;
            var entry = FindLightingScenario(scenario);
            int layoutHash = GetLayoutHash();
            if (entry?.HasValidBakedLighting(totalProbeCount, layoutHash) != true) return false;
            if (entry.streamableAssets == null || entry.streamableAssets.Count == 0) return true;
            if (entry.streamableAssets.Count != cells.Count) return false;
            var assets = new Dictionary<int, APRTStreamableAsset>();
            foreach (var descriptor in entry.streamableAssets)
            {
                if (descriptor == null || assets.ContainsKey(descriptor.cellIndex)) return false;
                assets.Add(descriptor.cellIndex, descriptor);
            }
            foreach (var cell in cells)
                if (!assets.TryGetValue(cell.index, out var descriptor) ||
                    !descriptor.IsValid(cell.index, cell.probeCount, layoutHash, APRTStreamableDataFlags.Scenario)) return false;
            return true;
        }

        internal void SetBakedLightingScenario(APRTLightingScenario scenario)
        {
            if (scenario == null || !HasValidSharedData())
                throw new ArgumentException("Invalid APRT baked scenario; the previous data was retained.", nameof(scenario));
            var snapshot = scenario.Clone();
            snapshot.bakedLayoutHash = GetLayoutHash();
            if (!snapshot.HasValidBakedLighting(totalProbeCount, snapshot.bakedLayoutHash))
                throw new ArgumentException("Invalid APRT baked scenario SH; the previous data was retained.", nameof(scenario));
            if (snapshot.streamableAssets != null && snapshot.streamableAssets.Count > 0)
            {
                if (snapshot.streamableAssets.Count != cells.Count)
                    throw new ArgumentException("APRT streamed scenario cells do not match the shared layout.", nameof(scenario));
                var descriptors = new Dictionary<int, APRTStreamableAsset>();
                foreach (var asset in snapshot.streamableAssets) descriptors.Add(asset.cellIndex, asset);
                foreach (var cell in cells)
                    if (!descriptors.TryGetValue(cell.index, out var asset) ||
                        !asset.IsValid(cell.index, cell.probeCount, snapshot.bakedLayoutHash, APRTStreamableDataFlags.Scenario))
                        throw new ArgumentException("APRT streamed scenario cells do not match the shared layout.", nameof(scenario));
            }
            SetLightingScenario(snapshot);
        }
        internal APRTLightingScenario FindLightingScenario(string scenario)
        {
            if (lightingScenarios != null)
                foreach (var entry in lightingScenarios)
                    if (entry != null && entry.name == scenario && entry.IsValid()) return entry;
            return null;
        }

        internal void SetLightingScenario(APRTLightingScenario scenario)
        {
            if (scenario == null || !scenario.IsValid())
                throw new ArgumentException("The APRT lighting scenario is invalid; the previous capture was retained.", nameof(scenario));
            var snapshot = scenario.Clone();
            lightingScenarios ??= new List<APRTLightingScenario>();
            for (int i = 0; i < lightingScenarios.Count; ++i)
                if (lightingScenarios[i]?.name == snapshot.name)
                {
                    lightingScenarios[i] = snapshot;
                    ++lightingScenarioRevision;
                    return;
                }
            lightingScenarios.Add(snapshot);
            ++lightingScenarioRevision;
        }

        internal bool HasLightingScenario(string scenario) => FindLightingScenario(scenario) != null ||
            ((lightingScenarios == null || lightingScenarios.Count == 0) &&
                (string.IsNullOrEmpty(scenario) || scenario == TProbeReferenceVolumeBase.defaultLightingScenario));

        internal override bool HasBakedData(string scenario, bool supportLightingScenarios) =>
            HasValidSharedData() && (!supportLightingScenarios || HasBakedLightingScenario(scenario ?? TProbeReferenceVolumeBase.defaultLightingScenario));
        internal override bool HasValidSharedData()
        {
            if (formatVersion != k_CurrentVersion || (lightingModel != APRTLightingModel.DiffuseTransfer && lightingModel != APRTLightingModel.APRTV) || cells == null || cells.Count == 0 ||
                !Finite(bakedMinDistanceBetweenProbes) || bakedMinDistanceBetweenProbes <= 0 ||
                bakedSimplificationLevels < 0 || bakedSimplificationLevels >= ProbeBrickIndex.kMaxSubdivisionLevels ||
                !Finite(bakedProbeOffset) || !Finite(globalBounds.center) || !Finite(globalBounds.size) || samplingDomains == null ||
                (hasSkyBounceTransfer && !hasSkyIrradianceTransfer))
                return false;
            long count = 0, bricks = 0;
            int layoutHash = GetLayoutHash();
            var cellIndices = new HashSet<int>();
            foreach (var cell in cells)
            {
                if (cell == null || !cellIndices.Add(cell.index) || cell.probeCount <= 0 ||
                    cell.probePositions == null || cell.probePositions.Length != cell.probeCount ||
                    cell.brickPositions == null || cell.brickSubdivisions == null || cell.brickProbeIndices == null ||
                    cell.brickPositions.Length != cell.brickSubdivisions.Length ||
                    cell.brickProbeIndices.LongLength != (long)cell.brickPositions.Length * 64)
                    return false;
                bool external = cell.streamableAsset != null;
                if (external && !cell.streamableAsset.IsValid(cell.index, cell.probeCount, layoutHash,
                    APRTDiskData.SharedFlags(hasSkyIrradianceTransfer, hasSkyBounceTransfer))) return false;
                // Hydrated resident cells can retain their disk descriptor. A
                // partial array never bypasses payload validation.
                bool metadataOnly = external && (cell.axisData == null || cell.axisData.Length == 0) &&
                    (cell.visibilityMoments == null || cell.visibilityMoments.Length == 0) &&
                    (cell.probeValidity == null || cell.probeValidity.Length == 0) &&
                    (cell.skyIrradianceTransfer == null || cell.skyIrradianceTransfer.Length == 0) &&
                    (cell.skyIrradianceTransferRGB == null || cell.skyIrradianceTransferRGB.Length == 0);
                if (!metadataOnly && (cell.axisData == null || cell.axisData.LongLength != (long)cell.probeCount * 6 ||
                    cell.visibilityMoments == null || cell.visibilityMoments.LongLength != (long)cell.probeCount * 6 ||
                    cell.probeValidity == null || cell.probeValidity.Length != cell.probeCount ||
                    (hasSkyIrradianceTransfer && (cell.skyIrradianceTransfer == null ||
                        cell.skyIrradianceTransfer.LongLength != (long)cell.probeCount * k_SkyIrradianceTransferCoefficientCount)) ||
                    (hasSkyBounceTransfer && (cell.skyIrradianceTransferRGB == null ||
                        cell.skyIrradianceTransferRGB.LongLength != (long)cell.probeCount * k_SkyIrradianceTransferCoefficientCount))))
                    return false;
                count += cell.probeCount;
                bricks += cell.brickPositions.Length;
                if (count > int.MaxValue / 6 || bricks > int.MaxValue / 64 ||
                    (hasSkyIrradianceTransfer && count > int.MaxValue / k_SkyIrradianceTransferCoefficientCount)) return false;
                for (int i = 0; i < cell.probeCount; ++i)
                    if (!Finite(cell.probePositions[i]) || (!metadataOnly && (!Finite(cell.probeValidity[i]) ||
                        cell.probeValidity[i] < 0 || cell.probeValidity[i] > 1))) return false;
                for (int i = 0; !metadataOnly && i < cell.axisData.Length; ++i)
                {
                    var axis = cell.axisData[i];
                    var moment = cell.visibilityMoments[i];
                    if (!Finite(axis) || axis.x < 0 || axis.y < 0 || axis.z < 0 || axis.w < 0 || axis.w > 1 ||
                        !Finite(moment) || moment.x < 0 || moment.y < 0) return false;
                }
                if (!metadataOnly && hasSkyIrradianceTransfer)
                    foreach (var coefficient in cell.skyIrradianceTransfer)
                        // Transfer basis terms have either sign; only non-finite
                        // values indicate a corrupt integral.
                        if (!Finite(coefficient)) return false;
                if (!metadataOnly && hasSkyBounceTransfer)
                    foreach (var coefficient in cell.skyIrradianceTransferRGB)
                        if (!Finite(coefficient)) return false;
                for (int i = 0; i < cell.brickPositions.Length; ++i)
                {
                    int level = cell.brickSubdivisions[i];
                    if (level < 0 || level > bakedSimplificationLevels) return false;
                    int size = (int)Mathf.Pow(3, level);
                    var p = cell.brickPositions[i];
                    if (p.x % size != 0 || p.y % size != 0 || p.z % size != 0) return false;
                }
                foreach (var index in cell.brickProbeIndices)
                    if (index < 0 || index >= cell.probeCount) return false;
            }
            foreach (var domain in samplingDomains)
            {
                for (int i = 0; i < 16; ++i) if (!Finite(domain[i])) return false;
                if (Mathf.Abs(domain.determinant) < 1e-10f) return false;
            }
            if (!regularGrid) return bricks > 0;
            if (regularGridDimensions.x < 1 || regularGridDimensions.y < 1 || regularGridDimensions.z < 1 ||
                (lightingModel == APRTLightingModel.APRTV && regularGridSamplingSize == Vector3.zero &&
                    (regularGridDimensions.x < 2 || regularGridDimensions.y < 2 || regularGridDimensions.z < 2)) ||
                !Finite(regularGridOrigin) || !Finite(regularGridSpacing) || regularGridSpacing <= 0 ||
                !Finite(regularGridSamplingSize) || !Finite(regularGridSamplingOrigin) || (regularGridSamplingSize != Vector3.zero &&
                    (regularGridSamplingSize.x <= 0 || regularGridSamplingSize.y <= 0 || regularGridSamplingSize.z <= 0)) ||
                !Finite(new Vector4(regularGridRotation.x, regularGridRotation.y, regularGridRotation.z, regularGridRotation.w)) ||
                Quaternion.Dot(regularGridRotation, regularGridRotation) < .0001f || regularGridProbeIndices == null ||
                regularGridProbeIndices.LongLength != (long)regularGridDimensions.x * regularGridDimensions.y * regularGridDimensions.z)
                return false;
            foreach (var index in regularGridProbeIndices)
                if (index < 0 || index >= count) return false;
            return true;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        internal override bool CheckCompatibleCellLayout(ProbeVolumeBakingSet bakingSet) =>
            HasValidSharedData() && base.CheckCompatibleCellLayout(bakingSet);
        internal override bool HasValidScenarioData(string scenario, TProbeReferenceVolumeBase volume) =>
            HasBakedLightingScenario(scenario);
        internal override bool HasSupportData() => HasValidSharedData();
        internal override void CleanupStreamableAssets() { }

#if UNITY_EDITOR
        internal override string streamingAssetsPath => APRTDiskData.streamingAssetsDirectory;
        internal override int scenarioCount
        {
            get
            {
                if (!HasValidSharedData()) return 0;
                int count = 0;
                if (lightingScenarios != null) foreach (var scenario in lightingScenarios)
                    if (scenario != null && HasBakedLightingScenario(scenario.name)) ++count;
                return count;
            }
        }
        internal override IEnumerable<ProbeVolumeBuildResource> GetBuildResources(IReadOnlyList<RenderPipelineAsset> renderPipelineAssets) =>
            Array.Empty<ProbeVolumeBuildResource>();
        internal override bool TryGetScenarioHash(string scenario, out int sceneHash)
        {
            sceneHash = 0;
            if (!HasBakedLightingScenario(scenario)) return false;
            sceneHash = GetLayoutHash();
            return true;
        }
        internal override long GetDiskSizeOfSharedData()
        {
            if (!HasValidSharedData()) return 0;
            long bytes = 0;
            if (cells != null) foreach (var cell in cells)
                if (cell != null) bytes += (cell.streamableAsset != null ? cell.streamableAsset.byteCount + (long)cell.probeCount * 12 : (long)cell.probeCount * (12 + 6 * 32 + 4 +
                    (hasSkyIrradianceTransfer ? k_SkyIrradianceTransferCoefficientCount * sizeof(float) : 0) +
                    (hasSkyBounceTransfer ? k_SkyIrradianceTransferCoefficientCount * 3 * sizeof(float) : 0))) +
                    (long)(cell.brickPositions?.Length ?? 0) * (16 + 64 * 4);
            return bytes;
        }
        internal override long GetDiskSizeOfScenarioData(string scenario)
        {
            var entry = FindLightingScenario(scenario);
            if (entry == null) return 0;
            if (entry.streamableAssets != null && entry.streamableAssets.Count > 0)
            {
                long bytes = (4 + 4 + 9) * 16;
                foreach (var descriptor in entry.streamableAssets) bytes += descriptor?.byteCount ?? 0;
                return bytes;
            }
            return (long)(4 + 4 + 9 +
                (entry.bakedDirectionalSH?.Length ?? 0) + (entry.bakedSkyIrradianceSH?.Length ?? 0)) * 4 * sizeof(float);
        }
        internal override void ClearAssets()
        {
            // Explicit clearing drops only this backend's references; it never deletes assets.
            sceneSkyTexture = null;
            lightingScenarios = new List<APRTLightingScenario>();
            cells = new List<APRTBakedCell>();
            sampleCount = 0;
            hasLeakReductionData = false;
            hasSkyIrradianceTransfer = false;
            hasSkyBounceTransfer = false;
            regularGrid = false;
            regularGridOrigin = Vector3.zero;
            regularGridRotation = Quaternion.identity;
            regularGridDimensions = Vector3Int.zero;
            regularGridSpacing = 0;
            regularGridSamplingSize = Vector3.zero;
            regularGridSamplingOrigin = Vector3.zero;
            regularGridProbeIndices = Array.Empty<int>();
            samplingDomains = Array.Empty<Matrix4x4>();
            formatVersion = 0;
            bakedRegionSettingsHash = 0;
            ++lightingScenarioRevision;
            lightingModel = APRTLightingModel.DiffuseTransfer;
        }
        internal override void RemoveScenario(string scenario)
        {
            lightingScenarios?.RemoveAll(entry => entry?.name == scenario);
            ++lightingScenarioRevision;
        }
        internal override void RenameScenario(string scenario, string newName, string assetName)
        {
            if (lightingScenarios == null) return;
            foreach (var entry in lightingScenarios)
                if (entry != null && entry.name == scenario) entry.name = newName;
            ++lightingScenarioRevision;
        }
        internal override void EnsureScenarioAssetNames(string assetName) { }
#endif
    }
}
