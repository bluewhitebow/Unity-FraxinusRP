#ifndef APRT_SAMPLING_INCLUDED
#define APRT_SAMPLING_INCLUDED

float _EnableAPRT;
float _APRTGIIntensity;
float _APRTAddAmbientLighting;
int _APRTProbeCount;
int _APRTLightingModel;
int _APRTDebugMode;

// GLES and low-target material variants retain their normal URP GI fallback.
#if SHADER_TARGET >= 45 && !defined(SHADER_API_GLES) && !defined(SHADER_API_GLES3)
StructuredBuffer<float4> _APRTRuntimeSH;
StructuredBuffer<float4> _APRTSkyIrradianceSH;
StructuredBuffer<float4> _APRTBakedAxis;
StructuredBuffer<float3> _APRTProbePositions;
StructuredBuffer<float> _APRTProbeValidity;
StructuredBuffer<float4> _APRTVisibilityMoments;
StructuredBuffer<int4> _APRTBricks;
StructuredBuffer<int> _APRTBrickProbeIndices;
StructuredBuffer<int> _APRTBrickHash;
StructuredBuffer<int> _APRTGridProbeIndices;
StructuredBuffer<float4x4> _APRTWorldToDomains;
int _APRTBrickCount;
int _APRTBrickHashMask;
int _APRTBrickHashMaxSteps;
int _APRTMaxSubdivision;
int _APRTDomainCount;
int _APRTRegularGrid;
float4 _APRTGridDimensions;
float4x4 _APRTWorldToGrid;
float4x4 _APRTWorldToSamplingGrid;
float4x4 _APRTSamplingGridToWorld;
float3 _APRTGridSamplingSpacing;
float3 _APRTProbeOffset;
float _APRTMinBrickSize;
float _APRTNormalBias;
float _APRTVisibilityBias;
float _APRTValidityThreshold;
float _APRTLeakReduction;
float _APRTFloorBoost;
float _APRTFloorPower;

// CPU layout: APRTProbeReferenceVolume.SamplingAtlas.SamplingAtlasDescriptor.
struct APRTSamplingContext
{
    int4 probes; // probe offset/count, brick offset/count
    int4 lookup; // brick-probe offset, hash offset/mask/max steps
    int4 layout; // grid-index offset, domain offset/count, maximum subdivision
    int4 flags; // reference grid, lighting model, valid camera output, bounded atlas domain
    float4 gridDimensions, samplingSpacing, probeOffset;
    float4 receiver; // GI intensity, ambient weight, normal bias, visibility bias
    float4 response; // validity threshold, leak reduction, floor boost/power
    float4 boundsMin, boundsMax;
    float4x4 worldToGrid, worldToSamplingGrid, samplingGridToWorld;
};

#if defined(PROBE_VOLUMES_APRT) || defined(APRT_SAMPLING_ATLAS)
StructuredBuffer<APRTSamplingContext> _APRTSamplingContexts;
int _APRTSamplingSetCount;
#endif

APRTSamplingContext APRTLegacySamplingContext()
{
    APRTSamplingContext context = (APRTSamplingContext)0;
    context.probes = int4(0, _APRTProbeCount, 0, _APRTBrickCount);
    context.lookup = int4(0, 0, _APRTBrickHashMask, _APRTBrickHashMaxSteps);
    context.layout = int4(0, 0, _APRTDomainCount, _APRTMaxSubdivision);
    context.flags = int4(_APRTRegularGrid, _APRTLightingModel, 1, 0);
    context.gridDimensions = _APRTGridDimensions;
    context.samplingSpacing = float4(_APRTGridSamplingSpacing, 0);
    context.probeOffset = float4(_APRTProbeOffset, _APRTMinBrickSize);
    context.receiver = float4(_APRTGIIntensity, _APRTAddAmbientLighting, _APRTNormalBias, _APRTVisibilityBias);
    context.response = float4(_APRTValidityThreshold, _APRTLeakReduction, _APRTFloorBoost, _APRTFloorPower);
    context.worldToGrid = _APRTWorldToGrid;
    context.worldToSamplingGrid = _APRTWorldToSamplingGrid;
    context.samplingGridToWorld = _APRTSamplingGridToWorld;
    return context;
}

