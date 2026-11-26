// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using BatchDrawCommandIndirect = Fraxinus.GPUBatcher.Rendering.BatchDrawCommandIndirect;
using BatchDrawCommandFlags = Fraxinus.GPUBatcher.Rendering.IndirectDrawCommandFlags;

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        #region Batch System Fields

        // cached shader property IDs
        private static readonly int s_idO2W = Shader.PropertyToID("unity_ObjectToWorld");
        private static readonly int s_idW2O = Shader.PropertyToID("unity_WorldToObject");
        private static readonly int s_idPrevM = Shader.PropertyToID("unity_MatrixPreviousM");
        private static readonly int s_idShCoeff = Shader.PropertyToID("unity_SHCoefficients");

        private static readonly int[] s_windParamIds = new int[GPUBatcherSpeedTreeWindBridge.maxWindParamsCount];
        private static readonly int[] s_windHistoryParamIds = new int[GPUBatcherSpeedTreeWindBridge.maxWindParamsCount];
        static GPUBatcherRenderer()
        {
            for (int i = 0; i < GPUBatcherSpeedTreeWindBridge.maxWindParamsCount; i++)
            {
                s_windParamIds[i] = Shader.PropertyToID($"DOTS_ST_WindParam{i}");
                s_windHistoryParamIds[i] = Shader.PropertyToID($"DOTS_ST_WindHistoryParam{i}");
            }
        }

        // stable-index batch storage
        private BatchInfo[] _batchSlots;
        private bool[] _batchSlotActive;
        private int _batchSlotHWM;
        private Stack<int> _freeBatchSlots = new();
        private List<int>[] _protoBatchSlots;

        private BatchID _sharedBatchID;
        private bool _sharedBatchIDValid;
        private BatchID[] _speedTreeBatchIDs;

        private NativeArray<BatchDrawCommandIndirect> _cameraDrawCommandTemplate;
        private NativeArray<uint> _cameraIndirectArgsTemplate; // 5 uints per command
        private NativeArray<BatchDrawCommandIndirect> _shadowDrawCommandTemplate;
        private NativeArray<uint> _shadowIndirectArgsTemplate;
        private NativeArray<ShadowCommandFilterInfo> _shadowCommandFilterInfo;

#if ADRENO_DRIVER_WORKAROUND
        // ADRENO DRIVER BUG WORKAROUND!s
        // adreno driver bug only (desktop vulkan is fine). vulkan folds the indirect args' startInstance
        // into unity_InstanceID, so the per-draw bucket offset travels there; other APIs use the cbuffer
        // visibleOffset. mirrors the shader's API branch
        static readonly bool s_indirectViaStartInstance =
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
#endif

        // persistent bucket arrays (written per-proto, read by culling)
        private NativeArray<DrawBucket> _buckets;
        private NativeArray<DrawBucket> _shadowBuckets;
#if UNITY_EDITOR
        private uint[] _camBucketLod;
