using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    class ShaderData : IDisposable
    {
        static ShaderData _instance = null;
        ComputeBuffer _lightDataBuffer = null;
        ComputeBuffer _lightIndicesBuffer = null;

        ComputeBuffer _additionalLightShadowParamsStructuredBuffer = null;
        ComputeBuffer _additionalLightShadowSliceMatricesStructuredBuffer = null;

        ShaderData()
        {
        }

        internal static ShaderData instance
        {
            get
            {
                if (_instance == null)
                    _instance = new ShaderData();

                return _instance;
            }
        }

        public void Dispose()
        {
            DisposeBuffer(ref _lightDataBuffer);
            DisposeBuffer(ref _lightIndicesBuffer);
            DisposeBuffer(ref _additionalLightShadowParamsStructuredBuffer);
            DisposeBuffer(ref _additionalLightShadowSliceMatricesStructuredBuffer);
        }

        internal ComputeBuffer GetLightDataBuffer(int size)
        {
            return GetOrUpdateBuffer<ShaderInput.LightData>(ref _lightDataBuffer, size);
        }

        internal ComputeBuffer GetLightIndicesBuffer(int size)
        {
            return GetOrUpdateBuffer<int>(ref _lightIndicesBuffer, size);
        }

        internal ComputeBuffer GetAdditionalLightShadowParamsStructuredBuffer(int size)
        {
            return GetOrUpdateBuffer<Vector4>(ref _additionalLightShadowParamsStructuredBuffer, size);
        }

        internal ComputeBuffer GetAdditionalLightShadowSliceMatricesStructuredBuffer(int size)
        {
            return GetOrUpdateBuffer<Matrix4x4>(ref _additionalLightShadowSliceMatricesStructuredBuffer, size);
        }

        ComputeBuffer GetOrUpdateBuffer<T>(ref ComputeBuffer buffer, int size) where T : struct
        {
            if (buffer == null)
            {
                buffer = new ComputeBuffer(size, Marshal.SizeOf<T>());
            }
            else if (size > buffer.count)
            {
                buffer.Dispose();
                buffer = new ComputeBuffer(size, Marshal.SizeOf<T>());
            }

            return buffer;
        }

        void DisposeBuffer(ref ComputeBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Dispose();
                buffer = null;
            }
        }
    }
}