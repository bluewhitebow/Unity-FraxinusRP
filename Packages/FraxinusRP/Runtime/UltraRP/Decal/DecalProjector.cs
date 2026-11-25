using System;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
    using UnityEditor;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>The scaling mode to apply to decals that use the Decal Projector.</summary>
    public enum DecalScaleMode
    {
        /// <summary>Ignores the transformation hierarchy and uses the scale values in the Decal Projector component directly.</summary>
        ScaleInvariant,
        /// <summary>Multiplies the lossy scale of the Transform with the Decal Projector's own scale then applies this to the decal.</summary>
        [InspectorName("Inherit from Hierarchy")]
        InheritFromHierarchy,
    }

    /// <summary>
    /// Decal Projector component.
    /// </summary>
    [ExecuteAlways]
#if UNITY_EDITOR
    [CanEditMultipleObjects]
#endif
    [AddComponentMenu("Rendering/UltraRP Decal Projector")]
    public class DecalProjector : MonoBehaviour
    {
        internal delegate void DecalProjectorAction(DecalProjector decalProjector);
        internal static event DecalProjectorAction onDecalAdd;
        internal static event DecalProjectorAction onDecalRemove;
        internal static event DecalProjectorAction onDecalPropertyChange;
        internal static event Action onAllDecalPropertyChange;
        internal static event DecalProjectorAction onDecalMaterialChange;
        internal static Material defaultMaterial { get; set; }
        internal static bool isSupported => onDecalAdd != null;

        internal DecalEntity decalEntity { get; set; }

        [SerializeField]
        private Material _material = null;
        /// <summary>
        /// The material used by the decal.
        /// </summary>
        public Material material
        {
            get
            {
                return _material;
            }
            set
            {
                _material = value;
                OnValidate();
            }
        }

        [SerializeField]
        private float _drawDistance = 1000.0f;
        /// <summary>
        /// Distance from camera at which the Decal is not rendered anymore.
        /// </summary>
        public float drawDistance
        {
            get
            {
                return _drawDistance;
            }
            set
            {
                _drawDistance = Mathf.Max(0f, value);
                OnValidate();
            }
        }

        [SerializeField]
        [Range(0, 1)]
        private float _fadeScale = 0.9f;
        /// <summary>
        /// Percent of the distance from the camera at which this Decal start to fade off.
        /// </summary>
        public float fadeScale
        {
            get
            {
                return _fadeScale;
            }
            set
            {
                _fadeScale = Mathf.Clamp01(value);
                OnValidate();
            }
        }

        [SerializeField]
        [Range(0, 180)]
        private float _startAngleFade = 180.0f;
        /// <summary>
        /// Angle between decal backward orientation and vertex normal of receiving surface at which the Decal start to fade off.
        /// </summary>
        public float startAngleFade
        {
            get
            {
                return _startAngleFade;
            }
            set
            {
                _startAngleFade = Mathf.Clamp(value, 0.0f, 180.0f);
                OnValidate();
            }
        }

        [SerializeField]
        [Range(0, 180)]
        private float _endAngleFade = 180.0f;
        /// <summary>
        /// Angle between decal backward orientation and vertex normal of receiving surface at which the Decal end to fade off.
        /// </summary>
        public float endAngleFade
        {
            get
            {
                return _endAngleFade;
            }
            set
            {
                _endAngleFade = Mathf.Clamp(value, _startAngleFade, 180.0f);
                OnValidate();
            }
        }

        [SerializeField]
        private Vector2 _uvScale = new Vector2(1, 1);
        /// <summary>
        /// Tilling of the UV of the projected texture.
        /// </summary>
        public Vector2 uvScale
        {
            get
            {
                return _uvScale;
            }
            set
            {
                _uvScale = value;
                OnValidate();
            }
        }

        [SerializeField]
        private Vector2 _uvBias = new Vector2(0, 0);
        /// <summary>
        /// Offset of the UV of the projected texture.
        /// </summary>
        public Vector2 uvBias
        {
            get
            {
                return _uvBias;
            }
            set
            {
                _uvBias = value;
                OnValidate();
            }
        }


        [SerializeField]
        uint _decalLayerMask = 1;
        /// <summary>
        /// The layer of the decal.
        /// </summary>
        public uint renderingLayerMask
        {
            get => _decalLayerMask;
            set => _decalLayerMask = value;
        }

        [SerializeField]
        private DecalScaleMode _scaleMode = DecalScaleMode.ScaleInvariant;
        /// <summary>
        /// The scaling mode to apply to decals that use this Decal Projector.
        /// </summary>
        public DecalScaleMode scaleMode
        {
            get => _scaleMode;
            set
            {
                _scaleMode = value;
                OnValidate();
            }
        }

        [SerializeField]
        internal Vector3 offsetValue = new Vector3(0, 0, 0.5f);
        /// <summary>
        /// Change the offset position.
        /// Do not expose: Could be changed by the inspector when manipulating the gizmo.
        /// </summary>
        public Vector3 pivot
        {
            get
            {
                return offsetValue;
            }
            set
            {
                offsetValue = value;
                OnValidate();
            }
        }

        [SerializeField]
        internal Vector3 sizeValue = new Vector3(1, 1, 1);
        /// <summary>
        /// The size of the projection volume.
        /// </summary>
        public Vector3 size
        {
            get
            {
                return sizeValue;
            }
            set
            {
                sizeValue = value;
                OnValidate();
            }
        }

        [SerializeField]
        [Range(0, 1)]
        private float _fadeFactor = 1.0f;
        /// <summary>
        /// Controls the transparency of the decal.
        /// </summary>
        public float fadeFactor
        {
            get
            {
                return _fadeFactor;
            }
            set
            {
                _fadeFactor = Mathf.Clamp01(value);
                OnValidate();
            }
        }

        /// <summary>A scale that should be used for rendering and handles.</summary>
        internal Vector3 effectiveScale => _scaleMode == DecalScaleMode.InheritFromHierarchy ? transform.lossyScale : Vector3.one;
        /// <summary>current size in a way the DecalSystem will be able to use it</summary>
        internal Vector3 decalSize => new Vector3(sizeValue.x, sizeValue.z, sizeValue.y);
        /// <summary>current size in a way the DecalSystem will be able to use it</summary>
        internal Vector3 decalOffset => new Vector3(offsetValue.x, -offsetValue.z, offsetValue.y);
        /// <summary>current uv parameters in a way the DecalSystem will be able to use it</summary>
        internal Vector4 uvScaleBias => new Vector4(_uvScale.x, _uvScale.y, _uvBias.x, _uvBias.y);

        private Material _oldMaterial = null;
        private float _oldDrawDistance = 1000.0f;
        private float _oldFadeScale = 0.9f;
        private float _oldStartAngleFade = 180.0f;
        private float _oldEndAngleFade = 180.0f;
        private Vector2 _oldUVScale = new Vector2(1, 1);
        private Vector2 _oldUVBias = new Vector2(0, 0);
        private DecalScaleMode _oldScaleMode = DecalScaleMode.ScaleInvariant;
        private Vector3 _oldOffset = new Vector3(0, 0, 0.5f);
        private Vector3 _oldSize = new Vector3(1, 1, 1);
        private float _oldFadeFactor = 1.0f;

        void InitMaterial()
        {
            if (_material == null)
            {
#if UNITY_EDITOR
                _material = defaultMaterial;
#endif
            }
        }

        void OnEnable()
        {
            InitMaterial();

            _oldMaterial = _material;

            onDecalAdd?.Invoke(this);

#if UNITY_EDITOR
            // Handle scene visibility
            UnityEditor.SceneVisibilityManager.visibilityChanged += UpdateDecalVisibility;
#endif
        }

#if UNITY_EDITOR
        void UpdateDecalVisibility()
        {
            // Fade out the decal when it is hidden by the scene visibility
            if (UnityEditor.SceneVisibilityManager.instance.IsHidden(gameObject))
            {
                onDecalRemove?.Invoke(this);
            }
            else
            {
                onDecalAdd?.Invoke(this);
                onDecalPropertyChange?.Invoke(this); // Scene culling mask may have changed.
            }
        }
#endif

        void OnDisable()
        {
            onDecalRemove?.Invoke(this);

#if UNITY_EDITOR
            UnityEditor.SceneVisibilityManager.visibilityChanged -= UpdateDecalVisibility;
#endif
        }

        internal void OnValidate()
        {
            if (!isActiveAndEnabled)
                return;

            if (_material != _oldMaterial)
            {
                onDecalMaterialChange?.Invoke(this);
                _oldMaterial = _material;
            }
            else
                onDecalPropertyChange?.Invoke(this);

            _oldDrawDistance = _drawDistance;
            _oldFadeScale = _fadeScale;
            _oldStartAngleFade = _startAngleFade;
            _oldEndAngleFade = _endAngleFade;
            _oldUVScale = _uvScale;
            _oldUVBias = _uvBias;
            _oldScaleMode = _scaleMode;
            _oldOffset = offsetValue;
            _oldSize = sizeValue;
            _oldFadeFactor = _fadeFactor;
        }

        void OnDidApplyAnimationProperties()
        {
            // Needed to be able to update state properly for animated serialized-properties.
            if (_oldMaterial != _material ||
                Mathf.Abs(_oldDrawDistance - _drawDistance) > Mathf.Epsilon ||
                Mathf.Abs(_oldFadeScale - _fadeScale) > Mathf.Epsilon ||
                Mathf.Abs(_oldStartAngleFade - _startAngleFade) > Mathf.Epsilon ||
                Mathf.Abs(_oldEndAngleFade - _endAngleFade) > Mathf.Epsilon ||
                _oldUVScale != _uvScale ||
                _oldUVBias != _uvBias ||
                _oldScaleMode != _scaleMode ||
                _oldOffset != offsetValue ||
                _oldSize != sizeValue ||
                Mathf.Abs(_oldFadeFactor - _fadeFactor) > Mathf.Epsilon)
            {
                OnValidate();
            }
        }

        /// <summary>
        /// Checks if material is valid for rendering decals.
        /// </summary>
        /// <returns>True if material is valid.</returns>
        public bool IsValid()
        {
            if (material == null)
                return false;

            if (material.FindPass(DecalShaderPassNames.dBufferProjector) != -1)
                return true;

            if (material.FindPass(DecalShaderPassNames.decalProjectorForwardEmissive) != -1)
                return true;

            if (material.FindPass(DecalShaderPassNames.decalScreenSpaceProjector) != -1)
                return true;

            if (material.FindPass(DecalShaderPassNames.decalGBufferProjector) != -1)
                return true;

            return false;
        }

        internal static void UpdateAllDecalProperties()
        {
            onAllDecalPropertyChange?.Invoke();
        }
    }
}

