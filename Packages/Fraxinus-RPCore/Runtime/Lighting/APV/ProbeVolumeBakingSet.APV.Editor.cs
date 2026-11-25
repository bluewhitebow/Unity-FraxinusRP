#if UNITY_EDITOR
using System.IO;
using UnityEditor;

namespace UnityEngine.Rendering
{
    public sealed partial class ProbeVolumeBakingSet
    {
        string GetOrCreateFileName(ProbeVolumeStreamableAsset asset, string filePath)
        {
            string res = string.Empty;
            if (asset != null && asset.IsValid())
                res = asset.GetAssetPath();
            if (string.IsNullOrEmpty(res))
                res = filePath;
            return res;
        }

        internal void GetCellDataFileNames(string basePath, string scenario, out string cellDataFileName, out string cellOptionalDataFileName, out string cellProbeOcclusionDataFileName)
        {
            cellDataFileName = $"{basePath}-{scenario}.CellData.bytes";
            cellOptionalDataFileName = $"{basePath}-{scenario}.CellOptionalData.bytes";
            cellProbeOcclusionDataFileName = $"{basePath}-{scenario}.CellProbeOcclusionData.bytes";
        }

        internal void GetBlobFileNames(string scenario, out string cellDataFilename, out string cellBricksDataFilename, out string cellOptionalDataFilename, out string cellProbeOcclusionDataFilename, out string cellSharedDataFilename, out string cellSupportDataFilename)
        {
            string baseDir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(this));

            string basePath = Path.Combine(baseDir, name);

            GetCellDataFileNames(basePath, scenario, out string dataFile, out string optionalDataFile, out string probeOcclusionDataFile);

            cellDataFilename = GetOrCreateFileName(scenarios[scenario].cellDataAsset, dataFile);
            cellOptionalDataFilename = GetOrCreateFileName(scenarios[scenario].cellOptionalDataAsset, optionalDataFile);
            cellProbeOcclusionDataFilename = GetOrCreateFileName(scenarios[scenario].cellProbeOcclusionDataAsset, probeOcclusionDataFile);
            cellBricksDataFilename = GetOrCreateFileName(cellBricksDataAsset, basePath + ".CellBricksData.bytes");
            cellSharedDataFilename = GetOrCreateFileName(cellSharedDataAsset, basePath + ".CellSharedData.bytes");
            cellSupportDataFilename = GetOrCreateFileName(cellSupportDataAsset, basePath + ".CellSupportData.bytes");
        }

    }
}
#endif
