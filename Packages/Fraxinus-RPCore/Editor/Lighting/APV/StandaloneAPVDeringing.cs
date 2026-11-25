using System;

// Offline SH post-processing for the standalone baker. No native baking API.
namespace UnityEngine.Rendering
{
    internal static class StandaloneAPVDeringing
    {
        internal readonly struct BandScales
        {
            public readonly float l1, l2;
            internal BandScales(float l1, float l2) { this.l1 = l1; this.l2 = l2; }
            internal static BandScales Identity => new BandScales(1, 1);
        }

        // Smooth spectral windows, ordered from identity to the strongest filter.
        // L1 and L2 attenuate independently; DC always remains unchanged.
        static readonly BandScales[] s_Windows =
        {
            new BandScales(1f, 1f),
            new BandScales(.970142245f, .906294703f),
            new BandScales(.940284550f, .812589467f),
            new BandScales(.921331406f, .764526367f),
            new BandScales(.902378201f, .716463327f),
            new BandScales(.829950631f, .564606309f),
            new BandScales(.757523060f, .412749231f),
        };

        // Channel-major Unity SH: RGB, each DC,Y,Z,X,XY,YZ,ZZ,XZ,XX-YY.
        // All channels share the most conservative band window, preserving hue.
        internal static BandScales ComputeBandScales(ReadOnlySpan<float> sh)
        {
            if (sh.Length != 27)
                throw new ArgumentException("Expected 27 SH coefficients.", nameof(sh));
            float l1 = 1, l2 = 1;
            for (int channel = 0; channel < 3; ++channel)
            {
                var c = sh.Slice(channel * 9, 9);
                for (int i = 0; i < 9; ++i)
                    if (float.IsNaN(c[i]) || float.IsInfinity(c[i]))
                        throw new ArgumentException("SH must be finite.", nameof(sh));
                // Near-black channels must not determine the window for brighter RGB.
                if (c[0] <= .001f) continue;
                var scale = ComputeChannelScales(c);
                l1 = Math.Min(l1, scale.l1);
                l2 = Math.Min(l2, scale.l2);
            }
            return new BandScales(l1, l2);
        }

        static BandScales ComputeChannelScales(ReadOnlySpan<float> sh)
        {
            float length = (float)Math.Sqrt(sh[3] * sh[3] + sh[1] * sh[1] + sh[2] * sh[2]);
            RotateBands(sh, ref length, out float zonal, out float m1, out float m2);
            float a = zonal * .315391570f * 3f;
            float b = length * .488602519f;
            float c = zonal * -.315391570f;
            float dc = sh[0] * .282094806f;
            var first = BandScales.Identity;
            if (MinimumQuadratic(a, b, c + dc) <= 0)
                first = FindWindow(a, b, c, dc, 0, false);
            if (m1 <= 0 && m2 <= 0) return first;
            a *= first.l2;
            m2 *= first.l2;
            a += .546274245f * m2;
            b *= first.l1;
            c *= first.l2;
            c -= .546274245f * m2;
            float d = m1 * first.l2 * 1.09254849f;
            var second = FindWindow(a, b, c, dc, d, true);
            return new BandScales(first.l1 * second.l1, first.l2 * second.l2);
        }

        static BandScales FindWindow(float a, float b, float c, float dc, float d,
            bool includeAzimuthalBands)
        {
            for (int i = 0; i < s_Windows.Length; ++i)
            {
                if (Minimum(a, b, c, dc, d, s_Windows[i], includeAzimuthalBands) <= 0)
                    continue;
                if (i == 0) return s_Windows[0];
                float lo = 0, hi = 1;
                for (int iteration = 0; iteration < 5; ++iteration)
                {
                    float mid = (lo + hi) * .5f;
                    var candidate = Lerp(s_Windows[i - 1], s_Windows[i], mid);
                    if (Minimum(a, b, c, dc, d, candidate, includeAzimuthalBands) < 0)
                        lo = mid;
                    else hi = mid;
                }
                var result = Lerp(s_Windows[i - 1], s_Windows[i], hi);
                return result;
            }
            return s_Windows[s_Windows.Length - 1];
        }

        static BandScales Lerp(BandScales a, BandScales b, float t) =>
            new BandScales((1 - t) * a.l1 + t * b.l1, (1 - t) * a.l2 + t * b.l2);

        static float Minimum(float a, float b, float c, float dc, float d,
            BandScales w, bool includeAzimuthalBands)
        {
            a *= w.l2; b *= w.l1; c = c * w.l2 + dc; d *= w.l2;
            return includeAzimuthalBands ? MinimumCurve(a, b, c, d) : MinimumQuadratic(a, b, c);
        }

        static float MinimumQuadratic(float a, float b, float c)
        {
            if (a > 0)
            {
                float z = -b / (2 * a);
                if (z > -1 && z < 1)
                    return a * z * z + b * z + c;
            }
            return Math.Min(a + b + c, a - b + c);
        }

