using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.ShaderGraph;

using static FraxinusEngine.Rendering.Ultra.ShaderUtils;
using static FraxinusEditor.Rendering.Ultra.UltraRPSubShaderUtils;


namespace FraxinusEditor.Rendering.Ultra
{
    sealed class UltraRPSixWaySubTarget : UltraRPSubTarget
    {
        static readonly GUID k_sourceCodeGuid = new GUID("00000000000000000000000000000006"); // UltraRPSixWaySubTarget.cs

        public override int latestVersion => 2;

        public UltraRPSixWaySubTarget()
        {
            displayName = "Six-way Smoke Lit";
        }

        protected override ShaderID shaderID => ShaderID.SG_SixWaySmokeLit;

        public override bool IsActive() => true;

        [SerializeField]
        bool _useColorAbsorption = true;

        bool useColorAbsorption
        {
            get => _useColorAbsorption;
            set => _useColorAbsorption = value;
        }

        public override void Setup(ref TargetSetupContext context)
        {
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);
            base.Setup(ref context);

            var ultraRPType = typeof(UltraRenderPipelineAsset);
            if (!context.HasCustomEditorForRenderPipeline(ultraRPType))
            {
                var gui = typeof(SixWayGUI);
#if HAS_VFX_GRAPH
                if (TargetsVFX())
                    gui = typeof(UnityEditor.VFX.UltraRP.VFXSixWayGUI);
#endif
                context.AddCustomEditorForRenderPipeline(gui.FullName, ultraRPType);
            }

            // Process SubShaders
            context.AddSubShader(PostProcessSubShader(SubShaders.SixWaySubShader(target, target.renderType, target.renderQueue, target.disableBatching, useColorAbsorption)));
        }

        public override void ProcessPreviewMaterial(Material material)
        {
            if (target.allowMaterialOverride)
            {
                // copy our target's default settings into the material
                // (technically not necessary since we are always recreating the material from the shader each time,
                // which will pull over the defaults from the shader definition)
                // but if that ever changes, this will ensure the defaults are set

                material.SetFloat(UltraRPProperty.CastShadows, target.castShadows ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.ReceiveShadows, target.receiveShadows ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.SurfaceType, (float)target.surfaceType);
                material.SetFloat(UltraRPProperty.BlendMode, (float)target.alphaMode);
                material.SetFloat(UltraRPProperty.AlphaClip, target.alphaClip ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.CullMode, (int)target.renderFace);
                material.SetFloat(UltraRPProperty.ZWriteControl, (float)target.zWriteControl);
                material.SetFloat(UltraRPProperty.ZTest, (float)target.zTestMode);
                material.SetFloat(SixWayProperties.UseColorAbsorption, useColorAbsorption ? 1.0f : 0.0f);
            }

            // We always need these properties regardless of whether the material is allowed to override
            // Queue control & offset enable correct automatic render queue behavior
            // Control == 0 is automatic, 1 is user-specified render queue
            material.SetFloat(UltraRPProperty.QueueOffset, 0.0f);
            material.SetFloat(UltraRPProperty.QueueControl, 0.0f);

            // call the full lit material setup function
            bool useColorAbsorptionValue = false;
            if (material.HasProperty(SixWayProperties.UseColorAbsorption))
                useColorAbsorptionValue = material.GetFloat(SixWayProperties.UseColorAbsorption) > 0.0f;
            CoreUtils.SetKeyword(material, "_SIX_WAY_COLOR_ABSORPTION", useColorAbsorptionValue);
        }

        public override void GetFields(ref TargetFieldContext context)
        {
            base.GetFields(ref context);

            bool containsNormalDescriptor = false;
            foreach (var block in context.blocks)
            {
                if (Equals(block.descriptor, BlockFields.SurfaceDescription.NormalOS) ||
                    Equals(block.descriptor, BlockFields.SurfaceDescription.NormalTS) ||
                    Equals(block.descriptor, BlockFields.SurfaceDescription.NormalWS))
                {
                    containsNormalDescriptor = true;
                    break;
                }
            }
            context.AddField(UltraRPFields.Normal, containsNormalDescriptor);
        }

        public struct Varyings
        {
            public static string name = "Varyings";

            public static FieldDescriptor diffuseGIData0 = new FieldDescriptor(Varyings.name, "diffuseGIData0",
                "VARYINGS_NEED_SIX_WAY_DIFFUSE_GI_DATA", ShaderValueType.Float4, subscriptOptions: StructFieldOptions.Optional);

