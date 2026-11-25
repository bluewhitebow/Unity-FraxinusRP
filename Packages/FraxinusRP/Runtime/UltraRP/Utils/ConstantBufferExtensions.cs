using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    static class ConstantBufferCompute
    {
        static readonly List<ConstantBufferComputeBase> s_registeredBuffers = new List<ConstantBufferComputeBase>();

        internal static void Register(ConstantBufferComputeBase cb)
        {
            s_registeredBuffers.Add(cb);
        }

        public static void ReleaseAll()
        {
            foreach (var cb in s_registeredBuffers)
                cb.Release();

            s_registeredBuffers.Clear();
        }

        public static void Push<CBType>(ComputeCommandBuffer cmd, in CBType data, ComputeShader cs, int shaderId)
            where CBType : struct
        {
            ConstantBufferCompute<CBType>.instance.Push(cmd, data, cs, shaderId);
        }


        public static void Push<CBType>(ComputeCommandBuffer cmd, in CBType data, Material mat, int shaderId)
            where CBType : struct
        {
            ConstantBufferCompute<CBType>.instance.Push(cmd, data, mat, shaderId);
        }

        public static void PushGlobal<CBType>(ComputeCommandBuffer cmd, in CBType data, int shaderId)
            where CBType : struct
        {
            ConstantBufferCompute<CBType>.instance.PushGlobal(cmd, data, shaderId);
        }

        public static void PushGlobal<CBType>(in CBType data, int shaderId)
            where CBType : struct
        {
            ConstantBufferCompute<CBType>.instance.PushGlobal(data, shaderId);
        }
    }

    abstract class ConstantBufferComputeBase
    {
        public abstract void Release();
    }

    class ConstantBufferCompute<CBType> : ConstantBufferComputeBase where CBType : struct
    {
        static ConstantBufferCompute<CBType> s_instance;
        readonly HashSet<int> _globalBindings = new HashSet<int>();

        internal static ConstantBufferCompute<CBType> instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new ConstantBufferCompute<CBType>();
                    ConstantBufferCompute.Register(s_instance);
                }
                return s_instance;
            }
        }

        readonly CBType[] _data = new CBType[1];
        ComputeBuffer _gpuBuffer;

        ConstantBufferCompute()
        {
            _gpuBuffer = new ComputeBuffer(1, UnsafeUtility.SizeOf<CBType>(), ComputeBufferType.Constant);
        }

        void UpdateData(ComputeCommandBuffer cmd, in CBType data)
        {
            _data[0] = data;
            cmd.SetBufferData(_gpuBuffer, _data);
        }

        void UpdateData(in CBType data)
        {
            _data[0] = data;
            _gpuBuffer.SetData(_data);
        }

        void SetGlobal(ComputeCommandBuffer cmd, int shaderId)
        {
            _globalBindings.Add(shaderId);
            cmd.SetGlobalConstantBuffer(_gpuBuffer, shaderId, 0, _gpuBuffer.stride);
        }

        void SetGlobal(int shaderId)
        {
            _globalBindings.Add(shaderId);
            Shader.SetGlobalConstantBuffer(shaderId, _gpuBuffer, 0, _gpuBuffer.stride);
        }

        void Set(ComputeCommandBuffer cmd, ComputeShader cs, int shaderId)
        {
            cmd.SetComputeConstantBufferParam(cs, shaderId, _gpuBuffer, 0, _gpuBuffer.stride);
        }

        void Set(Material mat, int shaderId)
        {
            mat.SetConstantBuffer(shaderId, _gpuBuffer, 0, _gpuBuffer.stride);
        }

        internal void Push(ComputeCommandBuffer cmd, in CBType data, ComputeShader cs, int shaderId)
        {
            UpdateData(cmd, data);
            Set(cmd, cs, shaderId);
        }

        internal void Push(ComputeCommandBuffer cmd, in CBType data, Material mat, int shaderId)
        {
            UpdateData(cmd, data);
            Set(mat, shaderId);
        }

        internal void PushGlobal(ComputeCommandBuffer cmd, in CBType data, int shaderId)
        {
            UpdateData(cmd, data);
            SetGlobal(cmd, shaderId);
        }

        internal void PushGlobal(in CBType data, int shaderId)
        {
            UpdateData(data);
            SetGlobal(shaderId);
        }

        public override void Release()
        {
            foreach (int shaderId in _globalBindings)
                Shader.SetGlobalConstantBuffer(shaderId, (ComputeBuffer)null, 0, 0);
            _globalBindings.Clear();

            CoreUtils.SafeRelease(_gpuBuffer);
            _gpuBuffer = null;
            s_instance = null;
        }
    }
}
