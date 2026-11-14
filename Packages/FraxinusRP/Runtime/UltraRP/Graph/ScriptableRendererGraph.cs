using FraxinusEngine.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [Flags]
    public enum RenderPathCompatibility
    {
        /// <summary>Forward Rendering Path</summary>
        Forward = 1 << 0,
        /// <summary>Deferred Rendering Path</summary>
        Deferred = 1 << 1,
        /// <summary>Forward+ Rendering Path</summary>
        ForwardPlus = 1 << 2,
        /// <summary>All Rendering Paths</summary>
        All = Forward | Deferred | ForwardPlus
    }

    [AttributeUsage(AttributeTargets.Field)]
    sealed class RenderPathCompatibleAttribute : Attribute
    {
        public RenderPathCompatibility renderPath;

        public RenderPathCompatibleAttribute(RenderPathCompatibility renderPath)
        {
            this.renderPath = renderPath;
        }
    }

    public enum DepthFormat
    {
        /// <summary>
        /// Default format for Android and Switch platforms is <see cref="GraphicsFormat.D24_UNorm_S8_UInt"/> and <see cref="GraphicsFormat.D32_SFloat_S8_UInt"/> for other platforms
        /// </summary>
        [RenderPathCompatible(RenderPathCompatibility.All)]
        Default,

        /// <summary>
        /// Format containing 16 unsigned normalized bits in depth component. Corresponds to <see cref="GraphicsFormat.D16_UNorm"/>.
        /// </summary>
        [RenderPathCompatible(RenderPathCompatibility.Forward | RenderPathCompatibility.ForwardPlus)]
        Depth_16 = GraphicsFormat.D16_UNorm,

        /// <summary>
        /// Format containing 24 unsigned normalized bits in depth component. Corresponds to <see cref="GraphicsFormat.D24_UNorm"/>.
        /// </summary>
        [RenderPathCompatible(RenderPathCompatibility.Forward | RenderPathCompatibility.ForwardPlus)]
        Depth_24 = GraphicsFormat.D24_UNorm,

        /// <summary>
        /// Format containing 32 signed float bits in depth component. Corresponds to <see cref="GraphicsFormat.D32_SFloat"/>.
        /// </summary>
        [RenderPathCompatible(RenderPathCompatibility.Forward | RenderPathCompatibility.ForwardPlus)]
        Depth_32 = GraphicsFormat.D32_SFloat,

        /// <summary>
        /// Format containing 16 unsigned normalized bits in depth component and 8 unsigned integer bits in stencil. Corresponds to <see cref="GraphicsFormat.D16_UNorm_S8_UInt"/>.
        /// </summary>
        [RenderPathCompatible(RenderPathCompatibility.All)]
        Depth_16_Stencil_8 = GraphicsFormat.D16_UNorm_S8_UInt,

        /// <summary>
        /// Format containing 24 unsigned normalized bits in depth component and 8 unsigned integer bits in stencil. Corresponds to <see cref="GraphicsFormat.D24_UNorm_S8_UInt"/>.
        /// </summary>
        [RenderPathCompatible(RenderPathCompatibility.All)]
        Depth_24_Stencil_8 = GraphicsFormat.D24_UNorm_S8_UInt,

        /// <summary>
        /// Format containing 32 signed float bits in depth component and 8 unsigned integer bits in stencil. Corresponds to <see cref="GraphicsFormat.D32_SFloat_S8_UInt"/>.
        /// </summary>
        [RenderPathCompatible(RenderPathCompatibility.All)]
        Depth_32_Stencil_8 = GraphicsFormat.D32_SFloat_S8_UInt,
    }


    [Icon("UnityEngine/Rendering/RenderPipelineAsset Icon")]
    public abstract class ScriptableRendererGraph : BaseGraph
    {
        public bool globalPreviewEnabled = true;

        // Store only overrides so existing graphs and newly discovered passes stay enabled.
        // Exclude assembly versions so package upgrades preserve the user's choices.
        [SerializeField, HideInInspector]
        private List<string> _disabledInjectPasses = new List<string>();

        private static readonly Dictionary<Type, string> s_injectPassTypeIds = new Dictionary<Type, string>();

        internal IReadOnlyList<string> disabledInjectPasses => _disabledInjectPasses ?? (IReadOnlyList<string>)Array.Empty<string>();
        internal event Action injectPassSettingsChanged;

        /// <summary>Whether this graph permits an injected pass type to execute.</summary>
        public bool IsInjectPassEnabled(Type passType)
        {
            string typeId = GetInjectPassTypeId(passType);
            return _disabledInjectPasses == null || !_disabledInjectPasses.Contains(typeId);
        }

        /// <summary>
        /// Enables or disables all injections of a pass type in this graph, including fallbacks.
        /// The next render reads this setting without recreating the renderer or its modules.
        /// </summary>
        public void SetInjectPassEnabled(Type passType, bool enabled)
        {
            string typeId = GetInjectPassTypeId(passType);
            bool changed = false;
            if (enabled)
                changed = _disabledInjectPasses != null && _disabledInjectPasses.RemoveAll(id => id == typeId) > 0;
            else
            {
                _disabledInjectPasses ??= new List<string>();
                if (!_disabledInjectPasses.Contains(typeId))
                {
                    _disabledInjectPasses.Add(typeId);
                    changed = true;
                }
            }
            if (changed)
                injectPassSettingsChanged?.Invoke();
        }

        private static string GetInjectPassTypeId(Type passType)
        {
            if (passType == null)
                throw new ArgumentNullException(nameof(passType));
            if (!typeof(InjectPass).IsAssignableFrom(passType))
                throw new ArgumentException("The type must derive from InjectPass.", nameof(passType));

            if (!s_injectPassTypeIds.TryGetValue(passType, out string typeId))
            {
                typeId = passType.FullName + ", " + passType.Assembly.GetName().Name;
                s_injectPassTypeIds.Add(passType, typeId);
            }
            return typeId;
        }
        
        [HideInInspector]
        public int previewCameraInstanceID = 0;

        [NonSerialized] RenderDrivenPass _drivenNode;
        [NonSerialized] PostProcessPass _postProcessNode;

        public PostProcessData postProcessData = null;

        public RenderDrivenPass drivenNode
        {
            get
            {
#if !UNITY_EDITOR
                if (_drivenNode == null)
#endif
                _drivenNode = nodes.FirstOrDefault(n => n is RenderDrivenPass) as RenderDrivenPass;

                return _drivenNode;
            }

            internal set => _drivenNode = value;
        }


        public MaterialLibrary materialLib = new MaterialLibrary();


        public PostProcessPass postNode
        {
            get
            {
#if !UNITY_EDITOR
                if (_postProcessNode == null)
#endif
                    _postProcessNode = nodes.FirstOrDefault(n => n is PostProcessPass) as PostProcessPass;

                return _postProcessNode;
            }

            internal set => _postProcessNode = value;
        }

        public PassNodeSettings settings = new PassNodeSettings()
        {
            // Default graph values:
            width = 1024,
            height = 1024,
            volumeDepth = 1,
            resolutionScale = 1,
            volumeDepthScale = 1,
        };

        public bool isParameterViewOpen;

        internal bool isInvalidated { get; set; }

        protected abstract ScriptableRenderer Create();

        public ScriptableRendererGraph()
        {
            base.onEnabled += Enabled;
        }

        protected override void OnEnable()
        {
            SanitizeSettings();
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
        }

        public new void SetDirty()
        {
            isInvalidated = true;
        }

        public void UpdateNodeInheritanceMode()
        {
            foreach (var node in nodes)
            {
                if (node is ScriptableRenderPassNode n)
                {

                    //n.settings.SyncInheritanceMode(defaultNodeInheritanceMode);
                }
            }
        }

        internal ScriptableRenderer InternalCreateRenderer()
        {
            isInvalidated = false;
            return Create();
        }

        internal void InitPostProcessData()
        {
            if (postProcessData)
            {
                materialLib?.Cleanup();
                materialLib.Init(postProcessData);
            }
        }

        protected void ReloadAllNullProperties()
        {
#if UNITY_EDITOR
            ResourceReloader.TryReloadAllNullIn(this, UltraRenderPipelineAsset.packagePath);
            if (postProcessData != null)
                ResourceReloader.TryReloadAllNullIn(postProcessData, UltraRenderPipelineAsset.packagePath);
#endif
        }

        void Enabled()
        {
            if (drivenNode == null)
                drivenNode = AddNode(BaseNode.CreateFromType<RenderDrivenPass>(new Vector2(-500, 0))) as RenderDrivenPass;

            if (postNode == null)   
                postNode = AddNode(BaseNode.CreateFromType<PostProcessPass>(new Vector2(0, 0))) as PostProcessPass;

            ReloadAllNullProperties();
        }

        void SanitizeSettings()
        {
            if (settings.potSize == 0)
                settings.SetPOTSize(1024);

            if (settings.resolutionScale == 0)
                settings.resolutionScale = 1;

            if (settings.volumeDepthScale == 0)
                settings.volumeDepthScale = 1;
        }

#if UNITY_EDITOR
        internal virtual Material GetDefaultMaterial(DefaultMaterialType materialType)
        {
            return null;
        }

        internal virtual Shader GetDefaultShader()
        {
            return null;
        }
#endif
    }
}
