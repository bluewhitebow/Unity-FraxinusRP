// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Rendering;

#if UNITY_6000_4_OR_NEWER
using WindID = UnityEngine.EntityId;
#else
using WindID = System.Int32;
#endif

namespace Fraxinus.GPUBatcher
{
    public enum NegativeScaleMode
    {
        FlipWinding = 0,
        ForcePositiveScale = 1
    }

    public enum AnimatedCrossfadeMode
    {
        PerPrototype = 0,
        PerInstance = 1
    }

    /// <summary>64-bit hash builder.</summary>
    public struct Hash64
    {
        private HashCode _a;
        private HashCode _b;
        private int _i;

        public void Add<T>(T value)
        {
            if ((_i++ & 1) == 0) _a.Add(value);
            else _b.Add(value);
        }

        public long ToHashCode()
            => ((long)_a.ToHashCode() << 32) | (uint)_b.ToHashCode();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct GPUProtoCrossfadeState
    {
        public float cycleProgress;
        public int isBoundary;
    }

    [Serializable]
    public struct InstanceData
    {
        public float3 position;
        public quaternion rotation;
        public float3 scale;
        public int signatureIndex;
        public Color32 color;
    }

    /// <summary>asset identity for a prototype - mesh, materials, LOD structure.</summary>
    [Serializable]
    public class PrototypeData
    {
#if UNITY_EDITOR
        public GameObject sourcePrefab;
#endif
        public Mesh[] lodMeshes;
        [NonSerialized] public Material[][] lodMaterials;

        // LOD0 convenience views, write-through to lodMeshes/lodMaterials (the single source of truth)
        public Mesh mesh
        {
            get => (lodMeshes != null && lodMeshes.Length > 0) ? lodMeshes[0] : null;
            set { if (lodMeshes == null || lodMeshes.Length == 0) lodMeshes = new Mesh[1]; lodMeshes[0] = value; }
        }
        public Material[] materials
        {
            get => (lodMaterials != null && lodMaterials.Length > 0) ? lodMaterials[0] : null;
            set { if (lodMaterials == null || lodMaterials.Length == 0) lodMaterials = new Material[1][]; lodMaterials[0] = value; }
        }
        public float[] lodScreenSizes;
        public float[] lodFadeTransitionWidths;
        public ShadowCastingMode[] lodShadowCastingModes;
        public MotionVectorGenerationMode[] lodMotionVectorModes;
        [FormerlySerializedAs("LodReferenceSize")]
        public float lodReferenceSize;
        public LODFadeMode fadeMode;
        public uint percentageFadeMask; // bit N = LOD N uses percentage fade
        public WindID speedTreeRendererID; // source renderer retained by the native wind capture bridge

        public void AssignRendererID(Renderer renderer)
        {
            speedTreeRendererID = GPUBatcherHelpers.GetEntityId(renderer);
        }

#if UNITY_6000_2_OR_NEWER
        public bool usesMeshLod = false; // Unity 6.2+ single-mesh LOD instead of separate meshes per level
        public int[] meshLodIndices; // per LOD level: mesh LOD index for indirect args, only valid with usesMeshLod
#endif

        public Mesh GetLODMesh(int lodLevel)
        {
            if (lodLevel < 0) lodLevel = 0;
            if (lodMeshes != null && lodLevel < lodMeshes.Length && lodMeshes[lodLevel] != null)
                return lodMeshes[lodLevel];
            return mesh;
        }

        public Material[] GetLODMaterials(int lodLevel)
        {
            if (lodLevel < 0) lodLevel = 0;
            if (lodMaterials != null && lodLevel < lodMaterials.Length && lodMaterials[lodLevel] != null)
                return lodMaterials[lodLevel];
            return materials;
        }

        // raw per-LOD lookups, null for empty LODs instead of LOD0 fallback. fall back only when the
        // array is missing. used by batch creation to skip empty LODs and by the editor to show them truthfully.
        public Mesh GetLODMeshRaw(int lodLevel)
        {
            if (lodMeshes != null)
                return (lodLevel >= 0 && lodLevel < lodMeshes.Length) ? lodMeshes[lodLevel] : mesh;
            return mesh;
        }

