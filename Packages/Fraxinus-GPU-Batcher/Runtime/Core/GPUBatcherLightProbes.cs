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

        private NativeArray<SHCoefficients> _packBuffer;

        private static readonly int s_bytesPerInstance = GPUBatcherRenderer.shBytesPerInstance;      // 128
        private const int k_initialManagedArraySize = 256;
        private const int k_initialStagingUintCapacity = 32768;

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
            _packBuffer = new NativeArray<SHCoefficients>(initialArraySize, Allocator.Persistent);

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
            if (_packBuffer.Length < count)
            {
                _packBuffer.DisposeIfCreated();
                _packBuffer = new NativeArray<SHCoefficients>(count, Allocator.Persistent);
            }

            for (int i = 0; i < count; i++)
            {
                int sample = srcIndex + i;
                _packBuffer[i] = new SHCoefficients(_shResults[sample], _occlusionResults[sample]);
            }

            int uintCount = count * (s_bytesPerInstance / sizeof(uint));
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

            var packAsUint = _packBuffer.Reinterpret<uint>(s_bytesPerInstance);
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