uint APRTBrickHash(int3 position, int level)
{
    uint hash = (uint)position.x * 73856093u ^ (uint)position.y * 19349663u ^ (uint)position.z * 83492791u ^ (uint)level * 2654435761u;
    hash ^= hash >> 16;
    hash *= 2246822519u;
    return hash ^ (hash >> 13);
}

int APRTFindBrickContext(APRTSamplingContext context, int3 position, int subdivision)
{
    uint mask = (uint)context.lookup.z;
    uint slot = APRTBrickHash(position, subdivision) & mask;
    [loop] for (int step = 0; step < context.lookup.w; ++step)
    {
        int index = _APRTBrickHash[context.lookup.y + slot] - 1;
        if (index < 0) return -1;
        int4 brick = _APRTBricks[context.probes.z + index];
        if (all(brick.xyz == position) && brick.w == subdivision) return index;
        slot = (slot + 1u) & mask;
    }
    return -1;
}

bool APRTInsideDomainsContext(APRTSamplingContext context, float3 positionWS)
{
    if (context.layout.z == 0) return true;
    [loop] for (int domain = 0; domain < context.layout.z; ++domain)
    {
        float3 local = mul(_APRTWorldToDomains[context.layout.y + domain], float4(positionWS, 1)).xyz;
        if (all(abs(local) <= 0.50001)) return true;
    }
    return false;
}

float2 APRTSampleMomentsContext(APRTSamplingContext context, uint probeIndex, float3 direction)
{
    float3 weight = abs(direction);
    weight /= max(weight.x + weight.y + weight.z, 1e-6);
    uint offset = (probeIndex + context.probes.x) * 6;
    return _APRTVisibilityMoments[offset + (direction.x < 0 ? 1 : 0)].xy * weight.x
         + _APRTVisibilityMoments[offset + (direction.y < 0 ? 3 : 2)].xy * weight.y
         + _APRTVisibilityMoments[offset + (direction.z < 0 ? 5 : 4)].xy * weight.z;
}

float APRTReceiverWeightContext(APRTSamplingContext context, uint index, float3 probePositionWS, float3 positionWS, float3 normalWS)
{
    float validity = _APRTProbeValidity[index + context.probes.x];
    bool parity = context.flags.y == 1;
    if (parity && context.response.y <= 0.5) validity = 1;
    else if (validity < context.response.x || validity <= 0) return 0;
    float3 toProbe = probePositionWS - positionWS;
    float distanceSquared = dot(toProbe, toProbe);
    if (distanceSquared < 1e-8) return validity * (parity ? 0.5 : 1.0);
    float distance = sqrt(distanceSquared);
    float3 direction = toProbe / distance;
    // Wrapped normal weighting favors probes on the receiver's visible side.
    float normalWeight = parity ? saturate(dot(normalWS, direction) * 4.0 + 0.5) : saturate(dot(normalWS, direction) * 0.5 + 0.5);
    if (!parity) normalWeight = normalWeight * normalWeight + 0.05;
    float visibility = 1;
    if (context.response.y > 0.5)
    {
        float2 moments = APRTSampleMomentsContext(context, index, -direction);
        float biasedDistance = max(0, distance - context.receiver.w);
        float difference = max(0, biasedDistance - moments.x);
        float variance = max(moments.y - moments.x * moments.x, 1e-4);
        visibility = variance / (variance + difference * difference);
        visibility = visibility * visibility * visibility;
    }
    return validity * normalWeight * visibility;
}

float3 APRTEvaluateProbeContext(APRTSamplingContext context, uint index, float3 normalWS)
{
    uint offset = (index + context.probes.x) * 4;
    float directionalScale = context.flags.y == 1 ? 2.0 : 1.0;
    return _APRTRuntimeSH[offset].rgb
        + directionalScale * (
        normalWS.x * _APRTRuntimeSH[offset + 1].rgb
        + normalWS.y * _APRTRuntimeSH[offset + 2].rgb
        + normalWS.z * _APRTRuntimeSH[offset + 3].rgb);
}

