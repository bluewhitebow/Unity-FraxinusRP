using System.Collections.Generic;
using System.Text;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    class UltraRPPreprocessBuild : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue + 100;

        public void OnPreprocessBuild(BuildReport report)
        {
            UltraRPShaderBuildDiagnostics.End();
            bool isDevelopmentBuild = (report.summary.options & BuildOptions.Development) != 0;
            UltraRPShaderBuildSession.BeginPlayer(report);
            var buildData = UltraRPShaderBuildSession.data;
            if (!buildData.buildingPlayerForUltraRenderPipeline)
                return;

            try
            {
                if (!UltraRPBuildDataValidator.IsProjectValidForBuilding(report, out var message))
                    throw new BuildFailedException(message);
                UltraRPShaderBuildDiagnostics.Begin(report);
                UltraRenderPipelineGlobalSettings.Ensure()?.PrepareGraphicsSettingsForPlayerBuild();
                UltraRPShaderPrefiltering.PrepareForBuild(isDevelopmentBuild,
                    report.summary.platform, buildData.allRenderPipelineAssets);
                LogIncludedAssets(buildData.renderPipelineAssets);
            }
            catch
            {
                ClearBuildState("player preparation failed");
                throw;
            }
        }

        internal static void LogIncludedAssets(List<UltraRenderPipelineAsset> assetsList)
        {
            using (GenericPool<StringBuilder>.Get(out var assetsIncluded))
            {
                assetsIncluded.Clear();

                assetsIncluded.AppendLine($"{assetsList.Count} UltraRP assets included in build");

                foreach (var ultraAsset in assetsList)
                {
                    assetsIncluded.AppendLine($"- {ultraAsset.name} - {AssetDatabase.GetAssetPath(ultraAsset)}");
                    foreach (var rendererGraph in ultraAsset.rendererGraphList)
                    {
                        if (rendererGraph != null)
                            assetsIncluded.AppendLine($"\t - {rendererGraph.name} - {AssetDatabase.GetAssetPath(rendererGraph)} - {rendererGraph.GetType()}");
                    }
                }

                Debug.Log(assetsIncluded);
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            string status = "shader diagnostics failed";
            try
            {
                UltraRPShaderBuildDiagnostics.Complete(report);
                status = report.summary.result.ToString();
            }
            finally
            {
                // Clear the in-memory state before recovery, which can fail on disk I/O.
                ClearBuildState(status);
            }
        }

        static void ClearBuildState(string status)
        {
            UltraRPShaderBuildDiagnostics.End();
            UltraRPShaderBuildSession.End(status);
        }
    }
}
