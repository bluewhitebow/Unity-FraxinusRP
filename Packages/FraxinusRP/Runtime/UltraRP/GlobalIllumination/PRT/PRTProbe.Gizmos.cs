#if UNITY_EDITOR
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class PRTProbe
    {
        private static class ShaderConstants
        {
            public static readonly int _CoefficientSH9 = Shader.PropertyToID("_CoefficientSH9");
            public static readonly int _TintColor = Shader.PropertyToID("_TintColor");
        }

        private readonly MaterialPropertyBlock _matPropBlock;

        /// <summary>
        /// Debug renderer
        /// </summary>
        private readonly MeshRenderer _renderer;
        
        /// <summary>
        /// Update Probe debug visibility based on debug mode
        /// </summary>
        internal void UpdateVisibility()
        {
            // Update position and scale
            _renderer.transform.position = position;
            float size = _volume.probeHandleSize;
            _renderer.transform.localScale = new Vector3(size, size, size);
            
            bool shouldShowIrradianceSphere = _volume.debugMode == ProbeVolumeDebugMode.ProbeRadiance;
            
            // Hide when is selected and using other debug modes
            bool isSelected = _volume.selectedProbeIndex == index && index != -1;
            if (isSelected && _volume.selectedProbeDebugMode != ProbeDebugMode.IrradianceSphere)
            {
                shouldShowIrradianceSphere = false;
            }
            
            // Hide when show surfel brick to prevent hide surfel gizmos
            shouldShowIrradianceSphere &= _volume.selectedProbeDebugMode != ProbeDebugMode.SurfelBrickGrid;
            shouldShowIrradianceSphere &= !PRTVolumeManager.isBaking;
            _renderer.enabled = shouldShowIrradianceSphere;

            // Update material properties if sphere is visible
            if (shouldShowIrradianceSphere)
                UpdateIrradianceSphereShader();
        }

        /// <summary>
        /// Update irradiance sphere shader properties
        /// </summary>
        private void UpdateIrradianceSphereShader()
        {
            if (!_renderer || !_volume)
                return;

            var debugData = _volume.GetProbeDebugData(index);
            if (debugData == null) return;
            
            // Check if probe is invalidated, if so render as black
            if (!_volume.IsProbeValid(index))
            {
                // Set all SH coefficients to zero to render as black
                _matPropBlock.SetBuffer(ShaderConstants._CoefficientSH9, debugData.coefficientSH9);
                _matPropBlock.SetColor(ShaderConstants._TintColor, Color.black);
            }
            else
            {
                _matPropBlock.SetBuffer(ShaderConstants._CoefficientSH9, debugData.coefficientSH9);
                _matPropBlock.SetColor(ShaderConstants._TintColor, Color.white);
            }
            
            _renderer.SetPropertyBlock(_matPropBlock);
        }

        private void ReleaseDebugObject()
        {
            UObject.DestroyImmediate(_renderer.gameObject);
        }
    }
}
#endif
