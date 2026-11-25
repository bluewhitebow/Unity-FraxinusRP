using System.Linq;
using UnityEngine;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    public class RenderPassNodeInspectorSettingsPopupWindow : PopupWindowContent
    {
        RenderPassNodeInspectorObjectEditor _inspector;
        RenderPassNodeInspectorObject _target;

        public static readonly int width = 260;
        public static readonly int height = 230;

        public RenderPassNodeInspectorSettingsPopupWindow(RenderPassNodeInspectorObjectEditor inspector)
        {
            this._inspector = inspector;
            this._target = inspector.nodeInspectorObject;
        }

        public override void OnGUI(Rect rect)
        {
            EditorGUI.BeginChangeCheck();

            EditorGUIUtility.labelWidth = 90;

            EditorGUILayout.BeginHorizontal();
            bool r = GUILayout.Toggle((_target.channels & PreviewChannels.R) != 0, "R", EditorStyles.toolbarButton);
            bool g = GUILayout.Toggle((_target.channels & PreviewChannels.G) != 0, "G", EditorStyles.toolbarButton);
            bool b = GUILayout.Toggle((_target.channels & PreviewChannels.B) != 0, "B", EditorStyles.toolbarButton);
            bool a = GUILayout.Toggle((_target.channels & PreviewChannels.A) != 0, "A", EditorStyles.toolbarButton);
            _target.channels = (r ? PreviewChannels.R : 0) |
                (g ? PreviewChannels.G : 0) |
                (b ? PreviewChannels.B : 0) |
                (a ? PreviewChannels.A : 0);
            EditorGUILayout.EndHorizontal();

            var passNode = _inspector.nodeWithPreviews.FirstOrDefault();
            int maxMip = passNode != null ? passNode.previewTexture.mipmapCount : 1;
            EditorGUI.BeginDisabledGroup(maxMip == 1);
            _target.mipLevel = EditorGUILayout.Slider("Mip Level", _target.mipLevel, 0, maxMip - 1);
            EditorGUI.EndDisabledGroup();

            _target.alwaysRefresh = EditorGUILayout.Toggle("Always Refresh", _target.alwaysRefresh);
            _target.preserveAspect = EditorGUILayout.Toggle("Keep Aspect", _target.preserveAspect);

            if (passNode != null && passNode.previewTexture != null)
            {
                var dim = passNode.previewTexture.dimension;
                if (dim == TextureDimension.Tex3D)
                {
                    EditorGUILayout.LabelField("3D View", EditorStyles.boldLabel);

                }
                else if (dim == TextureDimension.Cube)
                {
                    EditorGUILayout.LabelField("Cubemap View", EditorStyles.boldLabel);
                }
            }

            if (EditorGUI.EndChangeCheck())
                _inspector.Repaint();
        }
    }
}
