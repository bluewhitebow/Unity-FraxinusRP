using System.Collections.Generic;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
using ProbeVolumeWithBoundsList = System.Collections.Generic.List<UnityEngine.Rendering.ProbeVolumePlacementDescriptor>;
#endif

namespace UnityEngine.Rendering
{
#if UNITY_EDITOR
    internal readonly struct ProbeVolumePlacementDescriptor
    {
        internal readonly Component component;
        internal readonly TProbeReferenceVolumeBase.Volume volume;
        internal readonly Bounds bounds;
        internal readonly bool overrideRendererFilters, overridesSubdivLevels, fillEmptySpaces;
        internal readonly LayerMask objectLayerMask;
        internal readonly float minRendererVolumeSize;
        internal readonly int lowestSubdivLevelOverride, highestSubdivLevelOverride;
        readonly bool m_SceneOnly;

        internal ProbeVolumePlacementDescriptor(ProbeVolume source)
        {
            component = source;
            volume = new TProbeReferenceVolumeBase.Volume(source.GetVolume(), 1, -1);
            bounds = volume.CalculateAABB();
            overrideRendererFilters = source.overrideRendererFilters;
            objectLayerMask = source.objectLayerMask;
            minRendererVolumeSize = source.minRendererVolumeSize;
            overridesSubdivLevels = source.overridesSubdivLevels;
            lowestSubdivLevelOverride = source.lowestSubdivLevelOverride;
            highestSubdivLevelOverride = source.highestSubdivLevelOverride;
            fillEmptySpaces = source.fillEmptySpaces;
            m_SceneOnly = false;
        }

        // APRT shares brick placement with APV, while keeping its own region component.
        // Scene/Global regions are fitted by the standalone baker before subdivision.
        internal ProbeVolumePlacementDescriptor(APRTVolume source, Bounds fittedBounds)
        {
            component = source;
            volume = source.mode == APRTVolume.Mode.Local
                ? new TProbeReferenceVolumeBase.Volume(source.volumeMatrix, 1, -1)
                : new TProbeReferenceVolumeBase.Volume(fittedBounds);
            bounds = volume.CalculateAABB();
            overrideRendererFilters = source.overrideRendererFilters;
            objectLayerMask = source.objectLayerMask;
            minRendererVolumeSize = source.minRendererVolumeSize;
            overridesSubdivLevels = source.overridesSubdivLevels;
            lowestSubdivLevelOverride = source.lowestSubdivLevelOverride;
            highestSubdivLevelOverride = source.highestSubdivLevelOverride;
            fillEmptySpaces = source.fillEmptySpaces;
            m_SceneOnly = source.mode == APRTVolume.Mode.Scene;
        }

        internal bool IncludesScene(Scene scene) => !m_SceneOnly || component.gameObject.scene == scene;

        internal void GetSubdivisionOverride(int maxSubdivisionLevel, out int minLevel, out int maxLevel)
        {
            maxLevel = overridesSubdivLevels ? Mathf.Min(highestSubdivLevelOverride, maxSubdivisionLevel) : maxSubdivisionLevel;
            minLevel = overridesSubdivLevels ? Mathf.Min(lowestSubdivLevelOverride, maxLevel) : 0;
        }
    }
#endif

    struct GIContributors
    {
#if UNITY_EDITOR
        public struct TerrainContributor
        {
            public struct TreePrototype
            {
                public MeshRenderer component;
                public Matrix4x4 transform;
                public Bounds prefabBounds;

                public List<(Matrix4x4 transform, Bounds boundsWS)> instances;
            }

            public Terrain component;
            public Bounds boundsWithTrees;
            public Bounds boundsTerrainOnly;
            public TreePrototype[] treePrototypes;
        }

        public List<(Renderer component, Bounds bounds)> renderers;
        public List<TerrainContributor> terrains;

        public int Count => renderers.Count + terrains.Count;

