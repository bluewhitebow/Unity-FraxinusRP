// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherInstanceGroup
    {
        // walks each direct child's hierarchy; every LODGroup and standalone MeshRenderer becomes one
        // instance (matching GPUBatcherGameObjectGroup). signatures are deduped per prototype, then the child
        // roots that contributed instances are destroyed (their TRS lives in the packed blob).
        public void Bake()
        {
            if (_baked) return;
            if (GPUBatcherRenderer.instance?.isInitialized != true) return;

            var keyToIndex = new Dictionary<long, int>();
            _signatures.Clear();
            var packed = new List<PackedInstance>();
            var toDestroy = new List<GameObject>();

            foreach (Transform child in transform)
            {
                int before = packed.Count;
                CollectInstances(child.gameObject, keyToIndex, packed);
                if (packed.Count > before)
                    toDestroy.Add(child.gameObject);
            }

            if (packed.Count == 0)
            {
                Debug.LogWarning("GPUBatcherInstanceGroup: no child renderers found to bake");
                return;
            }

            PackInstances(packed);
            foreach (var go in toDestroy)
                DestroyImmediate(go);

            _baked = true;
            EditorUtility.SetDirty(this);
            if (isActiveAndEnabled) Initialize();

            Debug.Log($"GPUBatcherInstanceGroup: baked {_instanceCount} instances, {_signatures.Count} prototypes.");
        }

        // each LODGroup and each unclaimed MeshRenderer in root's hierarchy becomes one instance
        void CollectInstances(GameObject root, Dictionary<long, int> keyToIndex, List<PackedInstance> packed)
        {
            var lodGroups = root.GetComponentsInChildren<LODGroup>();

            // renderers claimed by a LODGroup are skipped as standalone
            var claimed = new HashSet<MeshRenderer>();
            foreach (var lg in lodGroups)
                foreach (var lod in lg.GetLODs())
                    if (lod.renderers != null)
                        foreach (var r in lod.renderers)
                            if (r is MeshRenderer mr) claimed.Add(mr);

            var seen = new HashSet<GameObject>();

            foreach (var lg in lodGroups)
                AddInstance(lg.gameObject, keyToIndex, packed, seen);

            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (!mr.enabled) continue;
                if (claimed.Contains(mr)) continue;
                if (!mr.TryGetComponent<MeshFilter>(out var mf) || mf.sharedMesh == null) continue;
                AddInstance(mr.gameObject, keyToIndex, packed, seen);
            }
        }

        void AddInstance(GameObject source, Dictionary<long, int> keyToIndex, List<PackedInstance> packed, HashSet<GameObject> seen)
        {
            if (!seen.Add(source)) return;

            long key = ProtoKey(source);
            if (!keyToIndex.TryGetValue(key, out int protoIndex))
            {
                var snapshot = SnapshotSignature(source);
                if (snapshot == null) return;
                protoIndex = _signatures.Count;
                _signatures.Add(snapshot);
                keyToIndex[key] = protoIndex;
            }

            var t = source.transform;
            packed.Add(new PackedInstance
            {
                position = t.position,
                rotation = t.rotation,
                scale = t.lossyScale,
                protoIndex = protoIndex
            });
        }

        public void ExpandToGameObjects()
        {
            if (!_baked || _instanceBlob == null || _instanceBlob.Length == 0) return;

            if (_instanceCount <= 0 || (long)_instanceCount * UnsafeUtility.SizeOf<PackedInstance>() != _instanceBlob.Length)
            {
                Debug.LogWarning("GPUBatcherInstanceGroup: inconsistent baked instance count. The original bake was kept.", this);
                return;
            }
            using var bytes = new NativeArray<byte>(_instanceBlob, Allocator.Temp);
            var instances = bytes.Reinterpret<PackedInstance>(1);
            var checkedSignatures = new HashSet<int>();
            for (int i = 0; i < instances.Length; i++)
            {
                int proto = instances[i].protoIndex;
                if (!checkedSignatures.Add(proto)) continue;
                if (_signatures != null && proto >= 0 && proto < _signatures.Count && CanReconstruct(_signatures[proto])) continue;
                Debug.LogWarning($"GPUBatcherInstanceGroup: cannot restore prototype {proto}. Repair its prefab or mesh references and retry. The original bake was kept.", this);
                return;
            }

            var created = new List<GameObject>(instances.Length);
            try
            {
                float3 parentScale = transform.lossyScale;
                for (int i = 0; i < instances.Length; i++)
                {
                    var inst = instances[i];
                    var go = ReconstructInstance(_signatures[inst.protoIndex], created);
                    go.transform.SetParent(transform, worldPositionStays: false);
                    go.transform.SetPositionAndRotation(inst.position, inst.rotation);
                    // lossyScale -> localScale (assumes no skew on parent)
                    go.transform.localScale = new Vector3(
                        parentScale.x != 0 ? inst.scale.x / parentScale.x : inst.scale.x,
                        parentScale.y != 0 ? inst.scale.y / parentScale.y : inst.scale.y,
                        parentScale.z != 0 ? inst.scale.z / parentScale.z : inst.scale.z);
                }
            }
            catch (System.Exception e)
            {
                foreach (var go in created)
                    if (go != null) DestroyImmediate(go);
                Debug.LogException(e, this);
                return;
            }
            Shutdown();
            _instanceBlob = System.Array.Empty<byte>();
            _instanceCount = 0;
            _signatures.Clear();
            _baked = false;
            EditorUtility.SetDirty(this);
        }

        // rebuild an editable GameObject from a snapshot using the stored source meshes directly (no
        // recombine - one renderer per source mesh at its matrix), reconstructing the original LOD layout.
        static bool CanReconstruct(BakedSignature sig)
        {
            if (sig == null) return false;
            if (sig.sourcePrefab != null) return true;
            if (sig.lods == null) return false;
            bool hasMesh = false;
            foreach (var lod in sig.lods)
            {
                if (lod == null) return false;
                if (lod.directMesh != null) { hasMesh = true; continue; }
                if (lod.sourceMeshes == null) return false;
                foreach (var mesh in lod.sourceMeshes)
                {
                    if (mesh == null) return false;
                    hasMesh = true;
                }
            }
            return hasMesh;
        }

        GameObject ReconstructInstance(BakedSignature sig, List<GameObject> created)
        {
            if (sig.sourcePrefab != null)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(sig.sourcePrefab, gameObject.scene);
                created.Add(instance);
                return instance;
            }

            var root = new GameObject("BakedInstance") { layer = sig.layer };
            created.Add(root);

            // single LOD: renderers directly under the root, no LODGroup
            if (sig.lods.Length == 1)
            {
                BuildLodRenderers(root, sig, 0);
                return root;
            }

            var lodArray = new LOD[sig.lods.Length];
            for (int i = 0; i < sig.lods.Length; i++)
            {
                var lodGO = new GameObject($"LOD{i}") { layer = sig.layer };
                lodGO.transform.SetParent(root.transform, false);
                var renderers = BuildLodRenderers(lodGO, sig, i);
                lodArray[i] = new LOD(sig.lods[i].screenSize, renderers)
                {
                    fadeTransitionWidth = sig.lods[i].fadeWidth
                };
            }

            var lodGroup = root.AddComponent<LODGroup>();
            lodGroup.fadeMode = sig.fadeMode;
            if (sig.lodReferenceSize > 0f) lodGroup.size = sig.lodReferenceSize;
            lodGroup.SetLODs(lodArray);
            lodGroup.RecalculateBounds();
            return root;
        }

        Renderer[] BuildLodRenderers(GameObject parent, BakedSignature sig, int lodIndex)
        {
            var lod = sig.lods[lodIndex];

            // single direct (uncombined) mesh: one renderer on the parent
            if (lod.directMesh != null)
            {
                var mr = AddRenderer(parent, lod.directMesh, lod.materials, sig.renderingLayerMask, lod.shadowMode, lod.motionMode);
                return mr != null ? new Renderer[] { mr } : System.Array.Empty<Renderer>();
            }

            if (lod.sourceMeshes == null || lod.sourceMeshes.Length == 0) return System.Array.Empty<Renderer>();

            // combined LOD: one child renderer per source mesh, materials sliced by submesh count
            var renderers = new List<Renderer>(lod.sourceMeshes.Length);
            int matOffset = 0;
            for (int j = 0; j < lod.sourceMeshes.Length; j++)
            {
                var mesh = lod.sourceMeshes[j];
                if (mesh == null) continue;

                int subCount = Mathf.Max(1, mesh.subMeshCount);
                var mats = SliceMaterials(lod.materials, ref matOffset, subCount);

                var meshGO = new GameObject($"mesh{j}") { layer = sig.layer };
                meshGO.transform.SetParent(parent.transform, false);
                if (lod.sourceMatrices != null && j < lod.sourceMatrices.Length)
                {
                    var m = lod.sourceMatrices[j];
                    meshGO.transform.localPosition = m.GetColumn(3);
                    meshGO.transform.localRotation = m.rotation;
                    meshGO.transform.localScale = m.lossyScale;
                }

                var mr = AddRenderer(meshGO, mesh, mats, sig.renderingLayerMask, lod.shadowMode, lod.motionMode);
                if (mr != null) renderers.Add(mr);
            }
            return renderers.ToArray();
        }

        static MeshRenderer AddRenderer(GameObject go, Mesh mesh, Material[] materials, uint renderingLayerMask,
            ShadowCastingMode shadowMode, MotionVectorGenerationMode motionMode)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = materials;
            mr.shadowCastingMode = shadowMode;
            mr.motionVectorGenerationMode = motionMode;
            mr.renderingLayerMask = renderingLayerMask;
            return mr;
        }

        static Material[] SliceMaterials(Material[] all, ref int offset, int count)
        {
            if (all == null || all.Length == 0) return System.Array.Empty<Material>();
            var slice = new Material[count];
            for (int k = 0; k < count; k++)
                slice[k] = (offset + k < all.Length) ? all[offset + k] : null;
            offset += count;
            return slice;
        }

        // detect overrides: hash full render content so overridden instances get distinct prototypes.
        // otherwise dedup prefab instances by prefab identity (overrides ignored), non-prefabs by content.
        long ProtoKey(GameObject go)
        {
            if (!_detectPrefabOverrides)
            {
                var prefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                if (prefab != null) return GPUBatcherHelpers.IdentityHash(prefab);
            }
            return GPUBatcherGameObjectGroup.ComputeSourceDataHash(go);
        }

        BakedSignature SnapshotSignature(GameObject go)
        {
            var prefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);

            // detect-overrides off: extract from the source prefab asset, not the instance, so per-instance
            // overrides (material swaps etc.) are ignored and all instances share the clean prototype
            var source = (!_detectPrefabOverrides && prefab != null) ? prefab : go;

            var sig = registration.ExtractRenderSignature(source);
            if (sig?.asset == null) return null;

            var a = sig.asset;
            int n = a.lodMeshes?.Length ?? 0;
            if (n == 0) return null;

            var raw = CollectRawLodInputs(source, n);

            // detect-overrides on: drop the prefab ref for instances that differ from the base prefab so
            // Expand rebuilds the override from the signature instead of re-instantiating the clean prefab
            if (prefab != null && _detectPrefabOverrides
                && GPUBatcherGameObjectGroup.ComputeSourceDataHash(go) != GPUBatcherGameObjectGroup.ComputeSourceDataHash(prefab))
                prefab = null;

            var b = new BakedSignature
            {
                sourcePrefab = prefab,
                lods = new BakedLod[n],
                lodReferenceSize = a.lodReferenceSize,
                fadeMode = a.fadeMode,
                percentageFadeMask = a.percentageFadeMask,
                motionVectorMode = sig.motionVectorMode,
                animateCrossFading = sig.animateCrossFading,
                maxShadowCascade = sig.maxShadowCascade,
                minShadowLOD = sig.minShadowLOD,
                maxShadowDistance = sig.maxShadowDistance,
                maxShadowCrossfadeCascade = sig.maxShadowCrossfadeCascade,
                lodFadeDuration = sig.lodFadeDuration,
                prototypeGroup = sig.prototypeGroup,
                enableDistanceDensity = sig.enableDistanceDensity,
                distanceDensityMode = sig.distanceDensityMode,
                densityStartScreenSize = sig.densityStartScreenSize,
                densityStartDistance = sig.densityStartDistance,
                densityEndDistance = sig.densityEndDistance,
                densityMinFraction = sig.densityMinFraction,
                densityFadeWidth = sig.densityFadeWidth,
                layer = sig.layer,
                renderingLayerMask = sig.renderingLayerMask,
                overrideMask = (uint)sig.overrideMask,
            };