        public Material[] GetLODMaterialsRaw(int lodLevel)
        {
            if (lodMaterials != null)
                return (lodLevel >= 0 && lodLevel < lodMaterials.Length) ? lodMaterials[lodLevel] : materials;
            return materials;
        }

        public float GetLODFadeWidth(int lodLevel)
        {
            if (lodFadeTransitionWidths != null && lodLevel < lodFadeTransitionWidths.Length)
                return lodFadeTransitionWidths[lodLevel];
            return 0.5f;
        }

        // bitmask: bit N = 1 means LOD N casts shadows
        public uint GetLODShadowMask()
        {
            uint mask = 0;
            if (lodShadowCastingModes != null)
            {
                for (int i = 0; i < lodShadowCastingModes.Length && i < 32; i++)
                {
                    if (lodShadowCastingModes[i] != ShadowCastingMode.Off)
                        mask |= (1u << i);
                }
            }
            else
            {
                // default: all LODs cast shadows
                mask = 0xFFFFFFFF;
            }
            return mask;
        }

        public ShadowCastingMode GetShadowCastingMode()
        {
            if (lodShadowCastingModes == null || lodShadowCastingModes.Length == 0)
                return ShadowCastingMode.On;
            var best = ShadowCastingMode.Off;
            for (int i = 0; i < lodShadowCastingModes.Length; i++)
            {
                if (lodShadowCastingModes[i] > best)
                    best = lodShadowCastingModes[i];
            }
            return best;
        }

        public int lodCount => (lodMeshes != null && lodMeshes.Length > 0) ? lodMeshes.Length : 1;
    }

    /// <summary>full registration unit: asset data + render behavior config.</summary>
    [Serializable]
    public class RenderSignature
    {
        public PrototypeData asset;

        public GPUBatcherPrototypeGroup prototypeGroup; // assigned group, null = none

        public MotionVectorGenerationMode motionVectorMode;
        public bool animateCrossFading;
        public int maxShadowCascade = 4;
        public int minShadowLOD = 0;
        public float maxShadowDistance = 0f;
        public int maxShadowCrossfadeCascade = -99;
        public float lodFadeDuration = 0f;
#if UNITY_EDITOR
        //stored to override the value when "default animated crossfade duration" is changed on config
        public bool isDefaultCrossfadeDuration;
#endif
        public bool enableDistanceDensity = false;
        public DistanceDensityMode distanceDensityMode = DistanceDensityMode.ScreenSize;
        public float densityStartScreenSize = 0.1f;
        public float densityStartDistance = 50f;
        public float densityEndDistance = 150f;
        public float densityMinFraction = 0.3f;
        public float densityFadeWidth = 0.05f;
        public int layer = 0;
        public uint renderingLayerMask = 0xffffffff;

        // override tracking
        [Flags]
        public enum OverrideField : uint
        {
            MotionVectorMode          = 1 << 0,
            AnimateCrossFading        = 1 << 1,
            MaxShadowCascade          = 1 << 2,
            MinShadowLOD              = 1 << 3,
            MaxShadowDistance          = 1 << 4,
            MaxShadowCrossfadeCascade = 1 << 5,
            LodFadeDuration           = 1 << 6,
            EnableDistanceDensity      = 1 << 8,
            DistanceDensityMode        = 1 << 9,
            DensityStartScreenSize    = 1 << 10,
            DensityStartDistance      = 1 << 11,
            DensityEndDistance         = 1 << 12,
            DensityMinFraction        = 1 << 13,
            DensityFadeWidth          = 1 << 14,
            Layer                     = 1 << 15,
            RenderingLayerMask        = 1 << 16,
        }
        public OverrideField overrideMask;

