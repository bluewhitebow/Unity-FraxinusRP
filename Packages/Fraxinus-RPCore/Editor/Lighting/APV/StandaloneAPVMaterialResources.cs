using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
using UnityEngine.Rendering.UnifiedRayTracing;
#endif

namespace UnityEngine.Rendering
{
    // Material data shared by lighting, virtual offset and sky visibility. Texture
    // copies are GPU-only: imported textures never need their Read/Write flag changed.
    internal sealed class StandaloneAPVMaterialResources : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BakeMaterial
        {
            public Vector4 albedo, emission;
            public Vector4 baseST, emissionST;
            // Texture slice (-1 is white), wrap U, wrap V, filter mode.
            public Vector4 baseMap, emissionMap;
            // Cutoff (-1 is opaque), ignore texture alpha, double-sided GI, APRT cull mode.
            public Vector4 surface;
        }

        const long k_TextureBudget = 128L * 1024 * 1024;
        internal const int k_APRTSourceTextureLimit = 64;
        readonly bool m_OpacityOnly;
        readonly bool m_UseMetaPass;
        readonly bool m_PreserveSourceTextures;
        readonly List<BakeMaterial> m_Materials = new List<BakeMaterial>();
        readonly Dictionary<Material, uint> m_MaterialIDs = new Dictionary<Material, uint>();
        readonly List<Texture> m_Textures = new List<Texture>();
        readonly Dictionary<Texture, int> m_TextureIDs = new Dictionary<Texture, int>();
        readonly List<RenderTexture> m_MetaTextures = new List<RenderTexture>();
        GraphicsBuffer m_Buffer;
        GraphicsBuffer m_SourceTextureInfo;
        RenderTexture m_TextureArray;
        Mesh m_MetaQuad;
        bool m_HasAlphaClipping;

        static readonly int s_Materials = Shader.PropertyToID("_APVMaterials");
        static readonly int s_Textures = Shader.PropertyToID("_APVMaterialTextures");
        static readonly int s_HasAlphaClipping = Shader.PropertyToID("_APVHasAlphaClipping");
        static readonly int s_SourceTextureInfo = Shader.PropertyToID("_APRTSourceTextureInfo");
        static readonly int[] s_SourceTextures = CreateSourceTextureIDs();

        public StandaloneAPVMaterialResources(bool opacityOnly = false, bool useMetaPass = true, bool preserveSourceTextures = false)
        {
            m_OpacityOnly = opacityOnly;
            m_UseMetaPass = useMetaPass;
            m_PreserveSourceTextures = preserveSourceTextures;
            m_Materials.Add(new BakeMaterial
            {
                albedo = Vector4.one,
                baseST = new Vector4(1, 1, 0, 0),
                emissionST = new Vector4(1, 1, 0, 0),
                baseMap = new Vector4(-1, 0, 0, 0),
                emissionMap = new Vector4(-1, 0, 0, 0),
                surface = new Vector4(-1, 0, 0, m_PreserveSourceTextures ? 2 : 0)
            });
        }

        static int[] CreateSourceTextureIDs()
        {
            var ids = new int[k_APRTSourceTextureLimit];
            for (int i = 0; i < ids.Length; ++i)
                ids[i] = Shader.PropertyToID("_APRTSourceTexture" + i);
            return ids;
        }

        static string BaseMapProperty(Material material)
            => material.HasProperty("_BaseMap") ? "_BaseMap" :
               material.HasProperty("_BaseColorMap") ? "_BaseColorMap" :
               material.HasProperty("_MainTex") ? "_MainTex" : null;

        static bool IsAlphaClipped(Material material)
            => material.GetTag("RenderType", false, "Opaque") == "TransparentCutout" ||
               material.IsKeywordEnabled("_ALPHATEST_ON") ||
               (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f);

        static Texture GetTexture(Material material, string property)
            => property != null && material.HasProperty(property) ? material.GetTexture(property) : null;

        internal static bool TryValidate(Material material, out string reason, bool includeEmission = true)
        {
            reason = null;
            if (material == null)
                reason = "A contributing renderer has no material.";
            else if (material.GetTag("RenderType", false, "Opaque") == "Transparent" ||
                (material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f))
                reason = $"Material '{material.name}' uses alpha blending; standalone baking supports opaque and alpha-clipped surfaces.";
            else
            {
                var baseTexture = GetTexture(material, BaseMapProperty(material));
                var emissionTexture = includeEmission ? GetTexture(material, "_EmissionMap") : null;
                if ((baseTexture != null && baseTexture.dimension != TextureDimension.Tex2D) ||
                    (emissionTexture != null && emissionTexture.dimension != TextureDimension.Tex2D))
                    reason = $"Material '{material.name}' requires a 2D base/emission texture.";
            }
            return reason == null;
        }