#endif

        // range templates (rebuilt from RangeSegment[] when _rangesDirty)
        private NativeArray<BatchDrawRange> _cameraDrawRangeTemplate;
        private NativeArray<BatchDrawRange> _shadowDrawRangeTemplate;

        // GPU buffers for argsToBucket (uploaded when _rangesDirty)
        private GraphicsBuffer _argsToBucketBuffer;
        private GraphicsBuffer _shadowArgsToBucketBuffer;

        // counts for culling guards + buffer sizing
        private int _bucketCount;
        private int _drawCommandCount;       // = _cameraDrawCommandTemplate.Length (capacity)
        private int _cameraDrawRangeCount;
        private int _maxVisibleInstances;
        private int _shadowMaxVisibleInstances;
        private int _shadowBucketCount;
        private int _shadowDrawCommandCount; // = _shadowDrawCommandTemplate.Length (capacity)
        private int _shadowDrawRangeCount;

        private const int k_indirectArgsStride = 5 * sizeof(uint);

        // per-prototype bucket offsets and layout params (set by allocators)
        private int[] _cameraBucketOffsets;
        private int[] _shadowBucketOffsets;
        private int _totalCameraBuckets;
        private int _totalShadowBuckets;
        private int[] _protoFlipBucketOffsets;
        private int[] _protoShadowFlipBucketOffsets;
        private int[] _perProtoShadowCrossfadeCascades;
        private int[] _perProtoShadowLodStride;
        private int[] _perProtoMinShadowLOD;
        private uint[] _perProtoPercentageFadeMask;
        private int[] _protoCamBucketCount;
        private int[] _protoShdBucketCount;

        // per-proto visibility allocator state
        private int[] _protoCamVisBase;  // base offset into camera visibility buffer
        private int[] _protoShdVisBase;  // base offset into shadow visibility buffer
        private int[] _protoCamVisSize;  // allocated visibility region size
        private int[] _protoShdVisSize;

        // persistent cache: BRG RegisterMesh/RegisterMaterial IDs
        private Dictionary<Mesh, BatchMeshID> _meshIdCache = new();
        private Dictionary<Material, BatchMaterialID> _materialIdCache = new();
        private Dictionary<BatchMeshID, int> _meshRefCount = new();
        private Dictionary<BatchMaterialID, int> _materialRefCount = new();
        private Dictionary<BatchMeshID, Mesh> _meshIdReverse = new();
        private Dictionary<BatchMaterialID, Material> _materialIdReverse = new();

        // incremental allocators
        private RangeAllocator _camBucketAlloc = new();
        private RangeAllocator _shdBucketAlloc = new();
        private RangeAllocator _camVisAlloc = new();
        private RangeAllocator _shdVisAlloc = new();

        // per-range segment in the flat command array
        private struct RangeSegment
        {
            public int begin;
            public int count;
            public int capacity;
            public int layer;
            public uint renderingLayerMask;
            public MotionVectorGenerationMode motionMode;
            public ShadowCastingMode shadowCastingMode;
        }

        // per-command managed metadata
        private int[] _camCmdToBucket;  // flat for argsToBucket SetData
        private int[] _camCmd2Proto;
        private int[] _camCmdRangeIdx;  // which range segment (-1 = slack/inactive)
        private int[] _camCmdIndexInProtoList;  // position of cmdIdx in _camProtoCommands[proto]
        private int _camCmdCount; // total slots used (active + slack)
        private RangeSegment[] _camRanges;
        private int _camRangeCount;
        private Dictionary<ulong, int> _camRangeMap = new();
        private List<int>[] _camProtoCommands;

        private int[] _shdCmdToBucket;
        private int[] _shdCmd2Proto;
        private int[] _shdCmdRangeIdx;
        private int[] _shdCmdIndexInProtoList;
        private int _shdCmdCount;
        private RangeSegment[] _shdRanges;
        private int _shdRangeCount;
        private Dictionary<ulong, int> _shdRangeMap = new();
        private List<int>[] _shdProtoCommands;

        // pending proto adds (deferred until CachePrototypeBounds populates proto data)
        private readonly List<int> _pendingProtoAdds = new();
        // pending per-proto resizes (HWM growth, first flip)
        private readonly HashSet<int> _pendingProtoResizes = new();
        // set when IncAddPrototypeCommands runs, even if it emits nothing
        private readonly HashSet<int> _materializedProtos = new();
        // when true, IncFlush clears everything and re-adds all protos
        private bool _batchFullRecomputeNeeded;
        // when true, range templates + GPU buffers need rebuild (set on any add/remove/swap)
        private bool _rangesDirty;

        // active command counts (excludes slack gaps)
        private int ActiveCameraCommandCount
        {
            get { int n = 0; for (int i = 0; i < _camRangeCount; i++) n += _camRanges[i].count; return n; }
        }
        private int ActiveShadowCommandCount
        {
            get { int n = 0; for (int i = 0; i < _shdRangeCount; i++) n += _shdRanges[i].count; return n; }
        }

        #endregion

        #region Batch Data Structures

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct GPUSignatureData
        {
            public float3 boundsCenter;
            public float shadowDistanceSq;
            public uint lodScreenSizes01;
            public uint lodScreenSizes23;
            public uint lodScreenSizes45;
            public uint lodScreenSizes67;
            public uint lodFadeWidths01;
            public uint lodFadeWidths23;
            public uint lodFadeWidths45;
            public uint lodFadeWidths67;
            public uint densMinFracFadeDur;
            public uint boundsRadiusLodSize;
            public uint densParam1DensParam2;
            public uint fadeWidthPad;
            public uint cameraBucketOffset;
            public uint shadowBucketOffset;
            public uint flipBucketOffset;
            public uint shadowFlipBucketOffset;
            public uint flags0;
            public uint flags1;

            public static uint PackUNORM16(float lo, float hi)
            {
                uint loU = (uint)(math.saturate(lo) * 65535f + 0.5f) & 0xFFFF;
                uint hiU = (uint)(math.saturate(hi) * 65535f + 0.5f) & 0xFFFF;
                return loU | (hiU << 16);
            }
            public static uint PackHalf2(float lo, float hi)
                => math.f32tof16(lo) | (math.f32tof16(hi) << 16);
            public static uint PackScaledUNORM16(float loVal, float loMax, float hiVal, float hiMax)
            {
                uint loU = (uint)(math.saturate(loVal / loMax) * 65535f + 0.5f) & 0xFFFF;
                uint hiU = (uint)(math.saturate(hiVal / hiMax) * 65535f + 0.5f) & 0xFFFF;
                return loU | (hiU << 16);
            }
            public static uint PackFlags0(int lodCount, bool crossfadeEnabled, bool useAnimatedCrossfade,
                int densityMode, uint lodShadowMask, uint percentageFadeMask,
                int minShadowLOD, int maxShadowCascade, int maxShadowCrossfadeCascade)
            {
                return ((uint)(lodCount - 1) & 0x7)
                    | (crossfadeEnabled ? 1u << 3 : 0u)
                    | (useAnimatedCrossfade ? 1u << 4 : 0u)
                    | (((uint)densityMode & 0x3) << 5)
                    | ((lodShadowMask & 0xFF) << 8)
                    | ((percentageFadeMask & 0xFF) << 16)
                    | (((uint)minShadowLOD & 0x7) << 24)
                    | (((uint)(maxShadowCascade - 1) & 0x3) << 27)
                    | (((uint)maxShadowCrossfadeCascade & 0x7) << 29);
            }
            public static uint PackFlags1(int shadowCrossfadeCascades, int shadowLodStride)
                => ((uint)shadowCrossfadeCascades & 0x7)
                    | (((uint)(shadowLodStride - 1) & 0xF) << 3);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct DrawBucket
        {
            public uint bufferOffset;
            public uint instanceCount;
            public uint maxSlots;
        }

        private struct BatchInfo
        {
            public BatchID batchID;
            public BatchMeshID meshID;
            public BatchMaterialID materialID;
            public int signatureIndex;
            public int lodLevel;
            public int submeshIndex;
        }

        #endregion

        #region Batch Slot Management

        private bool IsSpeedTreeProto(int protoIndex)
            => _protoSpeedTreeSlotIndex != null && (uint)protoIndex < (uint)_protoSpeedTreeSlotIndex.Length
               && _protoSpeedTreeSlotIndex[protoIndex] >= 0;

        private NativeArray<MetadataValue> BuildBatchMetadata(int protoIndex = -1)
        {
            uint totalSlots = (uint)(_poolCapacity * k_poolSize);
            uint byteAddressO2W = 0;
            uint byteAddressW2O = totalSlots * k_sizeOfPackedMatrix;
            uint byteAddressPrevM = totalSlots * 2 * k_sizeOfPackedMatrix;
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
            Rendering.Custom2022IndirectSubmission.SetMatrixLayout(_matrixBuffer, byteAddressO2W, byteAddressW2O, byteAddressPrevM);
#endif
            int speedTreeSlot = IsSpeedTreeProto(protoIndex) ? _protoSpeedTreeSlotIndex[protoIndex] : -1;
            int baseCount = lightProbesEnabled ? 4 : 3;
            int speedTreeCount = speedTreeSlot >= 0 ? GPUBatcherSpeedTreeWindBridge.maxWindParamsCount * 2 : 0;
            var metadata = new NativeArray<MetadataValue>(baseCount + speedTreeCount, Allocator.Temp)
            {
                [0] = new MetadataValue { NameID = s_idO2W, Value = 0x80000000 | byteAddressO2W },
                [1] = new MetadataValue { NameID = s_idW2O, Value = 0x80000000 | byteAddressW2O },
                [2] = new MetadataValue { NameID = s_idPrevM, Value = 0x80000000 | byteAddressPrevM }
            };
            if (lightProbesEnabled)
                metadata[3] = new MetadataValue { NameID = s_idShCoeff, Value = 0x80000000 | (totalSlots * 3 * 12 * sizeof(float)) };
            if (speedTreeSlot >= 0)
            {
                int k = GPUBatcherSpeedTreeWindBridge.maxWindParamsCount;
                uint stBase = (uint)(_speedTreeRegionByteOffset + speedTreeSlot * k_speedTreeParamsPerProto * sizeof(float));
                for (int i = 0; i < k; i++)
                {
                    metadata[baseCount + i] = new MetadataValue { NameID = s_windParamIds[i], Value = stBase + (uint)(i * 16) };
                    metadata[baseCount + k + i] = new MetadataValue { NameID = s_windHistoryParamIds[i], Value = stBase + (uint)(k * 16 + i * 16) };
                }
            }
            return metadata;
        }

        private int AllocBatchSlot()
        {
            if (_freeBatchSlots.Count > 0) return _freeBatchSlots.Pop();
            int slot = _batchSlotHWM++;
            if (_batchSlots == null || slot >= _batchSlots.Length)
            {
                int newLen = math.max(16, _batchSlots?.Length ?? 0);
                while (newLen <= slot) newLen *= 2;
                Array.Resize(ref _batchSlots, newLen);
                Array.Resize(ref _batchSlotActive, newLen);
            }
            return slot;
        }

        private static readonly ProfilerMarker s_markerAddBatch = new ProfilerMarker("BRG.AddBatch");
        private static readonly ProfilerMarker s_markerRegMesh = new ProfilerMarker("BRG.RegisterMesh");
        private static readonly ProfilerMarker s_markerRegMat = new ProfilerMarker("BRG.RegisterMaterial");

        private void EnsureSharedBatchID()
        {
            if (_sharedBatchIDValid || _matrixBuffer == null) return;
            var metadata = BuildBatchMetadata();
            using (s_markerAddBatch.Auto())
                _sharedBatchID = _brg.AddBatch(metadata, _matrixBuffer.bufferHandle);
            metadata.Dispose();
            _sharedBatchIDValid = true;
        }

        private void EnsureSpeedTreeBatchIDs()
        {
            if (_speedTreeSlotHWM == 0) return;
            if (_speedTreeBatchIDs == null)
                _speedTreeBatchIDs = new BatchID[_speedTreeSlotHWM];
            else if (_speedTreeBatchIDs.Length < _speedTreeSlotHWM)
                Array.Resize(ref _speedTreeBatchIDs, _speedTreeSlotHWM);
            for (int p = 0; p < (_protoSpeedTreeSlotIndex?.Length ?? 0); p++)
            {
                int slot = _protoSpeedTreeSlotIndex[p];
                if (slot < 0 || _speedTreeBatchIDs[slot].value != 0) continue;
                var metadata = BuildBatchMetadata(p);
                using (s_markerAddBatch.Auto())
                    _speedTreeBatchIDs[slot] = _brg.AddBatch(metadata, _matrixBuffer.bufferHandle);
                metadata.Dispose();
            }
        }

        private BatchID GetBatchID(int protoIndex)
        {
            if (IsSpeedTreeProto(protoIndex))
                return _speedTreeBatchIDs[_protoSpeedTreeSlotIndex[protoIndex]];
            return _sharedBatchID;
        }

        private void CreateBatchesForPrototype(int protoIndex)
        {
            var proto = _renderSignatures[protoIndex];
            if (proto == null || proto.mesh == null) return;
            EnsureSharedBatchID();
            if (IsSpeedTreeProto(protoIndex)) EnsureSpeedTreeBatchIDs();
            if (_protoBatchSlots == null || protoIndex >= _protoBatchSlots.Length)
            {
                int newLen = math.max(8, _protoBatchSlots?.Length ?? 0);
                while (newLen <= protoIndex) newLen *= 2;
                var old = _protoBatchSlots;
                _protoBatchSlots = new List<int>[newLen];
                if (old != null) Array.Copy(old, _protoBatchSlots, old.Length);
            }
            _protoBatchSlots[protoIndex] ??= new List<int>(4);
            var slots = _protoBatchSlots[protoIndex];
            int lodCount = proto.lodCount;
#if UNITY_EDITOR
            ValidatePrototypeForBRG(protoIndex);
#endif
            for (int lod = 0; lod < lodCount; lod++)
            {
                // empty LODs (no renderers): no batch slot, so no mesh/material registered and no draw command emitted
                Mesh lodMesh = proto.GetLODMeshRaw(lod);
                if (lodMesh == null) continue;
                Material[] lodMaterials = proto.GetLODMaterialsRaw(lod) ?? proto.materials;
                bool newMesh = !_meshIdCache.TryGetValue(lodMesh, out var meshID);
                if (newMesh)
                {
                    using (s_markerRegMesh.Auto()) meshID = _brg.RegisterMesh(lodMesh);
                    _meshIdCache[lodMesh] = meshID;
                    _meshIdReverse[meshID] = lodMesh;
                    _meshRefCount[meshID] = 0;
                }
                int submeshCount = math.min(lodMesh.subMeshCount, lodMaterials.Length);
                for (int sub = 0; sub < submeshCount; sub++)
                {
                    Material mat = lodMaterials[sub];
                    if (mat == null) continue;
                    _meshRefCount[meshID] = _meshRefCount.GetValueOrDefault(meshID) + 1;
                    if (!_materialIdCache.TryGetValue(mat, out var materialID))
                    {
                        using (s_markerRegMat.Auto()) materialID = _brg.RegisterMaterial(mat);
                        _materialIdCache[mat] = materialID;
                        _materialIdReverse[materialID] = mat;
                        _materialRefCount[materialID] = 0;
                    }
                    _materialRefCount[materialID] = _materialRefCount.GetValueOrDefault(materialID) + 1;
                    int slot = AllocBatchSlot();
                    _batchSlots[slot] = new BatchInfo
                    {
                        batchID = GetBatchID(protoIndex), meshID = meshID, materialID = materialID,
                        signatureIndex = protoIndex, lodLevel = lod, submeshIndex = sub
                    };
                    _batchSlotActive[slot] = true;
                    slots.Add(slot);
                }
                if (newMesh && _meshRefCount.GetValueOrDefault(meshID) == 0)
                {
                    _brg.UnregisterMesh(meshID);
                    _meshIdCache.Remove(lodMesh);
                    _meshIdReverse.Remove(meshID);
                    _meshRefCount.Remove(meshID);
                }
            }
        }

#if UNITY_EDITOR
        // validated prototypes, deduped by signature ref across rebuilds
        private HashSet<RenderSignature> _dotsCheckedProtos;
        private HashSet<RenderSignature> _crossfadeCheckedProtos;
        private HashSet<RenderSignature> _emptyLodCheckedProtos;
        private readonly HashSet<Shader> _protoShaderScratch = new();

        // warn once per prototype when its shader isn't DOTS-instanced, crossfade is enabled but the
        // shader has no LOD_FADE_CROSSFADE variant, or a LOD has no mesh
        private void ValidatePrototypeForBRG(int protoIndex)
        {
            var proto = _renderSignatures[protoIndex];
            if (proto == null) return;
            var prefab = proto.asset?.sourcePrefab;
            string name = prefab != null ? prefab.name : proto.mesh != null ? proto.mesh.name : $"proto {protoIndex}";

            _dotsCheckedProtos ??= new HashSet<RenderSignature>();
            if (_dotsCheckedProtos.Add(proto))
                foreach (var shader in ProtoShaders(proto))
                    if (!ShaderHasKeyword(shader, "DOTS_INSTANCING_ON"))
                        Debug.LogError($"GPUBatcherRenderer: prototype '{name}' uses shader '{shader.name}' which has no DOTS_INSTANCING_ON variant - it is not DOTS Instancing compatible and will not render under BatchRendererGroup.", prefab);

            if (ProtoHasCrossfade(protoIndex))
            {
                _crossfadeCheckedProtos ??= new HashSet<RenderSignature>();
                if (_crossfadeCheckedProtos.Add(proto))
                    foreach (var shader in ProtoShaders(proto))
                        if (!ShaderHasKeyword(shader, "LOD_FADE_CROSSFADE"))
                            Debug.LogError($"GPUBatcherRenderer: prototype '{name}' has LOD crossfade enabled, but shader '{shader.name}' has no LOD_FADE_CROSSFADE variant - crossfade dithering will not appear. Enable LOD CrossFade on the shader, or disable crossfade on this prototype.", prefab);
            }

            _emptyLodCheckedProtos ??= new HashSet<RenderSignature>();
            if (_emptyLodCheckedProtos.Add(proto))
            {
                int lodCount = proto.lodCount;
                for (int lod = 0; lod < lodCount; lod++)
                    if (proto.GetLODMeshRaw(lod) == null)
                        Debug.LogWarning($"GPUBatcherRenderer: prototype '{name}' LOD {lod} has no mesh and will not render in its screen-size range.", prefab);
            }
        }

        private bool ProtoHasCrossfade(int proto)
            => (_prototypeCrossfadeEnabled.IsCreated && proto < _prototypeCrossfadeEnabled.Length && _prototypeCrossfadeEnabled[proto])
            || (_prototypeAnimatedCrossfade.IsCreated && proto < _prototypeAnimatedCrossfade.Length && _prototypeAnimatedCrossfade[proto]);

        // distinct non-null shaders across the prototype's LOD materials
        private HashSet<Shader> ProtoShaders(RenderSignature proto)
        {
            _protoShaderScratch.Clear();
            int lodCount = proto.lodCount;
            for (int lod = 0; lod < lodCount; lod++)
            {
                var mats = proto.GetLODMaterials(lod) ?? proto.materials;
                if (mats == null) continue;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && mats[i].shader != null) _protoShaderScratch.Add(mats[i].shader);
            }
            return _protoShaderScratch;
        }

        // keywordSpace includes global multi_compile keywords, not just locals
        private static bool ShaderHasKeyword(Shader shader, string keyword)
            => shader.keywordSpace.FindKeyword(keyword).isValid;

        // called from GPUBatcherPrefabPostprocessor after asset deletions
        internal bool HasDestroyedRegisteredAssets()
        {
            foreach (var mesh in _meshIdReverse.Values)
                if (mesh == null) return true;
            foreach (var mat in _materialIdReverse.Values)
                if (mat == null) return true;
            return false;
        }
