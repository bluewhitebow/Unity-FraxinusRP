// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_6000_4_OR_NEWER
using ViewID = UnityEngine.EntityId;
#else
using ViewID = System.Int32;
#endif

namespace Fraxinus.GPUBatcher
{
    // atlas-based HZB depth pyramid for two-pass GPU occlusion culling
    public partial class GPUBatcherRenderer
    {
        internal sealed class CameraHZBState : System.IDisposable
        {
            public RenderTexture hzbAtlas;
            public RTHandle hzbRTHandle;
            public int atlasWidth;
            public int atlasHeight;
            public int topMipWidth;
            public int topMipHeight;
            public int mipCount;
            public int[] mipBoundsFlat;
            public bool hzbValid;

            // previous frame's camera data for pass 0
            public Matrix4x4 prevViewProjMatrix;
            public Vector4 prevCameraPosition;
            public Vector4 prevFacingDir;
            public Vector4 prevRadialDir;
            public bool prevDataValid;

            public uint lastSeenFrame;

            public CameraHZBState()
            {
                mipBoundsFlat = new int[k_MaxOccluderMips * 4];
            }

            public void Dispose()
            {
                hzbRTHandle?.Release();
                hzbRTHandle = null;
                DestroyRT(ref hzbAtlas);
            }
        }

        private static void DestroyRT(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
#if UNITY_EDITOR
            if (Application.isPlaying)
                Object.Destroy(rt);
            else
                Object.DestroyImmediate(rt);
#else
            Object.Destroy(rt);
#endif
            rt = null;
        }

        private const int k_FirstDepthMipIndex = 3; // atlas mip 0 = depthSize / 8
        private const int k_MaxOccluderMips = 8;

        private readonly System.Collections.Generic.Dictionary<ViewID, CameraHZBState> _cameraHZBStates = new();
        private readonly System.Collections.Generic.List<ViewID> _cameraHZBStateKeys = new();
        private readonly System.Collections.Generic.List<ViewID> _staleHZBKeys = new();
        private const int kHZBEvictFrames = 3;



        private ComputeShader _occlusionShader;
        private int _hzbDownscaleSPDKernel = -1;   // single-pass: mips 0-4 via LDS
        private int _hzbDownscaleTailKernel = -1; // mips 5+: single workgroup, sequential UAV reads

        // scratch arrays to avoid params[] allocs
        private readonly int[] _intParams2 = new int[2];
        private readonly float[] _floatParams2 = new float[2];

        #region shader property IDs

        private static readonly int _ID_HZBTexture = Shader.PropertyToID("_HZBTexture");
        private static readonly int _ID_OccluderDepthPyramidSize = Shader.PropertyToID("_OccluderDepthPyramidSize");
        private static readonly int _ID_DepthSizeInOccluderPixels = Shader.PropertyToID("_DepthSizeInOccluderPixels");
        private static readonly int _ID_HZBMipCount = Shader.PropertyToID("_HZBMipCount");
        private static readonly int _ID_ViewProjMatrix = Shader.PropertyToID("_ViewProjMatrix");
        private static readonly int _ID_FacingDirWorldSpace = Shader.PropertyToID("_FacingDirWorldSpace");
        private static readonly int _ID_RadialDirWorldSpace = Shader.PropertyToID("_RadialDirWorldSpace");
        private static readonly int _ID_HZBSource = Shader.PropertyToID("_HZBSource");
        private static readonly int _ID_HZBMipOut = Shader.PropertyToID("_HZBMipOut");
        private static readonly int _ID_HZBSourceSize = Shader.PropertyToID("_HZBSourceSize");
        private static readonly int _ID_HZBCameraPosition = Shader.PropertyToID("_HZBCameraPosition");
        private static readonly int _ID_HZBPixelExpansion = Shader.PropertyToID("_HZBPixelExpansion");
        private static readonly int _ID_HZBTranslationScale = Shader.PropertyToID("_HZBTranslationScale");
        private static readonly int _ID_HZBTailStartMip = Shader.PropertyToID("_HZBTailStartMip");
        private static readonly int _ID_HZBTailMipCount = Shader.PropertyToID("_HZBTailMipCount");