        Vector4 RegisterTexture(Texture texture)
        {
            if (texture == null)
                return new Vector4(-1, 0, 0, 0);
            if (!m_TextureIDs.TryGetValue(texture, out int index))
            {
                index = m_Textures.Count;
                m_Textures.Add(texture);
                m_TextureIDs.Add(texture, index);
            }
            return new Vector4(index, (int)texture.wrapModeU, (int)texture.wrapModeV, (int)texture.filterMode);
        }

        static Vector4 TextureST(Material material, string property)
        {
            if (property == null) return new Vector4(1, 1, 0, 0);
            Vector2 scale = material.GetTextureScale(property), offset = material.GetTextureOffset(property);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        Mesh GetMetaQuad()
        {
            if (m_MetaQuad != null)
                return m_MetaQuad;

            m_MetaQuad = new Mesh { name = "Standalone APV Meta quad", hideFlags = HideFlags.HideAndDontSave };
            m_MetaQuad.vertices = new[]
            {
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
                new Vector3(-1, 1, 0), new Vector3(1, 1, 0)
            };
            m_MetaQuad.normals = new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
            var uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(1, 0) };
            m_MetaQuad.uv = uv;
            // Universal Meta passes use TEXCOORD1/TEXCOORD2 for the raster
            // position. The native MaterialPool fills TEXCOORD1; filling both
            // keeps custom Meta passes that read TEXCOORD2 deterministic.
            m_MetaQuad.uv2 = uv;
            m_MetaQuad.uv3 = uv;
            m_MetaQuad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            return m_MetaQuad;
        }

