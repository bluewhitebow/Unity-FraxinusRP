using UnityEngine;
using UnityEditor;

namespace FraxinusEditor.Rendering.Ultra
{
    static partial class UltraRenderPipelineCameraUI
    {
        public partial class Output
        {
            public class Styles
            {
                // Using the pipeline Settings
                public static GUIContent[] displayedCameraOptions =
                {
                    EditorGUIUtility.TrTextContent("Off"),
                    EditorGUIUtility.TrTextContent("Use settings from Render Pipeline Asset"),
                };
                
                public static int[] cameraOptions = { 0, 1 };
                
                // Using the project settings
                public static GUIContent[] hdrOuputOptions =
                {
                    EditorGUIUtility.TrTextContent("Off"),
                    EditorGUIUtility.TrTextContent("Use Project Settings"),
                };
                public static int[] hdrOuputValues = { 0, 1 };

                public static readonly GUIContent targetTextureLabel = EditorGUIUtility.TrTextContent("Output Texture", "The texture to render this camera into, if none then this camera renders to screen.");

                public static string inspectorOverlayCameraText = L10n.Tr("Inspector Overlay Camera");
                public static readonly GUIContent allowHDR = EditorGUIUtility.TrTextContent("HDR Rendering", "High Dynamic Range gives you a wider range of light intensities, so your lighting looks more realistic. With it, you can still see details and experience less saturation even with bright light.", (Texture)null);
                public static readonly GUIContent allowDynamicResolution = EditorGUIUtility.TrTextContent("UltraRP Dynamic Resolution", "Whether to support UltraRP dynamic resolution.");
                public static readonly GUIContent allowHDROutput = EditorGUIUtility.TrTextContent("HDR Output", "Whether to support outputting to HDR displays.");

                public static string unsupportedTargetTexture = L10n.Tr("UltraRP requires a single-sample target texture. Set the Render Texture's Anti-Aliasing to None and disable Bind MS.");
                public static string disabledHDRRenderingWithHDROutput = L10n.Tr("HDR Output is enabled but HDR rendering is disabled. Image may appear underexposed or oversaturated on an HDR display.");
            }
        }
    }
}
