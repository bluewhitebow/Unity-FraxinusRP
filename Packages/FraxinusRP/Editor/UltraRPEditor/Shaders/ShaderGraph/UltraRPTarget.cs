using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEditor.UIElements;
using FraxinusEngine.Rendering.Ultra;

#if HAS_VFX_GRAPH
using UnityEditor.VFX;
#endif

namespace FraxinusEditor.Rendering.Ultra
{
    sealed class UltraRPTarget : Target, IHasMetadata, IMaySupportVFX
#if HAS_VFX_GRAPH
        , IRequireVFXContext
#endif
    {
        public override int latestVersion => 1;

        // Constants
        static readonly GUID k_sourceCodeGuid = new GUID("75545ba323989d6479f8a2b95b3afd77");
        public const string k_pipelineTag = "UltraPipeline";
        public const string k_litMaterialTypeTag = "\"UltraMaterialType\" = \"Lit\"";
        public const string k_unlitMaterialTypeTag = "\"UltraMaterialType\" = \"Unlit\"";
        // The generated pass preserves graph alpha clipping and vertex deformation;
        // the generic object-motion fallback cannot evaluate either graph stage.
        public const string k_motionVectorsTag = "\"AlwaysRenderMotionVectors\" = \"true\"";
        public static readonly string[] k_sharedTemplateDirectories = GenerationUtils.GetDefaultSharedTemplateDirectories().Union(new string[]
        {
            "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Templates"
#if HAS_VFX_GRAPH
            , "Packages/com.unity.visualeffectgraph/Editor/ShaderGraph/Templates"
#endif
        }).ToArray();
        public const string k_uberTemplatePath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Shaders/ShaderGraph/Templates/ShaderPass.template";

        // SubTarget
        List<SubTarget> _subTargets;
        List<string> _subTargetNames;
        int activeSubTargetIndex => _subTargets.IndexOf(_activeSubTarget);

        // Subtarget Data
        [SerializeField]
        List<JsonData<JsonObject>> _datas = new List<JsonData<JsonObject>>();

        // View
        PopupField<string> _subTargetField;
        TextField _customGUIField;

#if HAS_VFX_GRAPH
        Toggle _supportVFXToggle;
#endif

        [SerializeField]
        JsonData<SubTarget> _activeSubTarget;

        [SerializeField]
        bool _allowMaterialOverride = false;

        [SerializeField]
        SurfaceType _surfaceType = SurfaceType.Opaque;

        [SerializeField]
        ZTestMode _zTestMode = ZTestMode.LEqual;

        [SerializeField]
        ZWriteControl _zWriteControl = ZWriteControl.Auto;

        [SerializeField]
        AlphaMode _alphaMode = AlphaMode.Alpha;

        [SerializeField]
        RenderFace _renderFace = RenderFace.Front;

        [SerializeField]
        bool _alphaClip = false;

        [SerializeField]
        bool _castShadows = true;

        [SerializeField]
        bool _receiveShadows = true;

        [SerializeField]
        AdditionalMotionVectorMode _additionalMotionVectorMode = AdditionalMotionVectorMode.None;

        [SerializeField]
        bool _supportsLODCrossFade = false;

        [SerializeField]
        string _customEditorGUI;

        [SerializeField]
        bool _supportVFX;

        public UltraRPTarget()
        {
            displayName = "UltraRP";
            _subTargets = TargetUtils.GetSubTargets(this);
            _subTargetNames = _subTargets.Select(x => x.displayName).ToList();
            TargetUtils.ProcessSubTargetList(ref _activeSubTarget, ref _subTargets);
            ProcessSubTargetDatas(_activeSubTarget.value);
        }

        public string renderType
        {
            get
            {
                if (surfaceType == SurfaceType.Transparent)
                    return $"{RenderType.Transparent}";
                else
                    return $"{RenderType.Opaque}";
            }
        }

        public string renderQueue
        {
            get
            {
                if (surfaceType == SurfaceType.Transparent)
                    return $"{UnityEditor.ShaderGraph.RenderQueue.Transparent}";
                else if (alphaClip)
                    return $"{UnityEditor.ShaderGraph.RenderQueue.AlphaTest}";
                else
                    return $"{UnityEditor.ShaderGraph.RenderQueue.Geometry}";
            }
        }

        public string disableBatching
        {
            get
            {
                if (supportsLodCrossFade)
                    return $"{UnityEditor.ShaderGraph.DisableBatching.LODFading}";
                else
                    return $"{UnityEditor.ShaderGraph.DisableBatching.False}";
            }
        }

        public SubTarget activeSubTarget
        {
            get => _activeSubTarget.value;
            set => _activeSubTarget = value;
        }

        public bool allowMaterialOverride
        {
            get => _allowMaterialOverride;
            set => _allowMaterialOverride = value;
        }

        public SurfaceType surfaceType
        {
            get => _surfaceType;
            set => _surfaceType = value;
        }

        public ZWriteControl zWriteControl
        {
            get => _zWriteControl;
            set => _zWriteControl = value;
        }

        public ZTestMode zTestMode
        {
            get => _zTestMode;
            set => _zTestMode = value;
        }

        public AlphaMode alphaMode
        {
            get => _alphaMode;
            set => _alphaMode = value;
        }

        public RenderFace renderFace
        {
            get => _renderFace;
            set => _renderFace = value;
        }

        public bool alphaClip
        {
            get => _alphaClip;
            set => _alphaClip = value;
        }

        public bool castShadows
        {
            get => _castShadows;
            set => _castShadows = value;
        }

        public bool receiveShadows
        {
            get => _receiveShadows;
            set => _receiveShadows = value;
        }

        public AdditionalMotionVectorMode additionalMotionVectorMode
        {
            get => _additionalMotionVectorMode;
            set => _additionalMotionVectorMode = value;
        }

        public bool supportsLodCrossFade
        {
            get => _supportsLODCrossFade;
            set => _supportsLODCrossFade = value;
        }

        public string customEditorGUI
        {
            get => _customEditorGUI;
            set => _customEditorGUI = value;
        }

        public bool mayWriteDepth
        {
            get
            {
                if (allowMaterialOverride)
                    return true;

                switch (zWriteControl)
                {
                    case ZWriteControl.Auto:
                        return (surfaceType == SurfaceType.Opaque);
                    case ZWriteControl.ForceDisabled:
                        return false;
                    default:
                        return true;
                }
            }
        }

        public override bool IsActive()
        {
            bool isUltraRenderPipeline = GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset;
            return isUltraRenderPipeline && activeSubTarget.IsActive();
        }

        public override bool IsNodeAllowedByTarget(Type nodeType)
        {
            SRPFilterAttribute srpFilter = NodeClassCache.GetAttributeOnNodeType<SRPFilterAttribute>(nodeType);
            bool worksWithThisSrp = srpFilter == null || srpFilter.srpTypes.Contains(typeof(UltraRenderPipeline));

            SubTargetFilterAttribute subTargetFilter = NodeClassCache.GetAttributeOnNodeType<SubTargetFilterAttribute>(nodeType);
            bool worksWithThisSubTarget = subTargetFilter == null || subTargetFilter.subTargetTypes.Contains(activeSubTarget.GetType());

            if (activeSubTarget.IsActive())
                worksWithThisSubTarget &= activeSubTarget.IsNodeAllowedBySubTarget(nodeType);

            return worksWithThisSrp && worksWithThisSubTarget && base.IsNodeAllowedByTarget(nodeType);
        }

        public override void Setup(ref TargetSetupContext context)
        {
            context.AddAssetDependency(k_sourceCodeGuid, AssetCollection.Flags.SourceDependency);

            if (!string.IsNullOrEmpty(_customEditorGUI))
                context.AddCustomEditorForRenderPipeline(_customEditorGUI, typeof(UltraRenderPipelineAsset));

            TargetUtils.ProcessSubTargetList(ref _activeSubTarget, ref _subTargets);
            _activeSubTarget.value.target = this;
            ProcessSubTargetDatas(_activeSubTarget.value);
            _activeSubTarget.value.Setup(ref context);
        }

        public override void OnAfterMultiDeserialize(string json)
        {
            TargetUtils.ProcessSubTargetList(ref _activeSubTarget, ref _subTargets);
            _activeSubTarget.value.target = this;
            _activeSubTarget.value.OnAfterParentTargetDeserialized();
        }

        public override void GetFields(ref TargetFieldContext context)
        {
            var descs = context.blocks.Select(x => x.descriptor);
            context.AddField(Fields.GraphVertex, descs.Contains(BlockFields.VertexDescription.Position) ||
                descs.Contains(BlockFields.VertexDescription.Normal) ||
                descs.Contains(BlockFields.VertexDescription.Tangent));
            context.AddField(Fields.GraphPixel);

            _activeSubTarget.value.GetFields(ref context);
        }

        public override void GetActiveBlocks(ref TargetActiveBlockContext context)
        {
            context.AddBlock(BlockFields.VertexDescription.Position);
            context.AddBlock(BlockFields.VertexDescription.Normal);
            context.AddBlock(BlockFields.VertexDescription.Tangent);
            context.AddBlock(BlockFields.SurfaceDescription.BaseColor);

            _activeSubTarget.value.GetActiveBlocks(ref context);
        }

        public override void ProcessPreviewMaterial(Material material)
        {
            _activeSubTarget.value.ProcessPreviewMaterial(material);
        }

        public override object saveContext => _activeSubTarget.value?.saveContext;

        public override void CollectShaderProperties(PropertyCollector collector, GenerationMode generationMode)
        {
            base.CollectShaderProperties(collector, generationMode);
            _activeSubTarget.value.CollectShaderProperties(collector, generationMode);
        }

        public override void GetPropertiesGUI(ref TargetPropertyGUIContext context, Action onChange, Action<String> registerUndo)
        {
            _subTargetField = new PopupField<string>(_subTargetNames, activeSubTargetIndex);
            context.AddProperty("Material", _subTargetField, (evt) =>
            {
                if (Equals(activeSubTargetIndex, _subTargetField.index))
                    return;

                registerUndo("Change Material");
                _activeSubTarget = _subTargets[_subTargetField.index];
                ProcessSubTargetDatas(_activeSubTarget.value);
                onChange();
            });

            _activeSubTarget.value.GetPropertiesGUI(ref context, onChange, registerUndo);

            _customGUIField = new TextField("") { value = customEditorGUI };
            _customGUIField.RegisterCallback<FocusOutEvent>(s =>
            {
                if (Equals(customEditorGUI, _customGUIField.value))
                    return;

                registerUndo("Change Custom Editor GUI");
                customEditorGUI = _customGUIField.value;
                onChange();
            });
            context.AddProperty("Custom Editor GUI", _customGUIField, (evt) => { });

#if HAS_VFX_GRAPH
            if (_activeSubTarget.value is UltraRPSubTarget && CanSupportVFX())
            {
                _supportVFXToggle = new Toggle("") { value = _supportVFX };
                context.AddProperty("Support VFX Graph", _supportVFXToggle, (evt) =>
                {
                    registerUndo("Change Support VFX Graph");
                    _supportVFX = _supportVFXToggle.value;
                });
            }
            else
            {
                context.AddHelpBox(MessageType.Info, $"The {_activeSubTarget.value.displayName} target does not support VFX Graph.");
            }
#endif
        }

        internal enum ZTestModeForUI
        {
            Never = 1,
            Less = 2,
            Equal = 3,
            LEqual = 4,
            Greater = 5,
            NotEqual = 6,
            GEqual = 7,
            Always = 8,
        };

        public void AddDefaultMaterialOverrideGUI(ref TargetPropertyGUIContext context, Action onChange, Action<String> registerUndo)
        {
            context.AddProperty("Allow Material Override", new Toggle() { value = allowMaterialOverride }, (evt) =>
            {
                if (Equals(allowMaterialOverride, evt.newValue))
                    return;

                registerUndo("Change Allow Material Override");
                allowMaterialOverride = evt.newValue;
                onChange();
            });
        }

        public void AddDefaultSurfacePropertiesGUI(ref TargetPropertyGUIContext context, Action onChange, Action<String> registerUndo, bool showReceiveShadows)
        {
            context.AddProperty("Surface Type", new EnumField(SurfaceType.Opaque) { value = surfaceType }, (evt) =>
            {
                if (Equals(surfaceType, evt.newValue))
                    return;

                registerUndo("Change Surface");
                surfaceType = (SurfaceType)evt.newValue;
                onChange();
            });

            context.AddProperty("Blending Mode", new EnumField(AlphaMode.Alpha) { value = alphaMode }, surfaceType == SurfaceType.Transparent, (evt) =>
            {
                if (Equals(alphaMode, evt.newValue))
                    return;

                registerUndo("Change Blend");
                alphaMode = (AlphaMode)evt.newValue;
                onChange();
            });

            context.AddProperty("Render Face", new EnumField(RenderFace.Front) { value = renderFace }, (evt) =>
            {
                if (Equals(renderFace, evt.newValue))
                    return;

                registerUndo("Change Render Face");
                renderFace = (RenderFace)evt.newValue;
                onChange();
            });

            context.AddProperty("Depth Write", new EnumField(ZWriteControl.Auto) { value = zWriteControl }, (evt) =>
            {
                if (Equals(zWriteControl, evt.newValue))
                    return;

                registerUndo("Change Depth Write Control");
                zWriteControl = (ZWriteControl)evt.newValue;
                onChange();
            });

            context.AddProperty("Depth Test", new EnumField(ZTestModeForUI.LEqual) { value = (ZTestModeForUI)zTestMode }, (evt) =>
            {
                if (Equals(zTestMode, evt.newValue))
                    return;

                registerUndo("Change Depth Test");
                zTestMode = (ZTestMode)evt.newValue;
                onChange();
            });

            context.AddProperty("Alpha Clipping", new Toggle() { value = alphaClip }, (evt) =>
            {
                if (Equals(alphaClip, evt.newValue))
                    return;

                registerUndo("Change Alpha Clip");
                alphaClip = evt.newValue;
                onChange();
            });

            context.AddProperty("Cast Shadows", new Toggle() { value = castShadows }, (evt) =>
            {
                if (Equals(castShadows, evt.newValue))
                    return;

                registerUndo("Change Cast Shadows");
                castShadows = evt.newValue;
                onChange();
            });

            if (showReceiveShadows)
            {
                context.AddProperty("Receive Shadows", new Toggle() { value = receiveShadows }, (evt) =>
                {
                    if (Equals(receiveShadows, evt.newValue))
                        return;

                    registerUndo("Change Receive Shadows");
                    receiveShadows = evt.newValue;
                    onChange();
                });
            }

            context.AddProperty("Supports LOD Cross Fade", new Toggle() { value = supportsLodCrossFade }, (evt) =>
            {
                if (Equals(supportsLodCrossFade, evt.newValue))
                    return;

                registerUndo("Change Supports LOD Cross Fade");
                supportsLodCrossFade = evt.newValue;
                onChange();
            });

            context.AddProperty("Additional Motion Vectors", new EnumField(AdditionalMotionVectorMode.None) { value = additionalMotionVectorMode }, (evt) =>
            {
                if (Equals(additionalMotionVectorMode, evt.newValue))
                    return;
                registerUndo("Change Additional Motion Vectors");
                additionalMotionVectorMode = (AdditionalMotionVectorMode)evt.newValue;
                onChange();
            });
        }

        public bool TrySetActiveSubTarget(Type subTargetType)
        {
            if (!subTargetType.IsSubclassOf(typeof(SubTarget)))
                return false;

            foreach (var subTarget in _subTargets)
            {
                if (subTarget.GetType().Equals(subTargetType))
                {
                    _activeSubTarget = subTarget;
                    ProcessSubTargetDatas(_activeSubTarget);
                    return true;
                }
            }

            return false;
        }

        void ProcessSubTargetDatas(SubTarget subTarget)
        {
            var typeCollection = TypeCache.GetTypesDerivedFrom<JsonObject>();
            foreach (var type in typeCollection)
            {
                if (type.IsGenericType)
                    continue;

                var methodInfo = typeof(UltraRPTarget).GetMethod(nameof(SetDataOnSubTarget));
                var genericMethodInfo = methodInfo.MakeGenericMethod(type);
                genericMethodInfo.Invoke(this, new object[] { subTarget });
            }
        }

        void ClearUnusedData()
        {
            for (int i = 0; i < _datas.Count; i++)
            {
                var data = _datas[i];
                if (data.value is null)
                    continue;

                var type = data.value.GetType();
                var methodInfo = typeof(UltraRPTarget).GetMethod(nameof(ValidateDataForSubTarget));
                var genericMethodInfo = methodInfo.MakeGenericMethod(type);
                genericMethodInfo.Invoke(this, new object[] { _activeSubTarget.value, data.value });
            }
        }

        public void SetDataOnSubTarget<T>(SubTarget subTarget) where T : JsonObject
        {
            if (!(subTarget is IRequiresData<T> requiresData))
                return;

            T data = null;
            foreach (var x in _datas.SelectValue())
            {
                if (x is T y)
                {
                    data = y;
                    break;
                }
            }

            if (data == null)
            {
                data = Activator.CreateInstance(typeof(T)) as T;
                _datas.Add(data);
            }

            requiresData.data = data;
        }

        public void ValidateDataForSubTarget<T>(SubTarget subTarget, T data) where T : JsonObject
        {
            if (!(subTarget is IRequiresData<T> requiresData))
            {
                _datas.Remove(data);
            }
        }

        public override void OnBeforeSerialize()
        {
            ClearUnusedData();
        }

        public override bool WorksWithSRP(RenderPipelineAsset scriptableRenderPipeline)
        {
            return scriptableRenderPipeline?.GetType() == typeof(UltraRenderPipelineAsset);
        }

#if HAS_VFX_GRAPH
        public void ConfigureContextData(VFXContext context, VFXTaskCompiledData data)
        {
            if (!(_activeSubTarget.value is IRequireVFXContext vfxSubtarget))
                return;

            vfxSubtarget.ConfigureContextData(context, data);
        }
#endif

        public bool CanSupportVFX()
        {
            // The Unity 2022 adapter does not include the VFX Graph integration.
            return false;
        }

        public bool SupportsVFX() => CanSupportVFX() && _supportVFX;

        #region Metadata
        string IHasMetadata.identifier
        {
            get
            {
                if (_activeSubTarget.value is IHasMetadata subTargetHasMetaData)
                    return subTargetHasMetaData.identifier;
                return null;
            }
        }

        ScriptableObject IHasMetadata.GetMetadataObject(GraphDataReadOnly graph)
        {
            if (_activeSubTarget.value is IHasMetadata subTargetHasMetaData)
                return subTargetHasMetaData.GetMetadataObject(graph);
            return null;
        }
        #endregion
    }
}