        static void RotateBands(ReadOnlySpan<float> sh, ref float length,
            out float zonal, out float m1, out float m2)
        {
            if (length == 0)
            {
                zonal = sh[6];
                m1 = (float)Math.Sqrt(sh[5] * sh[5] + sh[7] * sh[7]);
                m2 = (float)Math.Sqrt(sh[4] * sh[4] + sh[8] * sh[8]);
                return;
            }

            float inverse = 1f / length;
            float a = -sh[3] * inverse, b = -sh[1] * inverse, c = sh[2] * inverse;
            float u, v, w;
            if (Math.Abs(a) > Math.Abs(c)) { u = -b; v = a; w = 0; }
            else { u = 0; v = -c; w = b; }
            float tangentLengthSquared = (v * v + u * u) + w * w;
            float tangentInverse = tangentLengthSquared > 0 ? 1f / (float)Math.Sqrt(tangentLengthSquared) : tangentLengthSquared;
            u *= tangentInverse; v *= tangentInverse; w *= tangentInverse;
            float x = v * c - w * b, y = w * a - u * c, z = u * b - v * a;

            // Native uses the rotated L1 component, not its pre-rotation length.
            length = (c * sh[2] + -b * sh[1]) + -a * sh[3];

            const float sqrt3 = 1.7320507764816284f;
            const float inverseSqrt3 = 0.5773502588272095f;
            const float halfInverseSqrt3 = 0.28867512941360474f;
            const float sqrt3Over2 = 0.8660253882408142f;
            float xx = x * x, yy = y * y, zz = z * z;
            float uu = u * u, vv = v * v, ww = w * w;

            // Preserve float operation order: mathematically zonal inputs can
            // retain native's tiny azimuthal residual and take a different branch.
            float r44 = u * y + v * x;
            float r45 = -v * z - w * y;
            float r46 = (z * sqrt3) * w;
            float r47 = -u * z - w * x;
            float r48 = u * x - v * y;

            float r54 = -a * v - b * u;
            float r55 = b * w + c * v;
            float r56 = (w * -sqrt3) * c;
            float r57 = a * w + c * u;
            float r58 = b * v - a * u;

            float r64 = ((v * u + y * x) - (a + a) * b) * -inverseSqrt3;
            float r65 = ((w * v + z * y) - (b + b) * c) * inverseSqrt3;
            float r66 = ((-zz - ww) + (c * c + c * c)) * .5f;
            float r67 = ((w * u + z * x) - (a + a) * c) * inverseSqrt3;
            float r68 = ((((-xx - uu) + (a * a + a * a)) + yy) + vv - (b * b + b * b)) * halfInverseSqrt3;

            float r74 = -a * y - b * x;
            float r75 = b * z + c * y;
            float r76 = (z * -sqrt3) * c;
            float r77 = a * z + c * x;
            float r78 = b * y - a * x;

            float r84 = y * x - v * u;
            float r85 = w * v - z * y;
            float r86 = (zz - ww) * sqrt3Over2;
            float r87 = w * u - z * x;
            float r88 = ((xx - uu) - yy + vv) * .5f;

            float c4 = (((r45 * sh[5] + r44 * sh[4]) + r46 * sh[6]) + r47 * sh[7]) + r48 * sh[8];
            float c5 = (((r55 * sh[5] + r54 * sh[4]) + r56 * sh[6]) + r57 * sh[7]) + r58 * sh[8];
            zonal = (((r65 * sh[5] + r64 * sh[4]) + r66 * sh[6]) + r67 * sh[7]) + r68 * sh[8];
            float c7 = (((r75 * sh[5] + r74 * sh[4]) + r76 * sh[6]) + r77 * sh[7]) + r78 * sh[8];
            float c8 = (((r85 * sh[5] + r84 * sh[4]) + r86 * sh[6]) + r87 * sh[7]) + r88 * sh[8];
            m1 = (float)Math.Sqrt(c5 * c5 + c7 * c7);
            m2 = (float)Math.Sqrt(c4 * c4 + c8 * c8);
        }

        // Searches a*z*z + b*z + c + d*z*sqrt(1-z*z), with b,d >= 0.
        // Only the returned sign is used by the window search: the positive early
        // return is a sentinel, and a negative early return need not be the minimum.
        static float MinimumCurve(float a, float b, float c, float d)
        {
            float positiveEnd = (a + b) + c;
            float negativeEnd = (a - b) + c;
            if (MinimumQuadratic(a, b, c) - d * 0.5f > 0.0f)
                return 1.0f;

            float z = -0.7071070075035095f;
            float twiceA = a + a;
            bool valid = true;
            for (int iteration = 0; iteration < 10; ++iteration)
            {
                float root = (float)Math.Sqrt(1.0f - z * z);
                float firstDerivative = ((1.0f - (z + z) * z) * d) / root
                    + (twiceA * z + b);
                float secondDerivative = twiceA
                    - ((3.0f - (z + z) * z) * (z * d)) / ((1.0f - z * z) * root);
                float step = firstDerivative / secondDerivative;
                float candidate = z - step;

                valid = !float.IsNaN(candidate) && !float.IsInfinity(candidate)
                    && Math.Abs(candidate) < 3.0f;
                if (!valid)
                    break;

                z = candidate;
                // Native uses the previous iterate's square root in this sign test.
                float approximateValue = ((candidate * a) * candidate + candidate * b)
                    + c + (candidate * d) * root;
                if (approximateValue < 0.0f)
                {
                    return approximateValue;
                }

                if (Math.Abs(step) < 0.004999999888241291f)
                    break;
            }

            if (valid)
            {
                float root = (float)Math.Sqrt(1.0f - z * z);
                float value = root * (z * d) + ((z * a) * z + z * b + c);
                if (negativeEnd > value && positiveEnd > value)
                {
                    return value;
                }
            }

            if (positiveEnd > negativeEnd)
            {
                return negativeEnd;
            }

            return positiveEnd;
        }
    }
}
