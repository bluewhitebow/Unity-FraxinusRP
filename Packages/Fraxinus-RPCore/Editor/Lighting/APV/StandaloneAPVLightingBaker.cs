using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

#if HAS_FRAXINUS_LIGHT_TRANSPORT
using UnityEngine.Rendering.Sampling;
using UnityEngine.Rendering.UnifiedRayTracing;
#endif

namespace UnityEngine.Rendering
{
    /// <summary>Small, deliberately conservative settings object for the standalone APV baker.</summary>
    internal struct StandaloneAPVBakeSettings
    {
        public int samples;
        public int directSamples;
        public int environmentSamples;
        public int bounces;
        public int minBounces;
        public int probesPerBatch;
        public float environmentIntensity;

        public static StandaloneAPVBakeSettings Default => new StandaloneAPVBakeSettings
        {
            // Keep the default sample budget equivalent to Unity 6's native
            // LightTransport path for the Sponza LightingSettings asset.
            samples = 1024,
            directSamples = 128,
            environmentSamples = 1024,
            bounces = 2,
            minBounces = 2,
            probesPerBatch = 256,
            environmentIntensity = 1.0f
        };

        public void Sanitize()
        {
            samples = Mathf.Clamp(samples, 1, 4096);
            directSamples = Mathf.Clamp(directSamples, 1, 4096);
            environmentSamples = Mathf.Clamp(environmentSamples > 0 ? environmentSamples : samples, 1, 4096);
            bounces = Mathf.Clamp(bounces, 0, 4);
            minBounces = Mathf.Clamp(minBounces, 0, bounces);
            probesPerBatch = Mathf.Clamp(probesPerBatch, 1, 65535);
            environmentIntensity = Mathf.Max(0.0f, environmentIntensity);
        }
    }

    /// <summary>
    /// Experimental APV baker which owns the GPU dispatch and does not call Lightmapping.
    /// It supports textured opaque and alpha-clipped ContributeGI meshes and baked/mixed
    /// direct-light transport. Alpha-blended surfaces fail preflight.
    /// </summary>
    internal sealed class StandaloneAPVLightingBaker : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ProbeSH
        {
            public Vector3 c0, c1, c2, c3, c4, c5, c6, c7, c8;
            public float validity;
            public uint completedDispatch;

