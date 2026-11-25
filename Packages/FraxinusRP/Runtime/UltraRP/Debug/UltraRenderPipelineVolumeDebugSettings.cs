using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Volume debug settings.
    /// </summary>
    public partial class UltraRenderPipelineVolumeDebugSettings : VolumeDebugSettings<UltraAdditionalCameraData>
    {
        /// <summary>Selected camera volume stack.</summary>
        public override VolumeStack selectedCameraVolumeStack
        {
            get
            {
                Camera cam = selectedCamera;
                if (cam == null)
                    return null;

                var additionalCameraData = selectedCamera.GetComponent<UltraAdditionalCameraData>();
                if (additionalCameraData == null)
                    return null;

                var stack = additionalCameraData.volumeStack;
                if (stack != null)
                    return stack;

                return VolumeManager.instance.stack;
            }
        }

        /// <summary>Selected camera volume layer mask.</summary>
        public override LayerMask selectedCameraLayerMask
        {
            get
            {
                if (selectedCamera != null && selectedCamera.TryGetComponent<UltraAdditionalCameraData>(out var selectedAdditionalCameraData))
                    return selectedAdditionalCameraData.volumeLayerMask;

                return 1; // "Default"
            }
        }

        /// <summary>Selected camera volume position.</summary>
        public override Vector3 selectedCameraPosition => selectedCamera != null ? selectedCamera.transform.position : Vector3.zero;
    }
}
