namespace UnityEngine.Rendering
{
    internal interface IProbeVolumeConstantRuntimeResources
    {
        void Initialize();
        void Cleanup();
        void GetRuntimeResources<TResources>(ref TResources rr) where TResources : struct, IRuntimeResources;
    }
}
