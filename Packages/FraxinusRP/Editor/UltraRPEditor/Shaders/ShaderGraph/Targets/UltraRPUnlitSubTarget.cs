using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.UIElements;
using FraxinusEngine.Rendering.Ultra;
using static FraxinusEngine.Rendering.Ultra.ShaderUtils;

namespace FraxinusEditor.Rendering.Ultra
{
    sealed class UltraRPUnlitSubTarget : UltraRPSubTarget
    {
        static readonly GUID k_sourceCodeGuid = new GUID("8700eb67d6422d44d8fb75670e564225");

        public UltraRPUnlitSubTarget()
        {
            displayName = "Unlit";
        }

        protected override ShaderID shaderID => ShaderID.SG_Unlit;

        public override bool IsActive() => true;

        public override void Setup(ref TargetSetupContext context)
        {
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);
            base.Setup(ref context);

            var ultraRPType = typeof(UltraRenderPipelineAsset);
            if (!context.HasCustomEditorForRenderPipeline(ultraRPType))
            {
                var gui = typeof(ShaderGraphUnlitGUI);
#if HAS_VFX_GRAPH
                if (TargetsVFX())
                    gui = typeof(UnityEditor.VFX.UltraRP.VFXShaderGraphUnlitGUI);
#endif
                context.AddCustomEditorForRenderPipeline(gui.FullName, ultraRPType);
            }

            context.AddSubShader(PostProcessSubShader(SubShaders.UnlitSubShader(target)));
        }

        public override void ProcessPreviewMaterial(Material material)
        {
            if (target.allowMaterialOverride)
            {
                material.SetFloat(UltraRPProperty.SurfaceType, (float)target.surfaceType);
                material.SetFloat(UltraRPProperty.BlendMode, (float)target.alphaMode);
                material.SetFloat(UltraRPProperty.AlphaClip, target.alphaClip ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.CastShadows, target.castShadows ? 1.0f : 0.0f);
                material.SetFloat(UltraRPProperty.CullMode, (int)target.renderFace);
                material.SetFloat(UltraRPProperty.ZWriteControl, (float)target.zWriteControl);
                material.SetFloat(UltraRPProperty.ZTest, (float)target.zTestMode);
            }

            material.SetFloat(UltraRPProperty.QueueOffset, 0.0f);
            material.SetFloat(UltraRPProperty.QueueControl, 0.0f);
            ShaderGraphUnlitGUI.UpdateMaterial(material, ShaderEditorUtils.MaterialUpdateType.CreatedNewMaterial);
        }

        public override void GetFields(ref TargetFieldContext context)
        {
            base.GetFields(ref context);
        }

        public override void GetActiveBlocks(ref TargetActiveBlockContext context)
        {
            context.AddBlock(UltraRPBlockFields.VertexDescription.MotionVector, target.additionalMotionVectorMode == AdditionalMotionVectorMode.Custom);
            context.AddBlock(BlockFields.SurfaceDescription.Alpha);
            context.AddBlock(BlockFields.SurfaceDescription.AlphaClipThreshold, target.alphaClip || target.allowMaterialOverride);
        }

        public override void CollectShaderProperties(PropertyCollector collector, GenerationMode generationMode)
        {
            if (target.allowMaterialOverride)
            {
                collector.AddFloatProperty(UltraRPProperty.SurfaceType, (float)target.surfaceType);
                collector.AddFloatProperty(UltraRPProperty.BlendMode, (float)target.alphaMode);
                collector.AddFloatProperty(UltraRPProperty.AlphaClip, target.alphaClip ? 1.0f : 0.0f);
                collector.AddFloatProperty(UltraRPProperty.CastShadows, target.castShadows ? 1.0f : 0.0f);
                collector.AddFloatProperty(UltraRPProperty.SrcBlend, 1.0f);
                collector.AddFloatProperty(UltraRPProperty.DstBlend, 0.0f);
                collector.AddFloatProperty(UltraRPProperty.SrcBlendAlpha, 1.0f);
                collector.AddFloatProperty(UltraRPProperty.DstBlendAlpha, 0.0f);
                collector.AddFloatProperty(UltraRPProperty.ZWriteControl, (float)target.zWriteControl);
                collector.AddFloatProperty(UltraRPProperty.ZWrite, target.surfaceType == SurfaceType.Opaque ? 1.0f : 0.0f);
                collector.AddFloatProperty(UltraRPProperty.ZTest, (float)target.zTestMode);
                collector.AddFloatProperty(UltraRPProperty.CullMode, (float)target.renderFace);
            }

            collector.AddFloatProperty(UltraRPProperty.QueueOffset, 0.0f);
            collector.AddFloatProperty(UltraRPProperty.QueueControl, -1.0f);
        }

        public override void GetPropertiesGUI(ref TargetPropertyGUIContext context, Action onChange, Action<String> registerUndo)
        {
            target.AddDefaultMaterialOverrideGUI(ref context, onChange, registerUndo);
            target.AddDefaultSurfacePropertiesGUI(ref context, onChange, registerUndo, showReceiveShadows: false);
        }

        #region SubShader
        static class SubShaders
        {
            public static SubShaderDescriptor UnlitSubShader(UltraRPTarget target)
            {
                SubShaderDescriptor result = new SubShaderDescriptor()
                {
                    pipelineTag = UltraRPTarget.k_pipelineTag,
                    customTags = UltraRPTarget.k_unlitMaterialTypeTag + " " + UltraRPTarget.k_motionVectorsTag,
                    renderType = target.renderType,
                    renderQueue = target.renderQueue,
                    disableBatchingTag = target.disableBatching,
                    generatesPreview = true,
                    passes = new PassCollection
                    {
                        { UnlitPasses.Unlit(target) },
                        { UltraRPCorePasses.DepthNormals(target) },
                        { UltraRPCorePasses.Meta(target, false) },
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
        static class UnlitPasses
        {
            public static PassDescriptor Unlit(UltraRPTarget target)
            {
                var result = new PassDescriptor()
                {
                    displayName = "UltraRP Unlit",
                    virtualTextureFeedback = true,
                    referenceName = "SHADERPASS_UNLIT",
                    // Unlit graphs also need the opaque fallback in Deferred.
                    lightMode = "UltraForwardOnly",
                    useInPreview = true,

                    passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                    sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                    validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                    validPixelBlocks = UltraRPCoreBlockMasks.FragmentColorAlpha,

                    structs = UltraRPCoreStructCollections.Default,
                    requiredFields = UnlitRequiredFields.Unlit,
                    fieldDependencies = UltraRPCoreFieldDependencies.Default,

                    renderStates = UltraRPCoreRenderStates.Forward(target),
                    pragmas = UltraRPCorePragmas.Forward,
                    defines = new DefineCollection() { UltraRPCoreDefines.UseFragmentFog },
                    keywords = new KeywordCollection(),
                    includes = UltraRPCoreIncludes.Unlit,

                    customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
                };

                UltraRPCorePasses.AddTargetSurfaceControlsToPass(ref result, target);
                UltraRPCorePasses.AddBlendControlsToPass(ref result, target);
                UltraRPCorePasses.AddLODCrossFadeControlToPass(ref result, target);

                return result;
            }
        }
        #endregion

        #region RequiredFields
        static class UnlitRequiredFields
        {
            public static FieldCollection Unlit = new FieldCollection()
            {
                StructFields.Varyings.positionWS,
                UltraRPStructFields.Varyings.fogFactorAndVertexLight,
            };
        }
        #endregion
    }
}
