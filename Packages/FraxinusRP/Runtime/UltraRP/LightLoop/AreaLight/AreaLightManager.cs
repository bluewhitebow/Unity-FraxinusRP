using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    // Rectangle inputs are independent of native visibleLights: Unity 2022 does
    // not expose runtime rectangle dimensions or guarantee realtime area culling.
    internal sealed class AreaLightManager
    {
        internal const int MaxLights = 16;
        static readonly bool s_HardwareSupported = SystemInfo.supports2DArrayTextures && SystemInfo.graphicsShaderLevel >= 35;
        readonly Plane[] _frustum = new Plane[6];
        readonly UltraAdditionalLightData[] _selected = new UltraAdditionalLightData[MaxLights];
        readonly float[] _scores = new float[MaxLights];
        bool _overflowReported;

        internal sealed class Snapshot
        {
            internal int count;
            internal readonly Vector4[] positionRange = new Vector4[MaxLights];
            internal readonly Vector4[] rightSize = new Vector4[MaxLights];
            internal readonly Vector4[] upSize = new Vector4[MaxLights];
            internal readonly Vector4[] color = new Vector4[MaxLights];
            // Numeric uint16 halves preserve all 32 layer bits through float uniforms.
            internal readonly Vector4[] layers = new Vector4[MaxLights];
        }

        internal void Prepare(CameraData cameraData, LightData lightData, Snapshot output)
        {
            output.count = 0;
            var camera = cameraData.camera;
            if (!s_HardwareSupported || camera == null || camera.stereoEnabled || cameraData.isPreviewCamera ||
                UltraRenderer.IsOffscreenDepthTexture(cameraData))
                return;
#if UNITY_EDITOR
            if (cameraData.isSceneViewCamera && UnityEditor.SceneView.currentDrawingSceneView != null &&
                !UnityEditor.SceneView.currentDrawingSceneView.sceneLighting)
                return;
#endif

            GeometryUtility.CalculateFrustumPlanes(camera.cullingMatrix, _frustum);
            var candidates = UltraAdditionalLightData.areaLightCandidates;
            int eligibleCount = 0;
            for (int i = 0; i < candidates.Count; ++i)
            {
                var additional = candidates[i];
                if (additional == null || !additional.isActiveAndEnabled || !additional.isRealtimeAreaLight)
                    continue;
                var light = additional.light;
                if (light == null || !light.enabled || light.intensity <= 0f || light.range <= 0f ||
                    (light.cullingMask & camera.cullingMask) == 0)
                    continue;
#if UNITY_EDITOR
                // Respect prefab-stage isolation and the SceneView's scene mask.
                if (UnityEditor.SceneVisibilityManager.instance.IsHidden(light.gameObject))
                    continue;
                var cameraStage = camera.cameraType == CameraType.SceneView
                    ? UnityEditor.SceneManagement.StageUtility.GetCurrentStageHandle()
                    : UnityEditor.SceneManagement.StageUtility.GetStageHandle(camera.gameObject);
                if (UnityEditor.SceneManagement.StageUtility.GetStageHandle(light.gameObject) != cameraStage)
                    continue;
#endif
                var size = additional.areaSize;
                float radius = light.range + 0.5f * size.magnitude;
                var position = light.transform.position;
                if (!GeometryUtility.TestPlanesAABB(_frustum, new Bounds(position, Vector3.one * (2f * radius))))
                    continue;

                ++eligibleCount;
                float score = (position - camera.transform.position).sqrMagnitude;
                int insert = output.count;
                while (insert > 0 && (score < _scores[insert - 1] ||
                    (score == _scores[insert - 1] && light.GetInstanceID() < _selected[insert - 1].light.GetInstanceID())))
                    --insert;
                if (insert >= MaxLights)
                    continue;
                for (int slot = Mathf.Min(output.count, MaxLights - 1); slot > insert; --slot)
                {
                    _selected[slot] = _selected[slot - 1];
                    _scores[slot] = _scores[slot - 1];
                }
                _selected[insert] = additional;
                _scores[insert] = score;
                output.count = Mathf.Min(output.count + 1, MaxLights);
            }

            for (int i = 0; i < output.count; ++i)
            {
                var additional = _selected[i];
                var light = additional.light;
                var transform = light.transform;
                var size = additional.areaSize;
                Vector3 position = transform.position, right = transform.right, up = transform.up;
                output.positionRange[i] = new Vector4(position.x, position.y, position.z,
                    1f / Mathf.Max(light.range * light.range, 0.0001f));
                output.rightSize[i] = new Vector4(right.x, right.y, right.z, size.x * 0.5f);
                output.upSize[i] = new Vector4(up.x, up.y, up.z, size.y * 0.5f);
                Color radiance = GraphicsSettings.lightsUseLinearIntensity ? light.color.linear : light.color;
                if (GraphicsSettings.lightsUseColorTemperature && light.useColorTemperature)
                    radiance *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
                radiance *= light.intensity;
                output.color[i] = new Vector4(radiance.r, radiance.g, radiance.b, 0f);
                uint mask = lightData.supportsLightLayers
                    ? RenderingLayerUtils.ToValidRenderingLayers(additional.renderingLayers) : uint.MaxValue;
                output.layers[i] = new Vector4(mask & 0xffff, mask >> 16,
                    additional.areaDiffuseDimmer, additional.areaSpecularDimmer);
            }

            if (eligibleCount > MaxLights && !_overflowReported)
            {
                Debug.LogWarning("Fraxinus RP: this camera sees more than 16 realtime rectangle lights. The nearest 16 are used.", camera);
                _overflowReported = true;
            }
            else if (eligibleCount <= MaxLights)
                _overflowReported = false;
        }
    }
}
