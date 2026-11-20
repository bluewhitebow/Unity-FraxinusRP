// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    // handles prefab edits in the Project window (non-isolation mode) to refresh BRG prototypes
    internal class GPUBatcherPrefabPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            // plugin deleted externally
            foreach (string assetPath in deletedAssets)
            {
                if (assetPath.EndsWith("GPUBatcherConfig.cs"))
                {
                    GPUBatcherConfig.SuppressAssetCreation();
                    GPUBatcherRenderer.Shutdown();
                    return;
                }
            }

            // only process if BRG system exists
            if (!GPUBatcherRenderer.HasInstance) return;

            // any asset reimport can trigger shader variant recompilation,
            // which silently unbinds all ComputeShader.SetBuffer bindings
            GPUBatcherRenderer.Instance.InvalidateBufferBindings();

            bool anyMaterialChanged = false;

            foreach (string assetPath in importedAssets)
            {
                // detect prefab changes
                if (assetPath.EndsWith(".prefab"))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null) continue;

                    GPUBatcherRenderer.MarkPrototypeDirty(prefab);
                }
                // detect material changes (.mat files)
                else if (assetPath.EndsWith(".mat"))
                {
                    anyMaterialChanged = true;
                }
            }

            // deletions destroy assets the BRG may still have registered
            bool anyDestroyed = deletedAssets.Length > 0
                && GPUBatcherRenderer.Instance.IsInitialized
                && GPUBatcherRenderer.Instance.HasDestroyedRegisteredAssets();

            // if any material changed, mark system dirty for batch rebuild
            // (can't cheaply map material -> prefab)
            if ((anyMaterialChanged || anyDestroyed) && GPUBatcherRenderer.HasInstance && GPUBatcherRenderer.Instance.IsInitialized)
            {
                GPUBatcherRenderer.Instance.MarkBatchesDirtyForFullRebuild();
            }
        }
    }

    // shuts the system down before the plugin is deleted in the editor,
    // so config auto-create can't resurrect the folder mid-delete
    internal class GPUBatcherAssetDeleteProcessor : AssetModificationProcessor
    {
        static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
        {
            if (!assetPath.EndsWith(".cs") && !AssetDatabase.IsValidFolder(assetPath))
                return AssetDeleteResult.DidNotDelete;

            var guids = AssetDatabase.FindAssets("GPUBatcherConfig t:MonoScript");
            foreach (var guid in guids)
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (scriptPath == assetPath || scriptPath.StartsWith(assetPath + "/", System.StringComparison.Ordinal))
                {
                    GPUBatcherConfig.SuppressAssetCreation();
                    GPUBatcherRenderer.Shutdown();
                    break;
                }
            }
            return AssetDeleteResult.DidNotDelete;
        }
    }
}
