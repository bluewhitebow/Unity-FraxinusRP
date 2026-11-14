using System;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Declares whether this exact extension type produces or enables the
    /// _MAIN_LIGHT_SHADOWS_SCREEN state. A false declaration also covers delegated
    /// draws, materials and callbacks owned by the extension. Keep it true, or omit
    /// the declaration, when a runtime configuration can require that state.
    /// This declaration does not describe deferred rendering or any other feature.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ScreenSpaceShadowUsageAttribute : Attribute
    {
        public bool usesScreenSpaceShadows { get; }

        public ScreenSpaceShadowUsageAttribute(bool usesScreenSpaceShadows)
            => this.usesScreenSpaceShadows = usesScreenSpaceShadows;
    }
}