            public void Accumulate(ProbeSH value)
            {
                c0 += value.c0; c1 += value.c1; c2 += value.c2;
                c3 += value.c3; c4 += value.c4; c5 += value.c5;
                c6 += value.c6; c7 += value.c7; c8 += value.c8;
                validity += value.validity;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BakeLight
        {
            public Vector4 positionRange;
            public Vector4 directionOuterCos;
            public Vector4 colorIntensity;
            public Vector4 innerCosTypeFlags;
            // Directional cone half-angle cosine and whether this light casts shadows.
            public Vector4 shape;
        }

        static readonly int s_ProbePositions = Shader.PropertyToID("_ProbePositions");
        static readonly int s_Out = Shader.PropertyToID("_Out");
        static readonly int s_PassMode = Shader.PropertyToID("_PassMode");
        static readonly int s_SampleCount = Shader.PropertyToID("_SampleCount");
        static readonly int s_DirectSampleCount = Shader.PropertyToID("_DirectSampleCount");
        static readonly int s_EnvironmentSampleCount = Shader.PropertyToID("_EnvironmentSampleCount");
        static readonly int s_SampleStart = Shader.PropertyToID("_SampleStart");
        static readonly int s_SampleBatchSize = Shader.PropertyToID("_SampleBatchSize");
        static readonly int s_DispatchID = Shader.PropertyToID("_DispatchID");
        static readonly int s_MaxBounces = Shader.PropertyToID("_MaxBounces");
        static readonly int s_MinBounces = Shader.PropertyToID("_MinBounces");
        static readonly int s_IndirectScale = Shader.PropertyToID("_IndirectScale");
        static readonly int[] s_EnvironmentSH =
        {
            Shader.PropertyToID("_EnvironmentSH0"),
            Shader.PropertyToID("_EnvironmentSH1"),
            Shader.PropertyToID("_EnvironmentSH2"),
            Shader.PropertyToID("_EnvironmentSH3"),
            Shader.PropertyToID("_EnvironmentSH4"),
            Shader.PropertyToID("_EnvironmentSH5"),
            Shader.PropertyToID("_EnvironmentSH6"),
            Shader.PropertyToID("_EnvironmentSH7"),
            Shader.PropertyToID("_EnvironmentSH8")
        };
        static readonly int s_EnvironmentCube = Shader.PropertyToID("_EnvironmentCube");
        static readonly int s_HasEnvironmentCube = Shader.PropertyToID("_HasEnvironmentCube");
        static readonly int s_EnvironmentImportanceSampling = Shader.PropertyToID("_EnvironmentImportanceSampling");
        static readonly int s_RayOffset = Shader.PropertyToID("_RayOffset");
        static readonly int s_ProbeCount = Shader.PropertyToID("_ProbeCount");
        static readonly int s_ProbeIndexOffset = Shader.PropertyToID("_ProbeIndexOffset");
        static readonly int s_Lights = Shader.PropertyToID("_Lights");
        static readonly int s_LightCount = Shader.PropertyToID("_LightCount");
        static readonly int s_EnvironmentIntensity = Shader.PropertyToID("_EnvironmentIntensity");
        static readonly int s_EnvironmentCdfConditionalResolution = Shader.PropertyToID("_EnvironmentCdfConditionalResolution");
        static readonly int s_EnvironmentCdfMarginalResolution = Shader.PropertyToID("_EnvironmentCdfMarginalResolution");
        static readonly int s_EnvironmentCdfConditionalBuffer = Shader.PropertyToID("_EnvironmentCdfConditionalBuffer");
        static readonly int s_EnvironmentCdfMarginalBuffer = Shader.PropertyToID("_EnvironmentCdfMarginalBuffer");
        static readonly int s_EnvironmentCdfNormalization = Shader.PropertyToID("_EnvironmentCdfNormalization");
        static readonly int s_SobolMatricesBuffer = Shader.PropertyToID("_SobolMatricesBuffer");
        static readonly int s_NativeProbeSampling = Shader.PropertyToID("_NativeProbeSampling");
        static readonly int s_NativeProbeCranleyPatterson = Shader.PropertyToID("_NativeProbeCranleyPatterson");
        static readonly int s_NativeProbeSobolSequence = Shader.PropertyToID("_NativeProbeSobolSequence");
        static readonly int s_NativeProbeRanking256 = Shader.PropertyToID("_NativeProbeRanking256");
        static readonly int s_NativeProbeScrambling256 = Shader.PropertyToID("_NativeProbeScrambling256");
        static readonly int s_NativeProbeSamplingFlipY = Shader.PropertyToID("_NativeProbeSamplingFlipY");

        const int kEnvironmentCdfMarginalResolution = 64;
        const int kEnvironmentCdfConditionalResolution = 128;
        // Bound both dimensions of a dispatch. Increasing bake quality must
        // increase the number of editor steps, not one GPU kernel's duration.
        const int kSamplesPerDispatch = 256;
        const int kMaxProbesPerDispatch = 256;

        readonly StandaloneAPVBakeSettings m_Settings;
        NativeArray<Vector3> m_Positions;
        NativeArray<SphericalHarmonicsL2> m_Irradiance;
        NativeArray<float> m_Validity;
        NativeArray<Vector4> m_Occlusion;
        GraphicsBuffer m_ProbeBuffer;
        GraphicsBuffer m_DirectOutputBuffer;
        GraphicsBuffer m_IndirectOutputBuffer;
        GraphicsBuffer m_ValidityOutputBuffer;
        GraphicsBuffer m_ScratchBuffer;
        ProbeSH[] m_DirectReadback;
        ProbeSH[] m_IndirectReadback;
        ProbeSH[] m_ValidityReadback;
        CommandBuffer m_Command;
        ComputeShader m_Shader;
        int m_Completed;
        int m_BatchStart;
        int m_BatchCount;
        int m_SampleStart;
        int m_DispatchID;
        bool m_Initialized;
        bool m_Pending;
        int m_ReadbacksPending;
        bool m_ReadbackFailed;
        bool m_Failed;
        bool m_LoggedUnsupported;
        uint m_Generation;
        StandaloneAPVMaterialResources m_MaterialResources;
        readonly List<BakeLight> m_Lights = new List<BakeLight>();
        readonly List<Light> m_SceneLights = new List<Light>();
        internal IReadOnlyList<Light> sceneLights => m_SceneLights;
        GraphicsBuffer m_LightBuffer;
        GraphicsBuffer m_SobolBuffer;
        GraphicsBuffer m_EnvironmentCdfConditionalBuffer;
        GraphicsBuffer m_EnvironmentCdfMarginalBuffer;
        GraphicsBuffer m_NativeProbeCranleyPatterson;
        Texture2D m_NativeProbeSobolSequence;
        Texture2D m_NativeProbeRanking256;
        Texture2D m_NativeProbeScrambling256;
        bool m_HasGeometry;
        SphericalHarmonicsL2 m_EnvironmentSH;
        Cubemap m_EnvironmentCube;
        bool m_HasEnvironmentCube;
        bool m_EnvironmentImportanceSampling;
        float m_EnvironmentCdfNormalization;
        int m_MinBounces;

#if HAS_FRAXINUS_LIGHT_TRANSPORT
        RayTracingContext m_RayTracingContext;
        AccelStructAdapter m_AccelerationStructure;
        IRayTracingShader m_RayShader;
#endif

        public StandaloneAPVLightingBaker() : this(StandaloneAPVBakeSettings.Default) { }

        public StandaloneAPVLightingBaker(StandaloneAPVBakeSettings settings)
        {
            settings.Sanitize();
            m_Settings = settings;
        }

        // Unity 6's native probe integrator seeds this Cranley-Patterson
        // table once with MT19937 and reuses it for every probe bake. Keep the
        // table local to the standalone baker so this path does not depend on
        // Unity's Lightmapping callbacks or global sampling state.
        static float[] CreateNativeProbeCranleyPatterson()
        {
            const uint seed = 98764546u;
            const int count = 512 * 12;
            var state = new uint[624];
            state[0] = seed;
            for (int i = 1; i < state.Length; ++i)
                state[i] = unchecked(1812433253u * (state[i - 1] ^ (state[i - 1] >> 30)) + (uint)i);

            var shifts = new float[count];
            int next = state.Length;
            for (int output = 0; output < shifts.Length; ++output)
            {
                if (next == state.Length)
                {
                    for (int i = 0; i < state.Length; ++i)
                    {
                        uint value = (state[i] & 0x80000000u) |
                            (state[(i + 1) % state.Length] & 0x7fffffffu);
                        state[i] = state[(i + 397) % state.Length] ^ (value >> 1) ^
                            ((value & 1u) != 0u ? 0x9908b0dfu : 0u);
                    }
                    next = 0;
                }
                uint outputValue = state[next++];
                outputValue ^= outputValue >> 11;
                outputValue ^= (outputValue << 7) & 0x9d2c5680u;
                outputValue ^= (outputValue << 15) & 0xefc60000u;
                outputValue ^= outputValue >> 18;
                shifts[output] = (float)((double)outputValue / 4294967295.0);
            }
            return shifts;
        }

        bool LoadNativeProbeSamplingResources()
        {
            const string basePath = "Packages/com.fraxinus.rendering.light-transport/Runtime/Sampling/Textures/SobolBlueNoise/";
            m_NativeProbeSobolSequence = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath + "SobolOwenScrambled256.png");
            m_NativeProbeRanking256 = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath + "RankingTile256SPP.png");
            m_NativeProbeScrambling256 = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath + "ScramblingTile256SPP.png");
            if (m_NativeProbeSobolSequence == null || m_NativeProbeRanking256 == null ||
                m_NativeProbeScrambling256 == null)
            {
                Debug.LogError("Standalone APV native probe sampling resources are missing from the Light Transport package.");
                return false;
            }
            if (m_NativeProbeSobolSequence.width != 256 || m_NativeProbeSobolSequence.height != 256 ||
                m_NativeProbeRanking256.width != 128 || m_NativeProbeRanking256.height != 1024 ||
                m_NativeProbeScrambling256.width != 128 || m_NativeProbeScrambling256.height != 1024)
            {
                Debug.LogError("Standalone APV native probe sampling resources have unexpected dimensions.");
                return false;
            }
            m_NativeProbeCranleyPatterson = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                512 * 12, sizeof(float));
            m_NativeProbeCranleyPatterson.SetData(CreateNativeProbeCranleyPatterson());
            return true;
        }

        public ulong currentStep => (ulong)m_Completed;
        int batchCapacity => Mathf.Min(m_Settings.probesPerBatch, kMaxProbesPerDispatch);
        public ulong stepCount => (ulong)(m_Positions.IsCreated ? m_Positions.Length : 0);
        public NativeArray<SphericalHarmonicsL2> irradiance => m_Irradiance;
        public NativeArray<float> validity => m_Validity;
        public NativeArray<Vector4> occlusion => m_Occlusion;

        public void Initialize(bool bakeProbeOcclusion, NativeArray<Vector3> probePositions)
        {
            Dispose();
            m_Positions = probePositions; // borrowed until baking has completed
            m_EnvironmentSH = CaptureEnvironmentSH();
            m_EnvironmentCube = RenderSettings.skybox != null
                ? CaptureEnvironmentCube() : null;
            m_HasEnvironmentCube = m_EnvironmentCube != null;
            // Keep the standalone environment estimator on the same branch as
            // Unity's Progressive Lightmapper. In particular, the URP6 Sponza
            // fixture has PVREnvironmentMIS disabled; forcing the CDF/MIS path
            // there changes finite-sample chroma even though both estimators
            // are unbiased in the limit.
            try
            {
                var lightingSettings = ProbeVolumeBakingWorkflow.GetLightingSettings();
                m_EnvironmentImportanceSampling = lightingSettings != null &&
                    lightingSettings.environmentImportanceSampling;
                m_MinBounces = lightingSettings != null
                    ? Mathf.Clamp(lightingSettings.minBounces, 0, m_Settings.bounces)
                    : m_Settings.minBounces;
            }
            catch (Exception exception)
            {
                // A missing settings asset uses Unity's default non-MIS path
                // for this standalone baker rather than silently diverging
                // from the serialized scene configuration.
                m_EnvironmentImportanceSampling = false;
                m_MinBounces = m_Settings.minBounces;
                Debug.LogWarning($"Standalone APV could not read environment MIS setting; using uniform sampling: {exception.Message}");
            }
            // Compute shader resource validation requires the cube property to
            // be bound even when the SH fallback is selected.
            m_EnvironmentCube ??= CreateBlackEnvironmentCube();
            BuildEnvironmentCdf();
            m_Irradiance = new NativeArray<SphericalHarmonicsL2>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            m_Validity = new NativeArray<float>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            m_Occlusion = bakeProbeOcclusion
                ? new NativeArray<Vector4>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory)
                : default;
            if (bakeProbeOcclusion && !m_LoggedUnsupported)
            {
                Debug.LogWarning("Standalone APV baking currently does not evaluate mixed-light occlusion; occlusion output is zero.");
                m_LoggedUnsupported = true;
            }

            m_Completed = 0;
            m_BatchStart = 0;
            m_SampleStart = 0;
            m_DispatchID = 0;
            m_Initialized = true;
            m_Failed = false;
            m_Pending = false;
            m_ReadbacksPending = 0;
            m_ReadbackFailed = false;
            m_Shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.core/Editor/Lighting/APV/StandaloneAPV.urtshader");
            if (m_Shader == null)
            {
                Debug.LogError("Standalone APV shader could not be loaded.");
                m_Failed = true;
                return;
            }
            BuildAccelerationStructure();
            if (m_Failed)
                return;
            m_ProbeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchCapacity, Marshal.SizeOf<Vector3>());
            int probeStride = Marshal.SizeOf<ProbeSH>();
            m_DirectOutputBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchCapacity, probeStride);
            m_IndirectOutputBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchCapacity, probeStride);
            m_ValidityOutputBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchCapacity, probeStride);
            m_DirectReadback = new ProbeSH[batchCapacity];
            m_IndirectReadback = new ProbeSH[batchCapacity];
            m_ValidityReadback = new ProbeSH[batchCapacity];