        public void OverrideMotionVectorMode(MotionVectorGenerationMode v)
            { motionVectorMode = v; overrideMask |= OverrideField.MotionVectorMode; }
        public void OverrideAnimateCrossFading(bool v)
            { animateCrossFading = v; overrideMask |= OverrideField.AnimateCrossFading; }
        public void OverrideMaxShadowCascade(int v)
            { maxShadowCascade = v; overrideMask |= OverrideField.MaxShadowCascade; }
        public void OverrideMinShadowLOD(int v)
            { minShadowLOD = v; overrideMask |= OverrideField.MinShadowLOD; }
        public void OverrideMaxShadowDistance(float v)
            { maxShadowDistance = v; overrideMask |= OverrideField.MaxShadowDistance; }
        public void OverrideMaxShadowCrossfadeCascade(int v)
            { maxShadowCrossfadeCascade = v; overrideMask |= OverrideField.MaxShadowCrossfadeCascade; }
        public void OverrideLodFadeDuration(float v)
            { lodFadeDuration = v; overrideMask |= OverrideField.LodFadeDuration; }
        public void OverrideEnableDistanceDensity(bool v)
            { enableDistanceDensity = v; overrideMask |= OverrideField.EnableDistanceDensity; }
        public void OverrideDistanceDensityMode(DistanceDensityMode v)
            { distanceDensityMode = v; overrideMask |= OverrideField.DistanceDensityMode; }
        public void OverrideDensityStartScreenSize(float v)
            { densityStartScreenSize = v; overrideMask |= OverrideField.DensityStartScreenSize; }
        public void OverrideDensityStartDistance(float v)
            { densityStartDistance = v; overrideMask |= OverrideField.DensityStartDistance; }
        public void OverrideDensityEndDistance(float v)
            { densityEndDistance = v; overrideMask |= OverrideField.DensityEndDistance; }
        public void OverrideDensityMinFraction(float v)
            { densityMinFraction = v; overrideMask |= OverrideField.DensityMinFraction; }
        public void OverrideDensityFadeWidth(float v)
            { densityFadeWidth = v; overrideMask |= OverrideField.DensityFadeWidth; }
        public void OverrideLayer(int v)
            { layer = v; overrideMask |= OverrideField.Layer; }
        public void OverrideRenderingLayerMask(uint v)
            { renderingLayerMask = v; overrideMask |= OverrideField.RenderingLayerMask; }

        public int lodCount => asset?.lodCount ?? 1;
        public LODFadeMode fadeMode => asset?.fadeMode ?? LODFadeMode.None;
        public uint percentageFadeMask => asset?.percentageFadeMask ?? 0;
        public WindID speedTreeRendererID => asset?.speedTreeRendererID ?? default;
        public float lodReferenceSize => asset?.lodReferenceSize ?? 0;
        public Mesh mesh => asset?.mesh;
        public Material[] materials => asset?.materials;
        public float[] lodScreenSizes => asset?.lodScreenSizes;
        public float[] lodFadeTransitionWidths => asset?.lodFadeTransitionWidths;
        public ShadowCastingMode[] lodShadowCastingModes => asset?.lodShadowCastingModes;
        public ShadowCastingMode GetShadowCastingMode() => asset?.GetShadowCastingMode() ?? ShadowCastingMode.On;
        public MotionVectorGenerationMode[] lodMotionVectorModes => asset?.lodMotionVectorModes;
        public Mesh GetLODMesh(int lod) => asset?.GetLODMesh(lod);
        public Material[] GetLODMaterials(int lod) => asset?.GetLODMaterials(lod);
        public Mesh GetLODMeshRaw(int lod) => asset?.GetLODMeshRaw(lod);
        public Material[] GetLODMaterialsRaw(int lod) => asset?.GetLODMaterialsRaw(lod);
        public float GetLODFadeWidth(int lod) => asset?.GetLODFadeWidth(lod) ?? 0.5f;
        public uint GetLODShadowMask() => asset?.GetLODShadowMask() ?? 0xFFFFFFFF;
#if UNITY_6000_2_OR_NEWER
        public bool usesMeshLod => asset?.usesMeshLod ?? false;
        public int[] meshLodIndices => asset?.meshLodIndices;
#endif

