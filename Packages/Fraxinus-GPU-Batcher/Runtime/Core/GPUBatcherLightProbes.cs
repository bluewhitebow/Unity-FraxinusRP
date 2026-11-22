// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Fraxinus.GPUBatcher
{
    /// <summary>per-instance SH light probe sampling, packing, and GPU staging.</summary>
    internal class GPUBatcherLightProbes : IDisposable
    {
        private GPUBatcherRenderer _renderer;
        private bool _initialized;

        // position cache for re-sampling on light probe bake
        private NativeArray<float3> _cachedPositions;
        private int _totalSlots;

        private GraphicsBuffer _stagingSHBuffer;
        private int _stagingSHUintCapacity;
        private int _stagingSHUintCursor;

        private struct StagedSHUpload
        {
            public int stagingOffsetBytes;
            public int liveSlotOffset;
            public int count;
        }

        private NativeList<StagedSHUpload> _stagedSHUploads;
        private bool _shDirty;

        private SphericalHarmonicsL2 _cachedAmbientProbe;
        private bool _ambientProbeCached;

        // reusable managed arrays
        private Vector3[] _samplePositions;
        private SphericalHarmonicsL2[] _shResults;
        private Vector4[] _occlusionResults;

        // packing scratch - 8 float4 per instance, AoS
        private NativeArray<float4> _packBuffer;

        private static readonly int s_float4sPerInstance = GPUBatcherRenderer.shFloat4sPerInstance;
        private static readonly int s_bytesPerInstance = GPUBatcherRenderer.shBytesPerInstance;      // 128
        private const int k_initialManagedArraySize = 256;
        private const int k_initialStagingUintCapacity = 32768;

        // SH coefficient index reference:
        // sphericalHarmonicsL2[channel, coefficient]:
        //   channel: 0=R, 1=G, 2=B
        //   coefficients: 0=Y00(DC), 1=Y1-1, 2=Y10, 3=Y11, 4=Y2-2, 5=Y2-1, 6=Y20, 7=Y21, 8=Y22

        public void Initialize(GPUBatcherRenderer renderer, int totalSlots)
        {
            if (_initialized) return;

            _renderer = renderer;
            _totalSlots = totalSlots;

            _cachedPositions = new NativeArray<float3>(totalSlots, Allocator.Persistent);

            _stagedSHUploads = new NativeList<StagedSHUpload>(64, Allocator.Persistent);
            _stagingSHUintCapacity = 0;
            _stagingSHUintCursor = 0;
            _shDirty = false;

            int initialArraySize = math.max(GPUBatcherRenderer.instancesPerPool, k_initialManagedArraySize);
            _samplePositions = new Vector3[initialArraySize];
            _shResults = new SphericalHarmonicsL2[initialArraySize];
            _occlusionResults = new Vector4[initialArraySize];
            _packBuffer = new NativeArray<float4>(initialArraySize * s_float4sPerInstance, Allocator.Persistent);

            LightProbes.lightProbesUpdated += OnLightProbesUpdated;
            SceneManager.sceneLoaded += OnSceneLoaded;

            _initialized = true;
        }

        public void Dispose()
        {
            if (!_initialized) return;

            LightProbes.lightProbesUpdated -= OnLightProbesUpdated;
            SceneManager.sceneLoaded -= OnSceneLoaded;

            _cachedPositions.DisposeIfCreated();
            _stagedSHUploads.DisposeIfCreated();
            _packBuffer.DisposeIfCreated();

            _stagingSHBuffer?.Dispose();
            _stagingSHBuffer = null;

            _samplePositions = null;
            _shResults = null;
            _occlusionResults = null;
            _renderer = null;
            _initialized = false;
        }

        public void OnPoolResize(int newTotalSlots)
        {
            if (!_initialized) return;

            var newPositions = new NativeArray<float3>(newTotalSlots, Allocator.Persistent);
            if (_cachedPositions.IsCreated)
            {
                int copyCount = math.min(_totalSlots, newTotalSlots);
                NativeArray<float3>.Copy(_cachedPositions, 0, newPositions, 0, copyCount);
                _cachedPositions.Dispose();
            }
            _cachedPositions = newPositions;
            _totalSlots = newTotalSlots;
            _stagingSHUintCapacity = 0;
        }

        public void SampleAndStageSingle(in InstanceData instance, int liveSlotIndex)
        {
            if (!_initialized) return;

            EnsureArrayCapacity(1);

            var pos = instance.position;
            _samplePositions[0] = new Vector3(pos.x, pos.y, pos.z);
            _cachedPositions[liveSlotIndex] = pos;

            LightProbes.CalculateInterpolatedLightAndOcclusionProbes(
                _samplePositions, _shResults, _occlusionResults);

            PackAndStage(0, 1, liveSlotIndex);
        }

        // one managed probe query for a whole chunk write; stage per slot range via StageBatchRange
        public void BeginBatchSample(NativeArray<InstanceData> instances, int srcOffset, int count)
        {
            if (!_initialized || count <= 0) return;

            EnsureArrayCapacity(count);

            for (int i = 0; i < count; i++)
            {
                var pos = instances[srcOffset + i].position;
                _samplePositions[i] = new Vector3(pos.x, pos.y, pos.z);
            }

            LightProbes.CalculateInterpolatedLightAndOcclusionProbes(
                _samplePositions, _shResults, _occlusionResults);
        }

        public void StageBatchRange(int batchIndex, int count, int liveSlotOffset)
        {
            if (!_initialized || count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                var p = _samplePositions[batchIndex + i];
                _cachedPositions[liveSlotOffset + i] = new float3(p.x, p.y, p.z);
            }

            PackAndStage(batchIndex, count, liveSlotOffset);
        }

        public void ClearSlots(int slotOffset, int count)
        {
            if (!_initialized || !_cachedPositions.IsCreated) return;

            for (int i = 0; i < count && slotOffset + i < _totalSlots; i++)
                _cachedPositions[slotOffset + i] = float3.zero;
        }

        public void Flush()
        {
            if (!_shDirty) return;

            int totalSlots = _renderer.poolCapacity * _renderer.poolSize;
            int shBaseBytes = totalSlots * 3 * 12 * sizeof(float); // byte offset after O2W + W2O + PrevM

            for (int i = 0; i < _stagedSHUploads.Length; i++)
            {
                var upload = _stagedSHUploads[i];

                // each instance is 128 bytes contiguous
                int srcOffsetBytes = upload.stagingOffsetBytes;
                int dstOffsetBytes = shBaseBytes + upload.liveSlotOffset * s_bytesPerInstance;
                int sizeBytes = upload.count * s_bytesPerInstance;

                _renderer.DispatchCopyBufferRegion(
                    _stagingSHBuffer, srcOffsetBytes,
                    _renderer.matrixBuffer, dstOffsetBytes,
                    sizeBytes);
            }

            _stagedSHUploads.Clear();
            _stagingSHUintCursor = 0;
            _shDirty = false;
        }

        public void ResampleAllLightProbes()
        {
            if (!_initialized || _renderer == null) return;

            var poolInUse = _renderer.poolInUse;
            var slotMask = _renderer.poolSlotMask;
            int poolCapacity = _renderer.poolCapacity;

            if (!poolInUse.IsCreated || !slotMask.IsCreated || !_cachedPositions.IsCreated)
                return;

            var activeSlots = new NativeList<int>(1024, Allocator.Temp);

            for (int poolIdx = 0; poolIdx < poolCapacity; poolIdx++)
            {
                if (!poolInUse[poolIdx]) continue;

                ulong mask = slotMask[poolIdx];
                int slotBase = poolIdx * GPUBatcherRenderer.instancesPerPool;
                while (mask != 0)
                {
                    int bit = math.tzcnt(mask);
                    mask &= mask - 1;

                    int globalSlot = slotBase + bit;
                    if (globalSlot < _totalSlots)
                        activeSlots.Add(globalSlot);
                }
            }

            if (activeSlots.Length == 0)
            {
                activeSlots.Dispose();
                return;
            }

            int batchSize = 1024;
            for (int batchStart = 0; batchStart < activeSlots.Length; batchStart += batchSize)
            {
                int count = math.min(batchSize, activeSlots.Length - batchStart);
                EnsureArrayCapacity(count);

                for (int i = 0; i < count; i++)
                {
                    var pos = _cachedPositions[activeSlots[batchStart + i]];
                    _samplePositions[i] = (Vector3)pos;
                }

                LightProbes.CalculateInterpolatedLightAndOcclusionProbes(
                    _samplePositions, _shResults, _occlusionResults);

                // coalesce consecutive slots into runs - one staged upload per run, not per instance
                int runStart = 0;
                while (runStart < count)
                {
                    int runEnd = runStart + 1;
                    while (runEnd < count &&
                           activeSlots[batchStart + runEnd] == activeSlots[batchStart + runEnd - 1] + 1)
                        runEnd++;

                    PackAndStage(runStart, runEnd - runStart, activeSlots[batchStart + runStart]);
                    runStart = runEnd;
                }
            }

            activeSlots.Dispose();
        }

        public void Tick()
        {
            if (!_initialized) return;
            TickDeferredResample();
            Flush();
        }

        // LightProbes.lightProbesUpdated doesn't fire in builds; re-sample when ambient probe changes
        private void TickDeferredResample()
        {
            var ambient = RenderSettings.ambientProbe;
            if (_ambientProbeCached && _cachedAmbientProbe == ambient) return;

            _cachedAmbientProbe = ambient;
            _ambientProbeCached = true;
            ResampleAllLightProbes();
        }

        #region Private Helpers

        private void PackAndStage(int srcIndex, int count, int liveSlotOffset)
        {
            int needed = count * s_float4sPerInstance;
            if (!_packBuffer.IsCreated || _packBuffer.Length < needed)
            {
                _packBuffer.DisposeIfCreated();
                _packBuffer = new NativeArray<float4>(math.max(needed, k_initialManagedArraySize * s_float4sPerInstance), Allocator.Persistent);
            }

            // pack SH data in AoS layout, matches SetupDOTSSHCoeffs() in UnityDOTSInstancing.hlsl
            for (int i = 0; i < count; i++)
            {
                var sh = _shResults[srcIndex + i];
                int baseIdx = i * s_float4sPerInstance;

                // SHAr: (L11_r, L1-1_r, L10_r, L00_r)
                _packBuffer[baseIdx + 0] = new float4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0]);
                // SHAg
                _packBuffer[baseIdx + 1] = new float4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0]);
                // SHAb
                _packBuffer[baseIdx + 2] = new float4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0]);
                // SHBr: (L2-2_r, L2-1_r, L20_r, L21_r)
                _packBuffer[baseIdx + 3] = new float4(sh[0, 4], sh[0, 5], sh[0, 6], sh[0, 7]);
                // SHBg
                _packBuffer[baseIdx + 4] = new float4(sh[1, 4], sh[1, 5], sh[1, 6], sh[1, 7]);
                // SHBb
                _packBuffer[baseIdx + 5] = new float4(sh[2, 4], sh[2, 5], sh[2, 6], sh[2, 7]);
                // SHC: (L22_r, L22_g, L22_b, 1.0)
                _packBuffer[baseIdx + 6] = new float4(sh[0, 8], sh[1, 8], sh[2, 8], 1.0f);
                var occ = _occlusionResults[srcIndex + i];
                _packBuffer[baseIdx + 7] = new float4(occ.x, occ.y, occ.z, occ.w);
            }

            int uintCount = count * s_float4sPerInstance * 4;
            int uintCursor = _stagingSHUintCursor;

            if (_stagingSHBuffer != null && uintCursor + uintCount > _stagingSHUintCapacity)
            {
                Flush();
                _stagedSHUploads.Clear();
                _stagingSHUintCursor = 0;
                _shDirty = false;
                uintCursor = 0;
            }

            EnsureStagingCapacity(uintCursor + uintCount);

            var packAsUint = _packBuffer.Reinterpret<uint>(16);
            _stagingSHBuffer.SetData(packAsUint, 0, uintCursor, uintCount);

            // extend the last entry when the new range continues it - one region copy per run
            bool merged = false;
            int n = _stagedSHUploads.Length;
            if (n > 0)
            {
                var last = _stagedSHUploads[n - 1];
                if (last.stagingOffsetBytes + last.count * s_bytesPerInstance == uintCursor * 4
                    && last.liveSlotOffset + last.count == liveSlotOffset)
                {
                    last.count += count;
                    _stagedSHUploads[n - 1] = last;
                    merged = true;
                }
            }
            if (!merged)
                _stagedSHUploads.Add(new StagedSHUpload
                {
                    stagingOffsetBytes = uintCursor * 4,
                    liveSlotOffset = liveSlotOffset,
                    count = count
                });

            _stagingSHUintCursor = uintCursor + uintCount;
            _shDirty = true;
        }

        private void EnsureStagingCapacity(int requiredUintCount)
        {
            if (_stagingSHBuffer != null && requiredUintCount <= _stagingSHUintCapacity)
                return;

            int newCapacity = math.max(requiredUintCount, math.max(_stagingSHUintCapacity * 2, k_initialStagingUintCapacity));

            _stagingSHBuffer?.Dispose();
            _stagingSHBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newCapacity, sizeof(uint));
            _stagingSHBuffer.name = "FraxinusGPUBatcher_StagingSH";
            _stagingSHUintCapacity = newCapacity;
        }

        private void EnsureArrayCapacity(int count)
        {
            if (_samplePositions != null && _samplePositions.Length >= count)
                return;
            _samplePositions = new Vector3[count];
            _shResults = new SphericalHarmonicsL2[count];
            _occlusionResults = new Vector4[count];
        }

        private void OnLightProbesUpdated()
        {
            ResampleAllLightProbes();
        }

        // additive scene may add probes without changing the ambient probe
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Additive)
                ResampleAllLightProbes();
        }

        #endregion
    }
}
