
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.Rendering;
using FraxinusEngine.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomPropertyDrawer(typeof(UltraRenderPipelineDefaultVolumeProfileSettings))]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    class UltraRenderPipelineDefaultVolumeProfileSettingsPropertyDrawer : DefaultVolumeProfileSettingsPropertyDrawer
    {
        GUIContent defaultVolumeProfileAssetLabel => EditorGUIUtility.TrTextContent("Default Profile",
            "Settings that will be applied project-wide to all Volumes by default when UltraRP is active.");

        protected override GUIContent volumeInfoBoxLabel => EditorGUIUtility.TrTextContent(
            "The values in the Default Volume can be overridden by a Volume Profile assigned to UltraRP asset and Volumes inside scenes.");

        protected override VisualElement CreateAssetFieldUI()
        {
            VisualElement profileLine = new();
            var toggle = new Toggle();
            toggle.AddToClassList(Foldout.toggleUssClassName);
            var checkmark = toggle.Q(className: Toggle.checkmarkUssClassName);
            checkmark.AddToClassList(Foldout.checkmarkUssClassName);
            var field = new ObjectField(defaultVolumeProfileAssetLabel.text)
            {
                tooltip = defaultVolumeProfileAssetLabel.tooltip,
                objectType = typeof(VolumeProfile),
                value = m_VolumeProfileSerializedProperty.objectReferenceValue as VolumeProfile,
            };
            field.AddToClassList("unity-base-field__aligned"); //Align with other BaseField<T>
            field.Q<Label>().RegisterCallback<ClickEvent>(evt => toggle.value ^= true);

            toggle.RegisterValueChangedCallback(evt =>
            {
                m_EditorContainer.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
                m_DefaultVolumeProfileFoldoutExpanded.value = evt.newValue;
            });
            toggle.SetValueWithoutNotify(m_DefaultVolumeProfileFoldoutExpanded.value);
            m_EditorContainer.style.display = m_DefaultVolumeProfileFoldoutExpanded.value ? DisplayStyle.Flex : DisplayStyle.None;

            profileLine.Add(toggle);
            profileLine.Add(field);
            profileLine.style.flexDirection = FlexDirection.Row;
            field.style.flexGrow = 1;

            field.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue)
                    return;
                
                if (RenderPipelineManager.currentPipeline is not UltraRenderPipeline)
                {
                    field.SetValueWithoutNotify(evt.previousValue);
                    Debug.Log("Cannot change Default Volume Profile when UltraRP is not active. Rolling back to previous value.");
                    return;
                }

                if (evt.newValue == null)
                {
                    field.SetValueWithoutNotify(evt.previousValue);
                    Debug.Log("This Volume Profile Asset cannot be null. Rolling back to previous value.");
                    return;
                }

                if (evt.previousValue == null)
                {
                    VolumeProfileUtils.UpdateGlobalDefaultVolumeProfile<UltraRenderPipeline>(evt.newValue as VolumeProfile);
                    m_VolumeProfileSerializedProperty.objectReferenceValue = evt.newValue;
                }
                else
                {
                    bool confirmed = VolumeProfileUtils.UpdateGlobalDefaultVolumeProfileWithConfirmation<UltraRenderPipeline>(evt.newValue as VolumeProfile);
                    m_VolumeProfileSerializedProperty.objectReferenceValue = confirmed ? evt.newValue : evt.previousValue;
                }

                m_VolumeProfileSerializedProperty.serializedObject.ApplyModifiedProperties();
                DestroyDefaultVolumeProfileEditor();
                CreateDefaultVolumeProfileEditor();
            });

            return profileLine;
        }

        public class UltraRenderPipelineDefaultVolumeProfileSettingsContextMenu : DefaultVolumeProfileSettingsContextMenu2<UltraRenderPipelineDefaultVolumeProfileSettings, UltraRenderPipeline>
        {
            protected override string defaultVolumeProfilePath => "Assets/VolumeProfile_Default.asset";
        }
    }
}
