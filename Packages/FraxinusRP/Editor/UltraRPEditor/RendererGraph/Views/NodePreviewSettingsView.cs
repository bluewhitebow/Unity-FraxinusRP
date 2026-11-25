using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

namespace FraxinusEditor.Rendering.Ultra
{
    public class NodePreviewSettingsView : VisualElement
    {
        public enum GraphOutputSizeMode
        {
            Absolute = NodeSettingSizeMode.Absolute,
        }

        const float k_maxSizeScale = 64;

        public const string headerStyleClass = "PropertyEditorHeader";

        ScriptableRendererGraphView     _owner;
        PassNodeSettings             _settings;
        ScriptableRendererGraph         _graph;
        string                          _title;
        bool                            _showSettingsForNode;

        Label _sizeHeader;
        Label _otherHeader;

        EnumField _sizeMode;
        EnumField _potSize;

        SliderInt       _resolutionScale;
        IntegerField    _width;
        IntegerField    _height;
        IntegerField    _volumeDepth;
        SliderInt       _volumeDepthScale;

        EnumField       _refreshMode;
        FloatField      _period;

        event Action _onChanged;

        public NodePreviewSettingsView(PassNodeSettings settings, ScriptableRendererGraphView owner, string title = "Node Settings", bool showSettingsForNode = true)
        {
            this._graph = owner.graph as ScriptableRendererGraph;
            this._settings = settings;
            this._owner = owner;
            this._title = title;
            this._showSettingsForNode = showSettingsForNode;

            var stylesheet = Resources.Load<StyleSheet>("UIElementRes/RendererGraphCommon");
            styleSheets.Add(stylesheet);
            ReloadSettingsView();
        }

        public void RegisterChangedCallback(Action callback) => _onChanged += callback;

        void ReloadSettingsView()
        {
            // Remove all old fields
            Clear();

            if (_title != null)
            {
                var titleLabel = new Label(_title);
                titleLabel.AddToClassList("PropertyEditorTitle");
                this.Add(titleLabel);
            }

            // TODO: form node previewTexture
            var dimension = TextureDimension.Tex2D;

            // Size Modes
            _sizeHeader = new Label("Size Properties");
            _sizeHeader.AddToClassList(headerStyleClass);
            this.Add(_sizeHeader);

            _sizeMode = _showSettingsForNode ? new EnumField(_settings.sizeMode) : new EnumField((GraphOutputSizeMode)_settings.sizeMode);
            _sizeMode.label = "Size Mode";
            _sizeMode.RegisterValueChangedCallback((EventCallback<ChangeEvent<Enum>>)(e => {
                _owner.RegisterCompleteObjectUndo("Updated Size mode " + e.newValue);
                _settings.sizeMode = (NodeSettingSizeMode)e.newValue;
                _onChanged?.Invoke();
                ReloadSettingsView();
                UpdateFieldVisibility(_settings);
            }));
            this.Add(_sizeMode);

            _potSize = new EnumField(_settings.potSize)
            {
                value = _settings.potSize,
                label = "Resolution",
            };

            _potSize.RegisterValueChangedCallback(e =>
            {
                _owner.RegisterCompleteObjectUndo("Updated Size " + e.newValue);
                var size = (POTSize)e.newValue;
                _settings.potSize = size;

                if (size != POTSize.Custom)
                {
                    _settings.width = (int)size;
                    _settings.height = (int)size;
                    _settings.volumeDepth = (int)size;
                }
                else
                {
                    _settings.width = _width.value;
                    _settings.height =_height.value;
                    if (_volumeDepth != null)
                        _settings.volumeDepth = _volumeDepth.value;
                }

                _onChanged?.Invoke();
                ReloadSettingsView();
                UpdateFieldVisibility(_settings);
            });

            this.Add(_potSize);

            _resolutionScale = CreateSizeScaleSlider(_settings.resolutionScale, (v) => _settings.resolutionScale = v, "Resolution Scale");
            this.Add(_resolutionScale);

            _width = new IntegerField()
            {
                value = _settings.width,
                label = "Width",
                isDelayed = true,
            };

            _width.RegisterValueChangedCallback(e =>
            {
                _owner.RegisterCompleteObjectUndo("Updated Width " + e.newValue);
                _settings.width = e.newValue;
                _onChanged?.Invoke();
            });
            this.Add(_width);

            if (dimension != TextureDimension.Cube)
            {
                _height = new IntegerField()
                {
                    value = _settings.height,
                    label = "Height",
                    isDelayed = true,
                };

                _height.RegisterValueChangedCallback(e =>
                {
                    _owner.RegisterCompleteObjectUndo("Updated Height " + e.newValue);
                    _settings.height = e.newValue;
                    _onChanged?.Invoke();
                });
                this.Add(_height);

                if (dimension == TextureDimension.Tex3D)
                {
                    _volumeDepth = new IntegerField()
                    {
                        value = _settings.volumeDepth,
                        label = "Depth",
                        isDelayed = true,
                    };

                    _volumeDepth.RegisterValueChangedCallback(e =>
                    {
                        _owner.RegisterCompleteObjectUndo("Updated Depth " + e.newValue);
                        _settings.volumeDepth = e.newValue;
                        _onChanged?.Invoke();
                    });
                    this.Add(_volumeDepth);

                    _volumeDepthScale = CreateSizeScaleSlider(_settings.volumeDepthScale, v => _settings.volumeDepthScale = v, "Depth Scale");
                    this.Add(_volumeDepthScale);
                }
            }

            SliderInt CreateSizeScaleSlider(float defaultValue, Action<float> setter, string propertyName)
            {
                var slider = new SliderInt(0, (int)(Mathf.Log(k_maxSizeScale, 2) * 2))
                {
                    value = SizeScaleToInt(_settings.heightScale),
                    label = propertyName,
                };
                slider.RegisterValueChangedCallback(e =>
                {
                    _owner.RegisterCompleteObjectUndo(propertyName + " " + e.newValue);
                    setter(IntToSizeScale(e.newValue));
                    _onChanged?.Invoke();
                });

                return slider;
            }

            UpdateFieldVisibility(_settings);
        }