            public static FieldDescriptor diffuseGIData1 = new FieldDescriptor(Varyings.name, "diffuseGIData1",
                "VARYINGS_NEED_SIX_WAY_DIFFUSE_GI_DATA", ShaderValueType.Float4, subscriptOptions: StructFieldOptions.Optional);

            public static FieldDescriptor diffuseGIData2 = new FieldDescriptor(Varyings.name, "diffuseGIData2",
                "VARYINGS_NEED_SIX_WAY_DIFFUSE_GI_DATA", ShaderValueType.Float4, subscriptOptions: StructFieldOptions.Optional);
        }

        public override void GetActiveBlocks(ref TargetActiveBlockContext context)
        {
            context.AddBlock(BlockFields.SurfaceDescription.Emission);
            context.AddBlock(BlockFields.SurfaceDescription.Occlusion);
            context.AddBlock(BlockFields.SurfaceDescription.MapRightTopBack);
            context.AddBlock(BlockFields.SurfaceDescription.MapLeftBottomFront);
            context.AddBlock(BlockFields.SurfaceDescription.AbsorptionStrength, useColorAbsorption || target.allowMaterialOverride);

            // when the surface options are material controlled, we must show all of these blocks
            // when target controlled, we can cull the unnecessary blocks
            context.AddBlock(BlockFields.SurfaceDescription.Alpha, (target.surfaceType == SurfaceType.Transparent || target.alphaClip) || target.allowMaterialOverride);
            context.AddBlock(BlockFields.SurfaceDescription.AlphaClipThreshold, (target.alphaClip) || target.allowMaterialOverride);
        }

        public override void CollectShaderProperties(PropertyCollector collector, GenerationMode generationMode)
        {
            // if using material control, add the material property to control workflow mode
            if (target.allowMaterialOverride)
            {
                collector.AddFloatProperty(UltraRPProperty.CastShadows, target.castShadows ? 1.0f : 0.0f);
                collector.AddToggleProperty(UltraRPProperty.ReceiveShadows, target.receiveShadows);

                // setup properties using the defaults
                collector.AddFloatProperty(UltraRPProperty.SurfaceType, (float)target.surfaceType);
                collector.AddFloatProperty(UltraRPProperty.BlendMode, (float)target.alphaMode);
                collector.AddFloatProperty(UltraRPProperty.AlphaClip, target.alphaClip ? 1.0f : 0.0f);
                collector.AddFloatProperty(UltraRPProperty.SrcBlend, 1.0f);    // always set by material inspector, ok to have incorrect values here
                collector.AddFloatProperty(UltraRPProperty.DstBlend, 0.0f);    // always set by material inspector, ok to have incorrect values here
                collector.AddToggleProperty(UltraRPProperty.ZWrite, (target.surfaceType == SurfaceType.Opaque));
                collector.AddFloatProperty(UltraRPProperty.ZWriteControl, (float)target.zWriteControl);
                collector.AddFloatProperty(UltraRPProperty.ZTest, (float)target.zTestMode);    // ztest mode is designed to directly pass as ztest
                collector.AddFloatProperty(UltraRPProperty.CullMode, (float)target.renderFace);    // render face enum is designed to directly pass as a cull mode

                bool enableAlphaToMask = (target.alphaClip && (target.surfaceType == SurfaceType.Opaque));
                collector.AddFloatProperty(UltraRPProperty.AlphaToMask, enableAlphaToMask ? 1.0f : 0.0f);

                collector.AddToggleProperty(SixWayProperties.UseColorAbsorption, useColorAbsorption);
            }

            // We always need these properties regardless of whether the material is allowed to override other shader properties.
            // Queue control & offset enable correct automatic render queue behavior.  Control == 0 is automatic, 1 is user-specified.
            // We initialize queue control to -1 to indicate to UpdateMaterial that it needs to initialize it properly on the material.
            collector.AddFloatProperty(UltraRPProperty.QueueOffset, 0.0f);
            collector.AddFloatProperty(UltraRPProperty.QueueControl, -1.0f);
        }

