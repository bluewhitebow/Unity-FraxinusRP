using UnityEngine;
using System.Collections.Generic;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    using CurveState = InspectorCurveEditor.CurveState;

    [CustomEditor(typeof(ColorCurves))]
    sealed class ColorCurvesEditor : VolumeComponentEditor
    {
        SerializedDataParameter _master;
        SerializedDataParameter _red;
        SerializedDataParameter _green;
        SerializedDataParameter _blue;

        SerializedDataParameter _hueVsHue;
        SerializedDataParameter _hueVsSat;
        SerializedDataParameter _satVsSat;
        SerializedDataParameter _lumVsSat;

        // Internal references to the actual animation curves
        // Needed for the curve editor
        SerializedProperty _rawMaster;
        SerializedProperty _rawRed;
        SerializedProperty _rawGreen;
        SerializedProperty _rawBlue;

        SerializedProperty _rawHueVsHue;
        SerializedProperty _rawHueVsSat;
        SerializedProperty _rawSatVsSat;
        SerializedProperty _rawLumVsSat;

        InspectorCurveEditor _curveEditor;
        Dictionary<SerializedProperty, Color> _curveDict;
        static Material s_materialGrid;

        static GUIStyle s_preLabel;

        static string[] s_curveNames =
        {
            "Master",
            "Red",
            "Green",
            "Blue",
            "Hue Vs Hue",
            "Hue Vs Sat",
            "Sat Vs Sat",
            "Lum Vs Sat"
        };

        SavedInt _selectedCurve;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<ColorCurves>(serializedObject);

            _master = Unpack(o.Find(x => x.master));
            _red = Unpack(o.Find(x => x.red));
            _green = Unpack(o.Find(x => x.green));
            _blue = Unpack(o.Find(x => x.blue));

            _hueVsHue = Unpack(o.Find(x => x.hueVsHue));
            _hueVsSat = Unpack(o.Find(x => x.hueVsSat));
            _satVsSat = Unpack(o.Find(x => x.satVsSat));
            _lumVsSat = Unpack(o.Find(x => x.lumVsSat));

            _rawMaster = o.Find("master.m_Value.m_Curve");
            _rawRed = o.Find("red.m_Value.m_Curve");
            _rawGreen = o.Find("green.m_Value.m_Curve");
            _rawBlue = o.Find("blue.m_Value.m_Curve");

            _rawHueVsHue = o.Find("hueVsHue.m_Value.m_Curve");
            _rawHueVsSat = o.Find("hueVsSat.m_Value.m_Curve");
            _rawSatVsSat = o.Find("satVsSat.m_Value.m_Curve");
            _rawLumVsSat = o.Find("lumVsSat.m_Value.m_Curve");

            _selectedCurve = new SavedInt($"{target.GetType()}.SelectedCurve", 0);

            // Prepare the curve editor
            _curveEditor = new InspectorCurveEditor();
            _curveDict = new Dictionary<SerializedProperty, Color>();

            SetupCurve(_rawMaster, new Color(1f, 1f, 1f), 2, false);
            SetupCurve(_rawRed, new Color(1f, 0f, 0f), 2, false);
            SetupCurve(_rawGreen, new Color(0f, 1f, 0f), 2, false);
            SetupCurve(_rawBlue, new Color(0f, 0.5f, 1f), 2, false);
            SetupCurve(_rawHueVsHue, new Color(1f, 1f, 1f), 0, true);
            SetupCurve(_rawHueVsSat, new Color(1f, 1f, 1f), 0, true);
            SetupCurve(_rawSatVsSat, new Color(1f, 1f, 1f), 0, false);
            SetupCurve(_rawLumVsSat, new Color(1f, 1f, 1f), 0, false);
        }

        void SetupCurve(SerializedProperty prop, Color color, uint minPointCount, bool loop)
        {
            var state = CurveState.defaultState;
            state.color = color;
            state.visible = false;
            state.minPointCount = minPointCount;
            state.onlyShowHandlesOnSelection = true;
            state.zeroKeyConstantValue = 0.5f;
            state.loopInBounds = loop;
            _curveEditor.Add(prop, state);
            _curveDict.Add(prop, color);
        }

        void ResetVisibleCurves()
        {
            foreach (var curve in _curveDict)
            {
                var state = _curveEditor.GetCurveState(curve.Key);
                state.visible = false;
                _curveEditor.SetCurveState(curve.Key, state);
            }
        }

        void SetCurveVisible(SerializedProperty rawProp, SerializedProperty overrideProp)
        {
            var state = _curveEditor.GetCurveState(rawProp);
            state.visible = true;
            state.editable = overrideProp.boolValue;
            _curveEditor.SetCurveState(rawProp, state);
        }

        void CurveOverrideToggle(SerializedProperty overrideProp)
        {
            overrideProp.boolValue = GUILayout.Toggle(overrideProp.boolValue, EditorGUIUtility.TrTextContent("Override"), EditorStyles.toolbarButton);
        }

        string MakeCurveSelectionPopupLabel(int id)
        {
            string label = s_curveNames[id];
            const string overrideSuffix = " (Overriding)";
            switch (id)
            {
                case 0: if (_master.overrideState.boolValue) label += overrideSuffix; break;
                case 1: if (_red.overrideState.boolValue) label += overrideSuffix; break;
                case 2: if (_green.overrideState.boolValue) label += overrideSuffix; break;
                case 3: if (_blue.overrideState.boolValue) label += overrideSuffix; break;
                case 4: if (_hueVsHue.overrideState.boolValue) label += overrideSuffix; break;
                case 5: if (_hueVsSat.overrideState.boolValue) label += overrideSuffix; break;
                case 6: if (_satVsSat.overrideState.boolValue) label += overrideSuffix; break;
                case 7: if (_lumVsSat.overrideState.boolValue) label += overrideSuffix; break;
            }
            return label;
        }

        int DoCurveSelectionPopup(int id)
        {
            var label = MakeCurveSelectionPopupLabel(id);
            GUILayout.Label(label, EditorStyles.toolbarPopup, GUILayout.MaxWidth(150f));

            var lastRect = GUILayoutUtility.GetLastRect();
            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0 && lastRect.Contains(e.mousePosition))
            {
                var menu = new GenericMenu();

                for (int i = 0; i < s_curveNames.Length; i++)
                {
                    if (i == 4)
                        menu.AddSeparator("");

                    int current = i; // Capture local for closure

                    var menuLabel = MakeCurveSelectionPopupLabel(i);
                    menu.AddItem(new GUIContent(menuLabel), current == id, () =>
                    {
                        _selectedCurve.value = current;
                        serializedObject.ApplyModifiedProperties();
                    });
                }

                menu.DropDown(new Rect(lastRect.xMin, lastRect.yMax, 1f, 1f));
            }

            return id;
        }

        void DrawBackgroundTexture(Rect rect, int pass)
        {
            if (s_materialGrid == null)
                s_materialGrid = new Material(Shader.Find("Hidden/Ultra Render Pipeline/Editor/CurveBackground")) { hideFlags = HideFlags.HideAndDontSave };

            float scale = EditorGUIUtility.pixelsPerPoint;

            var oldRt = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(Mathf.CeilToInt(rect.width * scale), Mathf.CeilToInt(rect.height * scale), 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            s_materialGrid.SetFloat("_DisabledState", GUI.enabled ? 1f : 0.5f);

            Graphics.Blit(null, rt, s_materialGrid, pass);
            RenderTexture.active = oldRt;

            GUI.DrawTexture(rect, rt);
            RenderTexture.ReleaseTemporary(rt);
        }

        void MarkTextureCurveAsDirty(int curveId)
        {
            var t = target as ColorCurves;

            if (t == null)
                return;

            switch (curveId)
            {
                case 0: t.master.value.SetDirty(); break;
                case 1: t.red.value.SetDirty(); break;
                case 2: t.green.value.SetDirty(); break;
                case 3: t.blue.value.SetDirty(); break;
                case 4: t.hueVsHue.value.SetDirty(); break;
                case 5: t.hueVsSat.value.SetDirty(); break;
                case 6: t.satVsSat.value.SetDirty(); break;
                case 7: t.lumVsSat.value.SetDirty(); break;
            }
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.Space();
            ResetVisibleCurves();

            using (new EditorGUI.DisabledGroupScope(serializedObject.isEditingMultipleObjects))
            {
                int curveEditingId;
                SerializedProperty currentCurveRawProp = null;

                // Top toolbar
                using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    curveEditingId = DoCurveSelectionPopup(_selectedCurve.value);
                    curveEditingId = Mathf.Clamp(curveEditingId, 0, 7);

                    EditorGUILayout.Space();

                    switch (curveEditingId)
                    {
                        case 0:
                            CurveOverrideToggle(_master.overrideState);
                            SetCurveVisible(_rawMaster, _master.overrideState);
                            currentCurveRawProp = _rawMaster;
                            break;
                        case 1:
                            CurveOverrideToggle(_red.overrideState);
                            SetCurveVisible(_rawRed, _red.overrideState);
                            currentCurveRawProp = _rawRed;
                            break;
                        case 2:
                            CurveOverrideToggle(_green.overrideState);
                            SetCurveVisible(_rawGreen, _green.overrideState);
                            currentCurveRawProp = _rawGreen;
                            break;
                        case 3:
                            CurveOverrideToggle(_blue.overrideState);
                            SetCurveVisible(_rawBlue, _blue.overrideState);
                            currentCurveRawProp = _rawBlue;
                            break;
                        case 4:
                            CurveOverrideToggle(_hueVsHue.overrideState);
                            SetCurveVisible(_rawHueVsHue, _hueVsHue.overrideState);
                            currentCurveRawProp = _rawHueVsHue;
                            break;
                        case 5:
                            CurveOverrideToggle(_hueVsSat.overrideState);
                            SetCurveVisible(_rawHueVsSat, _hueVsSat.overrideState);
                            currentCurveRawProp = _rawHueVsSat;
                            break;
                        case 6:
                            CurveOverrideToggle(_satVsSat.overrideState);
                            SetCurveVisible(_rawSatVsSat, _satVsSat.overrideState);
                            currentCurveRawProp = _rawSatVsSat;
                            break;
                        case 7:
                            CurveOverrideToggle(_lumVsSat.overrideState);
                            SetCurveVisible(_rawLumVsSat, _lumVsSat.overrideState);
                            currentCurveRawProp = _rawLumVsSat;
                            break;
                    }

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Reset", EditorStyles.toolbarButton))
                    {
                        MarkTextureCurveAsDirty(curveEditingId);

                        switch (curveEditingId)
                        {
                            case 0: _rawMaster.animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f); break;
                            case 1: _rawRed.animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f); break;
                            case 2: _rawGreen.animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f); break;
                            case 3: _rawBlue.animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f); break;
                            case 4: _rawHueVsHue.animationCurveValue = new AnimationCurve(); break;
                            case 5: _rawHueVsSat.animationCurveValue = new AnimationCurve(); break;
                            case 6: _rawSatVsSat.animationCurveValue = new AnimationCurve(); break;
                            case 7: _rawLumVsSat.animationCurveValue = new AnimationCurve(); break;
                        }
                    }

                    _selectedCurve.value = curveEditingId;
                }

                // Curve area
                var rect = GUILayoutUtility.GetAspectRect(2f);
                var innerRect = new RectOffset(10, 10, 10, 10).Remove(rect);

                if (Event.current.type == EventType.Repaint)
                {
                    // Background
                    EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f, 1f));

                    if (curveEditingId == 4 || curveEditingId == 5)
                        DrawBackgroundTexture(innerRect, 0);
                    else if (curveEditingId == 6 || curveEditingId == 7)
                        DrawBackgroundTexture(innerRect, 1);

                    // Bounds
                    Handles.color = Color.white * (GUI.enabled ? 1f : 0.5f);
                    Handles.DrawSolidRectangleWithOutline(innerRect, Color.clear, new Color(0.8f, 0.8f, 0.8f, 0.5f));

                    // Grid setup
                    Handles.color = new Color(1f, 1f, 1f, 0.05f);
                    int hLines = (int)Mathf.Sqrt(innerRect.width);
                    int vLines = (int)(hLines / (innerRect.width / innerRect.height));

                    // Vertical grid
                    int gridOffset = Mathf.FloorToInt(innerRect.width / hLines);
                    int gridPadding = ((int)(innerRect.width) % hLines) / 2;

                    for (int i = 1; i < hLines; i++)
                    {
                        var offset = i * Vector2.right * gridOffset;
                        offset.x += gridPadding;
                        Handles.DrawLine(innerRect.position + offset, new Vector2(innerRect.x, innerRect.yMax - 1) + offset);
                    }

                    // Horizontal grid
                    gridOffset = Mathf.FloorToInt(innerRect.height / vLines);
                    gridPadding = ((int)(innerRect.height) % vLines) / 2;

                    for (int i = 1; i < vLines; i++)
                    {
                        var offset = i * Vector2.up * gridOffset;
                        offset.y += gridPadding;
                        Handles.DrawLine(innerRect.position + offset, new Vector2(innerRect.xMax - 1, innerRect.y) + offset);
                    }
                }

                // Curve editor
                using (new GUI.ClipScope(innerRect))
                {
                    if (_curveEditor.OnGUI(new Rect(0, 0, innerRect.width - 1, innerRect.height - 1)))
                    {
                        Repaint();
                        GUI.changed = true;
                        MarkTextureCurveAsDirty(_selectedCurve.value);
                    }
                }

                if (Event.current.type == EventType.Repaint)
                {
                    // Borders
                    Handles.color = Color.black;
                    Handles.DrawLine(new Vector2(rect.x, rect.y - 20f), new Vector2(rect.xMax, rect.y - 20f));
                    Handles.DrawLine(new Vector2(rect.x, rect.y - 21f), new Vector2(rect.x, rect.yMax));
                    Handles.DrawLine(new Vector2(rect.x, rect.yMax), new Vector2(rect.xMax, rect.yMax));
                    Handles.DrawLine(new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMax, rect.y - 20f));

                    bool editable = _curveEditor.GetCurveState(currentCurveRawProp).editable;
                    string editableString = editable ? string.Empty : "(Not Overriding)\n";

                    // Selection info
                    var selection = _curveEditor.GetSelection();
                    var infoRect = innerRect;
                    infoRect.x += 5f;
                    infoRect.width = 100f;
                    infoRect.height = 30f;

                    if (s_preLabel == null)
                        s_preLabel = new GUIStyle("ShurikenLabel");

                    if (selection.curve != null && selection.keyframeIndex > -1)
                    {
                        var key = selection.keyframe.Value;
                        GUI.Label(infoRect, $"{key.time:F3}\n{key.value:F3}", s_preLabel);
                    }
                    else
                    {
                        GUI.Label(infoRect, editableString, s_preLabel);
                    }
                }
            }
        }
    }
}
