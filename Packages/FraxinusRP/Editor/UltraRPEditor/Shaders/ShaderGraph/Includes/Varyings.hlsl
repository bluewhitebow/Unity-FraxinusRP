#if (SHADERPASS == SHADERPASS_SHADOWCASTER)
#ifndef HAVE_VFX_MODIFICATION
    float3 _LightDirection;
#endif
    float3 _LightPosition;
#endif

#if defined(FEATURES_GRAPH_VERTEX)
VertexDescription BuildVertexDescription(Attributes input)
{
    VertexDescriptionInputs vertexDescriptionInputs = BuildVertexDescriptionInputs(input);
    VertexDescription vertexDescription = VertexDescriptionFunction(vertexDescriptionInputs);
    return vertexDescription;
}
#endif

#if (SHADERPASS == SHADERPASS_MOTION_VECTORS)
struct MotionVectorPassOutput
{
    float3 positionOS;
    float3 positionWS;
#if defined(FEATURES_GRAPH_VERTEX_MOTION_VECTOR_OUTPUT)
    float3 motionVector;
#endif
};
#endif

Varyings BuildVaryings(Attributes input
#if (SHADERPASS == SHADERPASS_MOTION_VECTORS)
    , out MotionVectorPassOutput motionVectorOutput
#endif
)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

#if defined(FEATURES_GRAPH_VERTEX)
    VertexDescription vertexDescription = BuildVertexDescription(input);
    #if defined(ATTRIBUTES_NEED_NORMAL) && defined(FEATURES_GRAPH_VERTEX_NORMAL_OUTPUT)
        input.normalOS = vertexDescription.Normal;
    #endif
    #if defined(ATTRIBUTES_NEED_TANGENT) && defined(FEATURES_GRAPH_VERTEX_TANGENT_OUTPUT)
        input.tangentOS.xyz = vertexDescription.Tangent.xyz;
    #endif
    input.positionOS = vertexDescription.Position;
#endif

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

#ifdef ATTRIBUTES_NEED_NORMAL
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
#else
    float3 normalWS = float3(0.0, 0.0, 0.0);
#endif

#ifdef ATTRIBUTES_NEED_TANGENT
    float4 tangentWS = float4(TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w * GetOddNegativeScale());
#endif

    float3 positionWS = TransformObjectToWorld(input.positionOS);

#if (SHADERPASS == SHADERPASS_MOTION_VECTORS)
    motionVectorOutput.positionOS = input.positionOS;
    motionVectorOutput.positionWS = positionWS;
    #if defined(FEATURES_GRAPH_VERTEX_MOTION_VECTOR_OUTPUT)
        motionVectorOutput.motionVector = vertexDescription.MotionVector;
    #endif
#endif

#if defined(CUSTOMINTERPOLATOR_VARYPASSTHROUGH_FUNC)
    CustomInterpolatorPassThroughFunc(output, vertexDescription);
#endif

#ifdef VARYINGS_NEED_POSITION_WS
    output.positionWS = positionWS;
#endif

#ifdef VARYINGS_NEED_NORMAL_WS
    output.normalWS = normalWS;
#endif

#ifdef VARYINGS_NEED_TANGENT_WS
    output.tangentWS = tangentWS;
#endif

#if (SHADERPASS == SHADERPASS_SHADOWCASTER)
    #if _CASTING_PUNCTUAL_LIGHT_SHADOW
        float3 lightDirectionWS = normalize(_LightPosition - positionWS);
    #else
        float3 lightDirectionWS = _LightDirection;
    #endif
    output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    output.positionCS = ApplyShadowClamping(output.positionCS);
#elif (SHADERPASS == SHADERPASS_META)
    output.positionCS = UnityMetaVertexPosition(input.positionOS, input.uv1.xy, input.uv2.xy, unity_LightmapST, unity_DynamicLightmapST);
#else
    output.positionCS = TransformWorldToHClip(positionWS);
#endif

#if defined(VARYINGS_NEED_TEXCOORD0)
    output.texCoord0 = input.uv0;
#endif
#if defined(EDITOR_VISUALIZATION) && (SHADERPASS == SHADERPASS_META)
    float2 vizUV = 0;
    float4 lightCoord = 0;
    UnityEditorVizData(input.positionOS, input.uv0.xy, input.uv1.xy, input.uv2.xy, vizUV, lightCoord);
#endif
#if defined(VARYINGS_NEED_TEXCOORD1)
    #if defined(EDITOR_VISUALIZATION) && (SHADERPASS == SHADERPASS_META)
    output.texCoord1 = float4(vizUV, 0, 0);
    #else
    output.texCoord1 = input.uv1;
    #endif
#endif
#if defined(VARYINGS_NEED_TEXCOORD2)
    #if defined(EDITOR_VISUALIZATION) && (SHADERPASS == SHADERPASS_META)
    output.texCoord2 = lightCoord;
    #else
    output.texCoord2 = input.uv2;
    #endif
#endif
#if defined(VARYINGS_NEED_TEXCOORD3)
    output.texCoord3 = input.uv3;
#endif

#if defined(VARYINGS_NEED_COLOR)
    output.color = input.color;
#endif

#if defined(VARYINGS_NEED_STATIC_LIGHTMAP_UV) && defined(LIGHTMAP_ON)
    OUTPUT_LIGHTMAP_UV(input.uv1, unity_LightmapST, output.staticLightmapUV);
#endif
#if defined(VARYINGS_NEED_DYNAMIC_LIGHTMAP_UV) && defined(DYNAMICLIGHTMAP_ON)
    output.dynamicLightmapUV = input.uv2.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif
#if defined(VARYINGS_NEED_SH) && !defined(LIGHTMAP_ON)
    OUTPUT_SH4(positionWS, normalWS, GetWorldSpaceNormalizeViewDir(positionWS), output.sh, output.probeOcclusion);
#endif

#ifdef VARYINGS_NEED_FOG_AND_VERTEX_LIGHT
    half fogFactor = 0;
    #if !defined(_FOG_FRAGMENT)
        fogFactor = ComputeFogFactor(output.positionCS.z);
    #endif
    half3 vertexLight = VertexLighting(positionWS, normalWS);
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
#endif

#if defined(VARYINGS_NEED_SHADOW_COORD) && defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    return output;
}

SurfaceDescription BuildSurfaceDescription(Varyings varyings)
{
    SurfaceDescriptionInputs surfaceDescriptionInputs = BuildSurfaceDescriptionInputs(varyings);
    SurfaceDescription surfaceDescription = SurfaceDescriptionFunction(surfaceDescriptionInputs);
    return surfaceDescription;
}
