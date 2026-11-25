using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.Rendering.Fullscreen.ShaderGraph;
using UnityEditor.Rendering.BuiltIn;
using static UnityEditor.Rendering.BuiltIn.ShaderUtils;

namespace FraxinusEditor.Rendering.Ultra
{
    class UltraRPFullscreenSubTarget : FullscreenSubTarget<UltraRPTarget>, IRequiresData<FullscreenData>, IHasMetadata
    {
        static readonly GUID k_sourceCodeGuid = new GUID("48080a5025a54a84087e882e2f988642");  // UltraRPFullscreenSubTarget.cs

        public override void Setup(ref TargetSetupContext context)
        {
            base.Setup(ref context);
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);
        }

        protected override KeywordCollection GetPassKeywords(FullscreenCompatibility compatibility)
        {
            var modifiedKeywordCollection = base.GetPassKeywords(compatibility);
            modifiedKeywordCollection.Add(UltraRPCoreKeywordDescriptors.GBufferNormalsOct);
            return modifiedKeywordCollection;
        }

        // We don't need the save context / update materials for now
        public override object saveContext => null;

        protected override string pipelineTag => UltraRPTarget.k_pipelineTag;

        const string k_ultraRPInput = "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Input.hlsl";

        protected override IncludeCollection pregraphIncludes => new IncludeCollection
        {
            { k_ultraRPInput, IncludeLocation.Pregraph }, // Include before kInstancing
            { kInstancing, IncludeLocation.Pregraph }, // For VR
            { UltraRPCoreIncludes.CorePregraph },
            { UltraRPCoreIncludes.ShaderGraphPregraph },
        };

        public UltraRPFullscreenSubTarget()
        {
            displayName = "Fullscreen";
        }
    }
}
