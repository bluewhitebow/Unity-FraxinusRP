using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;

using FraxinusEngine.Rendering.Ultra;
using static FraxinusEngine.Rendering.Ultra.ShaderUtils;

namespace FraxinusEditor.Rendering.Ultra
{
    sealed class UltraRPLitSubTarget : UltraRPSubTarget
    {
        static readonly GUID k_sourceCodeGuid = new GUID("abe7e5aed57f4364495f96cb03dcc9dd");

        public override int latestVersion => 1;

        [SerializeField]
        WorkflowMode _workflowMode = WorkflowMode.Metallic;

        [SerializeField]
        NormalDropOffSpace _normalDropOffSpace = NormalDropOffSpace.Tangent;

        [SerializeField]
        bool _clearCoat = false;

        [SerializeField]
        bool _blendModePreserveSpecular = true;

        public UltraRPLitSubTarget()
        {
            displayName = "Lit";
        }

        protected override ShaderID shaderID => ShaderID.SG_Lit;

        public WorkflowMode workflowMode
        {
            get => _workflowMode;
            set => _workflowMode = value;
        }

        public NormalDropOffSpace normalDropOffSpace
        {
            get => _normalDropOffSpace;
            set => _normalDropOffSpace = value;
        }

        public bool clearCoat
        {
            get => _clearCoat;
            set => _clearCoat = value;
        }

        private bool complexLit => clearCoat;

        public bool blendModePreserveSpecular
        {
            get => _blendModePreserveSpecular;
            set => _blendModePreserveSpecular = value;
        }

        public override bool IsActive() => true;

        protected override int ComputeMaterialNeedsUpdateHash()
        {
            unchecked { return base.ComputeMaterialNeedsUpdateHash() * 23 + workflowMode.GetHashCode(); }
        }

        public override void Setup(ref TargetSetupContext context)
        {
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);
            base.Setup(ref context);

            var ultraRPType = typeof(UltraRenderPipelineAsset);
            if (!context.HasCustomEditorForRenderPipeline(ultraRPType))
            {
                var gui = typeof(ShaderGraphLitGUI);
#if HAS_VFX_GRAPH
                if (TargetsVFX())
                    gui = typeof(UnityEditor.VFX.UltraRP.VFXShaderGraphLitGUI);
#endif
                context.AddCustomEditorForRenderPipeline(gui.FullName, ultraRPType);
            }

            context.AddSubShader(PostProcessSubShader(SubShaders.LitSubShader(target, workflowMode, blendModePreserveSpecular)));
        }

        public override void ProcessPreviewMaterial(Material material)
        {
            if (target.allowMaterialOverride)
            {
                material.SetFloat(UltraRPProperty.SpecularWorkflowMode, (float)workflowMode);
                material.SetFloat(UltraRPProperty.CastShadows, target.castShadows ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.ReceiveShadows, target.receiveShadows ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.SurfaceType, (float)target.surfaceType);
                material.SetFloat(UltraRPProperty.BlendMode, (float)target.alphaMode);
                material.SetFloat(UltraRPProperty.AlphaClip, target.alphaClip ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.CullMode, (int)target.renderFace);
                material.SetFloat(UltraRPProperty.ZWriteControl, (float)target.zWriteControl);
                material.SetFloat(UltraRPProperty.ZTest, (float)target.zTestMode);
            }

            material.SetFloat(UltraRPProperty.QueueOffset, 0.0f);
            material.SetFloat(UltraRPProperty.QueueControl, 0.0f);
            ShaderGraphLitGUI.UpdateMaterial(material, ShaderEditorUtils.MaterialUpdateType.CreatedNewMaterial);
        }

        public override void GetFields(ref TargetFieldContext context)
        {
            base.GetFields(ref context);

            var descs = context.blocks.Select(x => x.descriptor);

            context.AddField(UltraRPFields.NormalDropOffOS, normalDropOffSpace == NormalDropOffSpace.Object);
            context.AddField(UltraRPFields.NormalDropOffTS, normalDropOffSpace == NormalDropOffSpace.Tangent);
            context.AddField(UltraRPFields.NormalDropOffWS, normalDropOffSpace == NormalDropOffSpace.World);
            context.AddField(UltraRPFields.Normal, descs.Contains(BlockFields.SurfaceDescription.NormalOS) ||
                descs.Contains(BlockFields.SurfaceDescription.NormalTS) ||
                descs.Contains(BlockFields.SurfaceDescription.NormalWS));
            context.AddField(UltraRPFields.SpecularSetup, workflowMode == WorkflowMode.Specular);
        }

