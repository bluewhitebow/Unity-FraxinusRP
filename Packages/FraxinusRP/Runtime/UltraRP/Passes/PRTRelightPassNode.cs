using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, NodeMenuItem("UltraRP/PRT/PRT Relight Pass")]
    public class PRTRelightPassNode : ScriptableRenderPassNode
    {
        [Tooltip("Enable PRT relight for this pass")]
        public bool enableRelight = true;

        public PRTRelightPassNode() : base()
        {
            profilingSampler = new ProfilingSampler(nameof(PRTRelightPassNode));
        }

        public override bool SupportedInCurrentFrame(ContextContainer frameData)
        {
            if (!enableRelight || !PRTProbeVolume.isFeatureEnabled)
                return false;

            var volume = PRTVolumeManager.probeVolume;
            return volume != null && volume.IsActivate();
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            CameraData cameraData = frameData.Get<CameraData>();
            
            if (cameraData.cameraType is CameraType.Reflection or CameraType.Preview)
                return;

#if UNITY_EDITOR
            if (PRTVolumeManager.isBaking)
                return;
#endif

            var volume = PRTVolumeManager.probeVolume;
            if (volume == null || !volume.IsActivate())
                return;

            var renderer = cameraData.renderer;
            var prtRelightPass = renderer.GetModule<PRTRelightPass>();
            
            if (prtRelightPass != null)
            {
                prtRelightPass.Render(renderGraph, frameData, volume);
            }
        }
    }
}
