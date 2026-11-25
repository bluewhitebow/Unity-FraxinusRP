using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace UnityEngine.Rendering.Tests
{
    class APVProbeVolumeEditorTests
    {
        [Test]
        public void EmptyRenderingLayerMasksKeepUniversalMask()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                set.useRenderingLayers = true;
                set.renderingLayerMasks = Array.Empty<ProbeVolumeBakingSet.ProbeLayerMask>();
                uint4 masks = set.ComputeRegionMasks();
                Assert.That(masks.x, Is.EqualTo(uint.MaxValue));
                Assert.That(masks.y, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void URPLayerSupportUsesPublicCapabilityProperty()
        {
            var asset = ScriptableObject.CreateInstance<TestURPAsset>();
            try
            {
                Assert.That(APVProbeVolumeBakingSettingsEditor.GetURPSupportsLayers(typeof(TestURPAsset), asset), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void URPLayerSupportFallsBackToPrivateField()
        {
            var asset = ScriptableObject.CreateInstance<TestLegacyURPAsset>();
            try
            {
                Assert.That(APVProbeVolumeBakingSettingsEditor.GetURPSupportsLayers(typeof(TestLegacyURPAsset), asset), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void HDRPLayerSupportReadsNestedSettings()
        {
            var asset = ScriptableObject.CreateInstance<TestHDRPAsset>();
            try
            {
                Assert.That(APVProbeVolumeBakingSettingsEditor.GetHDRPSupportsLayers(typeof(TestHDRPAsset), asset), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        class TestURPAsset : RenderPipelineAsset
        {
            public bool useRenderingLayers => true;
            protected override RenderPipeline CreatePipeline() => null;
        }

        class TestLegacyURPAsset : RenderPipelineAsset
        {
            bool m_SupportsLightLayers = true;
            public bool CurrentValue => m_SupportsLightLayers;
            protected override RenderPipeline CreatePipeline() => null;
        }

        class TestHDRPAsset : RenderPipelineAsset
        {
            readonly TestHDRPSettings m_RenderPipelineSettings = new();
            protected override RenderPipeline CreatePipeline() => null;
        }

        class TestHDRPSettings
        {
            bool supportLightLayers = true;
            public bool CurrentValue => supportLightLayers;
        }
    }
}
