using System;
using System.Collections.Generic;
using Unity.Collections;

namespace UnityEngine.Rendering
{
    // Capture sky radiance independently of Unity's convolved ambient probe. The
    // resulting polynomial SH coefficients are consumed by APRT's irradiance
    // integral; no cosine convolution or ambient-intensity multiplier is applied.
    internal sealed class APRTSkyEnvironment : IDisposable
    {
        const int k_CaptureSize = 64;
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
        // Reciprocal basis norms, expressed relative to 1 / (4 pi).
        static readonly double[] s_Normalization = { 1, 3, 3, 3, 15, 15, 1.25, 15, 3.75 };
        static readonly int s_EnvironmentCube = Shader.PropertyToID("_APRTEnvironmentCube");
        static readonly int s_SunDisk = Shader.PropertyToID("_SunDisk");
        static readonly int s_AtmosphereThickness = Shader.PropertyToID("_AtmosphereThickness");
        static readonly int s_LightColor = Shader.PropertyToID("_LightColor0");
        static readonly int s_LightPosition = Shader.PropertyToID("_WorldSpaceLightPos0");

        struct ProjectionSample
        {
            public Vector3 direction;
            public double solidAngle;
        }

        internal Vector4[] radianceCoefficients { get; } = new Vector4[9];
        internal bool hasValidSource { get; private set; }
        Cubemap m_LastOverride;
        Material m_LastSkybox, m_CaptureMaterial;
        int m_LastMaterialCRC;
        ulong m_LastTextureVersions;
        readonly List<int> m_TextureProperties = new List<int>();
        Shader m_CubemapCaptureShader;
        int m_LastSunID;
        Vector3 m_LastSunForward;
        Color m_LastSunColor;
        float m_LastSunIntensity;
        bool m_HasSourceKey, m_Disposed;
        RenderTexture m_Target;
        Texture2D m_Readback;
        CommandBuffer m_Command;
        MaterialPropertyBlock m_CaptureProperties;
        Mesh m_SkyboxMesh, m_SixFacesMesh;
        NativeArray<ProjectionSample> m_ProjectionSamples;
        readonly double[] m_Sums = new double[27];
        readonly double[] m_Basis = new double[9];

        // A cache hit still returns true. False means there is no usable current
        // source; coefficients are then zero, rather than stale previous lighting.
        internal bool Update(Cubemap overrideTexture, Material skybox, Light sun)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(APRTSkyEnvironment));
            if (overrideTexture == null && skybox == null)
            {
                ClearCoefficients();
                m_HasSourceKey = false;
                return false;
            }

            int crc = overrideTexture == null ? skybox.ComputeCRC() : 0;
            if (overrideTexture == null && (!ReferenceEquals(skybox, m_LastSkybox) || crc != m_LastMaterialCRC))
                skybox.GetTexturePropertyNameIDs(m_TextureProperties);
            ulong textures = overrideTexture != null ? TextureVersion(overrideTexture) : MaterialTextureVersions(skybox);
            // Unity's cubemap, panoramic and six-sided skies do not consume the
            // sun. Avoid invalidating those sources for unrelated sun animation.
            bool proceduralSun = overrideTexture == null && skybox != null &&
                (skybox.HasProperty(s_SunDisk) || skybox.HasProperty(s_AtmosphereThickness));
            int sunID = proceduralSun && sun != null ? sun.GetInstanceID() : 0;
            Vector3 sunForward = sunID != 0 ? sun.transform.forward : Vector3.forward;
            Color sunColor = sunID != 0 ? sun.color : Color.black;
            float sunIntensity = sunID != 0 ? sun.intensity : 0;
            if (m_HasSourceKey && ReferenceEquals(overrideTexture, m_LastOverride) &&
                ReferenceEquals(overrideTexture != null ? null : skybox, m_LastSkybox) && crc == m_LastMaterialCRC &&
                textures == m_LastTextureVersions && sunID == m_LastSunID && sunForward == m_LastSunForward &&
                sunColor == m_LastSunColor && sunIntensity == m_LastSunIntensity)
                return hasValidSource;

