using System;
using System.Linq;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.SceneManagement;
using UnityEditor;
using Unity.Mathematics;

using Brick = UnityEngine.Rendering.ProbeBrickIndex.Brick;
using IndirectionEntryInfo = UnityEngine.Rendering.IndirectionEntryInfo;
using static UnityEngine.Rendering.AdaptiveProbeVolumes;

using TouchupVolumeWithBoundsList = System.Collections.Generic.List<(UnityEngine.Rendering.TProbeReferenceVolumeBase.Volume obb, UnityEngine.Bounds aabb, UnityEngine.Rendering.ProbeAdjustmentVolume volume)>;

namespace UnityEngine.Rendering
{
    struct BakingCell
    {
        public Vector3Int position;
        public int index;

        public Brick[] bricks;
        public Vector3[] probePositions;
        public SphericalHarmonicsL2[] sh;
        public byte[,] validityNeighbourMask;
        public Vector4[] skyOcclusionDataL0L1;
        public byte[] skyShadingDirectionIndices;
        public float[] validity;
        public Vector4[] probeOcclusion;
        public byte[] layerValidity;
        public Vector3[] offsetVectors;
        public float[] touchupVolumeInteraction;

        public int minSubdiv;
        public int indexChunkCount;
        public int shChunkCount;
        public IndirectionEntryInfo[] indirectionEntryInfo;

        public int[] probeIndices;

        public Bounds bounds;

        internal void ComputeBounds(float cellSize)
        {
            var center = new Vector3((position.x + 0.5f) * cellSize, (position.y + 0.5f) * cellSize, (position.z + 0.5f) * cellSize);
            bounds = new Bounds(center, new Vector3(cellSize, cellSize, cellSize));
        }

        internal TouchupVolumeWithBoundsList SelectIntersectingAdjustmentVolumes(TouchupVolumeWithBoundsList touchupVolumesAndBounds)
        {
            // Find the subset of touchup volumes that will be considered for this cell.
            // Capacity of the list to cover the worst case.
            var localTouchupVolumes = new TouchupVolumeWithBoundsList(touchupVolumesAndBounds.Count);
            foreach (var touchup in touchupVolumesAndBounds)
            {
                if (touchup.aabb.Intersects(bounds))
                    localTouchupVolumes.Add(touchup);
            }
            return localTouchupVolumes;
        }

        static internal void CompressSH(ref SphericalHarmonicsL2 shv, float intensityScale, bool clearForDilation)
        {
            // Compress the range of all coefficients but the DC component to [0..1]
            // Upper bounds taken from http://ppsloan.org/publications/Sig20_Advances.pptx
            // Divide each coefficient by DC*f to get to [-1,1] where f is from slide 33
            for (int rgb = 0; rgb < 3; ++rgb)
            {
                for (int k = 0; k < 9; ++k)
                    shv[rgb, k] *= intensityScale;

                var l0 = shv[rgb, 0];

                if (l0 == 0.0f)
                {
                    shv[rgb, 0] = 0.0f;
                    for (int k = 1; k < 9; ++k)
                        shv[rgb, k] = 0.5f;
                }
                else if (clearForDilation)
                {
                    for (int k = 0; k < 9; ++k)
                        shv[rgb, k] = 0.0f;
                }
                else
                {
                    // TODO: We're working on irradiance instead of radiance coefficients
                    //       Add safety margin 2 to avoid out-of-bounds values
                    float l1scale = 2.0f; // Should be: 3/(2*sqrt(3)) * 2, but rounding to 2 to issues we are observing.
                    float l2scale = 3.5777088f; // 4/sqrt(5) * 2

                    // L_1^m
                    shv[rgb, 1] = shv[rgb, 1] / (l0 * l1scale * 2.0f) + 0.5f;
                    shv[rgb, 2] = shv[rgb, 2] / (l0 * l1scale * 2.0f) + 0.5f;
                    shv[rgb, 3] = shv[rgb, 3] / (l0 * l1scale * 2.0f) + 0.5f;

                    // L_2^-2
                    shv[rgb, 4] = shv[rgb, 4] / (l0 * l2scale * 2.0f) + 0.5f;
                    shv[rgb, 5] = shv[rgb, 5] / (l0 * l2scale * 2.0f) + 0.5f;
                    shv[rgb, 6] = shv[rgb, 6] / (l0 * l2scale * 2.0f) + 0.5f;
                    shv[rgb, 7] = shv[rgb, 7] / (l0 * l2scale * 2.0f) + 0.5f;
                    shv[rgb, 8] = shv[rgb, 8] / (l0 * l2scale * 2.0f) + 0.5f;

                    for (int coeff = 1; coeff < 9; ++coeff)
                        shv[rgb, coeff] = Mathf.Clamp01(shv[rgb, coeff]);
                }
            }
        }

        static internal void DecompressSH(ref SphericalHarmonicsL2 shv)
        {
            for (int rgb = 0; rgb < 3; ++rgb)
            {
                var l0 = shv[rgb, 0];

                // See CompressSH
                float l1scale = 2.0f;
                float l2scale = 3.5777088f;

                // L_1^m
                shv[rgb, 1] = (shv[rgb, 1] - 0.5f) * (l0 * l1scale * 2.0f);
                shv[rgb, 2] = (shv[rgb, 2] - 0.5f) * (l0 * l1scale * 2.0f);
                shv[rgb, 3] = (shv[rgb, 3] - 0.5f) * (l0 * l1scale * 2.0f);

                // L_2^-2
                shv[rgb, 4] = (shv[rgb, 4] - 0.5f) * (l0 * l2scale * 2.0f);
                shv[rgb, 5] = (shv[rgb, 5] - 0.5f) * (l0 * l2scale * 2.0f);
                shv[rgb, 6] = (shv[rgb, 6] - 0.5f) * (l0 * l2scale * 2.0f);
                shv[rgb, 7] = (shv[rgb, 7] - 0.5f) * (l0 * l2scale * 2.0f);
                shv[rgb, 8] = (shv[rgb, 8] - 0.5f) * (l0 * l2scale * 2.0f);
            }
        }

