using UnityEngine;
using FraxinusEngine.Rendering.Ultra;

namespace FraxinusEditor.Rendering.Ultra
{
    /// <summary>
    /// ShaderGraph metadata for UltraRP shaders.
    /// </summary>
    class UltraRPMetadata : ScriptableObject
    {
        public ShaderUtils.ShaderID shaderID;
        public AlphaMode alphaMode;
        public bool allowMaterialOverride;
        public SurfaceType surfaceType;
        public bool castShadows;
        public bool hasVertexModificationInMotionVector;
        public bool isVFXCompatible;
    }
}
