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
        private const float k_grassWaveSizeScale = 0.4f;
        private const float k_grassWindStrengthScale = 6.0f;
        private const float k_grassWindSpeedScale = 0.05f;

        private static readonly int s_idMainTex = Shader.PropertyToID("_MainTex");
        private static readonly int s_idBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int s_idCutoff = Shader.PropertyToID("_Cutoff");
        private static readonly int s_idGrassInvHeight = Shader.PropertyToID("_GrassInvHeight");
        private static readonly int s_idWindSpeed = Shader.PropertyToID("_WindSpeed");
        private static readonly int s_idWaveSize = Shader.PropertyToID("_WaveSize");
        private static readonly int s_idWindStrength = Shader.PropertyToID("_WindStrength");
        private static readonly int s_idWavingTint = Shader.PropertyToID("_WavingTint");
        private static readonly int s_idHealthyColor = Shader.PropertyToID("_HealthyColor");
        private static readonly int s_idDryColor = Shader.PropertyToID("_DryColor");
        private static readonly int s_idNoiseSpread = Shader.PropertyToID("_NoiseSpread");
        private static readonly int s_idBaseDarken = Shader.PropertyToID("_BaseDarken");

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

            var config = GPUBatcherConfig.instance;
            if (!config.allowTextureGrass)
            {
                Debug.LogError($"TerrainGPUBatcherRegisterer: detail layer '{proto.prototypeTexture.name}' uses texture grass, but 'Allow Texture Grass' is off in the FraxinusGPUBatcher config. Enable it to render texture-mode grass through BRG.");
                return -1;
            }

            s_grassShader = config.textureGrassShader;
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
            data.lodReferenceSize = Mathf.Max(size.x, Mathf.Max(size.y, size.z));

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
            mat.SetTexture(s_idMainTex, proto.prototypeTexture);
            // unity bakes the gamma color bytes into grass vertex colors and reads them as linear (no
            // conversion). DetailPrototype colors are linear, so .gamma gives those bytes, sent raw via SetVector
            mat.SetVector(s_idHealthyColor, (Vector4)proto.healthyColor.gamma);
            mat.SetVector(s_idDryColor, (Vector4)proto.dryColor.gamma);
            mat.SetFloat(s_idNoiseSpread, proto.noiseSpread);
            mat.SetFloat(s_idCutoff, 0.5f);
            mat.SetFloat(s_idGrassInvHeight, 1f);
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
            mat.SetFloat(s_idWindSpeed, _terrainData.wavingGrassStrength * k_grassWindSpeedScale);
            mat.SetFloat(s_idWaveSize, _terrainData.wavingGrassSpeed * k_grassWaveSizeScale);
            mat.SetFloat(s_idWindStrength, _terrainData.wavingGrassAmount * k_grassWindStrengthScale);
            mat.SetColor(s_idWavingTint, _terrainData.wavingGrassTint);
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