#if HAS_FRAXINUS_LIGHT_TRANSPORT
            // Supply the shared Owen-scrambled Sobol sampler used for
            // primary rays and diffuse continuations.
            m_SobolBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                (int)(SobolData.SobolDims * SobolData.SobolSize), sizeof(uint));
            m_SobolBuffer.SetData(SobolData.SobolMatrices);
            if (!LoadNativeProbeSamplingResources())
            {
                m_Failed = true;
                return;
            }
#endif
        }

        static SphericalHarmonicsL2 CaptureEnvironmentSH()
        {
            // ambientProbe can come from a stale LightingDataAsset in Unity
            // 2022. Native APV samples the active environment instead, so a
            // Skybox bake must project the current skybox directly.
            // LightTransport always evaluates the active skybox material as
            // its environment source when one is assigned, even if the scene
            // ambient mode is Flat or Trilight.
            if (RenderSettings.skybox != null)
                return CaptureSkyboxSH();

            if (RenderSettings.ambientMode == AmbientMode.Trilight)
                return BuildAmbientGradientSH();

            Color ambient = RenderSettings.ambientLight.linear;
            ambient *= Mathf.Max(0.0f, RenderSettings.ambientIntensity);
            var result = new SphericalHarmonicsL2();
            result[0, 0] = ambient.r;
            result[1, 0] = ambient.g;
            result[2, 0] = ambient.b;
            return result;
        }

        static SphericalHarmonicsL2 CaptureSkyboxSH()
        {
            const int size = 64;
            var result = new SphericalHarmonicsL2();
            var cube = StandaloneAPVSkyboxCapture.Capture(RenderSettings.skybox, RenderSettings.sun, size);
            try
            {
                for (int face = 0; face < 6; ++face)
                {
                    var pixels = cube.GetPixels((CubemapFace)face);
                    for (int y = 0; y < size; ++y)
                    {
                        float v = 2.0f * (y + 0.5f) / size - 1.0f;
                        for (int x = 0; x < size; ++x)
                        {
                            float u = 2.0f * (x + 0.5f) / size - 1.0f;
                            Vector3 direction = StandaloneAPVSkyboxCapture.FaceDirection((CubemapFace)face, u, v);
                            Color sample = pixels[y * size + x];
                            float denominator = 1.0f + u * u + v * v;
                            float texelWeight = 4.0f /
                                (size * size * denominator * Mathf.Sqrt(denominator));
                            AddEnvironmentSH(ref result, new Vector3(sample.r, sample.g, sample.b), direction, texelWeight);
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
            return ScaleSH(result, Mathf.Max(0.0f, RenderSettings.ambientIntensity));
        }

        static Cubemap CaptureEnvironmentCube()
        {
            try
            {
                // Match the native APV environment resolution and linear HDR
                // representation without rendering a scene camera. Capturing
                // through URP can inherit editor state and return a black sky.
                return StandaloneAPVSkyboxCapture.Capture(RenderSettings.skybox, RenderSettings.sun,
                    1024, Mathf.Max(0.0f, RenderSettings.ambientIntensity));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Standalone APV HDR environment cube capture failed; using L2 environment fallback. " + e.Message);
                return null;
            }
        }

        static Color SampleEnvironmentCube(Color[][] faces, int size, Vector3 direction)
        {
            float ax = Mathf.Abs(direction.x), ay = Mathf.Abs(direction.y), az = Mathf.Abs(direction.z);
            int face;
            float u, v;
            if (ax >= ay && ax >= az)
            {
                float inv = 1.0f / Mathf.Max(ax, 1e-6f);
                face = direction.x >= 0.0f ? 0 : 1;
                u = ((direction.x >= 0.0f ? -direction.z : direction.z) * inv + 1.0f) * 0.5f;
                v = (-direction.y * inv + 1.0f) * 0.5f;
            }
            else if (ay >= az)
            {
                float inv = 1.0f / Mathf.Max(ay, 1e-6f);
                face = direction.y >= 0.0f ? 2 : 3;
                u = (direction.x * inv + 1.0f) * 0.5f;
                v = ((direction.y >= 0.0f ? direction.z : -direction.z) * inv + 1.0f) * 0.5f;
            }
            else
            {
                float inv = 1.0f / Mathf.Max(az, 1e-6f);
                face = direction.z >= 0.0f ? 4 : 5;
                u = ((direction.z >= 0.0f ? direction.x : -direction.x) * inv + 1.0f) * 0.5f;
                v = (-direction.y * inv + 1.0f) * 0.5f;
            }
            // CubemapRender creates a linear-filtered RenderTexture and the
            // native CDF compute pass samples it with SampleLevel(..., 0).
            // Use the equivalent bilinear interpolation for the CPU-built CDF
            // instead of nearest texels; this is significant at the edge of a
            // procedural sun disk.
            float fx = Mathf.Clamp01(u) * (size - 1);
            float fy = Mathf.Clamp01(v) * (size - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, size - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, size - 1);
            int x1 = Mathf.Min(x0 + 1, size - 1);
            int y1 = Mathf.Min(y0 + 1, size - 1);
            float tx = fx - x0;
            float ty = fy - y0;
            Color[] pixels = faces[face];
            Color c00 = pixels[y0 * size + x0];
            Color c10 = pixels[y0 * size + x1];
            Color c01 = pixels[y1 * size + x0];
            Color c11 = pixels[y1 * size + x1];
            Color lower = Color.LerpUnclamped(c00, c10, tx);
            Color upper = Color.LerpUnclamped(c01, c11, tx);
            return Color.LerpUnclamped(lower, upper, ty);
        }

        static Vector3 EnvironmentDirection(float u, float v)
        {
            float phi = 2.0f * Mathf.PI * (1.0f - u);
            float cosTheta = 2.0f * v - 1.0f;
            float sinTheta = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - cosTheta * cosTheta));
            // Match LightTransport's MapUVToSkyDirection after its GL-to-DX
            // conversion, which yields Unity's (x, y, z) cubemap direction.
            return new Vector3(sinTheta * Mathf.Cos(phi), cosTheta, sinTheta * Mathf.Sin(phi));
        }

        void BuildEnvironmentCdf()
        {
            var conditional = new float[kEnvironmentCdfMarginalResolution * kEnvironmentCdfConditionalResolution];
            var marginal = new float[kEnvironmentCdfMarginalResolution];
            int size = m_EnvironmentCube.width;
            var faces = new[]
            {
                m_EnvironmentCube.GetPixels(CubemapFace.PositiveX),
                m_EnvironmentCube.GetPixels(CubemapFace.NegativeX),
                m_EnvironmentCube.GetPixels(CubemapFace.PositiveY),
                m_EnvironmentCube.GetPixels(CubemapFace.NegativeY),
                m_EnvironmentCube.GetPixels(CubemapFace.PositiveZ),
                m_EnvironmentCube.GetPixels(CubemapFace.NegativeZ)
            };
            float total = 0.0f;
            for (int row = 0; row < kEnvironmentCdfMarginalResolution; ++row)
            {
                float rowSum = 0.0f;
                for (int column = 0; column < kEnvironmentCdfConditionalResolution; ++column)
                {
                    float u = (column + 0.5f) / kEnvironmentCdfConditionalResolution;
                    float v = (row + 0.5f) / kEnvironmentCdfMarginalResolution;
                    Color value = SampleEnvironmentCube(faces, size, EnvironmentDirection(u, v));
                    // Unity's native APV environment MIS uses the maximum
                    // linear channel as its proposal metric. Using Rec.709
                    // luminance here changes the proposal for the warm sky
                    // and leaves the estimator with a different finite-sample
                    // chromatic error.
                    float metric = Mathf.Max(0.0f, Mathf.Max(value.r, Mathf.Max(value.g, value.b)));
                    conditional[row * kEnvironmentCdfConditionalResolution + column] = metric;
                    rowSum += metric;
                }
                float safeRowSum = Mathf.Max(rowSum, Mathf.Epsilon);
                float prefix = 0.0f;
                for (int column = 0; column < kEnvironmentCdfConditionalResolution; ++column)
                {
                    int index = row * kEnvironmentCdfConditionalResolution + column;
                    float metric = conditional[index];
                    conditional[index] = prefix / safeRowSum;
                    prefix += metric;
                }
                marginal[row] = rowSum / kEnvironmentCdfConditionalResolution;
                total += marginal[row];
            }
            float safeTotal = Mathf.Max(total, Mathf.Epsilon);
            float marginalPrefix = 0.0f;
            for (int row = 0; row < kEnvironmentCdfMarginalResolution; ++row)
            {
                float rowValue = marginal[row];
                marginal[row] = marginalPrefix / safeTotal;
                marginalPrefix += rowValue;
            }
            // Keep the marginal CDF and the environment PDF normalization in
            // separate values. The first CDF entry is the true zero prefix;
            // reusing it for the normalization shifts row zero and biases the
            // importance sampler toward the warmest row. Unity stores these
            // values separately in its environment data.
            m_EnvironmentCdfNormalization = kEnvironmentCdfMarginalResolution /
                (safeTotal * 4.0f * Mathf.PI);
            m_EnvironmentCdfConditionalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                conditional.Length, sizeof(float));
            m_EnvironmentCdfMarginalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                marginal.Length, sizeof(float));
            m_EnvironmentCdfConditionalBuffer.SetData(conditional);
            m_EnvironmentCdfMarginalBuffer.SetData(marginal);
        }

        static Cubemap CreateBlackEnvironmentCube()
        {
            Cubemap result;
            try
            {
                result = new Cubemap(1, TextureFormat.RGBAFloat, false);
            }
            catch
            {
                // The resource is sampled only when _HasEnvironmentCube is
                // false, so an LDR black fallback is sufficient on devices
                // that cannot allocate floating-point cubemaps.
                result = new Cubemap(1, TextureFormat.RGBA32, false);
            }
            result.hideFlags = HideFlags.HideAndDontSave;
            result.wrapMode = TextureWrapMode.Clamp;
            result.filterMode = FilterMode.Point;
            var pixels = new[] { Color.black };
            foreach (CubemapFace face in new[]
                { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY,
                  CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ })
                result.SetPixels(pixels, face, 0);
            result.Apply(false, false);
            return result;
        }

        static SphericalHarmonicsL2 BuildAmbientGradientSH()
        {
            // Project the piecewise-linear sky/equator/ground gradient onto the
            // same polynomial basis used by AddIrradianceSH in the compute shader.
            // Trilight is evaluated directly without a cached ambient probe.
            // Deterministic quadrature runs once per bake.
            // Keep enough latitude bands that a uniform gradient with equal
            // endpoint colors collapses to a constant probe within 1e-5.
            const int thetaSamples = 256;
            const int phiSamples = 64;
            var result = new SphericalHarmonicsL2();
            Color sky = RenderSettings.ambientSkyColor.linear;
            Color equator = RenderSettings.ambientEquatorColor.linear;
            Color ground = RenderSettings.ambientGroundColor.linear;
            float intensity = Mathf.Max(0.0f, RenderSettings.ambientIntensity);
            sky *= intensity;
            equator *= intensity;
            ground *= intensity;

            int sampleCount = thetaSamples * phiSamples;
            float weight = 4.0f * Mathf.PI / sampleCount;
            for (int theta = 0; theta < thetaSamples; ++theta)
            {
                // Uniform solid-angle bands: y is the environment up axis.
                float y = 1.0f - 2.0f * (theta + 0.5f) / thetaSamples;
                float radius = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - y * y));
                Color upper = y >= 0.0f ? sky : ground;
                Color value = Color.Lerp(equator, upper, Mathf.Abs(y));
                for (int phiIndex = 0; phiIndex < phiSamples; ++phiIndex)
                {
                    float phi = 2.0f * Mathf.PI * (phiIndex + 0.5f) / phiSamples;
                    Vector3 direction = new Vector3(radius * Mathf.Cos(phi), y, radius * Mathf.Sin(phi));
                    AddEnvironmentSH(ref result, new Vector3(value.r, value.g, value.b), direction, weight);
                }
            }
            return result;
        }

        static void AddEnvironmentSH(ref SphericalHarmonicsL2 sh, Vector3 value, Vector3 direction, float weight)
        {
            float x = direction.x;
            float y = direction.y;
            float z = direction.z;
            for (int channel = 0; channel < 3; ++channel)
            {
                float radiance = value[channel];
                sh[channel, 0] += radiance * (0.079577471545948f * weight);
                sh[channel, 1] += radiance * (0.159154943091895f * y * weight);
                sh[channel, 2] += radiance * (0.159154943091895f * z * weight);
                sh[channel, 3] += radiance * (0.159154943091895f * x * weight);
                sh[channel, 4] += radiance * (0.298415518297304f * x * y * weight);
                sh[channel, 5] += radiance * (0.298415518297304f * y * z * weight);
                sh[channel, 6] += radiance * (0.024867959858109f * (3.0f * z * z - 1.0f) * weight);
                sh[channel, 7] += radiance * (0.298415518297304f * x * z * weight);
                sh[channel, 8] += radiance * (0.074603879574326f * (x * x - y * y) * weight);
            }
        }

        static SphericalHarmonicsL2 ScaleSH(in SphericalHarmonicsL2 value, float scale)
        {
            var result = value;
            for (int channel = 0; channel < 3; ++channel)
                for (int coefficient = 0; coefficient < 9; ++coefficient)
                    result[channel, coefficient] *= scale;
            return result;
        }

        bool Reject(string message)
        {
            Debug.LogError("Standalone APV preflight rejected the bake: " + message);
            m_Failed = true;
            return false;
        }

        bool TryGetMaterial(Material material, out uint materialID)
        {
            materialID = 0;
            if (!StandaloneAPVMaterialResources.TryValidate(material, out string reason))
                return Reject(reason);
            materialID = m_MaterialResources.GetMaterialID(material);
            return true;
        }

        static bool IsContributing(Renderer renderer)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || !renderer.gameObject.scene.isLoaded || renderer.isPartOfStaticBatch)
                return false;
            var flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
            return (flags & StaticEditorFlags.ContributeGI) != 0;
        }

        List<MeshRenderer> CollectRenderers()
        {
            var result = new List<MeshRenderer>();
            var lod0 = new HashSet<Renderer>();
            var lodGroups = UnityEngine.Object.FindObjectsOfType<LODGroup>(true);
            foreach (var lodGroup in lodGroups)
            {
                var levels = lodGroup.GetLODs();
                if (levels.Length == 0)
                    continue;
                foreach (var renderer in levels[0].renderers)
                    if (renderer != null)
                        lod0.Add(renderer);
            }

            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!IsContributing(renderer))
                    continue;
                var lodGroup = renderer.GetComponentInParent<LODGroup>();
                if (lodGroup != null && !lod0.Contains(renderer))
                    continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;
                result.Add(renderer);
            }
            return result;
        }

        bool ExtractLights()
        {
            // LightTransport follows the active LightingSettings mixed mode:
            // Subtractive treats Mixed lights as baked direct lighting,
            // Shadowmask keeps direct lighting realtime when a valid channel
            // exists, and IndirectOnly excludes Mixed direct lighting.
            MixedLightingMode mixedLightingMode = MixedLightingMode.IndirectOnly;
            try
            {
                var settings = ProbeVolumeBakingWorkflow.GetLightingSettings();
                if (settings != null)
                    mixedLightingMode = settings.mixedBakeMode;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Standalone APV could not read mixed lighting mode; using IndirectOnly: {exception.Message}");
            }
            foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (!light.isActiveAndEnabled || !light.gameObject.scene.isLoaded)
                    continue;
                if (light.type != LightType.Directional && light.type != LightType.Point && light.type != LightType.Spot)
                    continue;
                var bakeType = light.lightmapBakeType;
                if (bakeType == LightmapBakeType.Realtime)
                    continue;
                bool contributesToDirect = bakeType == LightmapBakeType.Baked ||
                    (bakeType == LightmapBakeType.Mixed && mixedLightingMode == MixedLightingMode.Subtractive);
                if (bakeType == LightmapBakeType.Mixed && mixedLightingMode == MixedLightingMode.Shadowmask)
                {
                    // A shadowmask channel means the runtime owns direct
                    // lighting; invalid channels fall back to baked direct,
                    // matching World.UpdateLights in LightTransport.
                    contributesToDirect = light.bakingOutput.occlusionMaskChannel == -1;
                }
                float inner = light.type == LightType.Spot ? Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f) : 1;
                float outer = light.type == LightType.Spot ? Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f) : -1;
                // LightTransport receives punctual-light radiance in the
                // legacy URP/LightBaker convention: intensity is folded into
                // the linear RGB payload and the transport bridge carries the
                // PI conversion in the fourth component. Keeping this layout
                // explicit prevents direct and indirect paths from applying
                // different radiometric units.
                Color color = GraphicsSettings.lightsUseLinearIntensity
                    ? light.color.linear * light.intensity
                    : (light.color * light.intensity).linear;
                if (light.useColorTemperature)
                    color *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
                m_Lights.Add(new BakeLight
                {
                    positionRange = new Vector4(light.transform.position.x, light.transform.position.y, light.transform.position.z, light.range),
                    directionOuterCos = new Vector4(light.transform.forward.x, light.transform.forward.y, light.transform.forward.z, outer),
                    colorIntensity = new Vector4(color.r, color.g, color.b, Mathf.PI),
                    innerCosTypeFlags = new Vector4(inner, (float)light.type, contributesToDirect ? 3 : 2, light.bounceIntensity),
                    // LightTransport stores the directional penumbra width in
                    // radians and samples a disk of that radius at unit
                    // distance. It uses the resulting jittered direction both
                    // for visibility and SH projection.
                    shape = new Vector4(light.type == LightType.Directional && light.shadows == LightShadows.Soft
                        ? light.shadowAngle * Mathf.Deg2Rad : 0.0f,
                        light.shadows != LightShadows.None ? 1.0f : 0.0f, 0, 0)
                });
                m_SceneLights.Add(light);
            }
            return true;
        }

        bool ExtractSceneData(out List<MeshRenderer> renderers)
        {
            renderers = CollectRenderers();
            foreach (var renderer in renderers)
            {
                var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                var materials = renderer.sharedMaterials;
                int submeshCount = mesh.subMeshCount;
                if (materials == null || materials.Length == 0)
                    return Reject($"Renderer '{renderer.name}' has no materials.");
                for (int i = 0; i < submeshCount; ++i)
                {
                    var material = materials[Mathf.Min(i, materials.Length - 1)];
                    if (!TryGetMaterial(material, out _))
                        return false;
                }
            }
            ExtractLights();
            m_HasGeometry = renderers.Count != 0;
            return true;
        }

        void BakeEmptyBatch(int start, int count)
        {
            var environment = ScaleSH(m_EnvironmentSH, m_Settings.environmentIntensity);
            for (int i = 0; i < count; ++i)
            {
                var sh = environment;
                foreach (var light in m_Lights)
                {
                    if (((int)light.innerCosTypeFlags.z & 1) == 0)
                        continue;
                    // No geometry: these lights have no occluders. Keep this
                    // analytic path consistent with SampleLight / AddIrradianceSH.
                    Vector3 direction = -(Vector3)light.directionOuterCos;
                    float attenuation = 1;
                    if ((LightType)light.innerCosTypeFlags.y != LightType.Directional)
                    {
                        direction = (Vector3)light.positionRange - m_Positions[start + i];
                        float distanceSqr = Mathf.Max(direction.sqrMagnitude, 0.0001f);
                        float range = Mathf.Max(light.positionRange.w, 0.001f);
                        float factor = distanceSqr / (range * range);
                        float fade = Mathf.Clamp01(1 - factor * factor);
                        attenuation = fade * fade / distanceSqr;
                        if ((LightType)light.innerCosTypeFlags.y == LightType.Spot)
                        {
                            float cone = Vector3.Dot(-direction.normalized, ((Vector3)light.directionOuterCos).normalized);
                            float angular = Mathf.Clamp01((cone - light.directionOuterCos.w) /
                                Mathf.Max(0.001f, light.innerCosTypeFlags.x - light.directionOuterCos.w));
                            attenuation *= angular * angular;
                        }
                    }
                    // The native path tracer represents a directional light as
                    // a single infinitely distant direction. Keep the empty
                    // scene fallback consistent with the GPU path; soft-shadow
                    // jitter belongs to visibility, not SH radiance projection.
                    // Native direct-radiance SH carries a PI factor before
                    // conversion to the Unity irradiance representation.
                    AddDirectSH(ref sh, (Vector3)light.colorIntensity *
                        (light.colorIntensity.w * attenuation), direction.normalized);
                }
                m_Irradiance[start + i] = sh;
                m_Validity[start + i] = 0.0f;
            }
            m_Completed = start + count;
        }

        static void AddDirectSH(ref SphericalHarmonicsL2 sh, Vector3 energy, Vector3 direction, float cosHalfAngle = 1.0f)
        {
            float x = direction.x, y = direction.y, z = direction.z;
            // Exact SH integral over the uniformly sampled directional cone.
            // Its band multipliers are the averages of the Legendre polynomials.
            float energyScale = 2.0f / (1.0f + cosHalfAngle);
            float l1Scale = (1.0f + cosHalfAngle) * 0.5f;
            float l2Scale = cosHalfAngle * (1.0f + cosHalfAngle) * 0.5f;
            for (int c = 0; c < 3; ++c)
            {
                float value = energy[c] * energyScale;
                sh[c, 0] += value * 0.079577471545948f;
                // Match the native direct-radiance SH window used by the GPU
                // path before the coefficients are combined with indirect SH.
                sh[c, 1] += value * 0.159154943091895f * y * 0.922066f * l1Scale;
                sh[c, 2] += value * 0.159154943091895f * z * 0.922066f * l1Scale;
                sh[c, 3] += value * 0.159154943091895f * x * 0.922066f * l1Scale;
                sh[c, 4] += value * 0.298415518297304f * x * y * 0.731864f * l2Scale;
                sh[c, 5] += value * 0.298415518297304f * y * z * 0.731864f * l2Scale;
                sh[c, 6] += value * 0.024867959858109f * (3 * z * z - 1) * 0.731864f * l2Scale;
                sh[c, 7] += value * 0.298415518297304f * x * z * 0.731864f * l2Scale;
                sh[c, 8] += value * 0.074603879574326f * (x * x - y * y) * 0.731864f * l2Scale;
            }
        }

        static void WindowDirectSH(ref SphericalHarmonicsL2 sh)
        {
            for (int channel = 0; channel < 3; ++channel)
            {
                sh[channel, 1] *= 0.922066f;
                sh[channel, 2] *= 0.922066f;
                sh[channel, 3] *= 0.922066f;
                sh[channel, 4] *= 0.731864f;
                sh[channel, 5] *= 0.731864f;
                sh[channel, 6] *= 0.731864f;
                sh[channel, 7] *= 0.731864f;
                sh[channel, 8] *= 0.731864f;
            }
        }

        void BuildAccelerationStructure()
        {
#if HAS_FRAXINUS_LIGHT_TRANSPORT
            try
            {
                m_MaterialResources = new StandaloneAPVMaterialResources();
                if (!ExtractSceneData(out var renderers))
                    return;
                if (!m_HasGeometry)
                    return;
                var resources = new RayTracingResources();
                resources.Load();
                if (!RayTracingContext.IsBackendSupported(RayTracingBackend.Compute))
                {
                    Reject("the Fraxinus compute ray-tracing backend is unavailable.");
                    return;
                }
                m_RayTracingContext = new RayTracingContext(RayTracingBackend.Compute, resources);
                m_RayShader = m_RayTracingContext.CreateRayTracingShader(m_Shader);
                m_AccelerationStructure = new AccelStructAdapter(m_RayTracingContext.CreateAccelerationStructure(new AccelerationStructureOptions()), resources);
                foreach (var renderer in renderers)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter.sharedMesh;
                    var masks = new uint[mesh.subMeshCount];
                    var materials = new uint[masks.Length];
                    for (int i = 0; i < masks.Length; ++i)
                    {
                        masks[i] = 0xFFFFFFFFu;
                        TryGetMaterial(renderer.sharedMaterials[Mathf.Min(i, renderer.sharedMaterials.Length - 1)], out materials[i]);
                    }
                    m_AccelerationStructure.AddInstance(renderer.GetInstanceID(), mesh,
                        renderer.transform.localToWorldMatrix, masks, materials, 1);
                }
                m_MaterialResources.Build();
                m_LightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, m_Lights.Count), Marshal.SizeOf<BakeLight>());
                if (m_Lights.Count != 0)
                    m_LightBuffer.SetData(m_Lights);
                m_Command = new CommandBuffer { name = "Standalone APV Build Acceleration Structure" };
                var scratch = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(m_AccelerationStructure.GetAccelerationStructure(), m_RayShader, (uint)batchCapacity, 1, 1);
                m_AccelerationStructure.Build(m_Command, ref scratch);
                Graphics.ExecuteCommandBuffer(m_Command);
                scratch?.Dispose();
                m_Command.Clear();
            }
            catch (Exception e)
            {
                Debug.LogError($"Standalone APV acceleration structure build failed: {e.Message}");
                m_Failed = true;
                m_AccelerationStructure?.Dispose();
                m_AccelerationStructure = null;
            }