        void SetSHCoefficients(int i, SphericalHarmonicsL2 value, float intensityScale, float valid, in ProbeDilationSettings dilationSettings)
        {
            bool clearForDilation = dilationSettings.enableDilation && dilationSettings.dilationDistance > 0.0f && valid > dilationSettings.dilationValidityThreshold;
            CompressSH(ref value, intensityScale, clearForDilation);

            SphericalHarmonicsL2Utils.SetL0(ref sh[i], new Vector3(value[0, 0], value[1, 0], value[2, 0]));
            SphericalHarmonicsL2Utils.SetL1R(ref sh[i], new Vector3(value[0, 3], value[0, 1], value[0, 2]));
            SphericalHarmonicsL2Utils.SetL1G(ref sh[i], new Vector3(value[1, 3], value[1, 1], value[1, 2]));
            SphericalHarmonicsL2Utils.SetL1B(ref sh[i], new Vector3(value[2, 3], value[2, 1], value[2, 2]));

            SphericalHarmonicsL2Utils.SetCoefficient(ref sh[i], 4, new Vector3(value[0, 4], value[1, 4], value[2, 4]));
            SphericalHarmonicsL2Utils.SetCoefficient(ref sh[i], 5, new Vector3(value[0, 5], value[1, 5], value[2, 5]));
            SphericalHarmonicsL2Utils.SetCoefficient(ref sh[i], 6, new Vector3(value[0, 6], value[1, 6], value[2, 6]));
            SphericalHarmonicsL2Utils.SetCoefficient(ref sh[i], 7, new Vector3(value[0, 7], value[1, 7], value[2, 7]));
            SphericalHarmonicsL2Utils.SetCoefficient(ref sh[i], 8, new Vector3(value[0, 8], value[1, 8], value[2, 8]));
        }

        void ReadAdjustmentVolumes(ProbeVolumeBakingSet bakingSet, BakingBatch bakingBatch, TouchupVolumeWithBoundsList localTouchupVolumes, int i, float validity,
            ref byte validityMask, out bool invalidatedProbe, out float intensityScale, out uint? skyShadingDirectionOverride)
        {
            invalidatedProbe = false;
            intensityScale = 1.0f;
            skyShadingDirectionOverride = null;

            foreach (var touchup in localTouchupVolumes)
            {
                var touchupBound = touchup.aabb;
                var touchupVolume = touchup.volume;

                // We check a small box around the probe to give some leniency (a couple of centimeters).
                var probeBounds = new Bounds(probePositions[i], new Vector3(0.02f, 0.02f, 0.02f));
                if (touchupVolume.IntersectsVolume(touchup.obb, touchup.aabb, probeBounds))
                {
                    if (touchupVolume.mode == ProbeAdjustmentVolume.Mode.InvalidateProbes)
                    {
                        invalidatedProbe = true;

                        if (validity < 0.05f) // We just want to add probes that were not already invalid or close to.
                        {
                            // We check as below 1 but bigger than 0 in the debug shader, so any value <1 will do to signify touched up.
                            touchupVolumeInteraction[i] = 0.5f;

                            bakingBatch.forceInvalidatedProbesAndTouchupVols[probePositions[i]] = touchupBound;
                        }
                        break;
                    }
                    else if (touchupVolume.mode == ProbeAdjustmentVolume.Mode.OverrideValidityThreshold)
                    {
                        float thresh = (1.0f - touchupVolume.overriddenDilationThreshold);
                        // The 1.0f + is used to determine the action (debug shader tests above 1), then we add the threshold to be able to retrieve it in debug phase.
                        touchupVolumeInteraction[i] = 1.0f + thresh;
                        bakingBatch.customDilationThresh[(index, i)] = thresh;
                    }
                    else if (touchupVolume.mode == ProbeAdjustmentVolume.Mode.OverrideSkyDirection && bakingSet.skyOcclusion && bakingSet.skyOcclusionShadingDirection)
                    {
                        skyShadingDirectionOverride = AdaptiveProbeVolumes.SkyOcclusionBaker.EncodeSkyShadingDirection(touchupVolume.skyDirection);
                    }
                    else if (touchupVolume.mode == ProbeAdjustmentVolume.Mode.OverrideRenderingLayerMask && bakingSet.useRenderingLayers)
                    {
                        switch (touchupVolume.renderingLayerMaskOperation)
                        {
                            case ProbeAdjustmentVolume.RenderingLayerMaskOperation.Override:
                                validityMask = touchupVolume.renderingLayerMask;
                                break;
                            case ProbeAdjustmentVolume.RenderingLayerMaskOperation.Add:
                                validityMask |= touchupVolume.renderingLayerMask;
                                break;
                            case ProbeAdjustmentVolume.RenderingLayerMaskOperation.Remove:
                                validityMask &= (byte)(~touchupVolume.renderingLayerMask);
                                break;
                        }
                    }

                    if (touchupVolume.mode == ProbeAdjustmentVolume.Mode.IntensityScale)
                        intensityScale = touchupVolume.intensityScale;
                    if (intensityScale != 1.0f)
                        touchupVolumeInteraction[i] = 2.0f + intensityScale;
                }
            }

            if (validity < 0.05f && bakingBatch.invalidatedPositions.ContainsKey(probePositions[i]) && bakingBatch.invalidatedPositions[probePositions[i]])
            {
                if (!bakingBatch.forceInvalidatedProbesAndTouchupVols.ContainsKey(probePositions[i]))
                    bakingBatch.forceInvalidatedProbesAndTouchupVols.Add(probePositions[i], new Bounds());

                invalidatedProbe = true;
            }
        }

        internal void SetBakedData(ProbeVolumeBakingSet bakingSet, BakingBatch bakingBatch, TouchupVolumeWithBoundsList localTouchupVolumes, int i, int probeIndex,
            in SphericalHarmonicsL2 sh, float validity, NativeArray<uint> renderingLayerMasks, NativeArray<Vector3> virtualOffsets, NativeArray<Vector4> skyOcclusion, NativeArray<uint> skyDirection, NativeArray<Vector4> probeOcclusion)
        {
            byte layerValidityMask = (byte)(renderingLayerMasks.IsCreated ? renderingLayerMasks[probeIndex] : 0);

            ReadAdjustmentVolumes(bakingSet, bakingBatch, localTouchupVolumes, i, validity, ref layerValidityMask, out var invalidatedProbe, out var intensityScale, out var skyShadingDirectionOverride);
            SetSHCoefficients(i, sh, intensityScale, validity, bakingSet.settings.dilationSettings);

            if (virtualOffsets.IsCreated)
                offsetVectors[i] = virtualOffsets[probeIndex];

            if (skyOcclusion.IsCreated)
            {
                skyOcclusionDataL0L1[i] = skyOcclusion[probeIndex];

                if (skyDirection.IsCreated)
                    skyShadingDirectionIndices[i] = (byte)(skyShadingDirectionOverride ?? skyDirection[probeIndex]);
            }

            if (renderingLayerMasks.IsCreated)
                layerValidity[i] = layerValidityMask;

            float currValidity = invalidatedProbe ? 1.0f : validity;
            byte currValidityNeighbourMask = 255;
            this.validity[i] = currValidity;
            for (int l = 0; l < APVDefinitions.probeMaxRegionCount; l++)
                validityNeighbourMask[l, i] = currValidityNeighbourMask;

            if (probeOcclusion.IsCreated)
                this.probeOcclusion[i] = probeOcclusion[probeIndex];
        }

