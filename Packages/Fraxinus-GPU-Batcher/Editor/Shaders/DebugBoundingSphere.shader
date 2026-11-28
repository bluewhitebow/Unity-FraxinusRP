// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

Shader "Hidden/Fraxinus/GPU Batcher/Debug Bounding Sphere"
{
    Properties
    {
        _DebugOpacity("Opacity", Range(0, 1)) = 0.5
    }

    SubShader
    {
        PackageRequirements { "com.bluewhitebow.fraxinus-rp": "0.1.0" }
        Tags
        {
            "RenderPipeline" = "UltraPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "Debug Bounding Sphere"
            Tags { "LightMode" = "UltraForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DebugSphereVertex
            #pragma fragment DebugSphereFragment
            #include_with_pragmas "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/AreaLightKeywords.hlsl"
            #pragma multi_compile_instancing
            #pragma instancing_options nolightprobe nolightmap
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fog
            #include_with_pragmas "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/ProbeVolumeVariants.hlsl"

            // The reference graph is transparent Lit, with shadow reception disabled.
            #define _SURFACE_TYPE_TRANSPARENT 1
            #define _RECEIVE_SHADOWS_OFF 1
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/LODCrossFade.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            #include "DrawBoundsDebug.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _DebugOpacity;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float3 color : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half3 vertexSH : TEXCOORD3;
                float4 probeOcclusion : TEXCOORD4;
                half3 vertexLighting : TEXCOORD5;
            };

            Varyings DebugSphereVertex(Attributes input)
            {
                InitIndirectDrawArgs(0);
                uint instanceID = GetIndirectInstanceID(input.instanceID);
                float3 positionWS;
                float lod;
                GPUBatcherDebugSphere_float(input.positionOS, instanceID, positionWS, lod);

                Varyings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                // RenderMeshIndirect supplies identity object-to-world for these
                // generated spheres; the reference graph keeps the mesh normals.
                output.normalWS = normalize(input.normalOS);
                output.vertexSH = SampleProbeSHVertex(GetAbsolutePositionWS(positionWS), output.normalWS,
                    GetWorldSpaceNormalizeViewDir(positionWS), output.probeOcclusion);
                output.vertexLighting = VertexLighting(positionWS, output.normalWS);
                GetLODColor_float(lod, output.color);
                return output;
            }

            half4 DebugSphereFragment(Varyings input) : SV_Target
            {
#ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(input.positionCS);
#endif
                InputData lighting = (InputData)0;
                lighting.positionWS = input.positionWS;
                lighting.positionCS = input.positionCS;
                lighting.normalWS = NormalizeNormalPerPixel(input.normalWS);
                lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1), 0);
                lighting.vertexLighting = input.vertexLighting;
                lighting.shadowMask = half4(1, 1, 1, 1);
#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
                lighting.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(input.positionWS),
                    lighting.normalWS, lighting.viewDirectionWS, input.positionCS.xy,
                    input.probeOcclusion, lighting.shadowMask);
#else
                lighting.bakedGI = SampleSHPixel(input.vertexSH, lighting.normalWS);
#endif
                // Preserve the connected graph values: LOD color, alpha 0.5,
                // metallic/smoothness/emission zero, and ambient occlusion one.
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = input.color;
                surface.normalTS = half3(0, 0, 1);
                surface.occlusion = 1;
                surface.alpha = _DebugOpacity;
                half4 color = UniversalFragmentPBR(lighting, surface);
                color.rgb = MixFog(color.rgb, lighting.fogCoord);
                return color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