#endif
        }

        public bool Step()
        {
#if !HAS_FRAXINUS_LIGHT_TRANSPORT
            Debug.LogError("Standalone APV requires HAS_FRAXINUS_LIGHT_TRANSPORT.");
            return false;
#else
            if (!m_Initialized || m_Failed) return false;
            if (m_Pending) return true;
            if (m_Completed >= m_Positions.Length) return true;
            m_BatchStart = m_Completed;
            m_BatchCount = Mathf.Min(batchCapacity, m_Positions.Length - m_BatchStart);
            if (!m_HasGeometry)
            {
                BakeEmptyBatch(m_BatchStart, m_BatchCount);
                return true;
            }
            var batch = new Vector3[m_BatchCount];
            for (int i = 0; i < m_BatchCount; ++i) batch[i] = m_Positions[m_BatchStart + i];
            m_ProbeBuffer.SetData(batch, 0, 0, m_BatchCount);
            m_Command ??= new CommandBuffer { name = "Standalone APV Lighting" };
            if (m_RayShader == null || m_AccelerationStructure == null)
            {
                Debug.LogError("Standalone APV requires the Fraxinus compute ray-tracing backend.");
                m_Failed = true;
                return false;
            }
            m_AccelerationStructure.Bind(m_Command, "_AccelStruct", m_RayShader);
            m_RayShader.SetIntParam(m_Command, s_SampleCount, m_Settings.samples);
            m_RayShader.SetIntParam(m_Command, s_SampleStart, m_SampleStart);
            m_RayShader.SetIntParam(m_Command, s_SampleBatchSize, kSamplesPerDispatch);
            m_RayShader.SetIntParam(m_Command, s_DispatchID, ++m_DispatchID);
            m_RayShader.SetIntParam(m_Command, s_DirectSampleCount, m_Settings.directSamples);
            // Unity 6 gives environment importance sampling its own budget
            // (environmentSamples = 256 * 4 for the reference bake). It is
            // independent from the explicit-light direct budget (32 * 4).
            m_RayShader.SetIntParam(m_Command, s_EnvironmentSampleCount, m_Settings.environmentSamples);
            m_RayShader.SetIntParam(m_Command, s_MaxBounces, m_Settings.bounces);
            m_RayShader.SetIntParam(m_Command, s_MinBounces, m_MinBounces);
            // LightingSettings.m_IndirectOutputScale is 1 for the reference
            // Sponza bake. Bind it explicitly so a missing global shader
            // value cannot silently zero the indirect pass.
            m_RayShader.SetFloatParam(m_Command, s_IndirectScale, 1.0f);
            for (int coefficient = 0; coefficient < 9; ++coefficient)
            {
                var value = new Vector4(m_EnvironmentSH[0, coefficient], m_EnvironmentSH[1, coefficient],
                    m_EnvironmentSH[2, coefficient], 0.0f);
                m_RayShader.SetVectorParam(m_Command, s_EnvironmentSH[coefficient], value);
            }
            m_RayShader.SetFloatParam(m_Command, s_EnvironmentIntensity, m_Settings.environmentIntensity);
            m_RayShader.SetIntParam(m_Command, s_EnvironmentCdfConditionalResolution, kEnvironmentCdfConditionalResolution);
            m_RayShader.SetIntParam(m_Command, s_EnvironmentCdfMarginalResolution, kEnvironmentCdfMarginalResolution);
            m_RayShader.SetBufferParam(m_Command, s_EnvironmentCdfConditionalBuffer, m_EnvironmentCdfConditionalBuffer);
            m_RayShader.SetBufferParam(m_Command, s_EnvironmentCdfMarginalBuffer, m_EnvironmentCdfMarginalBuffer);
            m_RayShader.SetFloatParam(m_Command, s_EnvironmentCdfNormalization, m_EnvironmentCdfNormalization);
            // Keep the shader on its SH fallback whenever cube capture did not
            // produce a usable texture instead of sampling an unbound resource.
            bool useEnvironmentCube = m_HasEnvironmentCube && m_EnvironmentCube != null && m_EnvironmentCube.width > 0;
            m_RayShader.SetIntParam(m_Command, s_HasEnvironmentCube, useEnvironmentCube ? 1 : 0);
            m_RayShader.SetIntParam(m_Command, s_EnvironmentImportanceSampling,
                m_EnvironmentImportanceSampling ? 1 : 0);
            m_RayShader.SetTextureParam(m_Command, s_EnvironmentCube, new RenderTargetIdentifier(m_EnvironmentCube));
            // Match LightTransport's k_PushOffset.  A millimeter-scale offset
            // changes visibility around thin Sponza surfaces and is much
            // larger than the native 0.1 mm push-off.
            m_RayShader.SetFloatParam(m_Command, s_RayOffset, 0.0001f);
            m_RayShader.SetIntParam(m_Command, s_ProbeCount, m_BatchCount);
            m_RayShader.SetIntParam(m_Command, s_ProbeIndexOffset, m_BatchStart);
            m_RayShader.SetIntParam(m_Command, s_LightCount, m_Lights.Count);
            m_RayShader.SetBufferParam(m_Command, s_ProbePositions, m_ProbeBuffer);
            m_RayShader.SetBufferParam(m_Command, s_SobolMatricesBuffer, m_SobolBuffer);
            // NativeProbeSampling reproduces Unity 6's probe sequence for the
            // primary sphere and diffuse continuation dimensions. Direct
            // light sampling remains the standalone hash path (documented in
            // the shader) until the native light distribution is ported too.
            m_RayShader.SetIntParam(m_Command, s_NativeProbeSampling, 1);
            m_RayShader.SetBufferParam(m_Command, s_NativeProbeCranleyPatterson, m_NativeProbeCranleyPatterson);
            m_RayShader.SetTextureParam(m_Command, s_NativeProbeSobolSequence,
                new RenderTargetIdentifier(m_NativeProbeSobolSequence));
            m_RayShader.SetTextureParam(m_Command, s_NativeProbeRanking256,
                new RenderTargetIdentifier(m_NativeProbeRanking256));
            m_RayShader.SetTextureParam(m_Command, s_NativeProbeScrambling256,
                new RenderTargetIdentifier(m_NativeProbeScrambling256));
            // Unity imports PNG rows bottom-up, while the native blue-noise
            // byte tables use the source PNG's top-down row order. Flip the
            // integer Load coordinates for all three tables to preserve the
            // native sample sequence (including ranking and scrambling).
            m_RayShader.SetIntParam(m_Command, s_NativeProbeSamplingFlipY, 1);
            m_MaterialResources.Bind(m_Command, m_RayShader);
            m_RayShader.SetBufferParam(m_Command, s_Lights, m_LightBuffer);
            m_ScratchBuffer ??= RayTracingHelper.CreateScratchBufferForBuildAndDispatch(m_AccelerationStructure.GetAccelerationStructure(), m_RayShader, (uint)batchCapacity, 1, 1);
            // Keep direct radiance, indirect radiance and validity isolated,
            // matching Unity's native APV integration passes.
            DispatchPass(1, m_DirectOutputBuffer);
            DispatchPass(2, m_IndirectOutputBuffer);
            DispatchPass(3, m_ValidityOutputBuffer);
            Graphics.ExecuteCommandBuffer(m_Command);
            m_Command.Clear();
            m_Pending = true;
            m_ReadbacksPending = 3;
            m_ReadbackFailed = false;
            uint generation = m_Generation;
            int batchStart = m_BatchStart;
            int batchCount = m_BatchCount;
            AsyncGPUReadback.Request(m_DirectOutputBuffer, request => HandlePassReadback(request, 1, generation, batchStart, batchCount));
            AsyncGPUReadback.Request(m_IndirectOutputBuffer, request => HandlePassReadback(request, 2, generation, batchStart, batchCount));
            AsyncGPUReadback.Request(m_ValidityOutputBuffer, request => HandlePassReadback(request, 3, generation, batchStart, batchCount));
            return true;
#endif
        }

