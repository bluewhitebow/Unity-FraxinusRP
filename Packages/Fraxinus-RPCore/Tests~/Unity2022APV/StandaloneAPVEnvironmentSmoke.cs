// Copy this file into Assets/Editor/APVEnvironmentSmoke.cs in a Unity project.
// Create Temp/APVEnvironmentSmoke.request to run in an idle interactive Editor.
// The test writes Temp/APVEnvironmentSmoke.json; it never starts a bake or saves
// a scene. It requires a real graphics device and restores the environment.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class APVEnvironmentSmoke
{
    const string RequestPath = "Temp/APVEnvironmentSmoke.request";
    const string ReportPath = "Temp/APVEnvironmentSmoke.json";
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static double s_NextPoll;
    static bool s_Running;

    // The shader returns analytic, linear radiance. SetVector deliberately
    // avoids Material.SetColor's project color-space conversion (a value such
    // as 0.8 can upload as about 0.604 in a linear project).
    const string ShaderSource = @"
Shader ""Hidden/APVEnvironmentSmoke/AnalyticSky""
{
    SubShader
    {
        Tags { ""Queue""=""Background"" ""RenderType""=""Background"" ""PreviewType""=""Skybox"" }
        Cull Off ZWrite Off ZTest LEqual
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""
            float4 _Constant, _LinearR, _LinearG, _LinearB;
            float4 _QuadraticA, _QuadraticB;
            struct Varyings { float4 position : SV_POSITION; float3 direction : TEXCOORD0; };
            Varyings vert(float4 vertex : POSITION)
            {
                Varyings result;
                result.position = UnityObjectToClipPos(vertex);
                // The skybox cube uses world-aligned directions, as in Unity's
                // Skybox/Cubemap shader. The view matrix rotates the projection.
                result.direction = vertex.xyz;
                return result;
            }
            float4 frag(Varyings input) : SV_Target
            {
                float3 d = normalize(input.direction);
                float3 value = _Constant.rgb + float3(dot(_LinearR.xyz, d), dot(_LinearG.xyz, d), dot(_LinearB.xyz, d));
                value.r += _QuadraticA.x * d.x * d.y + _QuadraticA.y * d.y * d.z;
                value.g += _QuadraticA.z * d.x * d.z + _QuadraticA.w * (d.x * d.x - d.y * d.y);
                value.b += _QuadraticB.x * (3 * d.z * d.z - 1);
                return float4(value, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}";

    [Serializable]
    sealed class Measurement
    {
        public string name;
        public float maxAbsoluteError;
        public float[] actual, expected;
    }

    [Serializable]
    sealed class Report
    {
        public bool passed, settingsRestored, activeTargetRestored, nativeBakeObserved;
        public string unityVersion, device, graphicsApi, error;
        public double elapsedSeconds;
        public List<string> checks = new List<string>();
        public List<string> failures = new List<string>();
        public List<Measurement> measurements = new List<Measurement>();
    }

    static APVEnvironmentSmoke()
    {
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (s_Running || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.timeSinceStartup < s_NextPoll)
            return;
        s_NextPoll = EditorApplication.timeSinceStartup + 0.25;
        if (!File.Exists(RequestPath))
            return;
        File.Delete(RequestPath);
        Run();
    }

    public static void Run()
    {
        if (s_Running)
            return;
        s_Running = true;
        var started = EditorApplication.timeSinceStartup;
        var report = new Report
        {
            unityVersion = Application.unityVersion,
            device = SystemInfo.graphicsDeviceName,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString()
        };
        var previousMode = RenderSettings.ambientMode;
        var previousSkybox = RenderSettings.skybox;
        var previousIntensity = RenderSettings.ambientIntensity;
        var previousProbe = RenderSettings.ambientProbe;
        var previousTarget = RenderTexture.active;
        Shader shader = null;
        Material material = null;
        RenderTexture sentinel = null;
        try
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Run in Edit Mode.");
            Require(!Lightmapping.isRunning && !AdaptiveProbeVolumes.isRunning, "A bake is already running.");
            Require(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A graphics device is required.");
            MethodInfo capture = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("UnityEngine.Rendering.StandaloneAPVLightingBaker");
                if (type != null)
                    capture = type.GetMethod("CaptureEnvironmentSH", Flags);
            }
            Require(capture != null, "StandaloneAPVLightingBaker.CaptureEnvironmentSH is missing.");
            shader = ShaderUtil.CreateShaderAsset(ShaderSource, true);
            shader.hideFlags = HideFlags.HideAndDontSave;
            Require(shader.isSupported, "Analytic skybox shader is unsupported.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            sentinel = new RenderTexture(8, 8, 0) { hideFlags = HideFlags.HideAndDontSave };
            sentinel.Create();
            Require(sentinel.IsCreated(), "Failed to create the active-target sentinel.");
            RenderTexture.active = sentinel;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.skybox = material;
            RenderSettings.ambientIntensity = 1;

            var constant = new Vector3(2, 4, 8);
            Configure(material, constant, Vector3.zero, Vector3.zero, Vector3.zero);
            Compare(report, "HDR constant radiance", Capture(capture, material, sentinel), Constant(constant));
            report.checks.Add("Linear HDR radiance 2/4/8 survives skybox rendering, readback and cosine convolution without clipping or exposure");

            constant = new Vector3(4, 5, 6);
            var red = new Vector3(0.6f, -0.9f, 1.2f);
            var green = new Vector3(-1.2f, 0.3f, -0.6f);
            var blue = new Vector3(0.9f, 1.2f, -0.3f);
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Configure(material, constant, red * sign, green * sign, blue * sign);
                var expected = Constant(constant);
                var gradients = new[] { red * sign, green * sign, blue * sign };
                for (int channel = 0; channel < 3; channel++)
                {
                    // Analytic Lambert convolution / PI multiplies an L1
                    // radiance gradient by 2/3. Unity's polynomial order is YZX.
                    expected[channel, 1] = gradients[channel].y * (2.0f / 3.0f);
                    expected[channel, 2] = gradients[channel].z * (2.0f / 3.0f);
                    expected[channel, 3] = gradients[channel].x * (2.0f / 3.0f);
                }
                Compare(report, "Signed XYZ gradient " + sign, Capture(capture, material, sentinel), expected);
            }
            report.checks.Add("Both signs of every world axis preserve Y/Z/X coefficient order and the analytic 2/3 Lambert factor");

            Configure(material, constant, Vector3.zero, Vector3.zero, Vector3.zero);
            material.SetVector("_QuadraticA", new Vector4(0.4f, 0.6f, 0.8f, 0.2f));
            material.SetVector("_QuadraticB", new Vector4(0.3f, 0, 0, 0));
            var quadratic = Constant(constant);
            // Every degree-two harmonic is attenuated by 1/4 under Lambert
            // convolution. The five functions span the complete L2 band.
            quadratic[0, 4] = 0.1f;
            quadratic[0, 5] = 0.15f;
            quadratic[1, 7] = 0.2f;
            quadratic[1, 8] = 0.05f;
            quadratic[2, 6] = 0.075f;
            Compare(report, "All five quadratic harmonics", Capture(capture, material, sentinel), quadratic);
            report.checks.Add("All five L2 harmonics preserve sign/order and the analytic 1/4 Lambert factor");

            foreach (float intensity in new[] { 0.0f, 0.5f, 2.0f })
            {
                RenderSettings.ambientIntensity = intensity;
                Compare(report, "Skybox ambientIntensity " + intensity, Capture(capture, material, sentinel), quadratic * intensity);
            }
            report.checks.Add("Skybox ambientIntensity scales all bands linearly at 0/0.5/2");

            RenderSettings.ambientIntensity = 1;
            var poisoned = new SphericalHarmonicsL2();
            for (int channel = 0; channel < 3; channel++)
                for (int coefficient = 0; coefficient < 9; coefficient++)
                    poisoned[channel, coefficient] = (channel + 1) * (coefficient + 1) * 10;
            RenderSettings.ambientProbe = poisoned;
            Compare(report, "Stale ambientProbe ignored", Capture(capture, material, sentinel), quadratic);
            report.checks.Add("Existing baked ambientProbe coefficients do not contaminate the active skybox capture");
            report.nativeBakeObserved = Lightmapping.isRunning || AdaptiveProbeVolumes.isRunning;
            Require(!report.nativeBakeObserved, "An unexpected bake started during the test.");
        }
        catch (Exception exception)
        {
            report.error = (exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException : exception).ToString();
        }
        finally
        {
            RenderSettings.ambientMode = previousMode;
            RenderSettings.skybox = previousSkybox;
            RenderSettings.ambientIntensity = previousIntensity;
            RenderSettings.ambientProbe = previousProbe;
            RenderTexture.active = previousTarget;
            if (sentinel != null) UnityEngine.Object.DestroyImmediate(sentinel);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
            report.settingsRestored = RenderSettings.ambientMode == previousMode &&
                RenderSettings.skybox == previousSkybox && RenderSettings.ambientIntensity == previousIntensity &&
                RenderSettings.ambientProbe.Equals(previousProbe);
            report.activeTargetRestored = RenderTexture.active == previousTarget;
            report.elapsedSeconds = EditorApplication.timeSinceStartup - started;
            report.passed = report.error == null && report.failures.Count == 0 &&
                report.settingsRestored && report.activeTargetRestored && !report.nativeBakeObserved;
            Directory.CreateDirectory("Temp");
            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Debug.Log("Standalone APV environment smoke " + (report.passed ? "passed" : "FAILED") + ": " + ReportPath);
            s_Running = false;
        }
    }

    static void Configure(Material material, Vector3 constant, Vector3 red, Vector3 green, Vector3 blue)
    {
        material.SetVector("_Constant", constant);
        material.SetVector("_LinearR", red);
        material.SetVector("_LinearG", green);
        material.SetVector("_LinearB", blue);
        material.SetVector("_QuadraticA", Vector4.zero);
        material.SetVector("_QuadraticB", Vector4.zero);
    }

    static SphericalHarmonicsL2 Capture(MethodInfo capture, Material material, RenderTexture sentinel)
    {
        var result = (SphericalHarmonicsL2)capture.Invoke(null, null);
        Require(RenderTexture.active == sentinel, "Capture changed the active render target.");
        Require(RenderSettings.skybox == material, "Capture changed RenderSettings.skybox.");
        return result;
    }

    static SphericalHarmonicsL2 Constant(Vector3 value)
    {
        var result = new SphericalHarmonicsL2();
        for (int channel = 0; channel < 3; channel++) result[channel, 0] = value[channel];
        return result;
    }

    static void Compare(Report report, string name, SphericalHarmonicsL2 actual, SphericalHarmonicsL2 expected)
    {
        var measurement = new Measurement { name = name, actual = new float[27], expected = new float[27] };
        for (int channel = 0; channel < 3; channel++)
            for (int coefficient = 0; coefficient < 9; coefficient++)
            {
                var value = actual[channel, coefficient];
                var reference = expected[channel, coefficient];
                int index = channel * 9 + coefficient;
                measurement.actual[index] = value;
                measurement.expected[index] = reference;
                float error = Mathf.Abs(value - reference);
                measurement.maxAbsoluteError = Mathf.Max(measurement.maxAbsoluteError, error);
                // The capture uses finite 64x64 cube-face quadrature. This
                // permits integration error while rejecting clipping, gamma,
                // orientation, convolution and stale-environment regressions.
                // Keep margin for ARGBHalf fallback and driver-specific skybox
                // rasterization while remaining far below orientation/gamma
                // errors (which are O(1) for these fixtures).
                if (float.IsNaN(value) || float.IsInfinity(value) || error > 0.004f + 0.002f * Mathf.Abs(reference))
                    report.failures.Add(name + " [" + channel + "," + coefficient + "]: actual=" + value + ", expected=" + reference);
            }
        report.measurements.Add(measurement);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
