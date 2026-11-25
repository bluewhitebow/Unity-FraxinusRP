using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // TODO: handle retina / EditorGUIUtility.pixelsPerPoint
    [CustomEditor(typeof(ShadowsMidtonesHighlights))]
    sealed class ShadowsMidtonesHighlightsEditor : VolumeComponentEditor
    {
        private static class Styles
        {
            public static readonly GUIContent shadowsLabel = EditorGUIUtility.TrTextContent("Shadows", "Use this to control and apply a hue to the shadows.");
            public static readonly GUIContent midtonesLabel = EditorGUIUtility.TrTextContent("Midtones", "Use this to control and apply a hue to the shadows.");
            public static readonly GUIContent highlightsLabel = EditorGUIUtility.TrTextContent("Highlights", "Use this to control and apply a hue to the shadows.");
        }

        SerializedDataParameter _shadows;
        SerializedDataParameter _midtones;
        SerializedDataParameter _highlights;
        SerializedDataParameter _shadowsStart;
        SerializedDataParameter _shadowsEnd;
        SerializedDataParameter _highlightsStart;
        SerializedDataParameter _highlightsEnd;

        const string k_shaderName = "Hidden/Ultra Render Pipeline/Editor/Shadows Midtones Highlights Curve";
        static Material s_material;
        readonly TrackballUIDrawer _trackballUIDrawer = new TrackballUIDrawer();

        // Curve drawing utilities
        Rect m_CurveRect;
        RenderTexture m_CurveTex;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<ShadowsMidtonesHighlights>(serializedObject);

            _shadows = Unpack(o.Find(x => x.shadows));
            _midtones = Unpack(o.Find(x => x.midtones));
            _highlights = Unpack(o.Find(x => x.highlights));
            _shadowsStart = Unpack(o.Find(x => x.shadowsStart));
            _shadowsEnd = Unpack(o.Find(x => x.shadowsEnd));
            _highlightsStart = Unpack(o.Find(x => x.highlightsStart));
            _highlightsEnd = Unpack(o.Find(x => x.highlightsEnd));
        }

        public override void OnInspectorGUI()
        {
            if (!CheckMaterialAndShader())
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _trackballUIDrawer.OnGUI(_shadows.value, enableOverrides ? _shadows.overrideState : null, Styles.shadowsLabel, GetWheelValue);
                GUILayout.Space(4f);
                _trackballUIDrawer.OnGUI(_midtones.value, enableOverrides ? _midtones.overrideState : null, Styles.midtonesLabel, GetWheelValue);
                GUILayout.Space(4f);
                _trackballUIDrawer.OnGUI(_highlights.value, enableOverrides ? _highlights.overrideState : null, Styles.highlightsLabel, GetWheelValue);
            }
            EditorGUILayout.Space();

            // Reserve GUI space
            m_CurveRect = GUILayoutUtility.GetRect(128, 80);
            m_CurveRect.xMin += EditorGUI.indentLevel * 15f;

            if (Event.current.type == EventType.Repaint)
            {
                float alpha = GUI.enabled ? 1f : 0.4f;
                var limits = new Vector4(_shadowsStart.value.floatValue, _shadowsEnd.value.floatValue, _highlightsStart.value.floatValue, _highlightsEnd.value.floatValue);

                s_material.SetVector("_ShaHiLimits", limits);
                s_material.SetVector("_Variants", new Vector4(alpha, Mathf.Max(_highlightsEnd.value.floatValue, 1f), 0f, 0f));

                CheckCurveRT((int)m_CurveRect.width, (int)m_CurveRect.height);

                var oldRt = RenderTexture.active;
                Graphics.Blit(null, m_CurveTex, s_material, EditorGUIUtility.isProSkin ? 0 : 1);
                RenderTexture.active = oldRt;

                GUI.DrawTexture(m_CurveRect, m_CurveTex);

                Handles.DrawSolidRectangleWithOutline(m_CurveRect, Color.clear, Color.white * 0.4f);
            }

            PropertyField(_shadowsStart, EditorGUIUtility.TrTextContent("Start"));
            _shadowsStart.value.floatValue = Mathf.Min(_shadowsStart.value.floatValue, _shadowsEnd.value.floatValue);
            PropertyField(_shadowsEnd, EditorGUIUtility.TrTextContent("End"));
            _shadowsEnd.value.floatValue = Mathf.Max(_shadowsStart.value.floatValue, _shadowsEnd.value.floatValue);

            PropertyField(_highlightsStart, EditorGUIUtility.TrTextContent("Start"));
            _highlightsStart.value.floatValue = Mathf.Min(_highlightsStart.value.floatValue, _highlightsEnd.value.floatValue);
            PropertyField(_highlightsEnd, EditorGUIUtility.TrTextContent("End"));
            _highlightsEnd.value.floatValue = Mathf.Max(_highlightsStart.value.floatValue, _highlightsEnd.value.floatValue);
        }

        void CheckCurveRT(int width, int height)
        {
            if (m_CurveTex == null || !m_CurveTex.IsCreated() || m_CurveTex.width != width || m_CurveTex.height != height)
            {
                CoreUtils.Destroy(m_CurveTex);
                m_CurveTex = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                m_CurveTex.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        static Vector3 GetWheelValue(Vector4 v)
        {
            float w = v.w * (Mathf.Sign(v.w) < 0f ? 1f : 4f);
            return new Vector3(
                Mathf.Max(v.x + w, 0f),
                Mathf.Max(v.y + w, 0f),
                Mathf.Max(v.z + w, 0f)
            );
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
                Debug.LogError("ShadowsMidtonesHighlightsEditor: Unable to find shader \"" + k_shaderName + "\"");
                return false;
            }
            s_material = new Material(shader);

            return true;
        }
    }
}
