using System;
using System.ComponentModel;
using UnityEngine.Rendering;
using UnityEngine.Scripting.APIUpdating;

namespace UnityEngine.Rendering.RenderGraphModule.Util
{
    [Serializable]
    [HideInInspector]
    [Category("Resources/Render Graph Helper Function Resources")]
    [SupportedOnRenderPipeline]
    // The Unity 6 source moved this settings object from the Experimental
    // RenderGraph namespace to the public RenderGraphModule namespace.  Keep
    // the migration metadata on the 2022 backport so already-serialized
    // UniversalRenderPipelineGlobalSettings assets resolve the old
    // ``UnityEngine.Experimental.Rendering.RenderGraphModule.Util`` type
    // instead of losing the CoreCopy shader reference on import.
    [MovedFrom(true, "UnityEngine.Experimental.Rendering.RenderGraphModule.Util", "Unity.RenderPipelines.Core.Runtime")]
    class RenderGraphUtilsResources : IRenderPipelineResources
    {
        public bool isAvailableInPlayerBuild => true;
        public enum Version
        {
            Initial,

            Count,
            Latest = Count - 1
        }
        [SerializeField, HideInInspector] Version m_Version = Version.Latest;
        int IRenderPipelineGraphicsSettings.version => (int)m_Version;

        [SerializeField, ResourcePath("Shaders/CoreCopy.shader")]
        internal Shader m_CoreCopyPS;

        /// <summary>
        /// Core Copy shader.
        /// </summary>
        public Shader coreCopyPS
        {
            get => m_CoreCopyPS;
            set => this.SetValueAndNotify(ref m_CoreCopyPS, value, nameof(m_CoreCopyPS));
        }
    }
}
