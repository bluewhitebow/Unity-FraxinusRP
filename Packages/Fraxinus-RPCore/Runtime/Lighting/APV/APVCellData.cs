using System;
using System.Collections.Generic;
using Unity.Collections;
using Brick = UnityEngine.Rendering.ProbeBrickIndex.Brick;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// APV 算法的 Cell 数据实现
    /// 完全复用现有的 CellData 结构
    /// </summary>
    public class APVCellData : ICellData<APVCellData.PerScenarioData>
    {
        // === 共享数据（所有场景通用） ===

        /// <summary>
        /// 有效性邻居掩码数据
        /// </summary>
        public NativeArray<byte> validityNeighMaskData;

        /// <summary>
        /// 天空遮蔽数据 L0L1
        /// </summary>
        public NativeArray<ushort> skyOcclusionDataL0L1 { get; internal set; }

        /// <summary>
        /// 天空遮蔽方向索引
        /// </summary>
        public NativeArray<byte> skyShadingDirectionIndices { get; internal set; }

        // === 每场景数据 ===

        /// <summary>
        /// APV 算法的每场景数据结构
        /// </summary>
        public struct PerScenarioData : IPerScenarioData
        {
            // L0/L1 数据
            public NativeArray<ushort> shL0L1RxData;
            public NativeArray<byte> shL1GL1RyData;
            public NativeArray<byte> shL1BL1RzData;

            // 可选 L2 数据
            public NativeArray<byte> shL2Data_0;
            public NativeArray<byte> shL2Data_1;
            public NativeArray<byte> shL2Data_2;
            public NativeArray<byte> shL2Data_3;

            // 4 unorm per probe, 1 for each occluded light
            public NativeArray<byte> probeOcclusion;
        }

        private Dictionary<string, PerScenarioData> _scenarios = new Dictionary<string, PerScenarioData>();
        public Dictionary<string, PerScenarioData> scenarios => _scenarios;

        IEnumerable<KeyValuePair<string, IPerScenarioData>> ICellData.EnumerateScenarioEntries()
        {
            foreach (var kvp in _scenarios)
                yield return new KeyValuePair<string, IPerScenarioData>(kvp.Key, kvp.Value);
        }

        bool ICellData.TryGetScenarioEntry(string name, out IPerScenarioData data)
        {
            if (_scenarios.TryGetValue(name, out var typed))
            {
                data = typed;
                return true;
            }

            data = default;
            return false;
        }

        bool ICellData.RemoveScenarioEntry(string name)
        {
            if (!_scenarios.TryGetValue(name, out var value))
                return false;
            CleanupPerScenarioData(value);
            _scenarios.Remove(name);
            return true;
        }

        bool ICellData.RenameScenarioEntry(string oldName, string newName)
        {
            if (!_scenarios.TryGetValue(oldName, out var value))
                return false;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return true;
            if (_scenarios.ContainsKey(newName))
                return false;
            _scenarios.Remove(oldName);
            _scenarios.Add(newName, value);
            return true;
        }

        public NativeArray<Vector3> probePositions { get; set; }

        public NativeArray<float> touchupVolumeInteraction { get; set; }

        public NativeArray<Vector3> offsetVectors { get; set; }

        public NativeArray<float> validity { get; set; }

        public NativeArray<byte> layer { get; set; }

        public NativeArray<Brick> bricks { get; set; }

        // === ICellData 接口实现 ===

        /// <inheritdoc />
        public bool TryGetPerScenario(string scenarioName, out PerScenarioData data)
        {
            return _scenarios.TryGetValue(scenarioName, out data);
        }

        /// <summary>
        /// 清理每场景数据
        /// </summary>
        /// <param name="data">要清理的每场景数据</param>
        public void CleanupPerScenarioData(IPerScenarioData data)
        {
            if (data is PerScenarioData apvData)
            {
                if (apvData.shL0L1RxData.IsCreated)
                {
                    apvData.shL0L1RxData.Dispose();
                    apvData.shL1GL1RyData.Dispose();
                    apvData.shL1BL1RzData.Dispose();
                }

                if (apvData.shL2Data_0.IsCreated)
                {
                    apvData.shL2Data_0.Dispose();
                    apvData.shL2Data_1.Dispose();
                    apvData.shL2Data_2.Dispose();
                    apvData.shL2Data_3.Dispose();
                }

                if (apvData.probeOcclusion.IsCreated)
                {
                    apvData.probeOcclusion.Dispose();
                }
            }
        }

        /// <summary>
        /// 清理所有数据
        /// </summary>
        /// <param name="cleanScenarioList">是否清理场景列表</param>
        public void Cleanup(bool cleanScenarioList)
        {
            // GPU 数据。如果使用磁盘流式传输，则不存在。
            if (validityNeighMaskData.IsCreated)
            {
                validityNeighMaskData.Dispose();
                validityNeighMaskData = default;

                foreach (var scenario in _scenarios.Values)
                    CleanupPerScenarioData(scenario);
            }

            // 当使用磁盘流式传输时，我们不想清除此列表，因为它是唯一知道场景可用位置的地方
            // 这是可以的，因为场景数据在这里没有实例化。
            if (cleanScenarioList)
                _scenarios.Clear();

            // Bricks 和支持数据。使用磁盘流式传输时可能不存在。
            if (bricks.IsCreated)
            {
                bricks.Dispose();
                bricks = default;
            }

            if (skyOcclusionDataL0L1.IsCreated)
            {
                skyOcclusionDataL0L1.Dispose();
                skyOcclusionDataL0L1 = default;
            }

            if (skyShadingDirectionIndices.IsCreated)
            {
                skyShadingDirectionIndices.Dispose();
                skyShadingDirectionIndices = default;
            }

            if (probePositions.IsCreated)
            {
                probePositions.Dispose();
                probePositions = default;
            }

            if (touchupVolumeInteraction.IsCreated)
            {
                touchupVolumeInteraction.Dispose();
                touchupVolumeInteraction = default;
            }

            if (validity.IsCreated)
            {
                validity.Dispose();
                validity = default;
            }

            if (layer.IsCreated)
            {
                layer.Dispose();
                layer = default;
            }

            if (offsetVectors.IsCreated)
            {
                offsetVectors.Dispose();
                offsetVectors = default;
            }
        }
    }
}
