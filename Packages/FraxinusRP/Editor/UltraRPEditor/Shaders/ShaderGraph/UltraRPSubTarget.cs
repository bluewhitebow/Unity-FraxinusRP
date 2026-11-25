using UnityEngine;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using static FraxinusEngine.Rendering.Ultra.ShaderUtils;

#if HAS_VFX_GRAPH
using UnityEditor.VFX;
#endif

namespace FraxinusEditor.Rendering.Ultra
{
    abstract class UltraRPSubTarget : SubTarget<UltraRPTarget>, IHasMetadata
#if HAS_VFX_GRAPH
        , IRequireVFXContext
#endif
    {
        static readonly GUID k_sourceCodeGuid = new GUID("159e4c2089d40784e984d8ad2f8fe576");

#if HAS_VFX_GRAPH
        // VFX Properties
        VFXContext _contextVFX = null;
        VFXTaskCompiledData _taskDataVFX;

        protected bool TargetsVFX() => _contextVFX != null;

        public void ConfigureContextData(VFXContext context, VFXTaskCompiledData data)
        {
            _contextVFX = context;
            _taskDataVFX = data;
        }
#endif

        public override void Setup(ref TargetSetupContext context)
        {
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);
        }

        protected abstract ShaderID shaderID { get; }

        protected SubShaderDescriptor PostProcessSubShader(SubShaderDescriptor subShaderDescriptor)
        {
#if HAS_VFX_GRAPH
            if (TargetsVFX())
                return VFXSubTarget.PostProcessSubShader(subShaderDescriptor, _contextVFX, _taskDataVFX);
#endif
            return subShaderDescriptor;
        }

        public override void GetFields(ref TargetFieldContext context)
        {
#if HAS_VFX_GRAPH
            if (TargetsVFX())
                VFXSubTarget.GetFields(ref context, _contextVFX);
#endif
        }

        public virtual string identifier => GetType().Name;

        public virtual ScriptableObject GetMetadataObject(GraphDataReadOnly graphData)
        {
            var ultraMetadata = ScriptableObject.CreateInstance<UltraRPMetadata>();
            ultraMetadata.shaderID = shaderID;
            ultraMetadata.alphaMode = target.alphaMode;
            ultraMetadata.allowMaterialOverride = target.allowMaterialOverride;
            ultraMetadata.surfaceType = target.surfaceType;
            ultraMetadata.castShadows = target.castShadows;
            ultraMetadata.hasVertexModificationInMotionVector = target.additionalMotionVectorMode != AdditionalMotionVectorMode.None || graphData.AnyVertexAnimationActive();
            ultraMetadata.isVFXCompatible = graphData.IsVFXCompatible();
            return ultraMetadata;
        }

        private int _lastMaterialNeedsUpdateHash = 0;
        private bool _hasMaterialNeedsUpdateHash;
        protected virtual int ComputeMaterialNeedsUpdateHash()
        {
            unchecked
            {
                int hash = target.allowMaterialOverride.GetHashCode();
                hash = hash * 23 + target.surfaceType.GetHashCode();
                hash = hash * 23 + target.alphaMode.GetHashCode();
                hash = hash * 23 + target.alphaClip.GetHashCode();
                hash = hash * 23 + target.castShadows.GetHashCode();
                hash = hash * 23 + target.receiveShadows.GetHashCode();
                hash = hash * 23 + target.zWriteControl.GetHashCode();
                hash = hash * 23 + target.additionalMotionVectorMode.GetHashCode();
                return hash;
            }
        }

        public override object saveContext
        {
            get
            {
                int hash = ComputeMaterialNeedsUpdateHash();
                bool needsUpdate = !_hasMaterialNeedsUpdateHash || hash != _lastMaterialNeedsUpdateHash;
                if (needsUpdate)
                {
                    _lastMaterialNeedsUpdateHash = hash;
                    _hasMaterialNeedsUpdateHash = true;
                }

                return new UltraRPShaderGraphSaveContext { updateMaterials = needsUpdate };
            }
        }
    }

    internal class UltraRPShaderGraphSaveContext
    {
        public bool updateMaterials;
    }

    internal static class UltraRPSubShaderUtils
    {
        internal static void AddFloatProperty(this PropertyCollector collector, string referenceName, float defaultValue, HLSLDeclaration declarationType = HLSLDeclaration.DoNotDeclare)
        {
            collector.AddShaderProperty(new Vector1ShaderProperty
            {
                floatType = FloatType.Default,
                hidden = true,
                overrideHLSLDeclaration = true,
                hlslDeclarationOverride = declarationType,
                value = defaultValue,
                displayName = referenceName,
                overrideReferenceName = referenceName,
            });
        }

        internal static void AddToggleProperty(this PropertyCollector collector, string referenceName, bool defaultValue, HLSLDeclaration declarationType = HLSLDeclaration.DoNotDeclare)
        {
            collector.AddShaderProperty(new BooleanShaderProperty
            {
                value = defaultValue,
                hidden = true,
                overrideHLSLDeclaration = true,
                hlslDeclarationOverride = declarationType,
                displayName = referenceName,
                overrideReferenceName = referenceName,
            });
        }

        internal static PassDescriptor PassVariant(in PassDescriptor source, PragmaCollection pragmas)
        {
            var result = source;
            result.pragmas = pragmas;
            return result;
        }
    }
}
