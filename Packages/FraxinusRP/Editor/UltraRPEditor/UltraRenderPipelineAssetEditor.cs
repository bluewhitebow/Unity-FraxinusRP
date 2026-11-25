using UnityEngine;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditorInternal;
using Styles = FraxinusEditor.Rendering.Ultra.UltraRenderPipelineAssetUI.Styles;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(UltraRenderPipelineAsset))]
    [CanEditMultipleObjects]
    sealed class UltraRenderPipelineAssetEditor : Editor
    {
        internal bool largeLabelWidth = true;

        SerializedProperty _rendererDataProp;
        SerializedProperty _defaultRendererProp;

        ReorderableList _rendererGraphList;
        internal ReorderableList rendererGraphList => _rendererGraphList;

        SerializedUltraRenderPipelineAsset _serializedUltraRP;

        void OnEnable()
        {
            _serializedUltraRP = new SerializedUltraRenderPipelineAsset(serializedObject);
            CreateRendererReorderableList();
        }

        public override void OnInspectorGUI()
        {
            _serializedUltraRP.Update();
            UltraRenderPipelineAssetUI.Inspector.Draw(_serializedUltraRP, this);
            _serializedUltraRP.Apply();
        }

        void CreateRendererReorderableList()
        {
            _rendererDataProp = serializedObject.FindProperty("rendererGraphList");
            _defaultRendererProp = serializedObject.FindProperty("defaultRendererIndex");
            _rendererGraphList = new ReorderableList(serializedObject, _rendererDataProp, true, true, true, true)
            {
                drawElementCallback = OnDrawElement,
                drawHeaderCallback = (Rect rect) => EditorGUI.LabelField(rect, Styles.rendererHeaderText),
                onCanRemoveCallback = reorderableList => reorderableList.count > 1,
                onRemoveCallback = OnRemoveElement,
                onReorderCallbackWithDetails = (reorderableList, index, newIndex) => UpdateDefaultRendererValue(index, newIndex) // Need to update the default renderer index
            };
        }

        void OnDrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            rect.y += 2;
            var indexRect = new Rect(rect.x, rect.y, 14, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(indexRect, index.ToString());

            using (var changeCheck = new EditorGUI.ChangeCheckScope())
            {
                var objectFieldWidth = rect.width - 110;
                var objRect = new Rect(rect.x + indexRect.width, rect.y, objectFieldWidth, EditorGUIUtility.singleLineHeight);
                EditorGUI.ObjectField(objRect, _rendererDataProp.GetArrayElementAtIndex(index), GUIContent.none);
                if (changeCheck.changed)
                    EditorUtility.SetDirty(target);
            }

            var isDefaultRenderer = index == _defaultRendererProp.intValue;
            using (new EditorGUI.DisabledScope(isDefaultRenderer))
            {
                var defaultButtonX = rect.width - 51;
                var defaultButtonRect = new Rect(defaultButtonX, rect.y, 86, EditorGUIUtility.singleLineHeight);
                if (GUI.Button(defaultButtonRect, !GUI.enabled ? Styles.rendererDefaultText : Styles.rendererSetDefaultText))
                {
                    _defaultRendererProp.intValue = index;
                    EditorUtility.SetDirty(target);
                }
            }

            // If object selector chose an object, assign it to the correct ScriptableRendererData slot.
            if (Event.current.commandName == "ObjectSelectorUpdated" && EditorGUIUtility.GetObjectPickerControlID() == index)
            {
                _rendererDataProp.GetArrayElementAtIndex(index).objectReferenceValue = EditorGUIUtility.GetObjectPickerObject();
            }
        }

        void OnRemoveElement(ReorderableList reorderableList)
        {
            bool shouldUpdateIndex = false;
            // Checking so that the user is not deleting  the default renderer
            if (reorderableList.index != _defaultRendererProp.intValue)
            {
                // Need to add the undo to the removal of our assets here, for it to work properly.
                Undo.RecordObject(target, $"Deleting renderer at index {reorderableList.index}");

                shouldUpdateIndex = true;
                _rendererDataProp.DeleteArrayElementAtIndex(reorderableList.index);
            }
            else
                EditorUtility.DisplayDialog(Styles.rendererListDefaultMessage.text, Styles.rendererListDefaultMessage.tooltip, "Close");

            if (shouldUpdateIndex)
                UpdateDefaultRendererValue(reorderableList.index);

            EditorUtility.SetDirty(target);
        }

        void UpdateDefaultRendererValue(int prevIndex, int newIndex)
        {
            // If we are moving the index that is the same as the default renderer we need to update that
            if (prevIndex == _defaultRendererProp.intValue)
                _defaultRendererProp.intValue = newIndex;
            // If newIndex is the same as default
            // then we need to know if newIndex is above or below the default index
            else if (newIndex == _defaultRendererProp.intValue)
                _defaultRendererProp.intValue += prevIndex > newIndex ? 1 : -1;
            // If the old index is lower than default renderer and
            // the new index is higher then we need to move the default renderer index one lower
            else if (prevIndex < _defaultRendererProp.intValue && newIndex > _defaultRendererProp.intValue)
                _defaultRendererProp.intValue--;
            else if (newIndex < _defaultRendererProp.intValue && prevIndex > _defaultRendererProp.intValue)
                _defaultRendererProp.intValue++;
        }

        void UpdateDefaultRendererValue(int index)
        {
            if (index < _defaultRendererProp.intValue)
                _defaultRendererProp.intValue--;
        }
    }
}