        void UpdateFieldVisibility(PassNodeSettings settings)
        {
            SetVisible(_sizeHeader, settings.CanEdit(EditFlags.Size));
            SetVisible(_sizeMode, settings.CanEdit(EditFlags.SizeMode));
            SetVisible(_potSize, settings.CanEdit(EditFlags.POTSize) && settings.sizeMode == NodeSettingSizeMode.Absolute);
            SetVisible(_resolutionScale, settings.CanEdit(EditFlags.Width | EditFlags.Height) && settings.sizeMode != NodeSettingSizeMode.Absolute);
            SetVisible(_width, settings.CanEdit(EditFlags.Width) && settings.sizeMode == NodeSettingSizeMode.Absolute && (settings.potSize == POTSize.Custom || !settings.CanEdit(EditFlags.POTSize)));
            SetVisible(_height, settings.CanEdit(EditFlags.Height) && settings.sizeMode == NodeSettingSizeMode.Absolute && (settings.potSize == POTSize.Custom || !settings.CanEdit(EditFlags.POTSize)));
            SetVisible(_volumeDepth, settings.CanEdit(EditFlags.Depth) && settings.sizeMode == NodeSettingSizeMode.Absolute && (settings.potSize == POTSize.Custom || !settings.CanEdit(EditFlags.POTSize)));
            SetVisible(_volumeDepthScale, settings.CanEdit(EditFlags.Depth) && settings.sizeMode != NodeSettingSizeMode.Absolute);
        }

        void SetVisible(VisualElement element, bool visible)
        {
            if (element == null)
                return;

            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        int SizeScaleToInt(float scale)
        {
            if (scale <= 0)
                scale = 1;

            scale *= k_maxSizeScale;
            return (int)Mathf.Log(scale, 2);
        }

        float IntToSizeScale(int i)
        {
            float f = Mathf.Pow(2, i);
            return f / k_maxSizeScale;
        }

        public void RefreshSettingsValues()
        {
            _sizeMode?.SetValueWithoutNotify(_settings.sizeMode);
            _potSize?.SetValueWithoutNotify(_settings.potSize);

            _resolutionScale?.SetValueWithoutNotify(SizeScaleToInt(_settings.resolutionScale));
            _width?.SetValueWithoutNotify(_settings.width);
            _height?.SetValueWithoutNotify(_settings.height);
            _volumeDepth?.SetValueWithoutNotify(_settings.volumeDepth);
            _volumeDepthScale?.SetValueWithoutNotify(SizeScaleToInt(_settings.volumeDepthScale));

        }
    }
}
