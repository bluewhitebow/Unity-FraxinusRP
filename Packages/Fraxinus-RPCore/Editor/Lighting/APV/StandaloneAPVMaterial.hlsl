#ifndef STANDALONE_APV_MATERIAL_INCLUDED
#define STANDALONE_APV_MATERIAL_INCLUDED

#include "Packages/com.fraxinus.rendering.light-transport/Runtime/UnifiedRayTracing/FetchGeometry.hlsl"
#include "Packages/com.fraxinus.rendering.light-transport/Runtime/UnifiedRayTracing/TraceRay.hlsl"

struct APVBakeMaterial
{
    float4 albedo, emission;
    float4 baseST, emissionST;
    float4 baseMap, emissionMap;
    float4 surface;
};

StructuredBuffer<APVBakeMaterial> _APVMaterials;
Texture2DArray<float4> _APVMaterialTextures;
int _APVHasAlphaClipping;

int APVWrapTexel(int coordinate, int size, int mode)
{
    if (mode == 1) return clamp(coordinate, 0, size - 1); // Clamp
    if (mode == 3) return min(coordinate < 0 ? -1 - coordinate : coordinate, size - 1); // MirrorOnce
    int period = mode == 2 ? size * 2 : size;
    uint magnitude = (uint)(coordinate < 0 ? -1 - coordinate : coordinate);
    int wrapped = (int)(magnitude % (uint)period);
    if (coordinate < 0) wrapped = period - 1 - wrapped;
    return mode == 2 ? min(wrapped, period - 1 - wrapped) : wrapped;
}

float4 APVLoadTexel(int2 pixel, int2 size, float4 map)
{
    return _APVMaterialTextures.Load(int4(APVWrapTexel(pixel.x, size.x, (int)map.y),
        APVWrapTexel(pixel.y, size.y, (int)map.z), (int)map.x, 0));
}

float4 APVSampleTexture(float4 map, float2 uv)
{
    if (map.x < 0) return 1;
    uint width, height, slices;
    _APVMaterialTextures.GetDimensions(width, height, slices);
    int2 size = int2(width, height);
    if (map.w == 0) return APVLoadTexel((int2)floor(uv * size), size, map);
    float2 pixel = uv * size - 0.5;
    int2 low = (int2)floor(pixel);
    float2 blend = frac(pixel);
    return lerp(lerp(APVLoadTexel(low, size, map), APVLoadTexel(low + int2(1, 0), size, map), blend.x),
        lerp(APVLoadTexel(low + int2(0, 1), size, map), APVLoadTexel(low + 1, size, map), blend.x), blend.y);
}

float4 APVSampleAlbedo(APVBakeMaterial material, float2 uv)
{
    float4 color = APVSampleTexture(material.baseMap, uv * material.baseST.xy + material.baseST.zw) * material.albedo;
    // Native LightTransport extracts albedo through the material Meta pass
    // with unity_MaxOutputValue = 0.97 (and AlbedoBoost = 1). Match that
    // radiometric clamp before using the value for diffuse transport.
    color.rgb = min(color.rgb, 0.97f);
    if (material.surface.y != 0) color.a = material.albedo.a;
    return color;
}

float3 APVSampleEmission(APVBakeMaterial material, float2 uv)
{
    return APVSampleTexture(material.emissionMap, uv * material.emissionST.xy + material.emissionST.zw).rgb * material.emission.rgb;
}

UnifiedRT::Hit TraceAPVClosestHit(UnifiedRT::DispatchInfo dispatchInfo,
    UnifiedRT::RayTracingAccelStruct accel, uint mask, UnifiedRT::Ray ray, uint flags)
{
    // A single initialized return avoids FXC's false uninitialized-value
    // warning when it inlines the tracing loop across an early return.
    UnifiedRT::Hit result = UnifiedRT::Hit::Invalid();
    if (_APVHasAlphaClipping == 0)
    {
        result = UnifiedRT::TraceRayClosestHit(dispatchInfo, accel, mask, ray, flags);
    }
    else
    {
        [loop]
        while (ray.tMin < ray.tMax)
        {
            UnifiedRT::Hit hit = UnifiedRT::TraceRayClosestHit(dispatchInfo, accel, mask, ray, flags);
            if (!hit.IsValid()) break;
            result = hit;
            APVBakeMaterial material = _APVMaterials[UnifiedRT::GetInstance(hit.instanceID).userMaterialID];
            if (material.surface.x < 0) break;
            float2 uv = UnifiedRT::FetchHitGeomAttributes(hit, UnifiedRT::kGeomAttribTexCoord0).uv0;
            if (APVSampleAlbedo(material, uv).a >= material.surface.x) break;
            result = UnifiedRT::Hit::Invalid();
            // Preserve the origin and tMax (especially for point-light shadow rays).
            // Advance by at least one representable float, with no fixed layer limit.
            ray.tMin = max(asfloat(asuint(max(ray.tMin, hit.hitDistance)) + 1u), hit.hitDistance + 0.00001);
        }
    }
    return result;
}

bool TraceAPVAnyHit(UnifiedRT::DispatchInfo dispatchInfo,
    UnifiedRT::RayTracingAccelStruct accel, uint mask, UnifiedRT::Ray ray, uint flags)
{
    bool result = false;
    if (_APVHasAlphaClipping == 0)
    {
        result = UnifiedRT::TraceRayAnyHit(dispatchInfo, accel, mask, ray, flags);
    }
    else
    {
        UnifiedRT::Hit hit = TraceAPVClosestHit(dispatchInfo, accel, mask, ray, flags);
        result = hit.IsValid();
    }
    return result;
}

#endif
