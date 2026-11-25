using System;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine.UIElements;
using Button = UnityEngine.UIElements.Button;

namespace UnityEngine.Rendering
{
    #region Probe Volume Scene Overlay

    [Overlay(typeof(SceneView), k_OverlayID)]
    [Icon("Packages/com.unity.render-pipelines.core/Editor/Resources/Gizmos/ProbeVolume.png")]
    internal class APVProbeVolumeOverlay : Overlay, ITransientOverlay
    {
        const string k_OverlayID = "APV Overlay";

        VisualElement m_Disabled = null;
        Toggle[] m_LayerToggles = null;
        Label[] m_BrickLabels = null;
        GroupBox layerMasksGroupBox = null;
        GroupBox probeDistanceGroupBox = null;
        GroupBox probeSamplingGroupBox = null;
        TextElement vertexSamplingWarning = null;

        int maxSubdiv;
        float minDistance;

        APVProbeReferenceVolume apv => APVProbeReferenceVolume.instance;

        public bool visible => IsVisible();

        (int maxSubdiv, float minDistance) GetSettings()
        {
            if (apv.probeVolumeDebug.realtimeSubdivision)
            {
                var probeVolume = GameObject.FindFirstObjectByType<ProbeVolume>();
                if (probeVolume != null && probeVolume.isActiveAndEnabled)
                {
                    var profile = ProbeVolumeBakingSet.GetBakingSetForScene(probeVolume.gameObject.scene);
                    if (profile != null)
                        return (profile.maxSubdivision, profile.minDistanceBetweenProbes);
                }
            }

            return (apv.GetMaxSubdivision(), apv.MinDistanceBetweenProbes());
        }

