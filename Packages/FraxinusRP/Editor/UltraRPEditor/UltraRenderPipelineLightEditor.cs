using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

namespace FraxinusEditor.Rendering.Ultra
{
#if UNITY_6000_0_OR_NEWER
    [CustomEditor(typeof(Light))]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
#else
    [CustomEditorForRenderPipeline(typeof(Light), typeof(UltraRenderPipelineAsset))]
#endif
    [CanEditMultipleObjects]
    class UltraRenderPipelineLightEditor : LightEditor
    {
        UltraRenderPipelineSerializedLight serializedLight { get; set; }

        protected override void OnEnable()
        {
            serializedLight = new UltraRenderPipelineSerializedLight(serializedObject, settings);
            Undo.undoRedoPerformed += ReconstructReferenceToAdditionalDataSO;
        }

        internal void ReconstructReferenceToAdditionalDataSO()
        {
            OnDisable();
            OnEnable();
        }

        protected void OnDisable()
        {
            Undo.undoRedoPerformed -= ReconstructReferenceToAdditionalDataSO;
        }

        // IsPreset is an internal API - lets reuse the usable part of this function
        // 93 is a "magic number" and does not represent a combination of other flags here
        internal static bool IsPresetEditor(UnityEditor.Editor editor)
        {
            return (int)((editor.target as Component).gameObject.hideFlags) == 93;
        }

        public override void OnInspectorGUI()
        {
            serializedLight.Update();

            if (IsPresetEditor(this))
                UltraRenderPipelineLightUI.presetInspector.Draw(serializedLight, this);
            else
                UltraRenderPipelineLightUI.inspector.Draw(serializedLight, this);

            serializedLight.Apply();
        }

        protected override void OnSceneGUI()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset))
                return;

            if (!(target is Light light) || light == null)
                return;

            switch (light.type)
            {
                case LightType.Spot:
                    using (new Handles.DrawingScope(Matrix4x4.TRS(light.transform.position, light.transform.rotation, Vector3.one)))
                    {
                        CoreLightEditorUtilities.DrawSpotLightGizmo(light);
                    }
                    break;

                case LightType.Point:
                    using (new Handles.DrawingScope(Matrix4x4.TRS(light.transform.position, Quaternion.identity, Vector3.one)))
                    {
                        CoreLightEditorUtilities.DrawPointLightGizmo(light);
                    }
                    break;

                case LightType.Rectangle:
                    EditorGUI.BeginChangeCheck();
                    using (new Handles.DrawingScope(Matrix4x4.TRS(light.transform.position, light.transform.rotation, Vector3.one)))
                    {
                        CoreLightEditorUtilities.DrawRectangleLightGizmo(light);
                    }
                    if (EditorGUI.EndChangeCheck() && light.TryGetComponent<UltraAdditionalLightData>(out var data))
                    {
                        Undo.RecordObject(data, "Adjust Rectangle Light Size");
                        data.areaSize = light.areaSize;
                        EditorUtility.SetDirty(data);
                    }
                    break;

                case LightType.Disc:
                    using (new Handles.DrawingScope(Matrix4x4.TRS(light.transform.position, light.transform.rotation, Vector3.one)))
                    {
                        CoreLightEditorUtilities.DrawDiscLightGizmo(light);
                    }
                    break;

                case LightType.Directional:
                    using (new Handles.DrawingScope(Matrix4x4.TRS(light.transform.position, light.transform.rotation, Vector3.one)))
                    {
                        CoreLightEditorUtilities.DrawDirectionalLightGizmo(light);
                    }
                    break;

                default:
                    base.OnSceneGUI();
                    break;
            }
        }
    }

}