        public override void GetActiveBlocks(ref TargetActiveBlockContext context)
        {
            context.AddBlock(UltraRPBlockFields.VertexDescription.MotionVector, target.additionalMotionVectorMode == AdditionalMotionVectorMode.Custom);

            context.AddBlock(BlockFields.SurfaceDescription.Smoothness);
            context.AddBlock(BlockFields.SurfaceDescription.NormalOS, normalDropOffSpace == NormalDropOffSpace.Object);
            context.AddBlock(BlockFields.SurfaceDescription.NormalTS, normalDropOffSpace == NormalDropOffSpace.Tangent);
            context.AddBlock(BlockFields.SurfaceDescription.NormalWS, normalDropOffSpace == NormalDropOffSpace.World);
            context.AddBlock(BlockFields.SurfaceDescription.Emission);
            context.AddBlock(BlockFields.SurfaceDescription.Occlusion);

            context.AddBlock(BlockFields.SurfaceDescription.Specular, workflowMode == WorkflowMode.Specular || target.allowMaterialOverride);
            context.AddBlock(BlockFields.SurfaceDescription.Metallic, workflowMode == WorkflowMode.Metallic || target.allowMaterialOverride);

            context.AddBlock(BlockFields.SurfaceDescription.Alpha);
            context.AddBlock(BlockFields.SurfaceDescription.AlphaClipThreshold, target.alphaClip || target.allowMaterialOverride);
        }

        public override void CollectShaderProperties(PropertyCollector collector, GenerationMode generationMode)
        {
            if (target.allowMaterialOverride)
            {
                collector.AddFloatProperty(UltraRPProperty.SpecularWorkflowMode, (float)workflowMode);
                collector.AddFloatProperty(UltraRPProperty.SurfaceType, (float)target.surfaceType);
                collector.AddFloatProperty(UltraRPProperty.BlendMode, (float)target.alphaMode);
                collector.AddFloatProperty(UltraRPProperty.AlphaClip, target.alphaClip ? 1.0f : 0.0f);
                collector.AddFloatProperty(UltraRPProperty.SrcBlend, 1.0f);
                collector.AddFloatProperty(UltraRPProperty.DstBlend, 0.0f);
                collector.AddFloatProperty(UltraRPProperty.SrcBlendAlpha, 1.0f);
                collector.AddFloatProperty(UltraRPProperty.DstBlendAlpha, 0.0f);
                collector.AddFloatProperty(UltraRPProperty.ZWriteControl, (float)target.zWriteControl);
                collector.AddFloatProperty(UltraRPProperty.ZWrite, target.surfaceType == SurfaceType.Opaque ? 1.0f : 0.0f);
                collector.AddFloatProperty(UltraRPProperty.ZTest, (float)target.zTestMode);
                collector.AddFloatProperty(UltraRPProperty.CullMode, (float)target.renderFace);
                collector.AddFloatProperty(UltraRPProperty.CastShadows, target.castShadows ? 1.0f : 0.0f);
                collector.AddFloatProperty(UltraRPProperty.ReceiveShadows, target.receiveShadows ? 1.0f : 0.0f);
            }

            collector.AddFloatProperty(UltraRPProperty.QueueOffset, 0.0f);
            collector.AddFloatProperty(UltraRPProperty.QueueControl, -1.0f);
        }

        public override void GetPropertiesGUI(ref TargetPropertyGUIContext context, Action onChange, Action<String> registerUndo)
        {
            target.AddDefaultMaterialOverrideGUI(ref context, onChange, registerUndo);
            target.AddDefaultSurfacePropertiesGUI(ref context, onChange, registerUndo, showReceiveShadows: true);

            context.AddProperty("Workflow Mode", new EnumField(WorkflowMode.Metallic) { value = workflowMode }, (evt) =>
            {
                if (Equals(workflowMode, evt.newValue))
                    return;

                registerUndo("Change Workflow Mode");
                workflowMode = (WorkflowMode)evt.newValue;
                onChange();
            });

            context.AddProperty("Normal Drop Off Space", new EnumField(NormalDropOffSpace.Tangent) { value = normalDropOffSpace }, (evt) =>
            {
                if (Equals(normalDropOffSpace, evt.newValue))
                    return;

                registerUndo("Change Normal Drop Off Space");
                normalDropOffSpace = (NormalDropOffSpace)evt.newValue;
                onChange();
            });
        }

