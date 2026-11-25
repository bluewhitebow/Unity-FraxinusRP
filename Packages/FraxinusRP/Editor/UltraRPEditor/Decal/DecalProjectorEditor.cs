using System;
using System.Collections.Generic;
using UnityEngine;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.IMGUI.Controls;
using UnityEditor.ShortcutManagement;
using UnityEditorInternal;

using static UnityEditorInternal.EditMode;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(DecalProjector))]
    [CanEditMultipleObjects]
    partial class DecalProjectorEditor : Editor
    {
        const float k_limit = 100000f;
        const float k_limitInv = 1f / k_limit;

        static Color fullColor
        {
            get
            {
                Color c = s_lastColor;
                c.a = 1f;
                return c;
            }
        }
        static Color s_lastColor;
        static void UpdateColorsInHandlesIfRequired()
        {
            Color c = DecalPreferences.decalGizmoColor;
            if (c != s_lastColor)
            {
                if (s_boxHandle != null && !s_boxHandle.Equals(null))
                    s_boxHandle = null;

                if (s_uvHandles != null && !s_uvHandles.Equals(null))
                    s_uvHandles.baseColor = c;

                s_lastColor = c;
            }
        }

        MaterialEditor _materialEditor = null;
        SerializedProperty _materialProperty;
        SerializedProperty _drawDistanceProperty;
        SerializedProperty _fadeScaleProperty;
        SerializedProperty _startAngleFadeProperty;
        SerializedProperty _endAngleFadeProperty;
        SerializedProperty _uvScaleProperty;
        SerializedProperty _uvBiasProperty;
        SerializedProperty _scaleMode;
        SerializedProperty _size;
        SerializedProperty[] _sizeValues;
        SerializedProperty _offset;
        SerializedProperty[] _offsetValues;
        SerializedProperty _fadeFactor;
        SerializedProperty _renderingLayerMask;

        int layerMask => (target as Component).gameObject.layer;
        bool layerMaskHasMultipleValues
        {
            get
            {
                if (targets.Length < 2)
                    return false;
                int layerMask = (targets[0] as Component).gameObject.layer;
                for (int index = 1; index < targets.Length; ++index)
                {
                    if ((targets[index] as Component).gameObject.layer != layerMask)
                        return true;
                }
                return false;
            }
        }

        static HierarchicalBox s_boxHandle;
        static HierarchicalBox boxHandle
        {
            get
            {
                if (s_boxHandle == null || s_boxHandle.Equals(null))
                {
                    Color c = fullColor;
                    s_boxHandle = new HierarchicalBox(s_lastColor, new[] { c, c, c, c, c, c });
                    s_boxHandle.SetBaseColor(s_lastColor);
                    s_boxHandle.monoHandle = false;
                }
                return s_boxHandle;
            }
        }

        static DisplacableRectHandles s_uvHandles;
        static DisplacableRectHandles uvHandles
        {
            get
            {
                if (s_uvHandles == null || s_uvHandles.Equals(null))
                    s_uvHandles = new DisplacableRectHandles(s_lastColor);
                return s_uvHandles;
            }
        }

        static readonly BoxBoundsHandle s_areaLightHandle =
            new BoxBoundsHandle { axes = PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Y };

        const SceneViewEditMode k_editShapeWithoutPreservingUV = (SceneViewEditMode)90;
        const SceneViewEditMode k_editShapePreservingUV = (SceneViewEditMode)91;
        const SceneViewEditMode k_editUVAndPivot = (SceneViewEditMode)92;
        static readonly SceneViewEditMode[] k_editVolumeModes = new SceneViewEditMode[]
        {
            k_editShapeWithoutPreservingUV,
            k_editShapePreservingUV,
            k_editUVAndPivot,
        };

        static Func<Vector3, Quaternion, Vector3> s_drawPivotHandle;

        static GUIContent[] k_editVolumeLabels = null;
        static GUIContent[] editVolumeLabels => k_editVolumeLabels ?? (k_editVolumeLabels = new GUIContent[]
        {
            EditorGUIUtility.TrIconContent("d_ScaleTool", k_EditShapeWithoutPreservingUVTooltip),
            EditorGUIUtility.TrIconContent("d_RectTool", k_EditShapePreservingUVTooltip),
            EditorGUIUtility.TrIconContent("d_MoveTool", k_EditUVTooltip),
        });

        static List<DecalProjectorEditor> s_instances = new List<DecalProjectorEditor>();

        static DecalProjectorEditor FindEditorFromSelection()
        {
            GameObject[] selection = Selection.gameObjects;
            DecalProjector[] selectionTargets = Selection.GetFiltered<DecalProjector>(SelectionMode.Unfiltered);

            foreach (DecalProjectorEditor editor in s_instances)
            {
                if (selectionTargets.Length != editor.targets.Length)
                    continue;
                bool allOk = true;
                foreach (DecalProjector selectionTarget in selectionTargets)
                {
                    if (!Array.Find(editor.targets, t => t == selectionTarget))
                    {
                        allOk = false;
                        break;
                    }
                }
                if (!allOk)
                    continue;
                return editor;
            }
            return null;
        }

        private void OnEnable()
        {
            s_instances.Add(this);

            // Create an instance of the MaterialEditor
            UpdateMaterialEditor();

            // Fetch serialized properties
            _materialProperty = serializedObject.FindProperty("_material");
            _drawDistanceProperty = serializedObject.FindProperty("_drawDistance");
            _fadeScaleProperty = serializedObject.FindProperty("_fadeScale");
            _startAngleFadeProperty = serializedObject.FindProperty("_startAngleFade");
            _endAngleFadeProperty = serializedObject.FindProperty("_endAngleFade");
            _uvScaleProperty = serializedObject.FindProperty("_uvScale");
            _uvBiasProperty = serializedObject.FindProperty("_uvBias");
            _scaleMode = serializedObject.FindProperty("_scaleMode");
            _size = serializedObject.FindProperty("sizeValue");
            _sizeValues = new[]
            {
                _size.FindPropertyRelative("x"),
                _size.FindPropertyRelative("y"),
                _size.FindPropertyRelative("z"),
            };
            _offset = serializedObject.FindProperty("offsetValue");
            _offsetValues = new[]
            {
                _offset.FindPropertyRelative("x"),
                _offset.FindPropertyRelative("y"),
                _offset.FindPropertyRelative("z"),
            };
            _fadeFactor = serializedObject.FindProperty("_fadeFactor");
            _renderingLayerMask = serializedObject.FindProperty("_decalLayerMask");

            ReinitSavedRatioSizePivotPosition();
        }

        private void OnDisable()
        {
            s_instances.Remove(this);
        }

        private void OnDestroy() =>
            DestroyImmediate(_materialEditor);

        public bool HasFrameBounds()
        {
            return true;
        }

        public Bounds OnGetFrameBounds()
        {
            DecalProjector decalProjector = target as DecalProjector;

            return new Bounds(decalProjector.transform.position, boxHandle.size);
        }

        public void UpdateMaterialEditor()
        {
            int validMaterialsCount = 0;
            for (int index = 0; index < targets.Length; ++index)
            {
                DecalProjector decalProjector = (targets[index] as DecalProjector);
                if ((decalProjector != null) && (decalProjector.material != null))
                    validMaterialsCount++;
            }
            // Update material editor with the new material
            UnityEngine.Object[] materials = new UnityEngine.Object[validMaterialsCount];
            validMaterialsCount = 0;
            for (int index = 0; index < targets.Length; ++index)
            {
                DecalProjector decalProjector = (targets[index] as DecalProjector);

                if ((decalProjector != null) && (decalProjector.material != null))
                    materials[validMaterialsCount++] = (targets[index] as DecalProjector).material;
            }
            _materialEditor = (MaterialEditor)CreateEditor(materials);
        }

        void OnSceneGUI()
        {
            //called on each targets
            DrawHandles();
        }

        void DrawBoxTransformationHandles(DecalProjector decalProjector)
        {
            Vector3 scale = decalProjector.effectiveScale;
            using (new Handles.DrawingScope(fullColor, Matrix4x4.TRS(decalProjector.transform.position, decalProjector.transform.rotation, scale)))
            {
                Vector3 centerStart = decalProjector.pivot;
                boxHandle.center = centerStart;
                boxHandle.size = decalProjector.size;

                Vector3 boundsSizePreviousOS = boxHandle.size;
                Vector3 boundsMinPreviousOS = boxHandle.size * -0.5f + boxHandle.center;

                EditorGUI.BeginChangeCheck();
                boxHandle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    // Adjust decal transform if handle changed.
                    Undo.RecordObject(decalProjector, "Decal Projector Change");

                    bool xChangeIsValid = scale.x != 0f;
                    bool yChangeIsValid = scale.y != 0f;
                    bool zChangeIsValid = scale.z != 0f;

                    // Preserve serialized state for axes with scale 0.
                    decalProjector.size = new Vector3(
                        xChangeIsValid ? boxHandle.size.x : decalProjector.size.x,
                        yChangeIsValid ? boxHandle.size.y : decalProjector.size.y,
                        zChangeIsValid ? boxHandle.size.z : decalProjector.size.z);
                    decalProjector.pivot = new Vector3(
                        xChangeIsValid ? boxHandle.center.x : decalProjector.pivot.x,
                        yChangeIsValid ? boxHandle.center.y : decalProjector.pivot.y,
                        zChangeIsValid ? boxHandle.center.z : decalProjector.pivot.z);

                    Vector3 boundsSizeCurrentOS = boxHandle.size;
                    Vector3 boundsMinCurrentOS = boxHandle.size * -0.5f + boxHandle.center;

                    if (editMode == k_editShapePreservingUV)
                    {
                        // Treat decal projector bounds as a crop tool, rather than a scale tool.
                        // Compute a new uv scale and bias terms to pin decal projection pixels in world space, irrespective of projector bounds.
                        // Preserve serialized state for axes with scale 0.
                        Vector2 uvScale = decalProjector.uvScale;
                        Vector2 uvBias = decalProjector.uvBias;
                        if (xChangeIsValid)
                        {
                            uvScale.x *= Mathf.Max(k_limitInv, boundsSizeCurrentOS.x) / Mathf.Max(k_limitInv, boundsSizePreviousOS.x);
                            uvBias.x += (boundsMinCurrentOS.x - boundsMinPreviousOS.x) / Mathf.Max(k_limitInv, boundsSizeCurrentOS.x) * uvScale.x;
                        }
                        if (yChangeIsValid)
                        {
                            uvScale.y *= Mathf.Max(k_limitInv, boundsSizeCurrentOS.y) / Mathf.Max(k_limitInv, boundsSizePreviousOS.y);
                            uvBias.y += (boundsMinCurrentOS.y - boundsMinPreviousOS.y) / Mathf.Max(k_limitInv, boundsSizeCurrentOS.y) * uvScale.y;
                        }
                        decalProjector.uvScale = uvScale;
                        decalProjector.uvBias = uvBias;
                    }

                    if (PrefabUtility.IsPartOfNonAssetPrefabInstance(decalProjector))
                    {
                        PrefabUtility.RecordPrefabInstancePropertyModifications(decalProjector);
                    }
                }
            }
        }

        void DrawPivotHandles(DecalProjector decalProjector)
        {
            Vector3 scale = decalProjector.effectiveScale;
            Vector3 scaledPivot = Vector3.Scale(decalProjector.pivot, scale);
            Vector3 scaledSize = Vector3.Scale(decalProjector.size, scale);

            using (new Handles.DrawingScope(fullColor, Matrix4x4.TRS(Vector3.zero, decalProjector.transform.rotation, Vector3.one)))
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newPosition = ProjectedTransform.DrawHandles(decalProjector.transform.position, .5f * scaledSize.z - scaledPivot.z, decalProjector.transform.rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(new UnityEngine.Object[] { decalProjector, decalProjector.transform }, "Decal Projector Change");

                    scaledPivot += Quaternion.Inverse(decalProjector.transform.rotation) * (decalProjector.transform.position - newPosition);
                    decalProjector.pivot = new Vector3(
                        scale.x != 0f ? scaledPivot.x / scale.x : decalProjector.pivot.x,
                        scale.y != 0f ? scaledPivot.y / scale.y : decalProjector.pivot.y,
                        scale.z != 0f ? scaledPivot.z / scale.z : decalProjector.pivot.z);
                    decalProjector.transform.position = newPosition;

                    ReinitSavedRatioSizePivotPosition();
                }
            }
        }

        void DrawUVHandles(DecalProjector decalProjector)
        {
            Vector3 scale = decalProjector.effectiveScale;
            Vector3 scaledPivot = Vector3.Scale(decalProjector.pivot, scale);
            Vector3 scaledSize = Vector3.Scale(decalProjector.size, scale);

            using (new Handles.DrawingScope(Matrix4x4.TRS(decalProjector.transform.position + decalProjector.transform.rotation * (scaledPivot - .5f * scaledSize), decalProjector.transform.rotation, scale)))
            {
                Vector2 uvScale = decalProjector.uvScale;
                Vector2 uvBias = decalProjector.uvBias;

                Vector2 uvSize = new Vector2(
                    (uvScale.x > k_limit || uvScale.x < -k_limit) ? 0f : decalProjector.size.x / uvScale.x,
                    (uvScale.y > k_limit || uvScale.y < -k_limit) ? 0f : decalProjector.size.y / uvScale.y
                );
                Vector2 uvCenter = uvSize * .5f - new Vector2(uvBias.x * uvSize.x, uvBias.y * uvSize.y);

                uvHandles.center = uvCenter;
                uvHandles.size = uvSize;

                EditorGUI.BeginChangeCheck();
                uvHandles.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(decalProjector, "Decal Projector Change");

                    for (int channel = 0; channel < 2; channel++)
                    {
                        // Preserve serialized state for axes with the scaled size 0.
                        if (scaledSize[channel] != 0f)
                        {
                            float handleSize = uvHandles.size[channel];
                            float minusNewUVStart = .5f * handleSize - uvHandles.center[channel];
                            float decalSize = decalProjector.size[channel];
                            float limit = k_limitInv * decalSize;
                            if (handleSize > limit || handleSize < -limit)
                            {
                                uvScale[channel] = decalSize / handleSize;
                                uvBias[channel] = minusNewUVStart / handleSize;
                            }
                            else
                            {
                                // TODO: Decide if uvHandles.size should ever have negative value. It can't currently.
                                uvScale[channel] = k_limit * Mathf.Sign(handleSize);
                                uvBias[channel] = k_limit * minusNewUVStart / decalSize;
                            }
                        }
                    }

                    decalProjector.uvScale = uvScale;
                    decalProjector.uvBias = uvBias;
                }
            }
        }

        void DrawHandles()
        {
            DecalProjector decalProjector = target as DecalProjector;

            if (editMode == k_editShapePreservingUV || editMode == k_editShapeWithoutPreservingUV)
                DrawBoxTransformationHandles(decalProjector);
            else if (editMode == k_editUVAndPivot)
            {
                DrawPivotHandles(decalProjector);
                DrawUVHandles(decalProjector);
            }
        }

        static float CalculateGizmoLOD(DecalProjector decalProjector)
        {
#if UNITY_6000_0_OR_NEWER
            return Gizmos.CalculateLOD(decalProjector.transform.position,
                decalProjector.size.magnitude * 0.25f);
#else
            // Gizmos.CalculateLOD was added after 2022.3. HandleUtility.GetHandleSize gives
            // the world-space size of a fixed screen-space handle, which lets the backport
            // retain the same three useful LOD states without depending on the newer engine API.
            SceneView sceneView = SceneView.currentDrawingSceneView ?? SceneView.lastActiveSceneView;
            Camera camera = sceneView != null ? sceneView.camera : null;
            if (camera == null)
                return 1.0f;

            Vector3 viewportPosition = camera.WorldToViewportPoint(decalProjector.transform.position);
            if (viewportPosition.z <= 0.0f)
                return 0.0f;

            float handleSize = Mathf.Max(HandleUtility.GetHandleSize(decalProjector.transform.position), 0.0001f);
            float relativeSize = decalProjector.size.magnitude * 0.25f / handleSize;
            if (relativeSize < 0.02f)
                return 0.0f;
            return relativeSize < 0.15f ? 0.5f : 1.0f;
#endif
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        static void DrawGizmosSelected(DecalProjector decalProjector, GizmoType gizmoType)
        {
            float lod = CalculateGizmoLOD(decalProjector);

            // skip drawing anything if it will be too small or behind the camera on screen
            if (lod < 0.1f)
                return;

            UpdateColorsInHandlesIfRequired();

            const float k_DotLength = 5f;

            // Draw them with scale applied to size and pivot instead of the matrix to keep the proportions of the arrow and lines.
            using (new Handles.DrawingScope(fullColor, Matrix4x4.TRS(decalProjector.transform.position, decalProjector.transform.rotation, Vector3.one)))
            {
                Vector3 scale = decalProjector.effectiveScale;
                Vector3 scaledPivot = Vector3.Scale(decalProjector.pivot, scale);
                Vector3 scaledSize = Vector3.Scale(decalProjector.size, scale);

                boxHandle.center = scaledPivot;
                boxHandle.size = scaledSize;
                bool isVolumeEditMode = editMode == k_editShapePreservingUV || editMode == k_editShapeWithoutPreservingUV;
                bool isPivotEditMode = editMode == k_editUVAndPivot;
                if (lod > 0.5f)
                {
                    boxHandle.DrawHull(isVolumeEditMode);
                }
                else
                    Handles.DrawWireCube(scaledPivot, scaledSize); // simplify the drawing if too small on screen

                if (lod == 1.0f) // only draw when big enough on screen to be useable
                {
                    Vector3 pivot = Vector3.zero;
                    Vector3 projectedPivot = new Vector3(0, 0, scaledPivot.z - .5f * scaledSize.z);

                    if (isPivotEditMode)
                    {
                        Handles.DrawDottedLines(new[] { projectedPivot, pivot }, k_DotLength);
                    }
                    else
                    {
                        float arrowSize = scaledSize.z * 0.25f;
                        Handles.ArrowHandleCap(0, projectedPivot, Quaternion.identity, arrowSize, EventType.Repaint);
                    }

                    //draw UV and bolder edges
                    using (new Handles.DrawingScope(Matrix4x4.TRS(decalProjector.transform.position + decalProjector.transform.rotation * new Vector3(scaledPivot.x, scaledPivot.y, scaledPivot.z - .5f * scaledSize.z), decalProjector.transform.rotation, Vector3.one)))
                    {
                        Vector2 UVSize = new Vector2(
                            (decalProjector.uvScale.x > k_limit || decalProjector.uvScale.x < -k_limit) ? 0f : scaledSize.x / decalProjector.uvScale.x,
                            (decalProjector.uvScale.y > k_limit || decalProjector.uvScale.y < -k_limit) ? 0f : scaledSize.y / decalProjector.uvScale.y
                        );
                        Vector2 UVCenter = UVSize * .5f - new Vector2(decalProjector.uvBias.x * UVSize.x, decalProjector.uvBias.y * UVSize.y) - (Vector2)scaledSize * .5f;

                        uvHandles.center = UVCenter;
                        uvHandles.size = UVSize;
                        uvHandles.DrawRect(dottedLine: true, screenSpaceSize: k_DotLength);

                        uvHandles.center = default;
                        uvHandles.size = scaledSize;
                        uvHandles.DrawRect(dottedLine: false, thickness: 3f);
                    }
                }
            }
        }

        static Func<Bounds> GetBoundsGetter(DecalProjector decalProjector)
        {
            return () =>
            {
                var bounds = new Bounds();
                var decalTransform = decalProjector.transform;
                bounds.Encapsulate(decalTransform.position);
                return bounds;
            };
        }

        // Temporarily save ratio between  size and pivot position while editing in inspector.
        // null or NaN is used to say that there is no saved ratio.
        // Aim is to keep proportion while sliding the value to 0 in Inspector and then go back to something else.
        // Current solution only works for the life of this editor, but is enough in most cases.
        // Which means if you go to there, selection something else and go back on it, pivot position is thus null.
        Dictionary<DecalProjector, Vector3> ratioSizePivotPositionSaved = null;

        void ReinitSavedRatioSizePivotPosition()
        {
            ratioSizePivotPositionSaved = null;
        }

        void UpdateSize(int axe, float newSize)
        {
            void UpdateSizeOfOneTarget(DecalProjector currentTarget)
            {
                //lazy init on demand as targets array cannot be accessed from OnSceneGUI so in edit mode.
                if (ratioSizePivotPositionSaved == null)
                {
                    ratioSizePivotPositionSaved = new Dictionary<DecalProjector, Vector3>();
                    foreach (DecalProjector projector in targets)
                        ratioSizePivotPositionSaved[projector] = new Vector3(float.NaN, float.NaN, float.NaN);
                }

                // Save old ratio if not registered
                // Either or are NaN or no one, check only first
                Vector3 saved = ratioSizePivotPositionSaved[currentTarget];
                if (float.IsNaN(saved[axe]))
                {
                    float oldSize = currentTarget.sizeValue[axe];
                    saved[axe] = Mathf.Abs(oldSize) <= Mathf.Epsilon ? 0f : currentTarget.offsetValue[axe] / oldSize;
                    ratioSizePivotPositionSaved[currentTarget] = saved;
                }

                currentTarget.sizeValue[axe] = newSize;
                currentTarget.offsetValue[axe] = saved[axe] * newSize;

                // refresh DecalProjector to update projection
                currentTarget.OnValidate();
            }

            // Manually register Undo as we work directly on the target
            Undo.RecordObjects(targets, "Change DecalProjector Size or Depth");

            // Apply any change on target first
            serializedObject.ApplyModifiedProperties();

            // update each target
            foreach (DecalProjector decalProjector in targets)
            {
                UpdateSizeOfOneTarget(decalProjector);

                // Fix for UUM-29105 (Changes made to Decal Project Prefab in the Inspector are not saved)
                // This editor doesn't use serializedObject to modify the target objects, explicitly mark the prefab
                // asset dirty to ensure the new data is saved.
                if (PrefabUtility.IsPartOfPrefabAsset(decalProjector))
                    EditorUtility.SetDirty(decalProjector);
            }

            // update again serialize object to register change in targets
            serializedObject.Update();

            // change was not tracked by SerializeReference so force repaint the scene views and game views
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            // strange: we need to force it throu serialization to update multiple differente value state (value are right but still detected as different)
            if (_sizeValues[axe].hasMultipleDifferentValues)
                _sizeValues[axe].floatValue = newSize;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            bool materialChanged = false;
            bool isDefaultMaterial = false;
            bool isValidDecalMaterial = true;

            bool isDecalSupported = DecalProjector.isSupported;
            if (!isDecalSupported)
                EditorUtils.FeatureHelpBox("The current renderer has no Decal Renderer Feature added.", MessageType.Warning);

            EditorGUI.BeginChangeCheck();
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                DoInspectorToolbar(k_editVolumeModes, editVolumeLabels, GetBoundsGetter(target as DecalProjector), this);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space();

                // Info box for tools
                GUIStyle style = new GUIStyle(EditorStyles.miniLabel);
                style.richText = true;
                GUILayout.BeginVertical(EditorStyles.helpBox);
                string helpText = k_BaseSceneEditingToolText;
                if (EditMode.editMode == k_editShapeWithoutPreservingUV && EditMode.IsOwner(this))
                    helpText = k_EditShapeWithoutPreservingUVName;
                if (EditMode.editMode == k_editShapePreservingUV && EditMode.IsOwner(this))
                    helpText = k_EditShapePreservingUVName;
                if (EditMode.editMode == k_editUVAndPivot && EditMode.IsOwner(this))
                    helpText = k_EditUVAndPivotName;
                GUILayout.Label(helpText, style);
                GUILayout.EndVertical();
                EditorGUILayout.Space();

                EditorGUILayout.PropertyField(_scaleMode, k_ScaleMode);

                bool negativeScale = false;
                foreach (var target in targets)
                {
                    var decalProjector = target as DecalProjector;

                    float combinedScale = decalProjector.transform.lossyScale.x * decalProjector.transform.lossyScale.y * decalProjector.transform.lossyScale.z;
                    negativeScale |= combinedScale < 0 && decalProjector.scaleMode == DecalScaleMode.InheritFromHierarchy;
                }
                if (negativeScale)
                {
                    EditorGUILayout.HelpBox("Does not work with negative odd scaling (When there are odd number of scale components)", MessageType.Warning);
                }

                var widthRect = EditorGUILayout.GetControlRect();
                EditorGUI.BeginProperty(widthRect, k_WidthContent, _sizeValues[0]);
                EditorGUI.BeginChangeCheck();
                float newSizeX = EditorGUI.FloatField(widthRect, k_WidthContent, _sizeValues[0].floatValue);
                if (EditorGUI.EndChangeCheck())
                    UpdateSize(0, Mathf.Max(0, newSizeX));
                EditorGUI.EndProperty();

                var heightRect = EditorGUILayout.GetControlRect();
                EditorGUI.BeginProperty(heightRect, k_HeightContent, _sizeValues[1]);
                EditorGUI.BeginChangeCheck();
                float newSizeY = EditorGUI.FloatField(heightRect, k_HeightContent, _sizeValues[1].floatValue);
                if (EditorGUI.EndChangeCheck())
                    UpdateSize(1, Mathf.Max(0, newSizeY));
                EditorGUI.EndProperty();

                var projectionRect = EditorGUILayout.GetControlRect();
                EditorGUI.BeginProperty(projectionRect, k_ProjectionDepthContent, _sizeValues[2]);
                EditorGUI.BeginChangeCheck();
                float newSizeZ = EditorGUI.FloatField(projectionRect, k_ProjectionDepthContent, _sizeValues[2].floatValue);
                if (EditorGUI.EndChangeCheck())
                    UpdateSize(2, Mathf.Max(0, newSizeZ));
                EditorGUI.EndProperty();

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(_offset, k_Offset);
                if (EditorGUI.EndChangeCheck())
                    ReinitSavedRatioSizePivotPosition();

                EditorGUILayout.Space();

                EditorGUI.BeginChangeCheck();
                MaterialFieldWithButton(_materialProperty, k_MaterialContent);
                materialChanged = EditorGUI.EndChangeCheck();

                EditorUtils.DrawRenderingLayerMask(_renderingLayerMask, k_RenderingLayerMaskContent);

                foreach (var target in targets)
                {
                    var decalProjector = target as DecalProjector;
                    var mat = decalProjector.material;

                    isDefaultMaterial |= decalProjector.material == DecalProjector.defaultMaterial;
                    isValidDecalMaterial &= decalProjector.IsValid();
                }

                if (_materialEditor && !isValidDecalMaterial)
                {
                    CoreEditorUtils.DrawFixMeBox("Decal only work with Decal Material. Use default material or create from decal shader graph sub target.", () =>
                    {
                        _materialProperty.objectReferenceValue = DecalProjector.defaultMaterial;
                        materialChanged = true;
                    });
                }

                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_uvScaleProperty, k_UVScaleContent);
                EditorGUILayout.PropertyField(_uvBiasProperty, k_UVBiasContent);
                EditorGUILayout.PropertyField(_fadeFactor, k_OpacityContent);
                EditorGUI.indentLevel--;

                bool angleFadeSupport = false;
                foreach (var decalProjector in targets)
                {
                    var mat = (decalProjector as DecalProjector).material;
                    if (mat == null)
                        continue;
                    angleFadeSupport = mat.HasProperty("_DecalAngleFadeSupported");
                }

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(_drawDistanceProperty, k_DistanceContent);
                if (EditorGUI.EndChangeCheck() && _drawDistanceProperty.floatValue < 0f)
                    _drawDistanceProperty.floatValue = 0f;

                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_fadeScaleProperty, k_FadeScaleContent);
                EditorGUI.indentLevel--;

                using (new EditorGUI.DisabledScope(!angleFadeSupport))
                {
                    float angleFadeMinValue = _startAngleFadeProperty.floatValue;
                    float angleFadeMaxValue = _endAngleFadeProperty.floatValue;
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.MinMaxSlider(k_AngleFadeContent, ref angleFadeMinValue, ref angleFadeMaxValue, 0.0f, 180.0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _startAngleFadeProperty.floatValue = angleFadeMinValue;
                        _endAngleFadeProperty.floatValue = angleFadeMaxValue;
                    }
                }

                if (!angleFadeSupport && isValidDecalMaterial)
                {
                    EditorGUILayout.HelpBox($"Decal Angle Fade is not enabled in Shader. In ShaderGraph enable Angle Fade option.", MessageType.Info);
                }

                EditorGUILayout.Space();
            }
            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();

            if (materialChanged)
                UpdateMaterialEditor();

            if (layerMaskHasMultipleValues || layerMask != (target as Component).gameObject.layer)
            {
                foreach (var decalProjector in targets)
                {
                    (decalProjector as DecalProjector).OnValidate();
                }
            }

            if (_materialEditor != null)
            {
                // We need to prevent the user to edit default decal materials
                if (isValidDecalMaterial)
                {
                    using (new DecalProjectorScope())
                    {
                        using (new EditorGUI.DisabledGroupScope(isDefaultMaterial))
                        {
                            // Draw the material's foldout and the material shader field
                            // Required to call m_MaterialEditor.OnInspectorGUI ();
                            _materialEditor.DrawHeader();

                            // Draw the material properties
                            // Works only if the foldout of m_MaterialEditor.DrawHeader () is open
                            _materialEditor.OnInspectorGUI();
                        }
                    }
                }
            }
        }

        internal void MaterialFieldWithButton(SerializedProperty prop, GUIContent label)
        {
            const int k_NewFieldWidth = 70;

            var rect = EditorGUILayout.GetControlRect();
            rect.xMax -= k_NewFieldWidth + 2;

            EditorGUI.PropertyField(rect, prop, label);

            var newFieldRect = rect;
            newFieldRect.x = rect.xMax + 2;
            newFieldRect.width = k_NewFieldWidth;

            if (GUI.Button(newFieldRect, k_NewMaterialButtonText))
            {
                string materialName = k_NewDecalMaterialText + ".mat";
                var materialIcon = AssetPreview.GetMiniTypeThumbnail(typeof(Material));
                var action = ScriptableObject.CreateInstance<DoCreateDecalDefaultMaterial>();
                action.decalProjector = target as DecalProjector;
                ProjectWindowUtil.StartNameEditingIfProjectWindowExists(0, action, materialName, materialIcon, null);
            }
                
        }

        [Shortcut("UltraRP/Decal: Handle changing size stretching UV", typeof(SceneView), KeyCode.Keypad1, ShortcutModifiers.Action)]
        static void EnterEditModeWithoutPreservingUV(ShortcutArguments args)
        {
            //If editor is not there, then the selected GameObject does not contains a DecalProjector
            DecalProjector activeDecalProjector = Selection.activeGameObject?.GetComponent<DecalProjector>();
            if (activeDecalProjector == null || activeDecalProjector.Equals(null))
                return;

            ChangeEditMode(k_editShapeWithoutPreservingUV, GetBoundsGetter(activeDecalProjector)(), FindEditorFromSelection());
        }

        [Shortcut("UltraRP/Decal: Handle changing size cropping UV", typeof(SceneView), KeyCode.Keypad2, ShortcutModifiers.Action)]
        static void EnterEditModePreservingUV(ShortcutArguments args)
        {
            //If editor is not there, then the selected GameObject does not contains a DecalProjector
            DecalProjector activeDecalProjector = Selection.activeGameObject?.GetComponent<DecalProjector>();
            if (activeDecalProjector == null || activeDecalProjector.Equals(null))
                return;

            ChangeEditMode(k_editShapePreservingUV, GetBoundsGetter(activeDecalProjector)(), FindEditorFromSelection());
        }

        [Shortcut("UltraRP/Decal: Handle changing pivot position and UVs", typeof(SceneView), KeyCode.Keypad3, ShortcutModifiers.Action)]
        static void EnterEditModePivotPreservingUV(ShortcutArguments args)
        {
            //If editor is not there, then the selected GameObject does not contains a DecalProjector
            DecalProjector activeDecalProjector = Selection.activeGameObject?.GetComponent<DecalProjector>();
            if (activeDecalProjector == null || activeDecalProjector.Equals(null))
                return;

            ChangeEditMode(k_editUVAndPivot, GetBoundsGetter(activeDecalProjector)(), FindEditorFromSelection());
        }

        [Shortcut("UltraRP/Decal: Handle swap between cropping and stretching UV", typeof(SceneView), KeyCode.Keypad4, ShortcutModifiers.Action)]
        static void SwappingEditUVMode(ShortcutArguments args)
        {
            //If editor is not there, then the selected GameObject does not contains a DecalProjector
            DecalProjector activeDecalProjector = Selection.activeGameObject?.GetComponent<DecalProjector>();
            if (activeDecalProjector == null || activeDecalProjector.Equals(null))
                return;

            SceneViewEditMode targetMode = SceneViewEditMode.None;
            switch (editMode)
            {
                case k_editShapePreservingUV:
                case k_editUVAndPivot:
                    targetMode = k_editShapeWithoutPreservingUV;
                    break;
                case k_editShapeWithoutPreservingUV:
                    targetMode = k_editShapePreservingUV;
                    break;
            }
            if (targetMode != SceneViewEditMode.None)
                ChangeEditMode(targetMode, GetBoundsGetter(activeDecalProjector)(), FindEditorFromSelection());
        }

        [Shortcut("UltraRP/Decal: Stop Editing", typeof(SceneView), KeyCode.Keypad0, ShortcutModifiers.Action)]
        static void ExitEditMode(ShortcutArguments args)
        {
            //If editor is not there, then the selected GameObject does not contains a DecalProjector
            DecalProjector activeDecalProjector = Selection.activeGameObject?.GetComponent<DecalProjector>();
            if (activeDecalProjector == null || activeDecalProjector.Equals(null))
                return;

            QuitEditMode();
        }
    }

    class DoCreateDecalDefaultMaterial : UnityEditor.ProjectWindowCallback.EndNameEditAction
    {
        public DecalProjector decalProjector;
        public override void Action(int instanceId, string pathName, string resourceFile)
        {
            var shader = DecalProjector.defaultMaterial.shader;
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, pathName);
            ProjectWindowUtil.ShowCreatedAsset(material);
            decalProjector.material = material;
        }
    }
}