#if HAS_FRAXINUS_LIGHT_TRANSPORT
        void DispatchPass(int passMode, GraphicsBuffer output)
        {
            m_RayShader.SetIntParam(m_Command, s_PassMode, passMode);
            m_RayShader.SetBufferParam(m_Command, s_Out, output);
            m_RayShader.Dispatch(m_Command, m_ScratchBuffer, (uint)m_BatchCount, 1, 1);
        }

        void HandlePassReadback(AsyncGPUReadbackRequest request, int passMode, uint generation,
            int batchStart, int batchCount)
        {
            if (!m_Initialized || generation != m_Generation)
                return;

            if (request.hasError)
                m_ReadbackFailed = true;
            else
            {
                ProbeSH[] destination = passMode == 1 ? m_DirectReadback :
                    (passMode == 2 ? m_IndirectReadback : m_ValidityReadback);
                var source = request.GetData<ProbeSH>();
                for (int i = 0; i < batchCount; ++i)
                {
                    var value = source[i];
                    // A device reset can leave a readback containing zeros or
                    // stale output without reporting hasError. Never publish
                    // it as a successful black bake.
                    if (value.completedDispatch != (uint)m_DispatchID)
                    {
                        m_ReadbackFailed = true;
                        break;
                    }
                    if (m_SampleStart == 0)
                        destination[i] = value;
                    else
                        destination[i].Accumulate(value);
                }
            }

            if (--m_ReadbacksPending > 0)
                return;

            m_Pending = false;
            if (m_ReadbackFailed)
            {
                m_Failed = true;
                Debug.LogError("Standalone APV GPU integration failed or returned incomplete data. The previous bake has been preserved.");
                return;
            }

            m_SampleStart += kSamplesPerDispatch;
            if (m_SampleStart < Mathf.Max(m_Settings.samples,
                Mathf.Max(m_Settings.directSamples, m_Settings.environmentSamples)))
                return;
            m_SampleStart = 0;

            for (int i = 0; i < batchCount; ++i)
            {
                var direct = m_DirectReadback[i];
                var indirect = m_IndirectReadback[i];
                var sh = new SphericalHarmonicsL2();
                Vector3[] directCoefficients = { direct.c0, direct.c1, direct.c2, direct.c3, direct.c4, direct.c5, direct.c6, direct.c7, direct.c8 };
                Vector3[] indirectCoefficients = { indirect.c0, indirect.c1, indirect.c2, indirect.c3, indirect.c4, indirect.c5, indirect.c6, indirect.c7, indirect.c8 };
                for (int channel = 0; channel < 3; ++channel)
                    for (int coefficient = 0; coefficient < 9; ++coefficient)
                        sh[channel, coefficient] = directCoefficients[coefficient][channel] + indirectCoefficients[coefficient][channel];
                m_Irradiance[batchStart + i] = sh;
                m_Validity[batchStart + i] = m_ValidityReadback[i].validity;
            }
            m_Completed = batchStart + batchCount;
        }