        /// <summary>combined asset+config dedup key. distinct prototypes hash to distinct keys.</summary>
        public long GetSignatureKey()
        {
            var h = new Hash64();

            // asset identity
            if (asset != null)
            {
                if (asset.lodMeshes != null)
                    for (int i = 0; i < asset.lodMeshes.Length; i++)
                        h.Add(asset.lodMeshes[i] != null ? GPUBatcherHelpers.IdentityHash(asset.lodMeshes[i]) : 0);
                if (asset.lodMaterials != null)
                    for (int i = 0; i < asset.lodMaterials.Length; i++)
                        if (asset.lodMaterials[i] != null)
                            for (int j = 0; j < asset.lodMaterials[i].Length; j++)
                                h.Add(asset.lodMaterials[i][j] != null ? GPUBatcherHelpers.IdentityHash(asset.lodMaterials[i][j]) : 0);
                if (asset.lodScreenSizes != null)
                    for (int i = 0; i < asset.lodScreenSizes.Length; i++)
                        h.Add(asset.lodScreenSizes[i]);
                if (asset.lodShadowCastingModes != null)
                    for (int i = 0; i < asset.lodShadowCastingModes.Length; i++)
                        h.Add(asset.lodShadowCastingModes[i]);
                if (asset.lodFadeTransitionWidths != null)
                    for (int i = 0; i < asset.lodFadeTransitionWidths.Length; i++)
                        h.Add(asset.lodFadeTransitionWidths[i]);
                h.Add(asset.fadeMode);
                h.Add(asset.lodReferenceSize);
                h.Add(asset.percentageFadeMask);
                h.Add(asset.speedTreeRendererID);
#if UNITY_6000_2_OR_NEWER
                h.Add(asset.usesMeshLod);
                if (asset.meshLodIndices != null)
                    for (int i = 0; i < asset.meshLodIndices.Length; i++)
                        h.Add(asset.meshLodIndices[i]);
#endif
            }

            // render config
            h.Add(maxShadowCascade);
            h.Add(minShadowLOD);
            h.Add(maxShadowDistance);
            h.Add(maxShadowCrossfadeCascade);
            h.Add(lodFadeDuration);
            h.Add(prototypeGroup != null ? GPUBatcherHelpers.IdentityHash(prototypeGroup) : 0);
            h.Add(enableDistanceDensity);
            h.Add(distanceDensityMode);
            h.Add(densityStartScreenSize);
            h.Add(densityStartDistance);
            h.Add(densityEndDistance);
            h.Add(densityMinFraction);
            h.Add(densityFadeWidth);
            h.Add(motionVectorMode);
            h.Add(animateCrossFading);
            h.Add(layer);
            h.Add(renderingLayerMask);
            return h.ToHashCode();
        }

        public void ApplyDefaults(RenderSignature fresh)
        {
            if ((overrideMask & OverrideField.MotionVectorMode) == 0) motionVectorMode = fresh.motionVectorMode;
            if ((overrideMask & OverrideField.AnimateCrossFading) == 0) animateCrossFading = fresh.animateCrossFading;
            if ((overrideMask & OverrideField.MaxShadowCascade) == 0) maxShadowCascade = fresh.maxShadowCascade;
            if ((overrideMask & OverrideField.MinShadowLOD) == 0) minShadowLOD = fresh.minShadowLOD;
            if ((overrideMask & OverrideField.MaxShadowDistance) == 0) maxShadowDistance = fresh.maxShadowDistance;
            if ((overrideMask & OverrideField.MaxShadowCrossfadeCascade) == 0) maxShadowCrossfadeCascade = fresh.maxShadowCrossfadeCascade;
            if ((overrideMask & OverrideField.LodFadeDuration) == 0) lodFadeDuration = fresh.lodFadeDuration;
            if ((overrideMask & OverrideField.EnableDistanceDensity) == 0) enableDistanceDensity = fresh.enableDistanceDensity;
            if ((overrideMask & OverrideField.DistanceDensityMode) == 0) distanceDensityMode = fresh.distanceDensityMode;
            if ((overrideMask & OverrideField.DensityStartScreenSize) == 0) densityStartScreenSize = fresh.densityStartScreenSize;
            if ((overrideMask & OverrideField.DensityStartDistance) == 0) densityStartDistance = fresh.densityStartDistance;
            if ((overrideMask & OverrideField.DensityEndDistance) == 0) densityEndDistance = fresh.densityEndDistance;
            if ((overrideMask & OverrideField.DensityMinFraction) == 0) densityMinFraction = fresh.densityMinFraction;
            if ((overrideMask & OverrideField.DensityFadeWidth) == 0) densityFadeWidth = fresh.densityFadeWidth;
            if ((overrideMask & OverrideField.Layer) == 0) layer = fresh.layer;
            if ((overrideMask & OverrideField.RenderingLayerMask) == 0) renderingLayerMask = fresh.renderingLayerMask;
        }
    }

