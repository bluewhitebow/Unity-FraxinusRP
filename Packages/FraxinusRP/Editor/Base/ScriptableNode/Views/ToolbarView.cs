using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

using Status = UnityEngine.UIElements.DropdownMenuAction.Status;

namespace FraxinusEditor.Base
{
    public class ToolbarView : VisualElement
    {
        protected enum ElementType
        {
            Button,
            Toggle,
            DropDownButton,
            Separator,
            Custom,
            FlexibleSpace,
        }

        protected class ToolbarButtonData
        {
            public GUIContent   content;
            public ElementType  type;
            public bool         value;
            public bool         visible = true;
            public Action       buttonCallback;
            public Action<bool> toggleCallback;
            public int          size;
            public Action       customDrawFunction;
        }

        List<ToolbarButtonData> _leftButtonDatas = new List<ToolbarButtonData>();
        List<ToolbarButtonData> _rightButtonDatas = new List<ToolbarButtonData>();
        protected BaseGraphView _graphView;
        
        ToolbarButtonData _showProcessor;
        ToolbarButtonData _showParameters;

        public ToolbarView(BaseGraphView graphView)
        {
            name = "ToolbarView";
            this._graphView = graphView;

            graphView.initialized += () => 
            {
                _leftButtonDatas.Clear();
                _rightButtonDatas.Clear();
                AddButtons();
            };

            Add(new IMGUIContainer(DrawImGUIToolbar));
        }

        protected ToolbarButtonData AddButton(string name, Action callback, bool left = true)
            => AddButton(new GUIContent(name), callback, left);

        protected ToolbarButtonData AddButton(GUIContent content, Action callback, bool left = true)
        {
            var data = new ToolbarButtonData
            {
                content = content,
                type = ElementType.Button,
                buttonCallback = callback
            };

            ((left) ? _leftButtonDatas : _rightButtonDatas).Add(data);
            return data;
        }

        protected void AddSeparator(int sizeInPixels = 10, bool left = true)
        {
            var data = new ToolbarButtonData
            {
                type = ElementType.Separator,
                size = sizeInPixels,
            };
            ((left) ? _leftButtonDatas : _rightButtonDatas).Add(data);
        }

        protected void AddCustom(Action imguiDrawFunction, bool left = true)
        {
            if (imguiDrawFunction == null)
                throw new ArgumentException("imguiDrawFunction can't be null");

            var data = new ToolbarButtonData{
                type = ElementType.Custom,
                customDrawFunction = imguiDrawFunction,
            };
            ((left) ? _leftButtonDatas : _rightButtonDatas).Add(data);
        }

        protected void AddFlexibleSpace(bool left = true)
        {
            ((left) ? _leftButtonDatas : _rightButtonDatas).Add(new ToolbarButtonData{ type = ElementType.FlexibleSpace });
        }

        protected ToolbarButtonData AddToggle(string name, bool defaultValue, Action<bool> callback, bool left = true)
            => AddToggle(new GUIContent(name), defaultValue, callback, left);

        protected ToolbarButtonData AddToggle(GUIContent content, bool defaultValue, Action<bool> callback, bool left = true)
        {
            var data = new ToolbarButtonData
            {
                content = content,
                type = ElementType.Toggle,
                value = defaultValue,
                toggleCallback = callback
            };
            ((left) ? _leftButtonDatas : _rightButtonDatas).Add(data);
            return data;
        }

        protected ToolbarButtonData AddDropDownButton(string name, Action callback, bool left = true)
            => AddDropDownButton(new GUIContent(name), callback, left);

        protected ToolbarButtonData AddDropDownButton(GUIContent content, Action callback, bool left = true)
        {
            var data = new ToolbarButtonData
            {
                content = content,
                type = ElementType.DropDownButton,
                buttonCallback = callback
            };
            ((left) ? _leftButtonDatas : _rightButtonDatas).Add(data);
            return data;
        }

        /// <summary>
        /// Also works for toggles
        /// </summary>
        /// <param name="name"></param>
        /// <param name="left"></param>
        protected void RemoveButton(string name, bool left)
        {
            ((left) ? _leftButtonDatas : _rightButtonDatas).RemoveAll(b => b.content.text == name);
        }
        
        /// <summary>
        /// Hide the button
        /// </summary>
        /// <param name="name">Display name of the button</param>
        protected void HideButton(string name)
        {
            _leftButtonDatas.Concat(_rightButtonDatas).All(b => 
            {
                if (b?.content?.text == name)
                    b.visible = false;

                return true;
            });
        }

        /// <summary>
        /// Show the button
        /// </summary>
        /// <param name="name">Display name of the button</param>
        protected void ShowButton(string name)
        {
            _leftButtonDatas.Concat(_rightButtonDatas).All(b =>
            {
                if (b?.content?.text == name)
                    b.visible = true;

                return true;
            });
        }

        protected virtual void AddButtons()
        {
            AddButton("Center", _graphView.ResetPositionAndZoom);

            bool processorVisible = _graphView.GetPinnedElementStatus<ProcessorView>() != Status.Hidden;
            _showProcessor = AddToggle("Show Processor", processorVisible, (v) => _graphView.ToggleView<ProcessorView>());
            bool exposedParamsVisible = _graphView.GetPinnedElementStatus<ExposedParameterView>() != Status.Hidden;
            _showParameters = AddToggle("Show Parameters", exposedParamsVisible, (v) => _graphView.ToggleView<ExposedParameterView>());

            AddButton("Show In Project", () => EditorGUIUtility.PingObject(_graphView.graph), false);
        }

        public virtual void UpdateButtonStatus()
        {
            if (_showProcessor != null)
                _showProcessor.value = _graphView.GetPinnedElementStatus<ProcessorView>() != Status.Hidden;

            if (_showParameters != null)
                _showParameters.value = _graphView.GetPinnedElementStatus<ExposedParameterView>() != Status.Hidden;
        }

        void DrawImGUIButtonList(List<ToolbarButtonData > buttons)
        {
            foreach (var button in buttons.ToList())
            {
                if (!button.visible)
                    continue;

                switch (button.type)
                {
                    case ElementType.Button:
                        if (GUILayout.Button(button.content, EditorStyles.toolbarButton) && button.buttonCallback != null)
                            button.buttonCallback();
                        break;
                    case ElementType.Toggle:
                        EditorGUI.BeginChangeCheck();
                        button.value = GUILayout.Toggle(button.value, button.content, EditorStyles.toolbarButton);
                        if (EditorGUI.EndChangeCheck() && button.toggleCallback != null)
                            button.toggleCallback(button.value);
                        break;
                    case ElementType.DropDownButton:
                        if (EditorGUILayout.DropdownButton(button.content, FocusType.Passive, EditorStyles.toolbarDropDown))
                            button.buttonCallback();
                        break;
                    case ElementType.Separator:
                        EditorGUILayout.Separator();
                        EditorGUILayout.Space(button.size);
                        break;
                    case ElementType.Custom:
                        button.customDrawFunction();
                        break;
                    case ElementType.FlexibleSpace:
                        GUILayout.FlexibleSpace();
                        break;
                }
            }
        }

        protected virtual void DrawImGUIToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            DrawImGUIButtonList(_leftButtonDatas);

            GUILayout.FlexibleSpace();

            DrawImGUIButtonList(_rightButtonDatas);

            GUILayout.EndHorizontal();
        }
    }
}
