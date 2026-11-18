using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public class GPUInstancerFloatingOriginHandler : MonoBehaviour
    {
        [System.NonSerialized]
        public List<GPUInstancerManager> gPUIManagers;
        public bool applyRotationAndScale;

        private Vector3 _previousPosition;
        private Matrix4x4 _previousMatrix;
        private Transform _cachedTransform;

        void OnEnable()
        {
            _cachedTransform = transform;
            _previousPosition = _cachedTransform.position;
            _previousMatrix = _cachedTransform.localToWorldMatrix;
            _cachedTransform.hasChanged = false;
        }

        void Update()
        {
            if (_cachedTransform.hasChanged)
            {
                if (applyRotationAndScale)
                {
                    Matrix4x4 newMatrix = _cachedTransform.localToWorldMatrix;
                    if (newMatrix != _previousMatrix)
                    {
                        if (gPUIManagers != null)
                        {
                            for (int i = gPUIManagers.Count - 1; i >= 0; i--)
                            {
                                GPUInstancerManager manager = gPUIManagers[i];
                                if (manager == null || manager.isDisposed)
                                {
                                    gPUIManagers.RemoveAt(i);
                                    continue;
                                }
                                GPUInstancerAPI.SetGlobalMatrixOffset(manager, newMatrix * _previousMatrix.inverse);
                            }
                        }
                        _previousMatrix = _cachedTransform.localToWorldMatrix;
                        _cachedTransform.hasChanged = false;
                    }
                }
                else if (_cachedTransform.position != _previousPosition)
                {
                    if (gPUIManagers != null)
                    {
                        for (int i = gPUIManagers.Count - 1; i >= 0; i--)
                        {
                            GPUInstancerManager manager = gPUIManagers[i];
                            if (manager == null || manager.isDisposed)
                            {
                                gPUIManagers.RemoveAt(i);
                                continue;
                            }
                            GPUInstancerAPI.SetGlobalPositionOffset(manager, _cachedTransform.position - _previousPosition);
                        }
                    }
                    _previousPosition = _cachedTransform.position;
                    _cachedTransform.hasChanged = false;
                }
            }
        }
    }
}
