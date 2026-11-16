
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEditorInternal;

using System;

namespace FraxinusEditor.Rendering.Ultra
{
    using Styles = UltraRenderPipelineCameraUI.Styles;

#if UNITY_6000_0_OR_NEWER
    [CustomEditor(typeof(Camera))]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
#else
    [CustomEditorForRenderPipeline(typeof(Camera), typeof(UltraRenderPipelineAsset))]
#endif
    [CanEditMultipleObjects]
    partial class UltraRenderPipelineCameraEditor : Editor
    {
        ReorderableList _layerList;

        CameraEditor.Settings _settings;
        protected CameraEditor.Settings settings => _settings ??= new CameraEditor.Settings(serializedObject);

        public Camera camera => target as Camera;
        static Camera selectedCameraInStack;

        List<Camera> validCameras = new List<Camera>();
        List<Camera> _typeErrorCameras = new List<Camera>();
        List<Camera> _notSupportedOverlayCameras = new List<Camera>();
        List<Camera> _incompatibleCameras = new List<Camera>();
        List<(Camera, UltraRenderPipelineSerializedCamera)> _outputWarningCameras = new();

        UltraRenderPipelineSerializedCamera _serializedCamera;

        public void OnEnable()
        {
            settings.OnEnable();
            selectedCameraInStack = null;
            _serializedCamera = new UltraRenderPipelineSerializedCamera(serializedObject);

            validCameras.Clear();
            _typeErrorCameras.Clear();
            _notSupportedOverlayCameras.Clear();
            _incompatibleCameras.Clear();
            _outputWarningCameras.Clear();

            UpdateCameras();

            Undo.undoRedoPerformed += ReconstructReferenceToAdditionalDataSO;
        }

        public void OnDisable()
        {
            Undo.undoRedoPerformed -= ReconstructReferenceToAdditionalDataSO;
        }

        void UpdateCameras()
        {
            _serializedCamera.Refresh();

            _layerList = new ReorderableList(_serializedCamera.serializedObject, _serializedCamera.cameras, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, Styles.cameras),
                drawElementCallback = DrawElementCallback,
                onSelectCallback = SelectElement,
                onRemoveCallback = RemoveCamera,
                onCanRemoveCallback = CanRemoveCamera,
                onAddDropdownCallback = AddCameraToCameraList
            };

        }

