using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;

using FraxinusEngine.Rendering.Ultra;
using static FraxinusEngine.Rendering.Ultra.ShaderUtils;


namespace FraxinusEditor.Rendering.Ultra
{
    sealed class UltraRPDecalSubTarget : UltraRPSubTarget
    {
        static readonly GUID k_sourceCodeGuid = new GUID("00000000000000000000000000000005"); // UltraRPDecalSubTarget.cs

        internal class Styles
        {
            public const string header = "Surface Options";

            public static GUIContent affectAlbedoText = new GUIContent("Affect BaseColor", "When enabled, this decal uses its base color. When disabled, the decal has no base color effect.");
            public static GUIContent affectNormalText = new GUIContent("Affect Normal", "When enabled, this decal uses its normal. When disabled, the decal has no normal effect.");
            public static GUIContent affectNormalBlendText = new GUIContent("Affect Normal Blend", "When enabled, this decal uses its normal blending. When disabled, the decal has no normal blending effect.");
            public static GUIContent affectMetalText = new GUIContent("Affect Metal", "When enabled, this decal uses the metallic channel of its Mask Map. When disabled, the decal has no metallic effect.");
            public static GUIContent affectAmbientOcclusionText = new GUIContent("Affect Ambient Occlusion", "When enabled, this decal uses the smoothness channel of its Mask Map. When disabled, the decal has no smoothness effect.");
            public static GUIContent affectSmoothnessText = new GUIContent("Affect Smoothness", "When enabled, this decal uses the ambient occlusion channel of its Mask Map. When disabled, the decal has no ambient occlusion effect.");
            public static GUIContent affectEmissionText = new GUIContent("Affect Emission", "When enabled, this decal becomes emissive and appears self-illuminated. Affect Emission does not support Affects Transparent option on Decal Projector.");
            public static GUIContent supportLodCrossFadeText = new GUIContent("Support LOD CrossFade", "When enabled, this decal material supports LOD Cross fade if use on a Mesh.");
        }

        private const string k_material = "Material";
        private static FieldDescriptor s_affectsAlbedo = new FieldDescriptor(k_material, "AffectsAlbedo", "");
        private static FieldDescriptor s_affectsNormal = new FieldDescriptor(k_material, "AffectsNormal", "");
        private static FieldDescriptor s_affectsNormalBlend = new FieldDescriptor(k_material, "AffectsNormalBlend", "");
        private static FieldDescriptor s_affectsMAOS = new FieldDescriptor(k_material, "AffectsAO", "");
        private static FieldDescriptor s_affectsEmission = new FieldDescriptor(k_material, "AffectsEmission", "");
        private static FieldDescriptor s_affectsDBuffer = new FieldDescriptor(k_material, "AffectsDBuffer", "");
        private static FieldDescriptor s_decalDefault = new FieldDescriptor(k_material, "DecalDefault", "");
        private static FieldDescriptor s_angleFade = new FieldDescriptor(k_material, "AngleFade", "");

        [System.Serializable]
        class DecalData
        {
            public bool affectsAlbedo = true;
            public bool affectsNormalBlend = true;
            public bool affectsNormal = true;
            public bool affectsMAOS;
            public bool affectsEmission;
            public int drawOrder;
            public bool supportLodCrossFade;
            public bool angleFade;
        }

        [SerializeField]
        DecalData _decalData;

        [SerializeField]
        private DecalData decalData
        {
            get
            {
                if (_decalData == null)
                    _decalData = new DecalData();
                return _decalData;
            }
        }

        public UltraRPDecalSubTarget()
        {
            displayName = "Decal";
        }

        public override bool IsActive() => true;

        protected override ShaderID shaderID => ShaderID.SG_Decal;

        public override void Setup(ref TargetSetupContext context)
        {
            base.Setup(ref context);
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);
            context.AddCustomEditorForRenderPipeline("FraxinusEditor.Rendering.Ultra.DecalShaderGraphGUI", typeof(UltraRenderPipelineAsset));

            {
                SubShaderDescriptor subShader = SubShaders.Decal(decalData.supportLodCrossFade ? $"{UnityEditor.ShaderGraph.DisableBatching.LODFading}" : $"{UnityEditor.ShaderGraph.DisableBatching.False}");

                var passes = new PassCollection();
                foreach (var pass in subShader.passes)
                {
                    var passDescriptor = pass.descriptor;

                    CollectPassRenderState(ref passDescriptor);

                    passes.Add(passDescriptor, pass.fieldConditions);
                }
                subShader.passes = passes;

                context.AddSubShader(subShader);
            }
        }

        private void CollectPassRenderState(ref PassDescriptor pass)
        {
            if (pass.lightMode == DecalShaderPassNames.decalGBufferProjector ||
                pass.lightMode == DecalShaderPassNames.decalGBufferMesh)
            {
                // Make copy to avoid overwriting static
                pass.renderStates = new RenderStateCollection() { pass.renderStates };

                if (decalData.affectsAlbedo)
                    pass.renderStates.Add(RenderState.ColorMask("ColorMask RGB"));
                else
                    pass.renderStates.Add(RenderState.ColorMask("ColorMask 0"));

                pass.renderStates.Add(RenderState.ColorMask("ColorMask 0 1"));

                if (decalData.affectsNormal)
                    pass.renderStates.Add(RenderState.ColorMask("ColorMask RGB 2"));
                else
                    pass.renderStates.Add(RenderState.ColorMask("ColorMask 0 2"));

                // GI needs it unconditionaly
                pass.renderStates.Add(RenderState.ColorMask("ColorMask RGB 3"));
            }

            if (pass.lightMode == DecalShaderPassNames.dBufferProjector ||
                pass.lightMode == DecalShaderPassNames.dBufferMesh)
            {
                // Make copy to avoid overwriting static
                pass.renderStates = new RenderStateCollection() { pass.renderStates };

                if (decalData.affectsAlbedo)
                    pass.renderStates.Add(DecalColorMasks.s_colorMaskRGBA0);
                else
                    pass.renderStates.Add(DecalColorMasks.s_colorMaskNone0);

                if (decalData.affectsNormal)
                    pass.renderStates.Add(DecalColorMasks.s_colorMaskRGBA1);
                else
                    pass.renderStates.Add(DecalColorMasks.s_colorMaskNone1);

                if (decalData.affectsMAOS)
                    pass.renderStates.Add(DecalColorMasks.s_colorMaskRGBA2);
                else
                    pass.renderStates.Add(DecalColorMasks.s_colorMaskNone2);
            }
        }

