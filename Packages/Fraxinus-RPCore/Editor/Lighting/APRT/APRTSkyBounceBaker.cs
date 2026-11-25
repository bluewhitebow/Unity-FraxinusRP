using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering
{
    // This integrates only sky -> static diffuse surface -> probe. Its result is
    // added to the existing direct-sky matrix; it never feeds probe lighting back.
    internal sealed class APRTSkyBounceBaker : IDisposable
    {
        const int k_Coefficients = 36;
        const float k_OriginBias = 0.001f;
        const float k_MinRayDistance = 0.00001f;
        const double k_GoldenAngle = 2.39996322972865332;

        struct Triangle
        {
            internal Vector3 origin, edge1, edge2, normal0, normal1, normal2, faceNormal;
            internal Vector2 uv0, uv1, uv2;
            internal Vector3 min, max, center;
            internal int material;
        }

        struct Node
        {
            internal Vector3 min, max;
            internal int start, count, left, right;
        }

        struct Hit
        {
            internal int triangle;
            internal float distance, u, v;
        }

        sealed class TriangleComparer : IComparer<Triangle>
        {
            internal int axis;
            public int Compare(Triangle a, Triangle b) => a.center[axis].CompareTo(b.center[axis]);
        }

        sealed class Pixels
        {
            internal Color[] values;
            internal int width, height;
            internal TextureWrapMode wrapU, wrapV;
            internal bool pointFilter;

            static int Index(int index, int size, TextureWrapMode wrap)
            {
                if (wrap == TextureWrapMode.Repeat)
                    return (index % size + size) % size;
                if (wrap == TextureWrapMode.Mirror)
                {
                    int mirrored = (index % (size * 2) + size * 2) % (size * 2);
                    return mirrored < size ? mirrored : size * 2 - 1 - mirrored;
                }
                return Mathf.Clamp(index, 0, size - 1);
            }

            internal Color Sample(Vector2 uv)
            {
                if (wrapU == TextureWrapMode.MirrorOnce) uv.x = Mathf.Abs(uv.x);
                if (wrapV == TextureWrapMode.MirrorOnce) uv.y = Mathf.Abs(uv.y);
                if (pointFilter)
                    return values[Index(Mathf.FloorToInt(uv.x * width), width, wrapU) + Index(Mathf.FloorToInt(uv.y * height), height, wrapV) * width];
                float x = uv.x * width - 0.5f, y = uv.y * height - 0.5f;
                int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
                float tx = x - x0, ty = y - y0;
                int a = Index(x0, width, wrapU), b = Index(x0 + 1, width, wrapU);
                int c = Index(y0, height, wrapV), d = Index(y0 + 1, height, wrapV);
                return Color.LerpUnclamped(Color.LerpUnclamped(values[a + c * width], values[b + c * width], tx),
                    Color.LerpUnclamped(values[a + d * width], values[b + d * width], tx), ty);
            }
        }

        sealed class Surface
        {
            internal Color color = Color.white;
            internal Pixels baseMap, metallicMap;
            internal Vector2 baseScale = Vector2.one, baseOffset;
            internal Vector2 metallicScale = Vector2.one, metallicOffset;
            internal float metallic, cutoff = 0.5f;
            internal bool alphaClip, twoSided;

            internal bool Evaluate(Vector2 uv, out Vector3 diffuse)
            {
                Color texel = baseMap != null ? baseMap.Sample(Vector2.Scale(uv, baseScale) + baseOffset) : Color.white;
                Color albedo = texel * color;
                if (alphaClip && albedo.a < cutoff)
                {
                    diffuse = Vector3.zero;
                    return false;
                }
                // URP's metallic workflow takes R from the map in place of the
                // scalar metallic value. Smoothness does not affect this path.
                float metal = metallicMap != null ? metallicMap.Sample(Vector2.Scale(uv, metallicScale) + metallicOffset).r : metallic;
                float dielectric = 1 - Mathf.Clamp01(metal);
                diffuse = new Vector3(Mathf.Clamp01(albedo.r) * dielectric,
                    Mathf.Clamp01(albedo.g) * dielectric, Mathf.Clamp01(albedo.b) * dielectric);
                return true;
            }
        }

        readonly List<Surface> m_Surfaces = new();
        readonly Dictionary<Material, int> m_Materials = new();
        readonly Dictionary<Texture, Pixels> m_Textures = new();
        readonly List<Node> m_Nodes = new();
        readonly TriangleComparer m_Comparer = new();
        readonly int[] m_Stack = new int[64];
        readonly Vector3[] m_PrimaryDirections, m_HemisphereDirections;
        readonly double[] m_Basis = new double[9], m_SurfaceTransfer = new double[9], m_Transfer = new double[108];
        readonly float m_MaxDistance;
        Triangle[] m_Triangles;
        bool m_Disposed;

        internal int triangleCount => m_Triangles.Length;

        internal APRTSkyBounceBaker(GIContributors contributors, int primarySamples, int skySamples, float maxDistance)
        {
            if (primarySamples < 16 || skySamples < 16)
                throw new ArgumentOutOfRangeException(nameof(primarySamples), "Sky bounce integration requires at least 16 samples for each integral.");
            if (float.IsNaN(maxDistance) || float.IsInfinity(maxDistance) || maxDistance <= k_OriginBias)
                throw new ArgumentOutOfRangeException(nameof(maxDistance));
            // A terrain approximation can silently remove walls/holes and add
            // reflected energy. Keep this initial small-room feature explicit.
            if (contributors.terrains != null && contributors.terrains.Count != 0)
                throw new NotSupportedException("APRT offline sky bounce currently supports static mesh contributors only. Disable sky bounce for terrain baking.");
            m_MaxDistance = maxDistance;
            m_PrimaryDirections = new Vector3[primarySamples];
            m_HemisphereDirections = new Vector3[skySamples];
            for (int i = 0; i < primarySamples; ++i)
                m_PrimaryDirections[i] = SphereDirection(i, primarySamples);
            for (int i = 0; i < skySamples; ++i)
                m_HemisphereDirections[i] = CosineHemisphereDirection(i, skySamples);

            var triangles = new List<Triangle>();
            if (contributors.renderers != null)
                foreach (var item in contributors.renderers)
                {
                    var filter = item.component.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null)
                    {
                        if (item.component.HasPropertyBlock())
                            throw new NotSupportedException($"APRT offline sky bounce cannot bake MaterialPropertyBlock overrides on '{item.component.name}'. Use explicit materials for this bake.");
                        AddMesh(triangles, filter.sharedMesh, item.component.localToWorldMatrix, item.component.sharedMaterials);
                    }
                }
            m_Triangles = triangles.ToArray();
            if (m_Triangles.Length > 0) BuildNode(0, m_Triangles.Length);
        }

        internal static Vector3 SphereDirection(int sample, int count)
        {
            double y = 1 - 2 * (sample + 0.5) / count;
            double radius = Math.Sqrt(Math.Max(0, 1 - y * y)), angle = sample * k_GoldenAngle;
            return new Vector3((float)(radius * Math.Cos(angle)), (float)y, (float)(radius * Math.Sin(angle)));
        }

        internal static Vector3 CosineHemisphereDirection(int sample, int count)
        {
            double radius = Math.Sqrt((sample + 0.5) / count), angle = sample * k_GoldenAngle;
            return new Vector3((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)), (float)Math.Sqrt(1 - radius * radius));
        }

        internal static void EvaluateSkyBasis(Vector3 direction, double[] values)
        {
            double x = direction.x, y = direction.y, z = direction.z;
            values[0] = 1; values[1] = y; values[2] = z; values[3] = x;
            values[4] = x * y; values[5] = y * z; values[6] = 3 * z * z - 1;
            values[7] = x * z; values[8] = x * x - y * y;
        }

        internal void Capture(Vector3 position, Vector3[] transfer, int probe)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(APRTSkyBounceBaker));
            if (transfer == null || probe < 0 || transfer.LongLength < ((long)probe + 1) * k_Coefficients)
                throw new ArgumentException("The RGB sky bounce transfer array must contain 36 coefficients per captured probe.", nameof(transfer));
            if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z))
                throw new ArgumentException("The probe position must be finite.", nameof(position));
            Array.Clear(m_Transfer, 0, m_Transfer.Length);
            double sphereWeight = 1.0 / m_PrimaryDirections.Length;
            foreach (var direction in m_PrimaryDirections)
            {
                if (!Trace(position, direction, m_MaxDistance, out var hit)) continue;
                var triangle = m_Triangles[hit.triangle];
                var surface = m_Surfaces[triangle.material];
                float w = 1 - hit.u - hit.v;
                Vector2 uv = triangle.uv0 * w + triangle.uv1 * hit.u + triangle.uv2 * hit.v;
                surface.Evaluate(uv, out var diffuse);
                if (diffuse == Vector3.zero) continue;
                Vector3 normal = triangle.normal0 * w + triangle.normal1 * hit.u + triangle.normal2 * hit.v;
                if (normal.sqrMagnitude < 0.000001f) normal = triangle.faceNormal;
                normal.Normalize();
                bool backface = Vector3.Dot(triangle.faceNormal, direction) >= 0;
                if (backface && !surface.twoSided) continue;
                if (backface) normal = -normal;
                if (Vector3.Dot(normal, -direction) <= 0) normal = backface ? -triangle.faceNormal : triangle.faceNormal;
                Vector3 point = position + direction * hit.distance;
                // Offset along the geometric normal, not the smooth normal, so
                // a smooth shading normal cannot move the origin into a wall.
                Vector3 geometricNormal = backface ? -triangle.faceNormal : triangle.faceNormal;
                IntegrateSurfaceSky(point + geometricNormal * k_OriginBias, normal, geometricNormal);
                for (int coefficient = 0; coefficient < 9; ++coefficient)
                {
                    double value = m_SurfaceTransfer[coefficient] * sphereWeight;
                    AddTransfer(coefficient, diffuse, value);
                    // L1 irradiance/pi is sphere-average radiance times 2ω,
                    // matching APRTProbeCapture's direct-sky transfer layout.
                    AddTransfer(9 + coefficient, diffuse, value * 2 * direction.x);
                    AddTransfer(18 + coefficient, diffuse, value * 2 * direction.y);
                    AddTransfer(27 + coefficient, diffuse, value * 2 * direction.z);
                }
            }
            int offset = probe * k_Coefficients;
            for (int i = 0; i < k_Coefficients; ++i)
            {
                var value = new Vector3((float)m_Transfer[i * 3], (float)m_Transfer[i * 3 + 1], (float)m_Transfer[i * 3 + 2]);
                if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z))
                    throw new InvalidOperationException("The sky bounce integral generated a non-finite transfer coefficient.");
                transfer[offset + i] = value;
            }
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        void AddTransfer(int coefficient, Vector3 diffuse, double value)
        {
            int offset = coefficient * 3;
            m_Transfer[offset] += value * diffuse.x;
            m_Transfer[offset + 1] += value * diffuse.y;
            m_Transfer[offset + 2] += value * diffuse.z;
        }

        void IntegrateSurfaceSky(Vector3 position, Vector3 normal, Vector3 geometricNormal)
        {
            Array.Clear(m_SurfaceTransfer, 0, m_SurfaceTransfer.Length);
            Vector3 tangent = Vector3.Cross(Mathf.Abs(normal.y) < 0.99f ? Vector3.up : Vector3.right, normal).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent);
            double weight = 1.0 / m_HemisphereDirections.Length;
            foreach (var sample in m_HemisphereDirections)
            {
                Vector3 direction = tangent * sample.x + bitangent * sample.y + normal * sample.z;
                // Interpolated mesh normals may tilt across the actual surface.
                // Their below-surface hemisphere is opaque, not an escape path.
                if (Vector3.Dot(direction, geometricNormal) <= 0) continue;
                if (Trace(position, direction, m_MaxDistance, out _)) continue;
                EvaluateSkyBasis(direction, m_Basis);
                // Cosine-weighted sampling cancels cosθ/π exactly. This is the
                // surface's reflected radiance matrix before multiplying rho.
                for (int i = 0; i < 9; ++i) m_SurfaceTransfer[i] += m_Basis[i] * weight;
            }
        }

        int GetSurface(Material source)
        {
            if (source != null && m_Materials.TryGetValue(source, out int cached)) return cached;
            var surface = new Surface();
            if (source != null)
            {
                bool cutout = source.IsKeywordEnabled("_ALPHATEST_ON") || source.GetTag("RenderType", false) == "TransparentCutout" ||
                    (source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f);
                bool transparent = source.renderQueue >= (int)RenderQueue.Transparent ||
                    (source.HasProperty("_Surface") && source.GetFloat("_Surface") > 0.5f);
                if (transparent && !cutout) return -1;
                string colorProperty = source.HasProperty("_BaseColor") ? "_BaseColor" : source.HasProperty("_Color") ? "_Color" : null;
                // Authored material colors are sRGB; shader Color properties are
                // converted to linear in a linear project. Integrate physically
                // linear reflectance independently of the runtime sky source.
                if (colorProperty != null)
                {
                    Color color = source.GetColor(colorProperty);
                    int property = source.shader != null ? source.shader.FindPropertyIndex(colorProperty) : -1;
                    bool alreadyLinear = property >= 0 && (source.shader.GetPropertyFlags(property) & ShaderPropertyFlags.HDR) != 0;
                    surface.color = alreadyLinear ? color : color.linear;
                }
                string mapProperty = source.HasProperty("_BaseMap") ? "_BaseMap" : source.HasProperty("_MainTex") ? "_MainTex" : null;
                if (mapProperty != null)
                {
                    surface.baseMap = GetPixels(source.GetTexture(mapProperty));
                    surface.baseScale = source.GetTextureScale(mapProperty);
                    surface.baseOffset = source.GetTextureOffset(mapProperty);
                }
                surface.alphaClip = cutout;
                surface.cutoff = source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : 0.5f;
                surface.twoSided = source.doubleSidedGI ||
                    (source.HasProperty("_Cull") && source.GetFloat("_Cull") == 0) ||
                    (source.HasProperty("_CullMode") && source.GetFloat("_CullMode") == 0);
                surface.metallic = source.HasProperty("_Metallic") ? source.GetFloat("_Metallic") : 0;
                if ((source.IsKeywordEnabled("_METALLICSPECGLOSSMAP") || source.IsKeywordEnabled("_METALLICGLOSSMAP")) && source.HasProperty("_MetallicGlossMap"))
                {
                    surface.metallicMap = GetPixels(source.GetTexture("_MetallicGlossMap"));
                    surface.metallicScale = surface.baseScale;
                    surface.metallicOffset = surface.baseOffset;
                }
                if (source.IsKeywordEnabled("_SPECULAR_SETUP") || (source.shader != null && source.shader.name == "Standard (Specular setup)"))
                    throw new NotSupportedException($"APRT offline sky bounce currently supports the metallic diffuse workflow; '{source.name}' uses specular setup.");
                if (!Finite(surface.color.r) || !Finite(surface.color.g) || !Finite(surface.color.b) || !Finite(surface.color.a) ||
                    !Finite(surface.metallic) || !Finite(surface.cutoff) ||
                    !Finite(surface.baseScale.x) || !Finite(surface.baseScale.y) || !Finite(surface.baseOffset.x) || !Finite(surface.baseOffset.y))
                    throw new InvalidOperationException($"Material '{source.name}' has non-finite sky bounce inputs.");
            }
            int result = m_Surfaces.Count;
            m_Surfaces.Add(surface);
            if (source != null) m_Materials.Add(source, result);
            return result;
        }

        Pixels GetPixels(Texture source)
        {
            if (source == null) return null;
            if (m_Textures.TryGetValue(source, out var cached)) return cached;
            if (!(source is Texture2D))
                throw new NotSupportedException($"APRT sky bounce needs a static Texture2D material map, but '{source.name}' is {source.GetType().Name}.");
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                throw new NotSupportedException("APRT sky bounce material readback requires an ARGBFloat render target.");
            var previous = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            RenderTexture target = null;
            Texture2D readback = null;
            try
            {
                target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                readback = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
                GL.sRGBWrite = false;
                // Sampling an sRGB source decodes it once. The linear target and
                // readback preserve that result; do not call Color.linear again.
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readback.Apply(false, false);
                var pixels = readback.GetPixels();
                // Gamma rendering disables texture decoding. Offline transport
                // still requires linear diffuse reflectance in that project.
                bool gammaDecode = source.isDataSRGB && !GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
                for (int i = 0; i < pixels.Length; ++i)
                {
                    if (gammaDecode) pixels[i] = pixels[i].linear;
                    if (!Finite(pixels[i].r) || !Finite(pixels[i].g) || !Finite(pixels[i].b) || !Finite(pixels[i].a))
                        throw new InvalidOperationException($"Texture '{source.name}' contains non-finite sky bounce inputs.");
                }
                var result = new Pixels { values = pixels, width = source.width, height = source.height, wrapU = source.wrapModeU, wrapV = source.wrapModeV,
                    pointFilter = source.filterMode == FilterMode.Point };
                m_Textures.Add(source, result);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgbWrite;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        void AddMesh(List<Triangle> output, Mesh mesh, Matrix4x4 transform, Material[] materials)
        {
            // MeshData is available for imported meshes even if Read/Write is
            // disabled. Do not change their import settings or scene ownership.
            using var dataArray = UnityEditor.MeshUtility.AcquireReadOnlyMeshData(mesh);
            var data = dataArray[0];
            using var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
            using var normals = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
            using var uv = new NativeArray<Vector2>(data.vertexCount, Allocator.Temp);
            data.GetVertices(vertices);
            bool hasNormals = data.HasVertexAttribute(VertexAttribute.Normal);
            bool hasUV = data.HasVertexAttribute(VertexAttribute.TexCoord0);
            if (hasNormals) data.GetNormals(normals);
            if (hasUV) data.GetUVs(0, uv);
            Matrix4x4 normalTransform = transform.inverse.transpose;
            float orientation = transform.determinant < 0 ? -1 : 1;
            for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
            {
                var descriptor = data.GetSubMesh(submesh);
                if (descriptor.topology != MeshTopology.Triangles)
                    throw new NotSupportedException($"APRT sky bounce requires triangle meshes; '{mesh.name}' submesh {submesh} uses {descriptor.topology}.");
                var material = materials.Length > 0 ? materials[Mathf.Min(submesh, materials.Length - 1)] : null;
                int surface = GetSurface(material);
                if (surface < 0) continue;
                using var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp);
                data.GetIndices(indices, submesh, true);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    Vector3 p0 = transform.MultiplyPoint3x4(vertices[a]), p1 = transform.MultiplyPoint3x4(vertices[b]), p2 = transform.MultiplyPoint3x4(vertices[c]);
                    Vector3 edge1 = p1 - p0, edge2 = p2 - p0;
                    Vector3 geometricNormal = Vector3.Cross(edge1, edge2) * orientation;
                    if (geometricNormal.sqrMagnitude < 1e-16f) continue;
                    geometricNormal.Normalize();
                    if (!Finite(p0.x) || !Finite(p0.y) || !Finite(p0.z) || !Finite(p1.x) || !Finite(p1.y) || !Finite(p1.z) ||
                        !Finite(p2.x) || !Finite(p2.y) || !Finite(p2.z))
                        throw new InvalidOperationException($"Mesh '{mesh.name}' has non-finite sky bounce geometry.");
                    Vector3 min = Vector3.Min(p0, Vector3.Min(p1, p2)), max = Vector3.Max(p0, Vector3.Max(p1, p2));
                    output.Add(new Triangle
                    {
                        origin = p0, edge1 = edge1, edge2 = edge2,
                        normal0 = hasNormals ? normalTransform.MultiplyVector(normals[a]).normalized : geometricNormal,
                        normal1 = hasNormals ? normalTransform.MultiplyVector(normals[b]).normalized : geometricNormal,
                        normal2 = hasNormals ? normalTransform.MultiplyVector(normals[c]).normalized : geometricNormal,
                        faceNormal = geometricNormal,
                        uv0 = hasUV ? uv[a] : Vector2.zero, uv1 = hasUV ? uv[b] : Vector2.zero, uv2 = hasUV ? uv[c] : Vector2.zero,
                        min = min - Vector3.one * k_MinRayDistance, max = max + Vector3.one * k_MinRayDistance,
                        center = (min + max) * 0.5f, material = surface
                    });
                }
            }
        }

        int BuildNode(int start, int count)
        {
            Vector3 min = m_Triangles[start].min, max = m_Triangles[start].max;
            Vector3 centerMin = m_Triangles[start].center, centerMax = centerMin;
            for (int i = start + 1; i < start + count; ++i)
            {
                min = Vector3.Min(min, m_Triangles[i].min); max = Vector3.Max(max, m_Triangles[i].max);
                centerMin = Vector3.Min(centerMin, m_Triangles[i].center); centerMax = Vector3.Max(centerMax, m_Triangles[i].center);
            }
            int index = m_Nodes.Count;
            var node = new Node { min = min, max = max, start = start, count = count };
            m_Nodes.Add(node);
            if (count > 6)
            {
                Vector3 size = centerMax - centerMin;
                m_Comparer.axis = size.x > size.y ? (size.x > size.z ? 0 : 2) : (size.y > size.z ? 1 : 2);
                Array.Sort(m_Triangles, start, count, m_Comparer);
                int split = count / 2;
                node.left = BuildNode(start, split);
                node.right = BuildNode(start + split, count - split);
                node.count = 0;
                m_Nodes[index] = node;
            }
            return index;
        }

        static bool BoundsHit(Node node, Vector3 origin, Vector3 direction, float maxDistance)
        {
            float near = 0, far = maxDistance;
            for (int axis = 0; axis < 3; ++axis)
            {
                float o = origin[axis], d = direction[axis];
                if (Mathf.Abs(d) < 1e-10f)
                {
                    if (o < node.min[axis] || o > node.max[axis]) return false;
                    continue;
                }
                float a = (node.min[axis] - o) / d, b = (node.max[axis] - o) / d;
                near = Mathf.Max(near, Mathf.Min(a, b)); far = Mathf.Min(far, Mathf.Max(a, b));
                if (far < near) return false;
            }
            return true;
        }

        bool Trace(Vector3 origin, Vector3 direction, float maxDistance, out Hit result)
        {
            result = new Hit { triangle = -1, distance = maxDistance };
            if (m_Nodes.Count == 0) return false;
            int stackSize = 1;
            m_Stack[0] = 0;
            while (stackSize > 0)
            {
                var node = m_Nodes[m_Stack[--stackSize]];
                if (!BoundsHit(node, origin, direction, result.distance)) continue;
                if (node.count == 0)
                {
                    m_Stack[stackSize++] = node.left;
                    m_Stack[stackSize++] = node.right;
                    continue;
                }
                for (int i = node.start; i < node.start + node.count; ++i)
                {
                    var triangle = m_Triangles[i];
                    Vector3 p = Vector3.Cross(direction, triangle.edge2);
                    float determinant = Vector3.Dot(triangle.edge1, p);
                    if (Mathf.Abs(determinant) < 1e-10f) continue;
                    float inverse = 1 / determinant;
                    Vector3 from = origin - triangle.origin;
                    float u = Vector3.Dot(from, p) * inverse;
                    if (u < 0 || u > 1) continue;
                    Vector3 q = Vector3.Cross(from, triangle.edge1);
                    float v = Vector3.Dot(direction, q) * inverse;
                    if (v < 0 || u + v > 1) continue;
                    float distance = Vector3.Dot(triangle.edge2, q) * inverse;
                    if (distance < k_MinRayDistance || distance >= result.distance) continue;
                    if (m_Surfaces[triangle.material].alphaClip)
                    {
                        Vector2 uv = triangle.uv0 * (1 - u - v) + triangle.uv1 * u + triangle.uv2 * v;
                        if (!m_Surfaces[triangle.material].Evaluate(uv, out _)) continue;
                    }
                    result = new Hit { triangle = i, distance = distance, u = u, v = v };
                }
            }
            return result.triangle >= 0;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            m_Textures.Clear(); m_Materials.Clear(); m_Surfaces.Clear(); m_Nodes.Clear();
            m_Triangles = Array.Empty<Triangle>();
        }
    }
}
