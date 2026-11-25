using System;
using JetBrains.Annotations;
using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // TODO: Should be cleaned up and put into CoreRP/Editor
    sealed class TrackballUIDrawer
    {
        static readonly int s_thumbHash = "colorWheelThumb".GetHashCode();
        static GUIStyle s_wheelThumb;
        static Vector2 s_wheelThumbSize;
        static Material s_material;

        Func<Vector4, Vector3> _computeFunc;
        bool _resetState;
        Vector2 _cursorPos;
        const string k_shaderName = "Hidden/Ultra Render Pipeline/Editor/Trackball";

        public void OnGUI(SerializedProperty property, [CanBeNull] SerializedProperty overrideState, GUIContent title, Func<Vector4, Vector3> computeFunc)
        {
            if (!CheckMaterialAndShader())
            {
                return;
            }

            if (property.propertyType != SerializedPropertyType.Vector4)
            {
                Debug.LogWarning("TrackballUIDrawer requires a Vector4 property");
                return;
            }

            _computeFunc = computeFunc;
            var value = property.vector4Value;

            using (new EditorGUILayout.VerticalScope())
            {
                bool isOverridden = overrideState?.boolValue ?? true;
                using (new EditorGUI.DisabledScope(!isOverridden))
                    DrawWheel(ref value, isOverridden);

                DrawLabelAndOverride(title, overrideState);
            }

            if (_resetState)
            {
                value = new Vector4(1f, 1f, 1f, 0f);
                _resetState = false;
            }

            property.vector4Value = value;
        }

        void DrawWheel(ref Vector4 value, bool overrideState)
        {
            var wheelRect = GUILayoutUtility.GetAspectRect(1f);
            float size = wheelRect.width;
            float hsize = size / 2f;
            float radius = 0.38f * size;

            Vector3 hsv;
            Color.RGBToHSV(value, out hsv.x, out hsv.y, out hsv.z);
            float offset = value.w;

            // Thumb
            var thumbPos = Vector2.zero;
            float theta = hsv.x * (Mathf.PI * 2f);
            thumbPos.x = Mathf.Cos(theta + (Mathf.PI / 2f));
            thumbPos.y = Mathf.Sin(theta - (Mathf.PI / 2f));
            thumbPos *= hsv.y * radius;

            // Draw the wheel
            if (Event.current.type == EventType.Repaint)
            {
                // Style init
                if (s_wheelThumb == null)
                {
                    s_wheelThumb = new GUIStyle("ColorPicker2DThumb");
                    s_wheelThumbSize = new Vector2(
                        !Mathf.Approximately(s_wheelThumb.fixedWidth, 0f) ? s_wheelThumb.fixedWidth : s_wheelThumb.padding.horizontal,
                        !Mathf.Approximately(s_wheelThumb.fixedHeight, 0f) ? s_wheelThumb.fixedHeight : s_wheelThumb.padding.vertical
                    );
                }

                // Retina support
                float scale = EditorGUIUtility.pixelsPerPoint;

                // Wheel texture
                var oldRT = RenderTexture.active;
                var rt = RenderTexture.GetTemporary((int)(size * scale), (int)(size * scale), 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                s_material.SetFloat("_Offset", offset);
                s_material.SetFloat("_DisabledState", overrideState && GUI.enabled ? 1f : 0.5f);
                s_material.SetVector("_Resolution", new Vector2(size * scale, size * scale / 2f));
                Graphics.Blit(null, rt, s_material, EditorGUIUtility.isProSkin ? 0 : 1);
                RenderTexture.active = oldRT;

                GUI.DrawTexture(wheelRect, rt);
                RenderTexture.ReleaseTemporary(rt);

                var thumbSize = s_wheelThumbSize;
                var thumbSizeH = thumbSize / 2f;
                s_wheelThumb.Draw(new Rect(wheelRect.x + hsize + thumbPos.x - thumbSizeH.x, wheelRect.y + hsize + thumbPos.y - thumbSizeH.y, thumbSize.x, thumbSize.y), false, false, false, false);
            }

            // Input
            var bounds = wheelRect;
            bounds.x += hsize - radius;
            bounds.y += hsize - radius;
            bounds.width = bounds.height = radius * 2f;
            hsv = GetInput(bounds, hsv, thumbPos, radius);
            value = Color.HSVToRGB(hsv.x, hsv.y, 1f);
            value.w = offset;

            // Offset
            var sliderRect = GUILayoutUtility.GetRect(1f, 17f);
            float padding = sliderRect.width * 0.05f; // 5% padding
            sliderRect.xMin += padding;
            sliderRect.xMax -= padding;
            value.w = GUI.HorizontalSlider(sliderRect, value.w, -1f, 1f);

            if (_computeFunc == null)
                return;

            // Values
            var displayValue = _computeFunc(value);

            using (new EditorGUI.DisabledGroupScope(true))
            {
                var valuesRect = GUILayoutUtility.GetRect(1f, 17f);
                valuesRect.width /= 3f;
                GUI.Label(valuesRect, displayValue.x.ToString("F2"), EditorStyles.centeredGreyMiniLabel);
                valuesRect.x += valuesRect.width;
                GUI.Label(valuesRect, displayValue.y.ToString("F2"), EditorStyles.centeredGreyMiniLabel);
                valuesRect.x += valuesRect.width;
                GUI.Label(valuesRect, displayValue.z.ToString("F2"), EditorStyles.centeredGreyMiniLabel);
                valuesRect.x += valuesRect.width;
            }
        }

        void DrawLabelAndOverride(GUIContent title, SerializedProperty overrideState)
        {
            // Title
            var areaRect = GUILayoutUtility.GetRect(1f, 17f);
            var labelSize = EditorStyles.miniLabel.CalcSize(title);
            var labelRect = new Rect(areaRect.x + areaRect.width / 2 - labelSize.x / 2, areaRect.y, labelSize.x, labelSize.y);
            GUI.Label(labelRect, title, EditorStyles.miniLabel);

            // Override checkbox
            if (overrideState != null)
            {
                var overrideRect = new Rect(labelRect.x - 17, labelRect.y + 3, 17f, 17f);
                overrideState.boolValue = GUI.Toggle(overrideRect, overrideState.boolValue,
                    EditorGUIUtility.TrTextContent("", "Override this setting for this volume."),
                    CoreEditorStyles.smallTickbox);
            }
        }

        Vector3 GetInput(Rect bounds, Vector3 hsv, Vector2 thumbPos, float radius)
        {
            var e = Event.current;
            var id = GUIUtility.GetControlID(s_thumbHash, FocusType.Passive, bounds);
            var mousePos = e.mousePosition;

            if (e.type == EventType.MouseDown && GUIUtility.hotControl == 0 && bounds.Contains(mousePos))
            {
                if (e.button == 0)
                {
                    var center = new Vector2(bounds.x + radius, bounds.y + radius);
                    float dist = Vector2.Distance(center, mousePos);

                    if (dist <= radius)
                    {
                        e.Use();
                        _cursorPos = new Vector2(thumbPos.x + radius, thumbPos.y + radius);
                        GUIUtility.hotControl = id;
                        GUI.changed = true;
                    }
                }
                else if (e.button == 1)
                {
                    e.Use();
                    GUI.changed = true;
                    _resetState = true;
                }
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && GUIUtility.hotControl == id)
            {
                e.Use();
                GUI.changed = true;
                _cursorPos += e.delta * 0.2f; // Sensitivity
                GetWheelHueSaturation(_cursorPos.x, _cursorPos.y, radius, out hsv.x, out hsv.y);
            }
            else if (e.rawType == EventType.MouseUp && e.button == 0 && GUIUtility.hotControl == id)
            {
                e.Use();
                GUIUtility.hotControl = 0;
            }

            return hsv;
        }

        void GetWheelHueSaturation(float x, float y, float radius, out float hue, out float saturation)
        {
            float dx = (x - radius) / radius;
            float dy = (y - radius) / radius;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            hue = Mathf.Atan2(dx, -dy);
            hue = 1f - ((hue > 0) ? hue : (Mathf.PI * 2f) + hue) / (Mathf.PI * 2f);
            saturation = Mathf.Clamp01(d);
        }

        bool CheckMaterialAndShader()
        {
            if (s_material != null)
            {
                return true;
            }

            Shader shader = Shader.Find(k_shaderName);
            if (shader == null)
            {
                Debug.LogError("TrackballUIDrawer: Unable to find shader \"" + k_shaderName + "\"");
                return false;
            }
            s_material = new Material(shader);

            return true;
        }
    }
}