        public override void GetPropertiesGUI(ref TargetPropertyGUIContext context, Action onChange, Action<String> registerUndo)
        {
            target.AddDefaultMaterialOverrideGUI(ref context, onChange, registerUndo);

            target.AddDefaultSurfacePropertiesGUI(ref context, onChange, registerUndo, showReceiveShadows: true);

            context.AddProperty("Use Color Absorption", new Toggle() { value = useColorAbsorption }, (evt) =>
            {
                if (Equals(useColorAbsorption, evt.newValue))
                    return;

                registerUndo("Change Use Color Absorption");
                useColorAbsorption = evt.newValue;
                onChange();
            });
        }

        protected override int ComputeMaterialNeedsUpdateHash()
        {
            int hash = base.ComputeMaterialNeedsUpdateHash();
            hash = hash * 23 + target.allowMaterialOverride.GetHashCode();
            return hash;
        }

        #region Properties
        public static class SixWayProperties
        {
            public static readonly string UseColorAbsorption = "_UseColorAbsorption";
        }
        #endregion

        #region SubShader
        static class SubShaders
        {
            const string k_sixWayMaterialTypeTag = "\"UltraMaterialType\" = \"SixWayLit\"";

            public static SubShaderDescriptor SixWaySubShader(UltraRPTarget target, string renderType, string renderQueue, string disableBatchingTag, bool useColorAbsorption)
            {
                SubShaderDescriptor result = new SubShaderDescriptor()
                {
                    pipelineTag = UltraRPTarget.k_pipelineTag,
                    customTags = k_sixWayMaterialTypeTag,
                    renderType = renderType,
                    renderQueue = renderQueue,
                    disableBatchingTag = disableBatchingTag,
                    generatesPreview = true,
                    passes = new PassCollection()
                };

                result.passes.Add(SixWayPasses.ForwardOnly(target, UltraRPCoreBlockMasks.Vertex, SixWayBlockMasks.s_fragmentLit, UltraRPCorePragmas.Forward, SixWayKeywords.s_forward, useColorAbsorption));

                // cull the shadowcaster pass if we know it will never be used
                if (target.castShadows || target.allowMaterialOverride)
                    result.passes.Add(PassVariant(UltraRPCorePasses.ShadowCaster(target), UltraRPCorePragmas.Instanced));

                if (target.mayWriteDepth)
                    result.passes.Add(PassVariant(UltraRPCorePasses.DepthOnly(target), UltraRPCorePragmas.Instanced));

                result.passes.Add(PassVariant(SixWayPasses.Meta(target), UltraRPCorePragmas.Default));

                // Currently neither of these passes (selection/picking) can be last for the game view for
                // UI shaders to render correctly. Verify [1352225] before changing this order.
                result.passes.Add(PassVariant(UltraRPCorePasses.SceneSelection(target), UltraRPCorePragmas.Default));
                result.passes.Add(PassVariant(UltraRPCorePasses.ScenePicking(target), UltraRPCorePragmas.Default));

                return result;
            }
        }
        #endregion

        #region Passes
        static class SixWayPasses
        {
            static void AddReceiveShadowsControlToPass(ref PassDescriptor pass, UltraRPTarget target, bool receiveShadows)
            {
                if (target.allowMaterialOverride)
                    pass.keywords.Add(SixWayKeywords.s_receiveShadowsOff);
                else if (!receiveShadows)
                    pass.defines.Add(SixWayKeywords.s_receiveShadowsOff, 1);
            }

            static void AddColorAbsorptionControlToPass(ref PassDescriptor pass, UltraRPTarget target,
                bool useColorAbsorption)
            {
                if (target.allowMaterialOverride)
                    pass.keywords.Add(SixWayKeywords.s_useColorAbsorption);
                else if (useColorAbsorption)
                    pass.defines.Add(SixWayKeywords.s_useColorAbsorption, 1);
            }

            static StructDescriptor ForwardVaryings
            {
                get
                {
                    var baseVaryingsDescriptor = UltraRPStructs.Varyings;

                    FieldDescriptor[] varyingsDescriptorFields =
                        new FieldDescriptor[baseVaryingsDescriptor.fields.Length + 3];

                    varyingsDescriptorFields[0] = Varyings.diffuseGIData0;
                    varyingsDescriptorFields[1] = Varyings.diffuseGIData1;
                    varyingsDescriptorFields[2] = Varyings.diffuseGIData2;
                    baseVaryingsDescriptor.fields.CopyTo(varyingsDescriptorFields, 3);
                    baseVaryingsDescriptor.fields = varyingsDescriptorFields;

                    return baseVaryingsDescriptor;
                }
            }

