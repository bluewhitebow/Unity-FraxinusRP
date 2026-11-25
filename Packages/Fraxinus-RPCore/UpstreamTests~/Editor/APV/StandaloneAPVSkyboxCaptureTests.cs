using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.Tests
{
    public class StandaloneAPVSkyboxCaptureTests
    {
        readonly List<Object> m_Objects = new List<Object>();
        RenderTexture m_PreviousTarget;
        bool m_PreviousSRGB, m_PreviousInvertCulling, m_PreviousAsyncCompilation, m_StateSaved;
        RenderTexture m_Sentinel, m_SampleTarget;
        Texture2D m_Readback;
        Material m_Sampler;

        const string k_SampleShader = @"
Shader ""Hidden/APV/SkyboxCaptureTestSampler""
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include ""UnityCG.cginc""
            samplerCUBE _CapturedCube;
            float4 _Direction;
            float4 frag(v2f_img input) : SV_Target
            {
                return texCUBElod(_CapturedCube, float4(_Direction.xyz, 0));
            }
            ENDCG
        }
    }
}";

        const string k_GradientShader = @"
Shader ""Hidden/APV/SkyboxCaptureTestGradient""
{
    Properties { _Marker(""Marker"", Float) = 1 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _ _SUNDISK_NONE
            #include ""UnityCG.cginc""
            struct Varyings { float4 position : SV_POSITION; float3 direction : TEXCOORD0; };
            Varyings vert(float4 vertex : POSITION)
            {
                Varyings result;
                result.position = UnityObjectToClipPos(vertex);
                result.direction = vertex.xyz;
                return result;
            }
            float4 frag(Varyings input) : SV_Target
            {
                return float4(float3(2, 3, 4) + normalize(input.direction), 1);
            }
            ENDCG
        }
    }
}";

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                Assert.Ignore("Skybox capture tests require a graphics device with floating point render targets.");

            m_PreviousTarget = RenderTexture.active;
            m_PreviousSRGB = GL.sRGBWrite;
            m_PreviousInvertCulling = GL.invertCulling;
            m_PreviousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
            m_StateSaved = true;
            ShaderUtil.allowAsyncCompilation = false;
            m_Sentinel = Own(new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            Assert.That(m_Sentinel.Create(), Is.True);
            m_SampleTarget = Own(new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            Assert.That(m_SampleTarget.Create(), Is.True);
            m_Readback = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            m_Sampler = CreateMaterial(k_SampleShader);
        }

        [TearDown]
        public void TearDown()
        {
            if (m_StateSaved)
            {
                RenderTexture.active = m_PreviousTarget;
                GL.sRGBWrite = m_PreviousSRGB;
                GL.invertCulling = m_PreviousInvertCulling;
                ShaderUtil.allowAsyncCompilation = m_PreviousAsyncCompilation;
                m_StateSaved = false;
            }
            for (int i = m_Objects.Count - 1; i >= 0; --i)
                if (m_Objects[i] != null)
                    Object.DestroyImmediate(m_Objects[i]);
            m_Objects.Clear();
        }

        // The expected value is an analytic function of a world direction.
        // The oracle samples the returned cube on the GPU and never reuses the
        // capture helper's face matrices, FaceDirection, or CPU cube indexing.
        [TestCase(0.0f)]
        [TestCase(0.5f)]
        [TestCase(2.0f)]
        public void WorldDirectionGradientPreservesHDRAndIntensity(float intensity)
        {
            var source = CreateMaterial(k_GradientShader);
            source.SetFloat("_Marker", 0.37f);
            var cube = CaptureAndCheckState(source, intensity);
            var directions = new[]
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
                new Vector3(1, 0.35f, -0.6f), new Vector3(1, -0.7f, 0.2f),
                new Vector3(-1, 0.35f, -0.6f), new Vector3(-1, -0.7f, 0.2f),
                new Vector3(0.35f, 1, -0.6f), new Vector3(-0.7f, 1, 0.2f),
                new Vector3(0.35f, -1, -0.6f), new Vector3(-0.7f, -1, 0.2f),
                new Vector3(0.35f, -0.6f, 1), new Vector3(-0.7f, 0.2f, 1),
                new Vector3(0.35f, -0.6f, -1), new Vector3(-0.7f, 0.2f, -1)
            };
            foreach (var direction in directions)
            {
                Vector3 d = direction.normalized;
                var expected = (new Vector3(2, 3, 4) + d) * intensity;
                AssertRGB(SampleCube(cube, d), expected, 0.025f,
                    "World direction " + d + ", intensity " + intensity);
            }
        }

        [Test]
        public void SixSidedMaterialUsesTheAuthoredFacePasses()
        {
            // ShaderLab's six-sided sky order is front, back, left, right,
            // up, down. Each pass has a different HDR color, so a wrong pass
            // or mesh submesh cannot pass as a merely rotated gradient.
            var front = new Vector3(2, 0, 0);
            var back = new Vector3(0, 3, 0);
            var left = new Vector3(0, 0, 4);
            var right = new Vector3(2, 3, 0);
            var up = new Vector3(0, 3, 4);
            var down = new Vector3(2, 0, 4);
            var source = CreateMaterial(CreateSixSidedShader(new[] { front, back, left, right, up, down }));
            Assert.That(source.passCount, Is.EqualTo(6));
            var cube = CaptureAndCheckState(source, 1);
            AssertRGB(SampleCube(cube, Vector3.forward), front, 0.01f, "front");
            AssertRGB(SampleCube(cube, Vector3.back), back, 0.01f, "back");
            AssertRGB(SampleCube(cube, Vector3.right), left, 0.01f, "left skybox pass");
            AssertRGB(SampleCube(cube, Vector3.left), right, 0.01f, "right skybox pass");
            AssertRGB(SampleCube(cube, Vector3.up), up, 0.01f, "up");
            AssertRGB(SampleCube(cube, Vector3.down), down, 0.01f, "down");
        }

        Cubemap CaptureAndCheckState(Material source, float intensity)
        {
            var crc = source.ComputeCRC();
            string[] keywords = source.shaderKeywords;
            RenderTexture.active = m_Sentinel;
            GL.sRGBWrite = true;
            GL.invertCulling = true;
            var cube = Own(StandaloneAPVSkyboxCapture.Capture(source, null, 64, intensity));
            Assert.That(RenderTexture.active, Is.SameAs(m_Sentinel), "Capture changed the active render target.");
            Assert.That(GL.sRGBWrite, Is.True, "Capture changed GL.sRGBWrite.");
            Assert.That(GL.invertCulling, Is.True, "Capture changed GL.invertCulling.");
            Assert.That(source.ComputeCRC(), Is.EqualTo(crc), "Capture modified the source skybox material.");
            CollectionAssert.AreEquivalent(keywords, source.shaderKeywords, "Capture changed source keywords.");
            Assert.That(cube.isReadable, Is.True);
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
                Assert.That(cube.format, Is.EqualTo(TextureFormat.RGBAHalf));
            return cube;
        }

        Color SampleCube(Cubemap cube, Vector3 direction)
        {
            var previous = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            bool previousInvert = GL.invertCulling;
            try
            {
                GL.sRGBWrite = false;
                GL.invertCulling = false;
                m_Sampler.SetTexture("_CapturedCube", cube);
                m_Sampler.SetVector("_Direction", direction);
                Graphics.Blit(null, m_SampleTarget, m_Sampler);
                RenderTexture.active = m_SampleTarget;
                m_Readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                return m_Readback.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSRGB;
                GL.invertCulling = previousInvert;
            }
        }

        Material CreateMaterial(string source)
        {
            var shader = Own(ShaderUtil.CreateShaderAsset(source, true));
            Assert.That(shader.isSupported, Is.True, "Test shader failed to compile.");
            return Own(new Material(shader));
        }

        T Own<T>(T value) where T : Object
        {
            value.hideFlags = HideFlags.HideAndDontSave;
            m_Objects.Add(value);
            return value;
        }

        static void AssertRGB(Color actual, Vector3 expected, float tolerance, string context)
        {
            Assert.That(actual.r, Is.EqualTo(expected.x).Within(tolerance), context + " red");
            Assert.That(actual.g, Is.EqualTo(expected.y).Within(tolerance), context + " green");
            Assert.That(actual.b, Is.EqualTo(expected.z).Within(tolerance), context + " blue");
        }

        static string CreateSixSidedShader(Vector3[] colors)
        {
            var text = new StringBuilder("Shader \"Hidden/APV/SkyboxCaptureTestSixSided\" { SubShader { Cull Off ZWrite Off ZTest Always\n");
            foreach (var color in colors)
            {
                text.Append("Pass { CGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\n#include \"UnityCG.cginc\"\n");
                text.Append("float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }\n");
                text.AppendFormat(CultureInfo.InvariantCulture,
                    "float4 frag() : SV_Target {{ return float4({0}, {1}, {2}, 1); }}\n", color.x, color.y, color.z);
                text.Append("ENDCG\n}\n");
            }
            text.Append("} }");
            return text.ToString();
        }
    }
}
