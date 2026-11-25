using System.Collections.Generic;
using NUnit.Framework;

namespace UnityEngine.Rendering.Tests
{
    public class APRTReferenceCaptureTests
    {
        [Test]
        public void ReferenceCaptureClassifiesAxialDepthAboveNinetyAsSkyAndKeepsFrustumCorners()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device.");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", new Color(.8f, .04f, .03f));
            cube.GetComponent<Renderer>().sharedMaterial = material;
            var axes = new Vector4[6];
            var moments = new Vector4[6];
            try
            {
                void CaptureAt(Vector3 position, Vector3 scale)
                {
                    cube.transform.position = position;
                    cube.transform.localScale = scale;
                    var renderer = cube.GetComponent<Renderer>();
                    var contributors = new GIContributors
                    {
                        renderers = new List<(Renderer, Bounds)> { (renderer, renderer.bounds) },
                        terrains = new List<GIContributors.TerrainContributor>()
                    };
                    using var capture = new APRTProbeCapture(contributors, 64, 100, APRTLightingModel.APRTV, centerPower: 0);
                    capture.Capture(Vector3.zero, axes, moments, 0, out _);
                }
                CaptureAt(new Vector3(0, 80.1f, 0), new Vector3(200, .2f, 200));
                Assert.That(axes[2].x, Is.GreaterThan(.6f), "A face at axial depth 80 is hit transport.");
                Assert.That(axes[2].w, Is.LessThan(.001f));
                CaptureAt(new Vector3(0, 95.1f, 0), new Vector3(200, .2f, 200));
                Assert.That(axes[2].x, Is.Zero, "Reference capture treats positive axial depth above 90 as sky even before the camera far plane.");
                Assert.That(axes[2].w, Is.GreaterThan(.999f));
                CaptureAt(new Vector3(79, 79, 80.1f), new Vector3(4, 4, .2f));
                Assert.That(axes[4].x, Is.GreaterThan(.0001f), "A visible frustum corner remains a hit when its radial distance exceeds 100.");
                Assert.That(axes[4].w, Is.InRange(.4f, .5f), "Only uncovered upward rays contribute reference sky visibility.");
            }
            finally { Object.DestroyImmediate(cube); Object.DestroyImmediate(material); }
        }
    }
}