        public override void CollectShaderProperties(PropertyCollector collector, GenerationMode generationMode)
        {
            Vector1ShaderProperty drawOrder = new Vector1ShaderProperty();
            drawOrder.overrideReferenceName = "_DrawOrder";
            drawOrder.displayName = "Draw Order";
            drawOrder.floatType = FloatType.Slider;
            drawOrder.rangeValues = new Vector2(-50, 50);
            drawOrder.hidden = true;
            drawOrder.value = 0;
            collector.AddShaderProperty(drawOrder);

            collector.AddShaderProperty(new Vector1ShaderProperty
            {
                overrideReferenceName = "_DecalMeshBiasType",
                displayName = "DecalMesh BiasType",
                floatType = FloatType.Enum,
                value = (int)DecalMeshDepthBiasType.DepthBias,
                enumNames = { "Depth Bias", "View Bias" },
                enumValues = { (int)DecalMeshDepthBiasType.DepthBias, (int)DecalMeshDepthBiasType.ViewBias },
                hidden = true
            });

            Vector1ShaderProperty decalMeshDepthBias = new Vector1ShaderProperty();
            decalMeshDepthBias.overrideReferenceName = "_DecalMeshDepthBias";
            decalMeshDepthBias.displayName = "DecalMesh DepthBias";
            decalMeshDepthBias.hidden = true;
            decalMeshDepthBias.floatType = FloatType.Default;
            decalMeshDepthBias.value = 0;
            collector.AddShaderProperty(decalMeshDepthBias);

            Vector1ShaderProperty decalMeshViewBias = new Vector1ShaderProperty();
            decalMeshViewBias.overrideReferenceName = "_DecalMeshViewBias";
            decalMeshViewBias.displayName = "DecalMesh ViewBias";
            decalMeshViewBias.hidden = true;
            decalMeshViewBias.floatType = FloatType.Default;
            decalMeshViewBias.value = 0;
            collector.AddShaderProperty(decalMeshViewBias);

            if (decalData.angleFade)
            {
                Vector1ShaderProperty decalAngleFadeSupported = new Vector1ShaderProperty();
                decalAngleFadeSupported.overrideReferenceName = "_DecalAngleFadeSupported";
                decalAngleFadeSupported.displayName = "Decal Angle Fade Supported";
                decalAngleFadeSupported.hidden = true;
                decalAngleFadeSupported.floatType = FloatType.Default;
                decalAngleFadeSupported.value = 1;
                collector.AddShaderProperty(decalAngleFadeSupported);
            }
        }

        public override void GetFields(ref TargetFieldContext context)
        {
            // Decal properties
            context.AddField(s_affectsAlbedo, decalData.affectsAlbedo);
            context.AddField(s_affectsNormal, decalData.affectsNormal);
            context.AddField(s_affectsNormalBlend, decalData.affectsNormalBlend);
            context.AddField(s_affectsMAOS, decalData.affectsMAOS);
            context.AddField(s_affectsEmission, decalData.affectsEmission);
            context.AddField(s_affectsDBuffer, decalData.affectsAlbedo || decalData.affectsNormal || decalData.affectsMAOS);
            context.AddField(s_decalDefault, decalData.affectsAlbedo || decalData.affectsNormal || decalData.affectsMAOS || decalData.affectsEmission);
            context.AddField(Fields.LodCrossFade, decalData.supportLodCrossFade);
            context.AddField(s_angleFade, decalData.angleFade);
        }

        public override void GetActiveBlocks(ref TargetActiveBlockContext context)
        {
            // Vertex
            context.AddBlock(BlockFields.VertexDescription.Position);
            context.AddBlock(BlockFields.VertexDescription.Normal);
            context.AddBlock(BlockFields.VertexDescription.Tangent);

            // Decal
            context.AddBlock(BlockFields.SurfaceDescription.BaseColor, decalData.affectsAlbedo);
            context.AddBlock(BlockFields.SurfaceDescription.Alpha, decalData.affectsAlbedo || decalData.affectsEmission);
            context.AddBlock(BlockFields.SurfaceDescription.NormalTS, decalData.affectsNormal);
            context.AddBlock(UltraRPBlockFields.SurfaceDescription.NormalAlpha, decalData.affectsNormal && decalData.affectsNormalBlend);
            context.AddBlock(BlockFields.SurfaceDescription.Metallic, decalData.affectsMAOS);
            context.AddBlock(BlockFields.SurfaceDescription.Occlusion, decalData.affectsMAOS);
            context.AddBlock(BlockFields.SurfaceDescription.Smoothness, decalData.affectsMAOS);
            context.AddBlock(UltraRPBlockFields.SurfaceDescription.MAOSAlpha, decalData.affectsMAOS);
            context.AddBlock(BlockFields.SurfaceDescription.Emission, decalData.affectsEmission);
        }

