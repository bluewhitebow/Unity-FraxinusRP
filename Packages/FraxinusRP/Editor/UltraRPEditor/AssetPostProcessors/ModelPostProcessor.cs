using UnityEngine;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    class ModelPostprocessor : AssetPostprocessor
    {
        void OnPostprocessModel(GameObject go)
        {
            CoreEditorUtils.AddAdditionalData<Camera, UltraAdditionalCameraData>(go);
            CoreEditorUtils.AddAdditionalData<Light, UltraAdditionalLightData>(go);
        }
    }
}