// Debug views share the topology lookup, but inspect the unbiased geometric domain.
// Normal GI retains the APRTV boundary clamp and its reference sampling offsets.
bool APRTFindSamplingCellContext(APRTSamplingContext context, float3 positionWS, float3 normalWS, bool rawDebug,
    out float3 coordinate, out int3 lower, out int3 dimensions, out int brickIndex)
{
    coordinate = 0;
    lower = dimensions = 0;
    brickIndex = -1;
    bool parity = context.flags.y == 1;
    if (context.flags.w != 0)
    {
        if (any(positionWS < context.boundsMin.xyz - 0.0001) || any(positionWS > context.boundsMax.xyz + 0.0001)) return false;
        if (context.flags.x != 0)
        {
            float3 bounded = mul(context.worldToSamplingGrid, float4(positionWS, 1)).xyz;
            if (any(bounded < -0.50001) || any(bounded > context.gridDimensions.xyz - 0.49999)) return false;
        }
    }
    if ((rawDebug || !parity || context.flags.x == 0) && !APRTInsideDomainsContext(context, positionWS)) return false;
    if (context.flags.x != 0)
    {
        dimensions = (int3)context.gridDimensions.xyz;
        float3 last = (float3)(dimensions - 1);
        coordinate = mul(context.worldToGrid, float4(positionWS, 1)).xyz;
        if (parity)
        {
            coordinate = mul(context.worldToSamplingGrid, float4(positionWS, 1)).xyz;
            if (!rawDebug)
            {
                coordinate += mul((float3x3)context.worldToSamplingGrid, normalWS) * context.samplingSpacing.xyz * 0.1;
                coordinate *= last / max(last + 0.0001 / max(context.samplingSpacing.xyz, 1e-6), 1e-6);
            }
        }
        if ((rawDebug || !parity) && (any(coordinate < -0.0001) || any(coordinate > last + 0.0001))) return false;
        coordinate = clamp(coordinate, 0, last);
        lower = min((int3)floor(coordinate), max(dimensions - 2, 0));
    }
    else
    {
        float3 gridPosition = (positionWS - context.probeOffset.xyz) / context.probeOffset.w;
        float brickSize = 1;
        [loop] for (int level = 0; level <= context.layout.w; ++level)
        {
            int3 brickPosition = (int3)floor(gridPosition / brickSize) * (int)brickSize;
            brickIndex = APRTFindBrickContext(context, brickPosition, level);
            // The outside face belongs to its preceding brick even when floor selects an empty neighbor.
            // Only attempt adjacent keys at an exact grid boundary, keeping interior lookups constant.
            if (brickIndex < 0)
            {
                bool3 boundary = abs(gridPosition / brickSize - round(gridPosition / brickSize)) < 0.00001;
                [loop] for (uint neighbor = 1; neighbor < 8 && brickIndex < 0; ++neighbor)
                {
                    int3 delta = int3(neighbor & 1u, (neighbor >> 1u) & 1u, (neighbor >> 2u) & 1u);
                    if (any((delta != 0) & !boundary)) continue;
                    int3 previous = brickPosition - delta * (int)brickSize;
                    brickIndex = APRTFindBrickContext(context, previous, level);
                    if (brickIndex >= 0) brickPosition = previous;
                }
            }
            if (brickIndex >= 0)
            {
                coordinate = (gridPosition - brickPosition) * (3.0 / brickSize);
                if (parity && !rawDebug) coordinate = clamp(coordinate + normalWS * 0.1, 0, 3);
                break;
            }
            brickSize *= 3;
        }
        if (brickIndex < 0) return false;
        dimensions = int3(4, 4, 4);
        coordinate = clamp(coordinate, 0, 3);
        lower = min((int3)floor(coordinate), int3(2, 2, 2));
    }
    return true;
}