        public override void GetPropertiesGUI(ref TargetPropertyGUIContext context, Action onChange, Action<String> registerUndo)
        {
            context.AddProperty("Affect BaseColor", new Toggle() { value = decalData.affectsAlbedo }, (evt) =>
            {
                if (Equals(decalData.affectsAlbedo, evt.newValue))
                    return;

                registerUndo("Change Affect BaseColor");
                decalData.affectsAlbedo = (bool)evt.newValue;
                onChange();
            });

            context.AddProperty("Affect Normal", new Toggle() { value = decalData.affectsNormal }, (evt) =>
            {
                if (Equals(decalData.affectsNormal, evt.newValue))
                    return;

                registerUndo("Change Affects Normal");
                decalData.affectsNormal = (bool)evt.newValue;
                onChange();
            });

            context.globalIndentLevel++;
            context.AddProperty("Blend", new Toggle() { value = decalData.affectsNormalBlend }, (evt) =>
            {
                if (Equals(decalData.affectsNormalBlend, evt.newValue))
                    return;

                registerUndo("Change Affects Normal Blend");
                decalData.affectsNormalBlend = (bool)evt.newValue;
                onChange();
            });
            context.globalIndentLevel--;

            context.AddProperty("Affect MAOS", new Toggle() { value = decalData.affectsMAOS }, (evt) =>
            {
                if (Equals(decalData.affectsMAOS, evt.newValue))
                    return;

                registerUndo("Change Affect MAOS");
                decalData.affectsMAOS = (bool)evt.newValue;
                onChange();
            });

            context.AddProperty("Affect Emission", new Toggle() { value = decalData.affectsEmission }, (evt) =>
            {
                if (Equals(decalData.affectsEmission, evt.newValue))
                    return;

                registerUndo("Change Affect Emission");
                decalData.affectsEmission = (bool)evt.newValue;
                onChange();
            });

            context.AddProperty("Supports LOD Cross Fade", new Toggle() { value = decalData.supportLodCrossFade }, (evt) =>
            {
                if (Equals(decalData.supportLodCrossFade, evt.newValue))
                    return;

                registerUndo("Change Supports LOD Cross Fade");
                decalData.supportLodCrossFade = (bool)evt.newValue;
                onChange();
            });

            context.AddProperty("Angle Fade", new Toggle() { value = decalData.angleFade }, (evt) =>
            {
                if (Equals(decalData.angleFade, evt.newValue))
                    return;

                registerUndo("Change Angle Fade");
                decalData.angleFade = (bool)evt.newValue;
                onChange();
            });
        }

        #region SubShader
        static class SubShaders
        {
            public static SubShaderDescriptor Decal(string disableBatchingTag)
            {
                return new SubShaderDescriptor()
                {
                    pipelineTag = UltraRPTarget.k_pipelineTag,
                    customTags = "\"PreviewType\"=\"Plane\"",
                    disableBatchingTag = disableBatchingTag,
                    generatesPreview = true,
                    passes = new PassCollection
                    {
                        { DecalPasses.s_dBufferProjector, new FieldCondition(s_affectsDBuffer, true) },
                        { DecalPasses.s_forwardEmissiveProjector, new FieldCondition(s_affectsEmission, true) },
                        { DecalPasses.s_screenSpaceProjector, new FieldCondition(s_decalDefault, true) },
                        { DecalPasses.s_gBufferProjector, new FieldCondition(s_decalDefault, true) },
                        { DecalPasses.s_dBufferMesh, new FieldCondition(s_affectsDBuffer, true) },
                        { DecalPasses.s_forwardEmissiveMesh, new FieldCondition(s_affectsEmission, true) },
                        { DecalPasses.s_screenSpaceMesh, new FieldCondition(s_decalDefault, true) },
                        { DecalPasses.s_gBufferMesh, new FieldCondition(s_decalDefault, true) },
                        { DecalPasses.s_scenePicking, new FieldCondition(s_decalDefault, true) },
                    },
                };
            }
        }
        #endregion

        #region Passes
        static class DecalPasses
        {
            public static PassDescriptor s_scenePicking = GetScenePicking(DecalPragmas.s_instanced);
            public static PassDescriptor GetScenePicking(PragmaCollection pragma)
            {
                return new PassDescriptor
                {
                    // Definition
                    displayName = "ScenePickingPass",
                    referenceName = "SHADERPASS_DEPTHONLY",
                    lightMode = "Picking",
                    useInPreview = false,

                    // Template
                    passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                    sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                    // Collections
                    renderStates = DecalRenderStates.s_scenePicking,
                    pragmas = pragma,
                    defines = DecalDefines.s_scenePicking,
                    includes = DecalIncludes.s_scenePicking,

                    structs = UltraRPCoreStructCollections.Default,
                    fieldDependencies = UltraRPCoreFieldDependencies.Default,
                };
            }

            public static PassDescriptor s_dBufferProjector = new PassDescriptor()
            {
                // Definition
                displayName = DecalShaderPassNames.dBufferProjector,
                referenceName = "SHADERPASS_DBUFFER_PROJECTOR",
                lightMode = DecalShaderPassNames.dBufferProjector,
                useInPreview = false,

                // Template
                passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                // Port mask
                validPixelBlocks = DecalBlockMasks.s_fragmentWithoutEmessive,

                //Fields
                structs = UltraRPCoreStructCollections.Default,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,
                renderStates = DecalRenderStates.s_dBufferProjector,
                pragmas = DecalPragmas.s_multipleRenderTargets,
                keywords = DecalKeywords.s_dBufferProjector,
                defines = DecalDefines.s_projector,
                includes = DecalIncludes.s_dBuffer,
            };

            public static PassDescriptor s_forwardEmissiveProjector = new PassDescriptor()
            {
                // Definition
                displayName = DecalShaderPassNames.decalProjectorForwardEmissive,
                referenceName = "SHADERPASS_FORWARD_EMISSIVE_PROJECTOR",
                lightMode = DecalShaderPassNames.decalProjectorForwardEmissive,
                useInPreview = false,

                // Template
                passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                // Port mask
                validPixelBlocks = DecalBlockMasks.s_forwardOnlyEmissive,

                //Fields
                structs = UltraRPCoreStructCollections.Default,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                // Conditional State
                renderStates = DecalRenderStates.s_forwardEmissiveProjector,
                pragmas = DecalPragmas.s_multipleRenderTargets,
                defines = DecalDefines.s_projectorWithEmission,
                keywords = DecalKeywords.s_forwardEmissiveProjector,
                includes = DecalIncludes.s_dBuffer,
            };

