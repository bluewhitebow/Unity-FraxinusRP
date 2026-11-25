using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;

using UnityEditor;
using UnityEditor.UIElements;

namespace FraxinusEditor.Base
{
    public class ExposedParameterFieldFactory : IDisposable
    {
        BaseGraph _graph;
        [SerializeField]
        ExposedParameterWorkaround  _exposedParameterObject;
        SerializedObject            _serializedObject;
        SerializedProperty          _serializedParameters;

        Dictionary<ExposedParameter, object> _oldParameterValues = new Dictionary<ExposedParameter, object>();
        Dictionary<ExposedParameter, ExposedParameter.Settings> _oldParameterSettings = new Dictionary<ExposedParameter, ExposedParameter.Settings>();

        public ExposedParameterFieldFactory(BaseGraph graph, List<ExposedParameter> customParameters = null)
        {
            this._graph = graph;

            _exposedParameterObject = ScriptableObject.CreateInstance<ExposedParameterWorkaround>();
            _exposedParameterObject.graph = graph;
            _exposedParameterObject.hideFlags = HideFlags.HideAndDontSave ^ HideFlags.NotEditable;
            _serializedObject = new SerializedObject(_exposedParameterObject);
            UpdateSerializedProperties(customParameters);
        }

        public void UpdateSerializedProperties(List<ExposedParameter> parameters = null)
        {
            if (parameters != null)
                _exposedParameterObject.parameters = parameters;
            else
                _exposedParameterObject.parameters = _graph.exposedParameters;
            _serializedObject.Update();
            _serializedParameters = _serializedObject.FindProperty(nameof(ExposedParameterWorkaround.parameters));
        }

        public VisualElement GetParameterValueField(ExposedParameter parameter, Action<object> valueChangedCallback)
        {
            _serializedObject.Update();
            int propIndex = FindPropertyIndex(parameter);
            var field = new PropertyField(_serializedParameters.GetArrayElementAtIndex(propIndex));
            field.Bind(_serializedObject);

            VisualElement view = new VisualElement();
            view.Add(field);

            _oldParameterValues[parameter] = parameter.value;
            view.Add(new IMGUIContainer(() =>
            {
                if (_oldParameterValues.TryGetValue(parameter, out var value))
                {
                    if (parameter.value != null && !parameter.value.Equals(value))
                        valueChangedCallback(parameter.value);
                }
                _oldParameterValues[parameter] = parameter.value;
            }));

            // Disallow picking scene objects when the graph is not linked to a scene
            if (!this._graph.IsLinkedToScene())
            {
                var objectField = view.Q<ObjectField>();
                if (objectField != null)
                    objectField.allowSceneObjects = false;
            }
            return view;
        }

        public VisualElement GetParameterSettingsField(ExposedParameter parameter, Action<object> valueChangedCallback)
        {
            _serializedObject.Update();
            int propIndex = FindPropertyIndex(parameter);
            var serializedParameter = _serializedParameters.GetArrayElementAtIndex(propIndex);
            serializedParameter.managedReferenceValue = _exposedParameterObject.parameters[propIndex];
            var serializedSettings = serializedParameter.FindPropertyRelative(nameof(ExposedParameter.settings));
            serializedSettings.managedReferenceValue = _exposedParameterObject.parameters[propIndex].settings;
            var settingsField = new PropertyField(serializedSettings);
            settingsField.Bind(_serializedObject);

            VisualElement view = new VisualElement();
            view.Add(settingsField);

            // TODO: see if we can replace this with an event
            _oldParameterSettings[parameter] = parameter.settings;
            view.Add(new IMGUIContainer(() =>
            {
                if (_oldParameterSettings.TryGetValue(parameter, out var settings))
                {
                    if (!settings.Equals(parameter.settings))
                        valueChangedCallback(parameter.settings);
                }
                _oldParameterSettings[parameter] = parameter.settings;
            }));

            return view;
        }

        public void ResetOldParameter(ExposedParameter parameter)
        {
            _oldParameterValues.Remove(parameter);
            _oldParameterSettings.Remove(parameter);
        }

        int FindPropertyIndex(ExposedParameter param) => _exposedParameterObject.parameters.FindIndex(p => p == param);

        public void Dispose()
        {
            GameObject.DestroyImmediate(_exposedParameterObject);
        }
    }
}