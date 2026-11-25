using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;

using UnityEditor;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    public class ExposedParameterView : PinnedElementView
    {
        protected BaseGraphView _graphView;

        const string k_title = "Parameters";
        
        readonly string _exposedParameterViewStyle = "GraphProcessorStyles/ExposedParameterView";

        List<Rect> _blackboardLayouts = new List<Rect>();

        public ExposedParameterView()
        {
            var style = Resources.Load<StyleSheet>(_exposedParameterViewStyle);
            if (style != null)
                styleSheets.Add(style);
        }

        protected virtual void OnAddClicked()
        {
            var parameterType = new GenericMenu();

            foreach (var paramType in GetExposedParameterTypes())
                parameterType.AddItem(new GUIContent(GetNiceNameFromType(paramType)), false, () =>
                {
                    string uniqueName = "New " + GetNiceNameFromType(paramType);

                    uniqueName = GetUniqueExposedPropertyName(uniqueName);
                    _graphView.graph.AddExposedParameter(uniqueName, paramType);
                });

            parameterType.ShowAsContext();
        }

        protected string GetNiceNameFromType(Type type)
        {
            string name = type.Name;

            // Remove parameter in the name of the type if it exists
            name = name.Replace("Parameter", "");

            return ObjectNames.NicifyVariableName(name);
        }

        protected string GetUniqueExposedPropertyName(string name)
        {
            // Generate unique name
            string uniqueName = name;
            int i = 0;
            while (_graphView.graph.exposedParameters.Any(e => e.name == name))
                name = uniqueName + " " + i++;
            return name;
        }

        protected virtual IEnumerable<Type> GetExposedParameterTypes()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<ExposedParameter>())
            {
                if (type.IsGenericType)
                    continue ;

                yield return type;
            }
        }

        protected virtual void UpdateParameterList()
        {
            _content.Clear();

            foreach (var param in _graphView.graph.exposedParameters)
            {
                var row = new BlackboardRow(new ExposedParameterFieldView(_graphView, param), new ExposedParameterPropertyView(_graphView, param));
                row.expanded = param.settings.expanded;
                row.RegisterCallback<GeometryChangedEvent>(e => 
                {
                    param.settings.expanded = row.expanded;
                });

                _content.Add(row);
            }
        }

        protected override void Initialize(BaseGraphView graphView)
        {
            this._graphView = graphView;
            base.title = k_title;
            scrollable = true;

            graphView.onExposedParameterListChanged += UpdateParameterList;
            graphView.initialized += UpdateParameterList;
            Undo.undoRedoPerformed += UpdateParameterList;

            RegisterCallback<DragUpdatedEvent>(OnDragUpdatedEvent);
            RegisterCallback<DragPerformEvent>(OnDragPerformEvent);
            RegisterCallback<MouseDownEvent>(OnMouseDownEvent, TrickleDown.TrickleDown);
            RegisterCallback<DetachFromPanelEvent>(OnViewClosed);

            UpdateParameterList();

            // Add exposed parameter button
            _header.Add(new Button(OnAddClicked){
                text = "+"
            });
        }

        void OnViewClosed(DetachFromPanelEvent evt)
            => Dispose();

        protected override void Destroy()
        {
            Undo.undoRedoPerformed -= UpdateParameterList;
            if (_graphView != null)
            {
                _graphView.onExposedParameterListChanged -= UpdateParameterList;
                _graphView.initialized -= UpdateParameterList;
            }
            base.Destroy();
        }

        void OnMouseDownEvent(MouseDownEvent evt)
        {
            _blackboardLayouts = _content.Children().Select(c => c.layout).ToList();
        }

        int GetInsertIndexFromMousePosition(Vector2 pos)
        {
            pos = _content.WorldToLocal(pos);
            // We only need to look for y axis;
            float mousePos = pos.y;

            if (mousePos < 0)
                return 0;

            int index = 0;
            foreach (var layout in _blackboardLayouts)
            {
                if (mousePos > layout.yMin && mousePos < layout.yMax)
                    return index + 1;
                index++;
            }

            return _content.childCount;
        }

        void OnDragUpdatedEvent(DragUpdatedEvent evt)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            int newIndex = GetInsertIndexFromMousePosition(evt.mousePosition);
            var graphSelectionDragData = DragAndDrop.GetGenericData("DragSelection");

            if (graphSelectionDragData == null)
                return;

            foreach (var obj in graphSelectionDragData as List<ISelectable>)
            {
                if (obj is ExposedParameterFieldView view)
                {
                    var blackBoardRow = view.parent.parent.parent.parent.parent.parent;
                    int oldIndex = _content.Children().ToList().FindIndex(c => c == blackBoardRow);
                    // Try to find the blackboard row
                    _content.Remove(blackBoardRow);

                    if (newIndex > oldIndex)
                        newIndex--;

                    _content.Insert(newIndex, blackBoardRow);
                }
            }
        }

        void OnDragPerformEvent(DragPerformEvent evt)
        {
            bool updateList = false;

            int newIndex = GetInsertIndexFromMousePosition(evt.mousePosition);
            foreach (var obj in DragAndDrop.GetGenericData("DragSelection") as List<ISelectable>)
            {
                if (obj is ExposedParameterFieldView view)
                {
                    if (!updateList)
                        _graphView.RegisterCompleteObjectUndo("Moved parameters");

                    int oldIndex = _graphView.graph.exposedParameters.FindIndex(e => e == view.parameter);
                    var parameter = _graphView.graph.exposedParameters[oldIndex];
                    _graphView.graph.exposedParameters.RemoveAt(oldIndex);

                    // Patch new index after the remove operation:
                    if (newIndex > oldIndex)
                        newIndex--;

                    _graphView.graph.exposedParameters.Insert(newIndex, parameter);

                    updateList = true;
                }
            }

            if (updateList)
            {
                _graphView.graph.NotifyExposedParameterListChanged();
                evt.StopImmediatePropagation();
                UpdateParameterList();
            }
        }
    }
}
