using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;

namespace FraxinusEditor.Rendering.Ultra
{
    static class UltraRPCorePasses
    {
        internal static void AddBlendControlsToPass(ref PassDescriptor pass, UltraRPTarget target)
        {
            if (target.allowMaterialOverride)
            {
                pass.keywords.Add(UltraRPCoreKeywordDescriptors.AlphaPremultiply);
                pass.keywords.Add(UltraRPCoreKeywordDescriptors.AlphaModulate);
            }
            else if (target.surfaceType == SurfaceType.Transparent)
            {
                if (target.alphaMode == AlphaMode.Premultiply)
                    pass.defines.Add(UltraRPCoreKeywordDescriptors.AlphaPremultiply, 1);
                if (target.alphaMode == AlphaMode.Multiply)
                    pass.defines.Add(UltraRPCoreKeywordDescriptors.AlphaModulate, 1);
            }
        }

        internal static void AddAlphaClipControlToPass(ref PassDescriptor pass, UltraRPTarget target)
        {
            if (target.allowMaterialOverride)
                pass.keywords.Add(UltraRPCoreKeywordDescriptors.AlphaTestOn);
            else if (target.alphaClip)
                pass.defines.Add(UltraRPCoreKeywordDescriptors.AlphaTestOn, 1);
        }

        internal static void AddTargetSurfaceControlsToPass(ref PassDescriptor pass, UltraRPTarget target)
        {
            if (target.allowMaterialOverride)
            {
                pass.keywords.Add(UltraRPCoreKeywordDescriptors.SurfaceTypeTransparent);
                pass.keywords.Add(UltraRPCoreKeywordDescriptors.AlphaTestOn);
            }
            else
            {
                if (target.surfaceType == SurfaceType.Transparent)
                    pass.defines.Add(UltraRPCoreKeywordDescriptors.SurfaceTypeTransparent, 1);

                if (target.alphaClip)
                    pass.defines.Add(UltraRPCoreKeywordDescriptors.AlphaTestOn, 1);
            }
        }

        internal static void AddAlphaToMaskControlToPass(ref PassDescriptor pass, UltraRPTarget target)
        {
            if (target.allowMaterialOverride)
                pass.keywords.Add(UltraRPCoreKeywordDescriptors.AlphaToMask);
            else if (target.alphaClip && (target.surfaceType == SurfaceType.Opaque))
                pass.defines.Add(UltraRPCoreKeywordDescriptors.AlphaToMask, 1);
        }

        internal static void AddLODCrossFadeControlToPass(ref PassDescriptor pass, UltraRPTarget target)
        {
            if (target.supportsLodCrossFade)
            {
                pass.defines.Add(UltraRPCoreKeywordDescriptors.UseUnityCrossFade, 1);
                pass.keywords.Add(UltraRPCoreKeywordDescriptors.LODFadeCrossFade);
            }
        }

        public static PassDescriptor DepthOnly(UltraRPTarget target)
        {
            var result = new PassDescriptor()
            {
                displayName = "DepthOnly",
                referenceName = "SHADERPASS_DEPTHONLY",
                lightMode = "DepthOnly",
                useInPreview = true,

                passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                validPixelBlocks = UltraRPCoreBlockMasks.FragmentAlphaOnly,

                structs = UltraRPCoreStructCollections.Default,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                renderStates = UltraRPCoreRenderStates.DepthOnly(target),
                pragmas = UltraRPCorePragmas.Instanced,
                defines = new DefineCollection(),
                keywords = new KeywordCollection(),
                includes = UltraRPCoreIncludes.DepthOnly,

                customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
            };

            AddAlphaClipControlToPass(ref result, target);
            AddLODCrossFadeControlToPass(ref result, target);

            return result;
        }

        public static PassDescriptor DepthNormals(UltraRPTarget target)
        {
            var result = new PassDescriptor()
            {
                displayName = "DepthNormals",
                referenceName = "SHADERPASS_DEPTHNORMALS",
                // These graphs do not contribute normals through a GBuffer pass.
                // Include them in Deferred's partial depth/normal prepass.
                lightMode = "DepthNormalsOnly",
                useInPreview = true,

                passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                validPixelBlocks = UltraRPCoreBlockMasks.FragmentDepthNormals,

                structs = UltraRPCoreStructCollections.Default,
                requiredFields = UltraRPCoreRequiredFields.DepthNormals,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                renderStates = UltraRPCoreRenderStates.DepthNormalsOnly(target),
                pragmas = UltraRPCorePragmas.RenderingLayers,
                defines = new DefineCollection(),
                keywords = new KeywordCollection { UltraRPCoreKeywordDescriptors.GBufferNormalsOct },
                includes = UltraRPCoreIncludes.DepthNormalsOnly,

                customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
            };

            AddAlphaClipControlToPass(ref result, target);
            AddLODCrossFadeControlToPass(ref result, target);

            return result;
        }

