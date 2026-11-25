using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    public enum PRTProbeAdjustmentMode
    {
        ApplyVirtualOffset,
        OverrideVirtualOffsetSettings,
        IntensityScale,
        InvalidateProbes
    }

    public enum PRTProbeAdjustmentShape
    {
        Box,
        Sphere
    }

    [ExecuteAlways]
    [AddComponentMenu("Rendering/PRT/PRT Probe Adjustment Volume")]
    public class PRTProbeAdjustmentVolume : MonoBehaviour
    {
        public PRTProbeAdjustmentShape shape = PRTProbeAdjustmentShape.Box;
        public Vector3 size = Vector3.one;
        public float radius = 1.0f;
        public PRTProbeAdjustmentMode mode = PRTProbeAdjustmentMode.ApplyVirtualOffset;

        [Range(0f, 360f)]
        public float virtualOffsetRotation;

        [Range(0f, 10f)]
        public float virtualOffsetDistance = 1f;

        [Range(0f, 1f)]
        public float geometryBias = 0.1f;

        [Range(0f, 1f)]
        public float rayOriginBias = 0.1f;

        [Range(0f, 5f)]
        public float intensityScale = 1f;

#if UNITY_EDITOR
        [HideInInspector]
        public bool showVolumeBounds = true;
#endif

        private void OnEnable()
        {
            PRTVolumeManager.RegisterAdjustmentVolume(this);
        }

        private void OnDisable()
        {
            PRTVolumeManager.UnregisterAdjustmentVolume(this);
        }

        public bool Contains(Vector3 point)
        {
            Vector3 localPoint = transform.InverseTransformPoint(point);

            if (shape == PRTProbeAdjustmentShape.Box)
            {
                return Mathf.Abs(localPoint.x) <= size.x * 0.5f &&
                       Mathf.Abs(localPoint.y) <= size.y * 0.5f &&
                       Mathf.Abs(localPoint.z) <= size.z * 0.5f;
            }

            return localPoint.magnitude <= radius;
        }

        public Vector3 GetAdditionalVirtualOffset()
        {
            if (mode != PRTProbeAdjustmentMode.ApplyVirtualOffset)
                return Vector3.zero;

            Vector3 offsetDirection = Quaternion.AngleAxis(virtualOffsetRotation, Vector3.up) * Vector3.forward;
            return offsetDirection * virtualOffsetDistance;
        }

        public float GetIntensityScale()
        {
            return mode == PRTProbeAdjustmentMode.IntensityScale ? intensityScale : 1f;
        }

        public bool ShouldInvalidateProbe()
        {
            return mode == PRTProbeAdjustmentMode.InvalidateProbes;
        }
    }
}