        internal int GetBakingHashCode()
        {
            int hash = position.GetHashCode();
            hash = hash * 23 + minSubdiv.GetHashCode();
            hash = hash * 23 + indexChunkCount.GetHashCode();
            hash = hash * 23 + shChunkCount.GetHashCode();

            foreach (var brick in bricks)
            {
                hash = hash * 23 + brick.position.GetHashCode();
                hash = hash * 23 + brick.subdivisionLevel.GetHashCode();
            }
            return hash;
        }
    }

    class BakingBatch
    {
        public Dictionary<int, HashSet<string>> cellIndex2SceneReferences = new ();
        public List<BakingCell> cells = new ();
        // Used to retrieve probe data from it's position in order to fix seams
        public Dictionary<int, int> positionToIndex = new ();
        // Allow to get a mapping to subdiv level with the unique positions. It stores the minimum subdiv level found for a given position.
        // Can be probably done cleaner.
        public Dictionary<int, int> uniqueBrickSubdiv = new ();
        // Mapping for explicit invalidation, whether it comes from the auto finding of occluders or from the touch up volumes
        // TODO: This is not used yet. Will soon.
        public Dictionary<Vector3, bool> invalidatedPositions = new ();
        // Utilities to compute unique probe position hash
        Vector3Int maxBrickCount;
        float inverseScale;
        Vector3 offset;

        public Dictionary<(int, int), float> customDilationThresh = new ();
        public Dictionary<Vector3, Bounds> forceInvalidatedProbesAndTouchupVols = new ();

        private GIContributors? m_Contributors;
        public GIContributors contributors
        {
            get
            {
                if (!m_Contributors.HasValue)
                    m_Contributors = GIContributors.Find(GIContributors.ContributorFilter.All);
                return m_Contributors.Value;
            }
        }

        private BakingBatch() { }

        public BakingBatch(Vector3Int cellCount, TProbeReferenceVolumeBase volume)
        {
            maxBrickCount = cellCount * TProbeReferenceVolumeBase.CellSize(volume.GetMaxSubdivision());
            inverseScale = ProbeBrickPoolBase.brickCellCount / volume.MinBrickSize();
            offset = volume.ProbeOffset();
        }

        public int GetProbePositionHash(Vector3 position)
        {
            var brickPosition = Vector3Int.RoundToInt((position - offset) * inverseScale); // Inverse of op in ConvertBricksToPositions()
            return GetBrickPositionHash(brickPosition);
        }

        public int GetBrickPositionHash(Vector3Int brickPosition)
        {
            return brickPosition.x + brickPosition.y * maxBrickCount.x + brickPosition.z * maxBrickCount.x * maxBrickCount.y;
        }

        public int GetSubdivLevelAt(Vector3 position) => uniqueBrickSubdiv[GetProbePositionHash(position)];
    }

    /// <summary>
    /// Class responsible for baking of Probe Volumes
    /// </summary>
    internal sealed partial class APVProbeVolumeBaking : ProbeVolumeBaking<APVProbeReferenceVolume>
    {
        internal abstract class BakingProfiling<T> where T : Enum
        {
            protected virtual string LogFile => null; // Override in child classes to write profiling data to disk
            protected virtual bool ShowProgressBar => true;

            protected T prevStage;
            bool disposed = false;
            static float globalProgress = 0.0f;

            public float GetProgress(T stage) => (int)(object)stage / (float)(int)(object)GetLastStep();
            void UpdateProgressBar(T stage)
            {
                if (!ShowProgressBar)
                    return;

                if (EqualityComparer<T>.Default.Equals(stage, GetLastStep()))
                {
                    globalProgress = 0.0f;
                    EditorUtility.ClearProgressBar();
                }
                else
                {
                    globalProgress = Mathf.Max(GetProgress(stage), globalProgress); // prevent progress from going back
                    EditorUtility.DisplayProgressBar("Baking Adaptive Probe Volumes", stage.ToString(), globalProgress);
                }
            }

            public abstract T GetLastStep();

            public BakingProfiling(T stage, ref T currentStage)
            {
                if (LogFile != null && EqualityComparer<T>.Default.Equals(currentStage, GetLastStep()))
                {
                    Profiling.Profiler.logFile = LogFile;
                    Profiling.Profiler.enableBinaryLog = true;
                    Profiling.Profiler.enabled = true;
                }

                prevStage = currentStage;
                currentStage = stage;
                UpdateProgressBar(stage);

                if (LogFile != null)
                    Profiling.Profiler.BeginSample(stage.ToString());
            }

            public void OnDispose(ref T currentStage)
            {
                if (disposed) return;
                disposed = true;

                if (LogFile != null)
                    Profiling.Profiler.EndSample();

                UpdateProgressBar(prevStage);
                currentStage = prevStage;

                if (LogFile != null && EqualityComparer<T>.Default.Equals(currentStage, GetLastStep()))
                {
                    Profiling.Profiler.enabled = false;
                    Profiling.Profiler.logFile = null;
                }
            }
        }

        internal class BakingSetupProfiling : BakingProfiling<BakingSetupProfiling.Stages>, IDisposable
        {
            //protected override string LogFile => "PrepareBake";

            public enum Stages
            {
                PrepareBake,
                PrepareWorldSubdivision,
                EnsurePerSceneDataInOpenScenes,
                FindWorldBounds,
                PlaceProbes,
                BakeBricks,
                ApplySubdivisionResults,
                None
            }

