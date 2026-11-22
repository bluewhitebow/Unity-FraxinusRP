// Draw the imported SpeedTree mesh so native current/history wind metadata is retained.
Shader "Hidden/FraxinusGPUBatcher/NativeSpeedTreeWindCapture"
{
    Properties
    {
        _WindQuality ("Wind Quality", Float) = 4
        _WindEnabled ("Wind Enabled", Float) = 1
        [HideInInspector] _FraxinusWindOutputByteOffset ("Output Offset", Integer) = 0
        [HideInInspector] _FraxinusWindVertex0 ("Vertex 0", Integer) = 0
        [HideInInspector] _FraxinusWindVertex1 ("Vertex 1", Integer) = 1
        [HideInInspector] _FraxinusWindVertex2 ("Vertex 2", Integer) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "DisableBatching" = "LODFading" }
        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile_vertex LOD_FADE_PERCENTAGE
        #pragma multi_compile_local _ _WINDQUALITY_BEST
        #define ENABLE_WIND
        #include "UnityCG.cginc"
        #define SPEEDTREE_8_WIND 1
        #include "SpeedTreeWind.cginc"
        CBUFFER_START(SpeedTreeWindHistory)
        float4 _ST_WindVectorHistory, _ST_WindGlobalHistory, _ST_WindBranchHistory;
        float4 _ST_WindBranchTwitchHistory, _ST_WindBranchWhipHistory, _ST_WindBranchAnchorHistory;
        float4 _ST_WindBranchAdherencesHistory, _ST_WindTurbulencesHistory;
        float4 _ST_WindLeaf1RippleHistory, _ST_WindLeaf1TumbleHistory, _ST_WindLeaf1TwitchHistory;
        float4 _ST_WindLeaf2RippleHistory, _ST_WindLeaf2TumbleHistory, _ST_WindLeaf2TwitchHistory;
        float4 _ST_WindFrondRippleHistory, _ST_WindAnimationHistory;
        CBUFFER_END
        RWByteAddressBuffer _FraxinusWindOutput : register(u1);
        uint _FraxinusWindOutputByteOffset;
        uint _FraxinusWindVertex0, _FraxinusWindVertex1, _FraxinusWindVertex2;
        struct Varyings
        {
            float4 position : SV_POSITION;
            nointerpolation float4 wind[16] : TEXCOORD0;
            nointerpolation uint history : TEXCOORD16;
        };
        Varyings CaptureVertex(uint vertexID, bool history)
        {
            Varyings result;
            result.position = float4(0, 0, -1, 0);
            if (vertexID == _FraxinusWindVertex0) result.position = float4(-1, -1, 0, 1);
            else if (vertexID == _FraxinusWindVertex1) result.position = float4(-1, 3, 0, 1);
            else if (vertexID == _FraxinusWindVertex2) result.position = float4(3, -1, 0, 1);
            result.history = history ? 1 : 0;
            float4 current[16] = {
                _ST_WindVector, _ST_WindGlobal, _ST_WindBranch, _ST_WindBranchTwitch,
                _ST_WindBranchWhip, _ST_WindBranchAnchor, _ST_WindBranchAdherences, _ST_WindTurbulences,
                _ST_WindLeaf1Ripple, _ST_WindLeaf1Tumble, _ST_WindLeaf1Twitch, _ST_WindLeaf2Ripple,
                _ST_WindLeaf2Tumble, _ST_WindLeaf2Twitch, _ST_WindFrondRipple, _ST_WindAnimation
            };
            float4 previous[16] = {
                _ST_WindVectorHistory, _ST_WindGlobalHistory, _ST_WindBranchHistory, _ST_WindBranchTwitchHistory,
                _ST_WindBranchWhipHistory, _ST_WindBranchAnchorHistory, _ST_WindBranchAdherencesHistory, _ST_WindTurbulencesHistory,
                _ST_WindLeaf1RippleHistory, _ST_WindLeaf1TumbleHistory, _ST_WindLeaf1TwitchHistory, _ST_WindLeaf2RippleHistory,
                _ST_WindLeaf2TumbleHistory, _ST_WindLeaf2TwitchHistory, _ST_WindFrondRippleHistory, _ST_WindAnimationHistory
            };
            for (uint i = 0; i < 16; ++i) result.wind[i] = history ? previous[i] : current[i];
            return result;
        }
        Varyings VertCurrent(uint vertexID : SV_VertexID) { return CaptureVertex(vertexID, false); }
        Varyings VertHistory(uint vertexID : SV_VertexID) { return CaptureVertex(vertexID, true); }
        float4 Frag(Varyings input) : SV_Target
        {
            uint parameter = (uint)input.position.x % 16;
            uint offset = _FraxinusWindOutputByteOffset + (input.history * 16 + parameter) * 16;
            _FraxinusWindOutput.Store4(offset, asuint(input.wind[parameter]));
            return 0;
        }
        ENDHLSL
        Pass
        {
            Name "CurrentWind"
            Tags { "LightMode" = "FraxinusGPUBatcherWindCaptureCurrent" }
            Cull Off ZTest Always ZWrite Off ColorMask 0
            HLSLPROGRAM
            #pragma vertex VertCurrent
            #pragma fragment Frag
            ENDHLSL
        }
        Pass
        {
            Name "HistoryWind"
            Tags { "LightMode" = "FraxinusGPUBatcherWindCaptureHistory" }
            Cull Off ZTest Always ZWrite Off ColorMask 0
            HLSLPROGRAM
            #pragma vertex VertHistory
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
