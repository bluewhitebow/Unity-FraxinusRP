using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    [InitializeOnLoad]
    static class ShaderGraphMaterialsUpdater
    {
        static ShaderGraphMaterialsUpdater()
        {
            GraphData.onSaveGraph += OnShaderGraphSaved;
        }

        static void OnShaderGraphSaved(Shader shader, object saveContext)
        {
            if (shader == null || !(saveContext is UltraRPShaderGraphSaveContext context) || !context.updateMaterials)
                return;

            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material.shader != shader)
                    continue;

                ShaderEditorUtils.UpdateMaterial(material, ShaderEditorUtils.MaterialUpdateType.ModifiedShader);
                EditorUtility.SetDirty(material);
            }
        }
    }
}