    // per-shadow-draw-command metadata for cascade/fade filtering
    public struct ShadowCommandFilterInfo
    {
        public int cascadeIndex;
        public int fadeState;
        public int protoMaxCascades;
    }

    // contiguous range free-list allocator.
    // allocates/frees contiguous ranges from a virtual address space.
    // used for bucket indices and visibility buffer offsets.
    public class RangeAllocator
    {
        private readonly List<(int start, int count)> _free = new();
        private int _hwm; // high water mark - next fresh address

        public int highWaterMark => _hwm;

        // allocate a contiguous range of 'count' slots.
        // returns the start offset.
        public int Allocate(int count)
        {
            if (count <= 0) return 0;

            // first-fit search
            for (int i = 0; i < _free.Count; i++)
            {
                var (start, size) = _free[i];
                if (size >= count)
                {
                    if (size == count)
                        _free.RemoveAt(i);
                    else
                        _free[i] = (start + count, size - count);
                    return start;
                }
            }

            // no free range big enough - bump hwm
            int result = _hwm;
            _hwm += count;
            return result;
        }

        // free a contiguous range, coalescing with neighbors
        public void Free(int start, int count)
        {
            if (count <= 0) return;

            int end = start + count;

            // find insertion point (sorted by start)
            int idx = 0;
            while (idx < _free.Count && _free[idx].start < start)
                idx++;

            // coalesce with previous
            bool mergedPrev = false;
            if (idx > 0)
            {
                var (ps, pc) = _free[idx - 1];
                if (ps + pc == start)
                {
                    _free[idx - 1] = (ps, pc + count);
                    mergedPrev = true;
                    idx--; // point at merged entry
                }
            }

            // coalesce with next
            if (mergedPrev)
            {
                var (ms, mc) = _free[idx];
                if (idx + 1 < _free.Count)
                {
                    var (ns, nc) = _free[idx + 1];
                    if (ms + mc == ns)
                    {
                        _free[idx] = (ms, mc + nc);
                        _free.RemoveAt(idx + 1);
                    }
                }
            }
            else
            {
                // check if we can merge with next
                if (idx < _free.Count)
                {
                    var (ns, nc) = _free[idx];
                    if (end == ns)
                    {
                        _free[idx] = (start, count + nc);
                        return;
                    }
                }
                _free.Insert(idx, (start, count));
            }
        }

        // try to grow an existing allocation in-place.
        // returns true if the adjacent free space was sufficient.
        public bool TryExtend(int start, int oldCount, int newCount)
        {
            if (newCount <= oldCount) return true;
            int growth = newCount - oldCount;
            int end = start + oldCount;

            // if allocation is at the HWM boundary, just bump it
            if (end == _hwm) { _hwm += growth; return true; }

            for (int i = 0; i < _free.Count; i++)
            {
                var (fs, fc) = _free[i];
                if (fs == end && fc >= growth)
                {
                    if (fc == growth)
                        _free.RemoveAt(i);
                    else
                        _free[i] = (fs + growth, fc - growth);
                    return true;
                }
            }
            return false;
        }

        // reset to empty state
        public void Clear()
        {
            _free.Clear();
            _hwm = 0;
        }
    }
}
