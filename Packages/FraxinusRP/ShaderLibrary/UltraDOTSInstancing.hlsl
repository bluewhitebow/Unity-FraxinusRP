#ifndef ULTRA_DOTS_INSTANCING_INCLUDED
#define ULTRA_DOTS_INSTANCING_INCLUDED

#ifdef UNITY_DOTS_INSTANCING_ENABLED

#undef unity_ObjectToWorld
#undef unity_WorldToObject
#undef unity_MatrixPreviousM
#undef unity_MatrixPreviousMI
// TODO: This might not work correctly in all cases, double check!
UNITY_DOTS_INSTANCING_START(BuiltinPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_ObjectToWorld)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_WorldToObject)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float4,   unity_SpecCube0_HDR)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float4,   unity_LightmapST)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float4,   unity_LightmapIndex)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float4,   unity_DynamicLightmapST)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_MatrixPreviousM)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_MatrixPreviousMI)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(SH,       unity_SHCoefficients)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(uint2,    unity_EntityId)
#if UNITY_VERSION < 600000 && defined(SHADER_API_VULKAN) && !defined(UNITY_DOTS_INSTANCING_UNIFORM_BUFFER)
    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(uint, _FraxinusVkVisibilityByteBase)
#endif
UNITY_DOTS_INSTANCING_END(BuiltinPropertyMetadata)

#if UNITY_VERSION < 600000 && defined(SHADER_API_VULKAN) && !defined(UNITY_DOTS_INSTANCING_UNIFORM_BUFFER)
#if defined(FRAXINUS_BRG_NATIVE_INDIRECT)
// This material variant is selected by the extended 2022 engine for its indexed draws.
// Use the standard Unity 6 header and the caller's original visibility buffer.
#undef UNITY_SETUP_DOTS_VISIBLE_INSTANCING_DATA
#define UNITY_SETUP_DOTS_VISIBLE_INSTANCING_DATA() SetupDOTSVisibleInstancingData()
#else
void SetupFraxinusVulkanVisibleInstances()
{
    // The normal BRG batch leaves this metadata at the Unity default of zero.
    uint visibilityBase = UNITY_DOTS_INSTANCED_METADATA_NAME(uint, _FraxinusVkVisibilityByteBase);
    [branch] if (visibilityBase == 0)
    {
        uint4 visible = unity_DOTSVisibleInstances[unity_InstanceID].VisibleData;
        unity_SampledDOTSIndirectVisibleIndex = unity_InstanceID;
        unity_SampledDOTSInstanceIndex = visible.x;
        unity_SampledLODCrossfade = asint(visible.y); // native Unity 2022 direct layout
    }
    else
    {
        // One direct placeholder supplies the token in entry 0 for the entire indirect draw.
        // The raw buffer contains the batch's regular instance data followed by this view's GPU list.
        uint token = unity_DOTSVisibleInstances[0].VisibleData.x;
        uint listIndex = (token & 0x007fffffu) + unity_InstanceID;
        uint packedID = unity_DOTSInstanceData.Load(visibilityBase + (listIndex << 2));
        unity_SampledDOTSIndirectVisibleIndex = listIndex;
        bool usePackedFade = (token & 0x00800000u) != 0;
        unity_SampledDOTSInstanceIndex = usePackedFade ? (packedID & 0x00ffffffu) : packedID;
        unity_SampledLODCrossfade = usePackedFade ? (asint(packedID) >> 24) : 0;
    }
    SetupDOTSInstanceSelectMasks();
}
#undef UNITY_SETUP_DOTS_VISIBLE_INSTANCING_DATA
#define UNITY_SETUP_DOTS_VISIBLE_INSTANCING_DATA() SetupFraxinusVulkanVisibleInstances()
#endif
#endif

#define unity_LODFade               LoadDOTSInstancedData_LODFade()
#define unity_SpecCube0_HDR         UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_CUSTOM_DEFAULT(float4, unity_SpecCube0_HDR, unity_DOTS_SpecCube0_HDR)
#define unity_LightmapST            UNITY_ACCESS_DOTS_INSTANCED_PROP(float4,   unity_LightmapST)
#define unity_LightmapIndex         UNITY_ACCESS_DOTS_INSTANCED_PROP(float4,   unity_LightmapIndex)
#define unity_DynamicLightmapST     UNITY_ACCESS_DOTS_INSTANCED_PROP(float4,   unity_DynamicLightmapST)
#define unity_SHAr                  LoadDOTSInstancedData_SHAr()
#define unity_SHAg                  LoadDOTSInstancedData_SHAg()
#define unity_SHAb                  LoadDOTSInstancedData_SHAb()
#define unity_SHBr                  LoadDOTSInstancedData_SHBr()
#define unity_SHBg                  LoadDOTSInstancedData_SHBg()
#define unity_SHBb                  LoadDOTSInstancedData_SHBb()
#define unity_SHC                   LoadDOTSInstancedData_SHC()
#define unity_ProbesOcclusion       LoadDOTSInstancedData_ProbesOcclusion()
#define unity_LightData             LoadDOTSInstancedData_LightData()
#define unity_WorldTransformParams  LoadDOTSInstancedData_WorldTransformParams()
#define unity_RenderingLayer        LoadDOTSInstancedData_RenderingLayer()
#define unity_MotionVectorsParams   LoadDOTSInstancedData_MotionVectorsParams() 
#define unity_RendererBounds_Min    LoadDOTSInstancedData_RendererBounds_Min()
#define unity_RendererBounds_Max    LoadDOTSInstancedData_RendererBounds_Max()

#define UNITY_SETUP_DOTS_SH_COEFFS  SetupDOTSSHCoeffs(UNITY_DOTS_INSTANCED_METADATA_NAME(SH, unity_SHCoefficients))
#define UNITY_SETUP_DOTS_RENDER_BOUNDS  SetupDOTSRendererBounds(UNITY_DOTS_MATRIX_M)

// Not supported by BatchRendererGroup. Just define them as constants.
// ------------------------------------------------------------------------------
static const float2x4 unity_LightIndices = float2x4(0,0,0,0, 0,0,0,0);

static const float4 unity_SpecCube0_BoxMax = float4(1,1,1,1);
static const float4 unity_SpecCube0_BoxMin = float4(0,0,0,0);
static const float4 unity_SpecCube0_ProbePosition = float4(0,0,0,0);

static const float4 unity_SpecCube1_BoxMax = float4(1,1,1,1);
static const float4 unity_SpecCube1_BoxMin = float4(0,0,0,0);
static const float4 unity_SpecCube1_ProbePosition = float4(0,0,0,0);
static const float4 unity_SpecCube1_HDR = float4(0,0,0,0);

// Set up by BRG picking/selection code
int unity_SubmeshIndex;
#define unity_SelectionID UNITY_ACCESS_DOTS_INSTANCED_SELECTION_VALUE(unity_EntityId, unity_SubmeshIndex, _SelectionID)

#else

#define unity_SelectionID _SelectionID
#define UNITY_SETUP_DOTS_RENDER_BOUNDS

#endif

#endif