#if UNITY_6000_2_OR_NEWER
            b.usesMeshLod = a.usesMeshLod;
            b.meshLodIndices = a.meshLodIndices;
#endif

            for (int i = 0; i < n; i++)
            {
                var lod = new BakedLod
                {
                    materials = a.lodMaterials != null && i < a.lodMaterials.Length ? a.lodMaterials[i] : null,
                    screenSize = a.lodScreenSizes != null && i < a.lodScreenSizes.Length ? a.lodScreenSizes[i] : 0f,
                    fadeWidth = a.lodFadeTransitionWidths != null && i < a.lodFadeTransitionWidths.Length ? a.lodFadeTransitionWidths[i] : 0f,
                    shadowMode = a.lodShadowCastingModes != null && i < a.lodShadowCastingModes.Length ? a.lodShadowCastingModes[i] : ShadowCastingMode.On,
                    motionMode = a.lodMotionVectorModes != null && i < a.lodMotionVectorModes.Length ? a.lodMotionVectorModes[i] : MotionVectorGenerationMode.Camera,
                };

                var mesh = a.lodMeshes[i];
                if (mesh != null && EditorUtility.IsPersistent(mesh))
                {
                    lod.directMesh = mesh;
                }
                else if (raw != null && i < raw.Count)
                {
                    lod.sourceMeshes = raw[i].meshes;
                    lod.sourceMatrices = raw[i].matrices;
                }
                b.lods[i] = lod;
            }

            return b;
        }

        // per-LOD source meshes + root-local matrices, mirroring ExtractLODRenderers so combined LODs
        // can be recombined at runtime
        static List<(Mesh[] meshes, Matrix4x4[] matrices)> CollectRawLodInputs(GameObject go, int lodCount)
        {
            var result = new List<(Mesh[], Matrix4x4[])>(lodCount);
            var root = go.transform;
            var lodGroup = go.GetComponent<LODGroup>();

            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                {
                    var meshes = new List<Mesh>();
                    var matrices = new List<Matrix4x4>();
                    if (lods[i].renderers != null)
                    {
                        foreach (var r in lods[i].renderers)
                        {
                            if (r is not MeshRenderer mr) continue;
                            if (!mr.enabled) continue;
                            if (!mr.TryGetComponent<MeshFilter>(out var mf) || mf.sharedMesh == null) continue;
                            meshes.Add(mf.sharedMesh);
                            matrices.Add(GPUBatcherHelpers.LocalMatrixRelativeTo(mr.transform, root));
                        }
                    }
                    result.Add((meshes.ToArray(), matrices.ToArray()));
                }
                return result;
            }

            MeshRenderer rootMr = null;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                if (r.enabled) { rootMr = r; break; }
            var rootMf = rootMr != null ? rootMr.GetComponent<MeshFilter>() : null;
            result.Add((
                rootMf != null && rootMf.sharedMesh != null ? new[] { rootMf.sharedMesh } : System.Array.Empty<Mesh>(),
                new[] { Matrix4x4.identity }));
            return result;
        }
    }
}
#endif
