using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Tests
{
    public class APRTLightingTests
    {
        internal static APRTBakedData GridData(float skyVisibility = 1, Vector3? albedo = null)
        {
            var tint = albedo ?? Vector3.one * .5f;
            var cell = new APRTBakedCell
            {
                index = 0,
                probeCount = 8,
                probePositions = new Vector3[8],
                axisData = new Vector4[48],
                visibilityMoments = Enumerable.Repeat(new Vector4(100, 10000, 0, 0), 48).ToArray(),
                probeValidity = Enumerable.Repeat(1f, 8).ToArray()
            };
            for (int z = 0; z < 2; ++z)
                for (int y = 0; y < 2; ++y)
                    for (int x = 0; x < 2; ++x)
                        cell.probePositions[x + 2 * (y + 2 * z)] = new Vector3(x, y, z);
            for (int i = 0; i < cell.axisData.Length; ++i)
                cell.axisData[i] = new Vector4(tint.x, tint.y, tint.z, skyVisibility);
            return new APRTBakedData
            {
                formatVersion = APRTBakedData.k_CurrentVersion,
                bakedMinDistanceBetweenProbes = 1,
                bakedSimplificationLevels = 0,
                globalBounds = new Bounds(Vector3.one * .5f, Vector3.one),
                cells = new List<APRTBakedCell> { cell },
                regularGrid = true,
                regularGridDimensions = new Vector3Int(2, 2, 2),
                regularGridRotation = Quaternion.identity,
                regularGridSpacing = 1,
                regularGridProbeIndices = Enumerable.Range(0, 8).ToArray(),
                hasLeakReductionData = true
            };
        }

        [Test]
        public void CurrentFormatSurvivesSerializationAndReportsUsableData()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var copy = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = GridData();
                set.CommitAPRTBakedData(data);
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(set), copy);
                Assert.That(copy.aprtBakedData.HasValidSharedData(), Is.True);
                Assert.That(copy.aprtBakedData.HasBakedData(null, false), Is.True);
                Assert.That(copy.aprtBakedData.HasSupportData(), Is.True);
                Assert.That(copy.aprtBakedData.cells[0].axisData, Is.EqualTo(data.cells[0].axisData));
                Assert.That(copy.aprtBakedData.regularGridProbeIndices, Is.EqualTo(data.regularGridProbeIndices));
                Assert.That(copy.aprtBakedData.GetDiskSizeOfSharedData(), Is.GreaterThan(0));
            }
            finally { Object.DestroyImmediate(set); Object.DestroyImmediate(copy); }
        }

        [TestCase("version")]
        [TestCase("axis-count")]
        [TestCase("non-finite-position")]
        [TestCase("non-finite-axis")]
        [TestCase("sky-visibility")]
        [TestCase("grid-index")]
        [TestCase("grid-count")]
        [TestCase("singular-domain")]
        public void MalformedBakesCannotReplaceThePreviousBake(string fault)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var previous = GridData();
                set.CommitAPRTBakedData(previous);
                var invalid = GridData();
                switch (fault)
                {
                    case "version": invalid.formatVersion = 0; break;
                    case "axis-count": invalid.cells[0].axisData = new Vector4[1]; break;
                    case "non-finite-position": invalid.cells[0].probePositions[0] = new Vector3(float.NaN, 0, 0); break;
                    case "non-finite-axis": invalid.cells[0].axisData[0].x = float.PositiveInfinity; break;
                    case "sky-visibility": invalid.cells[0].axisData[0].w = 1.1f; break;
                    case "grid-index": invalid.regularGridProbeIndices[0] = 8; break;
                    case "grid-count": invalid.regularGridProbeIndices = new int[1]; break;
                    case "singular-domain": invalid.samplingDomains = new[] { Matrix4x4.zero }; break;
                }
                Assert.That(invalid.HasValidSharedData(), Is.False);
                Assert.Throws<ArgumentException>(() => set.CommitAPRTBakedData(invalid));
                Assert.That(set.aprtBakedData, Is.SameAs(previous));
            }
            finally { Object.DestroyImmediate(set); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedCellAdapterResolvesCurrentDataAndRejectsMissingCellsAtomically(bool streamingOnly)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                var data = GridData();
                set.CommitAPRTBakedData(data);
                var format = new APRTProbeVolumeBakingDataFormat();
                Assert.That(format.CreateCellData(set, 0, streamingOnly), Is.TypeOf<APRTCellData>());
                Assert.That(format.CreateCellData(set, 77, streamingOnly), Is.Null);
                var missing = new ProbeVolumeBakingDataResolveContext(set, volume, new List<int> { 0, 77 }, streamingOnly, "APRT");
                Assert.That(format.ResolveSharedCellData(missing), Is.False);
                Assert.That(set.GetCellDataMap("APRT"), Is.Empty, "A failed resolution must not leave a partial cell map.");
                var valid = new ProbeVolumeBakingDataResolveContext(set, volume, new List<int> { 0 }, streamingOnly, "APRT");
                Assert.That(format.ResolveSharedCellData(valid), Is.True);
                Assert.That(format.ResolveScenarioCellData(valid), Is.True);
                Assert.That(((APRTCellData)set.GetCellDataMap("APRT")[0]).bakedCell, Is.SameAs(data.cells[0]));
                Assert.That(format.GetChunkGPUMemory(set, volume), Is.GreaterThan(0));
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero, "The cell adapter does not own GPU allocations.");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void GPUCaptureRecordsCubeAlbedoAndDistanceRatherThanOnlyClearSky()
        {
            RequireGPU();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                cube.transform.position = new Vector3(0, 0, 2);
                cube.transform.localScale = Vector3.one * 2;
                material.SetColor("_BaseColor", new Color(.8f, .04f, .03f));
                var renderer = cube.GetComponent<Renderer>();
                renderer.sharedMaterial = material;
                var contributors = new GIContributors
                {
                    renderers = new List<(Renderer, Bounds)> { (renderer, renderer.bounds) },
                    terrains = new List<GIContributors.TerrainContributor>()
                };
                using var capture = new APRTProbeCapture(contributors, 8, 30);
                var axes = new Vector4[6];
                var moments = new Vector4[6];
                capture.Capture(Vector3.zero, axes, moments, 0, out float validity);
                Assert.That(axes[4].w, Is.LessThan(.05f), "The +Z face is completely covered by the cube.");
                Assert.That(axes[4].x, Is.GreaterThan(.6f));
                Assert.That(axes[4].y, Is.LessThan(.1f));
                Assert.That(moments[4].x, Is.InRange(1, 2));
                Assert.That(validity, Is.GreaterThan(.9f));
                Assert.That(axes[5].w, Is.EqualTo(1).Within(.00001f), "The -Z face points into empty sky.");
                capture.Capture(cube.transform.position, axes, moments, 0, out float embeddedValidity);
                Assert.That(embeddedValidity, Is.EqualTo(0), "A probe surrounded by backfaces is invalid and stays within the serialized probability range.");
            }
            finally { Object.DestroyImmediate(cube); Object.DestroyImmediate(material); }
        }

        [Test]
        public void RuntimeRemainsUnpublishedUntilFullRelightAndReleasesOnReload()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            using var cmd = new CommandBuffer();
            try
            {
                set.CommitAPRTBakedData(GridData());
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);
                Assert.That(volume.PrepareRelight(null), Is.True);
                Assert.That(volume.runtimeSHBuffer.count, Is.EqualTo(8 * 4));
                Assert.That(volume.DataHasBeenLoaded(), Is.False);
                var initialized = new Vector4[volume.runtimeSHBuffer.count];
                volume.runtimeSHBuffer.GetData(initialized);
                Assert.That(initialized, Is.All.EqualTo(Vector4.zero));
                volume.BindPRVRuntimeResources(cmd, true);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
                cmd.Clear();
                volume.DispatchRelight(cmd, new Vector4[4], new Vector4[4], AmbientSky());
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(volume.DataHasBeenLoaded(), Is.True);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.EqualTo(1));
                Assert.That(volume.GetVideoMemoryCost(), Is.GreaterThan(0));
                volume.ReloadBakedData();
                Assert.That(volume.runtimeSHBuffer, Is.Null);
                Assert.That(volume.gpuBuffers, Is.Empty);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
                volume.Cleanup();
                volume.Cleanup();
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void ClearingLoadedBakingSetDisablesSamplingAndReleasesObsoleteBuffers()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                set.CommitAPRTBakedData(GridData());
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);
                Relight(volume, null, AmbientSky());
                Assert.That(volume.DataHasBeenLoaded(), Is.True);
                Assert.That(volume.GetVideoMemoryCost(), Is.GreaterThan(0));
                var previous = set.aprtBakedData;
                set.Clear("APRT");
                Assert.That(set.aprtBakedData, Is.SameAs(previous), "Generic clear mutates this payload in place.");
                Assert.That(volume.DataHasBeenLoaded(), Is.False);
                using var cmd = new CommandBuffer();
                volume.BindPRVRuntimeResources(cmd, true);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero, "Binding must detect invalidation before the next relight.");
                Assert.That(Shader.GetGlobalFloat("_APRTAddAmbientLighting"), Is.EqualTo(1));
                Assert.That(volume.PrepareRelight(null), Is.False);
                Assert.That(volume.runtimeSHBuffer, Is.Null);
                Assert.That(volume.gpuBuffers, Is.Empty);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
                Assert.That(Shader.GetGlobalInt("_APRTProbeCount"), Is.Zero);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void GridBindingMapsRotatedWorldPositionsToExactLatticeCoordinates()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var origin = new Vector3(11, -2, 7);
            var rotation = Quaternion.Euler(13, 67, -19);
            const float spacing = 1.7f;
            try
            {
                var data = GridData();
                data.regularGridOrigin = origin;
                data.regularGridRotation = rotation;
                data.regularGridSpacing = spacing;
                for (int i = 0; i < data.cells[0].probePositions.Length; ++i)
                    data.cells[0].probePositions[i] = origin + rotation * (data.cells[0].probePositions[i] * spacing);
                set.CommitAPRTBakedData(data);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);
                Relight(volume, null, AmbientSky());
                var worldToGrid = Shader.GetGlobalMatrix("_APRTWorldToGrid");
                var sampleCoordinates = new[] { Vector3.zero, Vector3.one, new Vector3(.3f, .7f, .8f), new Vector3(-.1f, 0, 0) };
                foreach (var coordinate in sampleCoordinates)
                {
                    var world = origin + rotation * (coordinate * spacing);
                    Assert.That((worldToGrid.MultiplyPoint3x4(world) - coordinate).sqrMagnitude, Is.LessThan(1e-8f),
                        "Sampling must reconstruct the baked grid positions exactly, including fractional coordinates and boundaries.");
                }
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void LegacyBakeCannotCreateSkyFromOrdinaryAmbientWithoutDirectionalTransport()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                set.aprtTemporalBlend = 1;
                set.CommitAPRTBakedData(GridData());
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);
                var dark = Relight(volume, null, new Vector4[7]);
                var sky = Relight(volume, null, AmbientSky());
                Assert.That(dark, Is.All.EqualTo(Vector4.zero));
                Assert.That(sky, Is.All.EqualTo(Vector4.zero),
                    "Legacy six-axis mean visibility is insufficient for physical sky irradiance. Ordinary ambient SH must not bypass a missing directional sky transfer bake.");
                AssertFinite(sky);
                set.aprtSkyIntensity = 0;
                Assert.That(Relight(volume, null, AmbientSky()), Is.All.EqualTo(Vector4.zero));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void DirectionalOutputContainsOnlyBounceAndScalesLinearly()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var colors = new[] { new Vector4(1, .5f, .25f, 1), Vector4.zero, Vector4.zero, Vector4.zero };
            try
            {
                set.aprtTemporalBlend = 1;
                set.aprtSkyIntensity = 0;
                set.CommitAPRTBakedData(GridData(.2f, new Vector3(.8f, .2f, .1f)));
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);
                set.aprtBounceIntensity = 0;
                Assert.That(Relight(volume, colors, new Vector4[7]), Is.All.EqualTo(Vector4.zero),
                    "Disabling indirect bounce must not leave direct light in APRT.");
                set.aprtBounceIntensity = 1;
                var once = Relight(volume, colors, new Vector4[7]);
                Assert.That(once.Any(value => value.x > .00001f), Is.True);
                set.aprtBounceIntensity = 2;
                var twice = Relight(volume, colors, new Vector4[7]);
                for (int i = 0; i < once.Length; ++i)
                    Assert.That((twice[i] - once[i] * 2).sqrMagnitude, Is.LessThan(1e-8f),
                        "Bounce intensity must be applied once.");
                AssertFinite(twice);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        static Vector4[] AmbientSky()
        {
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(.1f, .2f, .3f));
            var packed = new SHCoefficients(probe);
            return new[] { packed.SHAr, packed.SHAg, packed.SHAb, packed.SHBr, packed.SHBg, packed.SHBb, packed.SHC };
        }
        static Vector4[] Relight(APRTProbeReferenceVolume volume, Vector4[] colors, Vector4[] sky)
        {
            using var cmd = new CommandBuffer();
            volume.DispatchRelight(cmd, colors, new[] { new Vector4(0, 1, 0, 0), Vector4.zero, Vector4.zero, Vector4.zero }, sky);
            Graphics.ExecuteCommandBuffer(cmd);
            Assert.That(volume.runtimeSHBuffer, Is.Not.Null);
            var values = new Vector4[volume.runtimeSHBuffer.count];
            volume.runtimeSHBuffer.GetData(values);
            return values;
        }
        static void AssertFinite(Vector4[] values)
        {
            foreach (var value in values)
                for (int channel = 0; channel < 4; ++channel)
                    Assert.That(float.IsNaN(value[channel]) || float.IsInfinity(value[channel]), Is.False);
        }
        static void RequireGPU()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
        }
    }
}