        internal enum ContributorFilter { All, Scene, Selection };

        internal static bool ContributesGI(GameObject go) =>
            (GameObjectUtility.GetStaticEditorFlags(go) & StaticEditorFlags.ContributeGI) != 0;

        internal static Vector3[] m_Vertices = new Vector3[8];

        static Bounds TransformBounds(Bounds bounds, Matrix4x4 transform)
        {
            Vector3 boundsMin = bounds.min, boundsMax = bounds.max;
            m_Vertices[0] = new Vector3(boundsMin.x, boundsMin.y, boundsMin.z);
            m_Vertices[1] = new Vector3(boundsMax.x, boundsMin.y, boundsMin.z);
            m_Vertices[2] = new Vector3(boundsMax.x, boundsMax.y, boundsMin.z);
            m_Vertices[3] = new Vector3(boundsMin.x, boundsMax.y, boundsMin.z);
            m_Vertices[4] = new Vector3(boundsMin.x, boundsMin.y, boundsMax.z);
            m_Vertices[5] = new Vector3(boundsMax.x, boundsMin.y, boundsMax.z);
            m_Vertices[6] = new Vector3(boundsMax.x, boundsMax.y, boundsMax.z);
            m_Vertices[7] = new Vector3(boundsMin.x, boundsMax.y, boundsMax.z);

            Vector3 min = transform.MultiplyPoint(m_Vertices[0]);
            Vector3 max = min;

            for (int i = 1; i < 8; i++)
            {
                var point = transform.MultiplyPoint(m_Vertices[i]);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            Bounds result = default;
            result.SetMinMax(min, max);
            return result;
        }

        static internal Matrix4x4 GetTreeInstanceTransform(Terrain terrain, TreeInstance tree)
        {
            var position = terrain.GetPosition() + Vector3.Scale(tree.position, terrain.terrainData.size);
            var rotation = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
            var scale = new Vector3(tree.widthScale, tree.heightScale, tree.widthScale);

            return Matrix4x4.TRS(position, rotation, scale);
        }

        static bool IsLOD0(Renderer renderer)
        {
#if UNITY_6000_0_OR_NEWER
            return renderer.isLOD0;
#else
            var lodGroup = renderer.GetComponentInParent<LODGroup>();
            if (lodGroup == null)
                return true;

            var lods = lodGroup.GetLODs();
            if (lods.Length == 0)
                return true;

            var lod0Renderers = lods[0].renderers;
            for (int i = 0; i < lod0Renderers.Length; ++i)
            {
                if (lod0Renderers[i] == renderer)
                    return true;
            }

            return false;
#endif
        }

        public static GIContributors Find(ContributorFilter filter, Scene? scene = null)
        {
            if (filter == ContributorFilter.Scene && scene == null)
                return default;

            Profiling.Profiler.BeginSample("GIContributors.Find");

            var contributors = new GIContributors()
            {
                renderers = new(),
                terrains = new(),
            };

            void PushRenderer(Renderer renderer)
            {
                if (!ContributesGI(renderer.gameObject) || !renderer.gameObject.TryGetComponent<MeshFilter>(out var _) || !renderer.gameObject.activeInHierarchy || !renderer.enabled || !IsLOD0(renderer))
                    return;

                var bounds = renderer.bounds;
                bounds.size += Vector3.one * 0.01f;
                contributors.renderers.Add((renderer, bounds));
            }

            void PushTerrain(Terrain terrain)
            {
                if (!ContributesGI(terrain.gameObject) || !terrain.gameObject.activeInHierarchy || !terrain.enabled || terrain.terrainData == null)
                    return;

                var terrainData = terrain.terrainData;
                var terrainBounds = terrainData.bounds;
                terrainBounds.center += terrain.GetPosition();
                terrainBounds.size += Vector3.one * 0.01f;

                var prototypes = terrainData.treePrototypes;
                var treePrototypes = new TerrainContributor.TreePrototype[prototypes.Length];
                for (int i = 0; i < prototypes.Length; i++)
                {
                    MeshRenderer renderer = null;

                    var prefab = prototypes[i].prefab;
                    if (prefab == null)
                        continue;

                    if (prefab.TryGetComponent<LODGroup>(out var lodGroup))
                    {
                        var groups = lodGroup.GetLODs();
                        if (groups.Length != 0 && groups[0].renderers.Length != 0)
                            renderer = groups[0].renderers[0] as MeshRenderer;
                    }
                    if (renderer == null)
                        renderer = prefab.GetComponent<MeshRenderer>();

                    if (renderer != null && renderer.enabled && ContributesGI(renderer.gameObject))
                    {
                        var tr = prefab.transform;
                        // For some reason, tree instances are not affected by rotation and position of prefab root
                        // But they are affected by scale, and by any other transform in the hierarchy
                        var transform = Matrix4x4.TRS(tr.position, tr.rotation, Vector3.one).inverse * renderer.localToWorldMatrix;

                        // Compute prefab bounds. This will be used to compute highest tree to expand terrain bounds
                        // and to approximate the bounds of tree instances for culling during voxelization.
                        var prefabBounds = TransformBounds(renderer.localBounds, transform);

                        treePrototypes[i] = new TerrainContributor.TreePrototype()
                        {
                            component = renderer,
                            transform = transform,
                            prefabBounds = prefabBounds,
                            instances = new List<(Matrix4x4 transform, Bounds boundsWS)>(),
                        };
                    }
                }

                Vector3 totalMax = terrainBounds.max;
                foreach (var tree in terrainData.treeInstances)
                {
                    var prototype = treePrototypes[tree.prototypeIndex];
                    if (prototype.component == null)
                        continue;

                    // Approximate instance bounds since rotation can only be on y axis
                    var transform = GetTreeInstanceTransform(terrain, tree);
                    var boundsCenter = transform.MultiplyPoint(prototype.prefabBounds.center);
                    var boundsSize = prototype.prefabBounds.size;
                    float maxTreeWidth = Mathf.Max(boundsSize.x, boundsSize.z) * tree.widthScale * Mathf.Sqrt(2.0f);
                    boundsSize = new Vector3(maxTreeWidth, boundsSize.y * tree.heightScale, maxTreeWidth);

                    prototype.instances.Add((transform, new Bounds(boundsCenter, boundsSize)));
                    totalMax.y = Mathf.Max(boundsCenter.y + boundsSize.y * 0.5f, totalMax.y);
                }

                var totalBounds = new Bounds();
                totalBounds.SetMinMax(terrainBounds.min, totalMax);

                contributors.terrains.Add(new TerrainContributor()
                {
                    component = terrain,
                    boundsWithTrees = totalBounds,
                    boundsTerrainOnly = terrainBounds,
                    treePrototypes = treePrototypes,
                });
            }

            if (filter == ContributorFilter.Selection)
            {
                var transforms = Selection.transforms;
                foreach (var transform in transforms)
                {
                    var childrens = transform.gameObject.GetComponentsInChildren<Transform>();
                    foreach (var children in childrens)
                    {
                        if (children.gameObject.TryGetComponent(out Renderer renderer))
                            PushRenderer(renderer);
                        else if (children.gameObject.TryGetComponent(out Terrain terrain))
                            PushTerrain(terrain);
                    }
                }
            }
            else
            {
                var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.InstanceID);
                Profiling.Profiler.BeginSample($"Find Renderers ({renderers.Length})");
                foreach (var renderer in renderers)
                {
                    if (filter != ContributorFilter.Scene || renderer.gameObject.scene == scene)
                        PushRenderer(renderer);
                }
                Profiling.Profiler.EndSample();

                var terrains = Object.FindObjectsByType<Terrain>(FindObjectsSortMode.InstanceID);
                Profiling.Profiler.BeginSample($"Find Terrains ({terrains.Length})");
                foreach (var terrain in terrains)
                {
                    if (filter != ContributorFilter.Scene || terrain.gameObject.scene == scene)
                        PushTerrain(terrain);
                }
                Profiling.Profiler.EndSample();
            }

            Profiling.Profiler.EndSample();
            return contributors;
        }