            public static PassDescriptor s_screenSpaceProjector = GetScreenSpaceProjector(DecalPragmas.s_screenSpace, DecalKeywords.s_screenSpaceProjector);

            public static PassDescriptor GetScreenSpaceProjector(PragmaCollection pragma, KeywordCollection keywords)
            {
                return new PassDescriptor
                {
                    // Definition
                    displayName = DecalShaderPassNames.decalScreenSpaceProjector,
                    referenceName = "SHADERPASS_DECAL_SCREEN_SPACE_PROJECTOR",
                    lightMode = DecalShaderPassNames.decalScreenSpaceProjector,
                    useInPreview = false,

                    // Template
                    passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                    sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                    // Port mask
                    validPixelBlocks = DecalBlockMasks.s_fragment,

                    //Fields
                    structs = UltraRPCoreStructCollections.Default,
                    requiredFields = DecalRequiredFields.s_screenSpaceProjector,
                    fieldDependencies = UltraRPCoreFieldDependencies.Default,

                    renderStates = DecalRenderStates.s_screenSpaceProjector,
                    pragmas = pragma,
                    defines = DecalDefines.s_projectorWithEmission,
                    keywords = keywords,
                    includes = DecalIncludes.s_screenSpace,
                };
            }

            public static PassDescriptor s_gBufferProjector = new PassDescriptor()
            {
                // Definition
                displayName = DecalShaderPassNames.decalGBufferProjector,
                referenceName = "SHADERPASS_DECAL_GBUFFER_PROJECTOR",
                lightMode = DecalShaderPassNames.decalGBufferProjector,
                useInPreview = false,

                // Template
                passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                // Port mask
                validPixelBlocks = DecalBlockMasks.s_fragment,

                //Fields
                structs = UltraRPCoreStructCollections.Default,
                requiredFields = DecalRequiredFields.s_gBufferProjector,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                renderStates = DecalRenderStates.s_gBufferProjector,
                pragmas = DecalPragmas.s_gBuffer,
                defines = DecalDefines.s_projectorWithEmission,
                keywords = DecalKeywords.s_gBufferProjector,
                includes = DecalIncludes.s_gBuffer,
            };

            public static PassDescriptor s_dBufferMesh = new PassDescriptor()
            {
                // Definition
                displayName = DecalShaderPassNames.dBufferMesh,
                referenceName = "SHADERPASS_DBUFFER_MESH",
                lightMode = DecalShaderPassNames.dBufferMesh,
                useInPreview = false,

                // Template
                passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                // Port mask
                validPixelBlocks = DecalBlockMasks.s_fragmentWithoutEmessive,

                //Fields
                structs = UltraRPCoreStructCollections.Default,
                requiredFields = DecalRequiredFields.s_mesh,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                // Conditional State
                renderStates = DecalRenderStates.s_dBufferMesh,
                pragmas = DecalPragmas.s_multipleRenderTargets,
                defines = DecalDefines.s_mesh,
                keywords = DecalKeywords.s_dBufferMesh,
                includes = DecalIncludes.s_dBuffer,
            };

            public static PassDescriptor s_forwardEmissiveMesh = new PassDescriptor()
            {
                // Definition
                displayName = DecalShaderPassNames.decalMeshForwardEmissive,
                referenceName = "SHADERPASS_FORWARD_EMISSIVE_MESH",
                lightMode = DecalShaderPassNames.decalMeshForwardEmissive,
                useInPreview = false,

                // Template
                passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                // Port mask
                validPixelBlocks = DecalBlockMasks.s_fragment,

                //Fields
                structs = UltraRPCoreStructCollections.Default,
                requiredFields = DecalRequiredFields.s_mesh,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                // Conditional State
                renderStates = DecalRenderStates.s_forwardEmissiveMesh,
                pragmas = DecalPragmas.s_multipleRenderTargets,
                defines = DecalDefines.s_meshWithEmission,
                keywords = DecalKeywords.s_forwardEmissiveProjector,
                includes = DecalIncludes.s_dBuffer,
            };

            public static PassDescriptor s_screenSpaceMesh = GetScreenSpaceMesh(DecalPragmas.s_screenSpace, DecalKeywords.s_screenSpaceMesh);

            public static PassDescriptor GetScreenSpaceMesh(PragmaCollection pragma, KeywordCollection keywords)
            {
                return new PassDescriptor
                {
                    // Definition
                    displayName = DecalShaderPassNames.decalScreenSpaceMesh,
                    referenceName = "SHADERPASS_DECAL_SCREEN_SPACE_MESH",
                    lightMode = DecalShaderPassNames.decalScreenSpaceMesh,
                    useInPreview = true,

                    // Template
                    passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                    sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                    // Port mask
                    validPixelBlocks = DecalBlockMasks.s_fragment,

                    //Fields
                    structs = UltraRPCoreStructCollections.Default,
                    requiredFields = DecalRequiredFields.s_screenSpaceMesh,
                    fieldDependencies = UltraRPCoreFieldDependencies.Default,

                    renderStates = DecalRenderStates.s_screenSpaceMesh,
                    pragmas = pragma,
                    defines = DecalDefines.s_meshWithEmission,
                    keywords = keywords,
                    includes = DecalIncludes.s_screenSpace,
                };
            }

