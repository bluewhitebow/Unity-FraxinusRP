
using UnityEngine;
using static UnityEditor.Rendering.BuiltIn.ShaderUtils;
using UnityEditor.Rendering.BuiltIn;
using System;
using UnityEngine.UIElements;
using UnityEditor.Rendering.Canvas.ShaderGraph;
using UnityEditor;
using UnityEditor.ShaderGraph;

namespace FraxinusEditor.Rendering.Ultra
{
    class UltraRPCanvasSubTarget : CanvasSubTarget<UltraRPTarget>, IRequiresData<CanvasData>, IHasMetadata
    {
        static readonly GUID k_sourceCodeGuid = new GUID("f7075c3a804b49bf86535f6f86615132");  // UltraRPCanvasSubTarget.cs

        static readonly string k_canvasPass = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Includes/CanvasPass.hlsl";

        public override void Setup(ref TargetSetupContext context)
        {
            base.Setup(ref context);
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);
        }

        public override bool IsActive() => true;

        // We don't need the save context / update materials for now
        public override object saveContext => null;

        protected override string pipelineTag => UltraRPTarget.k_pipelineTag;

        protected override IncludeCollection pregraphIncludes => new IncludeCollection
        {
            { UltraRPCoreIncludes.CorePregraph },
            { kInstancing, IncludeLocation.Pregraph },
            { UltraRPCoreIncludes.ShaderGraphPregraph },
        };

        protected override IncludeCollection postgraphIncludes => new IncludeCollection
        {
            { k_canvasPass, IncludeLocation.Postgraph },
        };

        public UltraRPCanvasSubTarget()
        {
            displayName = "Canvas";
        }

        protected override DefineCollection GetAdditionalDefines()
        {
            var result = new DefineCollection();
            if (canvasData.alphaClip)
                result.Add(UltraRPCoreKeywordDescriptors.AlphaTestOn, 1);

            result.Add(base.GetAdditionalDefines());
            return result;
        }

        public override void GetActiveBlocks(ref TargetActiveBlockContext context)
        {
            base.GetActiveBlocks(ref context);
            context.AddBlock(BlockFields.SurfaceDescription.AlphaClipThreshold, canvasData.alphaClip);
        }

        public override void GetFields(ref TargetFieldContext context)
        {
            base.GetFields(ref context);
            if (canvasData.alphaClip)
                context.AddField(UnityEditor.ShaderGraph.Fields.AlphaTest);
        }

        public override void CollectShaderProperties(PropertyCollector collector, GenerationMode generationMode)
        {
            base.CollectShaderProperties(collector, generationMode);
            if (canvasData.alphaClip)
                collector.AddShaderProperty(CanvasProperties.AlphaTest);
        }
    }
}
