using System.IO;
using System.Linq;
using UnityEngine;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEngine.UIElements;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    public static class RendererGraphEditorUtils
    {
        static Texture2D s_pinIcon;
        public static Texture2D pinIcon
        {
            get => s_pinIcon == null ? s_pinIcon = LoadIcon("UIElementRes/Icons/Pin") : s_pinIcon;
        }

        static Texture2D s_unpinIcon;
        public static Texture2D unpinIcon
        {
            get => s_unpinIcon == null ? s_unpinIcon = LoadIcon("UIElementRes/Icons/Unpin") : s_unpinIcon;
        }

        static Texture2D s_fitIcon;
        public static Texture2D fitIcon
        {
            get => s_fitIcon == null ? s_fitIcon = LoadIcon("UIElementRes/Icons/Fit") : s_fitIcon;
        }

        static Texture2D s_compareIcon;
        public static Texture2D compareIcon
        {
            get => s_compareIcon == null ? s_compareIcon = LoadIcon("UIElementRes/Icons/CompareImages") : s_compareIcon;
        }

        static Texture2D s_lockOpen;
        public static Texture2D lockOpen
        {
            get => s_lockOpen == null ? s_lockOpen = LoadIcon("UIElementRes/Icons/LockOpened") : s_lockOpen;
        }

        static Texture2D s_lockClose;
        public static Texture2D lockClose
        {
            get => s_lockClose == null ? s_lockClose = LoadIcon("UIElementRes/Icons/LockClosed") : s_lockClose;
        }


        static Texture2D s_settings;
        public static Texture2D settingsIcon
        {
            get => s_settings == null ? s_settings = LoadIcon("UIElementRes/Icons/Settings") : s_settings;
        }

        static Texture2D s_settings24;
        public static Texture2D settingsIcon24
        {
            get => s_settings24 == null ? s_settings24 = LoadIcon("UIElementRes/Icons/Settings24") : s_settings24;
        }

        static Texture2D LoadIcon(string resourceName)
        {
            if (UnityEditorInternal.InternalEditorUtility.HasPro())
            {
                string darkIconPath = Path.GetDirectoryName(resourceName) + "/d_" + Path.GetFileName(resourceName);
                var darkIcon = Resources.Load<Texture2D>(darkIconPath);
                if (darkIcon != null)
                    return darkIcon;
            }

            return Resources.Load<Texture2D>(resourceName);
        }

        static Material s_texture2DPreviewMaterial;
        public static Material texture2DPreviewMaterial
        {
            get
            {
                if (s_texture2DPreviewMaterial == null)
                    s_texture2DPreviewMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Editor/RendererGraphTexture2DPreview"));

                return s_texture2DPreviewMaterial;
            }
        }

        public static Vector4 GetChannelMask(PreviewChannels channels)
        {
            return new Vector4(
                (channels & PreviewChannels.R) == 0 ? 0 : 1,
                (channels & PreviewChannels.G) == 0 ? 0 : 1,
                (channels & PreviewChannels.B) == 0 ? 0 : 1,
                (channels & PreviewChannels.A) == 0 ? 0 : 1
                );
        }

        public static ScriptableRendererGraph GetGraphAtPath(string path)
          => AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(o => o is ScriptableRendererGraph) as ScriptableRendererGraph;

        public static IVisualElementScheduledItem ScheduleAutoHide(VisualElement target, ScriptableRendererGraphView view)
        {
            return target.schedule.Execute(() =>
            {
                target.visible = float.IsNaN(target.worldBound.x) || target.worldBound.Overlaps(view.worldBound);
            }).Every(16);
        }

        public static readonly string texture2DPrefix = "_2D";
        public static readonly string texture3DPrefix = "_3D";
        public static readonly string textureCubePrefix = "_Cube";

        public static readonly Dictionary<TextureDimension, string> shaderPropertiesDimensionSuffix = new Dictionary<TextureDimension, string>
        {
            { TextureDimension.Tex2D, texture2DPrefix },
            { TextureDimension.Tex3D, texture3DPrefix },
            { TextureDimension.Cube,  textureCubePrefix },
        };

        public static void SetTextureWithDimension(Material material, string propertyName, Texture texture)
        {
            if (shaderPropertiesDimensionSuffix.TryGetValue(texture.dimension, out var suffix))
            {
#if UNITY_EDITOR
                int id = material.shader.FindPropertyIndex(propertyName + suffix);
                if (id == -1)
                    return;

                if (material.shader.GetPropertyTextureDimension(id) == texture.dimension)
#endif
                    material.SetTexture(propertyName + suffix, texture);
            }
        }
    }
}