int APRTCornerProbeIndexContext(APRTSamplingContext context, int brickIndex, int3 gridIndex, int3 dimensions)
{
    int index = -1;
    [branch] if (context.flags.x != 0)
        index = _APRTGridProbeIndices[context.layout.x + gridIndex.x + dimensions.x * (gridIndex.y + dimensions.y * gridIndex.z)];
    else
        index = _APRTBrickProbeIndices[context.lookup.x + brickIndex * 64 + gridIndex.x + 4 * gridIndex.y + 16 * gridIndex.z];
    return index;
}

bool APRTSampleGIContext(APRTSamplingContext context, float3 positionWS, float3 normalWS, out float3 irradianceOverPi)
{
    irradianceOverPi = 0;
    bool parity = context.flags.y == 1;
    if (_EnableAPRT <= 0.5 || context.flags.z == 0 || context.probes.y <= 0 || context.receiver.x <= 0) return false;
    normalWS *= rsqrt(max(dot(normalWS, normalWS), 1e-8));
    float3 receiverWS = positionWS + normalWS * context.receiver.z;
    float3 coordinate;
    int3 lower, dimensions;
    int brickIndex;
    if (!APRTFindSamplingCellContext(context, positionWS, normalWS, false, coordinate, lower, dimensions, brickIndex)) return false;
    float3 fraction = saturate(coordinate - lower);
    float totalWeight = 0;
    float3 skyIrradiance = 0;
    [unroll] for (uint corner = 0; corner < 8; ++corner)
    {
        int3 delta = int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u);
        int3 gridIndex = min(lower + delta, dimensions - 1);
        int index = APRTCornerProbeIndexContext(context, brickIndex, gridIndex, dimensions);
        // A streamed grid cell requires its complete interpolation footprint.
        // Partial corners must not extend resident lighting into unloaded cells.
        if (index < 0 || index >= context.probes.y) { irradianceOverPi = 0; return false; }
        float3 weights = lerp(1 - fraction, fraction, (float3)delta);
        float3 probePositionWS = parity && context.flags.x != 0
            ? mul(context.samplingGridToWorld, float4(gridIndex, 1)).xyz
            : _APRTProbePositions[index + context.probes.x];
        float weight = weights.x * weights.y * weights.z * APRTReceiverWeightContext(context, (uint)index, probePositionWS, parity ? positionWS : receiverWS, normalWS);
        float3 probeGI = APRTEvaluateProbeContext(context, (uint)index, normalWS);
        irradianceOverPi += (parity ? probeGI : max(0, probeGI)) * weight;
        uint skyOffset = ((uint)index + context.probes.x) * 4;
        skyIrradiance += (_APRTSkyIrradianceSH[skyOffset].rgb
            + normalWS.x * _APRTSkyIrradianceSH[skyOffset + 1].rgb
            + normalWS.y * _APRTSkyIrradianceSH[skyOffset + 2].rgb
            + normalWS.z * _APRTSkyIrradianceSH[skyOffset + 3].rgb) * weight;
        totalWeight += weight;
    }
    if (totalWeight <= 1e-5) { irradianceOverPi = 0; return false; }
    if (parity)
    {
        irradianceOverPi = max(0, irradianceOverPi / (totalWeight + 1e-5));
        irradianceOverPi *= 1 + pow(saturate(normalWS.y), context.response.w) * context.response.z;
        irradianceOverPi = (irradianceOverPi + max(0, skyIrradiance / (totalWeight + 1e-5))) * context.receiver.x;
    }
    else
    {
        irradianceOverPi = (irradianceOverPi + max(0, skyIrradiance)) * (context.receiver.x / totalWeight);
    }
    return true;
}