            static readonly StructCollection s_forwardStructCollection = new StructCollection
            {
                { Structs.Attributes },
                { ForwardVaryings },
                { Structs.SurfaceDescriptionInputs },
                { Structs.VertexDescriptionInputs },
            };

            public static PassDescriptor ForwardOnly(
                UltraRPTarget target,
                BlockFieldDescriptor[] vertexBlocks,
                BlockFieldDescriptor[] pixelBlocks,
                PragmaCollection pragmas,
                KeywordCollection keywords,
                bool useColorAbsorption)
            {
                var result = new PassDescriptor
                {
                    // Definition
                    displayName = "UltraRP Forward Only",
                    referenceName = "SHADERPASS_FORWARDONLY",
                    lightMode = "UltraRPForwardOnly",
                    useInPreview = true,

                    // Template
                    passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                    sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                    // Port Mask
                    validVertexBlocks = vertexBlocks,
                    validPixelBlocks = pixelBlocks,

                    // Fields
                    structs = s_forwardStructCollection,
                    requiredFields = SixWayRequiredFields.s_forward,
                    fieldDependencies = UltraRPCoreFieldDependencies.Default,

                    // Conditional State
                    renderStates = UltraRPCoreRenderStates.UberSwitchedRenderState(target),
                    pragmas = pragmas,
                    defines = new DefineCollection { UltraRPCoreDefines.UseFragmentFog },
                    keywords = new KeywordCollection { keywords },
                    includes = new IncludeCollection { SixWayIncludes.s_forward },

                    // Custom Interpolator Support
                    customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
                };

                UltraRPCorePasses.AddTargetSurfaceControlsToPass(ref result, target);
                UltraRPCorePasses.AddAlphaToMaskControlToPass(ref result, target);
                AddReceiveShadowsControlToPass(ref result, target, target.receiveShadows);
                UltraRPCorePasses.AddLODCrossFadeControlToPass(ref result, target);
                AddColorAbsorptionControlToPass(ref result, target, useColorAbsorption);

                return result;
            }

            public static PassDescriptor Meta(UltraRPTarget target)
            {
                var result = new PassDescriptor()
                {
                    // Definition
                    displayName = "Meta",
                    referenceName = "SHADERPASS_META",
                    lightMode = "Meta",

                    // Template
                    passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                    sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                    // Port Mask
                    validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                    validPixelBlocks = SixWayBlockMasks.s_fragmentMeta,

                    // Fields
                    structs = UltraRPCoreStructCollections.Default,
                    requiredFields = SixWayRequiredFields.s_meta,
                    fieldDependencies = UltraRPCoreFieldDependencies.Default,

                    // Conditional State
                    renderStates = UltraRPCoreRenderStates.Meta,
                    pragmas = UltraRPCorePragmas.Default,
                    defines = new DefineCollection() { UltraRPCoreDefines.UseFragmentFog },
                    keywords = new KeywordCollection() { UltraRPCoreKeywordDescriptors.EditorVisualization },
                    includes = SixWayIncludes.s_meta,

                    // Custom Interpolator Support
                    customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
                };

                UltraRPCorePasses.AddAlphaClipControlToPass(ref result, target);

                return result;
            }
        }
        #endregion

        #region PortMasks
        static class SixWayBlockMasks
        {
            public static readonly BlockFieldDescriptor[] s_fragmentLit = new BlockFieldDescriptor[]
            {
                BlockFields.SurfaceDescription.BaseColor,
                BlockFields.SurfaceDescription.MapRightTopBack,
                BlockFields.SurfaceDescription.MapLeftBottomFront,
                BlockFields.SurfaceDescription.AbsorptionStrength,
                BlockFields.SurfaceDescription.Emission,
                BlockFields.SurfaceDescription.Occlusion,
                BlockFields.SurfaceDescription.Alpha,
                BlockFields.SurfaceDescription.AlphaClipThreshold,
            };

            public static readonly BlockFieldDescriptor[] s_fragmentMeta = new BlockFieldDescriptor[]
            {
                BlockFields.SurfaceDescription.BaseColor,
                BlockFields.SurfaceDescription.Emission,
                BlockFields.SurfaceDescription.Alpha,
                BlockFields.SurfaceDescription.AlphaClipThreshold,
            };
        }
        #endregion

