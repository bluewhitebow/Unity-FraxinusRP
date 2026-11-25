using UnityEngine;

namespace UnityEditor.Rendering
{
    // LightType gained the rectangular/pyramid/tube values after Unity 2022.
    // Comparing the enum name keeps editor code source-compatible with both
    // enum layouts without relying on unstable numeric values.
    internal static class LightTypeCompatibility
    {
        internal static bool IsBox(LightType type) => type.ToString() == "Box";
        internal static bool IsPyramid(LightType type) => type.ToString() == "Pyramid";
        internal static bool IsTube(LightType type) => type.ToString() == "Tube";
    }
}
