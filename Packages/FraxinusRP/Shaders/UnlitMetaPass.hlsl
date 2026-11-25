#ifndef ULTRA_UNLIT_META_PASS_INCLUDED
#define ULTRA_UNLIT_META_PASS_INCLUDED

#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/UltraMetaPass.hlsl"

half4 UniversalFragmentMetaUnlit(Varyings input) : SV_Target
{
    MetaInput metaInput = (MetaInput)0;
    metaInput.Albedo = _BaseColor.rgb * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb;

    return UltraFragmentMeta(input, metaInput);
}
#endif
