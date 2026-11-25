using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Light Utils contains function to convert light intensities between units
    /// </summary>
    class LightUtils
    {
        static float s_luminanceToEvFactor => Mathf.Log(100f / ColorUtils.s_LightMeterCalibrationConstant, 2);
        static float s_evToLuminanceFactor => -Mathf.Log(100f / ColorUtils.s_LightMeterCalibrationConstant, 2);

        // Physical light unit helper
        // All light unit are in lumen (Luminous power)
        // Punctual light (point, spot) are convert to candela (cd = lumens / steradian)

        // For our isotropic area lights which expect radiance(W / (sr* m^2)) in the shader:
        // power = Integral{area, Integral{hemisphere, radiance * <N, L>}},
        // power = area * Pi * radiance,
        // radiance = power / (area * Pi).
        // We use photometric unit, so radiance is luminance and power is luminous power

        // Ref: Moving Frostbite to PBR
        // Also good ref: https://www.radiance-online.org/community/workshops/2004-fribourg/presentations/Wandachowicz_paper.pdf

        /// <summary>
        /// Convert an intensity in Lumen to Candela for a point light
        /// </summary>
        /// <param name="intensity"></param>
        /// <returns></returns>
        public static float ConvertPointLightLumenToCandela(float intensity)
            => intensity / (4.0f * Mathf.PI);

        /// <summary>
        /// Convert an intensity in Candela to Lumen for a point light
        /// </summary>
        /// <param name="intensity"></param>
        /// <returns></returns>
        public static float ConvertPointLightCandelaToLumen(float intensity)
            => intensity * (4.0f * Mathf.PI);

        // angle is the full angle, not the half angle in radian
        // convert intensity (lumen) to candela
        /// <summary>
        /// Convert an intensity in Lumen to Candela for a cone spot light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="angle">Full angle in radian</param>
        /// <param name="exact">Exact computation or an approximation</param>
        /// <returns></returns>
        public static float ConvertSpotLightLumenToCandela(float intensity, float angle, bool exact)
            => exact ? intensity / (2.0f * (1.0f - Mathf.Cos(angle / 2.0f)) * Mathf.PI) : intensity / Mathf.PI;

        /// <summary>
        /// Convert an intensity in Candela to Lumen for a cone pot light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="angle">Full angle in radian</param>
        /// <param name="exact">Exact computation or an approximation</param>
        /// <returns></returns>
        public static float ConvertSpotLightCandelaToLumen(float intensity, float angle, bool exact)
            => exact ? intensity * (2.0f * (1.0f - Mathf.Cos(angle / 2.0f)) * Mathf.PI) : intensity * Mathf.PI;

        // angleA and angleB are the full opening angle, not half angle
        // convert intensity (lumen) to candela
        /// <summary>
        /// Convert an intensity in Lumen to Candela for a pyramid spot light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="angleA">Full opening angle in radian</param>
        /// <param name="angleB">Full opening angle in radian</param>
        /// <returns></returns>
        public static float ConvertFrustrumLightLumenToCandela(float intensity, float angleA, float angleB)
            => intensity / (4.0f * Mathf.Asin(Mathf.Sin(angleA / 2.0f) * Mathf.Sin(angleB / 2.0f)));

        /// <summary>
        /// Convert an intensity in Candela to Lumen for a pyramid spot light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="angleA">Full opening angle in radian</param>
        /// <param name="angleB">Full opening angle in radian</param>
        /// <returns></returns>
        public static float ConvertFrustrumLightCandelaToLumen(float intensity, float angleA, float angleB)
            => intensity * (4.0f * Mathf.Asin(Mathf.Sin(angleA / 2.0f) * Mathf.Sin(angleB / 2.0f)));

        /// <summary>
        /// Convert an intensity in Lumen to Luminance(nits) for a sphere light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="sphereRadius"></param>
        /// <returns></returns>
        public static float ConvertSphereLightLumenToLuminance(float intensity, float sphereRadius)
            => intensity / ((4.0f * Mathf.PI * sphereRadius * sphereRadius) * Mathf.PI);

        /// <summary>
        /// Convert an intensity in Luminance(nits) to Lumen for a sphere light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="sphereRadius"></param>
        /// <returns></returns>
        public static float ConvertSphereLightLuminanceToLumen(float intensity, float sphereRadius)
            => intensity * ((4.0f * Mathf.PI * sphereRadius * sphereRadius) * Mathf.PI);

        /// <summary>
        /// Convert an intensity in Lumen to Luminance(nits) for a disc light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="discRadius"></param>
        /// <returns></returns>
        public static float ConvertDiscLightLumenToLuminance(float intensity, float discRadius)
            => intensity / ((discRadius * discRadius * Mathf.PI) * Mathf.PI);

        /// <summary>
        /// Convert an intensity in Luminance(nits) to Lumen for a disc light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="discRadius"></param>
        /// <returns></returns>
        public static float ConvertDiscLightLuminanceToLumen(float intensity, float discRadius)
            => intensity * ((discRadius * discRadius * Mathf.PI) * Mathf.PI);

        /// <summary>
        /// Convert an intensity in Lumen to Luminance(nits) for a rectangular light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        public static float ConvertRectLightLumenToLuminance(float intensity, float width, float height)
            => intensity / ((width * height) * Mathf.PI);

        /// <summary>
        /// Convert an intensity in Luminance(nits) to Lumen for a rectangular light.
        /// </summary>
        /// <param name="intensity"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        public static float ConvertRectLightLuminanceToLumen(float intensity, float width, float height)
            => intensity * ((width * height) * Mathf.PI);

        // Helper for Lux, Candela, Luminance, Ev conversion
        /// <summary>
        /// Convert intensity in Lux at a certain distance in Candela.
        /// </summary>
        /// <param name="lux"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public static float ConvertLuxToCandela(float lux, float distance)
            => lux * distance * distance;

        /// <summary>
        /// Convert intensity in Candela at a certain distance in Lux.
        /// </summary>
        /// <param name="candela"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public static float ConvertCandelaToLux(float candela, float distance)
            => candela / (distance * distance);

        /// <summary>
        /// Convert EV100 to Luminance(nits)
        /// </summary>
        /// <param name="ev"></param>
        /// <returns></returns>
        public static float ConvertEvToLuminance(float ev)
        {
            return Mathf.Pow(2, ev + s_evToLuminanceFactor);
        }

        /// <summary>
        /// Convert EV100 to Candela
        /// </summary>
        /// <param name="ev"></param>
        /// <returns></returns>
        public static float ConvertEvToCandela(float ev)
        // From punctual point of view candela and luminance is the same
            => ConvertEvToLuminance(ev);

        /// <summary>
        /// Convert EV100 to Lux at a certain distance
        /// </summary>
        /// <param name="ev"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public static float ConvertEvToLux(float ev, float distance)
        // From punctual point of view candela and luminance is the same
            => ConvertCandelaToLux(ConvertEvToLuminance(ev), distance);

        /// <summary>
        /// Convert Luminance(nits) to EV100
        /// </summary>
        /// <param name="luminance"></param>
        /// <returns></returns>
        public static float ConvertLuminanceToEv(float luminance)
        {
            return Mathf.Log(luminance, 2) + s_luminanceToEvFactor;
        }

        /// <summary>
        /// Convert Candela to EV100
        /// </summary>
        /// <param name="candela"></param>
        /// <returns></returns>
        public static float ConvertCandelaToEv(float candela)
        // From punctual point of view candela and luminance is the same
            => ConvertLuminanceToEv(candela);

        /// <summary>
        /// Convert Lux at a certain distance to EV100
        /// </summary>
        /// <param name="lux"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public static float ConvertLuxToEv(float lux, float distance)
        // From punctual point of view candela and luminance is the same
            => ConvertLuminanceToEv(ConvertLuxToCandela(lux, distance));

        // Helper for punctual and area light unit conversion
        /// <summary>
        /// Convert a punctual light intensity in Lumen to Candela
        /// </summary>
        /// <param name="lightType"></param>
        /// <param name="lumen"></param>
        /// <param name="initialIntensity"></param>
        /// <param name="enableSpotReflector"></param>
        /// <returns></returns>
        public static float ConvertPunctualLightLumenToCandela(LightType lightType, float lumen, float initialIntensity, bool enableSpotReflector)
        {
            if (lightType == LightType.Spot && enableSpotReflector)
            {
                // We have already calculate the correct value, just assign it
                return initialIntensity;
            }
            return ConvertPointLightLumenToCandela(lumen);
        }

        /// <summary>
        /// Convert a punctual light intensity in Lumen to Lux
        /// </summary>
        /// <param name="lightType"></param>
        /// <param name="lumen"></param>
        /// <param name="initialIntensity"></param>
        /// <param name="enableSpotReflector"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public static float ConvertPunctualLightLumenToLux(LightType lightType, float lumen, float initialIntensity, bool enableSpotReflector, float distance)
        {
            float candela = ConvertPunctualLightLumenToCandela(lightType, lumen, initialIntensity, enableSpotReflector);
            return ConvertCandelaToLux(candela, distance);
        }
    }
}
