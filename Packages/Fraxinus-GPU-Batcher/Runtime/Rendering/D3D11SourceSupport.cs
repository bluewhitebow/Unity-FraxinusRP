using System;
using FraxinusEngine.Rendering.Ultra;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher.Rendering
{
    // The custom D3D11 adapter accepts the audited opaque pass layout for camera draws.
    internal static class D3D11SourceSupport
    {
        internal static bool requiresValidation
        {
            get
            {
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
                return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 && GPUBatcherRenderer.canSubmitDraws;
#else
                return false;
#endif
            }
        }

        static readonly Lazy<Func<Material, bool>> materialSupport = new Lazy<Func<Material, bool>>(() =>
        {
            var type = Type.GetType("Fraxinus.BrgContract.Managed.PlayerOutputTranslator, Fraxinus.BrgContract.Managed", false);
            var method = type?.GetMethod("SupportsD3D11IndexedMaterial", new[] {typeof(Material)});
            return method == null ? null : (Func<Material, bool>)method.CreateDelegate(typeof(Func<Material, bool>));
        });

        internal static bool SupportsCameraPasses =>
            GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset asset &&
            (asset.shadowDistance <= 0 || (!asset.supportsMainLightShadows && !asset.supportsAdditionalLightShadows));

        internal static bool Supports(Material[] materials)
        {
            if (materials == null || materials.Length == 0) return false;
            var supports = materialSupport.Value;
            if (supports == null) return false;
            foreach (var material in materials)
                if (!material || material.renderQueue > (int)RenderQueue.GeometryLast || !supports(material)) return false;
            return true;
        }

        internal static bool Supports(GameObject prefab)
        {
            if (!prefab) return false;
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return false;
            foreach (var renderer in renderers)
                if (!(renderer is MeshRenderer) || !Supports(renderer.sharedMaterials)) return false;
            return true;
        }
    }
}