            static Stages currentStage = Stages.None;
            public BakingSetupProfiling(Stages stage) : base(stage, ref currentStage) { }
            public override Stages GetLastStep() => Stages.None;
            public static void GetProgressRange(out float progress0, out float progress1) { float s = 1 / (float)Stages.None; progress0 = (float)currentStage * s; progress1 = progress0 + s; }
            public void Dispose() { OnDispose(ref currentStage); }
        }

        internal class BakingCompleteProfiling : BakingProfiling<BakingCompleteProfiling.Stages>, IDisposable
        {
            //protected override string LogFile => "FinalizeBake";

            public enum Stages
            {
                FinalizingBake,
                WriteBakedData,
                PerformDilation,
                None
            }

            static Stages currentStage = Stages.None;
            public BakingCompleteProfiling(Stages stage) : base(stage, ref currentStage) { }
            public override Stages GetLastStep() => Stages.None;
            public void Dispose() { OnDispose(ref currentStage); }
        }

        sealed class BakeData
        {
            readonly APVProbeVolumeBaking m_Baking;
            internal BakeData(APVProbeVolumeBaking baking) => m_Baking = baking;
            // Inputs
            public BakeJob[] jobs;
            public int probeCount;
            public int reflectionProbeCount;

            public NativeArray<int> positionRemap;
            public NativeArray<Vector3> sortedPositions;

            // Workers
            public VirtualOffsetBaker virtualOffsetJob;
            public SkyOcclusionBaker skyOcclusionJob;
            public StandaloneAPVLightingBaker lightingJob;
            public RenderingLayerBaker layerMaskJob;
            public int cellIndex;

            // Progress reporting
            public BakingStep step;
            public ulong stepCount;

            // Cancellation
            public bool failed;

            public void Init(ProbeVolumeBakingSet bakingSet, NativeList<Vector3> probePositions, List<Vector3> requests)
            {
                failed = false;
                probeCount = probePositions.Length;
                reflectionProbeCount = requests.Count;

                jobs = CreateBakingJobs(bakingSet, requests.Count != 0);
                SortPositions(probePositions, requests);

                virtualOffsetJob = m_Baking.virtualOffsetOverride ?? new DefaultVirtualOffset(m_Baking);
                virtualOffsetJob.Initialize(bakingSet, sortedPositions.GetSubArray(0, probeCount));

                skyOcclusionJob = m_Baking.skyOcclusionOverride ?? new DefaultSkyOcclusion(m_Baking);
                skyOcclusionJob.Initialize(bakingSet, sortedPositions.GetSubArray(0, probeCount));
                if (skyOcclusionJob is DefaultSkyOcclusion defaultSOJob)
                    defaultSOJob.jobs = jobs;

                layerMaskJob = new DefaultRenderingLayer(m_Baking);
                layerMaskJob.Initialize(bakingSet, sortedPositions.GetSubArray(0, probeCount));

                lightingJob = new StandaloneAPVLightingBaker(GetStandaloneBakeSettings());
                lightingJob.Initialize(ProbeVolumeBakingWorkflow.GetLightingSettings().mixedBakeMode != MixedLightingMode.IndirectOnly, sortedPositions);

                cellIndex = 0;

                step = BakingStep.VirtualOffset;
                stepCount = virtualOffsetJob.stepCount + lightingJob.stepCount + skyOcclusionJob.stepCount;
            }

            BakeJob[] CreateBakingJobs(ProbeVolumeBakingSet bakingSet, bool hasAdditionalRequests)
            {
                // Build the list of adjustment volumes affecting sample count
                var touchupVolumesAndBounds = new TouchupVolumeWithBoundsList();
                {
                    // This is slow, but we should have very little amount of touchup volumes.
                    foreach (var adjustment in m_Baking.s_AdjustmentVolumes)
                    {
                        if (adjustment.volume.mode == ProbeAdjustmentVolume.Mode.OverrideSampleCount)
                            touchupVolumesAndBounds.Add(adjustment);
                    }

                    // Sort by volume to give priority to smaller volumes
                    touchupVolumesAndBounds.Sort((a, b) => (a.volume.ComputeVolume(a.obb).CompareTo(b.volume.ComputeVolume(b.obb))));
                }

                int additionalJobs = hasAdditionalRequests ? 2 : 1;
                var jobs = new BakeJob[touchupVolumesAndBounds.Count + additionalJobs];

                for (int i = 0; i < touchupVolumesAndBounds.Count; i++)
                    jobs[i].Create(touchupVolumesAndBounds[i]);

                jobs[touchupVolumesAndBounds.Count].Create(bakingSet);
                if (hasAdditionalRequests)
                    jobs[touchupVolumesAndBounds.Count + 1].Create(bakingSet);

                return jobs;
            }

