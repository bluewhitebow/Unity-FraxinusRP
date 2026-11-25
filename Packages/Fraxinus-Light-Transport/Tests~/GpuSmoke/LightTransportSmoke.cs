using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace Fraxinus
{
    // The existing InternalsVisibleTo test assembly lets this exercise the same API
    // used by render-pipeline packages, without changing the production API.
    public static class LightTransportSmoke
    {
        const string ShaderPath = "Assets/Editor/FraxinusLightTransportSmoke/TraceRays.urtshader";
        const string ReportPath = "fraxinus-light-transport-smoke.json";

        [Serializable]
        sealed class Report
        {
            public bool passed;
            public string unityVersion;
            public string device;
            public string graphicsApi;
            public int computeResources;
            public int verifiedRays;
            public string[] checks;
            public string error;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SmokeRay
        {
            public Vector3 origin;
            public float minT;
            public Vector3 direction;
            public float maxT;
            public uint culling;
            public uint mask;
            public uint padding;
            public uint padding2;

            public SmokeRay(Vector3 origin, Vector3 direction, uint mask = uint.MaxValue, float maxT = 100)
            {
                this.origin = origin;
                this.direction = direction;
                this.mask = mask;
                this.maxT = maxT;
                minT = 0;
                culling = padding = padding2 = 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SmokeHit
        {
            public uint instanceID;
            public uint primitiveIndex;
            public Vector2 barycentrics;
            public float distance;
            public uint frontFace;
        }

        public static void Run()
        {
            var report = new Report
            {
                unityVersion = Application.unityVersion,
                device = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString()
            };
            var checks = new List<string>();
            var errors = new List<string>();
            Application.LogCallback callback = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    errors.Add(message);
            };
            Application.logMessageReceived += callback;
            try
            {
                Require(SystemInfo.supportsComputeShaders, "Compute shaders are unavailable. Run without -nographics.");
                Require(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A real graphics device is required.");
                Require(Marshal.SizeOf<SmokeRay>() == 48 && Marshal.SizeOf<SmokeHit>() == 24, "GPU structure layouts differ.");

                AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var shaderAsset = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
                CheckShader(shaderAsset, ShaderPath);
                Require(shaderAsset.HasKernel("MainRayGenShader"), "URT ray-generation kernel was not imported.");
                Require(shaderAsset.HasKernel("ComputeIndirectDispatchDims"), "URT indirect-dispatch kernel was not imported.");
                var resources = new RayTracingResources();
                resources.Load();
                foreach (var field in typeof(RayTracingResources).GetFields())
                {
                    if (field.FieldType != typeof(ComputeShader))
                        continue;
                    CheckShader((ComputeShader)field.GetValue(resources), field.Name);
                    report.computeResources++;
                }
                checks.Add("URT import and all compute resource imports have no compiler errors");

                var triangle = new Mesh { name = "Fraxinus smoke triangle" };
                triangle.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                triangle.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
                triangle.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                triangle.triangles = new[] { 0, 2, 1 };
                var cubeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                try
                {
                    using (var context = new RayTracingContext(RayTracingBackend.Compute, resources))
                    using (var accel = context.CreateAccelerationStructure(new AccelerationStructureOptions { buildFlags = BuildFlags.PreferFastTrace }))
                    {
                        var shader = context.CreateRayTracingShader(shaderAsset);
                        accel.AddInstance(new MeshInstanceDesc(triangle)
                        {
                            instanceID = 7,
                            mask = 1,
                            enableTriangleCulling = false
                        });
                        int cubeHandle = accel.AddInstance(new MeshInstanceDesc(cubeObject.GetComponent<MeshFilter>().sharedMesh)
                        {
                            instanceID = 19,
                            mask = 2,
                            enableTriangleCulling = false,
                            localToWorldMatrix = Matrix4x4.TRS(new Vector3(3, 0, 0), Quaternion.identity, Vector3.one * 2)
                        });
                        var rays = new[]
                        {
                            new SmokeRay(new Vector3(0.25f, 0.25f, 2), Vector3.back),
                            new SmokeRay(new Vector3(0.9f, 0.9f, 2), Vector3.back),
                            new SmokeRay(new Vector3(3, 0.1f, 3), Vector3.back),
                            new SmokeRay(new Vector3(3, 0.1f, 3), Vector3.back, 1),
                            new SmokeRay(new Vector3(0.25f, 0.25f, 2), Vector3.back, uint.MaxValue, 1),
                            new SmokeRay(new Vector3(0.25f, 0.25f, 2), Vector3.back, 0),
                            new SmokeRay(new Vector3(0.25f, 0.25f, -2), Vector3.forward),
                            new SmokeRay(new Vector3(3, 0.1f, 3), Vector3.back, 2, 1)
                        };
                        var hits = Trace(accel, shader, rays);
                        ExpectHit(hits[0], 7, 2, "triangle front");
                        Require(hits[0].primitiveIndex == 0, "Triangle primitive index was incorrect.");
                        Require(Vector2.Distance(hits[0].barycentrics, new Vector2(0.25f, 0.25f)) < 0.001f, "Triangle barycentrics were incorrect.");
                        ExpectMiss(hits[1], "outside triangle");
                        ExpectHit(hits[2], 19, 2, "scaled and translated cube");
                        ExpectMiss(hits[3], "instance mask");
                        ExpectMiss(hits[4], "triangle maximum ray distance");
                        ExpectMiss(hits[5], "empty ray mask");
                        ExpectHit(hits[6], 7, 2, "triangle backface with culling disabled");
                        ExpectMiss(hits[7], "cube maximum ray distance");
                        report.verifiedRays += hits.Length;
                        checks.Add("GPU BVH build, triangle/cube hit and miss, instance ID, barycentrics, distance, mask, transform, culling");

                        accel.UpdateInstanceTransform(cubeHandle, Matrix4x4.Translate(new Vector3(5, 0, 0)));
                        hits = Trace(accel, shader, new[]
                        {
                            new SmokeRay(new Vector3(3, 0.1f, 3), Vector3.back),
                            new SmokeRay(new Vector3(5, 0.1f, 3), Vector3.back)
                        });
                        ExpectMiss(hits[0], "old cube transform after rebuild");
                        ExpectHit(hits[1], 19, 2.5f, "updated cube transform after rebuild");
                        report.verifiedRays += hits.Length;
                        checks.Add("GPU BVH rebuild after instance transform update");

                        accel.RemoveInstance(cubeHandle);
                        hits = Trace(accel, shader, new[] { rays[0], new SmokeRay(new Vector3(5, 0.1f, 3), Vector3.back) });
                        ExpectHit(hits[0], 7, 2, "triangle after cube removal");
                        ExpectMiss(hits[1], "removed cube after rebuild");
                        report.verifiedRays += hits.Length;
                        checks.Add("GPU BVH rebuild after instance removal");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(cubeObject);
                    UnityEngine.Object.DestroyImmediate(triangle);
                }

                // Readback above synchronizes actual GPU execution; also reject any
                // Unity/driver error logged while building or dispatching the kernels.
                Require(errors.Count == 0, string.Join("\n", errors));
                CheckShader(shaderAsset, ShaderPath);
                report.passed = true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                Application.logMessageReceived -= callback;
                report.checks = checks.ToArray();
                File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }

            Debug.Log("FRAXINUS_GPU_SMOKE " + (report.passed ? "PASS" : "FAIL") + " device=" + report.device + " rays=" + report.verifiedRays + " report=" + Path.GetFullPath(ReportPath));
            if (Application.isBatchMode)
                EditorApplication.Exit(report.passed ? 0 : 1);
            else if (!report.passed)
                throw new Exception(report.error);
        }

        static SmokeHit[] Trace(IRayTracingAccelStruct accel, IRayTracingShader shader, SmokeRay[] rays)
        {
            using (var raysBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rays.Length, Marshal.SizeOf<SmokeRay>()))
            using (var hitsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rays.Length, Marshal.SizeOf<SmokeHit>()))
            using (var scratch = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(accel, shader, (uint)rays.Length, 1, 1))
            using (var cmd = new CommandBuffer { name = "Fraxinus real GPU ray hit smoke" })
            {
                raysBuffer.SetData(rays);
                // Poison results so a skipped dispatch cannot look like a passing miss.
                var hits = new SmokeHit[rays.Length];
                for (int i = 0; i < hits.Length; ++i)
                    hits[i].instanceID = 0xBADBADu;
                hitsBuffer.SetData(hits);
                accel.Build(cmd, scratch);
                shader.SetAccelerationStructure(cmd, "_AccelStruct", accel);
                shader.SetBufferParam(cmd, Shader.PropertyToID("_Rays"), raysBuffer);
                shader.SetBufferParam(cmd, Shader.PropertyToID("_Hits"), hitsBuffer);
                shader.Dispatch(cmd, scratch, (uint)rays.Length, 1, 1);
                Graphics.ExecuteCommandBuffer(cmd);
                hitsBuffer.GetData(hits);
                return hits;
            }
        }

        static void CheckShader(ComputeShader shader, string label)
        {
            Require(shader != null, "Compute shader resource failed to load: " + label);
            // Keep the harness compatible with editor versions where the compiler
            // message type is exposed from a different assembly or namespace.
            var method = typeof(ShaderUtil).GetMethod("GetComputeShaderMessages", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(ComputeShader) }, null);
            Require(method != null, "Cannot inspect compute shader compiler messages in this editor.");
            var messages = (IEnumerable)method.Invoke(null, new object[] { shader });
            foreach (var message in messages)
            {
                var type = message.GetType();
                var severity = type.GetProperty("severity")?.GetValue(message) ?? type.GetField("severity")?.GetValue(message);
                var text = type.GetProperty("message")?.GetValue(message) ?? type.GetField("message")?.GetValue(message);
                Require(!string.Equals(severity?.ToString(), "Error", StringComparison.OrdinalIgnoreCase), label + ": " + text);
            }
        }

        static void ExpectHit(SmokeHit hit, uint id, float distance, string label)
        {
            Require(hit.instanceID == id, label + ": expected instance " + id + ", got " + hit.instanceID);
            Require(!float.IsNaN(hit.distance) && Mathf.Abs(hit.distance - distance) < 0.001f,
                label + ": expected distance " + distance + ", got " + hit.distance);
        }

        static void ExpectMiss(SmokeHit hit, string label)
        {
            Require(hit.instanceID == uint.MaxValue, label + ": expected miss, got instance " + hit.instanceID);
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}


