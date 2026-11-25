using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;
using UObject = UnityEngine.Object;
using EditorProgress = UnityEditor.Progress;

namespace FraxinusEditor.Rendering.Ultra
{
    /// <summary>
    /// PRT Baker - Editor包装层，负责Editor特有功能（进度条、Undo等）
    /// 核心烘焙逻辑委托给Runtime的PRTBakerCore
    /// </summary>
    public sealed class PRTBaker : IPRTBaker, IDisposable
    {
        private readonly PRTBakerCore _core;
        private readonly int _progressId;
        private bool _disposed;

        /// <summary>
        /// 初始化Editor PRTBaker
        /// </summary>
        /// <param name="bakeResolution">烘焙分辨率</param>
        /// <param name="bakeLayers">烘焙Layer掩码</param>
        public PRTBaker(PRTBakeResolution bakeResolution, LayerMask bakeLayers)
        {
            // 创建Runtime核心烘焙器
            _core = new PRTBakerCore(bakeResolution, bakeLayers);
            
            // 注册进度回调，显示Editor进度条
            _core.onProgress += OnCoreProgress;
            
            // 创建Editor进度条
            _progressId = EditorProgress.Start("PRT Baking", "Initializing...");
            EditorProgress.ShowDetails(false);
        }

        /// <summary>
        /// 核心烘焙器进度回调 - 更新Editor进度条
        /// </summary>
        private void OnCoreProgress(string status, float progress)
        {
            EditorProgress.Report(_progressId, progress, status);
        }

        /// <summary>
        /// 烘焙Surfel数据（IPRTBaker接口实现）
        /// </summary>
        public Surfel[] BakeSurfelData(Vector3 probePosition)
        {
            if (_disposed)
            {
                Debug.LogError("[PRTBaker] Baker is already disposed.");
                return new Surfel[0];
            }

            return _core.BakeSurfelData(probePosition);
        }

        /// <summary>
        /// 烘焙ProbeVolume（委托给PRTBakerCore）
        /// </summary>
        public async Task BakeVolume(PRTProbeVolume volume, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                Debug.LogError("[PRTBaker] Baker is already disposed.");
                return;
            }

            try
            {
                EditorProgress.Report(_progressId, 0.0f, "Starting volume bake...");
                
                // 委托给Runtime核心烘焙器
                await _core.BakeVolume(volume, cancellationToken);
                
                EditorProgress.Report(_progressId, 1.0f, "Baking complete!");
                
                // 标记Asset为脏（触发保存）
                if (volume.asset)
                {
                    EditorUtility.SetDirty(volume.asset);
                }
            }
            catch (OperationCanceledException)
            {
                EditorProgress.Report(_progressId, 0.0f, "Baking canceled.");
                Debug.Log("[PRTBaker] Baking was canceled by user.");
                throw;
            }
            catch (Exception ex)
            {
                EditorProgress.Report(_progressId, 0.0f, $"Baking failed: {ex.Message}");
                Debug.LogError($"[PRTBaker] Baking failed: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 烘焙反射探针
        /// </summary>
        public void BakeReflectionProbe(ReflectionProbeAdditionalData probeAdditionalData, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                Debug.LogError("[PRTBaker] Baker is already disposed.");
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                EditorProgress.Report(_progressId, 0.5f, $"Baking reflection probe at {probeAdditionalData.transform.position}");
                
                // 烘焙反射探针附加数据
                var coefficients = _core.BakeAdditionalProbeData(probeAdditionalData.transform.position);
                probeAdditionalData.SetSHCoefficients(coefficients);
                
                EditorProgress.Report(_progressId, 1.0f, "Reflection probe bake complete!");
                
                // 标记为脏
                EditorUtility.SetDirty(probeAdditionalData);
            }
            catch (OperationCanceledException)
            {
                EditorProgress.Report(_progressId, 0.0f, "Reflection probe baking canceled.");
                Debug.Log("[PRTBaker] Reflection probe baking was canceled by user.");
                throw;
            }
            catch (Exception ex)
            {
                EditorProgress.Report(_progressId, 0.0f, $"Reflection probe baking failed: {ex.Message}");
                Debug.LogError($"[PRTBaker] Reflection probe baking failed: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            // 取消注册进度回调
            if (_core != null)
                _core.onProgress -= OnCoreProgress;

            // 释放Runtime核心烘焙器
            _core?.Dispose();

            // 移除Editor进度条
            EditorProgress.Remove(_progressId);

            _disposed = true;
        }
    }
}
