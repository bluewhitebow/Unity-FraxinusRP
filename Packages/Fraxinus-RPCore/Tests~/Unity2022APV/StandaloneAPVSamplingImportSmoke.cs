using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

// Verifies an external import contract using bytes captured independently from
// Unity 6's native sampling resource. Does not open/save scenes or bake probes.
[InitializeOnLoad]
public static class StandaloneAPVSamplingImportSmoke
{
    const string Prefix = "Assets/Editor/StandaloneAPVSamplingImportSmoke";
    const string Request = "Temp/StandaloneAPVSamplingImportSmoke.request";
    const string Output = "Logs/standalone-apv-sampling-import.json";
    const string ResourceRoot = "Packages/com.fraxinus.rendering.light-transport/Runtime/Sampling/Textures/SobolBlueNoise/";
    [StructLayout(LayoutKind.Sequential)]
    struct Coordinate { public int texture, x, y, padding; }
    [Serializable]
    sealed class Sample { public int x, y, topDownByte, bottomUpByte; }
    [Serializable]
    sealed class Table { public string name; public int width, height; public Sample[] samples; }
    [Serializable]
    sealed class Fixture { public string nativeUnityVersion, nativeResourceSha256; public Table[] tables; }
    [Serializable]
    sealed class Result
    {
        public string unityVersion, graphicsDevice, nativeResourceSha256, error;
        public bool passed;
        public int comparedBytes, topDownMismatches, bottomUpMismatches;
        public string[] mismatches;
    }

    static StandaloneAPVSamplingImportSmoke() { EditorApplication.update += Poll; }
    static void Poll()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || !File.Exists(Request))
            return;
        File.Delete(Request);
        Run();
    }

    public static void Run()
    {
        var result = new Result { unityVersion = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceType.ToString() };
        try
        {
            var fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(Prefix + ".json"));
            result.nativeResourceSha256 = fixture.nativeResourceSha256;
            if (fixture.tables == null || fixture.tables.Length != 3)
                throw new InvalidOperationException("Expected three native sampling table fixtures.");
            var textures = fixture.tables.Select(table => AssetDatabase.LoadAssetAtPath<Texture2D>(ResourceRoot + table.name + ".png")).ToArray();
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(Prefix + ".compute");
            if (shader == null || textures.Any(texture => texture == null))
                throw new InvalidOperationException("Missing imported sampling texture or smoke compute shader.");
            int kernel = shader.FindKernel("ReadSamplingTables");
            var coordinates = new List<Coordinate>();
            var expected = new List<Sample>();
            for (int table = 0; table < textures.Length; ++table)
            {
                var data = fixture.tables[table];
                if (textures[table].width != data.width || textures[table].height != data.height)
                    throw new InvalidOperationException("Unexpected dimensions: " + data.name);
                foreach (var sample in data.samples)
                {
                    coordinates.Add(new Coordinate { texture = table, x = sample.x, y = sample.y });
                    expected.Add(sample);
                }
            }
            using (var coordinateBuffer = new ComputeBuffer(coordinates.Count, 16))
            using (var valueBuffer = new ComputeBuffer(coordinates.Count, 4))
            {
                coordinateBuffer.SetData(coordinates);
                shader.SetTexture(kernel, "_Sequence", textures[0]);
                shader.SetTexture(kernel, "_Ranking", textures[1]);
                shader.SetTexture(kernel, "_Scrambling", textures[2]);
                shader.SetBuffer(kernel, "_Coordinates", coordinateBuffer);
                shader.SetBuffer(kernel, "_Values", valueBuffer);
                shader.SetInt("_Count", coordinates.Count);
                shader.Dispatch(kernel, (coordinates.Count + 63) / 64, 1, 1);
                var values = new uint[coordinates.Count];
                valueBuffer.GetData(values);
                var mismatches = new List<string>();
                for (int i = 0; i < values.Length; ++i)
                {
                    if (values[i] != expected[i].topDownByte) result.topDownMismatches++;
                    if (values[i] == expected[i].bottomUpByte) continue;
                    result.bottomUpMismatches++;
                    var coordinate = coordinates[i];
                    mismatches.Add(fixture.tables[coordinate.texture].name + " (" + coordinate.x + "," + coordinate.y + "): GPU=" + values[i] + ", expected=" + expected[i].bottomUpByte);
                }
                result.comparedBytes = values.Length;
                result.mismatches = mismatches.ToArray();
            }
            // The production sampler must reverse these imported rows when
            // addressing the native top-down sequence/ranking/scrambling data.
            result.passed = result.comparedBytes == 145 && result.bottomUpMismatches == 0 && result.topDownMismatches > 0;
        }
        catch (Exception error) { result.error = error.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        File.WriteAllText(Output, JsonUtility.ToJson(result, true));
        if (result.passed) Debug.Log("Standalone APV sampling import smoke passed: " + Output);
        else Debug.LogError("Standalone APV sampling import smoke failed: " + Output + " " + result.error);
        if (Application.isBatchMode) EditorApplication.Exit(result.passed ? 0 : 1);
    }
}
