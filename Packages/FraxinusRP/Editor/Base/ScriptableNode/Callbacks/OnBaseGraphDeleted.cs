using System.IO;
using UnityEngine;
using FraxinusEngine.Base;
using UnityEditor;

namespace FraxinusEditor.Base
{
    [ExecuteAlways]
    public class DeleteCallback : AssetModificationProcessor
    {
        static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            if (Path.GetExtension(path) == "asset")
            {
                var objects = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var obj in objects)
                {
                    if (obj is BaseGraph b)
                    {
                        foreach (var graphWindow in Resources.FindObjectsOfTypeAll<BaseGraphWindow>())
                            graphWindow.OnGraphDeleted();

                        b.OnAssetDeleted();
                    }
                }

            }

            return AssetDeleteResult.DidNotDelete;
        }
    }
}