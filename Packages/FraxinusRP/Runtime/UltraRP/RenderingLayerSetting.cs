namespace FraxinusEngine.Rendering.Ultra
{
    internal class RenderingLayerSetting
    {
        internal string renderingLayersTextureName;
        internal bool requiresRenderingLayer;
        internal RenderingLayerUtils.Event renderingLayersEvent;
        internal RenderingLayerUtils.MaskSize renderingLayersMaskSize;
        internal bool renderingLayerProvidesRenderObjectPass;
        internal bool renderingLayerProvidesByDepthNormalPass;
    }
}