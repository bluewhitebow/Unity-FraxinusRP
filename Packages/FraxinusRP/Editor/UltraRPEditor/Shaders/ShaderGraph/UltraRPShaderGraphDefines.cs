using UnityEngine;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    #region StructCollections
    static class UltraRPCoreStructCollections
    {
        public static readonly StructCollection Default = new StructCollection
        {
            { Structs.Attributes },
            { UltraRPStructs.Varyings },
            { Structs.SurfaceDescriptionInputs },
            { Structs.VertexDescriptionInputs },
        };
    }
    #endregion

    #region RequiredFields
    static class UltraRPCoreRequiredFields
    {
        public static readonly FieldCollection Meta = new FieldCollection
        {
            StructFields.Attributes.positionOS, StructFields.Attributes.normalOS,
            StructFields.Attributes.uv0, StructFields.Attributes.uv1, StructFields.Attributes.uv2,
            StructFields.Attributes.instanceID, StructFields.Varyings.positionCS,
            StructFields.Varyings.texCoord0, StructFields.Varyings.texCoord1, StructFields.Varyings.texCoord2,
        };

        public static readonly FieldCollection ShadowCaster = new FieldCollection()
        {
            StructFields.Varyings.normalWS,
        };

        public static readonly FieldCollection DepthNormals = new FieldCollection()
        {
            StructFields.Attributes.uv1,
            StructFields.Varyings.normalWS,
            StructFields.Varyings.tangentWS,
        };
    }
    #endregion

    #region FieldDependencies
    static class UltraRPCoreFieldDependencies
    {
        public static readonly DependencyCollection Default = new DependencyCollection()
        {
            { FieldDependencies.Default },
            new FieldDependency(UltraRPStructFields.Varyings.stereoTargetEyeIndexAsRTArrayIdx, StructFields.Attributes.instanceID),
            new FieldDependency(UltraRPStructFields.Varyings.stereoTargetEyeIndexAsBlendIdx0, StructFields.Attributes.instanceID),
        };
    }
    #endregion

    #region Pragmas
    static class UltraRPCorePragmas
    {
        public static readonly PragmaCollection MotionVectors = new PragmaCollection
        {
            { Pragma.Target(ShaderModel.Target35) },
            { Pragma.MultiCompileInstancing },
            { Pragma.Vertex("vert") }, { Pragma.Fragment("frag") },
        };

        public static readonly PragmaCollection RenderingLayers = new PragmaCollection
        {
            { Pragma.Target(ShaderModel.Target20) },
            { Pragma.MultiCompileInstancing },
            { Pragma.InstancingOptions(InstancingOptions.RenderingLayer) },
            { Pragma.Vertex("vert") }, { Pragma.Fragment("frag") },
        };

        public static readonly PragmaCollection Default = new PragmaCollection
        {
            { Pragma.Target(ShaderModel.Target20) },
            { Pragma.Vertex("vert") },
            { Pragma.Fragment("frag") },
        };

        public static readonly PragmaCollection Instanced = new PragmaCollection
        {
            { Pragma.Target(ShaderModel.Target20) },
            { Pragma.MultiCompileInstancing },
            { Pragma.Vertex("vert") },
            { Pragma.Fragment("frag") },
        };

        public static readonly PragmaCollection Forward = new PragmaCollection
        {
            { Pragma.Target(ShaderModel.Target20) },
            { Pragma.MultiCompileInstancing },
            { Pragma.MultiCompileFog },
            { Pragma.InstancingOptions(InstancingOptions.RenderingLayer) },
            { Pragma.Vertex("vert") },
            { Pragma.Fragment("frag") },
        };

        public static readonly PragmaCollection GBuffer = new PragmaCollection
        {
            { Pragma.Target(ShaderModel.Target45) },
            { Pragma.ExcludeRenderers(new[] { Platform.GLES3, Platform.GLCore }) },
            { Pragma.MultiCompileInstancing },
            { Pragma.MultiCompileFog },
            { Pragma.InstancingOptions(InstancingOptions.RenderingLayer) },
            { Pragma.Vertex("vert") },
            { Pragma.Fragment("frag") },
        };
    }
    #endregion

    #region Includes
    static class UltraRPCoreIncludes
    {
        // Core SRP includes
        const string k_color = "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl";
        const string k_texture = "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl";
        const string k_textureStack = "Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureStack.hlsl";

        // UltraRP includes
        const string k_core = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl";
        const string k_lighting = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Lighting.hlsl";
        const string k_graphFunctions = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/ShaderGraphFunctions.hlsl";
        const string k_dots = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DOTS.hlsl";
        const string k_probeVolumes = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/ProbeVolumeVariants.hlsl";

        // ShaderGraph includes (Functions.hlsl only - ShaderVariables already defined by UltraRP)
        const string k_shaderGraphFunctions = "Packages/com.unity.shadergraph/ShaderGraphLibrary/Functions.hlsl";

        // UltraRP ShaderGraph includes
        const string k_varyings = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/Varyings.hlsl";
        const string k_shaderPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/ShaderPass.hlsl";
        const string k_depthOnlyPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/DepthOnlyPass.hlsl";
        const string k_depthNormalsOnlyPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/DepthNormalsOnlyPass.hlsl";
        const string k_shadowCasterPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/ShadowCasterPass.hlsl";
        const string k_forwardPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/ForwardPass.hlsl";
        const string k_unlitPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/UnlitPass.hlsl";
        const string k_selectionPickingPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/SelectionPickingPass.hlsl";
        const string k_lodCrossFade = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/LODCrossFade.hlsl";
        const string k_renderingLayers = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/RenderingLayers.hlsl";
        const string k_metaInput = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/MetaInput.hlsl";
        const string k_metaPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/MetaPass.hlsl";
        const string k_motionVectorPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/MotionVectorPass.hlsl";

        public static readonly IncludeCollection DOTSPregraph = new IncludeCollection
        {
            { k_dots, IncludeLocation.Pregraph, true },
        };

        public static readonly IncludeCollection CorePregraph = new IncludeCollection
        {
            // Set pipeline inputs and VT mip bias before TextureStack selects
            // its native VT or ordinary-texture implementation.
            { k_core, IncludeLocation.Pregraph },
            { k_color, IncludeLocation.Pregraph },
            { k_texture, IncludeLocation.Pregraph },
            { k_textureStack, IncludeLocation.Pregraph },
            { k_lighting, IncludeLocation.Pregraph },
            { k_lodCrossFade, IncludeLocation.Pregraph },
        };

        public static readonly IncludeCollection ShaderGraphPregraph = new IncludeCollection
        {
            { k_graphFunctions, IncludeLocation.Pregraph },
            { k_shaderGraphFunctions, IncludeLocation.Pregraph },
        };

        public static readonly IncludeCollection CorePostgraph = new IncludeCollection
        {
            { k_shaderPass, IncludeLocation.Pregraph },
            { k_varyings, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection DepthOnly = new IncludeCollection
        {
            // Pre-graph
            { DOTSPregraph },
            { CorePregraph },
            { ShaderGraphPregraph },

            // Post-graph
            { CorePostgraph },
            { k_depthOnlyPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection DepthNormalsOnly = new IncludeCollection
        {
            { k_renderingLayers, IncludeLocation.Pregraph, true },
            // Pre-graph
            { DOTSPregraph },
            { CorePregraph },
            { ShaderGraphPregraph },

            // Post-graph
            { CorePostgraph },
            { k_depthNormalsOnlyPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection ShadowCaster = new IncludeCollection
        {
            // Pre-graph
            { DOTSPregraph },
            { CorePregraph },
            { ShaderGraphPregraph },

            // Post-graph
            { CorePostgraph },
            { k_shadowCasterPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection Forward = new IncludeCollection
        {
            { k_renderingLayers, IncludeLocation.Pregraph, true },
            { k_probeVolumes, IncludeLocation.Pregraph, true },
            // Pre-graph
            { DOTSPregraph },
            { CorePregraph },
            { ShaderGraphPregraph },

            // Post-graph
            { CorePostgraph },
            { k_forwardPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection Unlit = new IncludeCollection
        {
            { k_renderingLayers, IncludeLocation.Pregraph, true },
            // Pre-graph
            { DOTSPregraph },
            { CorePregraph },
            { ShaderGraphPregraph },

            // Post-graph
            { CorePostgraph },
            { k_unlitPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection SceneSelection = new IncludeCollection
        {
            // Pre-graph
            { DOTSPregraph },
            { CorePregraph },
            { ShaderGraphPregraph },

            // Post-graph
            { CorePostgraph },
            { k_selectionPickingPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection ScenePicking = new IncludeCollection
        {
            // Pre-graph
            { DOTSPregraph },
            { CorePregraph },
            { ShaderGraphPregraph },

            // Post-graph
            { CorePostgraph },
            { k_selectionPickingPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection Meta = new IncludeCollection
        {
            { DOTSPregraph }, { CorePregraph }, { ShaderGraphPregraph },
            { k_metaInput, IncludeLocation.Pregraph }, { CorePostgraph },
            { k_metaPass, IncludeLocation.Postgraph },
        };

        public static readonly IncludeCollection MotionVectors = new IncludeCollection
        {
            { DOTSPregraph }, { CorePregraph }, { ShaderGraphPregraph }, { CorePostgraph },
            { k_motionVectorPass, IncludeLocation.Postgraph },
        };
    }
    #endregion

    #region Keywords
    static class UltraRPCoreKeywordDescriptors
    {
        public static readonly KeywordDescriptor MetaLit = new KeywordDescriptor
        {
            displayName = "Meta Lit", referenceName = "ULTRA_META_LIT", type = KeywordType.Boolean,
        };
        public static readonly KeywordDescriptor AutomaticTimeBasedMotionVectors = new KeywordDescriptor
        {
            displayName = "Time Based Motion Vectors", referenceName = "AUTOMATIC_TIME_BASED_MOTION_VECTORS", type = KeywordType.Boolean,
        };
        static KeywordDescriptor MaterialKeyword(string name) => new KeywordDescriptor
        {
            displayName = name,
            referenceName = name,
            type = KeywordType.Boolean,
            definition = KeywordDefinition.ShaderFeature,
            scope = KeywordScope.Local,
            stages = KeywordShaderStage.Fragment,
        };

        public static readonly KeywordDescriptor SpecularSetup = MaterialKeyword("_SPECULAR_SETUP");
        public static readonly KeywordDescriptor ReceiveShadowsOff = MaterialKeyword("_RECEIVE_SHADOWS_OFF");
        public static readonly KeywordDescriptor AlphaPremultiply = MaterialKeyword("_ALPHAPREMULTIPLY_ON");
        public static readonly KeywordDescriptor AlphaModulate = MaterialKeyword("_ALPHAMODULATE_ON");

        public static readonly KeywordDescriptor AlphaTestOn = new KeywordDescriptor()
        {
            displayName = "_ALPHATEST_ON",
            referenceName = "_ALPHATEST_ON",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.ShaderFeature,
            scope = KeywordScope.Local,
            stages = KeywordShaderStage.Fragment,
        };

        public static readonly KeywordDescriptor SurfaceTypeTransparent = new KeywordDescriptor()
        {
            displayName = "_SURFACE_TYPE_TRANSPARENT",
            referenceName = "_SURFACE_TYPE_TRANSPARENT",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.ShaderFeature,
            scope = KeywordScope.Global,
            stages = KeywordShaderStage.Fragment,
        };

        public static readonly KeywordDescriptor MainLightShadows = new KeywordDescriptor()
        {
            displayName = "Main Light Shadows",
            referenceName = "",
            type = KeywordType.Enum,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
            entries = new KeywordEntry[]
            {
                new KeywordEntry() { displayName = "Off", referenceName = "" },
                new KeywordEntry() { displayName = "No Cascade", referenceName = "MAIN_LIGHT_SHADOWS" },
                new KeywordEntry() { displayName = "Cascade", referenceName = "MAIN_LIGHT_SHADOWS_CASCADE" },
                new KeywordEntry() { displayName = "Screen", referenceName = "MAIN_LIGHT_SHADOWS_SCREEN" },
            }
        };

        public static readonly KeywordDescriptor AdditionalLights = new KeywordDescriptor()
        {
            displayName = "Additional Lights",
            referenceName = "",
            type = KeywordType.Enum,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
            entries = new KeywordEntry[]
            {
                new KeywordEntry() { displayName = "Off", referenceName = "" },
                new KeywordEntry() { displayName = "Vertex", referenceName = "ADDITIONAL_LIGHTS_VERTEX" },
                new KeywordEntry() { displayName = "Fragment", referenceName = "ADDITIONAL_LIGHTS" },
            }
        };

        public static readonly KeywordDescriptor AdditionalLightShadows = new KeywordDescriptor()
        {
            displayName = "Additional Light Shadows",
            referenceName = "_ADDITIONAL_LIGHT_SHADOWS",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
            stages = KeywordShaderStage.Fragment,
        };

        public static readonly KeywordDescriptor ShadowsSoft = new KeywordDescriptor()
        {
            displayName = "Soft Shadows",
            referenceName = "",
            type = KeywordType.Enum,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
            stages = KeywordShaderStage.Fragment,
            entries = new KeywordEntry[]
            {
                new KeywordEntry() { displayName = "Off", referenceName = "" },
                new KeywordEntry() { displayName = "Soft Shadows Per Light", referenceName = "SHADOWS_SOFT" },
                new KeywordEntry() { displayName = "Soft Shadows Low", referenceName = "SHADOWS_SOFT_LOW" },
                new KeywordEntry() { displayName = "Soft Shadows Medium", referenceName = "SHADOWS_SOFT_MEDIUM" },
                new KeywordEntry() { displayName = "Soft Shadows High", referenceName = "SHADOWS_SOFT_HIGH" },
            }
        };

        public static readonly KeywordDescriptor CastingPunctualLightShadow = new KeywordDescriptor()
        {
            displayName = "Casting Punctual Light Shadow",
            referenceName = "_CASTING_PUNCTUAL_LIGHT_SHADOW",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
            stages = KeywordShaderStage.Vertex,
        };

        public static readonly KeywordDescriptor SceneSelectionPass = new KeywordDescriptor()
        {
            displayName = "Scene Selection Pass",
            referenceName = "SCENESELECTIONPASS",
            type = KeywordType.Boolean,
        };

        public static readonly KeywordDescriptor ScenePickingPass = new KeywordDescriptor()
        {
            displayName = "Scene Picking Pass",
            referenceName = "SCENEPICKINGPASS",
            type = KeywordType.Boolean,
        };

        public static readonly KeywordDescriptor AlphaClipThreshold = new KeywordDescriptor()
        {
            displayName = "AlphaClipThreshold",
            referenceName = "ALPHA_CLIP_THRESHOLD",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.Predefined,
        };

        public static readonly KeywordDescriptor AlphaToMask = new KeywordDescriptor()
        {
            displayName = "Alpha To Mask",
            referenceName = "_ALPHATOMASK_ON",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.ShaderFeature,
            scope = KeywordScope.Local,
            stages = KeywordShaderStage.Fragment,
        };

        public static readonly KeywordDescriptor UseUnityCrossFade = new KeywordDescriptor()
        {
            displayName = "Use Unity CrossFade",
            referenceName = "USE_UNITY_CROSSFADE",
            type = KeywordType.Boolean,
        };

        public static readonly KeywordDescriptor LODFadeCrossFade = new KeywordDescriptor()
        {
            displayName = "LOD Fade CrossFade",
            referenceName = "LOD_FADE_CROSSFADE",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor DBuffer = new KeywordDescriptor()
        {
            displayName = "DBuffer",
            referenceName = "",
            type = KeywordType.Enum,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
            entries = new KeywordEntry[]
            {
                new KeywordEntry() { displayName = "Off", referenceName = "" },
                new KeywordEntry() { displayName = "MRT1", referenceName = "DBUFFER_MRT1" },
                new KeywordEntry() { displayName = "MRT2", referenceName = "DBUFFER_MRT2" },
                new KeywordEntry() { displayName = "MRT3", referenceName = "DBUFFER_MRT3" },
            }
        };

        public static readonly KeywordDescriptor StaticLightmap = new KeywordDescriptor()
        {
            displayName = "Static Lightmap",
            referenceName = "LIGHTMAP_ON",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor DynamicLightmap = new KeywordDescriptor()
        {
            displayName = "Dynamic Lightmap",
            referenceName = "DYNAMICLIGHTMAP_ON",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor DirectionalLightmapCombined = new KeywordDescriptor()
        {
            displayName = "Directional Lightmap Combined",
            referenceName = "DIRLIGHTMAP_COMBINED",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor LightmapShadowMixing = new KeywordDescriptor()
        {
            displayName = "Lightmap Shadow Mixing",
            referenceName = "LIGHTMAP_SHADOW_MIXING",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor ShadowsShadowmask = new KeywordDescriptor()
        {
            displayName = "Shadows Shadowmask",
            referenceName = "SHADOWS_SHADOWMASK",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor ForwardPlus = new KeywordDescriptor()
        {
            displayName = "Forward+",
            referenceName = "_FORWARD_PLUS",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor DebugDisplay = new KeywordDescriptor()
        {
            displayName = "Debug Display",
            referenceName = "DEBUG_DISPLAY",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
            stages = KeywordShaderStage.Fragment,
        };

        public static readonly KeywordDescriptor LightCookies = new KeywordDescriptor()
        {
            displayName = "Light Cookies",
            referenceName = "_LIGHT_COOKIES",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor MixedLightingSubtractive = new KeywordDescriptor()
        {
            displayName = "Mixed Lighting Subtractive",
            referenceName = "_MIXED_LIGHTING_SUBTRACTIVE",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor GBufferNormalsOct = new KeywordDescriptor()
        {
            displayName = "GBuffer Normals Oct",
            referenceName = "_GBUFFER_NORMALS_OCT",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor RenderPassEnabled = new KeywordDescriptor()
        {
            displayName = "Render Pass Enabled",
            referenceName = "_RENDER_PASS_ENABLED",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor LightLayers = new KeywordDescriptor()
        {
            displayName = "Light Layers",
            referenceName = "_LIGHT_LAYERS",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.MultiCompile,
            scope = KeywordScope.Global,
        };

        public static readonly KeywordDescriptor EditorVisualization = new KeywordDescriptor()
        {
            displayName = "Editor Visualization",
            referenceName = "EDITOR_VISUALIZATION",
            type = KeywordType.Boolean,
            definition = KeywordDefinition.ShaderFeature,
            scope = KeywordScope.Global,
        };
    }

    static class UltraRPCoreKeywords
    {
        public static readonly KeywordCollection ShadowCaster = new KeywordCollection
        {
            { UltraRPCoreKeywordDescriptors.CastingPunctualLightShadow },
        };

        public static readonly KeywordCollection Forward = new KeywordCollection
        {
            { UltraRPCoreKeywordDescriptors.MainLightShadows },
            { UltraRPCoreKeywordDescriptors.AdditionalLights },
            { UltraRPCoreKeywordDescriptors.AdditionalLightShadows },
            { UltraRPCoreKeywordDescriptors.ShadowsSoft },
            { UltraRPCoreKeywordDescriptors.StaticLightmap },
            { UltraRPCoreKeywordDescriptors.DynamicLightmap },
            { UltraRPCoreKeywordDescriptors.DirectionalLightmapCombined },
            { UltraRPCoreKeywordDescriptors.LightmapShadowMixing },
            { UltraRPCoreKeywordDescriptors.ShadowsShadowmask },
            { UltraRPCoreKeywordDescriptors.ForwardPlus },
        };
    }
    #endregion

    #region Defines
    static class UltraRPCoreDefines
    {
        public static readonly DefineCollection SceneSelection = new DefineCollection
        {
            { UltraRPCoreKeywordDescriptors.SceneSelectionPass, 1 },
        };

        public static readonly DefineCollection ScenePicking = new DefineCollection
        {
            { UltraRPCoreKeywordDescriptors.ScenePickingPass, 1 },
        };

        public static readonly DefineCollection UseFragmentFog = new DefineCollection()
        {
            { new KeywordDescriptor { displayName = "UseFragmentFog", referenceName = "_FOG_FRAGMENT", type = KeywordType.Boolean }, 1 },
        };
    }
    #endregion

    #region RenderStates
    static class UltraRPCoreRenderStates
    {
        public static class Uniforms
        {
            public static readonly string srcBlend = "[" + UltraRPProperty.SrcBlend + "]";
            public static readonly string dstBlend = "[" + UltraRPProperty.DstBlend + "]";
            public static readonly string cullMode = "[" + UltraRPProperty.CullMode + "]";
            public static readonly string zWrite = "[" + UltraRPProperty.ZWrite + "]";
            public static readonly string zTest = "[" + UltraRPProperty.ZTest + "]";
        }

        public static Cull RenderFaceToCull(RenderFace renderFace)
        {
            switch (renderFace)
            {
                case RenderFace.Back:
                    return Cull.Front;
                case RenderFace.Front:
                    return Cull.Back;
                case RenderFace.Both:
                    return Cull.Off;
            }
            return Cull.Back;
        }

        public static RenderStateCollection Forward(UltraRPTarget target)
        {
            var result = UberSwitchedRenderState(target);
            // VT uses target 1, or target 2 when rendering layers occupy target 1.
            // Layer output has alpha 1 and therefore still overwrites its target.
            // Keep target 0's material-controlled color blending independent.
            result.Add(RenderState.Blend("Blend 1 One OneMinusSrcAlpha"));
            result.Add(RenderState.Blend("Blend 2 One OneMinusSrcAlpha"));
            return result;
        }

        public static RenderStateCollection UberSwitchedRenderState(UltraRPTarget target)
        {
            if (target.allowMaterialOverride)
            {
                return new RenderStateCollection
                {
                    RenderState.ZTest(Uniforms.zTest),
                    RenderState.ZWrite(Uniforms.zWrite),
                    RenderState.Cull(Uniforms.cullMode),
                    RenderState.Blend(Uniforms.srcBlend, Uniforms.dstBlend, "[" + UltraRPProperty.SrcBlendAlpha + "]", "[" + UltraRPProperty.DstBlendAlpha + "]"),
                };
            }
            else
            {
                var result = new RenderStateCollection();

                result.Add(RenderState.ZTest(target.zTestMode.ToString()));

                if (target.zWriteControl == ZWriteControl.Auto)
                {
                    if (target.surfaceType == SurfaceType.Opaque)
                        result.Add(RenderState.ZWrite(ZWrite.On));
                    else
                        result.Add(RenderState.ZWrite(ZWrite.Off));
                }
                else if (target.zWriteControl == ZWriteControl.ForceEnabled)
                    result.Add(RenderState.ZWrite(ZWrite.On));
                else
                    result.Add(RenderState.ZWrite(ZWrite.Off));

                result.Add(RenderState.Cull(RenderFaceToCull(target.renderFace)));

                if (target.surfaceType == SurfaceType.Opaque)
                {
                    result.Add(RenderState.Blend(Blend.One, Blend.Zero));
                }
                else
                {
                    switch (target.alphaMode)
                    {
                        case AlphaMode.Alpha:
                            result.Add(RenderState.Blend(Blend.SrcAlpha, Blend.OneMinusSrcAlpha, Blend.One, Blend.OneMinusSrcAlpha));
                            break;
                        case AlphaMode.Premultiply:
                            result.Add(RenderState.Blend(Blend.One, Blend.OneMinusSrcAlpha, Blend.One, Blend.OneMinusSrcAlpha));
                            break;
                        case AlphaMode.Additive:
                            result.Add(RenderState.Blend(Blend.SrcAlpha, Blend.One, Blend.One, Blend.One));
                            break;
                        case AlphaMode.Multiply:
                            result.Add(RenderState.Blend(Blend.DstColor, Blend.Zero, Blend.Zero, Blend.One));
                            break;
                    }
                }

                return result;
            }
        }

        public static RenderStateDescriptor UberSwitchedCullRenderState(UltraRPTarget target)
        {
            if (target.allowMaterialOverride)
                return RenderState.Cull(Uniforms.cullMode);
            else
                return RenderState.Cull(RenderFaceToCull(target.renderFace));
        }

        public static RenderStateCollection ShadowCaster(UltraRPTarget target)
        {
            var result = new RenderStateCollection
            {
                { RenderState.ZTest(ZTest.LEqual) },
                { RenderState.ZWrite(ZWrite.On) },
                { UberSwitchedCullRenderState(target) },
                { RenderState.ColorMask("ColorMask 0") },
            };
            return result;
        }

        public static RenderStateCollection DepthOnly(UltraRPTarget target)
        {
            var result = new RenderStateCollection
            {
                { RenderState.ZTest(ZTest.LEqual) },
                { RenderState.ZWrite(ZWrite.On) },
                { UberSwitchedCullRenderState(target) },
                { RenderState.ColorMask("ColorMask R") },
            };

            return result;
        }

        public static RenderStateCollection MotionVectors(UltraRPTarget target)
        {
            return new RenderStateCollection
            {
                RenderState.ZTest(ZTest.LEqual), RenderState.ZWrite(ZWrite.On),
                UberSwitchedCullRenderState(target), RenderState.ColorMask("ColorMask RG"),
            };
        }

        public static RenderStateCollection DepthNormalsOnly(UltraRPTarget target)
        {
            var result = new RenderStateCollection
            {
                { RenderState.ZTest(ZTest.LEqual) },
                { RenderState.ZWrite(ZWrite.On) },
                { UberSwitchedCullRenderState(target) }
            };

            return result;
        }

        public static RenderStateCollection SceneSelection(UltraRPTarget target)
        {
            var result = new RenderStateCollection
            {
                { RenderState.Cull(Cull.Off) },
            };

            return result;
        }

        public static RenderStateCollection ScenePicking(UltraRPTarget target)
        {
            var result = new RenderStateCollection
            {
                { UberSwitchedCullRenderState(target) }
            };

            return result;
        }

        public static readonly RenderStateCollection Meta = new RenderStateCollection
        {
            { RenderState.Cull(Cull.Off) },
        };
    }
    #endregion

    #region PortMasks
    static class UltraRPCoreBlockMasks
    {
        public static readonly BlockFieldDescriptor[] CustomMotionVectorVertex = {
            BlockFields.VertexDescription.Position, BlockFields.VertexDescription.Normal,
            BlockFields.VertexDescription.Tangent, UltraRPBlockFields.VertexDescription.MotionVector,
        };
        public static readonly BlockFieldDescriptor[] FragmentMeta = {
            BlockFields.SurfaceDescription.BaseColor, BlockFields.SurfaceDescription.Emission,
            BlockFields.SurfaceDescription.Alpha, BlockFields.SurfaceDescription.AlphaClipThreshold,
        };
        public static readonly BlockFieldDescriptor[] Vertex = new BlockFieldDescriptor[]
        {
            BlockFields.VertexDescription.Position,
            BlockFields.VertexDescription.Normal,
            BlockFields.VertexDescription.Tangent,
        };

        public static readonly BlockFieldDescriptor[] FragmentAlphaOnly = new BlockFieldDescriptor[]
        {
            BlockFields.SurfaceDescription.Alpha,
            BlockFields.SurfaceDescription.AlphaClipThreshold,
        };

        public static readonly BlockFieldDescriptor[] FragmentColorAlpha = new BlockFieldDescriptor[]
        {
            BlockFields.SurfaceDescription.BaseColor,
            BlockFields.SurfaceDescription.Alpha,
            BlockFields.SurfaceDescription.AlphaClipThreshold,
        };

        public static readonly BlockFieldDescriptor[] FragmentDepthNormals = new BlockFieldDescriptor[]
        {
            BlockFields.SurfaceDescription.NormalOS,
            BlockFields.SurfaceDescription.NormalTS,
            BlockFields.SurfaceDescription.NormalWS,
            BlockFields.SurfaceDescription.Alpha,
            BlockFields.SurfaceDescription.AlphaClipThreshold,
        };

        public static readonly BlockFieldDescriptor[] FragmentLit = new BlockFieldDescriptor[]
        {
            BlockFields.SurfaceDescription.BaseColor,
            BlockFields.SurfaceDescription.NormalOS,
            BlockFields.SurfaceDescription.NormalTS,
            BlockFields.SurfaceDescription.NormalWS,
            BlockFields.SurfaceDescription.Metallic,
            BlockFields.SurfaceDescription.Specular,
            BlockFields.SurfaceDescription.Smoothness,
            BlockFields.SurfaceDescription.Occlusion,
            BlockFields.SurfaceDescription.Emission,
            BlockFields.SurfaceDescription.Alpha,
            BlockFields.SurfaceDescription.AlphaClipThreshold,
        };
    }
    #endregion

    #region CustomInterpolators
    static class UltraRPCoreCustomInterpDescriptors
    {
        public static readonly CustomInterpSubGen.Collection Common = new CustomInterpSubGen.Collection
        {
            CustomInterpSubGen.Descriptor.MakeBlock(CustomInterpSubGen.Splice.k_spliceCopyToSDI, "output", "input"),
            CustomInterpSubGen.Descriptor.MakeFunc(CustomInterpSubGen.Splice.k_splicePreSurface, "CustomInterpolatorPassThroughFunc", "Varyings", "VertexDescription", "CUSTOMINTERPOLATOR_VARYPASSTHROUGH_FUNC", "FEATURES_GRAPH_VERTEX")
        };
    }
    #endregion
}
