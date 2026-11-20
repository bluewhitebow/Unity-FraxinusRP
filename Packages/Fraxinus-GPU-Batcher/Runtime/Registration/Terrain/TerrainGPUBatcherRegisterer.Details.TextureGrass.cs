// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

#if FRAXINUS_RP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Fraxinus.GPUBatcher
{
    public partial class TerrainGPUBatcherRegisterer
    {
        #region texture grass

        // maps TerrainData waving grass to _WaveAndDistance, measured from unity's grass draw
        private const float grassWaveSizeScale = 0.4f;
        private const float grassWindStrengthScale = 6.0f;
        private const float grassWindSpeedScale = 0.05f;

        private static readonly int _ID_MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int _ID_BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int _ID_Cutoff = Shader.PropertyToID("_Cutoff");
        private static readonly int _ID_GrassInvHeight = Shader.PropertyToID("_GrassInvHeight");
        private static readonly int _ID_WindSpeed = Shader.PropertyToID("_WindSpeed");
        private static readonly int _ID_WaveSize = Shader.PropertyToID("_WaveSize");
        private static readonly int _ID_WindStrength = Shader.PropertyToID("_WindStrength");
        private static readonly int _ID_WavingTint = Shader.PropertyToID("_WavingTint");
        private static readonly int _ID_HealthyColor = Shader.PropertyToID("_HealthyColor");
        private static readonly int _ID_DryColor = Shader.PropertyToID("_DryColor");
        private static readonly int _ID_NoiseSpread = Shader.PropertyToID("_NoiseSpread");
        private static readonly int _ID_BaseDarken = Shader.PropertyToID("_BaseDarken");

        private static Shader s_grassShader;
        private readonly List<Object> _generatedGrassAssets = new();
        private readonly List<int> _grassProtoIndices = new();
        private readonly List<Material> _grassMaterials = new();
        private float _lastWavingSpeed = float.NaN;
        private float _lastWavingAmount = float.NaN;
        private float _lastWavingStrength = float.NaN;
        private Color _lastWavingTint = new(-1f, -1f, -1f, -1f);

        // builds and registers a prototype for a texture-mode detail (grass / grass billboard).
        // returns the global proto index, or -1 if not a texture detail or the shader is missing.
        private int BuildTextureGrassPrototype(DetailPrototype proto)
        {
            if (proto.usePrototypeMesh || proto.prototypeTexture == null) return -1;

            var config = GPUBatcherConfig.Instance;
            if (!config.allowTextureGrass)
            {
                Debug.LogError($"TerrainGPUBatcherRegisterer: detail layer '{proto.prototypeTexture.name}' uses texture grass, but 'Allow Texture Grass' is off in the FraxinusGPUBatcher config. Enable it to render texture-mode grass through BRG.");
                return -1;
            }

            s_grassShader = config.TextureGrassShader;
            if (s_grassShader == null)
            {
                Debug.LogError("TerrainGPUBatcherRegisterer: Hidden/FraxinusGPUBatcher/TerrainTextureGrass shader not found, cannot render texture grass.");
                return -1;
            }

            Mesh mesh = BuildGrassQuad();
            Material mat = BuildGrassMaterial(proto);
            _generatedGrassAssets.Add(mesh);
            _generatedGrassAssets.Add(mat);

            var data = new PrototypeData
            {
                mesh = mesh,
                materials = new[] { mat },
                lodMeshes = new[] { mesh },
                lodMaterials = new Material[][] { new[] { mat } },
                lodScreenSizes = new float[] { 0f },
                lodFadeTransitionWidths = new float[] { 0f },
                lodShadowCastingModes = new ShadowCastingMode[] { ShadowCastingMode.Off },
                lodMotionVectorModes = new MotionVectorGenerationMode[] { MotionVectorGenerationMode.Camera },
                fadeMode = LODFadeMode.None,
            };
            var size = mesh.bounds.size;
            data.LodReferenceSize = Mathf.Max(size.x, Mathf.Max(size.y, size.z));

            var sig = new RenderSignature { asset = data };
            sig.layer = _terrain.gameObject.layer;
            sig.renderingLayerMask = 0xffffffff;

            int idx = RegisterRenderSignature(sig);
            if (idx >= 0) _grassProtoIndices.Add(idx);
            return idx;
        }

        // unit quad, base at y=0, centered on x, facing -z. per-instance scale sizes it
        private static Mesh BuildGrassQuad()
        {
            var mesh = new Mesh { name = "GPUBatcherGrassQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, 0f),
                new Vector3( 0.5f, 0f, 0f),
                new Vector3(-0.5f, 1f, 0f),
                new Vector3( 0.5f, 1f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            };
            var n = new Vector3(0f, 0f, -1f);
            mesh.normals = new[] { n, n, n, n };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private Material BuildGrassMaterial(DetailPrototype proto)
        {
            var mat = new Material(s_grassShader) { name = "GPUBatcherGrass_" + proto.prototypeTexture.name };
            mat.SetTexture(_ID_MainTex, proto.prototypeTexture);
            // unity bakes the gamma color bytes into grass vertex colors and reads them as linear (no
            // conversion). DetailPrototype colors are linear, so .gamma gives those bytes, sent raw via SetVector
            mat.SetVector(_ID_HealthyColor, (Vector4)proto.healthyColor.gamma);
            mat.SetVector(_ID_DryColor, (Vector4)proto.dryColor.gamma);
            mat.SetFloat(_ID_NoiseSpread, proto.noiseSpread);
            mat.SetFloat(_ID_Cutoff, 0.5f);
            mat.SetFloat(_ID_GrassInvHeight, 1f);
            ApplyWindToMaterial(mat);
            if (proto.renderMode == DetailRenderMode.GrassBillboard)
                mat.EnableKeyword("_BILLBOARD");
            _grassMaterials.Add(mat);
            return mat;
        }

        private void ApplyWindToMaterial(Material mat)
        {
            // TerrainData property names are rotated vs the inspector labels:
            // wavingGrassStrength = "Speed", wavingGrassSpeed = "Size", wavingGrassAmount = "Bending"
            mat.SetFloat(_ID_WindSpeed, _terrainData.wavingGrassStrength * grassWindSpeedScale);
            mat.SetFloat(_ID_WaveSize, _terrainData.wavingGrassSpeed * grassWaveSizeScale);
            mat.SetFloat(_ID_WindStrength, _terrainData.wavingGrassAmount * grassWindStrengthScale);
            mat.SetColor(_ID_WavingTint, _terrainData.wavingGrassTint);
        }

        private void UpdateGrassWindIfChanged()
        {
            if (_terrainData == null || _grassMaterials.Count == 0) return;

            float spd = _terrainData.wavingGrassSpeed;
            float amt = _terrainData.wavingGrassAmount;
            float str = _terrainData.wavingGrassStrength;
            Color tint = _terrainData.wavingGrassTint;
            if (spd == _lastWavingSpeed && amt == _lastWavingAmount && str == _lastWavingStrength && tint == _lastWavingTint) return;

            _lastWavingSpeed = spd;
            _lastWavingAmount = amt;
            _lastWavingStrength = str;
            _lastWavingTint = tint;
            for (int i = 0; i < _grassMaterials.Count; i++)
                if (_grassMaterials[i] != null) ApplyWindToMaterial(_grassMaterials[i]);
        }

        private void DisposeGeneratedGrassAssets()
        {
            if (GPUBatcherSystem != null)
                for (int i = 0; i < _grassProtoIndices.Count; i++)
                    GPUBatcherSystem.RemovePrototype(_grassProtoIndices[i]);
            _grassProtoIndices.Clear();

            for (int i = 0; i < _generatedGrassAssets.Count; i++)
            {
                if (_generatedGrassAssets[i] == null) continue;
                if (Application.isPlaying) Destroy(_generatedGrassAssets[i]);
                else DestroyImmediate(_generatedGrassAssets[i]);
            }
            _generatedGrassAssets.Clear();
            _grassMaterials.Clear();
            _lastWavingSpeed = _lastWavingAmount = _lastWavingStrength = float.NaN;
            _lastWavingTint = new(-1f, -1f, -1f, -1f);
        }

        #endregion
    }
}
#endif
