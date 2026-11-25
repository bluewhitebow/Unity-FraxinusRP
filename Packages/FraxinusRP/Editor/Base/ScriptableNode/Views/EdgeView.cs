using System;
using UnityEngine.UIElements;
using UnityEngine;
using FraxinusEngine.Base;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    public class EdgeView : Edge
    {
        public bool isConnected = false;
        public SerializableEdge serializedEdge { get { return userData as SerializableEdge; } }

        readonly string _edgeStyle = "GraphProcessorStyles/EdgeView";
        protected BaseGraphView owner => ((input ?? output) as PortView).owner.owner;

        public EdgeView() : base()
        {
            styleSheets.Add(Resources.Load<StyleSheet>(_edgeStyle));
            RegisterCallback<MouseDownEvent>(OnMouseDown);
        }

        protected override EdgeControl CreateEdgeControl()
        {
            return new GradientEdgeControl();
        }

        public override void OnPortChanged(bool isInput)
        {
            base.OnPortChanged(isInput);
            UpdateEdgeSize();
        }

        public void UpdateEdgeSize()
        {
            if (input == null && output == null)
                return;

            PortData inputPortData = (input as PortView)?.portData;
            PortData outputPortData = (output as PortView)?.portData;

            for (int i = 1; i < 20; i++)
                RemoveFromClassList($"edge_{i}");
            int maxPortSize = Mathf.Max(inputPortData?.sizeInPixel ?? 0, outputPortData?.sizeInPixel ?? 0);
            if (maxPortSize > 0)
                AddToClassList($"edge_{Mathf.Max(1, maxPortSize - 6)}");
        }

        protected override void OnCustomStyleResolved(ICustomStyle styles)
        {
            base.OnCustomStyleResolved(styles);
            UpdateEdgeControl();
        }

        void OnMouseDown(MouseDownEvent e)
        {
            if (e.clickCount == 2)
            {
                var position = e.mousePosition;
                position += new Vector2(-10f, -28);
                Vector2 mousePos = owner.ChangeCoordinatesTo(owner.contentViewContainer, position);
            }
        }
    }

    public class GradientEdgeControl : EdgeControl
    {
        private const int GRADIENT_SEGMENTS = 48;
        private const float GRADIENT_THRESHOLD = 0.05f;

        private Color _renderInputColor;
        private Color _renderOutputColor;
        private bool _useGradient;

        public GradientEdgeControl()
        {
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
            generateVisualContent += OnGenerateVisualContent;
        }

        private void OnCustomStyleResolved(CustomStyleResolvedEvent evt)
        {
            _renderInputColor = inputColor;
            _renderOutputColor = outputColor;
            _useGradient = NeedsGradient(_renderInputColor, _renderOutputColor);
            
            if (_useGradient)
            {
                base.inputColor = Color.clear;
                base.outputColor = Color.clear;
            }
        }

        public new Color inputColor
        {
            get => _renderInputColor;
            set
            {
                _renderInputColor = value;
                _useGradient = NeedsGradient(_renderInputColor, _renderOutputColor);
                base.inputColor = _useGradient ? Color.clear : value;
            }
        }

        public new Color outputColor
        {
            get => _renderOutputColor;
            set
            {
                _renderOutputColor = value;
                _useGradient = NeedsGradient(_renderInputColor, _renderOutputColor);
                base.outputColor = _useGradient ? Color.clear : value;
            }
        }

        private void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            if (!_useGradient || controlPoints == null || controlPoints.Length == 0)
                return;

            var painter = mgc.painter2D;
            painter.lineWidth = edgeWidth;
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;

            for (int i = 0; i < GRADIENT_SEGMENTS; i++)
            {
                float t0 = i / (float)GRADIENT_SEGMENTS;
                float t1 = (i + 1) / (float)GRADIENT_SEGMENTS;

                Vector2 p0 = EvaluateBezier(t0);
                Vector2 p1 = EvaluateBezier(t1);

                Color color0 = Color.Lerp(_renderOutputColor, _renderInputColor, t0);
                Color color1 = Color.Lerp(_renderOutputColor, _renderInputColor, t1);

                painter.strokeGradient = new Gradient
                {
                    colorKeys = new GradientColorKey[]
                    {
                        new GradientColorKey(color0, 0f),
                        new GradientColorKey(color1, 1f)
                    },
                    alphaKeys = new GradientAlphaKey[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(1f, 1f)
                    }
                };

                painter.BeginPath();
                painter.MoveTo(p0);
                painter.LineTo(p1);
                painter.Stroke();
            }
        }

        private bool NeedsGradient(Color input, Color output)
        {
            float colorDiff = Mathf.Abs(input.r - output.r) +
                             Mathf.Abs(input.g - output.g) +
                             Mathf.Abs(input.b - output.b);
            return colorDiff > GRADIENT_THRESHOLD;
        }

        private Vector2 EvaluateBezier(float t)
        {
            if (controlPoints.Length == 2)
            {
                return Vector2.Lerp(from, to, t);
            }
            else if (controlPoints.Length == 4)
            {
                float u = 1 - t;
                float tt = t * t;
                float uu = u * u;
                float uuu = uu * u;
                float ttt = tt * t;

                Vector2 p = uuu * from;
                p += 3 * uu * t * controlPoints[0];
                p += 3 * u * tt * controlPoints[1];
                p += ttt * to;

                return p;
            }

            return from;
        }
    }
}