            // Place positions contiguously for each bake job in a single array, with reflection probes at the end
            public void SortPositions(NativeList<Vector3> probePositions, List<Vector3> additionalRequests)
            {
                positionRemap = new NativeArray<int>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                sortedPositions = new NativeArray<Vector3>(probePositions.Length + additionalRequests.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                int regularJobCount = additionalRequests.Count != 0 ? jobs.Length - 1 : jobs.Length;

                // Place each probe in the correct job
                int[] jobSize = new int[regularJobCount];
                for (int i = 0; i < probePositions.Length; i++)
                {
                    // Last regular job (so before reflection probes if they exist) is the default one
                    // In case we don't match any touchup, we should be placed in this one
                    int jobIndex = 0;
                    for (; jobIndex < regularJobCount - 1; jobIndex++)
                    {
                        if (jobs[jobIndex].Contains(probePositions[i]))
                            break;
                    }

                    positionRemap[i] = jobIndex;
                    jobSize[jobIndex]++;
                }

                // Compute the size and offset of each job in the sorted array
                int currentOffset = 0;
                for (int i = 0; i < regularJobCount; i++)
                {
                    ref var job = ref jobs[i];
                    job.startOffset = currentOffset;
                    job.probeCount = jobSize[i];
                    currentOffset += job.probeCount;
                    jobSize[i] = 0;
                }

                Debug.Assert(currentOffset == probePositions.Length);

                // Sort position and store remapping
                for (int i = 0; i < probePositions.Length; i++)
                {
                    int jobIndex = positionRemap[i];
                    int newPos = jobs[jobIndex].startOffset + jobSize[jobIndex]++;
                    positionRemap[i] = newPos;
                    sortedPositions[newPos] = probePositions[i];
                }

                // Place reflection probe positions at the end of the array
                if (additionalRequests.Count != 0)
                {
                    ref var requestJob = ref jobs[jobs.Length - 1];
                    requestJob.startOffset = currentOffset;
                    requestJob.probeCount = additionalRequests.Count;
                    for (int i = 0; i < additionalRequests.Count; i++)
                        sortedPositions[currentOffset++] = additionalRequests[i];

                    Debug.Assert(currentOffset == sortedPositions.Length);
                }
            }

            public void ApplyVirtualOffset()
            {
                var offsets = virtualOffsetJob.offsets;
                for (int i = 0; i < offsets.Length; i++)
                    sortedPositions[i] += offsets[i];
            }

            public bool Done()
            {
                ulong currentStep = virtualOffsetJob.currentStep + lightingJob.currentStep + skyOcclusionJob.currentStep;
                return currentStep >= stepCount && step == BakingStep.Last;
            }

            public void Dispose()
            {
                if (failed)
                    Debug.LogError("Probe Volume Baking failed.");

                if (skyOcclusionJob != null && skyOcclusionJob.encodedDirections.IsCreated)
                    skyOcclusionJob.encodedDirections.Dispose();
                virtualOffsetJob?.Dispose();
                skyOcclusionJob?.Dispose();
                lightingJob?.Dispose();
                layerMaskJob?.Dispose();

                if (positionRemap.IsCreated) positionRemap.Dispose();
                if (sortedPositions.IsCreated) sortedPositions.Dispose();

                // clear references to managed data
                jobs = null;
                lightingJob = null;
                virtualOffsetJob = null;
                skyOcclusionJob = null;
                layerMaskJob = null;
                positionRemap = default;
                sortedPositions = default;
                probeCount = reflectionProbeCount = cellIndex = 0;
                stepCount = 0;
                step = BakingStep.VirtualOffset;
                failed = false;
            }
        }


        BakingBatch m_BakingBatch;
        ProbeVolumeBakingSet m_BakingSet => bakingSet;
        TouchupVolumeWithBoundsList s_AdjustmentVolumes;


#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
        APVRTContext s_TracingContext;
#endif
        BakeData s_BakeData;

        Dictionary<int, BakingCell> m_BakedCells = new Dictionary<int, BakingCell>();


        SphericalHarmonicsL2 s_BlackSH;
        bool s_BlackSHInitialized = false;

        SphericalHarmonicsL2 GetBlackSH()
        {
            if (!s_BlackSHInitialized)
            {
                s_BlackSHInitialized = true;
                // Init SH with values that will resolve to black
                s_BlackSH = new SphericalHarmonicsL2();
                for (int channel = 0; channel < 3; ++channel)
                {
                    s_BlackSH[channel, 0] = 0.0f;
                    for (int coeff = 1; coeff < 9; ++coeff)
                        s_BlackSH[channel, coeff] = 0.5f;
                }
            }

            return s_BlackSH;
        }

        internal override string backendId => APVProbeReferenceVolumeBackend.backendId;

        internal APVProbeVolumeBaking(APVProbeReferenceVolume volume, ProbeVolumeBakingSet bakingSet) : base(volume, bakingSet)
        {
            s_BakeData = new BakeData(this);
            virtualOffsetOverride = GetVirtualOffsetBakerOverride();
            skyOcclusionOverride = GetSkyOcclusionBakerOverride();
        }

        void DisposeTracing()
        {
#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
            s_TracingContext.Dispose();
            s_TracingContext = default;
#endif
        }

        internal override void Dispose()
        {
            CleanBakeData();
            DisposeTracing();
        }

        protected override void ClearAlgorithmData()
        {
            var probeVolumes = GameObject.FindObjectsByType<ProbeVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var probeVolume in probeVolumes)
            {
                if (bakingSet.sceneGUIDs.Contains(probeVolume.gameObject.scene.GetGUID()))
                    probeVolume.OnLightingDataAssetCleared();
            }
        }

        TouchupVolumeWithBoundsList GetAdjustementVolumes()
        {
            // This is slow, but we should have very little amount of touchup volumes.
            var touchupVolumes = Object.FindObjectsByType<ProbeAdjustmentVolume>(FindObjectsSortMode.InstanceID);

            var touchupVolumesAndBounds = new TouchupVolumeWithBoundsList(touchupVolumes.Length);
            foreach (var touchup in touchupVolumes)
            {
                if (touchup.isActiveAndEnabled)
                {
                    touchup.GetOBBandAABB(out var obb, out var aabb);
                    touchupVolumesAndBounds.Add((obb, aabb, touchup));
                    touchup.skyDirection.Normalize();
                }
            }

            // Sort by volume to give priority to bigger volumes so smaller volumes are applied last
            touchupVolumesAndBounds.Sort((a, b) => (b.volume.ComputeVolume(b.obb).CompareTo(a.volume.ComputeVolume(a.obb))));

            return touchupVolumesAndBounds;
        }

        // Actual baking process

        enum BakingStep
        {
            VirtualOffset,
            SkyOcclusion,
            RenderingLayerMask,
            Integration,
            FinalizeCells,

            Last = FinalizeCells + 1
        }

        protected override bool PrepareBakeData()
        {
            List<Vector3> requests;
            NativeList<Vector3> positions;
            using (new BakingSetupProfiling(BakingSetupProfiling.Stages.PrepareBake))
            {
                s_AdjustmentVolumes = GetAdjustementVolumes();
                requests = AdditionalGIBakeRequestsManager.GetProbeNormalizationRequests();

                bool canceledByUser = false;
                // Note: this could be executed in the baking delegate to be non blocking
                using (new BakingSetupProfiling(BakingSetupProfiling.Stages.PlaceProbes))
                    positions = RunPlacement(ref canceledByUser);

                if (positions.Length == 0 || canceledByUser)
                {
                    positions.Dispose();

                    // Cancellation preserves the previous bake. An empty volume clears it.
                    if (!canceledByUser)
                        Clear();
                    CleanBakeData();

                    return false;
                }
            }

            using (positions)
                s_BakeData.Init(m_BakingSet, positions, requests);
            return true;
        }

        protected override void PrepareBackend()
        {
            m_TotalCellCounts = new CellCounts();
        }

