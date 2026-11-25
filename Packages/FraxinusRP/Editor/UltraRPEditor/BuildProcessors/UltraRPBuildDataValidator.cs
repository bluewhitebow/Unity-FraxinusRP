using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor.Build.Reporting;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;

namespace FraxinusEditor.Rendering.Ultra
{
    class UltraRPBuildDataValidator
    {
        private static void ValidateRenderPipelineAssetsAreAtLastVersion(List<UltraRenderPipelineAsset> renderPipelineAssets, StringBuilder failures)
        {
            // Validate all included assets are at last version
            foreach (var ultraPipelineAsset in renderPipelineAssets)
            {
                if (!ultraPipelineAsset.IsAtLastVersion())
                {
                    failures.AppendLine(
                        $"- The {nameof(UltraRenderPipelineAsset)} with '{ultraPipelineAsset.name}({AssetDatabase.GetAssetPath(ultraPipelineAsset)})' is not at last version.");
                }
            }
        }

        private static void ValidateRenderPipelineGlobalSettings(UltraRenderPipelineGlobalSettings globalSettingsInstance, StringBuilder failures)
        {
            if (globalSettingsInstance == null)
                failures.AppendLine($"- The {nameof(UltraRenderPipelineGlobalSettings)} of the project are missing.");
            else
            {
                if (!globalSettingsInstance.IsAtLastVersion())
                {
                    failures.AppendLine(
                        $"- The {nameof(UltraRenderPipelineGlobalSettings)} with '{globalSettingsInstance.name}({AssetDatabase.GetAssetPath(globalSettingsInstance)})' is not at last version.");
                }
            }
        }

        public static bool IsProjectValidForBuilding(BuildReport report, out string message)
        {
            using (GenericPool<StringBuilder>.Get(out var failures))
            {
                failures.Clear();

                ValidateRenderPipelineAssetsAreAtLastVersion(UltraRPBuildData.instance.renderPipelineAssets, failures);
                ValidateRenderPipelineGlobalSettings(UltraRenderPipelineGlobalSettings.Ensure(), failures);

                string allFailures = failures.ToString();

                if (!string.IsNullOrEmpty(allFailures))
                {
                    message =
                        $"Please fix the following errors before building:{Environment.NewLine}{allFailures}. {Environment.NewLine}";
                    return false;
                }
            }

            message = string.Empty;
            return true;
        }
    }
}
