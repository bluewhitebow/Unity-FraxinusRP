Shader "Hidden/StandaloneAPV/CopyTexture"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment Fragment
            #pragma target 3.5
            #include "UnityCG.cginc"

            Texture2D<float4> _MainTex;
            float4 _MainTex_TexelSize;
            float _DecodeSRGB;
            float _PointFilter;

            float4 LoadLinear(int2 pixel)
            {
                float4 color = _MainTex.Load(int3(clamp(pixel, 0, (int2)_MainTex_TexelSize.zw - 1), 0));
                if (_DecodeSRGB != 0)
                    color.rgb = float3(
                        color.r <= 0.04045 ? color.r / 12.92 : pow((color.r + 0.055) / 1.055, 2.4),
                        color.g <= 0.04045 ? color.g / 12.92 : pow((color.g + 0.055) / 1.055, 2.4),
                        color.b <= 0.04045 ? color.b / 12.92 : pow((color.b + 0.055) / 1.055, 2.4));
                return color;
            }

            float4 Fragment(v2f_img input) : SV_Target
            {
                if (_PointFilter != 0) return LoadLinear((int2)floor(input.uv * _MainTex_TexelSize.zw));
                // Decode each source texel before filtering in Gamma projects,
                // matching the hardware sRGB sampler used in Linear projects.
                float2 pixel = input.uv * _MainTex_TexelSize.zw - 0.5;
                int2 low = (int2)floor(pixel);
                float2 blend = frac(pixel);
                return lerp(lerp(LoadLinear(low), LoadLinear(low + int2(1, 0)), blend.x),
                    lerp(LoadLinear(low + int2(0, 1)), LoadLinear(low + 1), blend.x), blend.y);
            }
            ENDHLSL
        }
    }
}
