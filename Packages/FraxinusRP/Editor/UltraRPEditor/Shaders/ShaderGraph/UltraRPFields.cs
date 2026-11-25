using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;

namespace FraxinusEditor.Rendering.Ultra
{
    static class UltraRPFields
    {
        public static FieldDescriptor SurfaceOpaque = new FieldDescriptor("UltraRP", "SurfaceOpaque", "ULTRARPTARGET_SURFACE_TYPE_OPAQUE");
        public static FieldDescriptor SurfaceTransparent = new FieldDescriptor("UltraRP", "SurfaceTransparent", "ULTRARPTARGET_SURFACE_TYPE_TRANSPARENT");
        public static FieldDescriptor BlendPremultiply = new FieldDescriptor("UltraRP", "BlendPremultiply", "ULTRARPTARGET_BLEND_MODE_PREMULTIPLY");
        public static FieldDescriptor BlendAdd = new FieldDescriptor("UltraRP", "BlendAdd", "ULTRARPTARGET_BLEND_MODE_ADD");
        public static FieldDescriptor BlendMultiply = new FieldDescriptor("UltraRP", "BlendMultiply", "ULTRARPTARGET_BLEND_MODE_MULTIPLY");
        public static FieldDescriptor NormalDropOffOS = new FieldDescriptor(string.Empty, "NormalDropOffOS", "_NORMAL_DROPOFF_OS 1");
        public static FieldDescriptor NormalDropOffTS = new FieldDescriptor(string.Empty, "NormalDropOffTS", "_NORMAL_DROPOFF_TS 1");
        public static FieldDescriptor NormalDropOffWS = new FieldDescriptor(string.Empty, "NormalDropOffWS", "_NORMAL_DROPOFF_WS 1");
        public static FieldDescriptor Normal = new FieldDescriptor(string.Empty, "Normal", "_NORMALMAP 1");
        public static FieldDescriptor SpecularSetup = new FieldDescriptor("UltraRP", "SpecularSetup", "ULTRARPTARGET_SPECULAR_SETUP");
    }
}