        public static PassDescriptor ShadowCaster(UltraRPTarget target)
        {
            var result = new PassDescriptor()
            {
                displayName = "ShadowCaster",
                referenceName = "SHADERPASS_SHADOWCASTER",
                lightMode = "ShadowCaster",

                passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                validPixelBlocks = UltraRPCoreBlockMasks.FragmentAlphaOnly,

                structs = UltraRPCoreStructCollections.Default,
                requiredFields = UltraRPCoreRequiredFields.ShadowCaster,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                renderStates = UltraRPCoreRenderStates.ShadowCaster(target),
                pragmas = UltraRPCorePragmas.Instanced,
                defines = new DefineCollection(),
                keywords = new KeywordCollection { UltraRPCoreKeywords.ShadowCaster },
                includes = UltraRPCoreIncludes.ShadowCaster,

                customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
            };

            AddAlphaClipControlToPass(ref result, target);
            AddLODCrossFadeControlToPass(ref result, target);

            return result;
        }

        public static PassDescriptor Meta(UltraRPTarget target, bool lit)
        {
            var result = new PassDescriptor
            {
                displayName = "Meta", referenceName = "SHADERPASS_META", lightMode = "Meta",
                passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,
                validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                validPixelBlocks = lit ? UltraRPCoreBlockMasks.FragmentMeta : UltraRPCoreBlockMasks.FragmentColorAlpha,
                structs = UltraRPCoreStructCollections.Default,
                requiredFields = UltraRPCoreRequiredFields.Meta,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,
                renderStates = UltraRPCoreRenderStates.Meta,
                pragmas = UltraRPCorePragmas.Instanced,
                defines = new DefineCollection(),
                keywords = new KeywordCollection { UltraRPCoreKeywordDescriptors.EditorVisualization },
                includes = UltraRPCoreIncludes.Meta,
                customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
            };
            if (lit) result.defines.Add(UltraRPCoreKeywordDescriptors.MetaLit, 1);
            AddAlphaClipControlToPass(ref result, target);
            return result;
        }

        public static PassDescriptor MotionVectors(UltraRPTarget target)
        {
            var result = new PassDescriptor
            {
                displayName = "MotionVectors", referenceName = "SHADERPASS_MOTION_VECTORS", lightMode = "MotionVectors",
                passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,
                validVertexBlocks = target.additionalMotionVectorMode == AdditionalMotionVectorMode.Custom
                    ? UltraRPCoreBlockMasks.CustomMotionVectorVertex : UltraRPCoreBlockMasks.Vertex,
                validPixelBlocks = UltraRPCoreBlockMasks.FragmentAlphaOnly,
                structs = UltraRPCoreStructCollections.Default,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,
                renderStates = UltraRPCoreRenderStates.MotionVectors(target),
                pragmas = UltraRPCorePragmas.MotionVectors,
                defines = new DefineCollection(), keywords = new KeywordCollection(),
                includes = UltraRPCoreIncludes.MotionVectors,
                customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
            };
            if (target.additionalMotionVectorMode == AdditionalMotionVectorMode.TimeBased)
                result.defines.Add(UltraRPCoreKeywordDescriptors.AutomaticTimeBasedMotionVectors, 1);
            AddAlphaClipControlToPass(ref result, target);
            AddLODCrossFadeControlToPass(ref result, target);
            return result;
        }

        public static PassDescriptor SceneSelection(UltraRPTarget target)
        {
            var result = new PassDescriptor()
            {
                displayName = "SceneSelectionPass",
                referenceName = "SHADERPASS_DEPTHONLY",
                lightMode = "SceneSelectionPass",
                useInPreview = false,

                passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                validPixelBlocks = UltraRPCoreBlockMasks.FragmentAlphaOnly,

                structs = UltraRPCoreStructCollections.Default,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                renderStates = UltraRPCoreRenderStates.SceneSelection(target),
                pragmas = UltraRPCorePragmas.Instanced,
                defines = new DefineCollection { UltraRPCoreDefines.SceneSelection, { UltraRPCoreKeywordDescriptors.AlphaClipThreshold, 1 } },
                keywords = new KeywordCollection(),
                includes = UltraRPCoreIncludes.SceneSelection,

                customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
            };

            AddAlphaClipControlToPass(ref result, target);

            return result;
        }

        public static PassDescriptor ScenePicking(UltraRPTarget target)
        {
            var result = new PassDescriptor()
            {
                displayName = "ScenePickingPass",
                referenceName = "SHADERPASS_DEPTHONLY",
                lightMode = "Picking",
                useInPreview = false,

                passTemplatePath = UltraRPTarget.k_uberTemplatePath,
                sharedTemplateDirectories = UltraRPTarget.k_sharedTemplateDirectories,

                validVertexBlocks = UltraRPCoreBlockMasks.Vertex,
                validPixelBlocks = UltraRPCoreBlockMasks.FragmentColorAlpha,

                structs = UltraRPCoreStructCollections.Default,
                fieldDependencies = UltraRPCoreFieldDependencies.Default,

                renderStates = UltraRPCoreRenderStates.ScenePicking(target),
                pragmas = UltraRPCorePragmas.Instanced,
                defines = new DefineCollection { UltraRPCoreDefines.ScenePicking, { UltraRPCoreKeywordDescriptors.AlphaClipThreshold, 1 } },
                keywords = new KeywordCollection(),
                includes = UltraRPCoreIncludes.ScenePicking,

                customInterpolators = UltraRPCoreCustomInterpDescriptors.Common
            };

            AddAlphaClipControlToPass(ref result, target);

            return result;
        }
    }
}