        void DrawElementCallback(Rect rect, int index, bool isActive, bool isFocused)
        {
            rect.height = EditorGUIUtility.singleLineHeight;
            rect.y += 1;

            (Camera camera, UltraRenderPipelineSerializedCamera serializedCamera) overlayCamera = _serializedCamera[index];
            Camera cam = overlayCamera.camera;

            if (cam != null)
            {
                var baseAdditionalData = camera.GetUltraAdditionalCameraData();
                bool outputWarning = false;

                // Checking if the Base Camera and the overlay cameras are of the same type.
                // If not, we report an error.
                var overlayAdditionalData = cam.GetUltraAdditionalCameraData();
                var type = overlayAdditionalData.renderType;

                GUIContent errorContent = EditorGUIUtility.TrTextContent(type.GetName()); ;

                var renderer = overlayAdditionalData.scriptableRenderer;

                if (baseAdditionalData.scriptableRenderer.GetType() != renderer.GetType())
                {
                    if (!_incompatibleCameras.Contains(cam))
                        _incompatibleCameras.Add(cam);

                    errorContent = EditorGUIUtility.TrTextContent("",
                        $"Only cameras with compatible renderer types can be stacked. " +
                        $"The camera: {cam.name} are using the renderer {renderer.GetType().Name}, " +
                        $"but the base camera: {camera.name} are using {baseAdditionalData.scriptableRenderer.GetType().Name}. Will skip rendering", CoreEditorStyles.iconFail);
                }
                else if (_incompatibleCameras.Contains(cam))
                    _incompatibleCameras.Remove(cam);

                // Check if the renderer on the camera we are checking does indeed support overlay camera
                // This can fail due to changing the renderer in the UI to a renderer that does not support overlay cameras
                // The UI will not stop you from changing the renderer sadly so this will have to tell the user that the
                // entry in the stack now is invalid.
                else if (!renderer.SupportsCameraStackingType(CameraRenderType.Overlay))
                {
                    if (!_notSupportedOverlayCameras.Contains(cam))
                        _notSupportedOverlayCameras.Add(cam);

                    errorContent = EditorGUIUtility.TrTextContent("",
                        $"The camera: {cam.name} is using a renderer of type {renderer.GetType().Name} which does not support Overlay cameras in it's current state.", CoreEditorStyles.iconFail);
                }
                else if (_notSupportedOverlayCameras.Contains(cam))
                    _notSupportedOverlayCameras.Remove(cam);

                else if (type != CameraRenderType.Overlay)
                {
                    if (!_typeErrorCameras.Contains(cam))
                        _typeErrorCameras.Add(cam);
  
                    errorContent = EditorGUIUtility.TrTextContent(type.GetName(), $"Stack can only contain Overlay cameras. The camera: {cam.name} " +
                                                                                    $"has a type {type} that is not supported. Will skip rendering.",
                        CoreEditorStyles.iconFail);
                }
                else if (_typeErrorCameras.Contains(cam))
                    _typeErrorCameras.Remove(cam);

                if (IsStackCameraOutputDirty(cam, overlayCamera.serializedCamera))
                {
                    outputWarning = true;
                    if (!_outputWarningCameras.Exists(c => c.Item1 == cam))
                        _outputWarningCameras.Add((cam, overlayCamera.serializedCamera));
                }
                else
                    _outputWarningCameras.RemoveAll(c => c.Item1 == cam);


                GUIContent nameContent =
                    outputWarning ?
                    EditorGUIUtility.TrTextContent(cam.name, "Output properties do not match base camera", CoreEditorStyles.iconWarn) :
                    EditorGUIUtility.TrTextContent(cam.name);

                EditorGUI.BeginProperty(rect, GUIContent.none, _serializedCamera.cameras.GetArrayElementAtIndex(index));
                var labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth -= 20f;

                using (var iconSizeScope = new EditorGUIUtility.IconSizeScope(new Vector2(rect.height, rect.height)))
                {
                    EditorGUI.LabelField(rect, nameContent, errorContent);
                }

                // Printing if Post Processing is on or not.
                //var isPostActive = cam.GetUltraAdditionalCameraData().renderPostProcessing;
                //if (isPostActive)
                //{
                //    Rect selectRect = new Rect(rect.width - 20, rect.y, 50, EditorGUIUtility.singleLineHeight);

                //    EditorGUI.LabelField(selectRect, "PP");
                //}
                EditorGUI.EndProperty();

                EditorGUIUtility.labelWidth = labelWidth;
            }
            else
            {
                camera.GetUltraAdditionalCameraData().UpdateCameraStack();

                // Need to clean out the errorCamera list here.
                _typeErrorCameras.Clear();
                _outputWarningCameras.Clear();
            }
        }

        bool CanRemoveCamera(ReorderableList list) => _serializedCamera.numCameras > 0;

        void RemoveCamera(ReorderableList list)
        {
            // As multi selection is disabled, selectedIndices will only return 1 element, remove that element from the list
            if (list.selectedIndices.Any())
                _serializedCamera.cameras.DeleteArrayElementAtIndex(list.selectedIndices.First());
            else
                // Nothing selected, remove the last item on the list
                ReorderableList.defaultBehaviours.DoRemoveButton(list);

            // Force update the list as removed camera could been there
            _typeErrorCameras.Clear();
            _notSupportedOverlayCameras.Clear();
            _incompatibleCameras.Clear();
            _outputWarningCameras.Clear();
        }

        void SelectElement(ReorderableList list)
        {
            var element = _serializedCamera.cameras.GetArrayElementAtIndex(list.index);
            selectedCameraInStack = element.objectReferenceValue as Camera;
            if (Event.current.clickCount == 2)
                Selection.activeObject = selectedCameraInStack;

            EditorGUIUtility.PingObject(selectedCameraInStack);
        }

        public override void OnInspectorGUI()
        {
            var rpAsset = UltraRenderPipeline.asset;
            if (rpAsset == null)
            {
                base.OnInspectorGUI();
                return;
            }

            _serializedCamera.Update();

            if (IsPresetEditor(this))
                UltraRenderPipelineCameraUI.presetInspector.Draw(_serializedCamera, this);
            else
                UltraRenderPipelineCameraUI.inspector.Draw(_serializedCamera, this);
   

            _serializedCamera.Apply();
        }

        private void UpdateStackCameraToOverlay()
        {
            var additionalCameraData = selectedCameraInStack.GetUltraAdditionalCameraData();
            if (additionalCameraData == null)
                return;

            if (additionalCameraData.renderType == CameraRenderType.Base)
            {
                Undo.RecordObject(additionalCameraData, Styles.inspectorOverlayCameraText);
                additionalCameraData.renderType = CameraRenderType.Overlay;
                EditorUtility.SetDirty(additionalCameraData);
            }
        }

