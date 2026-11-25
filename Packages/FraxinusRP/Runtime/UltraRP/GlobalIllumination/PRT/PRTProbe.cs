using System;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Base;
using UObject = UnityEngine.Object;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class PRTProbe : IDisposable
    {
        /// <summary>
        /// Index in the volume grid
        /// </summary>
        public int index { get; }

        /// <summary>
        /// World position of this probe
        /// </summary>
        public Vector3 position => _volume.transform.position + _relativePosition;

        private readonly Vector3 _relativePosition;

        private readonly PRTProbeVolume _volume;

        internal PRTProbe(int index, Vector3 relativePosition, PRTProbeVolume probeVolume)
        {
            this.index = index;
            _relativePosition = relativePosition;
            _volume = probeVolume;
#if UNITY_EDITOR
            var probeObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            probeObject.name = $"PRTProbe {index}";
            probeObject.hideFlags = HideFlags.HideAndDontSave;
            probeObject.transform.SetParent(probeVolume.transform);
            probeObject.transform.position = position;
            UObject.DestroyImmediate(probeObject.GetComponent<SphereCollider>());
            _renderer = probeObject.GetComponent<MeshRenderer>();
            
            // Load PRT debug shader from runtime shaders resource
            var shader = Shader.Find("Hidden/UltraRP/PRT/ProbeSHDebug");
            if (shader != null)
                _renderer.material = CoreUtils.CreateEngineMaterial(shader);
            else
                Debug.LogWarning("PRT Probe SH Debug shader not found");
            
            _renderer.enabled = false;
            float size = _volume.probeHandleSize;
            probeObject.transform.localScale = new Vector3(size, size, size);
            _matPropBlock = new MaterialPropertyBlock();
#endif
        }

        public void Dispose()
        {
#if UNITY_EDITOR
            ReleaseDebugObject();
#endif
        }
    }
}
