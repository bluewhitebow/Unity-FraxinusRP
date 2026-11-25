namespace UnityEngine.Rendering
{
    internal abstract partial class TProbeReferenceVolumeBase
    {
        public abstract void BindPRVRuntimeResources(CommandBuffer cmdBuffer, bool isProbeVolumeEnabled);
        public abstract bool UpdateShaderVariablesProbeVolumes(CommandBuffer cmd, ProbeVolumesOptions probeVolumeOptions, int taaFrameIndex, bool supportRenderingLayers = false);
    }

    internal abstract partial class TProbeReferenceVolume<TPRV, TData, TPerScenarioData, TCell, TProbeBrickPool, TProbeBrickBlendingPool, TDataLocation>
        : TProbeReferenceVolumeBase
        where TPRV : TProbeReferenceVolume<TPRV, TData, TPerScenarioData, TCell, TProbeBrickPool, TProbeBrickBlendingPool, TDataLocation>, new()
        where TData : class, ICellData<TPerScenarioData>
        where TPerScenarioData : struct, IPerScenarioData
        where TCell : Cell<TData, TPerScenarioData>, new()
        where TProbeBrickPool : TProbeBrickPool<TDataLocation>
        where TProbeBrickBlendingPool : TProbeBrickBlendingPool<TProbeBrickPool, TDataLocation>
        where TDataLocation : struct, ProbeBrickPoolBase.IDataLocation
    {
        protected ComputeBuffer m_EmptyIndexBuffer = null;

    }
}
