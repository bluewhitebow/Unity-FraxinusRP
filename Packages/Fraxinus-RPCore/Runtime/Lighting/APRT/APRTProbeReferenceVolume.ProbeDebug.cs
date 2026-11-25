using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    internal sealed partial class APRTProbeReferenceVolume
    {
        enum APRTProbeDebugColorMode { Positions, RelitLighting, Validity, BakedTransferRGB, SkyVisibility }

        APRTProbeDebugColorMode m_ProbeDebugColorMode;
        Mesh m_ProbeDebugMesh;
        Material m_ProbeDebugMaterial;

        // Loaded sets draw their own buffers with the coordinator's live UI
        // settings, including sets loaded after the display was enabled.
        ProbeVolumeDebug probeDebugSettings => (m_RuntimeOwner ?? this).m_Debug;
        APRTProbeDebugColorMode probeDebugColorMode => (m_RuntimeOwner ?? this).m_ProbeDebugColorMode;
        internal bool showProbeDebug => probeDebugSettings.drawProbes;

        // One camera's immutable draw inputs. RenderGraph declares every buffer
        // read before the command is recorded; no GPU readback is needed.
        internal sealed class ProbeDebugDraw
        {
            internal readonly Mesh mesh;
            internal readonly Material material;
            internal readonly MaterialPropertyBlock properties;
            internal readonly GraphicsBuffer[] buffers;
            internal readonly int probeCount;
            internal readonly APRTBakedData data;

            internal ProbeDebugDraw(Mesh mesh, Material material, MaterialPropertyBlock properties,
                GraphicsBuffer[] buffers, int probeCount, APRTBakedData data)
            {
                this.mesh = mesh;
                this.material = material;
                this.properties = properties;
                this.buffers = buffers;
                this.probeCount = probeCount;
                this.data = data;
            }
        }

        internal bool TryGetProbeDebugDraw(Camera camera, APRTRelightSettings settings, out ProbeDebugDraw draw)
        {
            draw = null;
            if (!showProbeDebug || !m_Initialized || !m_Enabled || camera == null ||
                camera.cameraType == CameraType.Reflection || camera.cameraType == CameraType.Preview ||
                !SystemInfo.supportsInstancing || SystemInfo.graphicsShaderLevel < 45 ||
                probeCount <= 0 || m_RuntimeData == null || m_RuntimeData.formatVersion != APRTBakedData.k_CurrentVersion ||
                currentBakingSet == null || !ReferenceEquals(currentBakingSet.GetBakedData(k_BackendId), m_RuntimeData) ||
                m_PositionBuffer == null || m_ValidityBuffer == null || m_AxisBuffer == null ||
                runtimeSHBuffer == null || runtimeSkyIrradianceBuffer == null)
                return false;

            var target = camera.targetTexture;
            if (target != null && (target.format == RenderTextureFormat.Depth ||
                target.dimension == TextureDimension.Cube || target.dimension == TextureDimension.CubeArray))
                return false;

            // The buffers are defined immediately after upload, even when the
            // first relight is only recorded. The graph orders its write before
            // this draw's reads, so probe spheres can appear on the first frame.
            if (!EnsureProbeDebugResources()) return false;
            var buffers = new[] { m_PositionBuffer, m_ValidityBuffer, m_AxisBuffer, runtimeSHBuffer, runtimeSkyIrradianceBuffer };
            var properties = new MaterialPropertyBlock();
            properties.SetBuffer("_APRTProbeDebugPositions", buffers[0]);
            properties.SetBuffer("_APRTProbeDebugValidity", buffers[1]);
            properties.SetBuffer("_APRTProbeDebugAxis", buffers[2]);
            properties.SetBuffer("_APRTProbeDebugSH", buffers[3]);
            properties.SetBuffer("_APRTProbeDebugSkySH", buffers[4]);
            properties.SetInt("_APRTProbeDebugCount", probeCount);
            properties.SetInt("_APRTProbeDebugColorMode", (int)probeDebugColorMode);
            properties.SetInt("_APRTProbeDebugLightingModel", (int)lightingModel);
            var debug = probeDebugSettings;
            properties.SetFloat("_APRTProbeDebugRadius", Mathf.Clamp(Intensity(debug.probeSize), .001f, 10));
            float distance = Mathf.Min(Intensity(debug.probeCullingDistance), 100000);
            properties.SetFloat("_APRTProbeDebugDistanceSquared", distance * distance);
            properties.SetFloat("_APRTProbeDebugValidityThreshold", 1 - Mathf.Clamp01(settings.probeInvalidityThreshold));
            properties.SetFloat("_APRTProbeDebugExposure", ValidIntensity(debug.exposureCompensation)
                ? Mathf.Pow(2, Mathf.Clamp(debug.exposureCompensation, -20, 20)) : 1);
            properties.SetVector("_APRTProbeDebugCameraPosition", camera.transform.position);
            draw = new ProbeDebugDraw(m_ProbeDebugMesh, m_ProbeDebugMaterial, properties, buffers, probeCount, m_RuntimeData);
            return true;
        }

        internal bool IsProbeDebugDrawValid(ProbeDebugDraw draw)
        {
            if (!showProbeDebug || !m_Initialized || !m_Enabled || draw == null ||
                draw.mesh == null || draw.material == null || draw.properties == null ||
                !ReferenceEquals(draw.mesh, m_ProbeDebugMesh) || !ReferenceEquals(draw.material, m_ProbeDebugMaterial) ||
                draw.probeCount != probeCount || !ReferenceEquals(draw.data, m_RuntimeData) ||
                m_RuntimeData == null || m_RuntimeData.formatVersion != APRTBakedData.k_CurrentVersion ||
                currentBakingSet == null || !ReferenceEquals(currentBakingSet.GetBakedData(k_BackendId), m_RuntimeData) ||
                draw.buffers == null || draw.buffers.Length != 5)
                return false;
            return ReferenceEquals(draw.buffers[0], m_PositionBuffer) &&
                ReferenceEquals(draw.buffers[1], m_ValidityBuffer) &&
                ReferenceEquals(draw.buffers[2], m_AxisBuffer) &&
                ReferenceEquals(draw.buffers[3], runtimeSHBuffer) &&
                ReferenceEquals(draw.buffers[4], runtimeSkyIrradianceBuffer) &&
                m_PositionBuffer != null && m_ValidityBuffer != null && m_AxisBuffer != null &&
                runtimeSHBuffer != null && runtimeSkyIrradianceBuffer != null;
        }

        internal void DrawProbeDebug(RasterCommandBuffer cmd, ProbeDebugDraw draw)
        {
            if (cmd == null || !IsProbeDebugDrawValid(draw)) return;
            cmd.DrawMeshInstancedProcedural(draw.mesh, 0, draw.material, 0, draw.probeCount, draw.properties);
        }

        bool EnsureProbeDebugResources()
        {
            if (m_ProbeDebugMaterial == null)
            {
                var shader = Resources.Load<Shader>("APRTProbeDebug");
                if (shader == null || !shader.isSupported) return false;
                m_ProbeDebugMaterial = new Material(shader)
                {
                    name = "APRT Probe Debug", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true
                };
            }
            if (m_ProbeDebugMesh == null) m_ProbeDebugMesh = CreateProbeDebugSphere();
            return true;
        }

        static Mesh CreateProbeDebugSphere()
        {
            // An icosphere with one subdivision: 42 vertices, 80 triangles.
            // Smooth radial normals expose the L1 response over the whole sphere.
            float t = (1 + Mathf.Sqrt(5)) * .5f;
            var vertices = new List<Vector3>
            {
                new Vector3(-1,t,0).normalized, new Vector3(1,t,0).normalized,
                new Vector3(-1,-t,0).normalized, new Vector3(1,-t,0).normalized,
                new Vector3(0,-1,t).normalized, new Vector3(0,1,t).normalized,
                new Vector3(0,-1,-t).normalized, new Vector3(0,1,-t).normalized,
                new Vector3(t,0,-1).normalized, new Vector3(t,0,1).normalized,
                new Vector3(-t,0,-1).normalized, new Vector3(-t,0,1).normalized
            };
            int[] faces =
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };
            var midpoints = new Dictionary<int, int>();
            int Midpoint(int a, int b)
            {
                int key = (Mathf.Min(a, b) << 16) | Mathf.Max(a, b);
                if (midpoints.TryGetValue(key, out int index)) return index;
                index = vertices.Count;
                vertices.Add((vertices[a] + vertices[b]).normalized);
                midpoints.Add(key, index);
                return index;
            }
            var triangles = new int[faces.Length * 4];
            int output = 0;
            for (int face = 0; face < faces.Length; face += 3)
            {
                int a = faces[face], b = faces[face + 1], c = faces[face + 2];
                int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                triangles[output++] = a; triangles[output++] = ab; triangles[output++] = ca;
                triangles[output++] = b; triangles[output++] = bc; triangles[output++] = ab;
                triangles[output++] = c; triangles[output++] = ca; triangles[output++] = bc;
                triangles[output++] = ab; triangles[output++] = bc; triangles[output++] = ca;
            }
            var positions = vertices.ToArray();
            var mesh = new Mesh
            {
                name = "APRT Probe Debug Sphere", hideFlags = HideFlags.HideAndDontSave,
                vertices = positions, normals = positions, triangles = triangles,
                bounds = new Bounds(Vector3.zero, Vector3.one * 2)
            };
            mesh.UploadMeshData(true);
            return mesh;
        }

        DebugUI.Foldout CreateProbeDebugWidgets()
        {
            var display = new DebugUI.Foldout { displayName = "Probe display", opened = false };
            display.children.Add(new DebugUI.BoolField
            {
                displayName = "Show Probes",
                tooltip = "Draw probe spheres in eligible Scene and Game cameras. This is independent of the receiver diagnostic view and remains available at zero GI Intensity.",
                getter = () => m_Debug.drawProbes,
                setter = value => { m_Debug.drawProbes = value; RefreshProbeDebug(); }
            });
            var settings = new DebugUI.Container { isHiddenCallback = () => !showProbeDebug };
            settings.children.Add(new DebugUI.FloatField
            {
                displayName = "Probe Size", tooltip = "Sphere radius in world units.",
                getter = () => m_Debug.probeSize,
                setter = value => { m_Debug.probeSize = value; RefreshProbeDebug(); }, min = () => .001f, max = () => 10
            });
            settings.children.Add(new DebugUI.FloatField
            {
                displayName = "Draw Distance", tooltip = "Maximum distance from the camera to a probe center, in world units. Zero hides all spheres.",
                getter = () => m_Debug.probeCullingDistance,
                setter = value => { m_Debug.probeCullingDistance = value; RefreshProbeDebug(); }, min = () => 0, max = () => 100000
            });
            settings.children.Add(new DebugUI.EnumField
            {
                displayName = "Color Mode", autoEnum = typeof(APRTProbeDebugColorMode),
                tooltip = "Positions: cyan. Relit Lighting: raw directional and sky lighting. Validity: red = invalid / green = valid. Baked Transfer RGB: static transport. Sky Visibility: legacy visibility, black = blocked / white = open.",
                getter = () => (int)m_ProbeDebugColorMode,
                setter = SetProbeDebugColorMode,
                getIndex = () => (int)m_ProbeDebugColorMode,
                setIndex = SetProbeDebugColorMode
            });
            settings.children.Add(new DebugUI.FloatField
            {
                displayName = "Exposure", tooltip = "Exposure in stops for Relit Lighting and Baked Transfer RGB. It only changes probe visualization.",
                isHiddenCallback = () => m_ProbeDebugColorMode != APRTProbeDebugColorMode.RelitLighting &&
                    m_ProbeDebugColorMode != APRTProbeDebugColorMode.BakedTransferRGB,
                getter = () => m_Debug.exposureCompensation,
                setter = value => { m_Debug.exposureCompensation = value; RefreshProbeDebug(); }, min = () => -20, max = () => 20
            });
            display.children.Add(settings);
            return display;
        }

        void SetProbeDebugColorMode(int value)
        {
            m_ProbeDebugColorMode = (APRTProbeDebugColorMode)Mathf.Clamp(value, 0, 4);
            RefreshProbeDebug();
        }

        static void RefreshProbeDebug()
        {
#if UNITY_EDITOR
            UnityEditor.SceneView.RepaintAll();
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void CleanupProbeDebugResources()
        {
            CoreUtils.Destroy(m_ProbeDebugMesh);
            CoreUtils.Destroy(m_ProbeDebugMaterial);
            m_ProbeDebugMesh = null;
            m_ProbeDebugMaterial = null;
        }
    }
}
