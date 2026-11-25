using System;
using System.Collections.Generic;
using System.Diagnostics;
using Chunk = UnityEngine.Rendering.ProbeBrickPoolBase.BrickChunkAlloc;

namespace UnityEngine.Rendering
{
    [Serializable]
    internal struct IndirectionEntryInfo
    {
        public Vector3Int positionInBricks;
        public int minSubdiv;
        public Vector3Int minBrickPos;
        public Vector3Int maxBrickPosPlusOne;
        public bool hasMinMax; // should be removed, only kept for migration
        public bool hasOnlyBiggerBricks; // True if it has only bricks that are bigger than the entry itself
    }

    [Serializable]
    internal class CellDesc
    {
        public Vector3Int position;
        public int index;
        public int probeCount;
        public int minSubdiv;
        public int indexChunkCount;
        public int shChunkCount;
        public int bricksCount;

        // This is data that is generated at bake time to not having to re-analyzing the content of the cell for the indirection buffer.
        // This is not technically part of the descriptor of the cell but it needs to be here because it's computed at bake time and needs
        // to be serialized with the rest of the cell.
        public IndirectionEntryInfo[] indirectionEntryInfo;

        public override string ToString()
        {
            return $"Index = {index} position = {position}";
        }
    }

    internal class CellPoolInfo
    {
        public List<Chunk> chunkList = new List<Chunk>();
        public int shChunkCount;

        public void Clear()
        {
            chunkList.Clear();
        }
    }

    internal class CellIndexInfo
    {
        public int[] flatIndicesInGlobalIndirection = null;
        public ProbeBrickIndex.CellIndexUpdateInfo updateInfo;
        public bool indexUpdated;
        public IndirectionEntryInfo[] indirectionEntryInfo;
        public int indexChunkCount;

        public void Clear()
        {
            flatIndicesInGlobalIndirection = null;
            updateInfo = default(ProbeBrickIndex.CellIndexUpdateInfo);
            indexUpdated = false;
            indirectionEntryInfo = null;
        }
    }

    internal class CellBlendingInfo
    {
        public List<Chunk> chunkList = new List<Chunk>();
        public float blendingScore;
        public float blendingFactor;
        public bool blending;

        public void MarkUpToDate() => blendingScore = float.MaxValue;
        public bool IsUpToDate() => blendingScore == float.MaxValue;
        public void ForceReupload() => blendingFactor = -1.0f;
        public bool ShouldReupload() => blendingFactor == -1.0f;
        public void Prioritize() => blendingFactor = -2.0f;
        public bool ShouldPrioritize() => blendingFactor == -2.0f;

        public void Clear()
        {
            chunkList.Clear();
            blendingScore = 0;
            blendingFactor = 0;
            blending = false;
        }
    }

    internal class CellStreamingInfo
    {
        public TProbeReferenceVolumeBase.ICellStreamingRequest request = null;
        public TProbeReferenceVolumeBase.ICellStreamingRequest blendingRequest0 = null;
        public TProbeReferenceVolumeBase.ICellStreamingRequest blendingRequest1 = null;
        public float streamingScore;

        public bool IsStreaming()
        {
            return request != null && request.IsStreaming();
        }

        public bool IsBlendingStreaming()
        {
            return blendingRequest0 != null && blendingRequest0.IsStreaming()
                || blendingRequest1 != null && blendingRequest1.IsStreaming();
        }

        public void Clear()
        {
            request = null;
            blendingRequest0 = null;
            blendingRequest1 = null;
            streamingScore = 0;
        }
    }

    internal class BaseCell
    {
        public CellDesc desc;

        public CellPoolInfo poolInfo = new CellPoolInfo();
        public CellIndexInfo indexInfo = new CellIndexInfo();
        public CellBlendingInfo blendingInfo = new CellBlendingInfo();
        public CellStreamingInfo streamingInfo = new CellStreamingInfo();

        public CellInstancedDebugProbes debugProbes;

        public virtual bool UpdateCellScenarioData(string scenarioName0, string scenarioName1)
        {
            return true;
        }

        public virtual void Clear()
        {
            desc = null;
            poolInfo.Clear();
            indexInfo.Clear();
            blendingInfo.Clear();
            streamingInfo.Clear();

            debugProbes = null;
        }
    }

    /// <summary>
    /// 探针体积运行时 Cell；<typeparamref name="TData"/> 为磁盘/CPU 侧 Cell 数据，<typeparamref name="TScenario"/> 为每场景着色数据。
    /// </summary>
    [DebuggerDisplay("Index = {desc.index} Loaded = {loaded}")]
    internal class Cell<TData, TScenario> : BaseCell, IComparable<Cell<TData, TScenario>>
        where TData : class, ICellData<TScenario>
        where TScenario : struct, IPerScenarioData
    {
        public TData data;

        public int referenceCount = 0;
        public bool loaded; // "Loaded" means the streaming system decided the cell should be loaded. It does not mean it's ready for GPU consumption (because of blending or disk streaming)

        public TScenario scenario0;
        public TScenario scenario1;
        public bool hasTwoScenarios;

        public int CompareTo(Cell<TData, TScenario> other)
        {
            if (streamingInfo.streamingScore < other.streamingInfo.streamingScore)
                return -1;
            else if (streamingInfo.streamingScore > other.streamingInfo.streamingScore)
                return 1;
            else
                return 0;
        }

        public override bool UpdateCellScenarioData(string scenarioName0, string scenarioName1)
        {
            if (!data.TryGetPerScenario(scenarioName0, out scenario0))
                return false;

            hasTwoScenarios = false;

            if (!string.IsNullOrEmpty(scenarioName1))
            {
                if (data.TryGetPerScenario(scenarioName1, out scenario1))
                    hasTwoScenarios = true;
            }

            return true;
        }

        public override void Clear()
        {
            base.Clear();

            data = null;

            referenceCount = 0;
            loaded = false;
            scenario0 = default;
            scenario1 = default;
            hasTwoScenarios = false;
        }
    }
}