            public static PassDescriptor s_gBufferMesh = new PassDescriptor()
            {
                // Definition
                displayName = DecalShaderPassNames.decalGBufferMesh,
                referenceName = "SHADERPASS_DECAL_GBUFFER_MESH",
                lightMode = DecalShaderPassNames.decalGBufferMesh,
                useInPreview = false,

                // Template
                passTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Decal/DecalPass.template",
                sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories(),

                // Port mask
                validPixelBlocks = DecalBlockMasks.s_fragment,

                //Fields
                structs = UltraRPCoreStructCollections.Default,
                requiredFields = DecalRequiredFields.s_screenSpaceMesh,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                renderStates = DecalRenderStates.s_gBufferMesh,
                pragmas = DecalPragmas.s_gBuffer,
                defines = DecalDefines.s_meshWithEmission,
                keywords = DecalKeywords.s_gBufferMesh,
                includes = DecalIncludes.s_gBuffer,
            };
        }
        #endregion

        #region PortMasks
        static class DecalBlockMasks
        {
            public static BlockFieldDescriptor[] s_fragmentWithoutEmessive = new BlockFieldDescriptor[]
            {
                BlockFields.SurfaceDescription.BaseColor,
                BlockFields.SurfaceDescription.Alpha,
                BlockFields.SurfaceDescription.NormalTS,
                UltraRPBlockFields.SurfaceDescription.NormalAlpha,
                BlockFields.SurfaceDescription.Metallic,
                BlockFields.SurfaceDescription.Occlusion,
                BlockFields.SurfaceDescription.Smoothness,
                UltraRPBlockFields.SurfaceDescription.MAOSAlpha,
            };

            public static BlockFieldDescriptor[] s_forwardOnlyEmissive = new BlockFieldDescriptor[]
            {
                BlockFields.SurfaceDescription.Alpha,
                BlockFields.SurfaceDescription.Emission,
            };

            public static BlockFieldDescriptor[] s_fragment = new BlockFieldDescriptor[]
            {
                BlockFields.SurfaceDescription.BaseColor,
                BlockFields.SurfaceDescription.Alpha,
                BlockFields.SurfaceDescription.NormalTS,
                UltraRPBlockFields.SurfaceDescription.NormalAlpha,
                BlockFields.SurfaceDescription.Metallic,
                BlockFields.SurfaceDescription.Occlusion,
                BlockFields.SurfaceDescription.Smoothness,

                UltraRPBlockFields.SurfaceDescription.MAOSAlpha,
                BlockFields.SurfaceDescription.Emission,
            };
        }
        #endregion

        #region RequiredFields
        static class DecalRequiredFields
        {
            public static FieldCollection s_mesh = new FieldCollection()
            {
                StructFields.Attributes.normalOS,
                StructFields.Attributes.tangentOS,
                StructFields.Attributes.uv1,
                StructFields.Attributes.uv2,
                StructFields.Varyings.normalWS,
                StructFields.Varyings.tangentWS,
                StructFields.Varyings.positionWS,
                StructFields.Varyings.texCoord0,
            };

            public static FieldCollection s_screenSpaceProjector = new FieldCollection()
            {
                StructFields.Varyings.normalWS,
                UltraRPStructFields.Varyings.staticLightmapUV,
                UltraRPStructFields.Varyings.dynamicLightmapUV,
                UltraRPStructFields.Varyings.sh,
                UltraRPStructFields.Varyings.fogFactorAndVertexLight,
                UltraRPStructFields.Varyings.shadowCoord,
            };

            public static FieldCollection s_gBufferProjector = new FieldCollection()
            {
                StructFields.Varyings.normalWS,
                UltraRPStructFields.Varyings.staticLightmapUV,
                UltraRPStructFields.Varyings.dynamicLightmapUV,
                UltraRPStructFields.Varyings.sh,
                UltraRPStructFields.Varyings.shadowCoord,
            };

            public static FieldCollection s_screenSpaceMesh = new FieldCollection()
            {
                StructFields.Attributes.normalOS,
                StructFields.Attributes.tangentOS,
                StructFields.Attributes.uv1,
                StructFields.Attributes.uv2,
                StructFields.Varyings.normalWS,
                StructFields.Varyings.tangentWS,
                StructFields.Varyings.positionWS,
                StructFields.Varyings.texCoord0,
                UltraRPStructFields.Varyings.staticLightmapUV,
                UltraRPStructFields.Varyings.dynamicLightmapUV,
                UltraRPStructFields.Varyings.sh,
                UltraRPStructFields.Varyings.fogFactorAndVertexLight,
                UltraRPStructFields.Varyings.shadowCoord,
            };
        }
        #endregion

        #region ColorMask
        static class DecalColorMasks
        {
            public static RenderStateDescriptor s_colorMaskRGBA0 = RenderState.ColorMask("ColorMask RGBA");
            public static RenderStateDescriptor s_colorMaskNone0 = RenderState.ColorMask("ColorMask 0");
            public static RenderStateDescriptor s_colorMaskRGBA1 = RenderState.ColorMask("ColorMask RGBA 1");
            public static RenderStateDescriptor s_colorMaskNone1 = RenderState.ColorMask("ColorMask 0 1");
            public static RenderStateDescriptor s_colorMaskRGBA2 = RenderState.ColorMask("ColorMask RGBA 2");
            public static RenderStateDescriptor s_colorMaskNone2 = RenderState.ColorMask("ColorMask 0 2");
        }
        #endregion

        #region RenderStates
        static class DecalRenderStates
        {
            private readonly static string[] s_dBufferBlends = new string[]
            {
                "Blend 0 SrcAlpha OneMinusSrcAlpha, Zero OneMinusSrcAlpha",
                "Blend 1 SrcAlpha OneMinusSrcAlpha, Zero OneMinusSrcAlpha",
                "Blend 2 SrcAlpha OneMinusSrcAlpha, Zero OneMinusSrcAlpha",
            };
            private readonly static string[] s_gBufferBlends = new string[]
            {
                "Blend 0 SrcAlpha OneMinusSrcAlpha",
                "Blend 1 SrcAlpha OneMinusSrcAlpha",
                "Blend 2 SrcAlpha OneMinusSrcAlpha",
                "Blend 3 SrcAlpha OneMinusSrcAlpha",
            };

