using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    public abstract partial class ScriptableRenderPassNode : BaseNode
    {
        public const int textureArrayPreviewIndexOffset = 1000;
        public const int textureArrayMaxElements = 8; // 支持预览数组的前 8 个元素

        private interface IConfigStore
        {
            void Reset();
            void ApplyAll();
            void Clear();
        }

        private class TypedConfigStore<T> : IConfigStore
        {
            public struct PortValueConfig
            {
                public NodeParameter<T> port;
                public T value;
            }

            private PortValueConfig[] _configs = new PortValueConfig[8];
            private int _count = 0;

            public void Reset()
            {
                _count = 0;
            }

            public void ApplyAll()
            {
                for (int i = 0; i < _count; i++)
                    _configs[i].port.SetValue(_configs[i].value);
            }

            public void Add(NodeParameter<T> port, T value)
            {
                if (port == null) return;

                if (_count >= _configs.Length)
                {
                    var newArray = new PortValueConfig[_configs.Length * 2];
                    Array.Copy(_configs, newArray, _configs.Length);
                    _configs = newArray;
                }

                _configs[_count].port = port;
                _configs[_count].value = value;
                _count++;
            }

            public void Clear()
            {
                for (int i = 0; i < _count; i++)
                {
                    _configs[i].port = null;
                    _configs[i].value = default(T);
                }
                _count = 0;
                _configs = null;
            }

            public T GetValue(int index)
            {
                if (index >= 0 && index < _count)
                {
                    return _configs[index].value;
                }
                return default(T);
            }
            
            public int count => _count;
        }

        private readonly Dictionary<Type, IConfigStore> _configStores = new Dictionary<Type, IConfigStore>();
        
        [HideInInspector] 
        public int previewTextureIndex = -1;

        [HideInInspector, NonSerialized]
        public string debugInfo = string.Empty;
        
        /// <summary>
        /// 节点所属的上下文作用域ID
        /// "Main" 表示主作用域，其他值表示子作用域
        /// </summary>
        [HideInInspector, NonSerialized]
        internal string contextId = "Main";

        /// <summary>
        /// Append debug information that will be displayed in the node view.
        /// This is cleared at the beginning of each frame execution.
        /// </summary>
        public void AppendDebugInfo(string info)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(debugInfo))
                debugInfo = info;
            else
                debugInfo += "\n" + info;
#endif
        }

        /// <summary>
        /// Append debug information with a variable name and value.
        /// </summary>
        public void AppendDebugInfo(string variableName, object value)
        {
            AppendDebugInfo($"{variableName}: {value}");
        }

        public new ScriptableRendererGraph graph => base.graph as ScriptableRendererGraph;

        [HideInInspector]
        public PassNodeSettings             nodeSettings = PassNodeSettings.defaultValue;

        protected virtual PassNodeSettings  _defaultSettings => PassNodeSettings.defaultValue;

        public virtual bool                 hasSettings => true;

        //public event Action                 beforeProcessSetup;
        //public event Action                 afterProcessSetup;

        internal event Action               onEnabled;  

        public override bool                needsInspector => true;

        [HideInInspector]
        public bool                         isPinned;

        [SerializeField, HideInInspector]
        private bool _active = true;

        [HideInInspector]
        public bool active
        {
            get => _active;
            set
            {
                if (_active != value)
                {
                    _active = value;
                    graph?.UpdateComputeOrder();
                }
            }
        }

        protected RTHandle _previewTexture;
        private string _previewTextureName;
        public virtual Texture previewTexture
        {
            get
            {
                if (_previewTexture == null)
                    return Texture2D.blackTexture;

                return _previewTexture;
            }
        }

        public bool previewTextureValid => (previewTexture != null) && (previewTexture != Texture2D.blackTexture);

        public ScriptableRenderPassNode() : base()
        {
            profilingSampler = new ProfilingSampler(this.GetType().Name);
        }

        public override void OnNodeCreated()
        {
            base.OnNodeCreated();
            nodeSettings = _defaultSettings;
        }

        protected override void Enable()
        {
            base.Enable();

            onEnabled?.Invoke();
        }

        protected override void Disable()
        {
            base.Disable();
            
            ClearAllPortConfigurations();
            ClearAllInjectPasses();
        }

        protected sealed override void Process() 
        {
            Debug.LogError($"[ScriptableRenderPassNode] Process() should not be called directly on {this.name}. Use OnProcess(RenderGraph, ...) instead.");
        }

        public void OnPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            if (active)
            {
                using (new ProfilingScope(Profiling.preCullSampler))
                    OnCameraPreCull(renderer, cameraData);
            }
        }

        public void OnProcess(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            ProcessNodeInjectPasses(renderGraph, context, frameData, true);

            using (new ProfilingScope(Profiling.pullDatasSampler))
                inputPorts.PullDatas();

            ResetAllPortConfigurations();

            SetupPass();

            // Clear debug info before execution
            debugInfo = string.Empty;

            if (active && SupportedInCurrentFrame(frameData))
            {
                using (new ProfilingScope(Profiling.excuteSampler))
                    Execute(renderGraph, context, frameData);
            }

            InvokePreviewProcessed(renderGraph, context, frameData);

            InvokeOnProcessed();

            ApplyAllPortConfigurations();

            using (new ProfilingScope(Profiling.pushDatasSampler))
                outputPorts.PushDatas();

            ProcessNodeInjectPasses(renderGraph, context, frameData, false);
        }

        public virtual void SetupPass() { }

        public virtual void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            Debug.LogWarning($"Execute RenderGraph is not implemented, the pass {this.ToString()} won't be recorded in the current RenderGraph.");
        }

        public virtual void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData) { }

        protected virtual void SetDirty() { }

