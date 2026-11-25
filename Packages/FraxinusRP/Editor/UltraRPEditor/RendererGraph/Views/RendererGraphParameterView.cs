using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;

using UnityEditor.Experimental.GraphView;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    public class RendererGraphParameterView : ExposedParameterView
    {
        static readonly string k_rendererGraphParameterStyleSheet = "UIElementRes/RendererGraphParameterView";

        public RendererGraphParameterView()
        {
            var style = Resources.Load<StyleSheet>(k_rendererGraphParameterStyleSheet);
            if (style != null)
                styleSheets.Add(style);
        }

        protected override IEnumerable<Type> GetExposedParameterTypes()
        {
            yield return typeof(BoolParameter);
            yield return typeof(IntParameter);
            yield return typeof(FloatParameter);
            yield return typeof(Vector2Parameter);
            yield return typeof(Vector3Parameter);
            yield return typeof(Vector4Parameter);
            yield return typeof(ColorParameter);
            yield return typeof(GradientParameter);
            yield return typeof(AnimationCurveParameter);
            yield return typeof(StringParameter);
            yield return typeof(TextureParameter);
            yield return typeof(Texture2DParameter);
            yield return typeof(Texture3DParameter);
            yield return typeof(CubemapParameter);
            yield return typeof(RenderTextureParameter);
            yield return typeof(ComputeBufferParameter);
        }

        protected override void UpdateParameterList()
        {
            _content.Clear();

            foreach (var param in _graphView.graph.exposedParameters)
            {
                var row = new BlackboardRow(new ExposedParameterFieldView(_graphView, param), new RendererGraphParameterPropertyView(_graphView, param));
                row.expanded = param.settings.expanded;
                row.RegisterCallback<GeometryChangedEvent>(e => 
                {
                    param.settings.expanded = row.expanded;
                });

                _content.Add(row);
            }
        }
    }
}