            public static RenderStateCollection s_scenePicking = new RenderStateCollection
            {
                { RenderState.Cull(Cull.Back) },
            };

            public static RenderStateCollection s_dBufferProjector = new RenderStateCollection
            {
                { RenderState.Blend(s_dBufferBlends[0]) },
                { RenderState.Blend(s_dBufferBlends[1]) },
                { RenderState.Blend(s_dBufferBlends[2]) },
                { RenderState.Cull(Cull.Front) },
                { RenderState.ZTest(ZTest.Greater) },
                { RenderState.ZWrite(ZWrite.Off) },
            };

            public static RenderStateCollection s_forwardEmissiveProjector = new RenderStateCollection
            {
                { RenderState.Blend("Blend 0 SrcAlpha One") },
                { RenderState.Cull(Cull.Front) },
                { RenderState.ZTest(ZTest.Greater) },
                { RenderState.ZWrite(ZWrite.Off) },
            };

            public static RenderStateCollection s_screenSpaceProjector = new RenderStateCollection
            {
                { RenderState.Blend("Blend SrcAlpha OneMinusSrcAlpha") },
                { RenderState.Cull(Cull.Front) },
                { RenderState.ZTest(ZTest.Greater) },
                { RenderState.ZWrite(ZWrite.Off) },
            };

            public static RenderStateCollection s_gBufferProjector = new RenderStateCollection
            {
                { RenderState.Blend(s_gBufferBlends[0]) },
                { RenderState.Blend(s_gBufferBlends[1]) },
                { RenderState.Blend(s_gBufferBlends[2]) },
                { RenderState.Blend(s_gBufferBlends[3]) },
                { RenderState.Cull(Cull.Front) },
                { RenderState.ZTest(ZTest.Greater) },
                { RenderState.ZWrite(ZWrite.Off) },
            };

            public static RenderStateCollection s_dBufferMesh = new RenderStateCollection
            {
                { RenderState.Blend(s_dBufferBlends[0]) },
                { RenderState.Blend(s_dBufferBlends[1]) },
                { RenderState.Blend(s_dBufferBlends[2]) },
                { RenderState.ZTest(ZTest.LEqual) },
                { RenderState.ZWrite(ZWrite.Off) },
            };

            public static RenderStateCollection s_forwardEmissiveMesh = new RenderStateCollection
            {
                { RenderState.Blend("Blend 0 SrcAlpha One") },
                { RenderState.ZTest(ZTest.LEqual) },
                { RenderState.ZWrite(ZWrite.Off) },
            };

            public static RenderStateCollection s_screenSpaceMesh = new RenderStateCollection
            {
                { RenderState.Blend("Blend SrcAlpha OneMinusSrcAlpha") },
                { RenderState.ZTest(ZTest.LEqual) },
                { RenderState.ZWrite(ZWrite.Off) },
            };

            public static RenderStateCollection s_gBufferMesh = new RenderStateCollection
            {
                { RenderState.Blend(s_gBufferBlends[0]) },
                { RenderState.Blend(s_gBufferBlends[1]) },
                { RenderState.Blend(s_gBufferBlends[2]) },
                { RenderState.Blend(s_gBufferBlends[3]) },
                { RenderState.ZWrite(ZWrite.Off) },
            };
        }
        #endregion

        #region Pragmas
        static class DecalPragmas
        {
            public static PragmaCollection s_screenSpace = new PragmaCollection
            {
                { Pragma.Target(ShaderModel.Target25) },
                { Pragma.Vertex("Vert") },
                { Pragma.Fragment("Frag") },
                { Pragma.MultiCompileInstancing },
                { Pragma.MultiCompileFog },
                { Pragma.EditorSyncCompilation },
            };

            public static PragmaCollection s_gBuffer = new PragmaCollection
            {
                { Pragma.Target(ShaderModel.Target45) }, // MRT4
                { Pragma.ExcludeRenderers(new[] { Platform.GLES3, Platform.GLCore }) },
                { Pragma.Vertex("Vert") },
                { Pragma.Fragment("Frag") },
                { Pragma.MultiCompileInstancing },
                { Pragma.MultiCompileFog },
                { Pragma.EditorSyncCompilation },
            };

            public static PragmaCollection s_multipleRenderTargets = new PragmaCollection
            {
                { Pragma.Target(ShaderModel.Target45) }, // MRT4
                { Pragma.ExcludeRenderers(new[] { Platform.GLES3, Platform.GLCore }) },
                { Pragma.Vertex("Vert") },
                { Pragma.Fragment("Frag") },
                { Pragma.MultiCompileInstancing },
                { Pragma.EditorSyncCompilation },
            };

            public static readonly PragmaCollection s_instanced = new PragmaCollection
            {
                { Pragma.Target(ShaderModel.Target20) },
                { Pragma.MultiCompileInstancing },
                { Pragma.EditorSyncCompilation },
                { Pragma.Vertex("Vert") },
                { Pragma.Fragment("Frag") },
            };
        }
        #endregion

        #region Defines
        static class DecalDefines
        {
            private static class Descriptors
            {
                public static KeywordDescriptor affectsAlbedo = new KeywordDescriptor()
                {
                    displayName = "Affects Albedo",
                    referenceName = "_MATERIAL_AFFECTS_ALBEDO",
                    type = KeywordType.Boolean,
                };

                public static KeywordDescriptor affectsNormal = new KeywordDescriptor()
                {
                    displayName = "Affects Normal",
                    referenceName = "_MATERIAL_AFFECTS_NORMAL",
                    type = KeywordType.Boolean,
                };