        // must set the entire HLSL array at once via the base property name
        private static readonly int _ID_OccluderMipBounds = Shader.PropertyToID("_OccluderMipBounds");

        #endregion

        internal bool HasPendingCullingForCamera(int cameraId) => _deferredContexts.ContainsKey(cameraId);

        internal bool HasPendingHZBForCamera(int cameraId) =>
            _deferredContexts.TryGetValue(cameraId, out var camera) && camera.useHZB;

        internal void UpdatePendingHZBCameraData(int cameraId, GPUBatcherHZBBridge.HZBCameraData data)
        {
            if (!_deferredContexts.TryGetValue(cameraId, out var camera)) return;
            camera.viewProjMatrix = data.viewProjMatrix;
            camera.hzbCameraPosition = data.viewOriginWorldSpace;
            camera.facingDir = data.facingDirWorldSpace;
            camera.radialDir = data.radialDirWorldSpace;
        }

        internal void DisableCameraHZB(int cameraId)
        {
            if (_deferredContexts.TryGetValue(cameraId, out var camera)) camera.useHZB = false;
        }

        internal int GetPendingCullingSplitCount(int cameraId) =>
            _deferredContexts.TryGetValue(cameraId, out var camera) && camera.splits != null ? camera.splits.Count : 0;

        internal CullingBufferSet GetPendingCullingBufferSet(int cameraId, int splitIndex = 0)
        {
            if (!_deferredContexts.TryGetValue(cameraId, out var camera) || camera.splits == null || splitIndex < 0 || splitIndex >= camera.splits.Count)
                return null;
            return camera.splits[splitIndex].viewSet;
        }

        internal RTHandle GetCameraHZBRTHandle(int cameraId) =>
            _cameraHZBStates.TryGetValue(cameraId, out var state) ? state.hzbRTHandle : null;

        // RenderGraph executes after recording, and the culling context can already
        // have returned to its pool by the time the depth pyramid is generated.
        internal struct HZBBuildContext
        {
            public CameraHZBState state;
            public Matrix4x4 viewProjection;
            public Vector4 cameraPosition;
            public Vector4 facingDirection;
            public Vector4 radialDirection;
            public bool frozen;
        }

        internal HZBBuildContext CaptureHZBBuildContext(int cameraId)
        {
            if (!_deferredContexts.TryGetValue(cameraId, out var camera) || !camera.useHZB) return default;
            return new HZBBuildContext
            {
                state = camera.hzbState,
                viewProjection = camera.viewProjMatrix,
                cameraPosition = camera.hzbCameraPosition,
                facingDirection = camera.facingDir,
                radialDirection = camera.radialDir,
#if UNITY_EDITOR
                frozen = camera.usesFrozenData
#endif
            };
        }

        internal void ExecuteGraphCulling(CommandBuffer command, int cameraId, bool skipHZBTextureBinding)
        {
            ExecuteDeferredCulling(command, cameraId, skipHZBTextureBinding);
        }

        internal void ExecuteGraphHZBRetest(CommandBuffer command, int cameraId, bool skipHZBTextureBinding)
        {
            ExecuteDeferredCullingPhase1(command, cameraId, skipHZBTextureBinding);
        }

        internal bool IsGPUOcclusionEnabled => SystemInfo.supportsComputeShaders &&
                                               SystemInfo.usesReversedZBuffer && SystemInfo.graphicsUVStartsAtTop &&
                                               Config.UseGPUOcclusionCulling &&
                                               _occlusionShader != null &&
                                               _hzbDownscaleSPDKernel >= 0;

        // expose per-camera HZB state for render graph
        internal RTHandle PendingHZBRTHandle => _currentCullingContext?.hzbState?.hzbRTHandle;
        internal ComputeShader OcclusionShader => _occlusionShader;
        internal int HZBDownscaleKernel => _hzbDownscaleSPDKernel;
        internal int ID_HZBSource => _ID_HZBSource;

        // expose for render graph TextureHandle binding
        internal ComputeShader CullingShaderRef => cullingShader;
        internal int CullChunksKernelRef => _cullChunksKernel;
        internal int CullInstancesKernelRef => _cullInstancesKernel;
        internal static int ID_HZBTextureRef => _ID_HZBTexture;
        internal static int ID_HZBMipOutRef => _ID_HZBMipOut;
        internal int HZBDownscaleTailKernel => _hzbDownscaleTailKernel;