#endif

        public void Dispose()
        {
            unchecked { ++m_Generation; }
            m_Initialized = false;
            m_Pending = false;
            m_ReadbacksPending = 0;
            m_ReadbackFailed = false;
            m_ProbeBuffer?.Dispose(); m_ProbeBuffer = null;
            m_DirectOutputBuffer?.Dispose(); m_DirectOutputBuffer = null;
            m_IndirectOutputBuffer?.Dispose(); m_IndirectOutputBuffer = null;
            m_ValidityOutputBuffer?.Dispose(); m_ValidityOutputBuffer = null;
            m_ScratchBuffer?.Dispose(); m_ScratchBuffer = null;
            m_Command?.Dispose(); m_Command = null;
            m_MaterialResources?.Dispose(); m_MaterialResources = null;
            m_LightBuffer?.Dispose(); m_LightBuffer = null;
            m_SobolBuffer?.Dispose(); m_SobolBuffer = null;
            m_NativeProbeCranleyPatterson?.Dispose(); m_NativeProbeCranleyPatterson = null;
            m_NativeProbeSobolSequence = null;
            m_NativeProbeRanking256 = null;
            m_NativeProbeScrambling256 = null;
            m_EnvironmentCdfConditionalBuffer?.Dispose(); m_EnvironmentCdfConditionalBuffer = null;
            m_EnvironmentCdfMarginalBuffer?.Dispose(); m_EnvironmentCdfMarginalBuffer = null;
#if HAS_FRAXINUS_LIGHT_TRANSPORT
            m_AccelerationStructure?.Dispose(); m_AccelerationStructure = null;
            m_RayTracingContext?.Dispose(); m_RayTracingContext = null;
#endif
            if (m_Irradiance.IsCreated) m_Irradiance.Dispose();
            if (m_Validity.IsCreated) m_Validity.Dispose();
            if (m_Occlusion.IsCreated) m_Occlusion.Dispose();
            m_Positions = default;
            m_Lights.Clear();
            m_SceneLights.Clear();
            m_EnvironmentSH = default;
            m_EnvironmentCdfNormalization = 0.0f;
            if (m_EnvironmentCube != null)
                UnityEngine.Object.DestroyImmediate(m_EnvironmentCube);
            m_EnvironmentCube = null;
            m_HasEnvironmentCube = false;
            m_HasGeometry = false;
        }
    }
}