        RenderTexture EvaluateMetaPass(Material source, bool emission)
        {
            if (source == null)
                return null;
            int pass = source.FindPass("Meta");
            if (pass < 0)
                return null;

            const int kMetaResolution = 256;
            var target = new RenderTexture(kMetaResolution, kMetaResolution, 0,
                RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = $"Standalone APV {(emission ? "emission" : "albedo")} meta {source.name}",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!target.Create())
            {
                UnityEngine.Object.DestroyImmediate(target);
                return null;
            }

            // Draw a clone so evaluating Meta does not mutate the authored
            // material's EDITOR_VISUALIZATION keyword state.
            var material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            material.DisableKeyword("EDITOR_VISUALIZATION");
            var properties = new MaterialPropertyBlock();
            properties.SetVector(Shader.PropertyToID("unity_MetaVertexControl"), Vector4.zero);
            properties.SetVector(Shader.PropertyToID("unity_MetaFragmentControl"),
                emission ? new Vector4(0, 1, 0, 0) : new Vector4(1, 0, 0, 0));
            properties.SetFloat(Shader.PropertyToID("unity_OneOverOutputBoost"), 1.0f);
            properties.SetFloat(Shader.PropertyToID("unity_MaxOutputValue"), 0.97f);
            properties.SetInt(Shader.PropertyToID("unity_UseLinearSpace"),
                QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);

            var command = new CommandBuffer { name = "Standalone APV Evaluate Meta" };
            command.SetRenderTarget(target);
            command.SetViewProjectionMatrices(Matrix4x4.identity,
                GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1, 1, -1, 1, -50, 50), true));
            command.SetViewport(new Rect(0, 0, kMetaResolution, kMetaResolution));
            command.ClearRenderTarget(false, true, Color.black);
            command.DrawMesh(GetMetaQuad(), Matrix4x4.identity, material, 0, pass, properties);
            Graphics.ExecuteCommandBuffer(command);
            command.Release();
            UnityEngine.Object.DestroyImmediate(material);
            m_MetaTextures.Add(target);
            return target;
        }

        public uint GetMaterialID(Material material)
        {
            if (material == null) return 0;
            if (m_MaterialIDs.TryGetValue(material, out uint id)) return id;
            bool alphaClipped = IsAlphaClipped(material);
            if (m_OpacityOnly && !alphaClipped) return 0;
            if (!TryValidate(material, out string reason, includeEmission: !m_PreserveSourceTextures))
                throw new InvalidOperationException(reason);

            string baseProperty = BaseMapProperty(material);
            Color albedo = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") :
                material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            Color emission = Color.black;
            string shaderName = material.shader != null ? material.shader.name : string.Empty;
            bool usesEmissionKeyword = shaderName.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal) ||
                shaderName == "Standard" || shaderName == "Standard (Specular setup)";
            // APRT preserves source albedo for the reference Unlit capture. Its
            // surfel contract has no emission, so emission maps must not consume
            // source texture bindings or impose validation on unused inputs.
            if (!m_OpacityOnly && !m_PreserveSourceTextures &&
                (!usesEmissionKeyword || material.IsKeywordEnabled("_EMISSION")) &&
                (material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0 &&
                (material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.BakedEmissive) != 0 &&
                material.HasProperty("_EmissionColor"))
                emission = material.GetColor("_EmissionColor");
            int emissionProperty = material.shader != null ? material.shader.FindPropertyIndex("_EmissionColor") : -1;
            // HDR colors (including URP Lit's emission) are stored in linear
            // space. Ordinary Color properties follow the sRGB color convention.
            bool linearEmission = emissionProperty >= 0 &&
                (material.shader.GetPropertyFlags(emissionProperty) & ShaderPropertyFlags.HDR) != 0;
            if (!linearEmission) emission = emission.linear;

            // Evaluate Meta on a unit quad, producing material-space maps
            // sampled with the original mesh UV0. Unlike a per-instance
            // lightmap atlas, these maps are independent of mesh lightmap UVs.
            // Authored texture transforms are already applied by Meta.
            // Alpha-clipped materials keep their source texture because Meta
            // does not preserve the cutout mask; opacity-only passes also
            // need the source alpha.
            bool useMeta = m_UseMetaPass && !m_OpacityOnly && !alphaClipped;
            Texture baseTexture = GetTexture(material, baseProperty);
            Texture emissionTexture = emission.maxColorComponent > 0 ? GetTexture(material, "_EmissionMap") : null;
            if (useMeta)
            {
                Texture metaBaseTexture = EvaluateMetaPass(material, false);
                useMeta = metaBaseTexture != null;
                baseTexture = metaBaseTexture ?? baseTexture;
                if (useMeta && emission.maxColorComponent > 0)
                    emissionTexture = EvaluateMetaPass(material, true) ?? emissionTexture;
            }
            // URP Lit and Standard sample emission with the transformed base UV.
            Vector4 baseST = useMeta ? new Vector4(1, 1, 0, 0) : TextureST(material, baseProperty);
            bool sharedUV = baseProperty == "_BaseMap" || baseProperty == "_MainTex";
            m_Materials.Add(new BakeMaterial
            {
                albedo = useMeta ? Vector4.one : new Vector4(albedo.linear.r, albedo.linear.g, albedo.linear.b, albedo.a),
                emission = useMeta && emissionTexture != null ? Vector4.one : new Vector4(emission.r, emission.g, emission.b, 0),
                baseST = baseST,
                emissionST = useMeta ? new Vector4(1, 1, 0, 0) : (sharedUV ? baseST : TextureST(material, material.HasProperty("_EmissionMap") ? "_EmissionMap" : null)),
                baseMap = RegisterTexture(baseTexture),
                emissionMap = RegisterTexture(emissionTexture),
                surface = new Vector4(alphaClipped ? (material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f) : -1,
                    material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") || material.IsKeywordEnabled("_GLOSSINESS_FROM_BASE_ALPHA") ? 1 : 0,
                    material.doubleSidedGI ? 1 : 0,
                    m_PreserveSourceTextures ? (material.HasProperty("_Cull") ? material.GetFloat("_Cull") : 2) : 0)
            });
            id = (uint)m_Materials.Count - 1;
            m_MaterialIDs.Add(material, id);
            m_HasAlphaClipping |= alphaClipped;
            return id;
        }

        int GetTextureResolution()
        {
            // Plan from the same scene-wide set in all three passes. Otherwise
            // opacity-only uploads could retain finer alpha coverage than lighting.
            var textures = new HashSet<Texture>(m_Textures);
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || !renderer.gameObject.scene.isLoaded ||
                    (GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.ContributeGI) == 0)
                    continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    var baseTexture = GetTexture(material, BaseMapProperty(material));
                    var emissionTexture = GetTexture(material, "_EmissionMap");
                    if (baseTexture != null && baseTexture.dimension == TextureDimension.Tex2D) textures.Add(baseTexture);
                    if (emissionTexture != null && emissionTexture.dimension == TextureDimension.Tex2D) textures.Add(emissionTexture);
                }
            }
            int resolution = 1;
            foreach (var texture in textures)
                resolution = Mathf.Max(resolution, Mathf.NextPowerOfTwo(Mathf.Max(texture.width, texture.height)));
            // Unity's native LightTransport MaterialPool renders Meta passes
            // into 256x256 texture-atlas slots. Use the same upper bound so
            // high-resolution Sponza albedo maps are filtered at the same
            // radiometric resolution during baking.
            resolution = Mathf.Min(resolution, 256, SystemInfo.maxTextureSize);
            while ((long)resolution * resolution * Mathf.Max(1, textures.Count) * 8 > k_TextureBudget && resolution > 1)
                resolution /= 2;
            return resolution;
        }

        public void Build()
        {
            if (m_Buffer != null) throw new InvalidOperationException("APV materials have already been uploaded.");
            if (m_PreserveSourceTextures)
            {
                if (m_Textures.Count > k_APRTSourceTextureLimit)
                    throw new InvalidOperationException($"APRT capture requires {m_Textures.Count} source textures; the native texture binding limit is {k_APRTSourceTextureLimit}. Split the Baking Set instead of reducing texture resolution.");
                var info = new Vector4[k_APRTSourceTextureLimit];
                for (int i = 0; i < info.Length; ++i)
                {
                    Texture texture = i < m_Textures.Count ? m_Textures[i] : Texture2D.whiteTexture;
                    int mips = texture is Texture2D image ? image.mipmapCount :
                        texture is RenderTexture target && target.useMipMap ? 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Max(target.width, target.height), 2)) : 1;
                    info[i] = new Vector4(texture.width, texture.height, mips,
                        texture.isDataSRGB && !GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) ? 1 : 0);
                }
                m_SourceTextureInfo = new GraphicsBuffer(GraphicsBuffer.Target.Structured, info.Length, 16);
                m_SourceTextureInfo.SetData(info);
                m_Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Materials.Count, Marshal.SizeOf<BakeMaterial>());
                m_Buffer.SetData(m_Materials);
                return;
            }
            int slices = Mathf.Max(1, m_Textures.Count);
            if (slices > SystemInfo.maxTextureArraySlices)
                throw new InvalidOperationException($"APV requires {slices} texture slices; this device supports {SystemInfo.maxTextureArraySlices}.");
            int resolution = m_Textures.Count == 0 ? 1 : GetTextureResolution();

            m_TextureArray = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "Standalone APV material textures",
                hideFlags = HideFlags.HideAndDontSave,
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = slices,
                useMipMap = false,
                filterMode = FilterMode.Bilinear
            };
            if (!m_TextureArray.Create()) throw new InvalidOperationException("Could not allocate APV material textures.");
            var copyShader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.unity.render-pipelines.core/Editor/Lighting/APV/StandaloneAPVCopyTexture.shader");
            if (copyShader == null || !copyShader.isSupported)
                throw new InvalidOperationException("The APV material texture copy shader is unavailable.");
            var copyMaterial = new Material(copyShader) { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture staging = null;
            RenderTexture previous = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            try
            {
                staging = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                GL.sRGBWrite = false;
                for (int slice = 0; slice < slices; ++slice)
                {
                    Texture source = m_Textures.Count == 0 ? Texture2D.whiteTexture : m_Textures[slice];
                    // Gamma projects can store sRGB data in a UNorm resource. Decode
                    // in the copy shader only when the source SRV doesn't do it.
                    copyMaterial.SetFloat("_DecodeSRGB", source.isDataSRGB && !GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat) ? 1 : 0);
                    copyMaterial.SetFloat("_PointFilter", source.filterMode == FilterMode.Point ? 1 : 0);
                    Graphics.Blit(source, staging, copyMaterial);
                    Graphics.CopyTexture(staging, 0, 0, m_TextureArray, slice, 0);
                }
            }
            finally
            {
                GL.sRGBWrite = previousSRGB;
                RenderTexture.active = previous;
                if (staging != null) RenderTexture.ReleaseTemporary(staging);
                UnityEngine.Object.DestroyImmediate(copyMaterial);
            }
            m_Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Materials.Count, Marshal.SizeOf<BakeMaterial>());
            m_Buffer.SetData(m_Materials);
        }

