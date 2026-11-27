// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    /// <summary>bakes child renderers into a compact instance blob and renders them via BRG with no
    /// per-instance GameObjects or GPUBatcherGameObjectLink. render signatures are stored on the component, so
    /// the source GameObjects are destroyed after baking. use Expand to recreate them for editing.</summary>
    [AddComponentMenu("Fraxinus GPU Batcher/GPU Batcher Instance Group")]
    [ExecuteAlways]
    public partial class GPUBatcherInstanceGroup : GPUBatcherRegisterer
    {
        // 44B blittable per-instance record packed into _instanceBlob
        struct PackedInstance
        {
            public float3 position;
            public quaternion rotation;
            public float3 scale;
            public int protoIndex;
        }

        // serializable per-LOD render data. mesh is either a direct asset ref or, for combined LODs,
        // the source meshes+matrices to recombine at runtime (the combined mesh isn't an asset).
        [Serializable]
        public class BakedLod
        {
            public Mesh directMesh;
            public Mesh[] sourceMeshes;
            public Matrix4x4[] sourceMatrices;
            public Material[] materials;
            public float screenSize;
            public float fadeWidth;
            public ShadowCastingMode shadowMode = ShadowCastingMode.On;
            public MotionVectorGenerationMode motionMode = MotionVectorGenerationMode.Camera;
        }

        // self-contained snapshot of a RenderSignature, captured at bake from ExtractRenderSignature
        [Serializable]
        public class BakedSignature
        {
#if UNITY_EDITOR
            public GameObject sourcePrefab; // used by Expand to re-instantiate the original
#endif
            public BakedLod[] lods;
            public float lodReferenceSize;
            public LODFadeMode fadeMode;
            public uint percentageFadeMask;
            public bool usesMeshLod;
            public int[] meshLodIndices;

            // RenderSignature scalar params (extra data already applied)
            public MotionVectorGenerationMode motionVectorMode;
            public bool animateCrossFading;
            public int maxShadowCascade = 4;
            public int minShadowLOD;
            public float maxShadowDistance;
            public int maxShadowCrossfadeCascade = -99;
            public float lodFadeDuration;
            public GPUBatcherPrototypeGroup prototypeGroup;
            public bool enableDistanceDensity;
            public DistanceDensityMode distanceDensityMode = DistanceDensityMode.ScreenSize;
            public float densityStartScreenSize = 0.1f;
            public float densityStartDistance = 50f;
            public float densityEndDistance = 150f;
            public float densityMinFraction = 0.3f;
            public float densityFadeWidth = 0.05f;
            public int layer;
            public uint renderingLayerMask = 0xffffffff;
            public uint overrideMask;
        }

        [SerializeField] ChunkMode _chunkMode = ChunkMode.Static;
        [SerializeField, Min(10f)] float _chunkSize = 256f;
        [SerializeField, Tooltip("Max render distance per chunk (0 = no limit)")]
        float _cullDistance = 0f;
        [SerializeField, Tooltip("When enabled, instances with different property overrides (e.g. material, " +
            "shadow, LOD settings) bake as separate prototypes. When disabled, all instances of the same prefab " +
            "share one prototype and per-instance overrides are ignored.")]
        bool _detectPrefabOverrides = false;
        [SerializeField] List<BakedSignature> _signatures = new();
        [SerializeField, HideInInspector] byte[] _instanceBlob;
        [SerializeField] int _instanceCount;
        [SerializeField] bool _baked;

        // runtime state
        int[] _globalProtoIndices;

        public bool isBaked => _baked;
        public int instanceCount => _instanceCount;
        public int prototypeCount => _signatures?.Count ?? 0;

        protected override void OnEnable()
        {
            if (!_baked || _instanceCount == 0) return;
            base.OnEnable();
        }

        protected override void OnInitialize()
        {
            if (!_baked || _signatures == null || _signatures.Count == 0 || _instanceCount == 0) return;

            _globalProtoIndices = new int[_signatures.Count];
            for (int i = 0; i < _signatures.Count; i++)
            {
                var sig = BuildSignature(_signatures[i], i);
                _globalProtoIndices[i] = sig != null ? RegisterRenderSignature(sig) : -1;
            }

            var instances = UnpackInstances();
            if (instances.Count == 0) return;

            // chunk by local prototype index (matches extents), remap to global at write time
            var extents = ComputePrototypeExtents();
            int maxPerChunk = MaxInstancesPerChunkConfigured();
            var chunks = GPUBatcherChunkBuilder.Build(instances, extents, maxPerChunk, _chunkMode, _chunkSize);

            foreach (var chunk in chunks)
            {
                if (chunk.instances == null || chunk.instances.Length == 0) continue;

                var link = AllocChunk();
                if (!link.isValid)
                {
                    Debug.LogWarning($"GPUBatcherInstanceGroup: failed to allocate chunk, {chunk.instances.Length} instances dropped");
                    continue;
                }

                var na = new NativeArray<InstanceData>(chunk.instances.Length, Allocator.TempJob);
                for (int i = 0; i < chunk.instances.Length; i++)
                {
                    var inst = chunk.instances[i];
                    int local = inst.signatureIndex;
                    inst.signatureIndex = (local >= 0 && local < _globalProtoIndices.Length) ? _globalProtoIndices[local] : -1;
                    na[i] = inst;
                }
                WriteChunk(link, na, chunk.bounds, _cullDistance);
                na.Dispose();
            }

            GPUBatcherSystem.RequestSnapAllViews();
        }

        protected override void OnShutdown()
        {
            _globalProtoIndices = null;
        }

        // unpack blob to InstanceData with local prototype indices (remapped to global at write time)
        List<InstanceData> UnpackInstances()
        {
            var list = new List<InstanceData>(_instanceCount);
            if (_instanceBlob == null || _instanceBlob.Length == 0) return list;

            var bytes = new NativeArray<byte>(_instanceBlob, Allocator.Temp);
            var packed = bytes.Reinterpret<PackedInstance>(1);
            for (int i = 0; i < packed.Length; i++)
            {
                var p = packed[i];
                list.Add(new InstanceData
                {
                    position = p.position,
                    rotation = p.rotation,
                    scale = p.scale,
                    signatureIndex = p.protoIndex
                });
            }
            bytes.Dispose();
            return list;
        }

        float[] ComputePrototypeExtents()
        {
            var extents = new float[_signatures.Count];
            for (int i = 0; i < _signatures.Count; i++)
                extents[i] = math.max(_signatures[i].lodReferenceSize * 0.5f, 1f);
            return extents;
        }

        int MaxInstancesPerChunkConfigured()
        {
            var cfg = GPUBatcherConfig.instance;
            return cfg != null ? cfg.maxPoolRefsPerChunk * GPUBatcherRenderer.instancesPerPool : MaxInstancesPerChunk;
        }

        // rebuild a RenderSignature from a stored snapshot, recombining meshes where needed
        RenderSignature BuildSignature(BakedSignature b, int index)
        {
            if (b == null || b.lods == null || b.lods.Length == 0) return null;

            int n = b.lods.Length;
            var proto = new PrototypeData
            {
                lodMeshes = new Mesh[n],
                lodMaterials = new Material[n][],
                lodScreenSizes = new float[n],
                lodFadeTransitionWidths = new float[n],
                lodShadowCastingModes = new ShadowCastingMode[n],
                lodMotionVectorModes = new MotionVectorGenerationMode[n],
                lodReferenceSize = b.lodReferenceSize,
                fadeMode = b.fadeMode,
                percentageFadeMask = b.percentageFadeMask,
            };
#if UNITY_6000_2_OR_NEWER
            proto.usesMeshLod = b.usesMeshLod;
            proto.meshLodIndices = (b.meshLodIndices != null && b.meshLodIndices.Length > 0) ? b.meshLodIndices : null;
#endif

            var cache = GPUBatcherSystem?.meshCombineCache;
            for (int i = 0; i < n; i++)
            {
                var lod = b.lods[i];
                proto.lodMeshes[i] = ResolveLodMesh(lod, cache, index, i);
                proto.lodMaterials[i] = lod.materials;
                proto.lodScreenSizes[i] = lod.screenSize;
                proto.lodFadeTransitionWidths[i] = lod.fadeWidth;
                proto.lodShadowCastingModes[i] = lod.shadowMode;
                proto.lodMotionVectorModes[i] = lod.motionMode;
            }
            proto.mesh = proto.lodMeshes[0];
            proto.materials = proto.lodMaterials[0];

            return new RenderSignature
            {
                asset = proto,
                motionVectorMode = b.motionVectorMode,
                animateCrossFading = b.animateCrossFading,
                maxShadowCascade = b.maxShadowCascade,
                minShadowLOD = b.minShadowLOD,
                maxShadowDistance = b.maxShadowDistance,
                maxShadowCrossfadeCascade = b.maxShadowCrossfadeCascade,
                lodFadeDuration = b.lodFadeDuration,
                prototypeGroup = b.prototypeGroup,
                enableDistanceDensity = b.enableDistanceDensity,
                distanceDensityMode = b.distanceDensityMode,
                densityStartScreenSize = b.densityStartScreenSize,
                densityStartDistance = b.densityStartDistance,
                densityEndDistance = b.densityEndDistance,
                densityMinFraction = b.densityMinFraction,
                densityFadeWidth = b.densityFadeWidth,
                layer = b.layer,
                renderingLayerMask = b.renderingLayerMask,
                overrideMask = (RenderSignature.OverrideField)b.overrideMask,
            };
        }

        Mesh ResolveLodMesh(BakedLod lod, MeshCombineCache cache, int protoIndex, int lodIndex)
        {
            if (lod.directMesh != null) return lod.directMesh;
            if (lod.sourceMeshes == null || lod.sourceMeshes.Length == 0) return null;

            // combining disabled: use first source mesh, ignore offset transforms
            if (!GPUBatcherConfig.instance.combineMultiMeshLODs)
                return lod.sourceMeshes[0];

            string debugName = $"{name}_proto{protoIndex}_LOD{lodIndex}_Combined";
            if (cache != null)
                return cache.GetOrCombine(lod.sourceMeshes, lod.sourceMatrices, debugName);

            var combines = new CombineInstance[lod.sourceMeshes.Length];
            for (int r = 0; r < combines.Length; r++)
            {
                combines[r].mesh = lod.sourceMeshes[r];
                combines[r].transform = lod.sourceMatrices[r];
                combines[r].subMeshIndex = 0;
            }
            var mesh = new Mesh { name = debugName };
            mesh.CombineMeshes(combines, mergeSubMeshes: false, useMatrices: true);
            mesh.RecalculateBounds();
            return mesh;
        }

        // pack a flat instance list into the serialized blob
        void PackInstances(List<PackedInstance> packed)
        {
            _instanceCount = packed.Count;
            if (packed.Count == 0) { _instanceBlob = Array.Empty<byte>(); return; }

            var na = new NativeArray<PackedInstance>(packed.Count, Allocator.Temp);
            for (int i = 0; i < packed.Count; i++) na[i] = packed[i];
            _instanceBlob = na.Reinterpret<byte>(UnsafeUtility.SizeOf<PackedInstance>()).ToArray();
            na.Dispose();
        }
    }
}