        private void UpdateStackCameraOutput(Camera cam, UltraRenderPipelineSerializedCamera serializedCamera)
        {
            if ((CameraRenderType)serializedCamera.cameraType.intValue == CameraRenderType.Base)
                return;

            serializedCamera.Update();
            Undo.RecordObject(camera, Styles.inspectorOverlayCameraText);

            bool isChanged = false;

            // Force same render texture
            RenderTexture targetTexture = settings.targetTexture.objectReferenceValue as RenderTexture;
            if (cam.targetTexture != targetTexture)
            {
                cam.targetTexture = targetTexture;
                isChanged = true;
            }

            // Force same hdr
            bool allowHDR = settings.HDR.boolValue;
            if (cam.allowHDR != allowHDR)
            {
                cam.allowHDR = allowHDR;
                isChanged = true;
            }

            // Force same viewport rect
            Rect rect = settings.normalizedViewPortRect.rectValue;
            if (cam.rect != rect)
            {
                cam.rect = settings.normalizedViewPortRect.rectValue;
                isChanged = true;
            }

            // Force same dynamic resolution
            bool allowDynamicResolution = settings.allowDynamicResolution.boolValue;
            if (serializedCamera.allowDynamicResolution.boolValue != allowDynamicResolution)
            {
                cam.allowDynamicResolution = allowDynamicResolution;
                isChanged = true;
            }

            // Force same target display
            int targetDisplay = settings.targetDisplay.intValue;
            if (cam.targetDisplay != targetDisplay)
            {
                cam.targetDisplay = targetDisplay;
                isChanged = true;
            }

            if (isChanged)
            {
                EditorUtility.SetDirty(cam);
                serializedCamera.Apply();
            }
        }

        private bool IsStackCameraOutputDirty(Camera cam, UltraRenderPipelineSerializedCamera serializedCamera)
        {
            serializedCamera.Update();

            // Force same render texture
            RenderTexture targetTexture = settings.targetTexture.objectReferenceValue as RenderTexture;
            if (cam.targetTexture != targetTexture)
                return true;

            // Force same hdr
            bool allowHDR = settings.HDR.boolValue;
            if (cam.allowHDR != allowHDR)
                return true;

            // Force same viewport rect
            Rect rect = settings.normalizedViewPortRect.rectValue;
            if (cam.rect != rect)
                return true;

            // Force same dynamic resolution
            bool allowDynamicResolution = settings.allowDynamicResolution.boolValue;
            if (serializedCamera.allowDynamicResolution.boolValue != allowDynamicResolution)
                return true;

            // Force same target display
            int targetDisplay = settings.targetDisplay.intValue;
            if (cam.targetDisplay != targetDisplay)
                return true;


            return false;
        }


        // Modified version of StageHandle.FindComponentsOfType<T>()
        // This version more closely represents unity object referencing restrictions.
        // I added these restrictions:
        // - Can not reference scene object outside scene
        // - Can not reference cross scenes
        // - Can reference child objects if it is prefab
        Camera[] FindCamerasToReference(GameObject gameObject)
        {
            var scene = gameObject.scene;

            var inScene = !EditorUtility.IsPersistent(camera) || scene.IsValid();
            var inPreviewScene = EditorSceneManager.IsPreviewScene(scene) && scene.IsValid();
            var inCurrentScene = !EditorUtility.IsPersistent(camera) && scene.IsValid();

            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            List<Camera> result = new List<Camera>();
            if (!inScene)
            {
                foreach (var camera in cameras)
                {
                    if (camera.transform.IsChildOf(gameObject.transform))
                        result.Add(camera);
                }
            }
            else if (inPreviewScene)
            {
                foreach (var camera in cameras)
                {
                    if (camera.gameObject.scene == scene)
                        result.Add(camera);
                }
            }
            else if (inCurrentScene)
            {
                foreach (var camera in cameras)
                {
                    if (!EditorUtility.IsPersistent(camera) && !EditorSceneManager.IsPreviewScene(camera.gameObject.scene) && camera.gameObject.scene == scene)
                        result.Add(camera);
                }
            }

            return result.ToArray();
        }