        void BakeDelegate(ref float progress, ref bool done)
        {
            if (s_BakeData.step == BakingStep.VirtualOffset)
            {
                if (!s_BakeData.virtualOffsetJob.Step())
                    s_BakeData.failed = true;
                if (s_BakeData.virtualOffsetJob.currentStep >= s_BakeData.virtualOffsetJob.stepCount)
                {
                    if (s_BakeData.virtualOffsetJob.offsets.IsCreated)
                        s_BakeData.ApplyVirtualOffset();
                    s_BakeData.step++;
                }
            }

            if (s_BakeData.step == BakingStep.SkyOcclusion)
            {
                if (!s_BakeData.skyOcclusionJob.Step())
                    s_BakeData.failed = true;
                if (s_BakeData.skyOcclusionJob.currentStep >= s_BakeData.skyOcclusionJob.stepCount)
                {
                    if (!s_BakeData.failed && s_BakeData.skyOcclusionJob.shadingDirections.IsCreated)
                        s_BakeData.skyOcclusionJob.Encode(volume);
                    s_BakeData.step++;
                }
            }

            if (s_BakeData.step == BakingStep.RenderingLayerMask)
            {
                if (!s_BakeData.layerMaskJob.Step())
                    s_BakeData.failed = true;
                if (s_BakeData.layerMaskJob.currentStep >= s_BakeData.layerMaskJob.stepCount)
                    s_BakeData.step++;
            }

            if (s_BakeData.step == BakingStep.Integration)
            {
                if (!s_BakeData.lightingJob.Step())
                    s_BakeData.failed = true;
                if (s_BakeData.lightingJob.currentStep >= s_BakeData.lightingJob.stepCount)
                {
                    if (!s_BakeData.failed)
                        PostProcessStandaloneLighting();
                    s_BakeData.step++;
                }
            }

            if (s_BakeData.step == BakingStep.FinalizeCells)
            {
                FinalizeCell(s_BakeData.cellIndex++, s_BakeData.positionRemap,
                    s_BakeData.lightingJob.irradiance, s_BakeData.lightingJob.validity,
                    s_BakeData.layerMaskJob.renderingLayerMasks,
                    s_BakeData.virtualOffsetJob.offsets,
                    s_BakeData.skyOcclusionJob.occlusion, s_BakeData.skyOcclusionJob.encodedDirections,
                    s_BakeData.lightingJob.occlusion);

                if (s_BakeData.cellIndex >= m_BakingBatch.cells.Count)
                    s_BakeData.step++;
            }

            // Handle error case
            if (s_BakeData.failed)
            {
                Debug.LogError($"Standalone APV bake failed during {s_BakeData.step} (probeCount={s_BakeData.probeCount}).");
                CleanBakeData();
                done = true;
                return;
            }

            ulong currentStep = s_BakeData.virtualOffsetJob.currentStep +
                s_BakeData.skyOcclusionJob.currentStep + s_BakeData.lightingJob.currentStep;
            progress = currentStep / (float)s_BakeData.stepCount;

            // Use our counter to determine when baking is done
            if (s_BakeData.Done())
            {
                FinalizeBake();
                done = true;
            }
        }

        void FinalizeBake()
        {
            int finalizedProbeCount = s_BakeData.probeCount;
            Debug.Log($"Standalone APV bake entering FinalizeBake (probeCount={finalizedProbeCount}).");
            using (new BakingCompleteProfiling(BakingCompleteProfiling.Stages.FinalizingBake))
            {
                if (s_BakeData.probeCount != 0)
                {
                    // Publication failures propagate to the driver so it rolls back
                    // the bake and releases resources without reporting success.
                    ApplyPostBakeOperations();
                    foreach (var data in GetPerSceneDataList())
                    {
                        data.SetStandaloneLightBakingOutputs(s_BakeData.lightingJob.sceneLights);
                        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(data.gameObject.scene);
                    }
                }

                if (s_BakeData.reflectionProbeCount != 0)
                {
                    var additionalIrradiance = s_BakeData.lightingJob.irradiance.GetSubArray(s_BakeData.probeCount, s_BakeData.reflectionProbeCount);
                    var additionalValidity = s_BakeData.lightingJob.validity.GetSubArray(s_BakeData.probeCount, s_BakeData.reflectionProbeCount);
                    AdditionalGIBakeRequestsManager.OnAdditionalProbesBakeCompleted(additionalIrradiance, additionalValidity);
                }
            }

            CleanBakeData();

            // We need to reset that view
            volume.ResetDebugViewToMaxSubdiv();
            AdaptiveProbeVolumes.MarkLastBakeSucceeded();
            Debug.Log($"Standalone APV bake finalized successfully (probeCount={finalizedProbeCount}).");
        }

        void CleanBakeData()
        {
            try
            {
                s_BakeData.Dispose();
            }
            finally
            {
                m_BakingBatch = null;
                s_AdjustmentVolumes = null;
                m_StandaloneBakeActive = false;
                m_StandalonePrepareActive = false;
                EditorApplication.update -= UpdateStandaloneBake;
                RemoveStandaloneProgress();
                CompleteBake();

                // A standalone bake owns the tracing context for its whole
                // session. Release it after every bake, including when the
                // Lighting window remains open, so a later bake recreates the
                // ray-tracing shader and cannot reuse stale imported kernels.
                DisposeTracing();
            }
        }

        class VoxelToBrickCache
        {
            readonly BakingBatch m_BakingBatch;
            internal VoxelToBrickCache(BakingBatch batch) => m_BakingBatch = batch;
            class CacheEntry
            {
                public int access; // keep track of last access age
                public Dictionary<int, Brick> map = new();
            }

            const int k_MaxCellsCached = 64;

            int accesses = 0;
            Dictionary<int, CacheEntry> cache = new();
            ObjectPool<CacheEntry> m_BrickMetaPool = new ObjectPool<CacheEntry>(x => x.map.Clear(), null, false);

            CacheEntry BuildMap(in BakingCell cell)
            {
                var entry = m_BrickMetaPool.Get();

                // Build a map from voxel to brick
                // A voxel is the size of a brick at subdivision level 0
                foreach (var brick in cell.bricks)
                {
                    int brick_size = APVProbeReferenceVolume.CellSize(brick.subdivisionLevel);
                    Vector3Int brickMin = brick.position;
                    Vector3Int brickMax = brick.position + Vector3Int.one * brick_size;

                    for (int x = brickMin.x; x < brickMax.x; ++x)
                    {
                        for (int z = brickMin.z; z < brickMax.z; ++z)
                        {
                            for (int y = brickMin.y; y < brickMax.y; ++y)
                            {
                                entry.map[m_BakingBatch.GetBrickPositionHash(new Vector3Int(x, y, z))] = brick;
                            }
                        }
                    }
                }

                return entry;
            }

