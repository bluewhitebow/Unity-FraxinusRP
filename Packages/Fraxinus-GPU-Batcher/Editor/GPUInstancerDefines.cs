using Fraxinus.GPUBatcher;
using Fraxinus.GPUBatcher.Editor.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Fraxinus.GPUBatcher.Editor
{
    [InitializeOnLoad]
    public class GPUInstancerDefines
    {
        // billboard extensions
        private static Type _billboardExtensionType;
        private static Assembly _billboardExtensionAssebly;
        public static List<GPUInstancerBillboardExtension> billboardExtensions;
        public static GPUInstancerPreviewCache previewCache;

        static GPUInstancerDefines()
        {
            GetBillboardExtensions();

            if (previewCache == null)
                previewCache = new GPUInstancerPreviewCache();
        }

        static void GetBillboardExtensions()
        {
                if (billboardExtensions == null)
                    billboardExtensions = new List<GPUInstancerBillboardExtension>();

                if (_billboardExtensionType == null)
                    _billboardExtensionType = typeof(GPUInstancerBillboardExtension);

                if (_billboardExtensionAssebly == null)
                    _billboardExtensionAssebly = Assembly.GetAssembly(_billboardExtensionType);

                IEnumerable<Type> types = _billboardExtensionAssebly.GetTypes()
                    .Where(p => _billboardExtensionType.IsAssignableFrom(p) && p != _billboardExtensionType);

                foreach (Type type in types)
                {
                    ConstructorInfo ci = type.GetConstructor(Type.EmptyTypes);
                    GPUInstancerBillboardExtension billboardExtension = (GPUInstancerBillboardExtension)ci.Invoke(null);
                    billboardExtensions.Add(billboardExtension);
                }
        }

        public static GPUInstancerShaderBindings GetGPUInstancerShaderBindings()
        {
            if (GPUInstancerConstants.projectSettings.shaderBindings == null)
                GPUInstancerConstants.projectSettings.shaderBindings = GPUInstancerProjectSettings.GetDefaultGPUInstancerShaderBindings();
            return GPUInstancerConstants.projectSettings.shaderBindings;
        }

        public static ShaderVariantCollection GetShaderVariantCollection()
        {
            if (GPUInstancerConstants.projectSettings.shaderVariantCollection == null)
                GPUInstancerConstants.projectSettings.shaderVariantCollection = GPUInstancerProjectSettings.GetDefaultShaderVariantCollection();
            return GPUInstancerConstants.projectSettings.shaderVariantCollection;
        }
    }
}
