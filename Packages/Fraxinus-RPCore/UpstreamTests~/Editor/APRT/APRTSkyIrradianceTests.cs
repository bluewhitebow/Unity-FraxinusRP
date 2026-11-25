using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Tests
{
    public class APRTSkyIrradianceTests
    {
        internal static APRTBakedData SkyGridData(float visibility = 1)
        {
            var data = APRTLightingTests.GridData(visibility);
            data.hasSkyIrradianceTransfer = true;
            var cell = data.cells[0];
            cell.skyIrradianceTransfer = new float[cell.probeCount * 36];
            for (int probe = 0; probe < cell.probeCount; ++probe)
            {
                int offset = probe * 36;
                // Exact integral for constant directional transmission q:
                // mean[V*b0]=q; 2*mean[V*w_i*b_i]=2q/3.
                cell.skyIrradianceTransfer[offset] = visibility;
                cell.skyIrradianceTransfer[offset + 9 + 3] = visibility * (2f / 3f);
                cell.skyIrradianceTransfer[offset + 18 + 1] = visibility * (2f / 3f);
                cell.skyIrradianceTransfer[offset + 27 + 2] = visibility * (2f / 3f);
            }
            return data;
        }

        internal static APRTBakedData RGBSkyGridData(float visibility = 1)
        {
            var data = SkyGridData(visibility);
            data.hasSkyBounceTransfer = true;
            foreach (var cell in data.cells)
                cell.skyIrradianceTransferRGB = cell.skyIrradianceTransfer.Select(value => Vector3.one * value).ToArray();
            return data;
        }

        [TestCase("null")]
        [TestCase("length")]
        [TestCase("nan-red")]
        [TestCase("infinite-green")]
        [TestCase("infinite-blue")]
        [TestCase("missing-direct")]
        public void MalformedColoredSkyTransferCannotReplaceExistingBake(string fault)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var previous = SkyGridData(); set.CommitAPRTBakedData(previous);
                var data = RGBSkyGridData();
                switch (fault)
                {
                    case "null": data.cells[0].skyIrradianceTransferRGB = null; break;
                    case "length": data.cells[0].skyIrradianceTransferRGB = new Vector3[35]; break;
                    case "nan-red": data.cells[0].skyIrradianceTransferRGB[0].x = float.NaN; break;
                    case "infinite-green": data.cells[0].skyIrradianceTransferRGB[0].y = float.PositiveInfinity; break;
                    case "infinite-blue": data.cells[0].skyIrradianceTransferRGB[0].z = float.NegativeInfinity; break;
                    case "missing-direct": data.hasSkyIrradianceTransfer = false; break;
                }
                Assert.That(data.HasValidSharedData(), Is.False);
                Assert.Throws<ArgumentException>(() => set.CommitAPRTBakedData(data));
                Assert.That(set.aprtBakedData, Is.SameAs(previous));
            }
            finally { Object.DestroyImmediate(set); }
        }

        [Test]
        public void ColoredSkyTransferSurvivesSerializationAndExplicitClearDropsItsFlag()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var copy = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = RGBSkyGridData(.25f);
                // Signed terms are valid transport coefficients.
                data.cells[0].skyIrradianceTransferRGB[7] = new Vector3(-.1f, .2f, -.3f);
                long directBytes = SkyGridData(.25f).GetDiskSizeOfSharedData();
                set.CommitAPRTBakedData(data);
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(set), copy);
                var restored = copy.aprtBakedData;
                Assert.That(restored.HasValidSharedData(), Is.True);
                Assert.That(restored.hasSkyBounceTransfer, Is.True);
                Assert.That(restored.cells[0].skyIrradianceTransferRGB, Is.EqualTo(data.cells[0].skyIrradianceTransferRGB));
                Assert.That(restored.GetDiskSizeOfSharedData() - directBytes, Is.EqualTo((long)data.cells[0].probeCount * 36 * 12));
                restored.ClearAssets();
                Assert.That(restored.hasSkyBounceTransfer, Is.False);
                Assert.That(restored.hasSkyIrradianceTransfer, Is.False);
                Assert.That(restored.cells, Is.Empty);
                Assert.That(restored.HasValidSharedData(), Is.False);
            }
            finally { Object.DestroyImmediate(set); Object.DestroyImmediate(copy); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void RGBBroadcastTransferExactlyMatchesScalarSkyAndPreservesDirectionalLighting(int model)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var scalarData = SkyGridData(.4f); scalarData.lightingModel = (APRTLightingModel)model;
                var rgbData = RGBSkyGridData(.4f); rgbData.lightingModel = (APRTLightingModel)model;
                // Exercise every row and L2 input, including signed coefficients.
                for (int coefficient = 0; coefficient < scalarData.cells[0].skyIrradianceTransfer.Length; ++coefficient)
                {
                    float value = (coefficient % 11 - 5) * .003f;
                    scalarData.cells[0].skyIrradianceTransfer[coefficient] = value;
                    rgbData.cells[0].skyIrradianceTransfer[coefficient] = value;
                    rgbData.cells[0].skyIrradianceTransferRGB[coefficient] = Vector3.one * value;
                }
                var radiance = Enumerable.Range(0, 9).Select(i => new Vector4(1 + i * .17f, 2 - i * .13f, i * .11f, 0)).ToArray();
                var lights = new[] { new Vector4(3, 2, 1, 1), Vector4.zero, Vector4.zero, Vector4.zero };
                PhysicalSkySettings(set); set.aprtIndirectIntensity = .7f;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                set.CommitAPRTBakedData(scalarData);
                var scalarSky = Relight(volume, radiance, lights);
                var scalarSun = new Vector4[volume.runtimeSHBuffer.count]; volume.runtimeSHBuffer.GetData(scalarSun);
                set.CommitAPRTBakedData(rgbData);
                var rgbSky = Relight(volume, radiance, lights);
                var rgbSun = new Vector4[volume.runtimeSHBuffer.count]; volume.runtimeSHBuffer.GetData(rgbSun);
                Assert.That(rgbSky, Is.EqualTo(scalarSky), "RGB broadcast must reproduce the old scalar dot products exactly.");
                Assert.That(rgbSun, Is.EqualTo(scalarSun), "Colored sky transport must not alter directional relighting.");
                Assert.That(volume.hasSkyBounceTransfer, Is.True);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ColoredSkyMatrixScalesChannelsAndLinearEnvironmentWithExactOffSwitches(int model)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = RGBSkyGridData(); data.lightingModel = (APRTLightingModel)model;
                var tint = new Vector3(.8f, .3f, .1f);
                for (int coefficient = 0; coefficient < data.cells[0].skyIrradianceTransferRGB.Length; ++coefficient)
                    data.cells[0].skyIrradianceTransferRGB[coefficient] = tint * data.cells[0].skyIrradianceTransfer[coefficient];
                set.CommitAPRTBakedData(data); PhysicalSkySettings(set);
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = .7f;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                var radiance = ConstantRadiance(new Vector3(2, 3, 4));
                radiance[3] = new Vector4(.2f, .4f, .6f, 0);
                radiance[1] = new Vector4(.3f, .5f, .7f, 0);
                radiance[2] = new Vector4(.4f, .6f, .8f, 0);
                var actual = Relight(volume, radiance);
                var normals = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, new Vector3(1, 2, 3).normalized };
                for (int probe = 0; probe < data.cells[0].probeCount; ++probe)
                    foreach (var normal in normals)
                    {
                        var response = (Vector3)actual[probe * 4] + (Vector3)actual[probe * 4 + 1] * normal.x
                            + (Vector3)actual[probe * 4 + 2] * normal.y + (Vector3)actual[probe * 4 + 3] * normal.z;
                        var incoming = new Vector3(2, 3, 4) +
                            ((Vector3)radiance[3] * normal.x + (Vector3)radiance[1] * normal.y + (Vector3)radiance[2] * normal.z) * (2f / 3f);
                        AssertRGB(response, Vector3.Scale(tint, incoming) * .7f, .00001f, "Colored linear environment");
                    }
                var doubled = Relight(volume, radiance.Select(value => value * 2).ToArray());
                for (int i = 0; i < actual.Length; ++i)
                    AssertRGB(doubled[i], (Vector3)actual[i] * 2, .00001f, "Linear source energy");
                Assert.That(Relight(volume, new Vector4[9]), Is.All.EqualTo(Vector4.zero), "No sky source cannot create bounce energy.");
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 0;
                Assert.That(Relight(volume, radiance), Is.All.EqualTo(Vector4.zero), "Sky gain is an exact off switch.");
                set.aprtGIIntensity = 0;
                Assert.That(volume.PrepareRelight(null), Is.False);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.EqualTo(0));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        internal static Vector4[] ConstantRadiance(Vector3 color)
        {
            var result = new Vector4[9]; result[0] = color; return result;
        }

        internal static Vector4[] ReadSky(APRTProbeReferenceVolume volume)
        {
            Assert.That(volume.runtimeSkyIrradianceBuffer, Is.Not.Null);
            var result = new Vector4[volume.runtimeSkyIrradianceBuffer.count];
            volume.runtimeSkyIrradianceBuffer.GetData(result); return result;
        }

        [TestCase(0, 1f)]
        [TestCase(1, 1f)]
        [TestCase(0, .25f)]
        [TestCase(1, .25f)]
        [TestCase(0, 0f)]
        [TestCase(1, 0f)]
        public void ConstantEnvironmentGivesIrradiancePiTimesRadianceAndRespectsTransmission(int model, float transmission)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = SkyGridData(transmission); data.lightingModel = (APRTLightingModel)model;
                set.CommitAPRTBakedData(data); PhysicalSkySettings(set);
                // Artistic directional controls must not contaminate physical sky irradiance.
                set.aprtIndirectIntensity = .13f; set.aprtSkyVisibilityPower = .31f; set.aprtFloorBoost = 15;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                var radiance = new Vector3(2, 3, 4);
                var actual = Relight(volume, ConstantRadiance(radiance));
                var expected = radiance * transmission;
                for (int probe = 0; probe < data.cells[0].probeCount; ++probe)
                {
                    AssertRGB(actual[probe * 4], expected, .00001f, "E/pi=L*V, probe " + probe);
                    for (int coefficient = 1; coefficient < 4; ++coefficient)
                        Assert.That(actual[probe * 4 + coefficient], Is.EqualTo(Vector4.zero));
                }
                if (transmission == 0) Assert.That(actual, Is.All.EqualTo(Vector4.zero));
                var sun = new Vector4[volume.runtimeSHBuffer.count]; volume.runtimeSHBuffer.GetData(sun);
                Assert.That(sun.Select(v => (Vector3)v), Is.All.EqualTo(Vector3.zero), "Independent sky must stay out of the directional light buffer.");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void LinearEnvironmentUsesCosineConvolutionAndZeroInputIsAnExactOffSwitch(int model)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume(); var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = SkyGridData(); data.lightingModel = (APRTLightingModel)model;
                set.CommitAPRTBakedData(data); PhysicalSkySettings(set);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                var radiance = ConstantRadiance(new Vector3(2, 3, 4));
                radiance[1] = new Vector4(0, 1, 0, 0); radiance[2] = new Vector4(0, 0, 1, 0); radiance[3] = new Vector4(1, 0, 0, 0);
                var actual = Relight(volume, radiance);
                var normals = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back, new Vector3(1, 2, 3).normalized };
                for (int probe = 0; probe < data.cells[0].probeCount; ++probe)
                    foreach (var normal in normals)
                    {
                        var response = (Vector3)actual[probe * 4] + (Vector3)actual[probe * 4 + 1] * normal.x
                            + (Vector3)actual[probe * 4 + 2] * normal.y + (Vector3)actual[probe * 4 + 3] * normal.z;
                        AssertRGB(response, new Vector3(2, 3, 4) + normal * (2f / 3f), .00001f, "Analytic cosine-convolved environment");
                    }
                Assert.That(Relight(volume, new Vector4[9]), Is.All.EqualTo(Vector4.zero));
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 0;
                Assert.That(Relight(volume, radiance), Is.All.EqualTo(Vector4.zero));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void OldTransportNeverInventsSkyFromScalarVisibilityOrAmbient(int model)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume(); var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = APRTLightingTests.GridData(); data.lightingModel = (APRTLightingModel)model;
                set.CommitAPRTBakedData(data); PhysicalSkySettings(set);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                Assert.That(Relight(volume, ConstantRadiance(new Vector3(2, 3, 4))), Is.All.EqualTo(Vector4.zero),
                    "A legacy bake lacks directional sky transport; it requires rebaking rather than scalar mean-visibility fallback.");
                var ambient = new SphericalHarmonicsL2(); ambient.AddAmbientLight(new Color(2, 3, 4));
                var packed = new SHCoefficients(ambient);
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, new Vector4[4], new Vector4[4],
                    new[] { packed.SHAr, packed.SHAg, packed.SHAb, packed.SHBr, packed.SHBg, packed.SHBb, packed.SHC },
                    ConstantRadiance(new Vector3(2, 3, 4)));
                Graphics.ExecuteCommandBuffer(cmd);
                var directional = new Vector4[volume.runtimeSHBuffer.count]; volume.runtimeSHBuffer.GetData(directional);
                Assert.That(directional, Is.All.EqualTo(Vector4.zero), "Ordinary ambient cannot fabricate physical sky in the legacy directional buffer.");
                Assert.That(ReadSky(volume), Is.All.EqualTo(Vector4.zero));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ZeroRadianceLightsNeverGateOrScaleIndependentSky(int model)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume(); var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = SkyGridData(); data.lightingModel = (APRTLightingModel)model;
                set.CommitAPRTBakedData(data); PhysicalSkySettings(set);
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = .7f;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                var radiance = ConstantRadiance(new Vector3(2, 3, 4));
                var disabled = Relight(volume, radiance);
                var enabledZeroRadiance = Relight(volume, radiance, Enumerable.Repeat(new Vector4(0, 0, 0, 1), 4).ToArray());
                for (int coefficient = 0; coefficient < disabled.Length; ++coefficient)
                    Assert.That((disabled[coefficient] - enabledZeroRadiance[coefficient]).magnitude, Is.LessThan(.00001f));
                for (int probe = 0; probe < data.cells[0].probeCount; ++probe)
                    AssertRGB(disabled[probe * 4], new Vector3(2, 3, 4) * .7f, .00001f, "Sky source gain is applied exactly once");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void CubemapRadianceIgnoresOrdinaryAmbientAndUpdatesWhenSourceChanges()
        {
            RequireGPU();
            var previousMode = RenderSettings.ambientMode; var previousColor = RenderSettings.ambientLight;
            float previousIntensity = RenderSettings.ambientIntensity; var previousProbe = RenderSettings.ambientProbe;
            var cube = new Cubemap(32, TextureFormat.RGBAFloat, false);
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.black;
                RenderSettings.ambientIntensity = 0; RenderSettings.ambientProbe = new SphericalHarmonicsL2();
                using var source = new APRTSkyEnvironment();
                SetConstantCube(cube, new Color(2, 3, 4, 1));
                Assert.That(source.Update(cube, null, null), Is.True);
                AssertRGB(source.radianceCoefficients[0], new Vector3(2, 3, 4), .001f, "Linear HDR cubemap radiance");
                for (int coefficient = 1; coefficient < 9; ++coefficient)
                    Assert.That(((Vector3)source.radianceCoefficients[coefficient]).magnitude, Is.LessThan(.002f));
                SetConstantCube(cube, new Color(1, .5f, 2, 1));
                Assert.That(source.Update(cube, null, null), Is.True);
                AssertRGB(source.radianceCoefficients[0], new Vector3(1, .5f, 2), .001f, "Modified cubemap must invalidate the cached projection");
                Assert.That(source.Update(null, null, null), Is.False);
                Assert.That(source.radianceCoefficients, Is.All.EqualTo(Vector4.zero));
            }
            finally
            {
                Object.DestroyImmediate(cube); RenderSettings.ambientMode = previousMode; RenderSettings.ambientLight = previousColor;
                RenderSettings.ambientIntensity = previousIntensity; RenderSettings.ambientProbe = previousProbe;
            }
        }

        [Test]
        public void AnalyticSkyboxProjectsRawRadianceAndReprojectsMaterialRotation()
        {
            RequireGPU();
            bool previousAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
            var shader = ShaderUtil.CreateShaderAsset(GradientShader, true); var material = new Material(shader);
            try
            {
                Assert.That(shader.isSupported, Is.True);
                using var source = new APRTSkyEnvironment();
                foreach (float rotation in new[] { 0f, 90f })
                {
                    material.SetFloat("_Rotation", rotation);
                    Assert.That(source.Update(null, material, null), Is.True);
                    float angle = rotation * Mathf.Deg2Rad;
                    AssertRGB(source.radianceCoefficients[0], new Vector3(2, 3, 4), .015f, "Raw L0 radiance");
                    AssertRGB(source.radianceCoefficients[3], new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)), .015f, "Raw X coefficient");
                    AssertRGB(source.radianceCoefficients[1], new Vector3(0, 1, 0), .015f, "Raw Y coefficient");
                    AssertRGB(source.radianceCoefficients[2], new Vector3(-Mathf.Sin(angle), 0, Mathf.Cos(angle)), .015f, "Raw Z coefficient");
                    for (int coefficient = 4; coefficient < 9; ++coefficient)
                        Assert.That(((Vector3)source.radianceCoefficients[coefficient]).magnitude, Is.LessThan(.02f));
                }
            }
            finally { Object.DestroyImmediate(material); Object.DestroyImmediate(shader); ShaderUtil.allowAsyncCompilation = previousAsync; }
        }

        [Test]
        public void ChangedAndCachedSkyboxUpdatesDoNotAllocateAfterWarmup()
        {
            RequireGPU();
            bool previousAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
            var shader = ShaderUtil.CreateShaderAsset(GradientShader, true); var material = new Material(shader);
            try
            {
                using var source = new APRTSkyEnvironment();
                Assert.That(source.Update(null, material, null), Is.True);
                material.SetFloat("_Rotation", 90);
                Assert.That(source.Update(null, material, null), Is.True);

                using var recorder = new APRTAllocationRecorder();
                recorder.Begin();
                material.SetFloat("_Rotation", 180);
                bool changed = source.Update(null, material, null);
                int changedAllocations = recorder.End();
                Assert.That(changed, Is.True);
                Assert.That(changedAllocations, Is.Zero, "A changed sky must reuse pixel storage, capture objects, and projection scratch space.");
                AssertRGB(source.radianceCoefficients[3], new Vector3(-1, 0, 0), .015f, "Changed source was actually reprojected");

                recorder.Begin();
                for (int i = 0; i < 8; ++i) source.Update(null, material, null);
                int cachedAllocations = recorder.End();
                Assert.That(cachedAllocations, Is.Zero, "An unchanged sky must not allocate while checking its source key.");
            }
            finally { Object.DestroyImmediate(material); Object.DestroyImmediate(shader); ShaderUtil.allowAsyncCompilation = previousAsync; }
        }

        [Test]
        public void SwitchingSkySourcesPreservesRadianceAndSourceMaterials()
        {
            RequireGPU();
            bool previousAsync = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
            var shader = ShaderUtil.CreateShaderAsset(GradientShader, true);
            var first = new Material(shader); var second = new Material(shader);
            var cube = new Cubemap(32, TextureFormat.RGBAFloat, false);
            try
            {
                first.SetFloat("_Rotation", 0); second.SetFloat("_Rotation", 90);
                int firstCRC = first.ComputeCRC(), secondCRC = second.ComputeCRC();
                SetConstantCube(cube, new Color(.5f, 1, 2, 1));
                using var source = new APRTSkyEnvironment();
                Assert.That(source.Update(null, first, null), Is.True);
                AssertRGB(source.radianceCoefficients[3], Vector3.right, .015f, "First sky material");
                Assert.That(source.Update(null, second, null), Is.True);
                AssertRGB(source.radianceCoefficients[3], Vector3.forward, .015f, "Replacement material with the same shader");
                Assert.That(source.Update(cube, second, null), Is.True);
                AssertRGB(source.radianceCoefficients[0], new Vector3(.5f, 1, 2), .001f, "Cubemap override");
                Assert.That(source.Update(null, first, null), Is.True);
                AssertRGB(source.radianceCoefficients[3], Vector3.right, .015f, "Restored sky shader after a cubemap override");
                Assert.That(first.ComputeCRC(), Is.EqualTo(firstCRC));
                Assert.That(second.ComputeCRC(), Is.EqualTo(secondCRC));
            }
            finally
            {
                Object.DestroyImmediate(cube); Object.DestroyImmediate(first); Object.DestroyImmediate(second);
                Object.DestroyImmediate(shader); ShaderUtil.allowAsyncCompilation = previousAsync;
            }
        }

        [Test]
        public void NativeCaptureIntegratesActualMissDirectionsAndSealedGeometryGetsZeroSky()
        {
            RequireGPU();
            var axes = new Vector4[6]; var moments = new Vector4[6]; var transfer = new float[36];
            var empty = new GIContributors { renderers = new List<(Renderer, Bounds)>(), terrains = new List<GIContributors.TerrainContributor>() };
            using (var capture = new APRTProbeCapture(empty, 64, 100, APRTLightingModel.APRTV))
                capture.Capture(Vector3.zero, axes, moments, 0, out _, transfer);
            var expectedOpen = SkyGridData().cells[0].skyIrradianceTransfer;
            for (int coefficient = 0; coefficient < 36; ++coefficient)
                Assert.That(transfer[coefficient], Is.EqualTo(expectedOpen[coefficient]).Within(.001f), "Unoccluded physical matrix coefficient " + coefficient);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetFloat("_Cull", 0); cube.GetComponent<Renderer>().sharedMaterial = material;
            try
            {
                cube.transform.localScale = Vector3.one * 10;
                var renderer = cube.GetComponent<Renderer>();
                var contributors = new GIContributors { renderers = new List<(Renderer, Bounds)> { (renderer, renderer.bounds) }, terrains = new List<GIContributors.TerrainContributor>() };
                using (var capture = new APRTProbeCapture(contributors, 64, 100, APRTLightingModel.APRTV))
                    capture.Capture(Vector3.zero, axes, moments, 0, out _, transfer);
                Assert.That(transfer, Is.All.EqualTo(0f), "A closed room has no true sky rays.");
                cube.transform.position = new Vector3(0, 95.1f, 0); cube.transform.localScale = new Vector3(200, .2f, 200);
                contributors.renderers[0] = (renderer, renderer.bounds);
                using (var capture = new APRTProbeCapture(contributors, 64, 100, APRTLightingModel.APRTV))
                    capture.Capture(Vector3.zero, axes, moments, 0, out _, transfer);
                Assert.That(axes[2].w, Is.GreaterThan(.999f), "Keep the original >90 axial-depth artistic sky classification.");
                Assert.That(transfer[0], Is.LessThan(.90f), "A real surface at distance 95 occludes physical sky despite its legacy artistic classification.");
            }
            finally { Object.DestroyImmediate(cube); Object.DestroyImmediate(material); }
        }

        [Test]
        public void NativeCaptureUpperAndLowerFaceOcclusionMatchIndependentWorldRayOracle()
        {
            RequireGPU();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); cube.GetComponent<Renderer>().sharedMaterial = material;
            try
            {
                var renderer = cube.GetComponent<Renderer>();
                var upper = new float[36]; var lower = new float[36];
                foreach (int sign in new[] { 1, -1 })
                {
                    // At z=2 the box covers exactly the corresponding half of the +Z face.
                    // The expected result is derived from world-space ray/AABB intersections,
                    // never from capture face orientation or cubemap indexing.
                    cube.transform.position = new Vector3(0, sign, 2.05f); cube.transform.localScale = new Vector3(20, 2, .1f);
                    var bounds = renderer.bounds;
                    var contributors = new GIContributors { renderers = new List<(Renderer, Bounds)> { (renderer, bounds) }, terrains = new List<GIContributors.TerrainContributor>() };
                    var actual = sign == 1 ? upper : lower;
                    using (var capture = new APRTProbeCapture(contributors, 64, 100, APRTLightingModel.APRTV))
                        capture.Capture(Vector3.zero, new Vector4[6], new Vector4[6], 0, out _, actual);
                    var expected = SphereTransmissionMatrix(w => !RayHitsBounds(w, bounds));
                    for (int coefficient = 0; coefficient < 36; ++coefficient)
                        Assert.That(actual[coefficient], Is.EqualTo(expected[coefficient]).Within(.012f),
                            "World-space " + (sign == 1 ? "upper" : "lower") + " occlusion coefficient " + coefficient);
                }
                Assert.That(upper[18], Is.LessThan(-.05f), "Blocking upper rays must give negative constant-sky Y response.");
                Assert.That(lower[18], Is.GreaterThan(.05f), "Blocking lower rays must give positive constant-sky Y response.");
                Assert.That(upper[0], Is.EqualTo(lower[0]).Within(.001f), "The two occluders have identical mean visibility.");
                // Radiance L=2+y is brighter above. With the same scalar visibility,
                // integrated energy must still distinguish which direction was blocked.
                float upperL0 = upper[0] * 2 + upper[1];
                float lowerL0 = lower[0] * 2 + lower[1];
                Assert.That(upperL0, Is.LessThan(lowerL0 - .05f), "Environment radiance must multiply directional transport before SH projection.");
            }
            finally { Object.DestroyImmediate(cube); Object.DestroyImmediate(material); }
        }

        static float[] SphereTransmissionMatrix(Func<Vector3, bool> visible)
        {
            const int count = 262144;
            var sums = new double[36];
            for (int i = 0; i < count; ++i)
            {
                double y = 1 - 2.0 * (i + .5) / count;
                double phi = Math.PI * 2 * ((i * .6180339887498948482) % 1.0);
                double r = Math.Sqrt(Math.Max(0, 1 - y * y));
                var w = new Vector3((float)(r * Math.Cos(phi)), (float)y, (float)(r * Math.Sin(phi)));
                if (!visible(w)) continue;
                var basis = new double[] { 1, w.y, w.z, w.x, w.x * w.y, w.y * w.z, 3 * w.z * w.z - 1, w.x * w.z, w.x * w.x - w.y * w.y };
                for (int coefficient = 0; coefficient < 9; ++coefficient)
                {
                    sums[coefficient] += basis[coefficient];
                    sums[9 + coefficient] += basis[coefficient] * 2 * w.x;
                    sums[18 + coefficient] += basis[coefficient] * 2 * w.y;
                    sums[27 + coefficient] += basis[coefficient] * 2 * w.z;
                }
            }
            return sums.Select(value => (float)(value / count)).ToArray();
        }
        static bool RayHitsBounds(Vector3 direction, Bounds bounds)
        {
            float near = .01f, far = float.PositiveInfinity;
            for (int axis = 0; axis < 3; ++axis)
            {
                if (Mathf.Abs(direction[axis]) < 1e-8f)
                {
                    if (bounds.min[axis] > 0 || bounds.max[axis] < 0) return false;
                    continue;
                }
                float a = bounds.min[axis] / direction[axis], b = bounds.max[axis] / direction[axis];
                near = Mathf.Max(near, Mathf.Min(a, b)); far = Mathf.Min(far, Mathf.Max(a, b));
                if (far < near) return false;
            }
            return far >= near;
        }

        internal static void PhysicalSkySettings(ProbeVolumeBakingSet set)
        {
            set.aprtTemporalBlend = 1; set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 1;
            set.aprtGIIntensity = 1; set.aprtFloorBoost = 0; set.aprtAddAmbientLighting = false;
        }
        static Vector4[] Relight(APRTProbeReferenceVolume volume, Vector4[] radiance, Vector4[] lights = null)
        {
            using var cmd = new CommandBuffer();
            volume.DispatchRelight(cmd, lights ?? new Vector4[4], Enumerable.Repeat(new Vector4(0, 1, 0, 0), 4).ToArray(), new Vector4[7], radiance);
            Graphics.ExecuteCommandBuffer(cmd); return ReadSky(volume);
        }
        static void SetConstantCube(Cubemap cube, Color color)
        {
            var pixels = Enumerable.Repeat(color, cube.width * cube.width).ToArray();
            for (int face = 0; face < 6; ++face) cube.SetPixels(pixels, (CubemapFace)face);
            cube.Apply(false, false);
        }
        internal static void AssertRGB(Vector3 actual, Vector3 expected, float tolerance, string context)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), context + " red");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), context + " green");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), context + " blue");
        }
        static void RequireGPU()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device.");
        }
        internal const string GradientShader = @"
Shader ""Hidden/APRT/SkyIrradianceGradientTest""
{
    Properties { _Rotation(""Rotation"", Float) = 0 }
    SubShader { Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include ""UnityCG.cginc""
        float _Rotation;
        struct Varyings { float4 position : SV_POSITION; float3 direction : TEXCOORD0; };
        Varyings vert(float4 vertex : POSITION) {
            Varyings o; o.position = UnityObjectToClipPos(vertex); o.direction = vertex.xyz; return o;
        }
        float4 frag(Varyings input) : SV_Target {
            float3 d = normalize(input.direction); float s, c; sincos(_Rotation * 0.01745329252, s, c);
            d = float3(c*d.x-s*d.z, d.y, s*d.x+c*d.z);
            return float4(float3(2,3,4)+d, 1);
        }
        ENDCG }
    }
}";
    }
}