            public Dictionary<int, Brick> GetMap(in BakingCell cell)
            {
                if (!cache.TryGetValue(cell.index, out var entry))
                {
                    if (cache.Count >= k_MaxCellsCached)
                    {
                        int worst = 0;
                        int oldest = int.MaxValue;
                        foreach (var ce in cache)
                        {
                            if (ce.Value.access < oldest)
                            {
                                oldest = ce.Value.access;
                                worst = ce.Key;
                            }
                        }
                        m_BrickMetaPool.Release(cache[worst]);
                        cache.Remove(worst);
                    }

                    entry = BuildMap(cell);
                    cache[cell.index] = entry;
                }

                entry.access = ++accesses;
                return entry.map;
            }
        }

        void FixSeams(NativeArray<int> positionRemap, NativeArray<Vector3> positions, NativeArray<SphericalHarmonicsL2> sh, NativeArray<float> validity, NativeArray<uint> renderingLayerMasks)
        {
            // Seams are caused are caused by probes on the boundary between two subdivision levels
            // The idea is to find first them and do a kind of dilation to smooth the values on the boundary
            // the dilation process consits in doing a trilinear sample of the higher subdivision brick and override the lower subdiv with that
            // We have to mark the probes on the boundary as valid otherwise leak reduction at runtime will interfere with this method


            // Use an indirection structure to ensure mem usage stays reasonable
            VoxelToBrickCache cache = new VoxelToBrickCache(m_BakingBatch);

            // Create a map from cell position to index for fast lookup across cells
            var cellPositionToIndex = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < m_BakingBatch.cells.Count; i++)
                cellPositionToIndex[m_BakingBatch.cells[i].position] = i;

            for (int c = 0; c < m_BakingBatch.cells.Count; c++)
            {
                var cell = m_BakingBatch.cells[c];
                var voxelToBrick = cache.GetMap(cell);

                float scale = m_Layout.minBrickSize / ProbeBrickPoolBase.brickCellCount;
                float minBrickSize = m_Layout.minBrickSize;
                Brick largestBrick = default;

                int numProbes = cell.probePositions.Length;
                for (int probeIndex = 0; probeIndex < numProbes; ++probeIndex)
                {
                    int i = positionRemap[cell.probeIndices[probeIndex]];
                    int minSubdiv = ProbeBrickIndex.kMaxSubdivisionLevels;
                    int maxSubdiv = -1;

                    Vector3 pos = positions[i] - m_Layout.probeOffset;

                    // 1.
                    // For each unique probe, find bricks from all 8 neighbouring voxels
                    for (int o = 0; o < 8; o++)
                    {
                        Vector3 sampleOffset = m_Layout.minDistanceBetweenProbes * (Vector3)GetSampleOffset(o);
                        Vector3Int voxel = Vector3Int.FloorToInt((pos - sampleOffset) / minBrickSize);
                        int hashCode = m_BakingBatch.GetBrickPositionHash(voxel);
                        if (!voxelToBrick.TryGetValue(hashCode, out var brick))
                        {
                            // If the brick was not found in the current cell, find it in the neighbouring cells
                            Vector3Int GetCellPositionFromVoxel(Vector3Int voxelToLookup, int cellSizeInBricks)
                            {
                                return new Vector3Int(
                                    FloorDivide(voxelToLookup.x, cellSizeInBricks),
                                    FloorDivide(voxelToLookup.y, cellSizeInBricks),
                                    FloorDivide(voxelToLookup.z, cellSizeInBricks)
                                );
                                int FloorDivide(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
                            }

                            // Find the position of the neighbouring cell that would contain the voxel
                            bool foundInOtherCell = false;
                            var cellToLookupPos = GetCellPositionFromVoxel(voxel, m_Layout.cellSizeInBricks);
                            if(cellPositionToIndex.TryGetValue(cellToLookupPos, out var cellIndex))
                            {
                                var currentCell = m_BakingBatch.cells[cellIndex];
                                var voxelToBrickNeighbouringCell = cache.GetMap(currentCell);
                                if (voxelToBrickNeighbouringCell.TryGetValue(hashCode, out brick))
                                    foundInOtherCell = true;
                            }

                            if(!foundInOtherCell)
                                continue;
                        }

                        if (brick.subdivisionLevel > maxSubdiv)
                            largestBrick = brick;

                        minSubdiv = Mathf.Min(minSubdiv, brick.subdivisionLevel);
                        maxSubdiv = Mathf.Max(maxSubdiv, brick.subdivisionLevel);
                    }

                    // 2.
                    // There is a seam when a probe is part of two bricks with different subdiv level
                    if (minSubdiv >= maxSubdiv)
                        continue;

                    // 3.
                    // Overwrite lighting data with trilinear sampled data from the brick with highest subdiv level
                    float brickSize = volume.BrickSize(largestBrick.subdivisionLevel - 1);
                    float3 uvw = math.clamp((pos - (Vector3)largestBrick.position * minBrickSize) / brickSize, 0, 3);

                    var probe = Vector3Int.FloorToInt(uvw);
                    var fract = math.frac(uvw);

                    int brick_size = APVProbeReferenceVolume.CellSize(largestBrick.subdivisionLevel);
                    Vector3Int brickOffset = largestBrick.position * ProbeBrickPoolBase.brickCellCount;

                    // We need to check if rendering layers masks were baked, since it happens in separate job
                    bool bakedRenderingLayerMasks = (renderingLayerMasks.IsCreated & renderingLayerMasks.Length > 0);
                    uint probeRenderingLayerMask = 0;

                    if (bakedRenderingLayerMasks) probeRenderingLayerMask = renderingLayerMasks[i];

                    float weightSum = 0.0f;
                    SphericalHarmonicsL2 trilinear = default;
                    for (int o = 0; o < 8; o++)
                    {
                        Vector3Int offset = GetSampleOffset(o);

                        // We need to make sure probe positions are computed in the same way as in ConvertBricksToPositions
                        // Otherwise floating point imprecision could give a different position hash
                        Vector3Int probeOffset = brickOffset + (probe + offset) * brick_size;
                        int probeHash = m_BakingBatch.GetProbePositionHash(m_Layout.probeOffset + (Vector3)probeOffset * scale);

                        if (m_BakingBatch.positionToIndex.TryGetValue(probeHash, out var index))
                        {
                            bool valid = validity[positionRemap[index]] <= k_MinValidityForLeaking;
                            if (!valid) continue;

                            if (bakedRenderingLayerMasks)
                            {
                                uint renderingLayerMask = renderingLayerMasks[positionRemap[index]];
                                bool commonRenderingLayer = (renderingLayerMask & probeRenderingLayerMask) != 0;
                                if (!commonRenderingLayer) continue; // We do not use this probe contribution if it does not share at least a common rendering layer
                            }

                            // Do the lerp in compressed format to match result on GPU
                            var sample = sh[positionRemap[index]];
                            BakingCell.CompressSH(ref sample, 1.0f, false);

                            float trilinearW =
                                ((offset.x == 1) ? fract.x : 1.0f - fract.x) *
                                ((offset.y == 1) ? fract.y : 1.0f - fract.y) *
                                ((offset.z == 1) ? fract.z : 1.0f - fract.z);

                            trilinear += sample * trilinearW;
                            weightSum += trilinearW;
                        }
                    }

                    if (weightSum != 0.0f)
                    {
                        trilinear *= 1.0f / weightSum;
                        BakingCell.DecompressSH(ref trilinear);
                        sh[i] = trilinear;
                        validity[i] = k_MinValidityForLeaking;
                    }
                }
            }
        }

