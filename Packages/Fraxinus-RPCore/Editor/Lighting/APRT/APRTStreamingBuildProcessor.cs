using System.Collections.Generic;
using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering
{
    // Preserve external payloads referenced by legacy bakes. New inline bakes
    // need no additional files; existing files are read synchronously at load.
    internal sealed class APRTStreamingBuildProcessor : BuildPlayerProcessor
    {
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            ProbeVolumeBakingSet.SyncBakingSets();
            var sets = new HashSet<ProbeVolumeBakingSet>();
            var paths = new Dictionary<string, APRTStreamableAsset>();
            foreach (string scene in context.BuildPlayerOptions.scenes)
            {
                var set = ProbeVolumeBakingSet.GetBakingSetForScene(AssetDatabase.AssetPathToGUID(scene));
                if (set == null || !sets.Add(set)) continue;
                if (set.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) is not APRTBakedData data) continue;
                foreach (var descriptor in GetStreamingResources(data))
                {
                    if (paths.TryGetValue(descriptor.streamingRelativePath, out var previous) &&
                        (previous.checksum != descriptor.checksum || previous.byteCount != descriptor.byteCount))
                        throw new BuildFailedException("Conflicting legacy APRT data paths. Rebake the affected Baking Sets.");
                    paths[descriptor.streamingRelativePath] = descriptor;
                }
            }
            foreach (var item in paths)
            {
                try { APRTDiskData.ValidateFileSync(item.Value); }
                catch (System.Exception error)
                { throw new BuildFailedException("Legacy APRT data is missing or corrupt: " + item.Value.projectRelativePath + ". " + error.Message); }
                context.AddAdditionalPathToStreamingAssets(item.Value.ResolvePath(), item.Key);
            }
        }

        internal static IEnumerable<APRTStreamableAsset> GetStreamingResources(APRTBakedData data)
        {
            if (data?.cells != null) foreach (var cell in data.cells)
                if (cell?.streamableAsset != null) yield return cell.streamableAsset;
            if (data?.lightingScenarios != null) foreach (var scenario in data.lightingScenarios)
                if (scenario?.streamableAssets != null) foreach (var descriptor in scenario.streamableAssets)
                    if (descriptor != null) yield return descriptor;
        }
    }
}