// Raw transport views deliberately bypass relighting, receiver material response,
// validity rejection, normal/visibility weights, floor uplift and GI gain.
bool APRTSampleDebugContext(APRTSamplingContext context, float3 positionWS, float3 normalWS, out float3 color)
{
    color = 0;
    if (_APRTDebugMode == 0) return false;
    color = float3(1, 0, 1); // No resident data or no geometric coverage.
    if (_EnableAPRT <= 0.5 || context.probes.y <= 0) return true;
    if (_APRTDebugMode == 5)
    {
        // GI Only keeps the actual receiver response, including its normal weighting.
        // Zero GI gain is black while unavailable resident data remains magenta.
        float3 response;
        if (context.receiver.x <= 0) color = 0;
        else if (APRTSampleGIContext(context, positionWS, normalWS, response)) color = response;
        return true;
    }
    float3 coordinate;
    int3 lower, dimensions;
    int brickIndex;
    if (!APRTFindSamplingCellContext(context, positionWS, normalWS, true, coordinate, lower, dimensions, brickIndex)) return true;
    float3 fraction = saturate(coordinate - lower);
    float4 raw = 0;
    float validity = 0;
    float totalWeight = 0;
    [unroll] for (uint corner = 0; corner < 8; ++corner)
    {
        int3 delta = int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u);
        int3 gridIndex = min(lower + delta, dimensions - 1);
        int index = APRTCornerProbeIndexContext(context, brickIndex, gridIndex, dimensions);
        if (index < 0 || index >= context.probes.y) return true;
        float3 weights = lerp(1 - fraction, fraction, (float3)delta);
        float weight = weights.x * weights.y * weights.z;
        if (_APRTDebugMode == 1 || _APRTDebugMode == 2)
        {
            float4 meanAxis = 0;
            [unroll] for (uint axis = 0; axis < 6; ++axis)
                meanAxis += _APRTBakedAxis[((uint)index + context.probes.x) * 6 + axis] / 6.0;
            raw += meanAxis * weight;
        }
        else if (_APRTDebugMode == 3)
            validity += _APRTProbeValidity[index + context.probes.x] * weight;
        totalWeight += weight;
    }
    if (totalWeight < 1e-5) return true;
    if (_APRTDebugMode == 1) color = raw.rgb / totalWeight;
    else if (_APRTDebugMode == 2) color = (raw.w / totalWeight).xxx;
    else if (_APRTDebugMode == 3)
    {
        validity /= totalWeight;
        float brightness = 0.25 + 0.75 * saturate(validity);
        color = validity <= 0 || validity < context.response.x
            ? float3(brightness, 0, 0) : float3(0, brightness, 0);
    }
    else if (_APRTDebugMode == 4) color = float3(0, 1, 0);
    return true;
}
// Overlapping valid domains contribute equally. Missing or unreadable cells do
// not darken another Baking Set's result and retain ordinary GI when none sample.
bool APRTSampleGI(float3 positionWS, float3 normalWS, out float3 irradianceOverPi)
{
    irradianceOverPi = 0;
#if defined(PROBE_VOLUMES_APRT) || defined(APRT_SAMPLING_ATLAS)
    if (_APRTSamplingSetCount > 0)
    {
        float samples = 0;
        [loop] for (int setIndex = 0; setIndex < _APRTSamplingSetCount; ++setIndex)
        {
            float3 value;
            if (APRTSampleGIContext(_APRTSamplingContexts[setIndex], positionWS, normalWS, value))
            { irradianceOverPi += value; samples += 1; }
        }
        if (samples <= 0) return false;
        irradianceOverPi /= samples;
        return true;
    }
#endif
    return APRTSampleGIContext(APRTLegacySamplingContext(), positionWS, normalWS, irradianceOverPi);
}

