using System;
using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    [Flags]
    public enum EditFlags
    {
        None            = 0,
        Width           = 1 << 0,
        SizeMode        = 1 << 1,
        Height          = 1 << 2,
        Depth           = 1 << 4,
        Dimension       = 1 << 6,
        TargetFormat    = 1 << 7,
        POTSize         = 1 << 8,

        Size            = SizeMode | Width | Height | Depth,
        Format          = POTSize | Dimension | TargetFormat,

        All             = ~0,
    }

    [Flags]
    public enum PreviewChannels
    {
        R = 1,
        G = 2,
        B = 4,
        A = 8,
        RG = R | G,
        RB = R | B,
        GB = G | B,
        RGB = R | G | B,
        RGBA = R | G | B | A,
    }

    public enum POTSize
    {
        _32     = 32,
        _64     = 64,
        _128    = 128,
        _256    = 256,
        _512    = 512,
        _1024   = 1024,
        Custom  = -1,
    }

    public enum NodeSettingSizeMode
    {
        InheritFromGraph = -1,
        Absolute = 1,
    }

    public enum RefreshMode
    {
        EveryXFrame,
        EveryXMillis,
        EveryXSeconds,
    }


    [Serializable]
    public class PassNodeSettings
    {
        public const int maxTextureResolution = 2048;

        [Range(0.0001f, 1.0f)] public float resolutionScale = 1.0f;
        [Range(0.0001f, 1.0f)] public float heightScale = 1.0f;
        [Range(0.0001f, 1.0f)] public float volumeDepthScale = 1.0f;
        [Min(1)] public int width;
        [Min(1)] public int height;
        [Min(1)] public int volumeDepth;

        public POTSize              potSize;
        public NodeSettingSizeMode  sizeMode;
        public EditFlags            editFlags;
        public RefreshMode          refreshMode;
        public float                period;

        public bool                 showPreview = true;
        public bool                 isPreviewCollapsed = false;

        public PreviewChannels      previewChannels;
        public bool                 previewSRGB;
        public float                previewSlice = 0;

        [NonSerialized]
        ScriptableRenderPassNode _node;

        [NonSerialized]
        internal PassNodeSettings resolvedSettings;

        public static PassNodeSettings defaultValue
        {
            get => new PassNodeSettings()
            {
                resolutionScale = 1.0f,
                width = 1024,
                height = 1024,
                volumeDepth = 1,
                sizeMode = NodeSettingSizeMode.InheritFromGraph,
                editFlags = ~EditFlags.POTSize,
                refreshMode = RefreshMode.EveryXSeconds,

                showPreview = true,
                isPreviewCollapsed = false,

                previewChannels = PreviewChannels.RGBA,
                previewSRGB = false
            };
        }

        public PassNodeSettings()
        {
            resolvedSettings = this;
        }

        public PassNodeSettings Clone()
        {
            return new PassNodeSettings
            {
                resolutionScale = resolutionScale,
                volumeDepthScale = volumeDepthScale,
                width = width,
                height = height,
                volumeDepth = volumeDepth,
                sizeMode = sizeMode,
                potSize = potSize,
                editFlags = editFlags,
                refreshMode = refreshMode,
                period = period,
            };
        }

        public void ResolveAndUpdate(ScriptableRenderPassNode node)
        {
            _node = node;
            var graph = _node.graph;

            if (resolutionScale == 0)
                resolutionScale = 1;
            if (volumeDepthScale == 0)
                volumeDepthScale = 1;

            if (resolvedSettings == this || resolvedSettings == null)
                resolvedSettings = new PassNodeSettings();

            resolvedSettings.width = Mathf.Clamp(ResolveWidth(node, graph), 1, maxTextureResolution);
            resolvedSettings.height = Mathf.Clamp(ResolveHeight(node, graph), 1, maxTextureResolution);
            resolvedSettings.volumeDepth = Mathf.Clamp(ResolveVolumeDepth(node, graph), 1, maxTextureResolution);
            resolvedSettings.resolutionScale = resolutionScale;
            resolvedSettings.volumeDepthScale = volumeDepthScale;
            resolvedSettings.potSize = potSize;
            resolvedSettings.refreshMode = refreshMode;

            int ResolveWidth(ScriptableRenderPassNode node, ScriptableRendererGraph graph)
            {
                switch (node.nodeSettings.sizeMode)
                {
                    default:
                    case NodeSettingSizeMode.InheritFromGraph:
                        return (int)(graph.settings.width * node.nodeSettings.resolutionScale);
                    case NodeSettingSizeMode.Absolute:
                        return node.nodeSettings.width;
                }
            }

            int ResolveHeight(ScriptableRenderPassNode node, ScriptableRendererGraph graph)
            {
                switch (node.nodeSettings.sizeMode)
                {
                    default:
                    case NodeSettingSizeMode.InheritFromGraph:
                        return (int)(graph.settings.height * node.nodeSettings.resolutionScale);
                    case NodeSettingSizeMode.Absolute:
                        return node.nodeSettings.height;
                }
            }

            int ResolveVolumeDepth(ScriptableRenderPassNode node, ScriptableRendererGraph graph)
            {
                switch (node.nodeSettings.sizeMode)
                {
                    default:
                    case NodeSettingSizeMode.InheritFromGraph:
                        return (int)(graph.settings.volumeDepth * node.nodeSettings.volumeDepth);
                    case NodeSettingSizeMode.Absolute:
                        return node.nodeSettings.volumeDepth;
                }
            }
        }

        public float GetUpdatePeriodInMilliseconds()
        {
            switch (refreshMode)
            {
                case RefreshMode.EveryXFrame:
                    return (1.0f / Application.targetFrameRate) * period;
                case RefreshMode.EveryXMillis:
                    return period / 1000.0f;
                case RefreshMode.EveryXSeconds:
                    return period;
                default:
                    return 0;
            }
        }

        public bool CanEdit(EditFlags flag) => (this.editFlags & flag) != 0;

        public int GetResolvedWidth() => resolvedSettings.width;

        public int GetResolvedHeight() => resolvedSettings.height;

        public int GetResolvedDepth() => resolvedSettings.volumeDepth;

        public void SetPOTSize(int size)
        {
            potSize = (POTSize)Mathf.ClosestPowerOfTwo(size);
            width = height = (int)potSize;
        }
    }

    public static class RendererGraphEnumExtension
    {
        public static bool Inherits(this NodeSettingSizeMode mode) => (int)mode <= 0;
    }
}
