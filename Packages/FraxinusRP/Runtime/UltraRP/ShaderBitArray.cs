using System;
using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    internal struct ShaderBitArray
    {
        const int k_bitsPerElement = 32;
        const int k_elementShift = 5;
        const int k_elementMask = (1 << k_elementShift) - 1;

        private float[] _data;

        public int elemLength => _data == null ? 0 : _data.Length;
        public int bitCapacity => elemLength * k_bitsPerElement;
        public float[] data => _data;

        public void Resize(int bitCount)
        {
            if (bitCapacity > bitCount)
                return;

            int newElemCount = ((bitCount + (k_bitsPerElement - 1)) / k_bitsPerElement);
            if (newElemCount == _data?.Length)
                return;

            var newData = new float[newElemCount];
            if (_data != null)
            {
                for (int i = 0; i < _data.Length; i++)
                    newData[i] = _data[i];
            }
            _data = newData;
        }

        public void Clear()
        {
            for (int i = 0; i < _data.Length; i++)
                _data[i] = 0;
        }

        private void GetElementIndexAndBitOffset(int index, out int elemIndex, out int bitOffset)
        {
            elemIndex = index >> k_elementShift;
            bitOffset = index & k_elementMask;
        }

        public bool this[int index]
        {
            get
            {
                GetElementIndexAndBitOffset(index, out var elemIndex, out var bitOffset);

                unsafe
                {
                    fixed (float* floatData = _data)
                    {
                        uint* uintElem = (uint*)&floatData[elemIndex];
                        bool val = ((*uintElem) & (1u << bitOffset)) != 0u;
                        return val;
                    }
                }
            }
            set
            {
                GetElementIndexAndBitOffset(index, out var elemIndex, out var bitOffset);
                unsafe
                {
                    fixed (float* floatData = _data)
                    {
                        uint* uintElem = (uint*)&floatData[elemIndex];
                        if (value == true)
                            *uintElem = (*uintElem) | (1u << bitOffset);
                        else
                            *uintElem = (*uintElem) & ~(1u << bitOffset);
                    }
                }
            }
        }

        public override string ToString()
        {
            unsafe
            {
                Debug.Assert(bitCapacity < 4096, "Bit string too long! It was truncated!");
                int len = Math.Min(bitCapacity, 4096);
                byte* buf = stackalloc byte[len];
                for (int i = 0; i < len; i++)
                    buf[i] = (byte)(this[i] ? '1' : '0');

                return new string((sbyte*)buf, 0, len, System.Text.Encoding.UTF8);
            }
        }
    }
}
