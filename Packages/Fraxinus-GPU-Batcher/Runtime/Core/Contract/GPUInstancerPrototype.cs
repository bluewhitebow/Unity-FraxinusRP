using System;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    [Serializable]
    public abstract class GPUInstancerPrototype : ScriptableObject
    {
        // Keep the default available without materializing it into a
        // profile-backed prototype at runtime. Consumers can use this fallback
        // for an incomplete shadow map while leaving the serialized recipe untouched.
        private static readonly float[] s_defaultShadowLODMap = new float[] {
            0, 4, 0, 0,
            1, 5, 0, 0,
            2, 6, 0, 0,
            3, 7, 0, 0};

        public GameObject prefabObject;

        // Shadows
        public bool isShadowCasting = true;
        public bool useCustomShadowDistance = false;
        public float shadowDistance = 0;
        public float[] shadowLODMap = new float[] {
            0, 4, 0, 0,
            1, 5, 0, 0,
            2, 6, 0, 0,
            3, 7, 0, 0};
        public bool useOriginalShaderForShadow = false;
        public bool cullShadows = false;

        // Culling
        public float minDistance = 0;
        public float maxDistance = 500;
        public bool isFrustumCulling = true;
        public bool isOcclusionCulling = true;
        public float frustumOffset = 0.2f;
        public float minCullingDistance = 0;
        public float occlusionOffset = 0;
        public int occlusionAccuracy = 1;

        // Bounds
        public Vector3 boundsOffset;

        // LOD
        public bool isLODCrossFade = false;
        public bool isLODCrossFadeAnimate = true;
        [Range(0.01f, 1.0f)]
        public float lodFadeTransitionWidth = 0.1f;
        public float lodBiasAdjustment = 1;

        // Billboard
        public GPUInstancerBillboard billboard;
        public bool isBillboardDisabled;
        // Set to true if the object should not have a billboard option
        public bool useGeneratedBillboard = false;
        public bool checkedForBillboardExtensions;

        /// <summary>
        /// Returns the serialized shadow LOD map when it is usable, otherwise
        /// the package default. This method intentionally does not assign the
        /// fallback to <see cref="shadowLODMap"/> so a runtime manager cannot
        /// dirty a Profile/Prototype asset merely by initializing buffers.
        /// </summary>
        public float[] GetShadowLODMapOrDefault()
        {
            return shadowLODMap != null && shadowLODMap.Length == 16
                ? shadowLODMap
                : s_defaultShadowLODMap;
        }

        // Other
        public bool autoUpdateTransformData;
        public GPUInstancerTreeType treeType;
        [NonSerialized]
        public string warningText;
        [NonSerialized]
        public Shader warningShader;

        public override string ToString()
        {
            if (prefabObject != null)
                return prefabObject.name;
            return name;
        }

        public virtual Texture2D GetPreviewTexture()
        {
            return null;
        }
    }

    [Serializable]
    public class GPUInstancerBillboard
    {
        public BillboardQuality billboardQuality = BillboardQuality.Mid;
        public int atlasResolution = 2048;
        public int frameCount = 8;
        public bool replaceLODCullWithBillboard = true;
        public bool isOverridingOriginalCutoff = false;
        public float cutoffOverride = -1f;
        [Range(0.0f, 1.0f)]
        public float billboardBrightness = 0.5f;
        [Range(0.01f, 1.0f)]
        public float billboardDistance = 0.8f;

        public float quadSize;
        public float yPivotOffset;

        public Texture2D albedoAtlasTexture;
        public Texture2D normalAtlasTexture;

        // true if LOD group already has a billboard
        public bool customBillboardInLODGroup;
        // Custom billboard mesh-material options
        public bool useCustomBillboard;
        public Mesh customBillboardMesh;
        public Material customBillboardMaterial;

        public bool isBillboardShadowCasting = false;

        public bool billboardFaceCamPos = false;

        public float normalStrength = 1.0f;

        [NonSerialized]
        public bool isBeingDeleted = false;
    }

    public enum BillboardQuality
    {
        Low = 0,
        Mid = 1,
        High = 2,
        VeryHigh = 3
    }
}