// Separating diffuse GI from APRTV emission handles overlapping sets with
// different lighting models without depending on a single global model flag.
bool APRTSampleDiffuseGI(float3 positionWS, float3 normalWS, out float3 irradianceOverPi, out float ambientWeight)
{
    irradianceOverPi = 0; ambientWeight = 1;
#if defined(PROBE_VOLUMES_APRT) || defined(APRT_SAMPLING_ATLAS)
    if (_APRTSamplingSetCount > 0)
    {
        float samples = 0; ambientWeight = 0;
        [loop] for (int setIndex = 0; setIndex < _APRTSamplingSetCount; ++setIndex)
        {
            APRTSamplingContext context = _APRTSamplingContexts[setIndex];
            float3 value;
            if (!APRTSampleGIContext(context, positionWS, normalWS, value)) continue;
            samples += 1;
            if (context.flags.y == 1) ambientWeight += 1;
            else { irradianceOverPi += value; ambientWeight += context.receiver.y; }
        }
        if (samples <= 0) return false;
        irradianceOverPi /= samples; ambientWeight /= samples;
        return true;
    }
#endif
    if (_APRTLightingModel == 1) return false;
    ambientWeight = _APRTAddAmbientLighting;
    return APRTSampleGIContext(APRTLegacySamplingContext(), positionWS, normalWS, irradianceOverPi);
}

bool APRTSampleEmissionGI(float3 positionWS, float3 normalWS, out float3 response)
{
    response = 0;
#if defined(PROBE_VOLUMES_APRT) || defined(APRT_SAMPLING_ATLAS)
    if (_APRTSamplingSetCount > 0)
    {
        float samples = 0;
        [loop] for (int setIndex = 0; setIndex < _APRTSamplingSetCount; ++setIndex)
        {
            APRTSamplingContext context = _APRTSamplingContexts[setIndex];
            float3 value;
            if (!APRTSampleGIContext(context, positionWS, normalWS, value)) continue;
            samples += 1;
            if (context.flags.y == 1) response += value;
        }
        if (samples <= 0) return false;
        response /= samples;
        return true;
    }
#endif
    return _APRTLightingModel == 1 && APRTSampleGIContext(APRTLegacySamplingContext(), positionWS, normalWS, response);
}

bool APRTSampleDebug(float3 positionWS, float3 normalWS, out float3 color)
{
#if defined(PROBE_VOLUMES_APRT) || defined(APRT_SAMPLING_ATLAS)
    if (_APRTSamplingSetCount > 0 && _APRTDebugMode != 0)
    {
        color = float3(1, 0, 1);
        float3 total = 0; float samples = 0;
        [loop] for (int setIndex = 0; setIndex < _APRTSamplingSetCount; ++setIndex)
        {
            APRTSamplingContext context = _APRTSamplingContexts[setIndex];
            float3 coordinate; int3 lower, dimensions; int brickIndex;
            if (context.flags.z == 0 || !APRTFindSamplingCellContext(context, positionWS, normalWS, true, coordinate, lower, dimensions, brickIndex)) continue;
            bool available = true;
            [unroll] for (uint corner = 0; corner < 8; ++corner)
            {
                int3 delta = int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u);
                int index = APRTCornerProbeIndexContext(context, brickIndex, min(lower + delta, dimensions - 1), dimensions);
                if (index < 0 || index >= context.probes.y) available = false;
            }
            if (!available) continue;
            float3 value;
            APRTSampleDebugContext(context, positionWS, normalWS, value);
            total += value; samples += 1;
        }
        if (samples > 0) color = total / samples;
        return true;
    }
#endif
    return APRTSampleDebugContext(APRTLegacySamplingContext(), positionWS, normalWS, color);
}

#else
bool APRTSampleGI(float3 positionWS, float3 normalWS, out float3 irradianceOverPi)
{
    irradianceOverPi = 0;
    return false;
}
bool APRTSampleDiffuseGI(float3 positionWS, float3 normalWS, out float3 irradianceOverPi, out float ambientWeight)
{
    irradianceOverPi = 0; ambientWeight = 1;
    return false;
}
bool APRTSampleEmissionGI(float3 positionWS, float3 normalWS, out float3 response)
{
    response = 0;
    return false;
}
bool APRTSampleDebug(float3 positionWS, float3 normalWS, out float3 color)
{
    color = 0;
    return false;
}
#endif

bool APRTSampleReceiverGI(float3 positionWS, float3 normalWS, out float3 irradianceOverPi)
{
    return APRTSampleGI(positionWS, normalWS, irradianceOverPi);
}

#endif