        #region RequiredFields
        static class SixWayRequiredFields
        {
            public static readonly FieldCollection s_forward = new FieldCollection()
            {
                StructFields.Attributes.uv1,
                StructFields.Attributes.uv2,
                StructFields.Varyings.positionWS,
                StructFields.Varyings.normalWS,
                StructFields.Varyings.tangentWS,                        // needed for vertex lighting
                UltraRPStructFields.Varyings.staticLightmapUV,
                UltraRPStructFields.Varyings.dynamicLightmapUV,
                UltraRPStructFields.Varyings.sh,
                UltraRPStructFields.Varyings.fogFactorAndVertexLight, // fog and vertex lighting, vert input is dependency
                UltraRPStructFields.Varyings.shadowCoord,             // shadow coord, vert input is dependency
                Varyings.diffuseGIData0,
                Varyings.diffuseGIData1,
                Varyings.diffuseGIData2,
            };

            public static readonly FieldCollection s_meta = new FieldCollection()
            {
                StructFields.Attributes.positionOS,
                StructFields.Attributes.normalOS,
                StructFields.Attributes.uv0,                            //
                StructFields.Attributes.uv1,                            // needed for meta vertex position
                StructFields.Attributes.uv2,                            // needed for meta UVs
                StructFields.Attributes.instanceID,                     // needed for rendering instanced terrain
                StructFields.Varyings.positionCS,
                StructFields.Varyings.texCoord0,                        // needed for meta UVs
                StructFields.Varyings.texCoord1,                        // VizUV
                StructFields.Varyings.texCoord2,                        // LightCoord
            };
        }
        #endregion

        #region Keywords
        static class SixWayKeywords
        {
            public static readonly KeywordDescriptor s_receiveShadowsOff = new KeywordDescriptor()
            {
                displayName = "Receive Shadows Off",
                referenceName = "_RECEIVE_SHADOWS_OFF",
                type = KeywordType.Boolean,
                definition = KeywordDefinition.ShaderFeature,
                scope = KeywordScope.Local,
            };

            public static readonly KeywordDescriptor s_useColorAbsorption = new KeywordDescriptor()
            {
                displayName = "Use Color Absorption",
                referenceName = "_SIX_WAY_COLOR_ABSORPTION",
                type = KeywordType.Boolean,
                definition = KeywordDefinition.ShaderFeature,
                scope = KeywordScope.Local,
                stages = KeywordShaderStage.Fragment
            };

            public static readonly KeywordCollection s_forward = new KeywordCollection
            {
                { UltraRPCoreKeywordDescriptors.MainLightShadows },
                { UltraRPCoreKeywordDescriptors.AdditionalLights },
                { UltraRPCoreKeywordDescriptors.AdditionalLightShadows },
                { UltraRPCoreKeywordDescriptors.ShadowsSoft },
                { UltraRPCoreKeywordDescriptors.ShadowsShadowmask },
                { UltraRPCoreKeywordDescriptors.LightLayers },
                { UltraRPCoreKeywordDescriptors.DebugDisplay },
                { UltraRPCoreKeywordDescriptors.LightCookies },
                { UltraRPCoreKeywordDescriptors.ForwardPlus },
            };
        }
        #endregion

        #region Includes
        static class SixWayIncludes
        {
            const string k_shadows = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Shadows.hlsl";
            const string k_metaInput = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/MetaInput.hlsl";
            const string k_forwardPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/SixWayForwardPass.hlsl";
            const string k_lightingMetaPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/LightingMetaPass.hlsl";
            const string k_sixWayLighting = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/SixWayLighting.hlsl";

            public static readonly IncludeCollection s_forward = new IncludeCollection
            {
                // Pre-graph
                { UltraRPCoreIncludes.DOTSPregraph },
                { UltraRPCoreIncludes.CorePregraph },
                { k_shadows, IncludeLocation.Pregraph },
                { UltraRPCoreIncludes.ShaderGraphPregraph },

                // Post-graph
                { k_sixWayLighting, IncludeLocation.Postgraph },
                { UltraRPCoreIncludes.CorePostgraph },
                { k_forwardPass, IncludeLocation.Postgraph },
            };

            public static readonly IncludeCollection s_meta = new IncludeCollection
            {
                // Pre-graph
                { UltraRPCoreIncludes.CorePregraph },
                { UltraRPCoreIncludes.ShaderGraphPregraph },
                { k_metaInput, IncludeLocation.Pregraph },

                // Post-graph
                { UltraRPCoreIncludes.CorePostgraph },
                { k_lightingMetaPass, IncludeLocation.Postgraph },
            };
        }
        #endregion
    }
}