        static bool DiscardedByProbeVolume(ProbeVolumePlacementDescriptor pv, ProbeVolumeBakingSet bakingSet, float boundsVolume, int layerMask)
        {
            if (bakingSet == null)
                return false;

            float minRendererBoundingBoxSize = bakingSet.minRendererVolumeSize;
            var renderersLayerMask = bakingSet.renderersLayerMask;
            if (pv.overrideRendererFilters)
            {
                minRendererBoundingBoxSize = pv.minRendererVolumeSize;
                renderersLayerMask = pv.objectLayerMask;
            }

            // Skip renderers that have a smaller volume than the min volume size from the profile or probe volume component
            // And renderers whose layer mask is excluded
            return (boundsVolume < minRendererBoundingBoxSize) || (layerMask & renderersLayerMask) == 0;
        }

        public GIContributors Filter(ProbeVolumeBakingSet bakingSet, Bounds cellBounds, ProbeVolumeWithBoundsList probeVolumes)
        {
            Profiling.Profiler.BeginSample("Filter GIContributors");

            var contributors = new GIContributors()
            {
                renderers = new(),
                terrains = new(),
            };

            Profiling.Profiler.BeginSample($"Filter Renderers ({renderers.Count})");
            foreach (var renderer in renderers)
            {
                if (!cellBounds.Intersects(renderer.bounds))
                    continue;

                var volumeSize = renderer.bounds.size;
                float rendererBoundsVolume = volumeSize.x * volumeSize.y * volumeSize.z;
                int rendererLayerMask = 1 << renderer.component.gameObject.layer;

                foreach (var probeVolume in probeVolumes)
                {
                    if (!probeVolume.IncludesScene(renderer.component.gameObject.scene) ||
                        DiscardedByProbeVolume(probeVolume, bakingSet, rendererBoundsVolume, rendererLayerMask) ||
                        !ProbeVolumePositioning.OBBAABBIntersect(probeVolume.volume, renderer.bounds, probeVolume.bounds))
                        continue;

                    contributors.renderers.Add(renderer);
                    break;
                }
            }
            Profiling.Profiler.EndSample();

            Profiling.Profiler.BeginSample($"Filter Terrains ({terrains.Count})");
            foreach (var terrain in terrains)
            {
                if (!cellBounds.Intersects(terrain.boundsWithTrees))
                    continue;

                var volumeSize = terrain.boundsWithTrees.size;
                float terrainBoundsVolume = volumeSize.x * volumeSize.y * volumeSize.z;
                int terrainLayerMask = 1 << terrain.component.gameObject.layer;

                // Find if terrain with trees hits at least one PV
                bool contributes = false;
                foreach (var probeVolume in probeVolumes)
                {
                    if (!probeVolume.IncludesScene(terrain.component.gameObject.scene) ||
                        DiscardedByProbeVolume(probeVolume, bakingSet, terrainBoundsVolume, terrainLayerMask) ||
                        !ProbeVolumePositioning.OBBAABBIntersect(probeVolume.volume, terrain.boundsWithTrees, probeVolume.bounds))
                        continue;

                    contributes = true;
                    break;
                }

                if (!contributes)
                    continue;

                // Cull trees - iterates over all instances for each pv, may be very slow
                var probeVolumesForProto = new List<Bounds>();
                Vector3 totalMax = terrain.boundsTerrainOnly.max;
                var treePrototypes = new TerrainContributor.TreePrototype[terrain.treePrototypes.Length];
                for (int i = 0; i < treePrototypes.Length; i++)
                {
                    var srcProto = terrain.treePrototypes[i];
                    // This prototype may have been previously filtered out
                    if (srcProto.component == null)
                        continue;

                    // Find which pv may intersect instances of this proto
                    probeVolumesForProto.Clear();
                    int prototypeLayerMask = 1 << srcProto.component.gameObject.layer;
                    foreach (var probeVolume in probeVolumes)
                    {
                        // Ignore bounds volume check for trees, assume they are always big enough
                        // Otherwise we have to do the complex math stuff to compute the actual tree bounds
                        if (!DiscardedByProbeVolume(probeVolume, bakingSet, float.MaxValue, prototypeLayerMask))
                            probeVolumesForProto.Add(probeVolume.bounds);
                    }
                    if (probeVolumesForProto.Count == 0)
                        continue;

                    treePrototypes[i] = new TerrainContributor.TreePrototype()
                    {
                        component = srcProto.component,
                        transform = srcProto.transform,
                        prefabBounds = srcProto.prefabBounds,
                        instances = new List<(Matrix4x4 transform, Bounds boundsWS)>(),
                    };

                    // Cull tree instances
                    for (int j = 0; j < srcProto.instances.Count; j++)
                    {
                        var treeBounds = srcProto.instances[j].boundsWS;
                        if (!treeBounds.Intersects(cellBounds))
                            continue;

                        foreach (var pvAABB in probeVolumesForProto)
                        {
                            if (treeBounds.Intersects(pvAABB))
                            {
                                treePrototypes[i].instances.Add(srcProto.instances[j]);
                                totalMax.y = Mathf.Max(treeBounds.max.y, totalMax.y);
                                break;
                            }
                        }
                    }
                }

                // Recompute terrain bounds by excluding trees that were filtered out
                var totalBounds = new Bounds();
                totalBounds.SetMinMax(terrain.boundsTerrainOnly.min, totalMax);

                var terrainContrib = new TerrainContributor()
                {
                    component = terrain.component,
                    boundsWithTrees = totalBounds,
                    boundsTerrainOnly = terrain.boundsTerrainOnly,
                    treePrototypes = treePrototypes,
                };
                contributors.terrains.Add(terrainContrib);
            }
            Profiling.Profiler.EndSample();

            Profiling.Profiler.EndSample();
            return contributors;
        }