            m_LastOverride = overrideTexture;
            m_LastSkybox = overrideTexture != null ? null : skybox;
            m_LastMaterialCRC = crc;
            m_LastTextureVersions = textures;
            m_LastSunID = sunID;
            m_LastSunForward = sunForward;
            m_LastSunColor = sunColor;
            m_LastSunIntensity = sunIntensity;
            m_HasSourceKey = true;
            try
            {
                PrepareResources(overrideTexture, skybox);
                Capture(proceduralSun ? sun : null);
                hasValidSource = true;
            }
            catch (Exception exception)
            {
                ClearCoefficients();
                // Cache a failed source too, so unsupported sky shaders cannot
                // trigger six readbacks or repeat warnings every frame.
                Debug.LogWarning($"APRT sky environment capture failed: {exception.Message}", overrideTexture != null ? (Object)overrideTexture : skybox);
            }
            return hasValidSource;
        }

        static ulong TextureVersion(Texture texture)
        {
            if (texture == null) return 0;
            return ((ulong)(uint)texture.GetInstanceID() << 32) | texture.updateCount;
        }

        ulong MaterialTextureVersions(Material material)
        {
            ulong hash = 14695981039346656037;
            foreach (var property in m_TextureProperties)
                unchecked { hash = (hash ^ TextureVersion(material.GetTexture(property))) * 1099511628211; }
            return hash;
        }

        void PrepareResources(Cubemap cube, Material source)
        {
            Shader shader;
            if (cube != null)
            {
                if (m_CubemapCaptureShader == null)
                    m_CubemapCaptureShader = Resources.Load<Shader>("APRTSkyCubemapCapture");
                shader = m_CubemapCaptureShader;
                if (shader == null || !shader.isSupported)
                    throw new InvalidOperationException("APRT cubemap capture shader is unavailable.");
            }
            else
            {
                shader = source.shader;
                if (shader == null || !shader.isSupported || source.passCount == 0)
                    throw new InvalidOperationException("The source skybox shader is unsupported.");
            }
            if (m_CaptureMaterial == null || m_CaptureMaterial.shader != shader)
            {
                DestroyResource(m_CaptureMaterial);
                m_CaptureMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (cube != null)
                m_CaptureMaterial.SetTexture(s_EnvironmentCube, cube);
            else
            {
                m_CaptureMaterial.CopyPropertiesFromMaterial(source);
                m_CaptureMaterial.DisableKeyword("_SUNDISK_SIMPLE");
                m_CaptureMaterial.DisableKeyword("_SUNDISK_HIGH_QUALITY");
                m_CaptureMaterial.EnableKeyword("_SUNDISK_NONE");
                if (m_CaptureMaterial.HasProperty(s_SunDisk)) m_CaptureMaterial.SetFloat(s_SunDisk, 0);
            }
            if (m_Target == null)
            {
                bool full = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat);
                if (!full && !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
                    throw new InvalidOperationException("A floating point render target is required for sky radiance.");
                m_Target = new RenderTexture(k_CaptureSize, k_CaptureSize, 0,
                    full ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "APRT Sky Radiance Capture", hideFlags = HideFlags.HideAndDontSave,
                    dimension = TextureDimension.Cube, useMipMap = false, autoGenerateMips = false,
                    antiAliasing = 1, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
            }
            if (!m_Target.IsCreated() && !m_Target.Create())
                throw new InvalidOperationException("Unable to create APRT sky capture target.");
            if (m_Readback == null)
                m_Readback = new Texture2D(k_CaptureSize, k_CaptureSize, TextureFormat.RGBAFloat, false, true)
                    { name = "APRT Sky Radiance Readback", hideFlags = HideFlags.HideAndDontSave };
            if (m_Command == null) m_Command = new CommandBuffer { name = "APRT Sky Environment" };
            if (m_CaptureProperties == null) m_CaptureProperties = new MaterialPropertyBlock();
            PrepareProjectionSamples();
        }

        void PrepareProjectionSamples()
        {
            if (m_ProjectionSamples.IsCreated) return;
            // All faces share texel solid angles and normalized coordinates.
            // Cache them in native memory so changing skies need no managed
            // pixel arrays, repeated trigonometry, or direction normalization.
            m_ProjectionSamples = new NativeArray<ProjectionSample>(k_CaptureSize * k_CaptureSize,
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int y = 0; y < k_CaptureSize; ++y)
                for (int x = 0; x < k_CaptureSize; ++x)
                {
                    double u0 = 2.0 * x / k_CaptureSize - 1, u1 = 2.0 * (x + 1) / k_CaptureSize - 1;
                    double v0 = 2.0 * y / k_CaptureSize - 1, v1 = 2.0 * (y + 1) / k_CaptureSize - 1;
                    m_ProjectionSamples[y * k_CaptureSize + x] = new ProjectionSample
                    {
                        direction = new Vector3((float)((u0 + u1) * .5), (float)((v0 + v1) * .5), 1).normalized,
                        solidAngle = SolidAngleArea(u1, v1) - SolidAngleArea(u0, v1) - SolidAngleArea(u1, v0) + SolidAngleArea(u0, v0)
                    };
                }
        }

        void Capture(Light sun)
        {
            bool sixFaces = m_CaptureMaterial.passCount == 6;
            if (sixFaces && m_SixFacesMesh == null) m_SixFacesMesh = Resources.Load<Mesh>("APRTSkyboxSixFaces");
            if (!sixFaces && m_SkyboxMesh == null) m_SkyboxMesh = Resources.Load<Mesh>("APRTSkyboxMesh");
            var mesh = sixFaces ? m_SixFacesMesh : m_SkyboxMesh;
            if (mesh == null) throw new InvalidOperationException("APRT skybox capture mesh is missing.");
            var previousTarget = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite, previousInvert = GL.invertCulling;
            m_CaptureProperties.SetVector(s_LightColor, sun != null ? (Vector4)(sun.color.linear * Mathf.Max(0, sun.intensity)) : Vector4.zero);
            m_CaptureProperties.SetVector(s_LightPosition, sun != null ? (Vector4)(-sun.transform.forward) : new Vector4(0, 0, -1, 0));
            var projection = Matrix4x4.Perspective(90, 1, .1f, 10);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore)
                projection.SetColumn(1, -projection.GetColumn(1));
            Array.Clear(m_Sums, 0, m_Sums.Length);
            GL.PushMatrix();
            try
            {
                m_Command.Clear();
                m_Command.DisableScissorRect();
                m_Command.SetInvertCulling(false);
#if UNITY_EDITOR
                // A cold sky variant must finish compiling before readback.
                // Otherwise the editor's placeholder color becomes cached light.
                UnityEditor.ShaderUtil.SetAsyncCompilation(m_Command, false);
#endif
                for (int face = 0; face < 6; ++face)
                {
                    m_Command.SetViewProjectionMatrices(s_FaceViews[face], projection);
                    m_Command.SetRenderTarget(new RenderTargetIdentifier(m_Target, 0, (CubemapFace)face));
                    m_Command.SetViewport(new Rect(0, 0, k_CaptureSize, k_CaptureSize));
                    m_Command.ClearRenderTarget(false, true, Color.black);
                    int pass = sixFaces ? s_FaceToSkyboxPass[face] : 0;
                    m_Command.DrawMesh(mesh, Matrix4x4.identity, m_CaptureMaterial, sixFaces ? pass : 0, pass, m_CaptureProperties);
                }
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(m_Command);
#endif
                m_Command.SetInvertCulling(previousInvert);
                GL.sRGBWrite = false;
                Graphics.ExecuteCommandBuffer(m_Command);
                // Synchronous readback on a source change also initializes the
                // first sky before receiver sampling. Unchanged sources skip it.
                for (int face = 0; face < 6; ++face)
                {
                    Graphics.SetRenderTarget(m_Target, 0, (CubemapFace)face);
                    m_Readback.ReadPixels(new Rect(0, 0, k_CaptureSize, k_CaptureSize), 0, 0, false);
                    // RGBAFloat maps directly to Color; this view borrows the
                    // texture's CPU memory and must not be disposed or retained.
                    var pixels = m_Readback.GetPixelData<Color>(0);
                    bool flipRows = SystemInfo.graphicsUVStartsAtTop;
                    for (int y = 0; y < k_CaptureSize; ++y)
                        for (int x = 0; x < k_CaptureSize; ++x)
                        {
                            // ReadPixels is bottom-up; restore native cube rows
                            // on top-origin APIs before assigning world directions.
                            int row = flipRows ? k_CaptureSize - 1 - y : y;
                            var color = pixels[row * k_CaptureSize + x];
                            var sample = m_ProjectionSamples[y * k_CaptureSize + x];
                            var direction = FaceDirection(face, sample.direction);
                            m_Basis[0] = 1; m_Basis[1] = direction.y; m_Basis[2] = direction.z; m_Basis[3] = direction.x;
                            m_Basis[4] = direction.x * direction.y; m_Basis[5] = direction.y * direction.z;
                            m_Basis[6] = 3 * direction.z * direction.z - 1; m_Basis[7] = direction.x * direction.z;
                            m_Basis[8] = direction.x * direction.x - direction.y * direction.y;
                            double red = FiniteRadiance(color.r), green = FiniteRadiance(color.g), blue = FiniteRadiance(color.b);
                            for (int coefficient = 0; coefficient < 9; ++coefficient)
                            {
                                double w = m_Basis[coefficient] * sample.solidAngle;
                                int offset = coefficient * 3;
                                m_Sums[offset] += red * w;
                                m_Sums[offset + 1] += green * w;
                                m_Sums[offset + 2] += blue * w;
                            }
                        }
                }
                for (int coefficient = 0; coefficient < 9; ++coefficient)
                {
                    double normalization = s_Normalization[coefficient] / (4 * Math.PI);
                    int offset = coefficient * 3;
                    radianceCoefficients[coefficient] = new Vector4((float)(m_Sums[offset] * normalization),
                        (float)(m_Sums[offset + 1] * normalization), (float)(m_Sums[offset + 2] * normalization), 0);
                }
            }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSRGB;
                GL.invertCulling = previousInvert;
            }
        }

        static double FiniteRadiance(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : Math.Max(0, value);
        static double SolidAngleArea(double x, double y) => Math.Atan2(x * y, Math.Sqrt(x * x + y * y + 1));
        static Vector3 FaceDirection(int face, Vector3 sample)
        {
            switch (face)
            {
                case 0: return new Vector3(sample.z, -sample.y, -sample.x);
                case 1: return new Vector3(-sample.z, -sample.y, sample.x);
                case 2: return new Vector3(sample.x, sample.z, sample.y);
                case 3: return new Vector3(sample.x, -sample.z, -sample.y);
                case 4: return new Vector3(sample.x, -sample.y, sample.z);
                default: return new Vector3(-sample.x, -sample.y, -sample.z);
            }
        }

        void ClearCoefficients()
        {
            Array.Clear(radianceCoefficients, 0, radianceCoefficients.Length);
            hasValidSource = false;
        }

        static void DestroyResource(Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) Object.Destroy(resource);
            else Object.DestroyImmediate(resource);
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            ClearCoefficients();
            m_Command?.Dispose();
            if (m_ProjectionSamples.IsCreated) m_ProjectionSamples.Dispose();
            m_Target?.Release();
            DestroyResource(m_CaptureMaterial);
            DestroyResource(m_Readback);
            DestroyResource(m_Target);
            m_CaptureMaterial = null; m_Readback = null; m_Target = null; m_Command = null;
            m_LastOverride = null; m_LastSkybox = null; m_TextureProperties.Clear();
            m_CaptureProperties = null; m_CubemapCaptureShader = null; m_SkyboxMesh = null; m_SixFacesMesh = null;
        }
    }
}
