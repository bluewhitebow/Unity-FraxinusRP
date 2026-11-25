using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, ReloadGroup, ExcludeFromPreset]
    public class UltraRendererGraph : ScriptableRendererGraph
    {
        [SerializeField] IntermediateTextureMode _intermediateTextureMode = IntermediateTextureMode.Always;

        public IntermediateTextureMode intermediateTextureMode
        {
            get => _intermediateTextureMode;
            set
            {
                SetDirty();
                _intermediateTextureMode = value;
            }
        }

        [SerializeField] DepthPrimingMode _depthPrimingMode = DepthPrimingMode.Auto;
        public DepthPrimingMode depthPrimingMode
        {
            get => _depthPrimingMode;
            set
            {
                SetDirty();
                _depthPrimingMode = value;
            }
        }

        [SerializeField] bool _useNativePassRenderGraphCompiler = true;

        public bool useNativePassRenderGraphCompile
        {
            get => _useNativePassRenderGraphCompiler;
            set
            {
                _useNativePassRenderGraphCompiler = value;
            }
        }

        protected override ScriptableRenderer Create()
        {
            if (!Application.isPlaying)
                ReloadAllNullProperties();

            return new UltraRenderer(this);
        }
    }
}