                public static KeywordDescriptor affectsNormalBlend = new KeywordDescriptor()
                {
                    displayName = "Affects Normal Blend",
                    referenceName = "_MATERIAL_AFFECTS_NORMAL_BLEND",
                    type = KeywordType.Boolean,
                };

                public static KeywordDescriptor affectsMAOS = new KeywordDescriptor()
                {
                    displayName = "Affects Metal",
                    referenceName = "_MATERIAL_AFFECTS_MAOS",
                    type = KeywordType.Boolean,
                };

                public static KeywordDescriptor affectsEmission = new KeywordDescriptor()
                {
                    displayName = "Affects Emission",
                    referenceName = "_MATERIAL_AFFECTS_EMISSION",
                    type = KeywordType.Boolean,
                };

                public static KeywordDescriptor angleFade = new KeywordDescriptor()
                {
                    displayName = "Angle Fade",
                    referenceName = "DECAL_ANGLE_FADE",
                    type = KeywordType.Boolean,
                };

                public static KeywordDescriptor scenePickingPass = new KeywordDescriptor()
                {
                    displayName = "Scene Picking Pass",
                    referenceName = "SCENEPICKINGPASS",
                    type = KeywordType.Boolean,
                };
            }

            public static DefineCollection s_projector = new DefineCollection
            {
                { Descriptors.affectsAlbedo, 1, new FieldCondition(s_affectsAlbedo, true) },
                { Descriptors.affectsNormal, 1, new FieldCondition(s_affectsNormal, true) },
                { Descriptors.affectsNormalBlend, 1, new FieldCondition(s_affectsNormalBlend, true) },
                { Descriptors.affectsMAOS, 1, new FieldCondition(s_affectsMAOS, true) },
                { Descriptors.angleFade, 1, new FieldCondition(s_angleFade, true) }
            };

            public static DefineCollection s_projectorEmission = new DefineCollection
            {
                { Descriptors.angleFade, 1, new FieldCondition(s_angleFade, true) },
                { Descriptors.affectsEmission, 1, new FieldCondition(s_affectsEmission, true) },
            };

            public static DefineCollection s_projectorWithEmission = new DefineCollection
            {
                { s_projector },
                { Descriptors.affectsEmission, 1, new FieldCondition(s_affectsEmission, true) },
            };

            public static DefineCollection s_mesh = new DefineCollection
            {
                { Descriptors.affectsAlbedo, 1, new FieldCondition(s_affectsAlbedo, true) },
                { Descriptors.affectsNormal, 1, new FieldCondition(s_affectsNormal, true) },
                { Descriptors.affectsNormalBlend, 1, new FieldCondition(s_affectsNormalBlend, true) },
                { Descriptors.affectsMAOS, 1, new FieldCondition(s_affectsMAOS, true) },
                { UltraRPCoreKeywordDescriptors.UseUnityCrossFade, 1, new FieldCondition(Fields.LodCrossFade, true) }
            };

            public static DefineCollection s_meshEmission = new DefineCollection
            {
                { Descriptors.affectsEmission, 1, new FieldCondition(s_affectsEmission, true) },
            };

            public static DefineCollection s_meshWithEmission = new DefineCollection
            {
                { s_mesh },
                { Descriptors.affectsEmission, 1, new FieldCondition(s_affectsEmission, true) },
            };

            public static DefineCollection s_scenePicking = new DefineCollection { { Descriptors.scenePickingPass, 1 }, };
        }
        #endregion

        #region Keywords
        static class DecalKeywords
        {
            static class Descriptors
            {
                public static KeywordDescriptor decalsNormalBlend = new KeywordDescriptor()
                {
                    displayName = "Decal Normal Blend",
                    referenceName = "_DECAL_NORMAL_BLEND",
                    type = KeywordType.Enum,
                    definition = KeywordDefinition.MultiCompile,
                    scope = KeywordScope.Global,
                    entries = new KeywordEntry[]
                    {
                        new KeywordEntry() { displayName = "LOW", referenceName = "LOW" },
                        new KeywordEntry() { displayName = "MEDIUM", referenceName = "MEDIUM" },
                        new KeywordEntry() { displayName = "HIGH", referenceName = "HIGH" },
                    }
                };

                public static readonly KeywordDescriptor decalLayers = new KeywordDescriptor()
                {
                    displayName = "Decal Layers",
                    referenceName = "_DECAL_LAYERS",
                    type = KeywordType.Boolean,
                    definition = KeywordDefinition.MultiCompile,
                    scope = KeywordScope.Global,
                };
            }

            public static KeywordCollection s_dBufferMesh = new KeywordCollection
            {
                { UltraRPCoreKeywordDescriptors.DBuffer },
                { UltraRPCoreKeywordDescriptors.LODFadeCrossFade, new FieldCondition(Fields.LodCrossFade, true) },
                { Descriptors.decalLayers },
            };

            public static KeywordCollection s_dBufferProjector = new KeywordCollection
            {
                { UltraRPCoreKeywordDescriptors.DBuffer },
                { Descriptors.decalLayers },
            };

            public static KeywordCollection s_forwardEmissiveProjector = new KeywordCollection
            {
                { Descriptors.decalLayers },
            };

            public static readonly KeywordCollection s_screenSpaceMeshGl = new KeywordCollection
            {
                { UltraRPCoreKeywordDescriptors.StaticLightmap },
                { UltraRPCoreKeywordDescriptors.DynamicLightmap },
                { UltraRPCoreKeywordDescriptors.DirectionalLightmapCombined },
                { UltraRPCoreKeywordDescriptors.MainLightShadows },
                { UltraRPCoreKeywordDescriptors.AdditionalLights },
                { UltraRPCoreKeywordDescriptors.AdditionalLightShadows },
                { UltraRPCoreKeywordDescriptors.ShadowsSoft },
                { UltraRPCoreKeywordDescriptors.LightmapShadowMixing },
                { UltraRPCoreKeywordDescriptors.ShadowsShadowmask },
                { UltraRPCoreKeywordDescriptors.ForwardPlus },
                { Descriptors.decalsNormalBlend },
                { UltraRPCoreKeywordDescriptors.LODFadeCrossFade, new FieldCondition(Fields.LodCrossFade, true) },
                { UltraRPCoreKeywordDescriptors.DebugDisplay },
            };

