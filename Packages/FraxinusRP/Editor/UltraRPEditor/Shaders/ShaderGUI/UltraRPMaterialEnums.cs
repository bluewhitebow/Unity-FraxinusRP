namespace FraxinusEditor.Rendering.Ultra
{
    /// <summary>
    /// Surface type options.
    /// </summary>
    public enum SurfaceType
    {
        Opaque,
        Transparent,
    }

    /// <summary>
    /// Depth write control options.
    /// </summary>
    public enum ZWriteControl
    {
        Auto = 0,
        ForceEnabled = 1,
        ForceDisabled = 2
    }

    /// <summary>
    /// Z-Test mode options.
    /// </summary>
    public enum ZTestMode
    {
        Disabled = 0,
        Never = 1,
        Less = 2,
        Equal = 3,
        LEqual = 4,
        Greater = 5,
        NotEqual = 6,
        GEqual = 7,
        Always = 8,
    }

    /// <summary>
    /// Alpha blending mode options.
    /// </summary>
    public enum AlphaMode
    {
        Alpha,
        Premultiply,
        Additive,
        Multiply,
    }

    /// <summary>
    /// Render face options.
    /// </summary>
    internal enum RenderFace
    {
        Front = 2,
        Back = 1,
        Both = 0
    }

    /// <summary>
    /// Workflow mode options.
    /// </summary>
    public enum WorkflowMode
    {
        Specular,
        Metallic,
    }

    /// <summary>
    /// Normal drop off space options.
    /// </summary>
    public enum NormalDropOffSpace
    {
        Object,
        Tangent,
        World,
    }

    /// <summary>
    /// Additional motion vector mode options.
    /// </summary>
    internal enum AdditionalMotionVectorMode
    {
        None,
        TimeBased,
        Custom
    }
}
