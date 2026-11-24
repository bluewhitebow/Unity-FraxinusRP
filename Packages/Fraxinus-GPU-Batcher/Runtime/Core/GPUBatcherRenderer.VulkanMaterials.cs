#if UNITY_EDITOR_WIN && !UNITY_6000_0_OR_NEWER
using UnityEditor;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        // A newly registered material can otherwise render its first Vulkan BRG request
        // before the editor has populated the DOTS variants for its passes.
        private static void VkPrepareMaterial(Material source)
        {
            if (!VulkanCarrierAvailable || source == null || source.shader == null) return;
            var keyword = source.shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON");
            if (!keyword.isValid) return;
            var variant = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                variant.SetKeyword(keyword, true);
                for (int pass = 0; pass < variant.passCount; ++pass)
                    if (!ShaderUtil.IsPassCompiled(variant, pass))
                        ShaderUtil.CompilePass(variant, pass, true);
            }
            finally { Object.DestroyImmediate(variant); }
        }
    }
}
#endif
