using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class AreaLightSystem
    {
        [GenerateHLSL]
        internal enum LTCLightingModel
        {
            // Lit, Stack-Lit and Fabric/Silk
            GGX,
            DisneyDiffuse,
            
            // Fabric/CottonWool shader
            Charlie,
            // FabricLambert, (Isotropic)
            
            // Hair
            KajiyaKaySpecular,
            
            // KajiyaKayDiffuse, (Isotropic)
            Marschner, // TODO
            
            // Other
            CookTorrance,
            Ward,
            OrenNayar,
            Count
        }


        private static AreaLightSystem _instance = new AreaLightSystem();

        public static AreaLightSystem instance
        {
            get
            {
                if (_instance == null)
                    _instance = new AreaLightSystem();

                return _instance;
            }
        }

        internal const int ltcLUTResolution = 64;


        Texture2DArray _ltcData;
        RTHandle _ltcHandle;
        int _referenceCount;

        internal bool isAvailable => _ltcData != null && _ltcHandle != null;
        
        internal void Build()
        {
            if (_referenceCount++ > 0)
                return;
            if (!SystemInfo.supports2DArrayTextures)
                return;
            _ltcData = new Texture2DArray(ltcLUTResolution, ltcLUTResolution, (int)LTCLightingModel.Count, GraphicsFormat.R16G16B16A16_SFloat,
                TextureCreationFlags.None)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = CoreUtils.GetTextureAutoName(ltcLUTResolution, ltcLUTResolution, GraphicsFormat.R16G16B16A16_SFloat,
                    depth: (int)LTCLightingModel.Count, dim: TextureDimension.Tex2DArray, name: "LTC_LUT")
            };

            _ltcData.SetPixelData(LTCAreaLight.ltcMatrixData_BRDF_GGX, 0, (int)LTCLightingModel.GGX);
            _ltcData.SetPixelData(LTCAreaLight.ltcMatrixData_BRDF_Disney, 0, (int)LTCLightingModel.DisneyDiffuse);
            _ltcData.SetPixelData(LTCAreaLight.ltcMatrixData_BRDF_Charlie, 0, (int)LTCLightingModel.Charlie);
            _ltcData.SetPixelData(LTCAreaLight.ltcMatrixData_BRDF_KajiyaKaySpecular, 0, (int)LTCLightingModel.KajiyaKaySpecular);
            _ltcData.SetPixelData(LTCAreaLight.ltcMatrixData_BRDF_CookTorrance, 0, (int)LTCLightingModel.CookTorrance);
            _ltcData.SetPixelData(LTCAreaLight.ltcMatrixData_BRDF_Ward, 0, (int)LTCLightingModel.Ward);
            _ltcData.SetPixelData(LTCAreaLight.ltcMatrixData_BRDF_OrenNayar, 0, (int)LTCLightingModel.OrenNayar);

            _ltcData.Apply();
            _ltcHandle = RTHandles.Alloc(_ltcData);
        }

        internal void Cleanup()
        {
            if (_referenceCount == 0 || --_referenceCount > 0)
                return;
            _ltcHandle?.Release();
            _ltcHandle = null;
            CoreUtils.Destroy(_ltcData);
            _ltcData = null;
        }

        internal TextureHandle ImportToRenderGraph(RenderGraph graph) =>
            isAvailable ? graph.ImportTexture(_ltcHandle) : TextureHandle.nullHandle;

        internal void Bind(CommandBuffer cmd)
        {
            cmd.SetGlobalTexture("_LtcData", _ltcData);
        }
    }
}
