using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityEngine.Rendering
{
    // Legacy external-format conversion and compatibility tests only. Normal
    // APRT baking keeps transport and scenario arrays inline in the Baking Set.
    // Immutable generations preserve older references and Undo.
    internal static class APRTStreamingDataWriter
    {
        internal sealed class Transaction : IDisposable
        {
            readonly string m_Directory;
            bool m_Published;
            internal Transaction(string directory) => m_Directory = directory;
            internal void Publish() => m_Published = true;
            public void Dispose()
            {
                if (m_Published || !Directory.Exists(m_Directory)) return;
                Directory.Delete(m_Directory, true);
                string meta = m_Directory + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
            }
        }

        internal static Transaction Prepare(ProbeVolumeBakingSet set, APRTBakedData staged)
        {
            if (set == null || staged == null || !staged.HasValidSharedData())
                throw new ArgumentException("A valid staged APRT bake is required for streaming publication.");
            string setPath = AssetDatabase.GetAssetPath(set);
            string setGuid = AssetDatabase.AssetPathToGUID(setPath);
            if (string.IsNullOrEmpty(setGuid)) throw new InvalidOperationException("Save the APRT Baking Set asset before baking streaming data.");
            string generation = Guid.NewGuid().ToString("N");
            string projectDirectory = Path.Combine(Path.GetDirectoryName(setPath), Path.GetFileNameWithoutExtension(setPath) + "_APRTData", generation).Replace('\\', '/');
            string streamingDirectory = Path.Combine(APRTDiskData.streamingAssetsDirectory, setGuid, generation).Replace('\\', '/');
            var transaction = new Transaction(Path.GetFullPath(projectDirectory));
            try
            {
                var previous = set.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
                if (previous != null && !ReferenceEquals(previous, staged) && staged.HasSameProbeLayout(previous) && previous.lightingScenarios != null)
                    foreach (var scenario in previous.lightingScenarios)
                        if (scenario != null && staged.FindLightingScenario(scenario.name) == null && previous.HasBakedLightingScenario(scenario.name))
                            staged.SetBakedLightingScenario(scenario);
                Directory.CreateDirectory(projectDirectory);
                int layoutHash = staged.GetLayoutHash();
                var sharedDescriptors = new Dictionary<int, APRTStreamableAsset>();
                var scenarioDescriptors = new Dictionary<APRTLightingScenario, List<APRTStreamableAsset>>();
                foreach (var cell in staged.cells)
                {
                    if (cell.streamableAsset != null)
                    { RequireFile(cell.streamableAsset); sharedDescriptors.Add(cell.index, cell.streamableAsset.Clone()); continue; }
                    var bytes = APRTDiskData.EncodeShared(cell, layoutHash, staged.hasSkyIrradianceTransfer, staged.hasSkyBounceTransfer);
                    var descriptor = Write(bytes, "shared-" + cell.index + ".aprtbin", cell, layoutHash,
                        APRTDiskData.SharedFlags(staged.hasSkyIrradianceTransfer, staged.hasSkyBounceTransfer), projectDirectory, streamingDirectory);
                    var validation = CopyLayout(cell);
                    validation.streamableAsset = descriptor;
                    APRTDiskData.ReadSharedSync(validation, layoutHash, staged.hasSkyIrradianceTransfer, staged.hasSkyBounceTransfer);
                    sharedDescriptors.Add(cell.index, descriptor);
                }
                int scenarioIndex = 0;
                if (staged.lightingScenarios != null) foreach (var scenario in staged.lightingScenarios)
                {
                    if (scenario?.HasValidBakedLighting(staged.totalProbeCount, layoutHash) != true) continue;
                    if (scenario.streamableAssets != null && scenario.streamableAssets.Count > 0)
                    { foreach (var descriptor in scenario.streamableAssets) RequireFile(descriptor); continue; }
                    int probeOffset = 0;
                    var descriptors = new List<APRTStreamableAsset>();
                    foreach (var cell in staged.cells)
                    {
                        var bytes = APRTDiskData.EncodeScenario(scenario, cell, probeOffset, layoutHash);
                        var descriptor = Write(bytes, "scenario-" + scenarioIndex + "-" + cell.index + ".aprtbin", cell,
                            layoutHash, APRTStreamableDataFlags.Scenario, projectDirectory, streamingDirectory);
                        var validation = new APRTLightingScenario { name = scenario.name, streamableAssets = new List<APRTStreamableAsset> { descriptor } };
                        APRTDiskData.ReadScenarioSync(validation, cell.index, cell.probeCount, layoutHash);
                        descriptors.Add(descriptor);
                        probeOffset += cell.probeCount;
                    }
                    scenarioDescriptors.Add(scenario, descriptors);
                    ++scenarioIndex;
                }
                // Every new file has passed the same decoder used at runtime.
                // Only now replace the staged large arrays with descriptors.
                foreach (var cell in staged.cells)
                {
                    cell.streamableAsset = sharedDescriptors[cell.index];
                    cell.axisData = Array.Empty<Vector4>(); cell.visibilityMoments = Array.Empty<Vector4>();
                    cell.probeValidity = Array.Empty<float>(); cell.skyIrradianceTransfer = Array.Empty<float>();
                    cell.skyIrradianceTransferRGB = Array.Empty<Vector3>();
                }
                foreach (var item in scenarioDescriptors)
                {
                    item.Key.streamableAssets = item.Value;
                    item.Key.bakedDirectionalSH = Array.Empty<Vector4>();
                    item.Key.bakedSkyIrradianceSH = Array.Empty<Vector4>();
                }
                if (!staged.HasValidSharedData()) throw new InvalidDataException("APRT streaming metadata failed validation; the previous bake was retained.");
                return transaction;
            }
            catch { transaction.Dispose(); throw; }
        }

        static APRTBakedCell CopyLayout(APRTBakedCell cell) => new APRTBakedCell
        {
            index = cell.index, position = cell.position, probeCount = cell.probeCount,
            probePositions = cell.probePositions, brickPositions = cell.brickPositions,
            brickSubdivisions = cell.brickSubdivisions, brickProbeIndices = cell.brickProbeIndices
        };

        static void RequireFile(APRTStreamableAsset descriptor)
        {
            // Validate one existing cell at a time; do not hydrate complete
            // previous scenarios merely to preserve their references.
            APRTDiskData.ValidateFileSync(descriptor);
        }

        static APRTStreamableAsset Write(byte[] bytes, string fileName, APRTBakedCell cell, int layoutHash,
            APRTStreamableDataFlags flags, string projectDirectory, string streamingDirectory)
        {
            string projectPath = Path.Combine(projectDirectory, fileName).Replace('\\', '/');
            // The generation is private until the Baking Set is committed.
            // Flush before opening the file through the runtime decoder.
            using (var stream = new FileStream(projectPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            return new APRTStreamableAsset
            {
                projectRelativePath = projectPath,
                streamingRelativePath = Path.Combine(streamingDirectory, fileName).Replace('\\', '/'),
                byteCount = bytes.Length, checksum = APRTDiskData.ComputeChecksum(bytes),
                cellIndex = cell.index, probeCount = cell.probeCount, layoutHash = layoutHash, flags = flags
            };
        }
    }
}
