// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace Fraxinus.GPUBatcher.Editor
{
    internal static class ConvertToTerrainPrefab
    {
        const string kMenu = "Tools/Fraxinus GPU Batcher/Convert To Terrain Prefab";
        const string kSubfolder = "TerrainDetailPrefab";

        [MenuItem(kMenu, true)]
        static bool Validate()
        {
            var selection = Selection.GetFiltered<GameObject>(SelectionMode.Assets);
            foreach (var go in selection)
            {
                if (PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.NotAPrefab)
                    return true;
            }
            return false;
        }

        [MenuItem(kMenu)]
        static void Convert()
        {
            var selection = Selection.GetFiltered<GameObject>(SelectionMode.Assets);
            int created = 0, skipped = 0;

            var outDirs = new System.Collections.Generic.Dictionary<GameObject, string>();
            foreach (var source in selection)
            {
                if (PrefabUtility.GetPrefabAssetType(source) == PrefabAssetType.NotAPrefab)
                    continue;

                string sourcePath = AssetDatabase.GetAssetPath(source);
                if (string.IsNullOrEmpty(sourcePath))
                    continue;

                string sourceDir = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
                if (!sourceDir.StartsWith("Assets/", System.StringComparison.Ordinal) && sourceDir != "Assets")
                {
                    sourceDir = "Assets/FraxinusGPUBatcherData";
                    if (!AssetDatabase.IsValidFolder(sourceDir))
                        AssetDatabase.CreateFolder("Assets", "FraxinusGPUBatcherData");
                }
                string outDir = $"{sourceDir}/{kSubfolder}";
                if (!AssetDatabase.IsValidFolder(outDir))
                    AssetDatabase.CreateFolder(sourceDir, kSubfolder);
                outDirs[source] = outDir;
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var source in selection)
                {
                    if (PrefabUtility.GetPrefabAssetType(source) == PrefabAssetType.NotAPrefab)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        if (outDirs.TryGetValue(source, out var outDir) && TryConvertOne(source, outDir))
                            created++;
                        else
                            skipped++;
                    }
                    catch (System.Exception ex)
                    {
                        skipped++;
                        Debug.LogError($"[ConvertToTerrainPrefab] creation failed for '{source.name}': {ex.Message}\n{ex.StackTrace}", source);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"[ConvertToTerrainPrefab] created {created}, skipped {skipped}");
        }

        static bool TryConvertOne(GameObject source, string outDir)
        {
            var lod0Renderer = FindLOD0MeshRenderer(source);
            if (lod0Renderer == null)
            {
                Debug.LogWarning($"[ConvertToTerrainPrefab] '{source.name}' has no LOD0 MeshRenderer, skipping.", source);
                return false;
            }

            var lod0Filter = lod0Renderer.GetComponent<MeshFilter>();
            if (lod0Filter == null || lod0Filter.sharedMesh == null)
            {
                Debug.LogWarning($"[ConvertToTerrainPrefab] '{source.name}' LOD0 renderer has no MeshFilter/mesh, skipping.", source);
                return false;
            }

            string outPath = $"{outDir}/{source.name}_NoLODForTerrain.prefab";
            outPath = AssetDatabase.GenerateUniqueAssetPath(outPath);

            var temp = new GameObject($"{source.name}_NoLODForTerrain");
            try
            {
                var mf = temp.AddComponent<MeshFilter>();
                mf.sharedMesh = lod0Filter.sharedMesh;

                var mr = temp.AddComponent<MeshRenderer>();
                mr.sharedMaterials = lod0Renderer.sharedMaterials;
                mr.shadowCastingMode = lod0Renderer.shadowCastingMode;
                mr.receiveShadows = lod0Renderer.receiveShadows;
                mr.lightProbeUsage = lod0Renderer.lightProbeUsage;
                mr.reflectionProbeUsage = lod0Renderer.reflectionProbeUsage;
                mr.motionVectorGenerationMode = lod0Renderer.motionVectorGenerationMode;
                mr.allowOcclusionWhenDynamic = lod0Renderer.allowOcclusionWhenDynamic;
                mr.renderingLayerMask = lod0Renderer.renderingLayerMask;

                var overrideComp = temp.AddComponent<GPUBatcherTerrainDetailOverride>();
                overrideComp.overridePrefab = source;

                PrefabUtility.SaveAsPrefabAsset(temp, outPath, out bool success);
                if (!success)
                {
                    Debug.LogError($"[ConvertToTerrainPrefab] failed to save prefab at '{outPath}'.");
                    return false;
                }

                Debug.Log($"[ConvertToTerrainPrefab] created '{outPath}'", AssetDatabase.LoadAssetAtPath<GameObject>(outPath));
                return true;
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }
        }

        static MeshRenderer FindLOD0MeshRenderer(GameObject prefab)
        {
            var lodGroup = prefab.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                if (lods.Length > 0)
                {
                    foreach (var r in lods[0].renderers)
                    {
                        if (r is MeshRenderer mr && mr.GetComponent<MeshFilter>() != null)
                            return mr;
                    }
                }
            }

            return prefab.GetComponentInChildren<MeshRenderer>();
        }
    }
}