        #region SubShader
        static class SubShaders
        {
            public static SubShaderDescriptor LitSubShader(UltraRPTarget target, WorkflowMode workflowMode, bool blendModePreserveSpecular)
            {
                SubShaderDescriptor result = new SubShaderDescriptor()
                {
                    pipelineTag = UltraRPTarget.k_pipelineTag,
                    customTags = UltraRPTarget.k_litMaterialTypeTag + " " + UltraRPTarget.k_motionVectorsTag,
                    renderType = target.renderType,
                    renderQueue = target.renderQueue,
                    disableBatchingTag = target.disableBatching,
                    generatesPreview = true,
                    passes = new PassCollection
                    {
                        { LitPasses.Forward(target, workflowMode, blendModePreserveSpecular) },
                        { UltraRPCorePasses.DepthNormals(target) },
                        { UltraRPCorePasses.Meta(target, true) },
                        { UltraRPCorePasses.MotionVectors(target) },
                        { UltraRPCorePasses.SceneSelection(target) },
                        { UltraRPCorePasses.ScenePicking(target) },
                    },
                };
                if (target.castShadows || target.allowMaterialOverride)
                    result.passes.Add(UltraRPCorePasses.ShadowCaster(target));
                if (target.mayWriteDepth)
                    result.passes.Add(UltraRPCorePasses.DepthOnly(target));
                return result;
            }
        }
        #endregion

        #region Passes
        static class LitPasses
        {
            public static PassDescriptor Forward(UltraRPTarget target, WorkflowMode workflowMode, bool blendModePreserveSpecular)
            {
                var result = new PassDescriptor()
                {
                    displayName = "UltraRP Forward",
                    virtualTextureFeedback = true,
                    referenceName = "SHADERPASS_FORWARD",
                    // Graphs have no GBuffer pass: both Forward and Deferred render
                    // them through the forward-only draw, exactly once.
                    lightMode = "UltraForwardOnly",
                    useInPreview = true,

                    passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                    sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                    validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                    validPixelBlocks = UltraRPCoreBlockMasks.FragmentLit,

                    structs = UltraRPCoreStructCollections.Default,
                    requiredFields = LitRequiredFields.Forward,
                    fieldDependencies = UltraRPCoreFieldDependencies.Default,

                    renderStates = UltraRPCoreRenderStates.Forward(target),
                    pragmas = UltraRPCorePragmas.Forward,
                    defines = new DefineCollection() { UltraRPCoreDefines.UseFragmentFog },
                    keywords = new KeywordCollection { UltraRPCoreKeywords.Forward },
                    includes = UltraRPCoreIncludes.Forward,

                    customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
                };

                UltraRPCorePasses.AddTargetSurfaceControlsToPass(ref result, target);
                UltraRPCorePasses.AddBlendControlsToPass(ref result, target);
                UltraRPCorePasses.AddLODCrossFadeControlToPass(ref result, target);
                if (target.allowMaterialOverride)
                {
                    result.keywords.Add(UltraRPCoreKeywordDescriptors.SpecularSetup);
                    result.keywords.Add(UltraRPCoreKeywordDescriptors.ReceiveShadowsOff);
                }
                else
                {
                    if (workflowMode == WorkflowMode.Specular)
                        result.defines.Add(UltraRPCoreKeywordDescriptors.SpecularSetup, 1);
                    if (!target.receiveShadows)
                        result.defines.Add(UltraRPCoreKeywordDescriptors.ReceiveShadowsOff, 1);
                }

                return result;
            }
        }
        #endregion

        #region RequiredFields
        static class LitRequiredFields
        {
            public static FieldCollection Forward = new FieldCollection()
            {
                StructFields.Attributes.uv1,
                StructFields.Attributes.uv2,
                StructFields.Varyings.positionWS,
                StructFields.Varyings.normalWS,
                StructFields.Varyings.tangentWS,
                UltraRPStructFields.Varyings.staticLightmapUV,
                UltraRPStructFields.Varyings.dynamicLightmapUV,
                UltraRPStructFields.Varyings.sh,
                UltraRPStructFields.Varyings.probeOcclusion,
                UltraRPStructFields.Varyings.fogFactorAndVertexLight,
                UltraRPStructFields.Varyings.shadowCoord,
            };
        }
        #endregion
    }
}