        public RenderTexture GetCameraHZBAtlas(ViewID cameraInstanceID)
        {
            if (_cameraHZBStates.TryGetValue(cameraInstanceID, out var state))
                return state.hzbAtlas;
            return null;
        }

        public RenderTexture GetCameraHZBAtlas(Camera camera)
        {
            if (camera == null) return null;
            return GetCameraHZBAtlas(GPUBatcherHelpers.GetViewID(camera));
        }

        private void InitHZB()
        {
            _occlusionShader = Config.occlusionShader;
            if (_occlusionShader == null) return;

            _hzbDownscaleSPDKernel = _occlusionShader.HasKernel("HZBDownscaleSPD") ? _occlusionShader.FindKernel("HZBDownscaleSPD") : -1;
            _hzbDownscaleTailKernel = _occlusionShader.HasKernel("HZBDownscaleTail") ? _occlusionShader.FindKernel("HZBDownscaleTail") : -1;
            if (_hzbDownscaleSPDKernel < 0 || _hzbDownscaleTailKernel < 0)
            {
                Debug.LogWarning("GPUBatcherRenderer: missing HZB generation compute kernels - GPU occlusion disabled.");
                _occlusionShader = null;
                _hzbDownscaleSPDKernel = -1;
                _hzbDownscaleTailKernel = -1;
            }
        }

        internal void OnGPUOcclusionToggled()
        {
            _bufferPoolGeneration++;

            if (!IsGPUOcclusionEnabled)
            {
                ClearAllHZBStates();
            }
            else
            {
                // invalidate all existing states
                foreach (var kvp in _cameraHZBStates)
                {
                    kvp.Value.hzbValid = false;
                    kvp.Value.prevDataValid = false;
                }
            }
        }

        private void ClearAllHZBStates()
        {
            foreach (var kvp in _cameraHZBStates)
                kvp.Value.Dispose();
            _cameraHZBStates.Clear();
            _cameraHZBStateKeys.Clear();
            if (_currentCullingContext != null) _currentCullingContext.hzbState = null;
        }

        private void ShutdownHZB()
        {
            ClearAllHZBStates();
            _occlusionShader = null;
        }

        private void EvictStaleHZBStates()
        {
            if (_cameraHZBStates.Count == 0) return;

            _staleHZBKeys.Clear();
            for (int i = 0; i < _cameraHZBStateKeys.Count; i++)
            {
                var key = _cameraHZBStateKeys[i];
                if (_cameraHZBStates.TryGetValue(key, out var state) &&
                    _renderCycleCounter - state.lastSeenFrame > kHZBEvictFrames)
                {
                    _staleHZBKeys.Add(key);
                }
            }

            for (int i = 0; i < _staleHZBKeys.Count; i++)
            {
                var key = _staleHZBKeys[i];
                if (_cameraHZBStates.TryGetValue(key, out var state))
                {
                    state.Dispose();
                    _cameraHZBStates.Remove(key);
                    _cameraHZBStateKeys.Remove(key);
                }
            }
        }

        private CameraHZBState AcquireCameraHZBState(ViewID cameraID)
        {
            if (!_cameraHZBStates.TryGetValue(cameraID, out var state))
            {
                state = new CameraHZBState();
                _cameraHZBStates[cameraID] = state;
                _cameraHZBStateKeys.Add(cameraID);
            }
            state.lastSeenFrame = _renderCycleCounter;
            return state;
        }

        // called from the build-pass record with the render-scaled depth dims
        internal void EnsureHZBAtlas(Camera camera, int scaledWidth, int scaledHeight)
        {
            if (!IsGPUOcclusionEnabled || camera.cameraType != CameraType.Game) return;
            EnsureHZBTexture(AcquireCameraHZBState(GPUBatcherHelpers.GetViewID(camera)), scaledWidth, scaledHeight);
        }

