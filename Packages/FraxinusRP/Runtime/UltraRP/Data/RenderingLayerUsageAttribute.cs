using System;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Declares whether this exact extension can request rendering-layer output or
    /// enable _WRITE_RENDERING_LAYERS independently of the pipeline's built-in
    /// light/decal settings. A false declaration covers its input getters, draws,
    /// delegated materials and callbacks, and every runtime configuration.
    /// It does not disable the reviewed built-in writer used by a DrawObjectsPass.
    /// </summary>
    /// <remarks>
    /// When inputs come from a serialized ScriptableRenderPassInput field, name
    /// that field so build analysis can inspect it without invoking the getter.
    /// New/unreviewed input bits always retain variants, including when this
    /// declaration is false. Keep the declaration true or omit it if usage is
    /// unknown. Declarations are not inherited and do not prove other features.
    /// This describes FRP's declared output requirements. Code that directly
    /// changes reserved pipeline keywords must declare that extension's usage;
    /// build analysis does not execute or inspect arbitrary user callbacks.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class RenderingLayerUsageAttribute : Attribute
    {
        public bool usesRenderingLayers { get; }
        public string inputFieldName { get; }

        public RenderingLayerUsageAttribute(bool usesRenderingLayers, string inputFieldName = null)
        {
            this.usesRenderingLayers = usesRenderingLayers;
            this.inputFieldName = inputFieldName;
        }
    }
}
