#ifndef FRAXINUS_GRAPH_LIT_INSTANCING_INCLUDED
#define FRAXINUS_GRAPH_LIT_INSTANCING_INCLUDED

// MaterialPropertyBlock values for a single indexed command in the active camera's render pass.
ByteAddressBuffer _FraxinusGraphInstanceData;
ByteAddressBuffer _FraxinusGraphVisibility;
uint _FraxinusGraphVisibleOffset;
uint _FraxinusGraphObjectToWorld;
uint _FraxinusGraphWorldToObject;
uint _FraxinusGraphPreviousObjectToWorld;
uint _FraxinusGraphDrawFlags;
uint _FraxinusGraphLayers;

static uint fraxinusGraphInstance;
static float4 fraxinusGraphLodFade;

void FraxinusSetupGraphInstance()
{
    uint visible = _FraxinusGraphVisibility.Load((_FraxinusGraphVisibleOffset + unity_InstanceID) * 4u);
    bool packed = (_FraxinusGraphDrawFlags & 32u) != 0;
    fraxinusGraphInstance = packed ? (visible & 0x00ffffffu) : visible;
    fraxinusGraphLodFade = packed ? max(int(visible) >> 24, -127) * (1.0 / 127.0) : 0.0;
}

float4x4 FraxinusGraphMatrix(uint metadata)
{
    // GPUBatcher stores the columns of each affine matrix as 12 consecutive floats.
    uint address = (metadata & 0x7fffffffu) + fraxinusGraphInstance * 48u;
    float4 a = asfloat(_FraxinusGraphInstanceData.Load4(address));
    float4 b = asfloat(_FraxinusGraphInstanceData.Load4(address + 16u));
    float4 c = asfloat(_FraxinusGraphInstanceData.Load4(address + 32u));
    return float4x4(a.x, a.w, b.z, c.y,
                    a.y, b.x, b.w, c.z,
                    a.z, b.y, c.x, c.w,
                    0.0, 0.0, 0.0, 1.0);
}

#undef UNITY_MATRIX_M
#undef UNITY_MATRIX_I_M
#undef UNITY_PREV_MATRIX_M
#undef unity_LODFade
#undef unity_RenderingLayer
#undef unity_WorldTransformParams
#define UNITY_MATRIX_M FraxinusGraphMatrix(_FraxinusGraphObjectToWorld)
#define UNITY_MATRIX_I_M FraxinusGraphMatrix(_FraxinusGraphWorldToObject)
#define UNITY_PREV_MATRIX_M FraxinusGraphMatrix(_FraxinusGraphPreviousObjectToWorld)
#define unity_LODFade fraxinusGraphLodFade
#define unity_RenderingLayer float4(asfloat(_FraxinusGraphLayers), 0.0, 0.0, 0.0)
#define unity_WorldTransformParams float4(0.0, 0.0, 0.0, (_FraxinusGraphDrawFlags & 1u) ? -1.0 : 1.0)

#endif
