using System.Diagnostics;
using System.Text.Json;
using UnityEngine.Rendering;

static class Program
{
    static int Checks;
    static double MaximumCoefficientError;

    static void Main()
    {
        var stopwatch = Stopwatch.StartNew();
        string path = Path.Combine(AppContext.BaseDirectory, "NativeUnity6000Fixtures.json");
        var document = JsonSerializer.Deserialize<FixtureDocument>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert(document.UnityVersion == "6000.0.31f1", "native oracle version");
        Assert(document.Cases.Length == 16, "all native fixtures loaded");

        foreach (var fixture in document.Cases)
            VerifyNativeFixture(fixture);
        VerifyThreshold();
        VerifySharedBands(document.Cases.Single(c => c.Label == "full-3").Input);
        VerifyInvalidInputs();

        var report = new
        {
            checks = Checks,
            nativeFixtures = document.Cases.Length,
            oracleUnityVersion = document.UnityVersion,
            maximumCoefficientError = MaximumCoefficientError,
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            note = "Independent captured native outputs; no Unity installation or baking API used at test time."
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText("result.json", json);
        Console.WriteLine(json);
    }

    static void VerifyNativeFixture(Fixture fixture)
    {
        Assert(fixture.Input.Length == 27 && fixture.Expected.Length == 27,
            fixture.Label + ": complete RGB SH");
        var original = (float[])fixture.Input.Clone();
        var scales = StandaloneAPVDeringing.ComputeBandScales(fixture.Input);
        Assert(float.IsFinite(scales.l1) && scales.l1 > 0 && scales.l1 <= 1,
            fixture.Label + ": finite L1 window");
        Assert(float.IsFinite(scales.l2) && scales.l2 > 0 && scales.l2 <= 1,
            fixture.Label + ": finite L2 window");
        Assert(fixture.Input.SequenceEqual(original), fixture.Label + ": input remains unchanged");

        for (int channel = 0; channel < 3; ++channel)
        {
            for (int coefficient = 0; coefficient < 9; ++coefficient)
            {
                int index = channel * 9 + coefficient;
                float scale = coefficient == 0 ? 1 : coefficient < 4 ? scales.l1 : scales.l2;
                float actual = fixture.Input[index] * scale;
                float expected = fixture.Expected[index];
                double error = Math.Abs((double)actual - expected);
                MaximumCoefficientError = Math.Max(MaximumCoefficientError, error);
                double tolerance = coefficient == 0 ? 0 : 0.000005 * Math.Max(1, Math.Abs(expected));
                Near(actual, expected, tolerance,
                    $"{fixture.Label}: channel {channel}, coefficient {coefficient}");
            }
        }

        if (fixture.Label == "full-3")
        {
            // The old per-channel filter made neutral/warm floor GI purple:
            // R=.086917 G=.063721 B=.099831 at the same up-facing point.
            var up = new float[3];
            for (int channel = 0; channel < 3; ++channel)
                up[channel] = fixture.Input[channel * 9]
                    + fixture.Input[channel * 9 + 1] * scales.l1;
            Near(up[0], .233555, .000005, "warm floor up-facing R");
            Near(up[1], .187414, .000005, "warm floor up-facing G");
            Near(up[2], .166791, .000005, "warm floor up-facing B");
            Assert(up[0] > up[1] && up[1] > up[2], "floor hue agrees with native oracle");
        }
    }

    static void VerifyThreshold()
    {
        var black = StandaloneAPVDeringing.ComputeBandScales(new float[27]);
        Near(black.l1, 1, 0, "black input L1");
        Near(black.l2, 1, 0, "black input L2");

        var sh = new float[27];
        sh[0] = 1;
        sh[9] = .001f;
        sh[10] = 50;
        sh[18] = 0;
        sh[19] = -50;
        var atThreshold = StandaloneAPVDeringing.ComputeBandScales(sh);
        Near(atThreshold.l1, 1, 0, "DC at threshold does not constrain L1");
        Near(atThreshold.l2, 1, 0, "zero/threshold DC does not constrain L2");

        sh[9] = MathF.BitIncrement(.001f);
        var aboveThreshold = StandaloneAPVDeringing.ComputeBandScales(sh);
        Near(aboveThreshold.l1, .7575230598449707, .000001,
            "DC immediately above threshold constrains all channels");
    }

    static void VerifySharedBands(float[] fixture)
    {
        var combined = StandaloneAPVDeringing.ComputeBandScales(fixture);
        float expectedL1 = 1, expectedL2 = 1;
        for (int channel = 0; channel < 3; ++channel)
        {
            var isolated = new float[27];
            Array.Copy(fixture, channel * 9, isolated, channel * 9, 9);
            var scales = StandaloneAPVDeringing.ComputeBandScales(isolated);
            expectedL1 = Math.Min(expectedL1, scales.l1);
            expectedL2 = Math.Min(expectedL2, scales.l2);
        }
        Near(combined.l1, expectedL1, 0, "RGB share the most conservative L1 window");
        Near(combined.l2, expectedL2, 0, "RGB share the most conservative L2 window");
        Assert(combined.l2 < combined.l1, "L2 and L1 use distinct band windows");

        var permuted = new float[27];
        for (int channel = 0; channel < 3; ++channel)
            Array.Copy(fixture, channel * 9, permuted, ((channel + 1) % 3) * 9, 9);
        var rotatedChannels = StandaloneAPVDeringing.ComputeBandScales(permuted);
        Near(rotatedChannels.l1, combined.l1, 0, "RGB permutation preserves L1 window");
        Near(rotatedChannels.l2, combined.l2, 0, "RGB permutation preserves L2 window");
    }

    static void VerifyInvalidInputs()
    {
        foreach (int length in new[] { 0, 9, 26, 28 })
            Reject(new float[length], "invalid coefficient count " + length);
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var sh = new float[27];
            sh[0] = 1;
            sh[25] = value;
            Reject(sh, "nonfinite coefficient in a dark channel");
        }
    }

    static void Reject(float[] sh, string label)
    {
        bool rejected = false;
        try { StandaloneAPVDeringing.ComputeBandScales(sh); }
        catch (ArgumentException) { rejected = true; }
        Assert(rejected, label);
    }

    static void Assert(bool condition, string message)
    {
        ++Checks;
        if (!condition) throw new Exception(message);
    }

    static void Near(double actual, double expected, double tolerance, string message)
        => Assert(Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} vs {expected}");

    sealed class FixtureDocument
    {
        public string UnityVersion { get; set; } = "";
        public Fixture[] Cases { get; set; } = Array.Empty<Fixture>();
    }

    sealed class Fixture
    {
        public string Label { get; set; } = "";
        public float[] Input { get; set; } = Array.Empty<float>();
        public float[] Expected { get; set; } = Array.Empty<float>();
    }
}