        void ApplyPostBakeOperations()
        {
            using var publication = new APVBakingDataPublication(m_BakingSet, volume);
            ApplyPostBakeOperations(publication);
            publication.Commit();
        }

        void ApplyPostBakeOperations(APVBakingDataPublication publication)
        {
            var probeRefVolume = volume;

            // Clear baked data
            foreach (var data in probeRefVolume.perSceneDataList)
                data.QueueSceneRemoval();
            probeRefVolume.Clear();

            // Make sure all pending operations are done (needs to be after the Clear to unload all previous scenes)
            probeRefVolume.PerformPendingOperations();
            probeRefVolume.SetSubdivisionDimensions(m_Layout.minBrickSize, m_Layout.maxSubdivision, m_Layout.probeOffset);

            // Use the globalBounds we just computed, as the one in probeRefVolume doesn't include scenes that have never been baked
            probeRefVolume.globalBounds = globalBounds;

            PrepareCellsForWriting(isBakingSceneSubset);

            m_BakingSet.chunkSizeInBricks = ProbeBrickPoolBase.GetChunkSizeInBrickCount();
            m_BakingSet.minCellPosition = minCellPosition;
            m_BakingSet.maxCellPosition = maxCellPosition;
            m_BakingSet.globalBounds = globalBounds;
            m_BakingSet.maxSHChunkCount = -1;

            m_BakingSet.scenarios.TryAdd(m_BakingSet.lightingScenario, new ProbeVolumeBakingSet.PerScenarioDataInfo());

            // Convert baking cells to runtime cells
            using (new BakingCompleteProfiling(BakingCompleteProfiling.Stages.WriteBakedData))
                WriteBakingCells(m_BakedCells.Values.ToArray(), publication);

            // Reset internal structures depending on current bake.
            Debug.Assert(probeRefVolume.EnsureCurrentBakingSet(m_BakingSet));

            EditorUtility.SetDirty(m_BakingSet);
            AssetDatabase.SaveAssetIfDirty(m_BakingSet);
            probeRefVolume.clearAssetsOnVolumeClear = false;

            if (m_BakingSet.hasDilation)
            {
                // This subsequent block needs to happen AFTER we call WriteBakingCells.
                // Otherwise, in cases where we change the spacing between probes, we end up loading cells with a certain layout in ForceSHBand
                // And then we unload cells using the wrong layout in PerformDilation (after WriteBakingCells updates the baking set object) which leads to a broken internal state.

                var prevForceNoDiskStreaming = probeRefVolume.forceNoDiskStreaming;
                var prevMemoryBudget = probeRefVolume.memoryBudget;
                var prevSHBands = probeRefVolume.shBands;

                try
                {
                    // Dilation needs the full neighborhood and all SH bands in
                    // memory. Forcing bands may reload files and can itself fail.
                    probeRefVolume.ForceNoDiskStreaming(true);
                    probeRefVolume.ForceMemoryBudget(ProbeVolumeTextureMemoryBudget.MemoryBudgetHigh);
                    probeRefVolume.ForceSHBand(ProbeVolumeSHBands.SphericalHarmonicsL2);
                    // Do it now otherwise it messes the loading bar
                    InitDilationShaders();
                    using (new BakingCompleteProfiling(BakingCompleteProfiling.Stages.PerformDilation))
                        PerformDilation(publication);
                }
                finally
                {
                    probeRefVolume.ForceNoDiskStreaming(prevForceNoDiskStreaming);
                    probeRefVolume.ForceMemoryBudget(prevMemoryBudget);
                    probeRefVolume.ForceSHBand(prevSHBands);
                }
            }
            else
            {
                foreach (var data in probeRefVolume.perSceneDataList)
                    data.Initialize();

                probeRefVolume.PerformPendingOperations();
            }

            // Mark stuff as up to date
            m_BakingBatch = null;
            foreach (var probeVolume in GetProbeVolumeList())
                probeVolume.OnBakeCompleted();
            foreach (var adjustment in s_AdjustmentVolumes)
                adjustment.volume.cachedHashCode = adjustment.volume.GetHashCode();

            // We allocate data even if dilation is off, that should be changed
            FinalizeDilation();
        }


        /// <summary>
        /// Returns true when the standalone APV bake is running, false otherwise (Read Only).
        /// </summary>
        public override bool isRunning
        {
            get => m_StandaloneBakeActive;
        }

        /// <summary>
        /// Cancels the currently running asynchronous bake job.
        /// </summary>
        /// <returns>Returns true if baking was successfully cancelled.</returns>
        public override bool Cancel()
        {
            if (!StandalonePathActive)
                return false;
            CleanBakeData();
            return true;
        }

        VirtualOffsetBaker virtualOffsetOverride = null;
        SkyOcclusionBaker skyOcclusionOverride = null;

    }
}