        // compute L-shaped mip layout matching OccluderContext
        private static void ComputeMipBounds(CameraHZBState state, int topMipW, int topMipH)
        {
            state.topMipWidth = topMipW;
            state.topMipHeight = topMipH;

            int totalW = 0, totalH = 0;
            int offX = 0, offY = 0;
            int mipW = topMipW, mipH = topMipH;

            int count = 0;
            for (int i = 0; i < k_MaxOccluderMips; i++)
            {
                if (mipW < 1 || mipH < 1) break;

                state.mipBoundsFlat[i * 4 + 0] = offX;
                state.mipBoundsFlat[i * 4 + 1] = offY;
                state.mipBoundsFlat[i * 4 + 2] = mipW;
                state.mipBoundsFlat[i * 4 + 3] = mipH;
                count++;

                totalW = math.max(totalW, offX + mipW);
                totalH = math.max(totalH, offY + mipH);

                if (i == 0)
                {
                    offX = 0;
                    offY += mipH;
                }
                else
                {
                    offX += mipW;
                }
                mipW = (mipW + 1) / 2;
                mipH = (mipH + 1) / 2;
            }

            state.mipCount = count;
            state.atlasWidth = totalW;
            state.atlasHeight = totalH;
        }

        /// <summary>
        /// Ensure per-camera HZB atlas matches depth buffer dimensions. Recreates on size change.
        /// </summary>
        private static void EnsureHZBTexture(CameraHZBState state, int screenWidth, int screenHeight)
        {
            int topMipW = math.max(1, screenWidth >> k_FirstDepthMipIndex);
            int topMipH = math.max(1, screenHeight >> k_FirstDepthMipIndex);

            if (state.hzbAtlas != null && state.topMipWidth == topMipW && state.topMipHeight == topMipH)
                return;

            DestroyRT(ref state.hzbAtlas);

            ComputeMipBounds(state, topMipW, topMipH);

            var desc = new RenderTextureDescriptor(state.atlasWidth, state.atlasHeight, RenderTextureFormat.RFloat, 0)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = true,
                sRGB = false,
                msaaSamples = 1,
            };

            state.hzbAtlas = new RenderTexture(desc);
            state.hzbAtlas.name = "FraxinusGPUBatcher_HZB_Atlas";
            state.hzbAtlas.filterMode = FilterMode.Point;
            state.hzbAtlas.wrapMode = TextureWrapMode.Clamp;
            state.hzbAtlas.Create();

            state.hzbRTHandle?.Release();
            state.hzbRTHandle = RTHandles.Alloc(state.hzbAtlas);

            state.hzbValid = false;
        }

        private void BindHZBAtlasUniforms(CommandBuffer cmd, ComputeShader shader, CameraHZBState hzbState)
        {
            var pyramidSize = new Vector4(hzbState.atlasWidth, hzbState.atlasHeight, 1f / hzbState.atlasWidth, 1f / hzbState.atlasHeight);
            cmd.SetComputeVectorParam(shader, _ID_OccluderDepthPyramidSize, pyramidSize);
            _floatParams2[0] = hzbState.topMipWidth; _floatParams2[1] = hzbState.topMipHeight;
            cmd.SetComputeFloatParams(shader, _ID_DepthSizeInOccluderPixels, _floatParams2);
            cmd.SetComputeIntParam(shader, _ID_HZBMipCount, hzbState.mipCount);
            cmd.SetComputeIntParams(shader, _ID_OccluderMipBounds, hzbState.mipBoundsFlat);
        }

        /// <summary>
        /// build the atlas-based HZB mip chain for the current camera.
        /// sourceTextureWidth/Height: actual depth texture dimensions for UV computation.
        /// when 0, defaults to depthWidth/depthHeight (correct when viewport == texture, e.g. URP).
        /// </summary>
        private bool _warnedHZBSizeMismatch;

        bool _hzbSourceTexArray;

        // HDRP depth source is Texture2DArray, URP is Texture2D; set by the active pipeline's HZB build, guarded so the keyword only changes on a pipeline switch
        internal void SetHZBSourceTexArray(bool texArray)
        {
            if (_occlusionShader == null || texArray == _hzbSourceTexArray) return;
            _hzbSourceTexArray = texArray;
            if (texArray) _occlusionShader.EnableKeyword("HZB_SOURCE_TEXARRAY");
            else _occlusionShader.DisableKeyword("HZB_SOURCE_TEXARRAY");
        }