#if UNITY_EDITOR
        internal override void OnValidate() => SetDirty();
#endif

        [IsCompatibleWithGraph]
        internal static bool IsNodeCompatibleWithGraph(BaseGraph graph)
        {
            return graph is ScriptableRendererGraph;
        }

        protected void ConfigurePortValue<T>(NodeParameter<T> port, T value)
        {
            if (port == null) return;

            var type = typeof(T);
            if (!_configStores.TryGetValue(type, out var store))
            {
                store = new TypedConfigStore<T>();
                _configStores[type] = store;
            }

            ((TypedConfigStore<T>)store).Add(port, value);
        }

        private void ResetAllPortConfigurations()
        {
            foreach (var store in _configStores.Values)
                store.Reset();
        }

        private void ApplyAllPortConfigurations()
        {
            foreach (var store in _configStores.Values)
                store.ApplyAll();
        }

        private void ClearAllPortConfigurations()
        {
            foreach (var store in _configStores.Values)
                store.Clear();

            _configStores.Clear();
        }

        private void InvokePreviewProcessed(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            CameraData cameraData = frameData.Get<CameraData>();
            if (!graph.globalPreviewEnabled || !nodeSettings.showPreview || cameraData.isPreviewCamera)
                return;

            var rendererGraph = graph as ScriptableRendererGraph;
            if (rendererGraph != null && rendererGraph.previewCameraInstanceID != 0)
            {
                if (cameraData.camera.GetInstanceID() != rendererGraph.previewCameraInstanceID)
                    return;
            }

            TextureHandle handleToPreview = TextureHandle.nullHandle;

            if (previewTextureIndex >= 0)
            {
                if (previewTextureIndex < textureArrayPreviewIndexOffset)
                {
                    handleToPreview = GetTextureHandleByIndex(previewTextureIndex);
                }
                else
                {
                    var rawIndex = previewTextureIndex - textureArrayPreviewIndexOffset;
                    var arrayIndex = rawIndex / textureArrayMaxElements;
                    var elementIndex = rawIndex % textureArrayMaxElements;

                    var arrayType = typeof(TextureHandle[]);
                    if (_configStores.TryGetValue(arrayType, out var arrayStore))
                    {
                        var array = ((TypedConfigStore<TextureHandle[]>)arrayStore).GetValue(arrayIndex);
                        if (array != null && elementIndex >= 0 && elementIndex < array.Length)
                        {
                            handleToPreview = array[elementIndex];
                        }
                    }
                }
            }
            
            if (!handleToPreview.IsValid())
            {
                handleToPreview = GetTextureHandleByIndex(0);

                if (!handleToPreview.IsValid())
                {
                    var bufferType = typeof(CameraTargetBuffer);
                    if (_configStores.TryGetValue(bufferType, out var bufferStore))
                    {
                        var buffer = ((TypedConfigStore<CameraTargetBuffer>)bufferStore).GetValue(0);
                        if (buffer.IsValid())
                            handleToPreview = buffer.color;
                    }
                }

                if (!handleToPreview.IsValid())
                {
                    var arrayType = typeof(TextureHandle[]);
                    if (_configStores.TryGetValue(arrayType, out var arrayStore))
                    {
                        var array = ((TypedConfigStore<TextureHandle[]>)arrayStore).GetValue(0);
                        if (array != null)
                        {
                            foreach (var h in array)
                            {
                                if (h.IsValid())
                                {
                                    handleToPreview = h;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (handleToPreview.IsValid())
            {
                UpdatePreviewRTHandle(renderGraph, handleToPreview);
                PreviewDebugPass.Render(renderGraph, handleToPreview, _previewTexture, cameraData);
            }
        }

        private TextureHandle GetTextureHandleByIndex(int index)
        {
            int currentIndex = 0;
            
            var textureType = typeof(TextureHandle);
            if (_configStores.TryGetValue(textureType, out var textureStore))
            {
                var store = (TypedConfigStore<TextureHandle>)textureStore;
                if (index < store.count)
                    return store.GetValue(index);
                currentIndex += store.count;
            }
            
            var bufferType = typeof(CameraTargetBuffer);
            if (_configStores.TryGetValue(bufferType, out var bufferStore))
            {
                var store = (TypedConfigStore<CameraTargetBuffer>)bufferStore;
                int bufferCount = store.count;
                
                for (int i = 0; i < bufferCount; i++)
                {
                    if (currentIndex == index)
                    {
                        var buffer = store.GetValue(i);
                        return buffer.color;
                    }
                    currentIndex++;
                    
                    if (currentIndex == index)
                    {
                        var buffer = store.GetValue(i);
                        return buffer.depth;
                    }
                    currentIndex++;
                }
            }
            
            return TextureHandle.nullHandle;
        }

        private void UpdatePreviewRTHandle(RenderGraph renderGraph, TextureHandle sourceHandle)
        {
            nodeSettings.ResolveAndUpdate(this);

            int width = nodeSettings.GetResolvedWidth();
            int height = nodeSettings.GetResolvedHeight();
            int volumeDepth = nodeSettings.GetResolvedDepth();

            RenderTargetInfo targetInfo = renderGraph.GetRenderTargetInfo(sourceHandle);

            float sourceAspect = (float)targetInfo.height / targetInfo.width;
            if (height > width * sourceAspect)
                height = Mathf.Max((int)(width * sourceAspect), 1);
            else
                width = Mathf.Max((int)(height / sourceAspect), 1);
            
            width = Mathf.Max(Mathf.Min(width, targetInfo.width), 1);
            height = Mathf.Max(Mathf.Min(height, targetInfo.height), 1);
            volumeDepth = Mathf.Max(volumeDepth, 1);

            var desc = new RenderTextureDescriptor();
            desc.width = width;
            desc.height = height;
            desc.volumeDepth = volumeDepth;
            desc.dimension = volumeDepth > 1 ? TextureDimension.Tex3D : TextureDimension.Tex2D;
            desc.msaaSamples = targetInfo.msaaSamples;
            desc.bindMS = targetInfo.bindMS;
            if (GraphicsFormatUtility.IsDepthFormat(targetInfo.format))
                desc.graphicsFormat = GraphicsFormat.R8_UNorm;
            else
                desc.graphicsFormat = targetInfo.format;

            _previewTextureName ??= $"{this.GetType().Name} Preview";
            RenderingUtils.ReAllocateHandleIfNeeded(ref _previewTexture, desc, name: _previewTextureName);
        }

        class PreviewDebugPass
        {
            internal static void Render(RenderGraph graph, TextureHandle srcHandle, RTHandle dest, CameraData cameraData)
            {
                var renderer = cameraData.renderer;
                ImportResourceParams importParams = new ImportResourceParams();
                importParams.clearOnFirstUse = false;
                importParams.discardOnLastUse = true;
                TextureHandle destHandle = graph.ImportTexture(dest, importParams);

                // isSourceTextureColor=false 强制使用 BlitPass，这样可以利用 Debug 材质正确处理深度图等格式的显示
                renderer.BlitToDebugTexture(graph, srcHandle, destHandle, isSourceTextureColor: false);
            }
        }
    }
}