        public GIContributors FilterLayerMaskOnly(LayerMask layerMask)
        {
            Profiling.Profiler.BeginSample("Filter GIContributors LayerMask");

            var contributors = new GIContributors()
            {
                renderers = new(),
                terrains = new(),
            };

            foreach (var renderer in renderers)
            {
                int rendererLayerMask = 1 << renderer.component.gameObject.layer;
                if ((rendererLayerMask & layerMask) != 0)
                    contributors.renderers.Add(renderer);
            }

            foreach (var terrain in terrains)
            {
                int terrainLayerMask = 1 << terrain.component.gameObject.layer;
                if ((terrainLayerMask & layerMask) != 0)
                {
                    // Filter out trees
                    var filteredPrototypes = new List<TerrainContributor.TreePrototype>();
                    foreach (var treeProto in terrain.treePrototypes)
                    {
                        // check if the mesh renderer exists
                        if (treeProto.component != null)
                        {
                            int treeProtoLayerMask = 1 << treeProto.component.gameObject.layer;

                            if ((treeProtoLayerMask & layerMask) != 0)
                                filteredPrototypes.Add(treeProto);
                        }
                    }

                    var terrainContrib = new TerrainContributor()
                    {
                        component = terrain.component,
                        boundsWithTrees = terrain.boundsWithTrees,
                        boundsTerrainOnly = terrain.boundsTerrainOnly,
                        treePrototypes = filteredPrototypes.ToArray(),
                    };

                    contributors.terrains.Add(terrainContrib);
                }
            }

            Profiling.Profiler.EndSample();
            return contributors;
        }
#endif
    }
}
