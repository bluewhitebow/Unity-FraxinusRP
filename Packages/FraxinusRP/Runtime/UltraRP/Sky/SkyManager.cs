using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Resolution of the sky reflection cubemap.
    /// </summary>
    [Serializable]
    public enum SkyResolution
    {
        /// <summary>128x128 per face.</summary>
        SkyResolution128 = 128,
        /// <summary>256x256 per face.</summary>
        SkyResolution256 = 256,
        /// <summary>512x512 per face.</summary>
        SkyResolution512 = 512,
        /// <summary>1024x1024 per face.</summary>
        SkyResolution1024 = 1024,
        /// <summary>2048x2048 per face.</summary>
        SkyResolution2048 = 2048,
        /// <summary>4096x4096 per face.</summary>
        SkyResolution4096 = 4096
    }

    /// <summary>
    /// Environment lighting update mode.
    /// </summary>
    public enum EnvironmentUpdateMode
    {
        /// <summary>Environment lighting is updated when the sky has changed.</summary>
        OnChanged = 0,
        /// <summary>Environment lighting is updated on demand.</summary>
        OnDemand,
        /// <summary>Environment lighting is updated in real time.</summary>
        Realtime
    }

    /// <summary>
    /// Parameters passed to sky rendering functions.
    /// </summary>
    public class BuiltinSkyParameters
    {
        /// <summary>Matrix mapping pixel coordinate to view direction.</summary>
        public Matrix4x4 pixelCoordToViewDirMatrix;
        /// <summary>World space camera position.</summary>
        public Vector3 worldSpaceCameraPos;
        /// <summary>Camera view matrix.</summary>
        public Matrix4x4 viewMatrix;
        /// <summary>Screen size: Width, height, inverse width, inverse height.</summary>
        public Vector4 screenSize;
        /// <summary>Command buffer used for rendering.</summary>
        public CommandBuffer commandBuffer;
        /// <summary>Current sun light.</summary>
        public Light sunLight;
        /// <summary>Color buffer used for rendering.</summary>
        public RTHandle colorBuffer;
        /// <summary>Depth buffer used for rendering.</summary>
        public RTHandle depthBuffer;
        /// <summary>Fullscreen texture rendering transmittance (1 - opacity) of the cloud and fog.
        /// This texture also contain the transmittance used for the multiple scattering in the Y component.</summary>
        public RTHandle cloudOpacity;
        /// <summary>Ambient probe containing sky lighting to be used when rendering clouds</summary>
        public GraphicsBuffer cloudAmbientProbe;
        /// <summary>Current frame index.</summary>
        public int frameIndex;
        /// <summary>Current sky settings.</summary>
        public SkySettings skySettings;
        /// <summary>Null color buffer render target identifier.</summary>
        public static RenderTargetIdentifier nullRT = -1;
        /// <summary>Index of the current cubemap face to render (Unknown for texture2D).</summary>
        public CubemapFace cubemapFace = CubemapFace.Unknown;

        /// <summary>
        /// Copy content of this BuiltinSkyParameters to another instance.
        /// </summary>
        /// <param name="other">Other instance to copy to.</param>
        public void CopyTo(BuiltinSkyParameters other)
        {
            other.pixelCoordToViewDirMatrix = pixelCoordToViewDirMatrix;
            other.worldSpaceCameraPos = worldSpaceCameraPos;
            other.viewMatrix = viewMatrix;
            other.screenSize = screenSize;
            other.commandBuffer = commandBuffer;
            other.sunLight = sunLight;
            other.colorBuffer = colorBuffer;
            other.depthBuffer = depthBuffer;
            other.frameIndex = frameIndex;
            other.skySettings = skySettings;
            other.cubemapFace = cubemapFace;
        }
    }

    struct CachedSkyContext
    {
        public Type type;
        public SkyRenderingContext renderingContext;
        public int hash;
        public int refCount;

        public void Reset()
        {
            // We keep around the rendering context to avoid useless allocation if they get reused.
            hash = 0;
            refCount = 0;
            if (renderingContext != null)
                renderingContext.Reset();
        }

        public void Cleanup()
        {
            Reset();

            if (renderingContext != null)
            {
                renderingContext.Cleanup();
                renderingContext = null;
            }
        }
    }

    class SkyManager
    {
        //Material m_StandardSkyboxMaterial; // This is the Unity standard skybox material. Used to pass the correct cubemap to Enlighten.
        //Material m_BlitCubemapMaterial;
        //Material m_OpaqueAtmScatteringMaterial;
        //int[] m_OpaqueFogPassNames;

        //SphericalHarmonicsL2 m_BlackAmbientProbe = new SphericalHarmonicsL2();

        //bool m_UpdateRequired = false;
        //bool m_StaticSkyUpdateRequired = false;
        //int m_Resolution, m_LowResolution;

        // This interpolation volume stack is used to interpolate the lighting override separately from the visual sky.
        // If a sky setting is present in this volume then it will be used for lighting override.
        public VolumeStack lightingOverrideVolumeStack { get; private set; }
        public LayerMask lightingOverrideLayerMask { get; private set; } = -1;

    }
}
