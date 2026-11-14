using System;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Declares that this exact rendering provider preserves the camera lighting
    /// policy established by UltraRenderer. It must not change reflection-probe
    /// blending, additional-light mode, their LightData inputs, or their global
    /// keywords, including through nested providers or deferred callbacks.
    /// </summary>
    /// <remarks>
    /// This declaration is not inherited. Derived providers must declare their
    /// own contract. It does not authorize arbitrary global-keyword state or
    /// undeclared runtime provider replacement. Without it, custom providers keep
    /// both ON and OFF variants when unused-variant stripping is enabled.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class PreservesCameraLightingKeywordsAttribute : Attribute
    {
        /// <summary>
        /// Also preserves main/additional shadow globals and ShadowData. Automatic
        /// callbacks must not draw shadow receivers before the graph's shadow passes.
        /// </summary>
        public bool preservesShadowKeywords { get; }

        public PreservesCameraLightingKeywordsAttribute(bool preservesShadowKeywords = false)
            => this.preservesShadowKeywords = preservesShadowKeywords;
    }
}