        internal void ExecuteHZBBuild(CommandBuffer cmd, HZBBuildContext camera, int depthWidth, int depthHeight,
            bool skipHZBOutputBinding = false,
            int sourceTextureWidth = 0, int sourceTextureHeight = 0)
        {
            var hzbState = camera.state;
            if (_occlusionShader == null || _hzbDownscaleSPDKernel < 0 || hzbState == null)
                return;

            // frozen data path - do not rebuild, would overwrite game camera's atlas with scene view depth
            if (camera.frozen)
                return;

            // texture pre-created on main thread; skip if atlas missing or dimensions changed
            if (hzbState.hzbAtlas == null) return;
            int expectedW = math.max(1, depthWidth >> k_FirstDepthMipIndex);
            int expectedH = math.max(1, depthHeight >> k_FirstDepthMipIndex);
            if (hzbState.topMipWidth != expectedW || hzbState.topMipHeight != expectedH)
            {
                if (!_warnedHZBSizeMismatch)
                {
                    _warnedHZBSizeMismatch = true;
                    Debug.LogWarning($"[FraxinusGPUBatcher] HZB atlas size mismatch (atlas {hzbState.topMipWidth}x{hzbState.topMipHeight} vs depth {expectedW}x{expectedH}); occlusion disabled this frame. EnsureHZBAtlas must be called with the build's depth dims.");
                }
                return;
            }

            const int kSPDTile = 16;
            const int kSPDMipLevels = 5; // mips 0-4 in one dispatch

            // _HZBSourceSize must be the actual texture dimensions for correct UV mapping.
            // In HDRP, the viewport can differ from the texture size (e.g. render texture cameras).
            _intParams2[0] = sourceTextureWidth > 0 ? sourceTextureWidth : depthWidth;
            _intParams2[1] = sourceTextureHeight > 0 ? sourceTextureHeight : depthHeight;
            cmd.SetComputeIntParams(_occlusionShader, _ID_HZBSourceSize, _intParams2);
            cmd.SetComputeIntParams(_occlusionShader, _ID_OccluderMipBounds, hzbState.mipBoundsFlat);
            cmd.SetComputeIntParam(_occlusionShader, _ID_HZBMipCount, hzbState.mipCount);
            if (!skipHZBOutputBinding)
                cmd.SetComputeTextureParam(_occlusionShader, _hzbDownscaleSPDKernel, _ID_HZBMipOut, hzbState.hzbAtlas);

            int groupsX = (hzbState.topMipWidth + kSPDTile - 1) / kSPDTile;
            int groupsY = (hzbState.topMipHeight + kSPDTile - 1) / kSPDTile;

#if DEBUG
            cmd.BeginSample("FraxinusGPUBatcher.HZBDownscaleSPD");
#endif
            cmd.DispatchCompute(_occlusionShader, _hzbDownscaleSPDKernel, groupsX, groupsY, 1);
#if DEBUG
            cmd.EndSample("FraxinusGPUBatcher.HZBDownscaleSPD");
#endif

            // mips 5+: separate dispatch, single workgroup
            if (kSPDMipLevels < hzbState.mipCount)
            {
                if (!skipHZBOutputBinding)
                    cmd.SetComputeTextureParam(_occlusionShader, _hzbDownscaleTailKernel, _ID_HZBMipOut, hzbState.hzbAtlas);
                cmd.SetComputeIntParam(_occlusionShader, _ID_HZBTailStartMip, kSPDMipLevels);
                cmd.SetComputeIntParam(_occlusionShader, _ID_HZBTailMipCount, hzbState.mipCount);
#if DEBUG
                cmd.BeginSample("FraxinusGPUBatcher.HZBDownscaleTail");
#endif
                cmd.DispatchCompute(_occlusionShader, _hzbDownscaleTailKernel, 1, 1, 1);
#if DEBUG
                cmd.EndSample("FraxinusGPUBatcher.HZBDownscaleTail");
#endif
            }

            hzbState.hzbValid = true;

            // save this frame's camera data for next frame's pass 0
            hzbState.prevViewProjMatrix = camera.viewProjection;
            hzbState.prevCameraPosition = camera.cameraPosition;
            hzbState.prevFacingDir = camera.facingDirection;
            hzbState.prevRadialDir = camera.radialDirection;
            hzbState.prevDataValid = true;
        }

    }
}
