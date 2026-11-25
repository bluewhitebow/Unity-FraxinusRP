using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public interface IUberEffectProcessor
    {
        Type componentType { get; }

        string displayName { get; }

        bool Valid(bool enable);

        void Setup(Material material, VolumeComponent component, PostProcessPass pass, CameraData cameraData);
    }
}

