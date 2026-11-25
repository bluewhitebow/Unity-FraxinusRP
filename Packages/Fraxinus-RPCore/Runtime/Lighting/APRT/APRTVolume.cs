using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    /// <summary>Region and probe layout for Adaptive Precompute Radiance Transfer.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Rendering/Adaptive PRT Volume")]
    public sealed class APRTVolume : MonoBehaviour
    {
        /// <summary>Which scenes or region the component describes.</summary>
        public enum Mode
        {
            /// <summary>All participating scenes in the Baking Set.</summary>
            Global,
            /// <summary>Only the scene containing this component.</summary>
            Scene,
            /// <summary>The local oriented box.</summary>
            Local
        }

        /// <summary>Probe layout used by this volume.</summary>
        public enum Layout
        {
            /// <summary>Geometry-adaptive bricks clipped to the volume.</summary>
            Adaptive,
            /// <summary>A bounded regular lattice matching the original PRT probe layout.</summary>
            ReferenceGrid
        }

        // Keep serialized layout names compatible with existing volumes.
        [HideInInspector] public Layout layout = Layout.Adaptive;
        [Tooltip("Number of unique probes on each axis of the reference grid.")]
        [HideInInspector] public Vector3Int regularGridDimensions = new Vector3Int(13, 4, 6);
        [HideInInspector, Min(0.01f)] public float regularGridSpacing = 1.7f;
        [Tooltip("Sampler dimensions in the grid's rotated axes. Zero uses the captured lattice extent. Override when the sampling box differs from the captured probe extent.")]
        [HideInInspector] public Vector3 regularGridSamplingSize;
        [Tooltip("Sampler origin offset from the first captured probe, in the grid's rotated axes. Used when Sampling Size is set.")]
        [HideInInspector] public Vector3 regularGridSamplingOriginOffset;
        [Tooltip("First probe offset from this transform, in its rotated local axes. Transform scale is ignored.")]
        [HideInInspector] public Vector3 regularGridOriginOffset;

        /// <summary>World position of the first reference-grid probe.</summary>
        public Vector3 regularGridOrigin => transform.position + transform.rotation * regularGridOriginOffset;

        [Tooltip("Global describes the Baking Set. Scene describes this scene. Local describes the oriented box.")]
        public Mode mode = Mode.Local;
        [Tooltip("World-space dimensions of the local box. Transform scale is ignored, as with Adaptive Probe Volumes.")]
        public Vector3 size = Vector3.one * 10.0f;
        [Tooltip("Override the Baking Set's probe spacing range for this Adaptive volume.")]
        [HideInInspector] public bool overridesSubdivLevels;
        [Tooltip("Finest allowed Adaptive subdivision level. Each level increases probe spacing by a factor of three.")]
        [HideInInspector] public int lowestSubdivLevelOverride;
        [Tooltip("Coarsest allowed Adaptive subdivision level, limited by the Baking Set.")]
        [HideInInspector] public int highestSubdivLevelOverride = ProbeBrickIndex.kMaxSubdivisionLevels;
        [Tooltip("Fill empty regions with coarse Adaptive bricks. Disabling can leave regions without probe coverage.")]
        [HideInInspector] public bool fillEmptySpaces = true;
        [HideInInspector] public bool overrideRendererFilters;
        [HideInInspector] public LayerMask objectLayerMask = -1;
        [HideInInspector, Min(0)] public float minRendererVolumeSize = 0.1f;

        /// <summary>Unit box coordinates (minus one half to one half) to world coordinates, independent of Transform scale.</summary>
        public Matrix4x4 volumeMatrix => Matrix4x4.TRS(transform.position, transform.rotation, Abs(size));

        internal Matrix4x4 boundsMatrix => Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        /// <summary>Axis aligned enclosure of the local oriented box.</summary>
        public Bounds worldBounds
        {
            get
            {
                var half = Abs(size) * 0.5f;
                var rotation = transform.rotation;
                var extents = Abs(rotation * Vector3.right) * half.x + Abs(rotation * Vector3.up) * half.y + Abs(rotation * Vector3.forward) * half.z;
                return new Bounds(transform.position, extents * 2.0f);
            }
        }

        /// <summary>Tests the local oriented box. Scene and Global volumes have no spatial boundary.</summary>
        public bool ContainsPoint(Vector3 worldPosition)
        {
            if (mode != Mode.Local)
                return true;
            var point = Quaternion.Inverse(transform.rotation) * (worldPosition - transform.position);
            var half = Abs(size) * 0.5f + Vector3.one * 0.00001f;
            return Mathf.Abs(point.x) <= half.x && Mathf.Abs(point.y) <= half.y && Mathf.Abs(point.z) <= half.z;
        }

        /// <summary>Tests intersection with the axis aligned enclosure of the local box.</summary>
        public bool IntersectsBounds(Bounds bounds) => mode != Mode.Local || worldBounds.Intersects(bounds);

        /// <summary>Whether a renderer scene is included before the local geometry test.</summary>
        public bool IncludesScene(Scene scene) => mode != Mode.Scene || scene == gameObject.scene;

        void OnValidate()
        {
            size = new Vector3(SanitizeSize(size.x), SanitizeSize(size.y), SanitizeSize(size.z));
        }

        static float SanitizeSize(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 10 : Mathf.Max(0.001f, Mathf.Abs(value));
        static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }
}
