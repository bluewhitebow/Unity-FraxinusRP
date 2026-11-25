using UnityEngine;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;
using static FraxinusEngine.Rendering.Ultra.ShaderUtils;

using UnityEditor;

namespace FraxinusEditor.Rendering.Ultra
{
    public static class ShaderEditorUtils
    {
        #region Editor ShaderID and Material Update


        public static bool IsShaderGraph(this ShaderID id)
        {
            return ((int)id >= 1000);
        }

        // The material tag is also present on in-memory ShaderGraph shaders.
        // Checking it does not require the optional ShaderGraph editor assembly.
        public static bool IsShaderGraph(this Material material)
        {
            return material != null && !string.IsNullOrEmpty(material.GetTag("ShaderGraphShader", false, string.Empty));
        }

        public static ShaderID GetShaderID(Shader shader)
        {
            if (shader == null)
                return ShaderID.Unknown;

            // Hybrid shares Complex Lit's material properties and ForwardOnly companion passes.
            if (shader.name == "Ultra Render Pipeline/Hybrid Lit")
                return ShaderID.ComplexLit;

            // Preserve identification of imported UltraRP graph metadata without
            // depending on ShaderGraph 17's internal editor API.
            var metadataId = GetMetadataShaderID(shader);
            if (metadataId != ShaderID.Unknown)
                return metadataId;
            return (ShaderID)ShaderUtils.GetEnumFromPath(shader.name);
        }

        static ShaderID GetMetadataShaderID(UnityEngine.Object assetObject)
        {
            var path = AssetDatabase.GetAssetPath(assetObject);
            if (string.IsNullOrEmpty(path))
                return ShaderID.Unknown;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is UltraRPMetadata metadata)
                    return metadata.shaderID;
            }
            return ShaderID.Unknown;
        }

        internal static bool HasMotionVectorLightModeTag(ShaderID id)
        {
            // Currently only these ShaderIDs have a pass with a { "LightMode" = "MotionVectors" } tag
            switch (id)
            {
                case ShaderID.Lit:
                case ShaderID.Unlit:
                case ShaderID.SimpleLit:
                case ShaderID.ComplexLit:
                case ShaderID.BakedLit:
                case ShaderID.SG_Unlit:
                case ShaderID.SG_Lit:
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Material update type for shader keyword updates.
        /// </summary>
        internal enum MaterialUpdateType
        {
            CreatedNewMaterial,
            ChangedAssignedShader,
            ModifiedShader,
            ModifiedMaterial
        }

        // Helper used by VFX, allow retrieval of ShaderID on another object than material.shader
        internal static void UpdateMaterial(Material material, MaterialUpdateType updateType, UnityEngine.Object assetWithURPMetaData)
        {
            var currentShaderId = assetWithURPMetaData != null
                ? GetMetadataShaderID(assetWithURPMetaData)
                : ShaderID.Unknown;
            UpdateMaterial(material, updateType, currentShaderId);
        }

        // This is used to update a material's keywords, applying any shader-associated logic
        internal static void UpdateMaterial(Material material, MaterialUpdateType updateType, ShaderID shaderID = ShaderID.Unknown)
        {
            // if unknown, look it up from the material's shader
            if (shaderID == ShaderID.Unknown)
                shaderID = GetShaderID(material.shader);

            switch (shaderID)
            {
                case ShaderID.SG_Lit:
                    ShaderGraphLitGUI.UpdateMaterial(material, updateType);
                    break;
                case ShaderID.SG_Unlit:
                    ShaderGraphUnlitGUI.UpdateMaterial(material, updateType);
                    break;
            }
        }

        #endregion
    }
}
