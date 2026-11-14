using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    /// <summary>
    /// Native AssetBundle builds with a known shader-filter lifetime and settings-aware
    /// incremental invalidation. The active target must already match the requested
    /// target. Existing Shader compiler caches are preserved even when bundles rebuild.
    /// </summary>
    public static class UltraRPAssetBundleBuilder
    {
        public static AssetBundleManifest BuildAssetBundles(string outputPath, BuildAssetBundleOptions options,
            BuildTarget target, IEnumerable<RenderPipelineAsset> additionalPipelineAssets = null)
            => Build(outputPath, options, target, additionalPipelineAssets,
                effective => BuildPipeline.BuildAssetBundles(outputPath, effective, target));

        public static AssetBundleManifest BuildAssetBundles(string outputPath, AssetBundleBuild[] builds,
            BuildAssetBundleOptions options, BuildTarget target,
            IEnumerable<RenderPipelineAsset> additionalPipelineAssets = null)
        {
            if (builds == null)
                throw new ArgumentNullException(nameof(builds));
            return Build(outputPath, options, target, additionalPipelineAssets,
                effective => BuildPipeline.BuildAssetBundles(outputPath, builds, effective, target));
        }

        static AssetBundleManifest Build(string outputPath, BuildAssetBundleOptions options, BuildTarget target,
            IEnumerable<RenderPipelineAsset> additionalAssets, Func<BuildAssetBundleOptions, AssetBundleManifest> build)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("A bundle output directory is required.", nameof(outputPath));
            string fullOutput = Path.GetFullPath(outputPath);
            string stampPath = UltraRPBundleBuildCache.StampPath(fullOutput);
            AssetBundleManifest manifest;
            string configuration;
            using (var scope = new UltraRPAssetBundleBuildScope(target, fullOutput, additionalAssets))
            {
                var configurationHash = UltraRPShaderBuildConfiguration.GetHash();
                if (!configurationHash.isValid)
                    throw new InvalidOperationException("All declared bundle pipeline assets must be saved, resolved Fraxinus configurations before incremental bundle caching can be used.");
                configuration = configurationHash + ":" +
                    ((int)(options & ~(BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DryRunBuild))).ToString();
                bool rebuild = UltraRPBundleBuildCache.NeedsRebuild(stampPath, fullOutput, configuration);
                var effective = rebuild ? options | BuildAssetBundleOptions.ForceRebuildAssetBundle : options;
                // A failed build can partially overwrite an older successful output.
                // Invalidate before native work, even if settings currently match.
                UltraRPBundleBuildCache.Invalidate(stampPath);
                Directory.CreateDirectory(fullOutput);
                Debug.Log("UltraRP native bundles: " + fullOutput + ", force rebuild=" +
                    ((effective & BuildAssetBundleOptions.ForceRebuildAssetBundle) != 0) + "; ShaderCache retained.");
                manifest = build(effective);
                scope.SetResult(manifest != null);
            }
            // Do not certify a dry run or a scope whose field recovery failed.
            if (manifest != null && (options & BuildAssetBundleOptions.DryRunBuild) == 0)
            {
                var outputs = new List<string>();
                foreach (string name in manifest.GetAllAssetBundles())
                {
                    outputs.Add(Path.Combine(fullOutput, name));
                    outputs.Add(Path.Combine(fullOutput, name + ".manifest"));
                }
                string rootManifest = Path.Combine(fullOutput, new DirectoryInfo(fullOutput).Name);
                outputs.Add(rootManifest);
                outputs.Add(rootManifest + ".manifest");
                UltraRPBundleBuildCache.RecordSuccess(stampPath, fullOutput, configuration, outputs);
            }
            return manifest;
        }
    }

    internal static class UltraRPBundleBuildCache
    {
        [Serializable]
        sealed class Stamp
        {
            public string outputPath;
            public string configuration;
            public List<Output> outputs = new List<Output>();
        }

        [Serializable]
        sealed class Output
        {
            public string path;
            public string manifestHash;
            public long length;
            public long writtenUtc;
        }

        internal static string StampPath(string outputPath)
            => "Library/FraxinusRP/NativeBundleConfigurations/" + Hash128.Compute(Normalize(outputPath)) + ".json";

        static string Normalize(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.DirectorySeparatorChar == '\\' ? full.ToUpperInvariant() : full;
        }

        internal static bool NeedsRebuild(string stampPath, string outputPath, string configuration)
        {
            if (!File.Exists(stampPath))
                return true;
            try
            {
                var stamp = JsonUtility.FromJson<Stamp>(File.ReadAllText(stampPath));
                if (stamp == null || stamp.outputPath != Normalize(outputPath) || stamp.configuration != configuration ||
                    stamp.outputs == null || stamp.outputs.Count == 0)
                    return true;
                foreach (var output in stamp.outputs)
                {
                    if (output == null || string.IsNullOrEmpty(output.path))
                        return true;
                    var info = new FileInfo(output.path);
                    if (!info.Exists || info.Length != output.length || info.LastWriteTimeUtc.Ticks != output.writtenUtc)
                        return true;
                    if (!string.IsNullOrEmpty(output.manifestHash) && output.manifestHash != HashManifest(output.path))
                        return true;
                }
                return false;
            }
            catch (Exception error) when (error is IOException || error is ArgumentException)
            {
                // A missing or corrupt cache stamp requires rebuilding the bundles.
                return true;
            }
        }

        internal static void Invalidate(string stampPath)
        {
            if (File.Exists(stampPath))
                File.Delete(stampPath);
        }

        internal static void RecordSuccess(string stampPath, string outputPath, string configuration, IEnumerable<string> outputs)
        {
            var stamp = new Stamp { outputPath = Normalize(outputPath), configuration = configuration };
            foreach (string path in outputs.Distinct(StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                // Missing expected outputs cannot be certified.
                if (!info.Exists)
                    return;
                stamp.outputs.Add(new Output
                {
                    path = info.FullName,
                    length = info.Length,
                    writtenUtc = info.LastWriteTimeUtc.Ticks,
                    manifestHash = path.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ? HashManifest(path) : null
                });
            }
            if (stamp.outputs.Count == 0)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(stampPath));
            File.WriteAllText(stampPath + ".tmp", JsonUtility.ToJson(stamp, true));
            if (File.Exists(stampPath))
                File.Delete(stampPath);
            File.Move(stampPath + ".tmp", stampPath);
        }

        static string HashManifest(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(stream));
        }
    }
}