#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
        public void Bind(CommandBuffer command, IRayTracingShader shader)
        {
            shader.SetBufferParam(command, s_Materials, m_Buffer);
            if (m_PreserveSourceTextures)
            {
                shader.SetBufferParam(command, s_SourceTextureInfo, m_SourceTextureInfo);
                for (int i = 0; i < s_SourceTextures.Length; ++i)
                    shader.SetTextureParam(command, s_SourceTextures[i], i < m_Textures.Count ? m_Textures[i] : Texture2D.whiteTexture);
            }
            else shader.SetTextureParam(command, s_Textures, m_TextureArray);
            shader.SetIntParam(command, s_HasAlphaClipping, m_HasAlphaClipping ? 1 : 0);
        }
#endif

        public void Dispose()
        {
            m_Buffer?.Dispose();
            m_Buffer = null;
            m_SourceTextureInfo?.Dispose();
            m_SourceTextureInfo = null;
            if (m_TextureArray != null)
            {
                m_TextureArray.Release();
                UnityEngine.Object.DestroyImmediate(m_TextureArray);
                m_TextureArray = null;
            }
            if (m_MetaQuad != null)
            {
                UnityEngine.Object.DestroyImmediate(m_MetaQuad);
                m_MetaQuad = null;
            }
            for (int i = 0; i < m_MetaTextures.Count; ++i)
            {
                m_MetaTextures[i].Release();
                UnityEngine.Object.DestroyImmediate(m_MetaTextures[i]);
            }
            m_MetaTextures.Clear();
        }
    }
}
