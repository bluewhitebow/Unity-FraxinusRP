using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;
using UnityEngine.SceneManagement;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.SceneManagement;
using EditorProgress = UnityEditor.Progress;

using Debug = UnityEngine.Debug;

namespace FraxinusEditor.Rendering.Ultra
{
    /// <summary>
    /// PRT Bake Manager - handles baking workflow and menu integration
    /// </summary>
    public class PRTBakeManager : PRTVolumeManager
    {
        private static CancellationTokenSource s_cancellationTokenSource;

        public static async void GenerateLighting()
        {
            var totalStopwatch = Stopwatch.StartNew();
            try
            {
                await BakeProbeVolume_Internal(probeVolume);
                
                foreach (var probe in reflectionProbeAdditionalData)
                    await BakeReflectionProbe_Internal(probe);
                
                totalStopwatch.Stop();
                Debug.Log($"[PRTBaker] Bake completed, total time: {FormatElapsed(totalStopwatch.Elapsed)}");
            }
            catch (Exception exception)
            {
                totalStopwatch.Stop();
                Debug.LogError($"[PRTBaker] Bake failed, total time: {FormatElapsed(totalStopwatch.Elapsed)}, {exception}");
            }
        }

        public static void ClearBakedData()
        {
            probeVolume.ClearBakedData();
            EditorUtility.SetDirty(probeVolume.asset);
            
            foreach (var probe in reflectionProbeAdditionalData)
            {
                probe.ClearSHCoefficients();
                EditorUtility.SetDirty(probe);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[PRTBaker] Clear baked data completed.");
        }

        /// <summary>
        /// Bake single reflection probe normalization data
        /// </summary>
        public static async void BakeReflectionProbe(ReflectionProbeAdditionalData reflectionProbe)
        {
            var totalStopwatch = Stopwatch.StartNew();
            try
            {
                await BakeReflectionProbe_Internal(reflectionProbe);
                totalStopwatch.Stop();
                Debug.Log($"[PRTBaker] Bake completed, total time: {FormatElapsed(totalStopwatch.Elapsed)}");
            }
            catch (Exception exception)
            {
                totalStopwatch.Stop();
                Debug.LogError($"[PRTBaker] Bake failed, total time: {FormatElapsed(totalStopwatch.Elapsed)}, {exception}");
            }
        }

        /// <summary>
        /// Bake all reflection probes normalization data
        /// </summary>
        public static async void BakeAllReflectionProbes()
        {
            var totalStopwatch = Stopwatch.StartNew();
            try
            {
                foreach (var probe in reflectionProbeAdditionalData)
                {
                    await BakeReflectionProbe_Internal(probe);
                }

                totalStopwatch.Stop();
                Debug.Log($"[PRTBaker] Bake completed, total time: {FormatElapsed(totalStopwatch.Elapsed)}");
            }
            catch (Exception exception)
            {
                totalStopwatch.Stop();
                Debug.LogError($"[PRTBaker] Bake failed, total time: {FormatElapsed(totalStopwatch.Elapsed)}, {exception}");
            }
        }

        /// <summary>
        /// Stop all baking tasks in the scene
        /// </summary>
        public static void StopBaking()
        {
            if (!isBaking) return;
            s_cancellationTokenSource?.Cancel();
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes >= 1)
            {
                // mm min ss.sss s
                return $"{(int)elapsed.TotalMinutes:D2}m {elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}s";
            }

            if (elapsed.TotalSeconds >= 1)
            {
                // ss.sss s
                return $"{elapsed.TotalSeconds:F3}s";
            }

            // xxx.xxx ms
            return $"{elapsed.TotalMilliseconds:F3}ms";
        }

        private static async Task BakeProbeVolume_Internal(PRTProbeVolume prtProbeVolume)
        {
            if (!prtProbeVolume.asset)
            {
                if (!InitializeProbeVolumeData(prtProbeVolume))
                {
                    return;
                }
            }

            s_cancellationTokenSource?.Dispose();
            s_cancellationTokenSource = new CancellationTokenSource();
            isBaking = true;
            
            // 创建PRTBaker时传入bakeLayers
            using var prtBaker = new PRTBaker(prtProbeVolume.bakeResolution, prtProbeVolume.bakeLayers);

            int progressId = EditorProgress.Start(
                $"Bake Probe Volume ({prtProbeVolume.name})",
                 options: EditorProgress.Options.Managed);

            // PRTBaker内部已通过OnProgress事件处理进度显示，不需要再单独设置
            
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(s_cancellationTokenSource.Token, timeout.Token);
            try
            {
                // Await cancellation completion before disposing the baker's GPU resources.
                await prtBaker.BakeVolume(prtProbeVolume, cancellation.Token);
                    
                EditorUtility.SetDirty(prtProbeVolume.asset);
                AssetDatabase.SaveAssets();
                prtProbeVolume.ReloadBakedData();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Debug.LogError("Baking timeout after 10 minutes!");
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("Baking was cancelled by user.");
            }
            finally
            {
                EditorProgress.Remove(progressId);
                s_cancellationTokenSource?.Dispose();
                s_cancellationTokenSource = null;
                isBaking = false;
            }
        }


        private static async Task BakeReflectionProbe_Internal(ReflectionProbeAdditionalData reflectionProbe)
        {
            isBaking = true;
            int progressId = EditorProgress.Start(
                $"Bake Reflection Probe ({reflectionProbe.name})",
                options: EditorProgress.Options.Managed);
            s_cancellationTokenSource?.Dispose();
            s_cancellationTokenSource = new CancellationTokenSource();
            
            // 反射探针烘焙使用默认LayerMask（所有Layer）
            using var prtBaker = new PRTBaker(PRTBakeResolution._512, ~0);

            // PRTBaker内部已通过OnProgress事件处理进度显示

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken: s_cancellationTokenSource.Token);
                prtBaker.BakeReflectionProbe(reflectionProbe);

                EditorUtility.SetDirty(reflectionProbe);
                AssetDatabase.SaveAssets();
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("Baking was cancelled by user.");
            }
            finally
            {
                EditorProgress.Remove(progressId);
                s_cancellationTokenSource?.Dispose();
                s_cancellationTokenSource = null;
                isBaking = false;
            }
        }

        private static bool InitializeProbeVolumeData(PRTProbeVolume prtProbeVolume)
        {
            string scenePath = SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                Debug.LogError("Please save your scene before baking.");
                return false;
            }

            string sceneDir = Path.GetDirectoryName(scenePath);
            string sceneName = Path.GetFileNameWithoutExtension(scenePath);

            string targetDir = Path.Combine(sceneDir!, sceneName);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                AssetDatabase.Refresh();
            }

            var asset = ScriptableObject.CreateInstance<PRTProbeVolumeAsset>();
            string assetPath = Path.Combine(targetDir, $"{sceneName}_ProbeVolume.asset");
            assetPath = assetPath.Replace("\\", "/");
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            prtProbeVolume.asset = asset;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return true;
        }
    }
}