        bool IsVisible()
        {
            if (ProbeReferenceVolumeProvider.instance != apv || !apv.enabledBySRP)
                return false;

            // Include some state tracking here because it's the only function called at each repaint
            var debug = apv.probeVolumeDebug;
            var bakingSet = apv.currentBakingSet;

            bool debugLayers = debug.drawProbes && debug.probeShading == DebugProbeShadingMode.RenderingLayerMasks && bakingSet != null;
            if (!debug.drawBricks && !debug.drawProbeSamplingDebug && !debugLayers)
                return false;

            EnableGroupBox(layerMasksGroupBox, debugLayers);
            EnableGroupBox(probeDistanceGroupBox, debug.drawBricks);
            EnableGroupBox(probeSamplingGroupBox, debug.drawProbeSamplingDebug);
            EnableTextArea(vertexSamplingWarning, apv.vertexSampling);

            if (debugLayers && m_LayerToggles != null)
            {
                if (bakingSet.bakedMaskCount != 1)
                {
                    debug.visibleLayers = 0;
                    m_Disabled.style.display = DisplayStyle.None;
                    var configuredMasks = bakingSet.renderingLayerMasks;
                    for (int i = 0; i < APVDefinitions.probeMaxRegionCount; i++)
                    {
                        int visibility = m_LayerToggles[i].value ? 1 << i : 0;
                        if (configuredMasks == null || i >= configuredMasks.Length)
                        {
                            debug.visibleLayers &= (byte)~visibility;
                            m_LayerToggles[i].parent.style.display = DisplayStyle.None;
                        }
                        else
                        {
                            debug.visibleLayers |= (byte)visibility;
                            m_LayerToggles[i].parent.style.display = DisplayStyle.Flex;
                            m_LayerToggles[i].label = configuredMasks[i].name;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < APVDefinitions.probeMaxRegionCount; i++)
                        m_LayerToggles[i].parent.style.display = DisplayStyle.None;
                    m_Disabled.style.display = DisplayStyle.Flex;
                }
            }
            if (debug.drawBricks && m_BrickLabels != null)
            {
                (int max, float min) = GetSettings();
                if (maxSubdiv != max)
                {
                    maxSubdiv = max;
                    for (int i = 0; i < m_BrickLabels.Length; i++)
                        m_BrickLabels[i].parent.EnableInClassList("unity-pbr-validation-hidden", i >= maxSubdiv);
                }
                if (minDistance != min)
                {
                    minDistance = min;
                    for (int i = 0; i < m_BrickLabels.Length; i++)
                        m_BrickLabels[i].text = (minDistance * APVProbeReferenceVolume.CellSize(i)) + " meters";
                }
            }

            return true;
        }

        void EnableGroupBox(GroupBox groupBox, bool b)
        {
            if (groupBox == null)
                return;

            groupBox.style.display = b ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void EnableTextArea(TextElement text, bool b)
        {
            if (text == null)
                return;

            text.style.display = b ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public override void OnCreated()
        {
            if (containerWindow is not SceneView)
                throw new Exception("APV Overlay is only valid in the Scene View");
        }

        VisualElement CreateColorSwatch(Color color)
        {
            var swatchContainer = new VisualElement();
            swatchContainer.AddToClassList("unity-base-field__label");
            swatchContainer.AddToClassList("unity-pbr-validation-color-swatch");

            var colorContent = new VisualElement() { name = "color-content" };
            colorContent.style.backgroundColor = new StyleColor(color);
            swatchContainer.Add(colorContent);

            return swatchContainer;
        }

        public override VisualElement CreatePanelContent()
        {
            displayName = "Adaptive Probe Volumes";
            maxSubdiv = 0;
            minDistance = -1;

            var root = new VisualElement();

            // Layer mask
            layerMasksGroupBox = new GroupBox();
            layerMasksGroupBox.text = "Rendering Layer Masks";

            m_LayerToggles = new Toggle[APVDefinitions.probeMaxRegionCount];
            for (int i = 0; i < APVDefinitions.probeMaxRegionCount; i++)
            {
                var row = new VisualElement() { style = { flexDirection = FlexDirection.Row } };
                layerMasksGroupBox.Add(row);

                row.Add(CreateColorSwatch(APVDefinitions.layerMaskColors[i]));

                m_LayerToggles[i] = new Toggle("Mask " + i) { name = "color-label" };
                m_LayerToggles[i].AddToClassList("unity-base-field__label");
                m_LayerToggles[i].value = true;
                row.Add(m_LayerToggles[i]);
            }
            {
                m_Disabled = new VisualElement() { style = { flexDirection = FlexDirection.Row } };
                layerMasksGroupBox.Add(m_Disabled);

                m_Disabled.Add(CreateColorSwatch(APVDefinitions.debugEmptyColor));

                var label = new Label("Disabled") { name = "color-label" };
                label.AddToClassList("unity-base-field__label");
                m_Disabled.Add(label);
                m_Disabled.style.display = DisplayStyle.None;
            }

            // Distance Between Probes
            probeDistanceGroupBox = new GroupBox();
            probeDistanceGroupBox.text = "Distance Between probes";

            m_BrickLabels = new Label[6];
            for (int i = 0; i < m_BrickLabels.Length; i++)
            {
                var row = new VisualElement() { style = { flexDirection = FlexDirection.Row } };
                probeDistanceGroupBox.Add(row);

                row.Add(CreateColorSwatch(apv.subdivisionDebugColors[i]));

                m_BrickLabels[i] = new Label() { name = "color-label" };
                m_BrickLabels[i].AddToClassList("unity-base-field__label");
                row.Add(m_BrickLabels[i]);
            }

            // Probe Sampling Select Pixel
            probeSamplingGroupBox = new GroupBox();
            probeSamplingGroupBox.text = "Probe Sampling";

            var probeSampling_row = new VisualElement() { style = { flexDirection = FlexDirection.Column } };
            probeSamplingGroupBox.Add(probeSampling_row);

            var selectPixelButton = new Button();
            selectPixelButton.clickable.activators.Clear();
            selectPixelButton.text = "Select Pixel";
            selectPixelButton.tooltip = "Use this button or Ctrl+Click on the viewport to select which pixel to debug for APV sampling";

            Color buttonDefaultColor = new Color(0.250f, 0.250f, 0.250f);
            Color buttonHoverColor = new Color(0.404f, 0.404f, 0.404f);
            Color buttonPressedColor = new Color(0.133f, 0.133f, 0.133f);
            selectPixelButton.style.backgroundColor = buttonDefaultColor;
            selectPixelButton.RegisterCallback<MouseOverEvent>((type) => { selectPixelButton.style.backgroundColor = buttonHoverColor; });
            selectPixelButton.RegisterCallback<MouseOutEvent>((type) => { selectPixelButton.style.backgroundColor = buttonDefaultColor; });
            selectPixelButton.RegisterCallback<MouseDownEvent>((type) => { selectPixelButton.style.backgroundColor = buttonPressedColor; });
            selectPixelButton.RegisterCallback<MouseUpEvent>((type) => { selectPixelButton.style.backgroundColor = buttonDefaultColor; });

            selectPixelButton.RegisterCallback<MouseDownEvent>(e => APVProbeReferenceVolume.probeSamplingDebugData.update = ProbeSamplingDebugUpdate.Always);

            probeSampling_row.Add(selectPixelButton);

            vertexSamplingWarning = new TextElement();
            vertexSamplingWarning.text = "Warning: Probe Sampling is currently set to \n" +
                                         "per-vertex. This debug mode shows per-pixel \n" +
                                         "information.";
            vertexSamplingWarning.style.display = DisplayStyle.None;

            probeSampling_row.Add(vertexSamplingWarning);

            layerMasksGroupBox.style.display = DisplayStyle.None;
            probeDistanceGroupBox.style.display = DisplayStyle.None;
            probeSamplingGroupBox.style.display = DisplayStyle.None;

            root.Add(layerMasksGroupBox);
            root.Add(probeDistanceGroupBox);
            root.Add(probeSamplingGroupBox);

            return root;
        }
    }
    #endregion
}
