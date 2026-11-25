using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Tests
{
    public sealed class APRTSkyBounceTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Release()
        {
            foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
            owned.Clear();
        }

        Material Material(Color gammaAlbedo, float metallic = 0)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) Assert.Ignore("Requires URP Lit for actual diffuse material semantics.");
            var material = new Material(shader); owned.Add(material);
            material.SetColor("_BaseColor", gammaAlbedo); material.SetFloat("_Metallic", metallic);
            return material;
        }

        Renderer Plane(Material material, bool downward = false)
        {
            var owner = new GameObject("Analytic sky bounce plane"); owned.Add(owner);
            var mesh = new Mesh(); owned.Add(mesh);
            // At h=1, even the lowest sampled ray intersects far inside this plane.
            const float size = 100000;
            mesh.vertices = new[] { new Vector3(-size, 0, -size), new Vector3(size, 0, -size),
                new Vector3(size, 0, size), new Vector3(-size, 0, size) };
            mesh.triangles = downward ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 2, 1, 0, 3, 2 };
            mesh.normals = Enumerable.Repeat(downward ? Vector3.down : Vector3.up, 4).ToArray();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.RecalculateBounds(); owner.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = owner.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; return renderer;
        }

        static GIContributors Contributors(params Renderer[] renderers) => new GIContributors
        {
            renderers = renderers.Select(r => (r, r.bounds)).ToList(),
            terrains = new List<GIContributors.TerrainContributor>()
        };

        static Vector3[] Capture(GIContributors contributors, Vector3 probe, int primary = 1024, int secondary = 256)
        {
            var transfer = new Vector3[36];
            using var baker = new APRTSkyBounceBaker(contributors, primary, secondary, 100000);
            baker.Capture(probe, transfer, 0); return transfer;
        }

        static void RGB(Vector3 actual, Vector3 expected, float tolerance, string context)
        {
            for (int channel = 0; channel < 3; ++channel)
                Assert.That(actual[channel], Is.EqualTo(expected[channel]).Within(tolerance), context + ", channel " + channel);
        }

        static Vector3 CaptureGpuDiffuse(Renderer renderer)
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires actual GPU capture for the material-color oracle.");
            var axes = new Vector4[6]; var moments = new Vector4[6];
            using var capture = new APRTProbeCapture(Contributors(renderer), 16, 100000, APRTLightingModel.DiffuseTransfer);
            capture.Capture(Vector3.up, axes, moments, 0, out _);
            Assert.That(axes[3].w, Is.LessThan(.001f), "The downward capture face must be completely covered by the plane.");
            TestContext.WriteLine("Independent GPU material-color oracle " + axes[3]);
            return axes[3];
        }

        [TestCase(1f, 0f)]
        [TestCase(.5f, 0f)]
        [TestCase(0f, 0f)]
        [TestCase(1f, 1f)]
        public void UnoccludedPlaneMatchesIndependentLambertianHalfSphereIntegral(float gammaAlbedo, float metallic)
        {
            var material = Material(new Color(gammaAlbedo, gammaAlbedo, gammaAlbedo, 1), metallic);
            var renderer = Plane(material);
            var transfer = Capture(Contributors(renderer), Vector3.up);
            float rho = CaptureGpuDiffuse(renderer).x * (1 - metallic);
            // Constant sky L gives outgoing rho*L. Its lower-hemisphere integral
            // gives irradiance/pi L0=rho*L/2 and Y=-rho*L/2 in the existing L1 basis.
            RGB(transfer[0], Vector3.one * (.5f * rho), .003f, "Independent hemisphere L0");
            RGB(transfer[18], Vector3.one * (-.5f * rho), .003f, "Independent hemisphere Y");
            RGB(transfer[9], Vector3.zero, .003f, "Rotational symmetry X");
            RGB(transfer[27], Vector3.zero, .003f, "Rotational symmetry Z");
            if (rho == 0) Assert.That(transfer, Is.All.EqualTo(Vector3.zero), "Black/metal surfaces reflect no diffuse sky.");
        }

        [Test]
        public void ColoredPlaneReflectsChannelsWithoutInventingAnotherBounce()
        {
            var color = new Color(.8f, .1f, .025f, 1);
            var renderer = Plane(Material(color));
            var transfer = Capture(Contributors(renderer), Vector3.up);
            var expected = CaptureGpuDiffuse(renderer) * .5f;
            RGB(transfer[0], expected, .003f, "Each linear reflectance channel scales once");
            RGB(transfer[18], -expected, .003f, "Colored half-sphere directional response");
        }

        [Test]
        public void OneSidedBackfaceCannotEmitSkyBounceIntoItsBack()
        {
            var transfer = Capture(Contributors(Plane(Material(Color.white), true)), Vector3.up);
            Assert.That(transfer, Is.All.EqualTo(Vector3.zero));
        }

        [Test]
        public void SealedTwoSidedCubeHasExactlyZeroSkyOneBounce()
        {
            var owner = GameObject.CreatePrimitive(PrimitiveType.Cube); owned.Add(owner);
            owner.transform.localScale = Vector3.one * 4;
            var material = Material(Color.white); material.SetFloat("_Cull", 0);
            var renderer = owner.GetComponent<Renderer>(); renderer.sharedMaterial = material;
            var transfer = Capture(Contributors(renderer), Vector3.zero, 256, 128);
            Assert.That(transfer, Is.All.EqualTo(Vector3.zero), "Every surface's second segment is occluded; no ambient fallback.");
        }

        [Test]
        public void EmptyGeometryAndFullyClippedGeometryContributeExactlyZero()
        {
            Assert.That(Capture(Contributors(), Vector3.up, 64, 32), Is.All.EqualTo(Vector3.zero));
            var material = Material(new Color(1, 1, 1, 0));
            material.SetFloat("_AlphaClip", 1); material.SetFloat("_Cutoff", .5f); material.EnableKeyword("_ALPHATEST_ON");
            Assert.That(Capture(Contributors(Plane(material)), Vector3.up, 64, 32), Is.All.EqualTo(Vector3.zero),
                "Alpha-clipped triangles must be transparent to the transport ray.");
        }

        [Test]
        public void SourceTexturesHonorLinearDecodeAndDiffuseMetallicMap()
        {
            var material = Material(Color.white);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false); owned.Add(texture);
            texture.SetPixels(Enumerable.Repeat(new Color(.5f, .5f, .5f, 1), 4).ToArray()); texture.Apply();
            material.SetTexture("_BaseMap", texture);
            var renderer = Plane(material);
            var textured = Capture(Contributors(renderer), Vector3.up, 256, 64);
            float channel = texture.GetPixel(0, 0).r;
            RGB(textured[0], Vector3.one * (.5f * Mathf.GammaToLinearSpace(channel)), .004f,
                "sRGB albedo texture decoded exactly once");
            var metallic = new Texture2D(2, 2, TextureFormat.RGBA32, false, true); owned.Add(metallic);
            metallic.SetPixels(Enumerable.Repeat(Color.white, 4).ToArray()); metallic.Apply();
            material.SetTexture("_MetallicGlossMap", metallic); material.EnableKeyword("_METALLICSPECGLOSSMAP");
            Assert.That(Capture(Contributors(renderer), Vector3.up, 256, 64), Is.All.EqualTo(Vector3.zero),
                "The metallic map removes diffuse reflection even when scalar metallic is zero.");
        }

        [Test]
        public void CaptureIsDeterministicIndependentOfProbeOrderAndDoesNotModifySources()
        {
            var material = Material(new Color(.8f, .1f, .025f, 1)); var renderer = Plane(material);
            var beforeMaterial = EditorJsonUtility.ToJson(material); var beforeMesh = EditorJsonUtility.ToJson(renderer.GetComponent<MeshFilter>().sharedMesh);
            using var baker = new APRTSkyBounceBaker(Contributors(renderer), 256, 64, 100000);
            var first = new Vector3[72]; var second = new Vector3[72];
            baker.Capture(Vector3.up, first, 0); baker.Capture(Vector3.up * 2, first, 1);
            baker.Capture(Vector3.up * 2, second, 1); baker.Capture(Vector3.up, second, 0);
            Assert.That(first, Is.EqualTo(second));
            Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(beforeMaterial));
            Assert.That(EditorJsonUtility.ToJson(renderer.GetComponent<MeshFilter>().sharedMesh), Is.EqualTo(beforeMesh));
        }

        [Test]
        public void UnreadableMeshCanBakeWithoutChangingItsReadWriteSetting()
        {
            var renderer = Plane(Material(Color.white));
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            mesh.UploadMeshData(true);
            Assert.That(mesh.isReadable, Is.False, "The fixture must reproduce a mesh with CPU Read/Write disabled.");
            var transfer = Capture(Contributors(renderer), Vector3.up, 256, 64);
            RGB(transfer[0], Vector3.one * .5f, .004f, "Editor mesh snapshots preserve the Lambertian hemisphere integral");
            Assert.That(mesh.isReadable, Is.False, "Offline transport must not change source mesh readability.");
        }
    }
}