#endif

        private void RemoveBatchesForPrototype(int protoIndex)
        {
            if (_protoBatchSlots == null || protoIndex >= _protoBatchSlots.Length) return;
            var slots = _protoBatchSlots[protoIndex];
            if (slots == null) return;
            foreach (int slot in slots)
            {
                var info = _batchSlots[slot];
                if (_meshRefCount.TryGetValue(info.meshID, out var mc))
                {
                    if (--mc <= 0) { _brg.UnregisterMesh(info.meshID); if (_meshIdReverse.TryGetValue(info.meshID, out var mesh)) { _meshIdCache.Remove(mesh); _meshIdReverse.Remove(info.meshID); } _meshRefCount.Remove(info.meshID); }
                    else _meshRefCount[info.meshID] = mc;
                }
                if (_materialRefCount.TryGetValue(info.materialID, out var mtc))
                {
                    if (--mtc <= 0)
                    {
                        _brg.UnregisterMaterial(info.materialID);
                        if (_materialIdReverse.TryGetValue(info.materialID, out var mat)) { _materialIdCache.Remove(mat); _materialIdReverse.Remove(info.materialID); }
                        _materialRefCount.Remove(info.materialID);
                    }
                    else _materialRefCount[info.materialID] = mtc;
                }
                _batchSlotActive[slot] = false;
                _batchSlots[slot] = default;
                _freeBatchSlots.Push(slot);
            }
            slots.Clear();
        }

        private void RemoveAllBatchIds()
        {
            if (_brg == null) return;
            if (_sharedBatchIDValid) { _brg.RemoveBatch(_sharedBatchID); _sharedBatchIDValid = false; }
            if (_speedTreeBatchIDs != null)
                for (int i = 0; i < _speedTreeBatchIDs.Length; i++)
                    if (_speedTreeBatchIDs[i].value != 0) { _brg.RemoveBatch(_speedTreeBatchIDs[i]); _speedTreeBatchIDs[i] = default; }
        }

        private void ClearBatchSlots()
        {
            if (_batchSlots != null) Array.Clear(_batchSlots, 0, _batchSlotHWM);
            if (_batchSlotActive != null) Array.Clear(_batchSlotActive, 0, _batchSlotHWM);
            _batchSlotHWM = 0;
            _freeBatchSlots.Clear();
            if (_protoBatchSlots != null)
                for (int i = 0; i < _protoBatchSlots.Length; i++) _protoBatchSlots[i]?.Clear();
        }

        #endregion

        #region Incremental Batch System

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsProtoVacant(int proto)
            => _prototypeVacant != null && proto < _prototypeVacant.Length && _prototypeVacant[proto];

        // emit a proto's commands only when it has instances
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ProtoHasInstances(int p)
            => _protoInstanceCounts.IsCreated && p < _protoInstanceCounts.Length && _protoInstanceCounts[p] > 0;

        // never retracted on drain to 0
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ProtoMaterialized(int p)
            => _materializedProtos.Contains(p);

        // allocate bucket + visibility regions for one prototype
        private void IncAddPrototype(int protoIndex)
        {
            var proto = _renderSignatures[protoIndex];
            if (proto == null) return;
            EnsurePerProtoArrays(_renderSignatures.Count);

            int lodCount = proto.lodCount;
            bool anyFade = _prototypeCrossfadeEnabled.IsCreated && protoIndex < _prototypeCrossfadeEnabled.Length
                && (_prototypeCrossfadeEnabled[protoIndex] || _prototypeAnimatedCrossfade[protoIndex]);

            uint pctMask = !anyFade ? 0xFFFFFFFFu : (proto.fadeMode == LODFadeMode.SpeedTree ? proto.percentageFadeMask : 0u);
            _perProtoPercentageFadeMask[protoIndex] = pctMask;

            bool hasShadow = !_prototypeShadowMaskCache.IsCreated || protoIndex >= _prototypeShadowMaskCache.Length
                || _prototypeShadowMaskCache[protoIndex] != 0;
            int shadowCascades = _shadowSplitCapacity > 4
                ? _shadowSplitCapacity : math.clamp(_prototypeMaxShadowCascade[protoIndex], 1, 4);
            int minShadowLOD = math.clamp(proto.minShadowLOD, 0, lodCount - 1);

            int crossfadeCascades;
            if (_prototypeAnimatedCrossfade.IsCreated && protoIndex < _prototypeAnimatedCrossfade.Length && _prototypeAnimatedCrossfade[protoIndex])
                crossfadeCascades = shadowCascades;
            else if (anyFade)
                crossfadeCascades = math.clamp(proto.maxShadowCrossfadeCascade == -99 ? _config.maxShadowCrossfadeCascade : proto.maxShadowCrossfadeCascade, 0, shadowCascades);
            else crossfadeCascades = 0;

            int shadowLodStride = crossfadeCascades + shadowCascades;
            _perProtoShadowCrossfadeCascades[protoIndex] = crossfadeCascades;
            _perProtoShadowLodStride[protoIndex] = shadowLodStride;
            _perProtoMinShadowLOD[protoIndex] = minShadowLOD;

            uint bucketMask = GetBucketPctMask(pctMask, lodCount);
            int crossfadeLods = lodCount - math.countbits(bucketMask & ((1u << lodCount) - 1u));
            int camBuckets = lodCount + crossfadeLods;
            int shdBuckets = hasShadow ? (lodCount - minShadowLOD) * shadowLodStride : 0;

            bool flipWinding = _cachedNegativeScaleMode == NegativeScaleMode.FlipWinding && _protoFlipCounts.IsCreated;
            bool hasFlip = flipWinding && protoIndex < _protoFlipCounts.Length && _protoFlipCounts[protoIndex] > 0;
            int totalCamBuckets = camBuckets + (hasFlip ? camBuckets : 0);
            int totalShdBuckets = shdBuckets + (hasFlip && hasShadow ? shdBuckets : 0);

            _cameraBucketOffsets[protoIndex] = _camBucketAlloc.Allocate(totalCamBuckets);
            _shadowBucketOffsets[protoIndex] = totalShdBuckets > 0 ? _shdBucketAlloc.Allocate(totalShdBuckets) : 0;
            _protoCamBucketCount[protoIndex] = totalCamBuckets;
            _protoShdBucketCount[protoIndex] = totalShdBuckets;
            _protoFlipBucketOffsets[protoIndex] = hasFlip ? camBuckets : 0;
            _protoShadowFlipBucketOffsets[protoIndex] = hasFlip && hasShadow ? shdBuckets : 0;

            // allocate independent visibility regions
            int hwm = (_protoInstanceCountHWM.IsCreated && protoIndex < _protoInstanceCountHWM.Length) ? _protoInstanceCountHWM[protoIndex] : 0;
            uint slots = (uint)math.max(hwm, 64);
            int flipHwm = (_protoFlipCountHWM.IsCreated && protoIndex < _protoFlipCountHWM.Length) ? _protoFlipCountHWM[protoIndex] : 0;
            uint flipSlots = (uint)math.max(flipHwm, 64);

            int camVisSize = (int)(camBuckets * slots + (hasFlip ? camBuckets * flipSlots : 0));
            int shdVisSize = totalShdBuckets > 0 ? (int)(shdBuckets * slots + (hasFlip && hasShadow ? shdBuckets * flipSlots : 0)) : 0;

            _protoCamVisBase[protoIndex] = _camVisAlloc.Allocate(math.max(1, camVisSize));
            _protoShdVisBase[protoIndex] = shdVisSize > 0 ? _shdVisAlloc.Allocate(shdVisSize) : 0;
            _protoCamVisSize[protoIndex] = camVisSize;
            _protoShdVisSize[protoIndex] = shdVisSize;
        }

        private void IncRemovePrototype(int protoIndex)
        {
            if (_cameraBucketOffsets == null || protoIndex >= _cameraBucketOffsets.Length) return;
            if (_protoCamBucketCount[protoIndex] > 0) _camBucketAlloc.Free(_cameraBucketOffsets[protoIndex], _protoCamBucketCount[protoIndex]);
            if (_protoShdBucketCount[protoIndex] > 0) _shdBucketAlloc.Free(_shadowBucketOffsets[protoIndex], _protoShdBucketCount[protoIndex]);
            if (_protoCamVisSize[protoIndex] > 0) _camVisAlloc.Free(_protoCamVisBase[protoIndex], _protoCamVisSize[protoIndex]);
            if (_protoShdVisSize[protoIndex] > 0) _shdVisAlloc.Free(_protoShdVisBase[protoIndex], _protoShdVisSize[protoIndex]);

            _cameraBucketOffsets[protoIndex] = 0; _shadowBucketOffsets[protoIndex] = 0;
            _protoCamBucketCount[protoIndex] = 0; _protoShdBucketCount[protoIndex] = 0;
            _protoCamVisBase[protoIndex] = 0; _protoShdVisBase[protoIndex] = 0;
            _protoCamVisSize[protoIndex] = 0; _protoShdVisSize[protoIndex] = 0;
            _protoFlipBucketOffsets[protoIndex] = 0; _protoShadowFlipBucketOffsets[protoIndex] = 0;
            _perProtoShadowCrossfadeCascades[protoIndex] = 0; _perProtoShadowLodStride[protoIndex] = 0;
            _perProtoMinShadowLOD[protoIndex] = 0; _perProtoPercentageFadeMask[protoIndex] = 0xFFFFFFFFu;
        }

        private void IncAddPrototypeCommands(int protoIndex)
        {
            var proto = _renderSignatures[protoIndex];
            if (proto == null) return;

            int lodCount = proto.lodCount;
            bool protoHasMotion = proto.motionVectorMode == MotionVectorGenerationMode.Object;
            int hwm = (_protoInstanceCountHWM.IsCreated && protoIndex < _protoInstanceCountHWM.Length) ? _protoInstanceCountHWM[protoIndex] : 0;
            uint slots = (uint)math.max(hwm, 64);
            int flipHwm = (_protoFlipCountHWM.IsCreated && protoIndex < _protoFlipCountHWM.Length) ? _protoFlipCountHWM[protoIndex] : 0;
            uint flipSlots = (uint)math.max(flipHwm, 64);

            bool flipWinding = _cachedNegativeScaleMode == NegativeScaleMode.FlipWinding && _protoFlipCounts.IsCreated;
            bool hasFlip = flipWinding && protoIndex < _protoFlipCounts.Length && _protoFlipCounts[protoIndex] > 0;
            bool hasShadow = !_prototypeShadowMaskCache.IsCreated || protoIndex >= _prototypeShadowMaskCache.Length
                || _prototypeShadowMaskCache[protoIndex] != 0;

            // ensure bucket NativeArrays are large enough
            EnsureBucketCapacity(ref _buckets, _camBucketAlloc.highWaterMark);
            if (hasShadow) EnsureBucketCapacity(ref _shadowBuckets, _shdBucketAlloc.highWaterMark);

            // camera: normal + flip (flip needs normalSlots for vis base offset)
            EmitCameraCommands(protoIndex, lodCount, slots, slots, protoHasMotion, false);
            if (hasFlip) EmitCameraCommands(protoIndex, lodCount, flipSlots, slots, protoHasMotion, true);

            // shadow: normal + flip
            if (hasShadow)
            {
                EmitShadowCommands(protoIndex, lodCount, slots, slots, protoHasMotion, false);
                if (hasFlip) EmitShadowCommands(protoIndex, lodCount, flipSlots, slots, protoHasMotion, true);
            }

            _materializedProtos.Add(protoIndex);
            _rangesDirty = true;
        }

        private void EmitCameraCommands(int protoIndex, int lodCount, uint visSlots, uint normalSlots, bool hasMotion, bool isFlip)
        {
            var proto = _renderSignatures[protoIndex];
            int baseOff = _cameraBucketOffsets[protoIndex];
            int flipDelta = _protoFlipBucketOffsets[protoIndex];
            uint bucketMask = GetBucketPctMask(_perProtoPercentageFadeMask[protoIndex], lodCount);
            int visBase = _protoCamVisBase[protoIndex];
            // for flip, vis region starts after normal buckets (using normalSlots, not flipSlots)
            int normalBucketCount = _protoFlipBucketOffsets[protoIndex] > 0 ? _protoFlipBucketOffsets[protoIndex] : _protoCamBucketCount[protoIndex];

            var slots = _protoBatchSlots[protoIndex];

            for (int lod = 0; lod < lodCount; lod++)
            {
                // empty LODs have no slots so the slot loop below emits no draw, but they still get a bucket
                // with a self-owned vis offset: any writes the GPU makes there for culled instances are unread
                ulong rangeKey = ComputeRangeKey(proto, lod, false);
                bool lodIsPercentage = (bucketMask & (1u << lod)) != 0;
                int fm = lodIsPercentage ? 1 : 2;
                uint maskBelow = (1u << lod) - 1u;
                int crossfadeBelow = math.countbits(~bucketMask & maskBelow);

                for (int fadeState = 0; fadeState < fm; fadeState++)
                {
                    int bucketIdx = !isFlip
                        ? baseOff + lod + crossfadeBelow + fadeState
                        : baseOff + flipDelta + lod + crossfadeBelow + fadeState;

                    int flipLocalIdx = lod + crossfadeBelow + fadeState;
                    uint bucketVisOffset = !isFlip
                        ? (uint)(visBase + flipLocalIdx * (int)visSlots)
                        : (uint)(visBase + normalBucketCount * (int)normalSlots + flipLocalIdx * (int)visSlots);

                    if (bucketIdx < _buckets.Length)
                    {
                        _buckets[bucketIdx] = new DrawBucket { bufferOffset = bucketVisOffset, instanceCount = 0, maxSlots = visSlots };
#if UNITY_EDITOR
                        if (_config.debugDrawBoundingSpheres)
                        {
                            if (_camBucketLod == null || _camBucketLod.Length < _buckets.Length)
                                Array.Resize(ref _camBucketLod, _buckets.Length);
                            _camBucketLod[bucketIdx] = (uint)lod;
                        }
#endif
                    }

                    for (int si = 0; si < slots.Count; si++)
                    {
                        int batchSlot = slots[si];
                        if (_batchSlots[batchSlot].lodLevel != lod) continue;
                        int cmdIdx = AllocCameraCommand(rangeKey, proto, lod);
                        var batch = _batchSlots[batchSlot];

                        BatchDrawCommandFlags flags = BatchDrawCommandFlags.LODCrossFadeValuePacked;
                        if (fadeState == 1) flags |= BatchDrawCommandFlags.LODCrossFade;
                        if (hasMotion) flags |= BatchDrawCommandFlags.HasMotion;
                        if (isFlip) flags |= BatchDrawCommandFlags.FlipWinding;

                        _cameraDrawCommandTemplate[cmdIdx] = new BatchDrawCommandIndirect
                        {
                            flags = flags,
                            batchID = batch.batchID,
                            materialID = batch.materialID,
                            splitVisibilityMask = 0xff,
                            meshID = batch.meshID,
                            topology = MeshTopology.Triangles,
#if ADRENO_DRIVER_WORKAROUND
                            visibleOffset = s_indirectViaStartInstance ? 0u : bucketVisOffset,
#else
                            visibleOffset = bucketVisOffset,
#endif
                            indirectArgsBufferOffset = (uint)(cmdIdx * k_indirectArgsStride),
                        };
                        WriteMeshArgsNative(_cameraIndirectArgsTemplate, cmdIdx, batch, proto);
#if ADRENO_DRIVER_WORKAROUND
                        if (s_indirectViaStartInstance)
                            _cameraIndirectArgsTemplate[cmdIdx * 5 + 4] = bucketVisOffset;   // startInstance
#endif
                        _camCmdToBucket[cmdIdx] = bucketIdx;
                        _camCmd2Proto[cmdIdx] = protoIndex;

                        EnsureProtoCommandList(ref _camProtoCommands, protoIndex);
                        _camProtoCommands[protoIndex].Add(cmdIdx);
                        _camCmdIndexInProtoList[cmdIdx] = _camProtoCommands[protoIndex].Count - 1;
                    }
                }
            }
        }

        private void EmitShadowCommands(int protoIndex, int lodCount, uint visSlots, uint normalSlots, bool hasMotion, bool isFlip)
        {
            var proto = _renderSignatures[protoIndex];
            int baseOff = _shadowBucketOffsets[protoIndex];
            int flipDelta = _protoShadowFlipBucketOffsets[protoIndex];
            int cascadeCount = _shadowSplitCapacity > 4
                ? _shadowSplitCapacity : math.clamp(_prototypeMaxShadowCascade[protoIndex], 1, 4);
            int crossfadeCascades = _perProtoShadowCrossfadeCascades[protoIndex];
            int lodStride = _perProtoShadowLodStride[protoIndex];
            int minShadowLOD = _perProtoMinShadowLOD[protoIndex];
            int visBase = _protoShdVisBase[protoIndex];
            int normalBucketCount = _protoShadowFlipBucketOffsets[protoIndex] > 0 ? _protoShadowFlipBucketOffsets[protoIndex] : _protoShdBucketCount[protoIndex];

            var slots = _protoBatchSlots[protoIndex];

            for (int lod = minShadowLOD; lod < lodCount; lod++)
            {
                // empty LODs have no slots (no draw emitted) but still get a self-owned bucket; see EmitCameraCommands
                ulong rangeKey = ComputeRangeKey(proto, lod, true);
                int relLod = lod - minShadowLOD;

                for (int cascade = 0; cascade < cascadeCount; cascade++)
                {
                    int fadeCount = (cascade < crossfadeCascades) ? 2 : 1;
                    for (int fadeState = 0; fadeState < fadeCount; fadeState++)
                    {
                        int cascadeOff = (cascade < crossfadeCascades) ? cascade * 2 + fadeState : crossfadeCascades + cascade;
                        int bucketIdx = !isFlip
                            ? baseOff + relLod * lodStride + cascadeOff
                            : baseOff + flipDelta + relLod * lodStride + cascadeOff;

                        int flipLocalIdx = relLod * lodStride + cascadeOff;
                        uint bucketVisOffset = !isFlip
                            ? (uint)(visBase + flipLocalIdx * (int)visSlots)
                            : (uint)(visBase + normalBucketCount * (int)normalSlots + flipLocalIdx * (int)visSlots);

                        if (bucketIdx < _shadowBuckets.Length)
                            _shadowBuckets[bucketIdx] = new DrawBucket { bufferOffset = bucketVisOffset, instanceCount = 0, maxSlots = visSlots };

                        for (int si = 0; si < slots.Count; si++)
                        {
                            int batchSlot = slots[si];
                            if (_batchSlots[batchSlot].lodLevel != lod) continue;
                            int cmdIdx = AllocShadowCommand(rangeKey, proto, lod);
                            var batch = _batchSlots[batchSlot];

                            BatchDrawCommandFlags flags = BatchDrawCommandFlags.LODCrossFadeValuePacked;
                            if (fadeState == 1) flags |= BatchDrawCommandFlags.LODCrossFade;
                            if (hasMotion) flags |= BatchDrawCommandFlags.HasMotion;
                            if (isFlip) flags |= BatchDrawCommandFlags.FlipWinding;

                            _shadowDrawCommandTemplate[cmdIdx] = new BatchDrawCommandIndirect
                            {
                                flags = flags, batchID = batch.batchID, materialID = batch.materialID,
                                splitVisibilityMask = 0, meshID = batch.meshID, topology = MeshTopology.Triangles,
#if ADRENO_DRIVER_WORKAROUND
                                visibleOffset = s_indirectViaStartInstance ? 0u : bucketVisOffset,
#else
                                visibleOffset = bucketVisOffset,
#endif
                                indirectArgsBufferOffset = (uint)(cmdIdx * k_indirectArgsStride),
                            };
                            WriteMeshArgsNative(_shadowIndirectArgsTemplate, cmdIdx, batch, proto);
#if ADRENO_DRIVER_WORKAROUND
                            if (s_indirectViaStartInstance)
                                _shadowIndirectArgsTemplate[cmdIdx * 5 + 4] = bucketVisOffset;   // startInstance
#endif
                            _shdCmdToBucket[cmdIdx] = bucketIdx;
                            _shdCmd2Proto[cmdIdx] = protoIndex;

                            int protoMaxCascades = (_prototypeMaxShadowCascade.IsCreated && batch.signatureIndex < _prototypeMaxShadowCascade.Length)
                                ? _prototypeMaxShadowCascade[batch.signatureIndex] : k_maxCascades;
                            _shadowCommandFilterInfo[cmdIdx] = new ShadowCommandFilterInfo { cascadeIndex = cascade, fadeState = fadeState, protoMaxCascades = protoMaxCascades };

                            EnsureProtoCommandList(ref _shdProtoCommands, protoIndex);
                            _shdProtoCommands[protoIndex].Add(cmdIdx);
                            _shdCmdIndexInProtoList[cmdIdx] = _shdProtoCommands[protoIndex].Count - 1;
                        }
                    }
                }
            }
        }

        private void IncRemovePrototypeCommands(int protoIndex)
        {
            _materializedProtos.Remove(protoIndex);
            if (_camProtoCommands != null && protoIndex < _camProtoCommands.Length && _camProtoCommands[protoIndex] != null)
            {
                var cmds = _camProtoCommands[protoIndex];
                if (_cameraDrawCommandTemplate.IsCreated && cmds.Count > 0)
                    for (int i = cmds.Count - 1; i >= 0; i--) SwapBackCameraCommand(cmds[i]);
                cmds.Clear();
            }
            if (_shdProtoCommands != null && protoIndex < _shdProtoCommands.Length && _shdProtoCommands[protoIndex] != null)
            {
                var cmds = _shdProtoCommands[protoIndex];
                if (_shadowDrawCommandTemplate.IsCreated && cmds.Count > 0)
                    for (int i = cmds.Count - 1; i >= 0; i--) SwapBackShadowCommand(cmds[i]);
                cmds.Clear();
            }
            _rangesDirty = true;
        }

        // try to resize a proto's vis regions in place (TryExtend).
        // if successful, patches bucket offsets + command visibleOffsets without touching ranges.
        // returns false if in-place extension isn't possible (caller falls back to remove+re-add).
        private bool TryResizeProtoInPlace(int p)
        {
            if (_cameraBucketOffsets == null || p >= _cameraBucketOffsets.Length) return false;
            // no commands yet (proto's first instance): nothing to patch in place, materialize via fallback
            if (!ProtoMaterialized(p)) return false;

            // if bucket structure changed (first flip appeared or disappeared), can't resize in place
            bool flipWinding = _cachedNegativeScaleMode == NegativeScaleMode.FlipWinding && _protoFlipCounts.IsCreated;
            bool needsFlip = flipWinding && p < _protoFlipCounts.Length && _protoFlipCounts[p] > 0;
            bool hasFlip = _protoFlipBucketOffsets[p] > 0;
            if (needsFlip != hasFlip) return false;

            int hwm = (_protoInstanceCountHWM.IsCreated && p < _protoInstanceCountHWM.Length) ? _protoInstanceCountHWM[p] : 0;
            uint newSlots = (uint)math.max(hwm, 64);
            int flipHwm = (_protoFlipCountHWM.IsCreated && p < _protoFlipCountHWM.Length) ? _protoFlipCountHWM[p] : 0;
            uint newFlipSlots = (uint)math.max(flipHwm, 64);

            int camBuckets = _protoCamBucketCount[p];
            int flipOff = _protoFlipBucketOffsets[p];
            int normalCamBuckets = flipOff > 0 ? flipOff : camBuckets;
            int flipCamBuckets = camBuckets - normalCamBuckets;

            int newCamVisSize = (int)(normalCamBuckets * newSlots + flipCamBuckets * newFlipSlots);
            int oldCamVisSize = _protoCamVisSize[p];

            // try extend camera vis
            bool camOk = newCamVisSize <= oldCamVisSize
                || _camVisAlloc.TryExtend(_protoCamVisBase[p], oldCamVisSize, newCamVisSize);
            if (!camOk) return false;

            // try extend shadow vis
            int shdBuckets = _protoShdBucketCount[p];
            int shdFlipOff = _protoShadowFlipBucketOffsets[p];
            int normalShdBuckets = shdFlipOff > 0 ? shdFlipOff : shdBuckets;
            int flipShdBuckets = shdBuckets - normalShdBuckets;

            int newShdVisSize = shdBuckets > 0 ? (int)(normalShdBuckets * newSlots + flipShdBuckets * newFlipSlots) : 0;
            int oldShdVisSize = _protoShdVisSize[p];

            bool shdOk = newShdVisSize <= oldShdVisSize
                || shdBuckets == 0
                || _shdVisAlloc.TryExtend(_protoShdVisBase[p], oldShdVisSize, newShdVisSize);
            if (!shdOk)
            {
                // undo camera extend if shadow fails
                if (newCamVisSize > oldCamVisSize)
                    _camVisAlloc.Free(_protoCamVisBase[p] + oldCamVisSize, newCamVisSize - oldCamVisSize);
                return false;
            }

            // success, update sizes
            _protoCamVisSize[p] = newCamVisSize;
            _protoShdVisSize[p] = newShdVisSize;

            int camVisBase = _protoCamVisBase[p];
            int camBaseOff = _cameraBucketOffsets[p];
            for (int b = 0; b < normalCamBuckets; b++)
            {
                int bi = camBaseOff + b;
                if (bi < _buckets.Length)
                {
                    var bkt = _buckets[bi];
                    bkt.bufferOffset = (uint)(camVisBase + b * (int)newSlots);
                    bkt.maxSlots = newSlots;
                    _buckets[bi] = bkt;
                }
            }
            for (int b = 0; b < flipCamBuckets; b++)
            {
                int bi = camBaseOff + flipOff + b;
                if (bi < _buckets.Length)
                {
                    var bkt = _buckets[bi];
                    bkt.bufferOffset = (uint)(camVisBase + normalCamBuckets * (int)newSlots + b * (int)newFlipSlots);
                    bkt.maxSlots = newFlipSlots;
                    _buckets[bi] = bkt;
                }
            }

            // patch camera command visibleOffsets
            if (_camProtoCommands != null && p < _camProtoCommands.Length && _camProtoCommands[p] != null
                && _cameraDrawCommandTemplate.IsCreated)
            {
                foreach (int ci in _camProtoCommands[p])
                {
                    int bucket = _camCmdToBucket[ci];
                    uint off = (bucket >= 0 && bucket < _buckets.Length) ? _buckets[bucket].bufferOffset : 0;
                    var tmpl = _cameraDrawCommandTemplate[ci];
#if ADRENO_DRIVER_WORKAROUND
                    tmpl.visibleOffset = s_indirectViaStartInstance ? 0u : off;
                    _cameraDrawCommandTemplate[ci] = tmpl;
                    if (s_indirectViaStartInstance) _cameraIndirectArgsTemplate[ci * 5 + 4] = off;
#else
                    tmpl.visibleOffset = off;
                    _cameraDrawCommandTemplate[ci] = tmpl;
#endif
                }
            }

            if (shdBuckets > 0)
            {
                int shdVisBase = _protoShdVisBase[p];
                int shdBaseOff = _shadowBucketOffsets[p];
                for (int b = 0; b < normalShdBuckets; b++)
                {
                    int bi = shdBaseOff + b;
                    if (bi < _shadowBuckets.Length)
                    {
                        var bkt = _shadowBuckets[bi];
                        bkt.bufferOffset = (uint)(shdVisBase + b * (int)newSlots);
                        bkt.maxSlots = newSlots;
                        _shadowBuckets[bi] = bkt;
                    }
                }
                for (int b = 0; b < flipShdBuckets; b++)
                {
                    int bi = shdBaseOff + shdFlipOff + b;
                    if (bi < _shadowBuckets.Length)
                    {
                        var bkt = _shadowBuckets[bi];
                        bkt.bufferOffset = (uint)(shdVisBase + normalShdBuckets * (int)newSlots + b * (int)newFlipSlots);
                        bkt.maxSlots = newFlipSlots;
                        _shadowBuckets[bi] = bkt;
                    }
                }

                if (_shdProtoCommands != null && p < _shdProtoCommands.Length && _shdProtoCommands[p] != null
                    && _shadowDrawCommandTemplate.IsCreated)
                {
                    foreach (int ci in _shdProtoCommands[p])
                    {
                        int bucket = _shdCmdToBucket[ci];
                        uint off = (bucket >= 0 && bucket < _shadowBuckets.Length) ? _shadowBuckets[bucket].bufferOffset : 0;
                        var tmpl = _shadowDrawCommandTemplate[ci];
#if ADRENO_DRIVER_WORKAROUND
                        tmpl.visibleOffset = s_indirectViaStartInstance ? 0u : off;
                        _shadowDrawCommandTemplate[ci] = tmpl;
                        if (s_indirectViaStartInstance) _shadowIndirectArgsTemplate[ci * 5 + 4] = off;
#else
                        tmpl.visibleOffset = off;
                        _shadowDrawCommandTemplate[ci] = tmpl;
#endif
                    }
                }
            }

#if ADRENO_DRIVER_WORKAROUND
            // startInstance lives in the args buffer (re-seeded from the template on generation change),
            // not the per-frame command template - bump so the moved bucket offsets reach the GPU
            if (s_indirectViaStartInstance) _bufferPoolGeneration++;
#endif
            _rangesDirty = true;
            return true;
        }

        private void SwapBackCameraCommand(int cmdIdx)
        {
            int ri = _camCmdRangeIdx[cmdIdx];
            if (ri < 0 || ri >= _camRangeCount) return;
            ref var range = ref _camRanges[ri];
            int last = range.begin + range.count - 1;

            if (cmdIdx != last)
            {
                var tmpT = _cameraDrawCommandTemplate[cmdIdx];
                _cameraDrawCommandTemplate[cmdIdx] = _cameraDrawCommandTemplate[last];
                _cameraDrawCommandTemplate[last] = tmpT;
                SwapArgsNative(_cameraIndirectArgsTemplate, cmdIdx, last);
                (_camCmdToBucket[cmdIdx], _camCmdToBucket[last]) = (_camCmdToBucket[last], _camCmdToBucket[cmdIdx]);
                (_camCmd2Proto[cmdIdx], _camCmd2Proto[last]) = (_camCmd2Proto[last], _camCmd2Proto[cmdIdx]);
                (_camCmdRangeIdx[cmdIdx], _camCmdRangeIdx[last]) = (_camCmdRangeIdx[last], _camCmdRangeIdx[cmdIdx]);
                (_camCmdIndexInProtoList[cmdIdx], _camCmdIndexInProtoList[last]) = (_camCmdIndexInProtoList[last], _camCmdIndexInProtoList[cmdIdx]);
                // fix indirectArgsBufferOffset for moved command
                var tmpl = _cameraDrawCommandTemplate[cmdIdx];
                tmpl.indirectArgsBufferOffset = (uint)(cmdIdx * k_indirectArgsStride);
                _cameraDrawCommandTemplate[cmdIdx] = tmpl;
                // update swapped command's reverse lookup
                int swappedProto = _camCmd2Proto[cmdIdx];
                if (_camProtoCommands != null && swappedProto < _camProtoCommands.Length && _camProtoCommands[swappedProto] != null)
                {
                    var list = _camProtoCommands[swappedProto];
                    int j = _camCmdIndexInProtoList[cmdIdx];
                    list[j] = cmdIdx;
                }
            }
            _cameraDrawCommandTemplate[last] = default;
            _camCmdRangeIdx[last] = -1;
            range.count--;
        }

        private void SwapBackShadowCommand(int cmdIdx)
        {
            int ri = _shdCmdRangeIdx[cmdIdx];
            if (ri < 0 || ri >= _shdRangeCount) return;
            ref var range = ref _shdRanges[ri];
            int last = range.begin + range.count - 1;

            if (cmdIdx != last)
            {
                var tmpT = _shadowDrawCommandTemplate[cmdIdx];
                _shadowDrawCommandTemplate[cmdIdx] = _shadowDrawCommandTemplate[last];
                _shadowDrawCommandTemplate[last] = tmpT;
                SwapArgsNative(_shadowIndirectArgsTemplate, cmdIdx, last);
                var tmpF = _shadowCommandFilterInfo[cmdIdx];
                _shadowCommandFilterInfo[cmdIdx] = _shadowCommandFilterInfo[last];
                _shadowCommandFilterInfo[last] = tmpF;
                (_shdCmdToBucket[cmdIdx], _shdCmdToBucket[last]) = (_shdCmdToBucket[last], _shdCmdToBucket[cmdIdx]);
                (_shdCmd2Proto[cmdIdx], _shdCmd2Proto[last]) = (_shdCmd2Proto[last], _shdCmd2Proto[cmdIdx]);
                (_shdCmdRangeIdx[cmdIdx], _shdCmdRangeIdx[last]) = (_shdCmdRangeIdx[last], _shdCmdRangeIdx[cmdIdx]);
                (_shdCmdIndexInProtoList[cmdIdx], _shdCmdIndexInProtoList[last]) = (_shdCmdIndexInProtoList[last], _shdCmdIndexInProtoList[cmdIdx]);
                var tmpl = _shadowDrawCommandTemplate[cmdIdx];
                tmpl.indirectArgsBufferOffset = (uint)(cmdIdx * k_indirectArgsStride);
                _shadowDrawCommandTemplate[cmdIdx] = tmpl;
                int swappedProto = _shdCmd2Proto[cmdIdx];
                if (_shdProtoCommands != null && swappedProto < _shdProtoCommands.Length && _shdProtoCommands[swappedProto] != null)
                {
                    var list = _shdProtoCommands[swappedProto];
                    int j = _shdCmdIndexInProtoList[cmdIdx];
                    list[j] = cmdIdx;
                }
            }
            _shadowDrawCommandTemplate[last] = default;
            _shadowCommandFilterInfo[last] = default;
            _shdCmdRangeIdx[last] = -1;
            range.count--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ComputeRangeKey(RenderSignature proto, int lodLevel, bool isShadow)
        {
            uint shadow = (uint)GetRangeShadowModeForLOD(proto, lodLevel, isShadow);
            return ((ulong)(proto.layer & 0x1F) << 38) | ((ulong)proto.renderingLayerMask << 6)
                | ((ulong)((int)proto.motionVectorMode & 0x7) << 3) | (ulong)(shadow & 0x7);
        }

        private int AllocCameraCommand(ulong rangeKey, RenderSignature proto, int lodLevel)
        {
            int ri = FindOrCreateCameraRange(rangeKey, proto, lodLevel);
            ref var range = ref _camRanges[ri];
            if (range.count >= range.capacity)
                GrowCameraRange(ri);
            int cmdIdx = range.begin + range.count;
            _camCmdRangeIdx[cmdIdx] = ri;
            range.count++;
            return cmdIdx;
        }

        private int AllocShadowCommand(ulong rangeKey, RenderSignature proto, int lodLevel)
        {
            int ri = FindOrCreateShadowRange(rangeKey, proto, lodLevel);
            ref var range = ref _shdRanges[ri];
            if (range.count >= range.capacity)
                GrowShadowRange(ri);
            int cmdIdx = range.begin + range.count;
            _shdCmdRangeIdx[cmdIdx] = ri;
            range.count++;
            return cmdIdx;
        }

        private int FindOrCreateCameraRange(ulong key, RenderSignature proto, int lodLevel)
        {
            if (_camRangeMap.TryGetValue(key, out int idx)) return idx;
            if (_camRanges == null || _camRangeCount >= _camRanges.Length)
                Array.Resize(ref _camRanges, math.max(8, (_camRanges?.Length ?? 0) * 2));

            int initialCapacity = 32;
            int begin = _camCmdCount;
            EnsureCameraNativeCapacity(begin + initialCapacity);
            EnsureCameraManagedCapacity(begin + initialCapacity);
            // mark slack slots as inactive
            for (int s = begin; s < begin + initialCapacity; s++) _camCmdRangeIdx[s] = -1;

            _camRanges[_camRangeCount] = new RangeSegment
            {
                begin = begin, count = 0, capacity = initialCapacity,
                layer = proto.layer, renderingLayerMask = proto.renderingLayerMask,
                motionMode = proto.motionVectorMode, shadowCastingMode = GetRangeShadowModeForLOD(proto, lodLevel, false)
            };
            _camCmdCount = begin + initialCapacity;
            _camRangeMap[key] = _camRangeCount;
            return _camRangeCount++;
        }

        private int FindOrCreateShadowRange(ulong key, RenderSignature proto, int lodLevel)
        {
            if (_shdRangeMap.TryGetValue(key, out int idx)) return idx;
            if (_shdRanges == null || _shdRangeCount >= _shdRanges.Length)
                Array.Resize(ref _shdRanges, math.max(8, (_shdRanges?.Length ?? 0) * 2));

            int initialCapacity = 32;
            int begin = _shdCmdCount;
            EnsureShadowNativeCapacity(begin + initialCapacity);
            EnsureShadowManagedCapacity(begin + initialCapacity);
            for (int s = begin; s < begin + initialCapacity; s++) _shdCmdRangeIdx[s] = -1;

            _shdRanges[_shdRangeCount] = new RangeSegment
            {
                begin = begin, count = 0, capacity = initialCapacity,
                layer = proto.layer, renderingLayerMask = proto.renderingLayerMask,
                motionMode = proto.motionVectorMode, shadowCastingMode = GetRangeShadowModeForLOD(proto, lodLevel, true)
            };
            _shdCmdCount = begin + initialCapacity;
            _shdRangeMap[key] = _shdRangeCount;
            return _shdRangeCount++;
        }

        private void GrowCameraRange(int rangeIdx)
        {
            GrowRangeCore(rangeIdx, _camRanges, ref _camRangeCount, ref _camCmdCount,
                _cameraDrawCommandTemplate, _cameraIndirectArgsTemplate,
                _camCmdToBucket, _camCmd2Proto, _camCmdRangeIdx, _camCmdIndexInProtoList, _camProtoCommands,
                true);
        }

        private void GrowShadowRange(int rangeIdx)
        {
            GrowRangeCore(rangeIdx, _shdRanges, ref _shdRangeCount, ref _shdCmdCount,
                _shadowDrawCommandTemplate, _shadowIndirectArgsTemplate,
                _shdCmdToBucket, _shdCmd2Proto, _shdCmdRangeIdx, _shdCmdIndexInProtoList, _shdProtoCommands,
                false);
        }

        private unsafe void GrowRangeCore(int rangeIdx,
            RangeSegment[] ranges, ref int rangeCount, ref int cmdCount,
            NativeArray<BatchDrawCommandIndirect> templates, NativeArray<uint> args,
            int[] toBucket, int[] cmd2Proto, int[] cmdRangeIdx, int[] cmdIndexInProtoList, List<int>[] protoCommands,
            bool isCamera)
        {
            ref var range = ref ranges[rangeIdx];
            int growth = math.max(range.capacity / 4, 16);
            int rangeEnd = range.begin + range.capacity;
            int totalUsed = cmdCount;
            int newTotal = totalUsed + growth;

            if (isCamera) { EnsureCameraNativeCapacity(newTotal); EnsureCameraManagedCapacity(newTotal); }
            else { EnsureShadowNativeCapacity(newTotal); EnsureShadowManagedCapacity(newTotal); }

            // re-fetch after potential resize (arrays may have been replaced)
            templates = isCamera ? _cameraDrawCommandTemplate : _shadowDrawCommandTemplate;
            args = isCamera ? _cameraIndirectArgsTemplate : _shadowIndirectArgsTemplate;
            toBucket = isCamera ? _camCmdToBucket : _shdCmdToBucket;
            cmd2Proto = isCamera ? _camCmd2Proto : _shdCmd2Proto;
            cmdRangeIdx = isCamera ? _camCmdRangeIdx : _shdCmdRangeIdx;
            cmdIndexInProtoList = isCamera ? _camCmdIndexInProtoList : _shdCmdIndexInProtoList;

            int shiftCount = totalUsed - rangeEnd;
            if (shiftCount > 0)
            {
                int tmplStride = UnsafeUtility.SizeOf<BatchDrawCommandIndirect>();
                UnsafeUtility.MemMove((byte*)templates.GetUnsafePtr() + (rangeEnd + growth) * tmplStride,
                    (byte*)templates.GetUnsafePtr() + rangeEnd * tmplStride, shiftCount * tmplStride);
                UnsafeUtility.MemMove((byte*)args.GetUnsafePtr() + (rangeEnd + growth) * k_indirectArgsStride,
                    (byte*)args.GetUnsafePtr() + rangeEnd * k_indirectArgsStride, shiftCount * k_indirectArgsStride);

                // shadow filter info
                if (!isCamera && _shadowCommandFilterInfo.IsCreated)
                {
                    int filterStride = UnsafeUtility.SizeOf<ShadowCommandFilterInfo>();
                    UnsafeUtility.MemMove((byte*)_shadowCommandFilterInfo.GetUnsafePtr() + (rangeEnd + growth) * filterStride,
                        (byte*)_shadowCommandFilterInfo.GetUnsafePtr() + rangeEnd * filterStride, shiftCount * filterStride);
                }

                Array.Copy(toBucket, rangeEnd, toBucket, rangeEnd + growth, shiftCount);
                Array.Copy(cmd2Proto, rangeEnd, cmd2Proto, rangeEnd + growth, shiftCount);
                Array.Copy(cmdRangeIdx, rangeEnd, cmdRangeIdx, rangeEnd + growth, shiftCount);
                Array.Copy(cmdIndexInProtoList, rangeEnd, cmdIndexInProtoList, rangeEnd + growth, shiftCount);

                // fix indirectArgsBufferOffset for shifted commands
                for (int i = rangeEnd + growth; i < rangeEnd + growth + shiftCount; i++)
                {
                    var t = templates[i];
                    t.indirectArgsBufferOffset = (uint)(i * k_indirectArgsStride);
                    templates[i] = t;
                }

                // update range begins
                for (int i = 0; i < rangeCount; i++)
                    if (ranges[i].begin >= rangeEnd) ranges[i].begin += growth;

                // update reverse lookup indices
                if (protoCommands != null)
                    for (int p = 0; p < protoCommands.Length; p++)
                    {
                        var list = protoCommands[p]; if (list == null) continue;
                        for (int j = 0; j < list.Count; j++)
                            if (list[j] >= rangeEnd) list[j] += growth;
                    }
            }

            range.capacity += growth;
            cmdCount = totalUsed + growth;
        }

        // lightweight flush: rebuild range templates + upload argsToBucket GPU buffers.
        private void FlushDirtyState()
        {
            if (!_rangesDirty) return;

            _maxVisibleInstances = math.max(1, _camVisAlloc.highWaterMark);
            _shadowMaxVisibleInstances = math.max(1, _shdVisAlloc.highWaterMark);
            _totalCameraBuckets = _camBucketAlloc.highWaterMark;
            _totalShadowBuckets = _shdBucketAlloc.highWaterMark;
            // zero-instance protos allocate buckets without growing the templates
            EnsureBucketCapacity(ref _buckets, _totalCameraBuckets);
            EnsureBucketCapacity(ref _shadowBuckets, _totalShadowBuckets);
            _bucketCount = math.max(1, _totalCameraBuckets);
            _shadowBucketCount = math.max(1, _totalShadowBuckets);
            _drawCommandCount = _camCmdCount;
            _shadowDrawCommandCount = _shdCmdCount;

            BuildRangeTemplate(_camRanges, _camRangeCount, ref _cameraDrawRangeTemplate, ref _cameraDrawRangeCount, true);
            BuildRangeTemplate(_shdRanges, _shdRangeCount, ref _shadowDrawRangeTemplate, ref _shadowDrawRangeCount, false);

            UploadArgsToBucket(ref _argsToBucketBuffer, _camCmdToBucket, _camCmdCount, "FraxinusGPUBatcher_ArgsToBucket");
            UploadArgsToBucket(ref _shadowArgsToBucketBuffer, _shdCmdToBucket, _shdCmdCount, "FraxinusGPUBatcher_ShadowArgsToBucket");

            _dirtyBufferBindings |= BufferBindingFlags.ArgsToBucket;
            _bufferPoolGeneration++;
            _rangesDirty = false;
        }

        private void IncFlush()
        {
            int protoCount = _renderSignatures.Count;
            if (protoCount == 0 || !_chunkSystemInitialized)
            {
                DisposeDrawOrderState();
                ClearCommandStores();
                _camBucketAlloc.Clear(); _shdBucketAlloc.Clear();
                _camVisAlloc.Clear(); _shdVisAlloc.Clear();
                return;
            }

            if (_batchFullRecomputeNeeded)
            {
                ClearCommandStores();
                _camBucketAlloc.Clear(); _shdBucketAlloc.Clear();
                _camVisAlloc.Clear(); _shdVisAlloc.Clear();
                _allSignaturesDirty = true;
                for (int p = 0; p < protoCount; p++)
                {
                    if (IsProtoVacant(p)) continue;
                    IncAddPrototype(p);
                    if (ProtoHasInstances(p)) IncAddPrototypeCommands(p);
                }
                _batchFullRecomputeNeeded = false;
                _pendingProtoAdds.Clear();
                _pendingProtoResizes.Clear();
            }
            else
            {
                if (_pendingProtoResizes.Count > 0)
                {
                    foreach (int p in _pendingProtoResizes)
                    {
                        if (IsProtoVacant(p)) continue;
                        if (_pendingProtoAdds.Contains(p)) continue; // will be added with current HWM
                        if (!TryResizeProtoInPlace(p))
                        {
                            // fallback: full remove+re-add (bucket offsets change)
                            IncRemovePrototypeCommands(p);
                            IncRemovePrototype(p);
                            IncAddPrototype(p);
                            if (ProtoHasInstances(p)) IncAddPrototypeCommands(p);
                            _dirtySignatureProtos.Add(p);
                        }
                    }
                    _pendingProtoResizes.Clear();
                }

                if (_pendingProtoAdds.Count > 0)
                {
                    foreach (int p in _pendingProtoAdds)
                    {
                        if (IsProtoVacant(p)) continue;
                        IncAddPrototype(p);
                        if (ProtoHasInstances(p)) IncAddPrototypeCommands(p);
                        _dirtySignatureProtos.Add(p);
                    }
                    _pendingProtoAdds.Clear();
                }
            }

            FlushDirtyState();
        }

        private void IncFullRebuild()
        {
#if DEBUG
            using var _cb = s_markerCreateBatches.Auto();
#endif
            RemoveAllBatchIds();
            ClearBatchSlots();
            _meshRefCount.Clear();
            _materialRefCount.Clear();
            DisposeDrawOrderState();
            ClearCommandStores();
            _camBucketAlloc.Clear(); _shdBucketAlloc.Clear();
            _camVisAlloc.Clear(); _shdVisAlloc.Clear();
            _allSignaturesDirty = true;

            if (!_chunkSystemInitialized || _matrixBuffer == null) return;
            int protoCount = _renderSignatures.Count;
            if (protoCount == 0) return;

            EnsureSharedBatchID();
            EnsureSpeedTreeBatchIDs();

            // create batch slots first so the lookup finds them
            for (int protoIdx = 0; protoIdx < protoCount; protoIdx++)
            {
                if (IsProtoVacant(protoIdx)) continue;
                CreateBatchesForPrototype(protoIdx);
            }
            for (int protoIdx = 0; protoIdx < protoCount; protoIdx++)
            {
                if (IsProtoVacant(protoIdx)) continue;
                IncAddPrototype(protoIdx);
                if (ProtoHasInstances(protoIdx)) IncAddPrototypeCommands(protoIdx);
            }
            _pendingProtoAdds.Clear();
            _pendingProtoResizes.Clear();
            _batchFullRecomputeNeeded = false;

            FlushDirtyState();
        }

        private void ClearCommandStores()
        {
            _materializedProtos.Clear();
            _camCmdCount = 0; _camRangeCount = 0; _camRangeMap.Clear();
            if (_camProtoCommands != null) for (int i = 0; i < _camProtoCommands.Length; i++) _camProtoCommands[i]?.Clear();
            _shdCmdCount = 0; _shdRangeCount = 0; _shdRangeMap.Clear();
            if (_shdProtoCommands != null) for (int i = 0; i < _shdProtoCommands.Length; i++) _shdProtoCommands[i]?.Clear();
            _rangesDirty = true;
        }

        #endregion

        #region NativeArray Helpers

        private void EnsureCameraNativeCapacity(int needed)
        {
            if (_cameraDrawCommandTemplate.IsCreated && _cameraDrawCommandTemplate.Length >= needed) return;
            int newLen = math.max(needed, _cameraDrawCommandTemplate.IsCreated ? _cameraDrawCommandTemplate.Length * 2 : 64);
            ResizeNative(ref _cameraDrawCommandTemplate, newLen);
            ResizeNative(ref _cameraIndirectArgsTemplate, newLen * 5);
        }

        private void EnsureShadowNativeCapacity(int needed)
        {
            if (_shadowDrawCommandTemplate.IsCreated && _shadowDrawCommandTemplate.Length >= needed) return;
            int newLen = math.max(needed, _shadowDrawCommandTemplate.IsCreated ? _shadowDrawCommandTemplate.Length * 2 : 64);
            ResizeNative(ref _shadowDrawCommandTemplate, newLen);
            ResizeNative(ref _shadowIndirectArgsTemplate, newLen * 5);
            ResizeNative(ref _shadowCommandFilterInfo, newLen);
        }

        private void EnsureCameraManagedCapacity(int needed)
        {
            if (_camCmdToBucket != null && _camCmdToBucket.Length >= needed) return;
            int newLen = math.max(needed, (_camCmdToBucket?.Length ?? 64) * 2);
            Array.Resize(ref _camCmdToBucket, newLen);
            Array.Resize(ref _camCmd2Proto, newLen);
            Array.Resize(ref _camCmdRangeIdx, newLen);
            Array.Resize(ref _camCmdIndexInProtoList, newLen);
        }

        private void EnsureShadowManagedCapacity(int needed)
        {
            if (_shdCmdToBucket != null && _shdCmdToBucket.Length >= needed) return;
            int newLen = math.max(needed, (_shdCmdToBucket?.Length ?? 64) * 2);
            Array.Resize(ref _shdCmdToBucket, newLen);
            Array.Resize(ref _shdCmd2Proto, newLen);
            Array.Resize(ref _shdCmdRangeIdx, newLen);
            Array.Resize(ref _shdCmdIndexInProtoList, newLen);
        }

        private static void ResizeNative<T>(ref NativeArray<T> arr, int newLen) where T : unmanaged
        {
            var old = arr;
            arr = new NativeArray<T>(newLen, Allocator.Persistent);
            if (old.IsCreated)
            {
                NativeArray<T>.Copy(old, 0, arr, 0, math.min(old.Length, newLen));
                old.Dispose();
            }
        }

        private static void EnsureBucketCapacity(ref NativeArray<DrawBucket> buckets, int needed)
        {
            needed = math.max(1, needed);
            if (buckets.IsCreated && buckets.Length >= needed) return;
            ResizeNative(ref buckets, math.max(needed, buckets.IsCreated ? buckets.Length * 2 : 64));
        }

        private static void SwapArgsNative(NativeArray<uint> args, int a, int b)
        {
            int ai = a * 5, bi = b * 5;
            for (int k = 0; k < 5; k++) { var tmp = args[ai + k]; args[ai + k] = args[bi + k]; args[bi + k] = tmp; }
        }

        private static void WriteMeshArgsNative(NativeArray<uint> args, int cmdIdx, BatchInfo batch, RenderSignature proto)
        {
            var mesh = proto.GetLODMesh(batch.lodLevel);
            if (mesh == null) return;
            int sub = batch.submeshIndex;
            int ai = cmdIdx * 5;
#if UNITY_6000_2_OR_NEWER
            if (proto.usesMeshLod && proto.meshLodIndices != null && batch.lodLevel < proto.meshLodIndices.Length)
            {
                int meshLod = proto.meshLodIndices[batch.lodLevel];
                args[ai] = mesh.GetIndexCount(sub, meshLod); args[ai+1] = 0;
                args[ai+2] = mesh.GetIndexStart(sub, meshLod); args[ai+3] = mesh.GetBaseVertex(sub); args[ai+4] = 0;
            }
            else
#endif
            { args[ai] = mesh.GetIndexCount(sub); args[ai+1] = 0; args[ai+2] = mesh.GetIndexStart(sub); args[ai+3] = mesh.GetBaseVertex(sub); args[ai+4] = 0; }
        }



        private static void EnsureProtoCommandList(ref List<int>[] lists, int protoIndex)
        {
            if (lists == null || protoIndex >= lists.Length)
            {
                int newLen = math.max(8, lists?.Length ?? 0);
                while (newLen <= protoIndex) newLen *= 2;
                Array.Resize(ref lists, newLen);
            }
            lists[protoIndex] ??= new List<int>(8);
        }

        private void EnsurePerProtoArrays(int protoCount)
        {
            if (_cameraBucketOffsets != null && _cameraBucketOffsets.Length >= protoCount) return;
            int oldLen = _cameraBucketOffsets?.Length ?? 0;
            Array.Resize(ref _cameraBucketOffsets, protoCount);
            Array.Resize(ref _shadowBucketOffsets, protoCount);
            Array.Resize(ref _perProtoShadowCrossfadeCascades, protoCount);
            Array.Resize(ref _perProtoShadowLodStride, protoCount);
            Array.Resize(ref _perProtoMinShadowLOD, protoCount);
            Array.Resize(ref _perProtoPercentageFadeMask, protoCount);
            Array.Resize(ref _protoCamBucketCount, protoCount);
            Array.Resize(ref _protoShdBucketCount, protoCount);
            Array.Resize(ref _protoCamVisBase, protoCount);
            Array.Resize(ref _protoShdVisBase, protoCount);
            Array.Resize(ref _protoCamVisSize, protoCount);
            Array.Resize(ref _protoShdVisSize, protoCount);
            Array.Resize(ref _protoFlipBucketOffsets, protoCount);
            Array.Resize(ref _protoShadowFlipBucketOffsets, protoCount);
            for (int i = oldLen; i < protoCount; i++) _perProtoPercentageFadeMask[i] = 0xFFFFFFFFu;
        }

        private int[] _rangeSortScratch; // reusable scratch for BuildRangeTemplate

        private void BuildRangeTemplate(RangeSegment[] ranges, int rangeCount,
            ref NativeArray<BatchDrawRange> template, ref int outRangeCount, bool receiveShadows)
        {
            // sort ranges by key for correct draw order
            if (_rangeSortScratch == null || _rangeSortScratch.Length < rangeCount)
                _rangeSortScratch = new int[math.max(rangeCount, 16)];
            var order = _rangeSortScratch;
            int validCount = 0;
            for (int i = 0; i < rangeCount; i++) { if (ranges[i].count > 0) order[validCount++] = i; }
            Array.Sort(order, 0, validCount, new RangeKeyComparer(ranges));

            if (!template.IsCreated || template.Length < validCount)
            {
                template.DisposeIfCreated();
                template = validCount > 0 ? new NativeArray<BatchDrawRange>(validCount, Allocator.Persistent) : default;
            }

            for (int r = 0; r < validCount; r++)
            {
                var seg = ranges[order[r]];
                template[r] = new BatchDrawRange
                {
                    drawCommandsBegin = (uint)seg.begin,
                    drawCommandsCount = (uint)seg.count,
                    filterSettings = new BatchFilterSettings
                    {
                        renderingLayerMask = seg.renderingLayerMask, layer = (byte)seg.layer,
                        motionMode = seg.motionMode, shadowCastingMode = seg.shadowCastingMode,
                        receiveShadows = receiveShadows, staticShadowCaster = false, allDepthSorted = false
                    }
                };
            }
            outRangeCount = validCount;
        }

        private void UploadArgsToBucket(ref GraphicsBuffer buf, int[] data, int count, string name)
        {
            count = math.max(1, count);
            if (buf == null || buf.count < count)
            {
                buf?.Dispose();
                buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(int));
                buf.name = name;
            }
            if (data != null && data.Length >= count)
                buf.SetData(data, 0, 0, count);
        }

        private struct RangeKeyComparer : IComparer<int>
        {
            private readonly RangeSegment[] _r;
            public RangeKeyComparer(RangeSegment[] r) => _r = r;
            public int Compare(int a, int b)
            {
                int c = _r[a].layer.CompareTo(_r[b].layer); if (c != 0) return c;
                c = _r[a].renderingLayerMask.CompareTo(_r[b].renderingLayerMask); if (c != 0) return c;
                c = ((int)_r[a].motionMode).CompareTo((int)_r[b].motionMode); if (c != 0) return c;
                return ((int)_r[a].shadowCastingMode).CompareTo((int)_r[b].shadowCastingMode);
            }
        }

        #endregion

        #region Template Patching

        private static ShadowCastingMode GetRangeShadowModeForLOD(RenderSignature proto, int lodLevel, bool isShadow)
        {
            var modes = proto.lodShadowCastingModes;
            var mode = (modes != null && lodLevel >= 0 && lodLevel < modes.Length) ? modes[lodLevel] : ShadowCastingMode.On;
            return isShadow ? (mode == ShadowCastingMode.TwoSided ? ShadowCastingMode.TwoSided : ShadowCastingMode.On)
                : (mode == ShadowCastingMode.ShadowsOnly ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off);
        }

        static uint GetBucketPctMask(uint pctMask, int lodCount)
        {
            uint lodBits = (1u << lodCount) - 1u;
            uint activePct = pctMask & lodBits;
            if (activePct != 0 && activePct != lodBits)
                return pctMask & ~(1u << (31 - math.lzcnt(activePct)));
            return pctMask;
        }

        private void RebindBatchBufferHandle()
        {
            if (_matrixBuffer == null) return;
            EnsureSharedBatchID();
            EnsureSpeedTreeBatchIDs();
            for (int i = 0; i < _batchSlotHWM; i++)
                if (_batchSlotActive[i]) _batchSlots[i].batchID = GetBatchID(_batchSlots[i].signatureIndex);
            RestampDrawCommandTemplates();
        }

        // iterate per-range active commands and patch batchID from current GetBatchID
        private void RestampDrawCommandTemplates()
        {
            if (_cameraDrawCommandTemplate.IsCreated && _camCmd2Proto != null)
            {
                for (int ri = 0; ri < _camRangeCount; ri++)
                {
                    var range = _camRanges[ri];
                    for (int j = range.begin; j < range.begin + range.count; j++)
                    {
                        var tmpl = _cameraDrawCommandTemplate[j];
                        tmpl.batchID = GetBatchID(_camCmd2Proto[j]);
                        _cameraDrawCommandTemplate[j] = tmpl;
                    }
                }
            }
            if (_shadowDrawCommandTemplate.IsCreated && _shdCmd2Proto != null)
            {
                for (int ri = 0; ri < _shdRangeCount; ri++)
                {
                    var range = _shdRanges[ri];
                    for (int j = range.begin; j < range.begin + range.count; j++)
                    {
                        var tmpl = _shadowDrawCommandTemplate[j];
                        tmpl.batchID = GetBatchID(_shdCmd2Proto[j]);
                        _shadowDrawCommandTemplate[j] = tmpl;
                    }
                }
            }
        }

        private void DisposeDrawCommandTemplates()
        {
            _cameraDrawCommandTemplate.DisposeIfCreated();
            _cameraDrawRangeTemplate.DisposeIfCreated();
            _cameraIndirectArgsTemplate.DisposeIfCreated();
            _shadowDrawCommandTemplate.DisposeIfCreated();
            _shadowCommandFilterInfo.DisposeIfCreated();
            _shadowDrawRangeTemplate.DisposeIfCreated();
            _shadowIndirectArgsTemplate.DisposeIfCreated();
        }

        private void DisposeDrawOrderState()
        {
            _argsToBucketBuffer?.Dispose(); _argsToBucketBuffer = null;
            _shadowArgsToBucketBuffer?.Dispose(); _shadowArgsToBucketBuffer = null;
            _buckets.DisposeIfCreated();
            _shadowBuckets.DisposeIfCreated();
            _gpuSignatureScratch.DisposeIfCreated();
            _sigStagingData.DisposeIfCreated();
            _sigStagingIndices.DisposeIfCreated();
            _sigStagingGpuBuffer?.Dispose(); _sigStagingGpuBuffer = null;
            _sigStagingIndexGpuBuffer?.Dispose(); _sigStagingIndexGpuBuffer = null;
            _dirtySignatureProtos.Clear();
            _bucketCount = 0; _drawCommandCount = 0; _cameraDrawRangeCount = 0;
            _shadowBucketCount = 0; _shadowDrawCommandCount = 0; _shadowDrawRangeCount = 0;
            DisposeDrawCommandTemplates();
        }

        #endregion

        #region Prototype Bounds & GPU Signature

        private void CachePrototypeBounds()
        {
            int protoCount = _renderSignatures.Count;
            if (!_protoBoundsCenter.IsCreated || _protoBoundsCenter.Length != protoCount)
            {
                _protoBoundsCenter.DisposeIfCreated(); _protoBoundsRadius.DisposeIfCreated(); _protoLodReferenceSize.DisposeIfCreated();
                _protoBoundsCenter = new NativeArray<float3>(protoCount, Allocator.Persistent);
                _protoBoundsRadius = new NativeArray<float>(protoCount, Allocator.Persistent);
                _protoLodReferenceSize = new NativeArray<float>(protoCount, Allocator.Persistent);
            }
            EnsureProtoArrayCapacity(protoCount);
            _anyPrototypeUsesAnimatedCrossfade = false;
#if DEBUG
            _statAnimatedProtoCount = 0;
#endif
            for (int p = 0; p < protoCount; p++)
            {
                if (IsProtoVacant(p)) continue;
                RenderSignature proto = _renderSignatures[p];
                if (proto.mesh != null)
                {
                    var bounds = proto.mesh.bounds;
                    var protoAsset = proto.asset;
                    if (protoAsset?.lodMeshes != null && protoAsset.lodMeshes.Length > 1)
                    {
                        for (int li = 1; li < protoAsset.lodMeshes.Length; li++)
                            if (protoAsset.lodMeshes[li] != null) bounds.Encapsulate(protoAsset.lodMeshes[li].bounds);
                    }
                    _protoBoundsCenter[p] = bounds.center;
                    _protoBoundsRadius[p] = bounds.extents.magnitude;
                    _protoLodReferenceSize[p] = proto.lodReferenceSize;
                }
                else
                {
                    _protoBoundsCenter[p] = float3.zero;
                    _protoBoundsRadius[p] = 1f;
                    _protoLodReferenceSize[p] = 2f;
                }
                _prototypeMaxShadowDistance[p] = proto.maxShadowDistance;
                _prototypeMaxShadowCascade[p] = math.clamp(proto.maxShadowCascade, 1, 4);
                _prototypeCrossfadeEnabled[p] = !_config.debugDisableCrossfade && (proto.fadeMode == LODFadeMode.CrossFade || proto.fadeMode == LODFadeMode.SpeedTree);
                _prototypeAnimatedCrossfade[p] = _prototypeCrossfadeEnabled[p] && proto.animateCrossFading;
                if (_prototypeAnimatedCrossfade[p]) { _anyPrototypeUsesAnimatedCrossfade = true;
#if DEBUG
                    _statAnimatedProtoCount++;
#endif
                }
                _prototypeShadowMaskCache[p] = proto.GetLODShadowMask();
            }
            if (_chunkHasAnimatedCrossfade.IsCreated)
                for (int a = 0; a < _allocatedCount; a++) _chunkHasAnimatedCrossfade[_activeChunks[a]] = RecomputeChunkHasAnimatedCrossfade(_activeChunks[a]);
            _animatedPoolListDirty = true;
        }

        private void FlushSignatureUploads()
        {
            if (_gpuSignatureBuffer != null && _dirtySignatureProtos.Count == 0 && !_allSignaturesDirty) return;
            if (_renderSignatures.Count == 0) return;
            CachePrototypeBounds();
            UpdateGPUSignatureBuffer();
        }

        private NativeArray<GPUSignatureData> _gpuSignatureScratch;

        // partial signature upload staging
        private const int k_sigStagingCapacity = 64;
        private readonly HashSet<int> _dirtySignatureProtos = new();
        private bool _allSignaturesDirty;
        private NativeArray<GPUSignatureData> _sigStagingData;
        private NativeArray<int> _sigStagingIndices;
        private GraphicsBuffer _sigStagingGpuBuffer;
        private GraphicsBuffer _sigStagingIndexGpuBuffer;

        private void UpdateGPUSignatureBuffer()
        {
            int prototypeCount = _renderSignatures.Count;
            if (prototypeCount == 0 && _gpuSignatureBuffer == null) return;

            int requiredLen = math.max(1, prototypeCount);

            // ensure cpu scratch is large enough
            if (!_gpuSignatureScratch.IsCreated || _gpuSignatureScratch.Length < requiredLen)
            { _gpuSignatureScratch.DisposeIfCreated(); _gpuSignatureScratch = new NativeArray<GPUSignatureData>(requiredLen, Allocator.Persistent); }

            // buffer resize -> full upload
            bool needsResize = _gpuSignatureBuffer == null || _gpuSignatureBuffer.count != requiredLen;
            if (needsResize)
            {
                _gpuSignatureBuffer?.Dispose();
                _gpuSignatureBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, requiredLen, UnsafeUtility.SizeOf<GPUSignatureData>());
                _gpuSignatureBuffer.name = "FraxinusGPUBatcher_SignatureBuffer";
                _allSignaturesDirty = true;
            }

            // full upload path (resize, config change, clear all)
            if (_allSignaturesDirty)
            {
                for (int p = 0; p < prototypeCount; p++)
                    _gpuSignatureScratch[p] = PackSignatureData(p);
                _gpuSignatureBuffer.SetData(_gpuSignatureScratch, 0, 0, requiredLen);
                _allSignaturesDirty = false;
                _dirtySignatureProtos.Clear();

                return;
            }

            // nothing dirty
            if (_dirtySignatureProtos.Count == 0)
            {

                return;
            }

            // ensure staging arrays exist
            if (!_sigStagingData.IsCreated)
            {
                _sigStagingData = new NativeArray<GPUSignatureData>(k_sigStagingCapacity, Allocator.Persistent);
                _sigStagingIndices = new NativeArray<int>(k_sigStagingCapacity, Allocator.Persistent);
            }
            if (_sigStagingGpuBuffer == null)
            {
                _sigStagingGpuBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_sigStagingCapacity, UnsafeUtility.SizeOf<GPUSignatureData>());
                _sigStagingGpuBuffer.name = "FraxinusGPUBatcher_SigStaging";
                _sigStagingIndexGpuBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_sigStagingCapacity, sizeof(int));
                _sigStagingIndexGpuBuffer.name = "FraxinusGPUBatcher_SigStagingIdx";
            }

            // partial upload: pack dirty protos into staging, scatter write in batches
            int cursor = 0;
            foreach (int p in _dirtySignatureProtos)
            {
                if (p < 0 || p >= prototypeCount) continue;
                _gpuSignatureScratch[p] = PackSignatureData(p);
                _sigStagingData[cursor] = _gpuSignatureScratch[p];
                _sigStagingIndices[cursor] = p;
                cursor++;
                if (cursor >= k_sigStagingCapacity)
                {
                    FlushSignatureStaging(cursor);
                    cursor = 0;
                }
            }
            if (cursor > 0)
                FlushSignatureStaging(cursor);

            _dirtySignatureProtos.Clear();

        }

        private void FlushSignatureStaging(int count)
        {
            if (_scatterWriteSignaturesKernel < 0 || _uploadShader == null || count <= 0) return;
            _sigStagingGpuBuffer.SetData(_sigStagingData, 0, 0, count);
            _sigStagingIndexGpuBuffer.SetData(_sigStagingIndices, 0, 0, count);
            _uploadShader.SetBuffer(_scatterWriteSignaturesKernel, "_SigStagingSrc", _sigStagingGpuBuffer);
            _uploadShader.SetBuffer(_scatterWriteSignaturesKernel, "_SigStagingIndices", _sigStagingIndexGpuBuffer);
            _uploadShader.SetBuffer(_scatterWriteSignaturesKernel, "_SigScatterDst", _gpuSignatureBuffer);
            _uploadShader.SetInt("_SigScatterCount", count);
            DispatchSafe(_uploadShader, _scatterWriteSignaturesKernel, (count + 63) / 64);
        }

        private GPUSignatureData PackSignatureData(int p)
        {
            if (IsProtoVacant(p))
                return new GPUSignatureData { flags0 = 0, lodScreenSizes01 = GPUSignatureData.PackHalf2(1f, 1f), lodScreenSizes23 = GPUSignatureData.PackHalf2(1f, 1f), lodScreenSizes45 = GPUSignatureData.PackHalf2(1f, 1f), lodScreenSizes67 = GPUSignatureData.PackHalf2(1f, 1f) };

            var proto = _renderSignatures[p];
            GetLODScreenSizes(proto, out var s0, out var s1);
            GetLODFadeWidths(proto, out var w0, out var w1);

            // per-group runtime tuning: bias bakes into LOD thresholds, density packs into the signature
            var group = proto.prototypeGroup;
            float groupLodBias = group != null ? group.lodBias : 1f;
            float groupDensity = group != null ? group.density : 1f;
            if (groupLodBias > 0f && groupLodBias != 1f)
            {
                float inv = 1f / groupLodBias; // larger bias -> smaller thresholds -> finer LOD held longer
                s0 *= inv; s1 *= inv;
            }
            int lodCount = proto.lodCount;
            int densityMode = !proto.enableDistanceDensity ? 0 : (int)proto.distanceDensityMode + 1;
            int minShadowLOD = math.clamp(proto.minShadowLOD, 0, lodCount - 1);
            int maxShadowCascade = math.clamp(_prototypeMaxShadowCascade[p], 1, 4);
            int maxShadowCrossfadeCascade = proto.maxShadowCrossfadeCascade == -99 ? _config.maxShadowCrossfadeCascade : proto.maxShadowCrossfadeCascade;
            float fadeDuration = math.clamp(proto.lodFadeDuration, 0f, 10f);
            float dp1 = proto.distanceDensityMode == DistanceDensityMode.ScreenSize ? proto.densityStartScreenSize : proto.densityStartDistance;
            float dp2;
            if (proto.distanceDensityMode == DistanceDensityMode.RawDistance)
                dp2 = 1f / math.max(0.001f, proto.densityEndDistance - proto.densityStartDistance);
            else
            {
                // ScreenSize mode: density ramp ends at the LOD/screen cull threshold (last LOD screen size)
                dp2 = proto.lodScreenSizes != null && proto.lodScreenSizes.Length > 0
                    ? proto.lodScreenSizes[proto.lodScreenSizes.Length - 1]
                    : 0f;
                // ensure start > end so the ramp has positive range; otherwise density would snap
                if (dp1 <= dp2) dp1 = dp2 + 0.0001f;
            }

            return new GPUSignatureData
            {
                boundsCenter = _protoBoundsCenter[p],
                shadowDistanceSq = _prototypeMaxShadowDistance[p] > 0 ? _prototypeMaxShadowDistance[p] * _prototypeMaxShadowDistance[p] : 0f,
                lodScreenSizes01 = GPUSignatureData.PackHalf2(s0.x, s0.y), lodScreenSizes23 = GPUSignatureData.PackHalf2(s0.z, s0.w),
                lodScreenSizes45 = GPUSignatureData.PackHalf2(s1.x, s1.y), lodScreenSizes67 = GPUSignatureData.PackHalf2(s1.z, s1.w),
                lodFadeWidths01 = GPUSignatureData.PackHalf2(w0.x, w0.y), lodFadeWidths23 = GPUSignatureData.PackHalf2(w0.z, w0.w),
                lodFadeWidths45 = GPUSignatureData.PackHalf2(w1.x, w1.y), lodFadeWidths67 = GPUSignatureData.PackHalf2(w1.z, w1.w),
                boundsRadiusLodSize = GPUSignatureData.PackHalf2(_protoBoundsRadius[p], _protoLodReferenceSize[p]),
                flags0 = GPUSignatureData.PackFlags0(lodCount, _prototypeCrossfadeEnabled[p], _prototypeAnimatedCrossfade[p], densityMode, _prototypeShadowMaskCache[p],
                    _perProtoPercentageFadeMask != null && p < _perProtoPercentageFadeMask.Length ? _perProtoPercentageFadeMask[p] : 0xFFFFFFFFu,
                    minShadowLOD, maxShadowCascade, maxShadowCrossfadeCascade),
                flags1 = GPUSignatureData.PackFlags1(
                    _perProtoShadowCrossfadeCascades != null && p < _perProtoShadowCrossfadeCascades.Length ? _perProtoShadowCrossfadeCascades[p] : 0,
                    _perProtoShadowLodStride != null && p < _perProtoShadowLodStride.Length ? math.max(1, _perProtoShadowLodStride[p]) : 1),
                densMinFracFadeDur = GPUSignatureData.PackScaledUNORM16(proto.densityMinFraction, 1f, fadeDuration, 10f),
                densParam1DensParam2 = GPUSignatureData.PackHalf2(dp1, dp2),
                fadeWidthPad = GPUSignatureData.PackHalf2(proto.densityFadeWidth, groupDensity),
                cameraBucketOffset = (uint)(_cameraBucketOffsets != null && p < _cameraBucketOffsets.Length ? _cameraBucketOffsets[p] : 0),
                shadowBucketOffset = (uint)(_shadowBucketOffsets != null && p < _shadowBucketOffsets.Length ? _shadowBucketOffsets[p] : 0),
                flipBucketOffset = (uint)(_protoFlipBucketOffsets != null && p < _protoFlipBucketOffsets.Length ? _protoFlipBucketOffsets[p] : 0),
                shadowFlipBucketOffset = (uint)(_protoShadowFlipBucketOffsets != null && p < _protoShadowFlipBucketOffsets.Length ? _protoShadowFlipBucketOffsets[p] : 0)
            };
        }

        #endregion

        #region LOD Helpers

        private void GetLODScreenSizes(RenderSignature proto, out float4 sizes0, out float4 sizes1)
        {
            var s = proto.lodScreenSizes;
            if (s != null && s.Length > 0)
            { int n = s.Length; sizes0 = new float4(s[0], n > 1 ? s[1] : 0f, n > 2 ? s[2] : 0f, n > 3 ? s[3] : 0f); sizes1 = new float4(n > 4 ? s[4] : 0f, n > 5 ? s[5] : 0f, n > 6 ? s[6] : 0f, n > 7 ? s[7] : 0f); }
            else { sizes0 = float4.zero; sizes1 = float4.zero; }
        }

        private void GetLODFadeWidths(RenderSignature proto, out float4 widths0, out float4 widths1)
        {
            var w = proto.lodFadeTransitionWidths;
            if (w != null && w.Length > 0)
            { int n = w.Length; widths0 = new float4(w[0], n > 1 ? w[1] : .5f, n > 2 ? w[2] : .5f, n > 3 ? w[3] : .5f); widths1 = new float4(n > 4 ? w[4] : .5f, n > 5 ? w[5] : .5f, n > 6 ? w[6] : .5f, n > 7 ? w[7] : .5f); }
            else { widths0 = new float4(.5f); widths1 = new float4(.5f); }
        }

        #endregion
    }
}
