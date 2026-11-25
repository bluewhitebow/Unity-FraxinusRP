using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.UIElements;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(UltraRendererGraph), true)]
    public class UltraRendererGraphEditor : ScriptableRendererGraphEditor
    {
        #region Styles
        
        private static class Styles
        {
            public static readonly GUIContent intermediateTextureMode = EditorGUIUtility.TrTextContent(
                "Intermediate Texture", 
                "Controls when UltraRP renders via an intermediate texture.");
            
            public static readonly GUIContent useNativePassRenderGraphCompiler = EditorGUIUtility.TrTextContent(
                "NativePass RenderGraph Compiler", 
                "Controls when renders's RenderGraph Compiler.");
            
            public static readonly GUIContent depthPrimingModeLabel = EditorGUIUtility.TrTextContent(
                "Depth Priming Mode", 
                "Depth priming uses a depth prepass to reject occluded fragments in opaque rendering. Disabled: Do not use depth priming. Auto: Enable it for supported cameras and platforms, adding a depth prepass when needed.");
            
            public static readonly GUIContent depthPrimingModeInfo = EditorGUIUtility.TrTextContent(
                "Depth priming is disabled on Android, iOS, tvOS and Embedded Linux.");
            
        }
        
        #endregion

        #region Serialized Properties
        
        private SerializedProperty _intermediateTextureMode;
        private SerializedProperty _useNativePassRenderGraphCompiler;
        private SerializedProperty _depthPrimingMode;
        
        #endregion

        #region Lifecycle
        
        protected override void OnEnable()
        {
            base.OnEnable();
            
            _intermediateTextureMode = serializedObject.FindProperty("_intermediateTextureMode");
            _useNativePassRenderGraphCompiler = serializedObject.FindProperty("_useNativePassRenderGraphCompiler");
            _depthPrimingMode = serializedObject.FindProperty("_depthPrimingMode");
        }

        protected override void OnDisable()
        {
            base.OnDisable();
        }
        
        #endregion

        #region UI Creation
        
        public override VisualElement CreateInspectorGUI()
        {
            var root = base.CreateInspectorGUI();

            CreateOpenGraphButton(root);
            CreateCompatibilitySection(root);

            return root;
        }

        private void CreateOpenGraphButton(VisualElement root)
        {
            var button = new Button(OnOpenGraphViewClicked)
            {
                text = "Open Graph View"
            };
            
            button.style.marginTop = 6;
            root.Add(button);
        }

        private void OnOpenGraphViewClicked()
        {
            if (_graph != null)
            {
                ScriptableRendererGraphWindow.Open(_graph);
                return;
            }

            var path = AssetDatabase.GetAssetPath(target);
            var graph = RendererGraphEditorUtils.GetGraphAtPath(path);
            
            if (graph != null)
                ScriptableRendererGraphWindow.Open(graph);
        }

        private void CreateCompatibilitySection(VisualElement root)
        {
            var section = new VisualElement();
            
            CreateSectionHeader(section);
            CreateCompatibilityFields(section);
            
            section.Bind(serializedObject);
            root.Add(section);
        }

        private void CreateSectionHeader(VisualElement parent)
        {
            var header = new Label("Compatibility");
            header.AddToClassList("Header");
            parent.Add(header);
        }

        private void CreateCompatibilityFields(VisualElement parent)
        {
            // Intermediate Texture Mode
            var intermediateField = CreatePropertyField(
                _intermediateTextureMode, 
                Styles.intermediateTextureMode);
            parent.Add(intermediateField);

            // NativePass RenderGraph Compiler
            var nativeCompilerField = CreatePropertyField(
                _useNativePassRenderGraphCompiler, 
                Styles.useNativePassRenderGraphCompiler);
            parent.Add(nativeCompilerField);

            // DepthPrimingMode RenderGraph Compiler
            var container = new VisualElement();
            var depthPrimingModeField = CreatePropertyField(_depthPrimingMode, Styles.depthPrimingModeLabel);
            var helpBoxContainer = new VisualElement { style = { marginTop = 4 } };

            container.Add(depthPrimingModeField);
            container.Add(helpBoxContainer);

            container.TrackPropertyValue(_depthPrimingMode, _ =>
                UpdateDepthPrimingVisibility(_depthPrimingMode, container, helpBoxContainer));

            parent.Add(container);
        }

        private void UpdateDepthPrimingVisibility(
            SerializedProperty depthPrimingMode, 
            VisualElement container, 
            VisualElement helpBoxContainer)
        {

            int depthPrimingValue = depthPrimingMode.intValue;
            if (depthPrimingValue == (int)DepthPrimingMode.Disabled)
            {
                helpBoxContainer.Clear();
                return;
            }

            helpBoxContainer.Clear();

            HelpBox helpBox = new HelpBox(Styles.depthPrimingModeInfo.text, HelpBoxMessageType.Info);

            helpBoxContainer.Add(helpBox);
        }

        private PropertyField CreatePropertyField(SerializedProperty property, GUIContent content)
        {
            var field = new PropertyField(property, content.text)
            {
                tooltip = content.tooltip
            };

            field.RegisterValueChangeCallback(e =>
            {
                serializedObject.ApplyModifiedProperties();
                if (target is UltraRendererGraph graph)
                {
                    graph.SetDirty();
                }
            });

            return field;
        }
        
        #endregion
    }
}
