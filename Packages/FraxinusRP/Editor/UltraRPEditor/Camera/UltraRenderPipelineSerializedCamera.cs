using FraxinusEngine.Rendering.Ultra;
using System;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    class UltraRenderPipelineSerializedCamera : ISerializedCamera
    {
        public SerializedObject serializedObject { get; }

        public SerializedObject serializedAdditionalDataObject { get; }

        public UnityEditor.CameraEditor.Settings baseCameraSettings { get; }

        // This one is internal in UnityEditor for whatever reason...
        public SerializedProperty projectionMatrixMode { get; }

        // Common properties
        public SerializedProperty dithering { get; }
        public SerializedProperty stopNaNs { get; }
        public SerializedProperty allowDynamicResolution { get; }
        public SerializedProperty volumeLayerMask { get; }
        public SerializedProperty clearDepth { get; }
        public SerializedProperty antialiasing { get; }

        // UltraRP Specific properties
        public SerializedProperty renderShadows { get; }
        public SerializedProperty renderer { get; }
        public SerializedProperty cameraType { get; }
        public SerializedProperty cameras { get; set; }
        public SerializedProperty volumeTrigger { get; }
        public SerializedProperty volumeFrameworkUpdateMode { get; }
        public SerializedProperty renderPostProcessing { get; }
        public SerializedProperty antialiasingQuality { get; }
        public SerializedProperty taaQuality { get; }
        public SerializedProperty taaFrameInfluence { get; }
        public SerializedProperty taaJitterScale { get; }
        public SerializedProperty taaMipBias { get; }
        public SerializedProperty taaVarianceClampScale { get; }
        public SerializedProperty taaContrastAdaptiveSharpening { get; }
        public SerializedProperty allowHDROutput { get; }

        public (Camera camera, UltraRenderPipelineSerializedCamera serializedCamera) this[int index]
        {
            get
            {
                if (index < 0 || index >= numCameras)
                    throw new ArgumentOutOfRangeException($"{index} is out of bounds [0 - {numCameras}]");

                // Return the camera on that index
                return (cameras.GetArrayElementAtIndex(index).objectReferenceValue as Camera, cameraSerializedObjects[index]);
            }
        }

        public int numCameras => cameras?.arraySize ?? 0;

        public UltraRenderPipelineSerializedCamera[] cameraSerializedObjects { get; set; }

        public UltraAdditionalCameraData[] camerasAdditionalData { get; }

        public UltraRenderPipelineSerializedCamera(SerializedObject serializedObject, CameraEditor.Settings settings = null)
        {
            this.serializedObject = serializedObject;
            projectionMatrixMode = serializedObject.FindProperty("m_projectionMatrixMode");
            allowDynamicResolution = serializedObject.FindProperty("m_AllowDynamicResolution");

            if (settings == null)
            {
                baseCameraSettings = new CameraEditor.Settings(serializedObject);
                baseCameraSettings.OnEnable();
            }
            else
                baseCameraSettings = settings;

            camerasAdditionalData = CoreEditorUtils.GetAdditionalData<UltraAdditionalCameraData>(serializedObject.targetObjects);
            serializedAdditionalDataObject = new SerializedObject(camerasAdditionalData);

            // Common properties
            stopNaNs = serializedAdditionalDataObject.FindProperty("_stopNaN");
            dithering = serializedAdditionalDataObject.FindProperty("_dithering");
            antialiasing = serializedAdditionalDataObject.FindProperty("_antialiasing");
            volumeLayerMask = serializedAdditionalDataObject.FindProperty("_volumeLayerMask");
            clearDepth = serializedAdditionalDataObject.FindProperty("_clearDepth");

            // Ultra Specific properties
            renderShadows = serializedAdditionalDataObject.FindProperty("_renderShadows");
            renderer = serializedAdditionalDataObject.FindProperty("_rendererIndex");
            volumeLayerMask = serializedAdditionalDataObject.FindProperty("_volumeLayerMask");
            volumeTrigger = serializedAdditionalDataObject.FindProperty("_volumeTrigger");
            volumeFrameworkUpdateMode = serializedAdditionalDataObject.FindProperty("_volumeFrameworkUpdateModeOption");
            renderPostProcessing = serializedAdditionalDataObject.FindProperty("_renderPostProcessing");
            antialiasingQuality = serializedAdditionalDataObject.FindProperty("_antialiasingQuality");
            cameraType = serializedAdditionalDataObject.FindProperty("_cameraType");

            var taaSettings = serializedAdditionalDataObject.FindProperty(nameof(UltraAdditionalCameraData.taaSettings));
            taaQuality = taaSettings.FindPropertyRelative(nameof(TAASettings.quality));
            taaFrameInfluence = taaSettings.FindPropertyRelative(nameof(TAASettings.frameInfluence));
            taaJitterScale = taaSettings.FindPropertyRelative(nameof(TAASettings.jitterScale));
            taaMipBias = taaSettings.FindPropertyRelative(nameof(TAASettings.mipBias));
            taaVarianceClampScale = taaSettings.FindPropertyRelative(nameof(TAASettings.varianceClampScale));
            taaContrastAdaptiveSharpening = taaSettings.FindPropertyRelative(nameof(TAASettings.contrastAdaptiveSharpening));

            allowHDROutput = serializedAdditionalDataObject.FindProperty("_allowHDROutput");
        }

        public void Apply()
        {
            ApplyInternal();

            if (cameraSerializedObjects == null || cameraSerializedObjects.Length != numCameras)
                Refresh();

            for (int i = 0; i < numCameras; ++i)
                cameraSerializedObjects[i]?.ApplyInternal();
        }

        public void Refresh()
        {
            var o = new PropertyFetcher<UltraAdditionalCameraData>(serializedAdditionalDataObject);
            cameras = o.Find("_cameras");

            cameraSerializedObjects = new UltraRenderPipelineSerializedCamera[numCameras];
            for (int i = 0; i < numCameras; ++i)
            {
                Camera cam = cameras.GetArrayElementAtIndex(i).objectReferenceValue as Camera;
                if (cam != null)
                    cameraSerializedObjects[i] = new UltraRenderPipelineSerializedCamera(new SerializedObject(cam));
            }
        }

        public void Update()
        {
            UpdateInternal();

            if (cameraSerializedObjects == null || cameraSerializedObjects.Length != numCameras)
                Refresh();

            for (int i = 0; i < numCameras; ++i)
                cameraSerializedObjects[i]?.UpdateInternal();
        }

        private void ApplyInternal()
        {
            baseCameraSettings.ApplyModifiedProperties();
            serializedObject.ApplyModifiedProperties();
            serializedAdditionalDataObject.ApplyModifiedProperties();
        }

        private void UpdateInternal()
        {
            baseCameraSettings.Update();
            serializedObject.Update();
            serializedAdditionalDataObject.Update();
        }
    }

}
