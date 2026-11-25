using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering
{
    // Draws private copies of source materials. No pipeline switch or scene mutation is required.
    internal sealed class APRTProbeCapture : IDisposable
    {
        struct Draw
        {
            internal Mesh mesh;
            internal Matrix4x4 transform;
            internal Material material;
            internal int submesh;
            internal Bounds bounds;
        }

        static readonly Quaternion[] s_Faces =
        {
            Quaternion.LookRotation(Vector3.right, Vector3.up), Quaternion.LookRotation(Vector3.left, Vector3.up),
            Quaternion.LookRotation(Vector3.up, Vector3.back), Quaternion.LookRotation(Vector3.down, Vector3.forward),
            Quaternion.LookRotation(Vector3.forward, Vector3.up), Quaternion.LookRotation(Vector3.back, Vector3.up)
        };
        readonly List<Draw> m_Draws = new();
        readonly List<UnityEngine.Object> m_Owned = new();
        readonly Dictionary<Material, Material> m_Materials = new();
        readonly CommandBuffer m_Command;
        readonly RenderTexture m_Target;
        readonly Texture2D m_Readback;
        readonly Shader m_Shader;
        readonly float m_Far;
        readonly float m_MissDistance;
        readonly double[] m_Weights;
        readonly double[][] m_SkyTransferTemplates;
        readonly double[] m_SkyTransferSum = new double[APRTBakedData.k_SkyIrradianceTransferCoefficientCount];
        readonly Matrix4x4 m_Projection;
        readonly APRTLightingModel m_LightingModel;
        readonly float m_SaturationPriority, m_CenterPower;

        internal APRTProbeCapture(GIContributors contributors, int resolution, float far,
            APRTLightingModel lightingModel = APRTLightingModel.DiffuseTransfer, float saturationPriority = 10, float centerPower = 5)
        {
            var targetFormat = lightingModel == APRTLightingModel.APRTV ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat;
            if (!SystemInfo.SupportsRenderTextureFormat(targetFormat))
                throw new InvalidOperationException($"APRT capture requires a {targetFormat} render target.");
            m_Shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.unity.render-pipelines.core/Editor/Lighting/APRT/APRTProbeCapture.shader") ??
                UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Packages/Fraxinus-RPCore/Editor/Lighting/APRT/APRTProbeCapture.shader");
            if (m_Shader == null || !m_Shader.isSupported)
                throw new InvalidOperationException("The APRT capture shader could not be loaded or is unsupported.");
            m_Far = far;
            // APRTV stores axial depth. Keep the miss marker separate so visibility
            // moments distinguish empty space from the >90 sky classification.
            m_MissDistance = lightingModel == APRTLightingModel.APRTV ? far * 2 : far;
            m_LightingModel = lightingModel;
            m_SaturationPriority = Mathf.Clamp(saturationPriority, 0, 10);
            m_CenterPower = Mathf.Clamp(centerPower, 0, 5);
            m_Command = new CommandBuffer { name = "APRT Geometry Capture" };
            m_Target = new RenderTexture(resolution, resolution, 24, targetFormat, RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave, name = "APRT Geometry Capture" };
            m_Target.Create();
            m_Readback = new Texture2D(resolution, resolution, lightingModel == APRTLightingModel.APRTV ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            m_Owned.Add(m_Target);
            m_Owned.Add(m_Readback);
            // SetViewProjectionMatrices performs the GPU API conversion, just like URP's
            // camera setup. A preconverted projection reverses depth twice and captures far surfaces.
            m_Projection = Matrix4x4.Perspective(90, 1, 0.01f, far);
            m_Weights = new double[resolution * resolution];
            // Cubemap pixels cover different solid angles. Weight the six cones consistently.
            for (int y = 0; y < resolution; ++y)
                for (int x = 0; x < resolution; ++x)
                {
                    double u = (x + 0.5) / resolution * 2 - 1, v = (y + 0.5) / resolution * 2 - 1;
                    m_Weights[x + resolution * y] = 1 / Math.Pow(1 + u * u + v * v, 1.5);
                }
            try
            {
                m_SkyTransferTemplates = BuildSkyTransferTemplates(resolution);
                foreach (var item in contributors.renderers)
                {
                    var filter = item.component.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    AddMesh(filter.sharedMesh, item.component.localToWorldMatrix, item.component.sharedMaterials, item.bounds);
                }
                foreach (var item in contributors.terrains)
                {
                    AddTerrain(item.component);
                    foreach (var prototype in item.treePrototypes)
                    {
                        if (prototype.component == null || prototype.instances == null) continue;
                        var filter = prototype.component.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null) continue;
                        foreach (var instance in prototype.instances)
                            AddMesh(filter.sharedMesh, instance.transform * prototype.transform,
                                prototype.component.sharedMaterials, instance.boundsWS);
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        double[][] BuildSkyTransferTemplates(int resolution)
        {
            // Normalize the quadrature over the complete sphere, rather than
            // over visible samples, so a small opening retains its energy loss.
            double fullSphereWeight = 0;
            foreach (double weight in m_Weights) fullSphereWeight += weight * 6;
            var templates = new double[6][];
            var basis = new double[9];
            for (int face = 0; face < templates.Length; ++face)
            {
                var values = templates[face] = new double[m_Weights.Length * APRTBakedData.k_SkyIrradianceTransferCoefficientCount];
                for (int y = 0; y < resolution; ++y)
                    for (int x = 0; x < resolution; ++x)
                    {
                        float u = 2f * (x + 0.5f) / resolution - 1;
                        float v = 2f * (y + 0.5f) / resolution - 1;
                        // APRTV uses this raster convention for capture.
                        // ReadPixels returns bottom-up rows for this logical
                        // projection, including on top-origin graphics APIs.
                        // Texture UV origin must not flip the world-space ray.
                        Vector3 ray = s_Faces[face] * new Vector3(u, v, 1).normalized;
                        double rx = ray.x, ry = ray.y, rz = ray.z;
                        basis[0] = 1; basis[1] = ry; basis[2] = rz; basis[3] = rx;
                        basis[4] = rx * ry; basis[5] = ry * rz;
                        basis[6] = 3 * rz * rz - 1; basis[7] = rx * rz; basis[8] = rx * rx - ry * ry;
                        int pixel = x + resolution * y;
                        int offset = pixel * APRTBakedData.k_SkyIrradianceTransferCoefficientCount;
                        double weight = m_Weights[pixel] / fullSphereWeight;
                        for (int coefficient = 0; coefficient < basis.Length; ++coefficient)
                        {
                            double value = basis[coefficient] * weight;
                            values[offset + coefficient] = value;
                            // First-order irradiance / pi convolution is 2/3;
                            // combined with the L1 radiance projection this gives
                            // 2 * omega times the sphere-average quadrature.
                            values[offset + 9 + coefficient] = 2 * rx * value;
                            values[offset + 18 + coefficient] = 2 * ry * value;
                            values[offset + 27 + coefficient] = 2 * rz * value;
                        }
                    }
            }
            return templates;
        }

        void AddMesh(Mesh mesh, Matrix4x4 transform, Material[] sources, Bounds bounds)
        {
            for (int submesh = 0; submesh < mesh.subMeshCount; ++submesh)
            {
                var source = sources.Length > 0 ? sources[Mathf.Min(submesh, sources.Length - 1)] : null;
                if (m_LightingModel != APRTLightingModel.APRTV && source != null && source.renderQueue >= (int)RenderQueue.Transparent &&
                    !source.IsKeywordEnabled("_ALPHATEST_ON") && (!source.HasProperty("_AlphaClip") || source.GetFloat("_AlphaClip") < 0.5f))
                    continue;
                m_Draws.Add(new Draw { mesh = mesh, transform = transform, material = GetMaterial(source), submesh = submesh, bounds = bounds });
            }
        }

        Material GetMaterial(Material source)
        {
            if (source != null && m_Materials.TryGetValue(source, out var cached)) return cached;
            var material = new Material(m_Shader) { hideFlags = HideFlags.HideAndDontSave };
            m_Owned.Add(material);
            if (source != null)
            {
                string map = source.HasProperty("_BaseMap") ? "_BaseMap" : source.HasProperty("_MainTex") ? "_MainTex" : null;
                if (map != null)
                {
                    material.SetTexture("_BaseMap", source.GetTexture(map) ?? Texture2D.whiteTexture);
                    if (m_LightingModel != APRTLightingModel.APRTV)
                    {
                        material.SetTextureScale("_BaseMap", source.GetTextureScale(map));
                        material.SetTextureOffset("_BaseMap", source.GetTextureOffset(map));
                    }
                }
                string color = source.HasProperty("_BaseColor") ? "_BaseColor" : m_LightingModel != APRTLightingModel.APRTV && source.HasProperty("_Color") ? "_Color" : null;
                if (color != null) material.SetColor("_BaseColor", source.GetColor(color));
                bool cutout = source.IsKeywordEnabled("_ALPHATEST_ON") || source.GetTag("RenderType", false) == "TransparentCutout" ||
                    (source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f);
                material.SetFloat("_AlphaClip", cutout && m_LightingModel != APRTLightingModel.APRTV ? 1 : 0);
                material.SetFloat("_Cutoff", source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : 0.5f);
                bool twoSided = (source.HasProperty("_Cull") && source.GetFloat("_Cull") == 0) ||
                    (source.HasProperty("_CullMode") && source.GetFloat("_CullMode") == 0) || source.doubleSidedGI;
                material.SetFloat("_TwoSided", twoSided ? 1 : 0);
                m_Materials.Add(source, material);
            }
            return material;
        }

        void AddTerrain(Terrain terrain)
        {
            var data = terrain.terrainData;
            int width = Mathf.Min(data.heightmapResolution, 257);
            int count = width * width;
            var vertices = new Vector3[count];
            var uv = new Vector2[count];
            var normals = new Vector3[count];
            for (int y = 0; y < width; ++y)
                for (int x = 0; x < width; ++x)
                {
                    int i = x + y * width;
                    float u = x / (float)(width - 1), v = y / (float)(width - 1);
                    vertices[i] = new Vector3(u * data.size.x, data.GetInterpolatedHeight(u, v), v * data.size.z);
                    normals[i] = data.GetInterpolatedNormal(u, v);
                    uv[i] = new Vector2(u, v);
                }
            var triangles = new int[(width - 1) * (width - 1) * 6];
            int cursor = 0;
            for (int y = 0; y < width - 1; ++y)
                for (int x = 0; x < width - 1; ++x)
                {
                    int a = x + y * width, b = a + width;
                    triangles[cursor++] = a; triangles[cursor++] = b; triangles[cursor++] = a + 1;
                    triangles[cursor++] = a + 1; triangles[cursor++] = b; triangles[cursor++] = b + 1;
                }
            var mesh = new Mesh { name = "APRT Terrain Capture", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            m_Owned.Add(mesh);
            mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.triangles = triangles; mesh.RecalculateBounds();
            var material = new Material(m_Shader) { hideFlags = HideFlags.HideAndDontSave };
            m_Owned.Add(material);
            material.SetFloat("_Terrain", 1);
            var controls = data.alphamapTextures;
            material.SetTexture("_Control0", controls.Length > 0 ? controls[0] : Texture2D.whiteTexture);
            material.SetTexture("_Control1", controls.Length > 1 ? controls[1] : Texture2D.blackTexture);
            material.SetTexture("_Holes", data.holesTexture != null ? data.holesTexture : Texture2D.whiteTexture);
            var layers = data.terrainLayers;
            for (int i = 0; i < 8; ++i)
            {
                var layer = i < layers.Length ? layers[i] : null;
                material.SetTexture("_Layer" + i, layer != null && layer.diffuseTexture != null ? layer.diffuseTexture : Texture2D.whiteTexture);
                var tile = layer != null ? layer.tileSize : Vector2.one;
                var offset = layer != null ? layer.tileOffset : Vector2.zero;
                material.SetVector("_Layer" + i + "_ST", new Vector4(data.size.x / Mathf.Max(tile.x, 0.0001f), data.size.z / Mathf.Max(tile.y, 0.0001f),
                    offset.x / Mathf.Max(tile.x, 0.0001f), offset.y / Mathf.Max(tile.y, 0.0001f)));
            }
            if (layers.Length > 8) Debug.LogWarning("APRT terrain capture uses the first eight terrain diffuse layers.", terrain);
            if (data.heightmapResolution > 257) Debug.LogWarning("APRT terrain capture resamples this heightfield to 257 x 257 vertices.", terrain);
            var bounds = data.bounds; bounds.center += terrain.GetPosition();
            m_Draws.Add(new Draw { mesh = mesh, transform = Matrix4x4.TRS(terrain.GetPosition(), Quaternion.identity, Vector3.one), material = material, bounds = bounds });
        }

        internal void Capture(Vector3 position, Vector4[] axes, Vector4[] moments, int probe, out float validity,
            float[] skyIrradianceTransfer = null)
        {
            if (skyIrradianceTransfer != null)
            {
                if (probe < 0 || skyIrradianceTransfer.LongLength < ((long)probe + 1) * APRTBakedData.k_SkyIrradianceTransferCoefficientCount)
                    throw new ArgumentException("The sky irradiance transfer array must contain 36 coefficients per captured probe.", nameof(skyIrradianceTransfer));
                Array.Clear(m_SkyTransferSum, 0, m_SkyTransferSum.Length);
            }
            double total = 0, backfaces = 0, nearHits = 0;
            for (int face = 0; face < 6; ++face)
            {
                var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(position, s_Faces[face], Vector3.one).inverse;
                m_Command.Clear();
                m_Command.SetRenderTarget(m_Target);
                m_Command.SetViewport(new Rect(0, 0, m_Target.width, m_Target.height));
                m_Command.DisableScissorRect();
                // ClearRenderTarget takes logical depth; Unity converts it for reversed-Z APIs.
                m_Command.ClearRenderTarget(true, true, new Color(0, 0, 0, m_MissDistance), 1);
                m_Command.SetViewProjectionMatrices(view, m_Projection);
                m_Command.SetGlobalVector("_APRTCapturePosition", new Vector4(position.x, position.y, position.z, 1));
                m_Command.SetGlobalVector("_APRTCaptureForward", s_Faces[face] * Vector3.forward);
                m_Command.SetGlobalFloat("_APRTCaptureDistance", m_Far);
                m_Command.SetGlobalFloat("_APRTReferenceCapture", m_LightingModel == APRTLightingModel.APRTV ? 1 : 0);
                UnityEditor.ShaderUtil.SetAsyncCompilation(m_Command, false);
                var planes = GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(90, 1, 0.01f, m_Far) * view);
                foreach (var draw in m_Draws)
                    if (GeometryUtility.TestPlanesAABB(planes, draw.bounds))
                        m_Command.DrawMesh(draw.mesh, draw.transform, draw.material, draw.submesh, 0);
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(m_Command);
                Graphics.ExecuteCommandBuffer(m_Command);
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = m_Target;
                    m_Readback.ReadPixels(new Rect(0, 0, m_Target.width, m_Target.height), 0, 0, false);
                }
                finally { RenderTexture.active = previous; }
                var pixels = m_Readback.GetPixels();
                double weight = 0, hitWeight = 0, skyWeight = 0, r = 0, g = 0, b = 0, distance = 0, squaredDistance = 0;
                double referenceWeight = 0, referenceR = 0, referenceG = 0, referenceB = 0;
                int referenceSkyCount = 0;
                for (int i = 0; i < pixels.Length; ++i)
                {
                    var sample = pixels[i];
                    double w = m_Weights[i];
                    bool miss = sample.a >= m_MissDistance - 0.001f;
                    if (miss && skyIrradianceTransfer != null)
                    {
                        // An APRTV hit beyond axial distance 90 is artistic sky
                        // in the legacy W channel, but remains geometry here.
                        int offset = i * APRTBakedData.k_SkyIrradianceTransferCoefficientCount;
                        var template = m_SkyTransferTemplates[face];
                        for (int coefficient = 0; coefficient < m_SkyTransferSum.Length; ++coefficient)
                            m_SkyTransferSum[coefficient] += template[offset + coefficient];
                    }
                    double radialDistance = Math.Abs(sample.a);
                    if (m_LightingModel == APRTLightingModel.APRTV && !miss)
                    {
                        // Capture stores axial depth to preserve APRTV's 90-unit sky
                        // classification. Moments still describe physical radial distance.
                        double rasterU = (i % m_Target.width + 0.5) / m_Target.width * 2 - 1;
                        double rasterV = (i / m_Target.width + 0.5) / m_Target.height * 2 - 1;
                        radialDistance *= Math.Sqrt(1 + rasterU * rasterU + rasterV * rasterV);
                    }
                    double d = Math.Min(m_Far, radialDistance);
                    weight += w; distance += w * d; squaredDistance += w * d * d;
                    if (miss) skyWeight += w;
                    else
                    {
                        hitWeight += w;
                        r += w * Mathf.Clamp01(sample.r); g += w * Mathf.Clamp01(sample.g); b += w * Mathf.Clamp01(sample.b);
                        if (sample.a < 0) backfaces += w;
                        if (d < 0.025f) nearHits += w;
                    }
                    if (m_LightingModel == APRTLightingModel.APRTV)
                    {
                        // Match the original sample location and deliberately unnormalized
                        // saturation preference. Its output is an artistic transfer value, not albedo.
                        float u = (float)(i % m_Target.width) / m_Target.width * 2 - 1;
                        float v = (float)(i / m_Target.width) / m_Target.height * 2 - 1;
                        float center = Mathf.Pow(Mathf.Max(0, 1 - Mathf.Sqrt(u * u + v * v) * 0.707f), m_CenterPower);
                        referenceWeight += center;
                        if (miss || sample.a > 90)
                        {
                            Vector3 ray = s_Faces[face] * new Vector3(u, v, 1).normalized;
                            if (ray.y > 0.05f) ++referenceSkyCount;
                        }
                        else if (sample.a >= 0)
                        {
                            float chroma = Mathf.Max(sample.r, Mathf.Max(sample.g, sample.b)) - Mathf.Min(sample.r, Mathf.Min(sample.g, sample.b));
                            double finalWeight = center * (1 + chroma * m_SaturationPriority);
                            referenceR += sample.r * finalWeight; referenceG += sample.g * finalWeight; referenceB += sample.b * finalWeight;
                        }
                    }
                }
                total += weight;
                int index = probe * 6 + face;
                axes[index] = m_LightingModel == APRTLightingModel.APRTV
                    ? new Vector4(referenceWeight > 0 ? (float)(referenceR / referenceWeight) : 0, referenceWeight > 0 ? (float)(referenceG / referenceWeight) : 0,
                        referenceWeight > 0 ? (float)(referenceB / referenceWeight) : 0, (float)referenceSkyCount / pixels.Length)
                    : new Vector4(hitWeight > 0 ? (float)(r / hitWeight) : 0,
                        hitWeight > 0 ? (float)(g / hitWeight) : 0, hitWeight > 0 ? (float)(b / hitWeight) : 0, Mathf.Clamp01((float)(skyWeight / weight)));
                moments[index] = new Vector4((float)(distance / weight), (float)(squaredDistance / weight), 0, 0);
            }
            // A large backface coverage is characteristic of a probe embedded in geometry.
            validity = nearHits / total > 0.1 ? 0 : Mathf.Clamp01((float)(1 - backfaces / total));
            if (skyIrradianceTransfer != null)
            {
                int offset = probe * APRTBakedData.k_SkyIrradianceTransferCoefficientCount;
                for (int coefficient = 0; coefficient < m_SkyTransferSum.Length; ++coefficient)
                    skyIrradianceTransfer[offset + coefficient] = (float)m_SkyTransferSum[coefficient];
            }
        }

        public void Dispose()
        {
            m_Command?.Release();
            if (m_Target != null) m_Target.Release();
            foreach (var item in m_Owned)
                if (item != null) UnityEngine.Object.DestroyImmediate(item);
            m_Owned.Clear(); m_Draws.Clear(); m_Materials.Clear();
        }
    }
}