        void AddCameraToCameraList(Rect rect, ReorderableList list)
        {
            // Need to do clear the list here otherwise the menu just fills up with more and more entries
            validCameras.Clear();
            // Need to get the base renderer here first
            var renderer = camera.GetUltraAdditionalCameraData().scriptableRenderer.GetType();
            var allCameras = FindCamerasToReference(camera.gameObject);
            foreach (var camera in allCameras)
            {
                var component = camera.GetUltraAdditionalCameraData();
                if (component != null)
                {
                    if (component.renderType == CameraRenderType.Overlay &&
                        component.scriptableRenderer.GetType() == renderer)
                    {
                        validCameras.Add(camera);
                    }
                }
            }

            var names = new GUIContent[validCameras.Count];
            for (int i = 0; i < validCameras.Count; ++i)
                names[i] = new GUIContent((i + 1) + " " + validCameras[i].name);

            if (!validCameras.Any())
            {
                names = new GUIContent[1];
                names[0] = new GUIContent("No Overlay Cameras exist.");
            }
            EditorUtility.DisplayCustomMenu(rect, names, -1, AddCameraToCameraListMenuSelected, null);
        }

        void AddCameraToCameraListMenuSelected(object userData, string[] options, int selected)
        {
            if (!validCameras.Any())
                return;

            _serializedCamera.cameras.InsertArrayElementAtIndex(_serializedCamera.numCameras);
            _serializedCamera.cameras.GetArrayElementAtIndex(_serializedCamera.numCameras - 1).objectReferenceValue = validCameras[selected];
            _serializedCamera.serializedAdditionalDataObject.ApplyModifiedProperties();

            _serializedCamera.Refresh();

            (Camera camera, UltraRenderPipelineSerializedCamera serializedCamera) overlayCamera = _serializedCamera[_serializedCamera.numCameras - 1];
            UpdateStackCameraOutput(overlayCamera.camera, overlayCamera.serializedCamera);
        }


        internal static bool IsPresetEditor(Editor editor) 
        {
            return (int)((editor.target as Component).gameObject.hideFlags) == 93;
        }

        internal void DrawStackSettings()
        {
            if (_serializedCamera.cameras.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("Cannot multi edit stack of multiple cameras.", MessageType.Info);
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            bool cameraStackingAvailable = _serializedCamera
                .camerasAdditionalData
                .All(c => c.scriptableRenderer?.SupportsCameraStackingType(CameraRenderType.Base) ?? false);

            if (!cameraStackingAvailable)
            {
                EditorGUILayout.HelpBox("The renderer used by this camera doesn't support camera stacking. Only Base camera will render.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space();

            _layerList.DoLayoutList();
            _serializedCamera.Apply();

            EditorGUI.indentLevel--;

            bool oldRichTextSupport = EditorStyles.helpBox.richText;
            EditorStyles.helpBox.richText = true;

            if (selectedCameraInStack != null)
            {
                if (_incompatibleCameras.Any())
                {
                    if (_incompatibleCameras.Contains(selectedCameraInStack))
                    {
                        var message = "This camera does not use the same type of renderer as the Base camera.";
                        EditorGUILayout.HelpBox(message, MessageType.Error);
                    }
                }

                if (_notSupportedOverlayCameras.Any())
                {
                    if (_notSupportedOverlayCameras.Contains(selectedCameraInStack))
                    {
                        var message = "This camera uses a renderer which does not support Overlays in it's current state.";
                        EditorGUILayout.HelpBox(message, MessageType.Error);
                    }
                }

                if (_typeErrorCameras.Any())
                {
                    if (_typeErrorCameras.Contains(selectedCameraInStack))
                    {
                        var message = "The type of this Camera must be Overlay render type.";
                        CoreEditorUtils.DrawFixMeBox(message, MessageType.Error, UpdateStackCameraToOverlay);
                    }
                }

                if (_outputWarningCameras.Any())
                {
                    var camIndex = _outputWarningCameras.FindIndex(c => c.Item1 == selectedCameraInStack);
                    if (camIndex != -1)
                    {
                        var message = "This camera's output properties do not match the Base camera's output properties.";
                        if ((CameraRenderType)_outputWarningCameras[camIndex].Item2.cameraType.intValue == CameraRenderType.Base)
                        {
                            EditorGUILayout.HelpBox(message, MessageType.Warning);
                        }
                        else
                        {
                            CoreEditorUtils.DrawFixMeBox(message, MessageType.Warning,
                                () => UpdateStackCameraOutput(_outputWarningCameras[camIndex].Item1, _outputWarningCameras[camIndex].Item2));
                        }
                    }
                }
            }

            EditorStyles.helpBox.richText = oldRichTextSupport;
            EditorGUI.indentLevel++;

            EditorGUILayout.Space();
        }


        void ReconstructReferenceToAdditionalDataSO()
        {
            OnDisable();
            OnEnable();
        }
    }
}
