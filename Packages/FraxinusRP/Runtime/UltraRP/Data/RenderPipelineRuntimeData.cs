using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, ReloadGroup]
    public class RenderPipelineRuntimeData : ScriptableObject
    {
        [Serializable, ReloadGroup]
        public sealed class ShaderResources
        {
            // Debug
            [Reload("Resources/Shaders/UltraRP/Debug/ProbeVolumeDebug.shader")]
            public Shader probeVolumeDebugShader;
        }

        [Serializable, ReloadGroup]
        public sealed class AssetResources
        {
            [Reload("Resources/UltraRPRes/Mesh/ProbeDebugSphere.fbx")]
            public Mesh probeDebugSphere;
        }

        public ShaderResources shaders;
        public AssetResources assets;
    }
}
