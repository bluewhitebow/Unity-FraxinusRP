using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    internal class TerrainLitShaderGUI : ShaderGUI, ITerrainLayerCustomUI
    {
        private class StylesLayer
        {
            public readonly GUIContent warningHeightBasedBlending = new GUIContent("Height-based blending is disabled if you have more than four TerrainLayer materials!");

            public readonly GUIContent enableHeightBlend = new GUIContent("Enable Height-based Blend", "Blend terrain layers based on height values.");
            public readonly GUIContent heightTransition = new GUIContent("Height Transition", "Size in world units of the smooth transition between layers.");
            public readonly GUIContent enableInstancedPerPixelNormal = new GUIContent("Enable Per-pixel Normal", "Enable per-pixel normal when the terrain uses instanced rendering.");

            public readonly GUIContent diffuseTexture = new GUIContent("Diffuse");
            public readonly GUIContent colorTint = new GUIContent("Color Tint");
            public readonly GUIContent opacityAsDensity = new GUIContent("Opacity as Density", "Enable Density Blend (if unchecked, opacity is used as Smoothness)");
            public readonly GUIContent normalMapTexture = new GUIContent("Normal Map");
            public readonly GUIContent normalScale = new GUIContent("Normal Scale");
            public readonly GUIContent maskMapTexture = new GUIContent("Mask", "R: Metallic\nG: AO\nB: Height\nA: Smoothness");
            public readonly GUIContent maskMapTextureWithoutHeight = new GUIContent("Mask Map", "R: Metallic\nG: AO\nA: Smoothness");
            public readonly GUIContent channelRemapping = new GUIContent("Channel Remapping");
            public readonly GUIContent defaultValues = new GUIContent("Channel Default Values");
            public readonly GUIContent metallic = new GUIContent("R: Metallic");
            public readonly GUIContent ao = new GUIContent("G: AO");
            public readonly GUIContent height = new GUIContent("B: Height");
            public readonly GUIContent heightParametrization = new GUIContent("Parametrization");
            public readonly GUIContent heightAmplitude = new GUIContent("Amplitude (cm)");
            public readonly GUIContent heightBase = new GUIContent("Base (cm)");
            public readonly GUIContent heightMin = new GUIContent("Min (cm)");
            public readonly GUIContent heightMax = new GUIContent("Max (cm)");
            public readonly GUIContent heightCm = new GUIContent("B: Height (cm)");
            public readonly GUIContent smoothness = new GUIContent("A: Smoothness");
        }

        private static StylesLayer s_styles = null;
        private static StylesLayer styles { get { if (s_styles == null) s_styles = new StylesLayer(); return s_styles; } }

        public TerrainLitShaderGUI()
        {
        }

        // Height blend params
        private MaterialProperty _enableHeightBlend = null;
        private const string k_enableHeightBlend = "_EnableHeightBlend";

        private MaterialProperty _heightTransition = null;
        private const string k_heightTransition = "_HeightTransition";

        // Per-pixel Normal (while instancing)
        private MaterialProperty _enableInstancedPerPixelNormal = null;
        private const string k_enableInstancedPerPixelNormal = "_EnableInstancedPerPixelNormal";

        private bool _showChannelRemapping = false;

        private enum HeightParametrization
        {
            Amplitude,
            MinMax
        }

        private HeightParametrization _heightParametrization = HeightParametrization.Amplitude;

        private static bool DoesTerrainUseMaskMaps(TerrainLayer[] terrainLayers)
        {
            for (int i = 0; i < terrainLayers.Length; ++i)
            {
                if (terrainLayers[i].maskMapTexture != null)
                    return true;
            }
            return false;
        }

        protected void FindMaterialProperties(MaterialProperty[] props)
        {
            _enableHeightBlend = FindProperty(k_enableHeightBlend, props, false);
            _heightTransition = FindProperty(k_heightTransition, props, false);
            _enableInstancedPerPixelNormal = FindProperty(k_enableInstancedPerPixelNormal, props, false);
        }

        public static void SetupMaterialKeywords(Material material)
        {
            bool enableHeightBlend = (material.HasProperty(k_enableHeightBlend) && material.GetFloat(k_enableHeightBlend) > 0);
            CoreUtils.SetKeyword(material, "_TERRAIN_BLEND_HEIGHT", enableHeightBlend);

            bool enableInstancedPerPixelNormal = material.GetFloat(k_enableInstancedPerPixelNormal) > 0.0f;
            CoreUtils.SetKeyword(material, "_TERRAIN_INSTANCED_PERPIXEL_NORMAL", enableInstancedPerPixelNormal);
        }

        static public bool TextureHasAlpha(Texture2D inTex)
        {
            if (inTex != null)
            {
                return GraphicsFormatUtility.HasAlphaChannel(GraphicsFormatUtility.GetGraphicsFormat(inTex.format, true));
            }
            return false;
        }

        public override void OnGUI(MaterialEditor materialEditorIn, MaterialProperty[] properties)
        {
            if (materialEditorIn == null)
                throw new ArgumentNullException("materialEditorIn");

            FindMaterialProperties(properties);

            bool optionsChanged = false;
            EditorGUI.BeginChangeCheck();
            {
                if (_enableHeightBlend != null)
                {
                    EditorGUI.indentLevel++;
                    materialEditorIn.ShaderProperty(_enableHeightBlend, styles.enableHeightBlend);
                    if (_enableHeightBlend.floatValue > 0)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.HelpBox(styles.warningHeightBasedBlending.text, MessageType.Info);
                        materialEditorIn.ShaderProperty(_heightTransition, styles.heightTransition);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space();
            }
            if (EditorGUI.EndChangeCheck())
            {
                optionsChanged = true;
            }

            bool enablePerPixelNormalChanged = false;

            // Since Instanced Per-pixel normal is actually dependent on instancing enabled or not, it is not
            // important to check it in the GUI.  The shader will make sure it is enabled/disabled properly.s
            if (_enableInstancedPerPixelNormal != null)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                materialEditorIn.ShaderProperty(_enableInstancedPerPixelNormal, styles.enableInstancedPerPixelNormal);
                enablePerPixelNormalChanged = EditorGUI.EndChangeCheck();
                EditorGUI.indentLevel--;
            }

            if (optionsChanged || enablePerPixelNormalChanged)
            {
                foreach (var obj in materialEditorIn.targets)
                {
                    SetupMaterialKeywords((Material)obj);
                }
            }

            // We should always do this call at the end
            materialEditorIn.serializedObject.ApplyModifiedProperties();
        }

        bool ITerrainLayerCustomUI.OnTerrainLayerGUI(TerrainLayer terrainLayer, Terrain terrain)
        {
            var terrainLayers = terrain.terrainData.terrainLayers;

            // Don't use the member field _enableHeightBlend as ShaderGUI.OnGUI might not be called if the material UI is folded.
            // heightblend shouldn't be available if we are in multi-pass mode, because it is guaranteed to be broken.
            bool heightBlendAvailable = (terrainLayers.Length <= 4);
            bool heightBlend = heightBlendAvailable && terrain.materialTemplate.HasProperty(k_enableHeightBlend) && (terrain.materialTemplate.GetFloat(k_enableHeightBlend) > 0);

            terrainLayer.diffuseTexture = EditorGUILayout.ObjectField(styles.diffuseTexture, terrainLayer.diffuseTexture, typeof(Texture2D), false) as Texture2D;
            TerrainLayerUtility.ValidateDiffuseTextureUI(terrainLayer.diffuseTexture);

            var diffuseRemapMin = terrainLayer.diffuseRemapMin;
            var diffuseRemapMax = terrainLayer.diffuseRemapMax;
            EditorGUI.BeginChangeCheck();

            bool enableDensity = false;
            if (terrainLayer.diffuseTexture != null)
            {
                var rect = GUILayoutUtility.GetLastRect();
                rect.y += 16 + 4;
                rect.width = EditorGUIUtility.labelWidth + 64;
                rect.height = 16;

                ++EditorGUI.indentLevel;

                var diffuseTint = new Color(diffuseRemapMax.x, diffuseRemapMax.y, diffuseRemapMax.z);
                diffuseTint = EditorGUI.ColorField(rect, styles.colorTint, diffuseTint, true, false, false);
                diffuseRemapMax.x = diffuseTint.r;
                diffuseRemapMax.y = diffuseTint.g;
                diffuseRemapMax.z = diffuseTint.b;
                diffuseRemapMin.x = diffuseRemapMin.y = diffuseRemapMin.z = 0;

                if (!heightBlend)
                {
                    rect.y = rect.yMax + 2;
                    enableDensity = EditorGUI.Toggle(rect, styles.opacityAsDensity, diffuseRemapMin.w > 0);
                }

                --EditorGUI.indentLevel;
            }
            diffuseRemapMax.w = 1;
            diffuseRemapMin.w = enableDensity ? 1 : 0;

            if (EditorGUI.EndChangeCheck())
            {
                terrainLayer.diffuseRemapMin = diffuseRemapMin;
                terrainLayer.diffuseRemapMax = diffuseRemapMax;
            }

            // Display normal map UI
            terrainLayer.normalMapTexture = EditorGUILayout.ObjectField(styles.normalMapTexture, terrainLayer.normalMapTexture, typeof(Texture2D), false) as Texture2D;
            TerrainLayerUtility.ValidateNormalMapTextureUI(terrainLayer.normalMapTexture, TerrainLayerUtility.CheckNormalMapTextureType(terrainLayer.normalMapTexture));

            if (terrainLayer.normalMapTexture != null)
            {
                var rect = GUILayoutUtility.GetLastRect();
                rect.y += 16 + 4;
                rect.width = EditorGUIUtility.labelWidth + 64;
                rect.height = 16;

                ++EditorGUI.indentLevel;
                terrainLayer.normalScale = EditorGUI.FloatField(rect, styles.normalScale, terrainLayer.normalScale);
                --EditorGUI.indentLevel;
            }

            // Display the mask map UI and the remap controls
            terrainLayer.maskMapTexture = EditorGUILayout.ObjectField(heightBlend ? styles.maskMapTexture : styles.maskMapTextureWithoutHeight, terrainLayer.maskMapTexture, typeof(Texture2D), false) as Texture2D;
            TerrainLayerUtility.ValidateMaskMapTextureUI(terrainLayer.maskMapTexture);

            var maskMapRemapMin = terrainLayer.maskMapRemapMin;
            var maskMapRemapMax = terrainLayer.maskMapRemapMax;
            var smoothness = terrainLayer.smoothness;
            var metallic = terrainLayer.metallic;

            ++EditorGUI.indentLevel;
            EditorGUI.BeginChangeCheck();

            _showChannelRemapping = EditorGUILayout.Foldout(_showChannelRemapping, terrainLayer.maskMapTexture != null ? s_styles.channelRemapping : s_styles.defaultValues);

            if (_showChannelRemapping)
            {
                if (terrainLayer.maskMapTexture != null)
                {
                    float min, max;
                    min = maskMapRemapMin.x; max = maskMapRemapMax.x;
                    EditorGUILayout.MinMaxSlider(s_styles.metallic, ref min, ref max, 0, 1);
                    maskMapRemapMin.x = min; maskMapRemapMax.x = max;

                    min = maskMapRemapMin.y; max = maskMapRemapMax.y;
                    EditorGUILayout.MinMaxSlider(s_styles.ao, ref min, ref max, 0, 1);
                    maskMapRemapMin.y = min; maskMapRemapMax.y = max;

                    if (heightBlend)
                    {
                        EditorGUILayout.LabelField(styles.height);
                        ++EditorGUI.indentLevel;
                        _heightParametrization = (HeightParametrization)EditorGUILayout.EnumPopup(styles.heightParametrization, _heightParametrization);
                        if (_heightParametrization == HeightParametrization.Amplitude)
                        {
                            // (height - heightBase) * amplitude
                            float amplitude = Mathf.Max(maskMapRemapMax.z - maskMapRemapMin.z, Mathf.Epsilon); // to avoid divide by zero
                            float heightBase = maskMapRemapMin.z / amplitude;
                            amplitude = EditorGUILayout.FloatField(styles.heightAmplitude, amplitude * 100) / 100;
                            heightBase = EditorGUILayout.FloatField(styles.heightBase, heightBase * 100) / 100;
                            maskMapRemapMin.z = heightBase * amplitude;
                            maskMapRemapMax.z = (1.0f - heightBase) * amplitude;
                        }
                        else
                        {
                            maskMapRemapMin.z = EditorGUILayout.FloatField(styles.heightMin, maskMapRemapMin.z * 100) / 100;
                            maskMapRemapMax.z = EditorGUILayout.FloatField(styles.heightMax, maskMapRemapMax.z * 100) / 100;
                        }
                        --EditorGUI.indentLevel;
                    }

                    min = maskMapRemapMin.w; max = maskMapRemapMax.w;
                    EditorGUILayout.MinMaxSlider(s_styles.smoothness, ref min, ref max, 0, 1);
                    maskMapRemapMin.w = min; maskMapRemapMax.w = max;
                }
                else
                {
                    metallic = EditorGUILayout.Slider(s_styles.metallic, metallic, 0, 1);
                    // AO and Height are still exclusively controlled via the maskRemap controls
                    // metallic and smoothness have their own values as fields within the LayerData.
                    maskMapRemapMax.y = EditorGUILayout.Slider(s_styles.ao, maskMapRemapMax.y, 0, 1);
                    if (heightBlend)
                    {
                        maskMapRemapMax.z = EditorGUILayout.FloatField(s_styles.heightCm, maskMapRemapMax.z * 100) / 100;
                    }

                    // There's a possibility that someone could slide max below the existing min value
                    // so we'll just protect against that by locking the min value down a little bit.
                    // In the case of height (Z), we are trying to set min to no lower than zero value unless
                    // max goes negative.  Zero is a good sensible value for the minimum.  For AO (Y), we
                    // don't need this extra protection step because the UI blocks us from going negative
                    // anyway.  In both cases, pushing the slider below the min value will lock them together,
                    // but min will be "left behind" if you go back up.
                    maskMapRemapMin.y = Mathf.Min(maskMapRemapMin.y, maskMapRemapMax.y);
                    maskMapRemapMin.z = Mathf.Min(Mathf.Max(0, maskMapRemapMin.z), maskMapRemapMax.z);

                    if (TextureHasAlpha(terrainLayer.diffuseTexture))
                    {
#if UNITY_6000_0_OR_NEWER
                        terrainLayer.smoothnessSource = (UnityEngine.TerrainLayerSmoothnessSource)EditorGUILayout.EnumPopup(EditorGUIUtility.TrTextContent("Smoothness Source"), terrainLayer.smoothnessSource);
                        if (terrainLayer.smoothnessSource == TerrainLayerSmoothnessSource.DiffuseAlphaChannel)
                        {
                            GUIStyle warnStyle = new GUIStyle(GUI.skin.label);
                            warnStyle.wordWrap = true;
                            GUILayout.Label("Smoothness is controlled by diffuse alpha channel", warnStyle);
                        }
                        else
                        {
                            smoothness = EditorGUILayout.Slider(s_styles.smoothness, smoothness, 0, 1);
                        }
#else
                        // In Unity 2022 a diffuse texture with alpha always owns terrain
                        // smoothness; the selectable source enum was added later.
                        GUIStyle warnStyle = new GUIStyle(GUI.skin.label);
                        warnStyle.wordWrap = true;
                        GUILayout.Label("Smoothness is controlled by diffuse alpha channel", warnStyle);
#endif
                    }
                    else
                    {
                        smoothness = EditorGUILayout.Slider(s_styles.smoothness, smoothness, 0, 1);
                    }
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                terrainLayer.maskMapRemapMin = maskMapRemapMin;
                terrainLayer.maskMapRemapMax = maskMapRemapMax;
                terrainLayer.smoothness = smoothness;
                terrainLayer.metallic = metallic;
            }
            --EditorGUI.indentLevel;

            EditorGUILayout.Space();
            TerrainLayerUtility.TilingSettingsUI(terrainLayer);

            return true;
        }
    }
}