            public static readonly KeywordCollection s_screenSpaceMesh = new KeywordCollection
            {
                { s_screenSpaceMeshGl },
                { Descriptors.decalLayers },

            };

            public static readonly KeywordCollection s_screenSpaceProjector = new KeywordCollection
            {
                { UltraRPCoreKeywordDescriptors.MainLightShadows },
                { UltraRPCoreKeywordDescriptors.AdditionalLights },
                { UltraRPCoreKeywordDescriptors.AdditionalLightShadows },
                { UltraRPCoreKeywordDescriptors.ShadowsSoft },
                { UltraRPCoreKeywordDescriptors.ForwardPlus },
                { UltraRPCoreKeywordDescriptors.LightCookies },
                { UltraRPCoreKeywordDescriptors.DebugDisplay },
                { Descriptors.decalsNormalBlend },
                { Descriptors.decalLayers },
            };

            public static readonly KeywordCollection s_gBufferMesh = new KeywordCollection
            {
                { UltraRPCoreKeywordDescriptors.StaticLightmap },
                { UltraRPCoreKeywordDescriptors.DynamicLightmap },
                { UltraRPCoreKeywordDescriptors.DirectionalLightmapCombined },
                { UltraRPCoreKeywordDescriptors.MainLightShadows },
                { UltraRPCoreKeywordDescriptors.ShadowsSoft },
                { UltraRPCoreKeywordDescriptors.LightmapShadowMixing },
                { UltraRPCoreKeywordDescriptors.MixedLightingSubtractive },
                { Descriptors.decalsNormalBlend },
                { Descriptors.decalLayers },
                { UltraRPCoreKeywordDescriptors.GBufferNormalsOct },
                { UltraRPCoreKeywordDescriptors.RenderPassEnabled },
                { UltraRPCoreKeywordDescriptors.LODFadeCrossFade, new FieldCondition(Fields.LodCrossFade, true) },
            };

            public static readonly KeywordCollection s_gBufferProjector = new KeywordCollection
            {
                { UltraRPCoreKeywordDescriptors.MainLightShadows },
                { UltraRPCoreKeywordDescriptors.ShadowsSoft },
                { Descriptors.decalsNormalBlend },
                { Descriptors.decalLayers },
                { UltraRPCoreKeywordDescriptors.GBufferNormalsOct },
                { UltraRPCoreKeywordDescriptors.RenderPassEnabled },
            };
        }
        #endregion

        #region Includes
        static class DecalIncludes
        {
            const string k_decalInput = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DecalInput.hlsl";
            const string k_shaderVariablesDecal = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/ShaderVariablesDecal.hlsl";
            const string k_passDecal = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/ShaderPassDecal.hlsl";
            const string k_shaderPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/ShaderPass.hlsl";
            const string k_varyings = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/Varyings.hlsl";
            const string k_dBuffer = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DBuffer.hlsl";
            const string k_gBuffer = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/UnityGBuffer.hlsl";
            const string k_lodCrossFade = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/LODCrossFade.hlsl";

            public static IncludeCollection s_decalPregraph = new IncludeCollection
            {
                { k_shaderPass, IncludeLocation.Pregraph },
                { k_decalInput, IncludeLocation.Pregraph },
                { k_shaderVariablesDecal, IncludeLocation.Pregraph },
            };

            public static IncludeCollection s_decalPostgraph = new IncludeCollection
            {
                { k_varyings, IncludeLocation.Postgraph },
                { k_passDecal, IncludeLocation.Postgraph },
            };

            public static IncludeCollection s_dBuffer = new IncludeCollection
            {
                // Pre-graph
                { UltraRPCoreIncludes.DOTSPregraph },
                { UltraRPCoreIncludes.CorePregraph },
                { UltraRPCoreIncludes.ShaderGraphPregraph },
                { s_decalPregraph },
                { k_dBuffer, IncludeLocation.Pregraph },
                { k_lodCrossFade, IncludeLocation.Pregraph, new FieldCondition(Fields.LodCrossFade, true) },

                // Post-graph
                { s_decalPostgraph },
            };

            public static IncludeCollection s_screenSpace = new IncludeCollection
            {
                // Pre-graph
                { UltraRPCoreIncludes.DOTSPregraph },
                { UltraRPCoreIncludes.CorePregraph },
                { UltraRPCoreIncludes.ShaderGraphPregraph },
                { s_decalPregraph },
                { k_lodCrossFade, IncludeLocation.Pregraph, new FieldCondition(Fields.LodCrossFade, true) },

                // Post-graph
                { s_decalPostgraph },
            };

            public static IncludeCollection s_gBuffer = new IncludeCollection
            {
                // Pre-graph
                { UltraRPCoreIncludes.DOTSPregraph },
                { UltraRPCoreIncludes.CorePregraph },
                { UltraRPCoreIncludes.ShaderGraphPregraph },
                { k_gBuffer, IncludeLocation.Pregraph },
                { s_decalPregraph },
                { k_lodCrossFade, IncludeLocation.Pregraph, new FieldCondition(Fields.LodCrossFade, true) },

                // Post-graph
                { s_decalPostgraph },
            };

            public static IncludeCollection s_scenePicking = new IncludeCollection
            {
                // Pre-graph
                { UltraRPCoreIncludes.CorePregraph },
                { UltraRPCoreIncludes.ShaderGraphPregraph },
                { s_decalPregraph },

                // Post-graph
                { s_decalPostgraph },
            };
        }
        #endregion
    }
}
