using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    internal enum DecalSurfaceData
    {
        [Tooltip("Decals will affect only base color and emission.")]
        Albedo,
        [Tooltip("Decals will affect only base color, normal and emission.")]
        AlbedoNormal,
        [Tooltip("Decals will affect base color, normal, metallic, ambient occlusion, smoothness and emission.")]
        AlbedoNormalMAOS,
    }

    internal enum DecalTechnique
    {
        Invalid,
        DBuffer,
        ScreenSpace,
        GBuffer,
    }

    internal enum DecalTechniqueOption
    {
        [Tooltip("Automatically selects technique based on build platform.")]
        Automatic,
        [Tooltip("Renders decals into DBuffer and then applied during opaque rendering. Requires DepthNormal prepass which makes not viable solution for the tile based renderers common on mobile.")]
        [InspectorName("DBuffer")]
        DBuffer,
        [Tooltip("Renders decals after opaque objects with normal reconstructed from depth. The decals are simply rendered as mesh on top of opaque ones, as result does not support blending per single surface data (etc. normal blending only).")]
        ScreenSpace,
    }

    [System.Serializable]
    internal class DBufferSettings
    {
        public DecalSurfaceData surfaceData = DecalSurfaceData.AlbedoNormalMAOS;
    }

    internal enum DecalNormalBlend
    {
        [Tooltip("Low quality of normal reconstruction (Uses 1 sample).")]
        Low,
        [Tooltip("Medium quality of normal reconstruction (Uses 5 samples).")]
        Medium,
        [Tooltip("High quality of normal reconstruction (Uses 9 samples).")]
        High,
    }

    [System.Serializable]
    internal class DecalScreenSpaceSettings
    {
        public DecalNormalBlend normalBlend = DecalNormalBlend.Low;
    }
}
