using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using FraxinusEngine.Rendering.Ultra;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(TextureBlitNode))]
    public class TextureBlitNodeView : ScriptableRenderPassNodeView
    {
        private EnumField _sourceModeField;
        private ObjectField _sourceTextureField;
        private ObjectField _blitMaterialField;
        
        private TextureBlitNode _blitNode => nodeTarget as TextureBlitNode;
        
        protected override bool _showDefaultInspector => false;
        
        public override void Enable(bool fromInspector)
        {
            base.Enable(fromInspector);
            
            if (!fromInspector)
            {
                BuildCustomInspector();
            }
        }
        
        private void BuildCustomInspector()
        {
            // Source Settings Header
            var sourceHeader = new Label("Source Settings");
            sourceHeader.AddToClassList("PropertyEditorTitle");
            controlsContainer.Add(sourceHeader);
            
            // Source Mode Field
            _sourceModeField = new EnumField("Source Mode", GetSourceMode())
            {
                tooltip = "纹理来源模式：Property = 从属性指定，InputPort = 从输入端口获取"
            };
            _sourceModeField.RegisterValueChangedCallback(OnSourceModeChanged);
            controlsContainer.Add(_sourceModeField);
            
            // Source Texture Field (conditionally visible)
            _sourceTextureField = new ObjectField("Source Texture")
            {
                objectType = typeof(Texture2D),
                value = GetSourceTexture(),
                tooltip = "要 Blit 到目标的源纹理（仅在 Property 模式下使用）"
            };
            _sourceTextureField.RegisterValueChangedCallback(OnSourceTextureChanged);
            controlsContainer.Add(_sourceTextureField);
            
            // Material Settings Header
            var materialHeader = new Label("Material Settings");
            materialHeader.AddToClassList("PropertyEditorTitle");
            controlsContainer.Add(materialHeader);
            
            // Blit Material Field
            _blitMaterialField = new ObjectField("Blit Material")
            {
                objectType = typeof(Material),
                value = GetBlitMaterial(),
                tooltip = "自定义 Blit 材质（可选，留空则使用默认 Blit 材质）"
            };
            _blitMaterialField.RegisterValueChangedCallback(OnBlitMaterialChanged);
            controlsContainer.Add(_blitMaterialField);
            
            // Update visibility based on current mode
            UpdateSourceTextureVisibility();
        }
        
        private void OnSourceModeChanged(ChangeEvent<System.Enum> evt)
        {
            owner.RegisterCompleteObjectUndo("Change Source Mode");
            SetSourceMode((TextureSourceMode)evt.newValue);
            UpdateSourceTextureVisibility();
            RefreshPorts();
        }
        
        private void OnSourceTextureChanged(ChangeEvent<Object> evt)
        {
            owner.RegisterCompleteObjectUndo("Change Source Texture");
            SetSourceTexture(evt.newValue as Texture2D);
        }
        
        private void OnBlitMaterialChanged(ChangeEvent<Object> evt)
        {
            owner.RegisterCompleteObjectUndo("Change Blit Material");
            SetBlitMaterial(evt.newValue as Material);
        }
        
        private void UpdateSourceTextureVisibility()
        {
            var mode = GetSourceMode();
            _sourceTextureField.style.display = mode == TextureSourceMode.Property 
                ? DisplayStyle.Flex 
                : DisplayStyle.None;
        }
        
        #region Reflection Helpers
        
        private TextureSourceMode GetSourceMode()
        {
            var field = typeof(TextureBlitNode).GetField("_sourceMode", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (TextureSourceMode)field?.GetValue(_blitNode);
        }
        
        private void SetSourceMode(TextureSourceMode mode)
        {
            var field = typeof(TextureBlitNode).GetField("_sourceMode", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_blitNode, mode);
        }
        
        private Texture2D GetSourceTexture()
        {
            var field = typeof(TextureBlitNode).GetField("_sourceTexture", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(_blitNode) as Texture2D;
        }
        
        private void SetSourceTexture(Texture2D texture)
        {
            var field = typeof(TextureBlitNode).GetField("_sourceTexture", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_blitNode, texture);
        }
        
        private Material GetBlitMaterial()
        {
            var field = typeof(TextureBlitNode).GetField("_blitMaterial", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(_blitNode) as Material;
        }
        
        private void SetBlitMaterial(Material material)
        {
            var field = typeof(TextureBlitNode).GetField("_blitMaterial", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(_blitNode, material);
        }
        
        #endregion
    }
}
