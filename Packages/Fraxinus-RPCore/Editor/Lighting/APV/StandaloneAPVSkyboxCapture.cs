using System;
using UnityEditor;
using UnityEngine;

namespace UnityEngine.Rendering
{
    // Draw the environment directly, as Unity's CubemapRender does. Camera.Render
    // enters the active SRP and can inherit the editor window's graphics state.
    internal static class StandaloneAPVSkyboxCapture
    {
        const string k_MeshPath = "Packages/com.unity.render-pipelines.core/Editor/Lighting/APV/Meshes/";
        static readonly int[] s_FaceToSkyboxPass = { 2, 3, 4, 5, 0, 1 };
        static readonly Matrix4x4[] s_FaceViews =
        {
            new Matrix4x4(new Vector4(0, 0, -1, 0), new Vector4(0, 1, 0, 0), new Vector4(-1, 0, 0, 0), new Vector4(0, 0, 0, 1)),
            new Matrix4x4(new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, 0), new Vector4(1, 0, 0, 0), new Vector4(0, 0, 0, 1)),
            new Matrix4x4(new Vector4(1, 0, 0, 0), new Vector4(0, 0, -1, 0), new Vector4(0, -1, 0, 0), new Vector4(0, 0, 0, 1)),
            new Matrix4x4(new Vector4(1, 0, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 0, 1)),
            new Matrix4x4(new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, -1, 0), new Vector4(0, 0, 0, 1)),
            new Matrix4x4(new Vector4(-1, 0, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 0, 0, 1))
        };

        // Matches ShaderLibrary/Sampling/Sampling.hlsl's cubemap face basis,
        // and the pixel order returned by Cubemap.GetPixels.
        internal static Vector3 FaceDirection(CubemapFace face, float u, float v)
        {
            Vector3 direction;
            switch (face)
            {
                case CubemapFace.PositiveX: direction = new Vector3(1, -v, -u); break;
                case CubemapFace.NegativeX: direction = new Vector3(-1, -v, u); break;
                case CubemapFace.PositiveY: direction = new Vector3(u, 1, v); break;
                case CubemapFace.NegativeY: direction = new Vector3(u, -1, -v); break;
                case CubemapFace.PositiveZ: direction = new Vector3(u, -v, 1); break;
                case CubemapFace.NegativeZ: direction = new Vector3(-u, -v, -1); break;
                default: throw new ArgumentOutOfRangeException(nameof(face));
            }
            return direction.normalized;
        }

        // Returns a readable linear cube owned by the caller. Apply ambient
        // intensity once while copying the captured material radiance.
        internal static Cubemap Capture(Material source, Light sun, int size, float intensity = 1.0f)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size));

            bool sixFaces = source.passCount == 6;
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(k_MeshPath +
                (sixFaces ? "6FaceSkyboxMesh.mesh" : "SkyboxMesh.mesh"));
            if (mesh == null)
                throw new InvalidOperationException("Standalone APV skybox capture mesh is missing.");

            var previousTarget = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            bool previousInvertCulling = GL.invertCulling;
            Material material = null;
            RenderTexture target = null;
            Texture2D readback = null;
            Cubemap result = null;
            CommandBuffer command = null;
            try
            {
                material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
                material.EnableKeyword("_SUNDISK_NONE");
                var properties = new MaterialPropertyBlock();
                properties.SetVector("_LightColor0", sun != null
                    ? (Vector4)(sun.color.linear * Mathf.Max(0.0f, sun.intensity))
                    : Vector4.zero);
                properties.SetVector("_WorldSpaceLightPos0", sun != null
                    ? (Vector4)(-sun.transform.forward)
                    : new Vector4(0, 0, -1, 0));

                bool half = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);
                target = new RenderTexture(size, size, 0,
                    half ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear)
                {
                    name = "Standalone APV Environment Capture",
                    hideFlags = HideFlags.HideAndDontSave,
                    dimension = TextureDimension.Cube,
                    useMipMap = false,
                    autoGenerateMips = false,
                    antiAliasing = 1,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                if (!target.Create())
                    throw new InvalidOperationException("Unable to create standalone APV HDR skybox target.");
                readback = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                result = new Cubemap(size, half ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat, false)
                {
                    name = "Standalone APV Environment",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                var projection = Matrix4x4.Perspective(90.0f, 1.0f, 0.1f, 10.0f);
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 ||
                    SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore)
                    projection.SetColumn(1, -projection.GetColumn(1));

                command = new CommandBuffer { name = "Standalone APV Skybox Capture" };
                command.DisableScissorRect();
                command.SetInvertCulling(false);
                ShaderUtil.SetAsyncCompilation(command, false);
                for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
                {
                    command.SetViewProjectionMatrices(s_FaceViews[faceIndex], projection);
                    command.SetRenderTarget(new RenderTargetIdentifier(target, 0, (CubemapFace)faceIndex));
                    command.SetViewport(new Rect(0, 0, size, size));
                    command.ClearRenderTarget(false, true, Color.black);
                    int pass = sixFaces ? s_FaceToSkyboxPass[faceIndex] : 0;
                    command.DrawMesh(mesh, Matrix4x4.identity, material, sixFaces ? pass : 0, pass, properties);
                }
                ShaderUtil.RestoreAsyncCompilation(command);
                command.SetInvertCulling(previousInvertCulling);
                GL.sRGBWrite = false;
                Graphics.ExecuteCommandBuffer(command);

                for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
                {
                    Graphics.SetRenderTarget(target, 0, (CubemapFace)faceIndex);
                    readback.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                    var pixels = readback.GetPixels();
                    // ReadPixels starts at the bottom of the render target.
                    // Restore native cube face rows on top-origin graphics APIs.
                    if (SystemInfo.graphicsUVStartsAtTop)
                    {
                        for (int y = 0; y < size / 2; ++y)
                            for (int x = 0; x < size; ++x)
                            {
                                int a = y * size + x;
                                int b = (size - 1 - y) * size + x;
                                var pixel = pixels[a];
                                pixels[a] = pixels[b];
                                pixels[b] = pixel;
                            }
                    }
                    if (!Mathf.Approximately(intensity, 1.0f))
                    {
                        for (int i = 0; i < pixels.Length; ++i)
                            pixels[i] *= intensity;
                    }
                    result.SetPixels(pixels, (CubemapFace)faceIndex, 0);
                }
                result.Apply(false, false);
                return result;
            }
            catch
            {
                if (result != null)
                    UnityEngine.Object.DestroyImmediate(result);
                throw;
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSRGB;
                GL.invertCulling = previousInvertCulling;
                command?.Dispose();
                if (material != null)
                    UnityEngine.Object.DestroyImmediate(material);
                if (readback != null)
                    UnityEngine.Object.DestroyImmediate(readback);
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
        }
    }
}
