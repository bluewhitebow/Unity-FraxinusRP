using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    // APRT keeps its baked transfer arrays in APRTBakedData. The cell adapter is
    // intentionally metadata-only so common streaming code never receives null,
    // while the runtime GPU loader owns the packed arrays and their lifetime.
    internal sealed class APRTCellData : ICellData
    {
        internal APRTBakedCell bakedCell { get; }
        internal APRTCellData(APRTBakedCell cell) => bakedCell = cell;
        readonly Dictionary<string, IPerScenarioData> m_Scenarios = new();
        public IEnumerable<KeyValuePair<string, IPerScenarioData>> EnumerateScenarioEntries() => m_Scenarios;
        public bool TryGetScenarioEntry(string name, out IPerScenarioData data) => m_Scenarios.TryGetValue(name, out data);
        public bool RemoveScenarioEntry(string name) => m_Scenarios.Remove(name);
        public bool RenameScenarioEntry(string oldName, string newName)
        {
            if (!m_Scenarios.TryGetValue(oldName, out var data) || m_Scenarios.ContainsKey(newName)) return false;
            m_Scenarios.Remove(oldName); m_Scenarios.Add(newName, data); return true;
        }
        public void CleanupPerScenarioData(IPerScenarioData data) { }
        public void Cleanup(bool cleanScenarioList) { if (cleanScenarioList) m_Scenarios.Clear(); }
    }

    // Resolve current inline transport while rejecting the retired payload format.
    internal sealed class APRTProbeVolumeBakingDataFormat : IProbeVolumeBakingDataFormat
    {
        public string backendId => APRTProbeReferenceVolumeBackend.backendId;
        public ProbeVolumeBakedData CreateBakedData() => new APRTBakedData();
        public ICellData CreateCellData(ProbeVolumeBakingSet bakingSet, int cellIndex, bool streamingOnly)
        {
            var data = bakingSet != null ? bakingSet.GetBakedData(backendId) as APRTBakedData : null;
            if (data == null || !data.HasValidSharedData()) return null;
            foreach (var cell in data.cells) if (cell.index == cellIndex) return new APRTCellData(cell);
            return null;
        }
        public bool ResolveSharedCellData(in ProbeVolumeBakingDataResolveContext context)
        {
            var set = context.bakingSet;
            var data = set != null ? set.GetBakedData(backendId) as APRTBakedData : null;
            if (data == null || !data.HasValidSharedData() || context.cellIndices == null) return false;
            var cellsByIndex = new Dictionary<int, APRTBakedCell>();
            foreach (var cell in data.cells) cellsByIndex.Add(cell.index, cell);
            foreach (var index in context.cellIndices) if (!cellsByIndex.ContainsKey(index)) return false;
            var map = set.GetCellDataMap(backendId);
            foreach (var index in context.cellIndices)
                if (!map.ContainsKey(index)) map.Add(index, new APRTCellData(cellsByIndex[index]));
            return true;
        }
        public bool ResolveScenarioCellData(in ProbeVolumeBakingDataResolveContext context)
        {
            var data = context.bakingSet != null ? context.bakingSet.GetBakedData(backendId) as APRTBakedData : null;
            if (data == null || !data.HasValidSharedData() || context.cellIndices == null) return false;
            var map = context.bakingSet.GetCellDataMap(backendId);
            foreach (var index in context.cellIndices) if (!map.ContainsKey(index)) return false;
            return true;
        }
        public int GetChunkGPUMemory(ProbeVolumeBakingSet bakingSet, TProbeReferenceVolumeBase referenceVolume)
        {
            var data = bakingSet != null ? bakingSet.GetBakedData(backendId) as APRTBakedData : null;
            if (data == null || !data.HasValidSharedData()) return 0;
            long bytes = 0;
            foreach (var cell in data.cells)
                bytes += (long)cell.probeCount * (12 + 6 * 32 + 4 + 8 * 16 +
                    (data.hasSkyBounceTransfer ? 36 * 12 : data.hasSkyIrradianceTransfer ? 36 * 4 : 0)) +
                    (long)cell.brickPositions.Length * (16 + 64 * 4);
            return (int)Math.Min(int.MaxValue, bytes);
        }
    }
}
