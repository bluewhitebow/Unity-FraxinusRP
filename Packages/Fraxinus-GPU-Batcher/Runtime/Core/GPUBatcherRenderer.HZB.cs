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
                mipBoundsFlat = new int[k_maxOccluderMips * 4];
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

        private const int k_firstDepthMipIndex = 3; // atlas mip 0 = depthSize / 8
        private const int k_maxOccluderMips = 8;

        private readonly System.Collections.Generic.Dictionary<ViewID, CameraHZBState> _cameraHZBStates = new();
        private readonly System.Collections.Generic.List<ViewID> _cameraHZBStateKeys = new();
        private readonly System.Collections.Generic.List<ViewID> _staleHZBKeys = new();
        private const int k_hzbEvictFrames = 3;



        private ComputeShader _occlusionShader;
        private int _hzbDownscaleSPDKernel = -1;   // single-pass: mips 0-4 via LDS
        private int _hzbDownscaleTailKernel = -1; // mips 5+: single workgroup, sequential UAV reads
        private int _hzbResolveMSAA2DKernel = -1;
        private int _hzbResolveMSAAArrayKernel = -1;

        // scratch arrays to avoid params[] allocs
        private readonly float[] _floatParams2 = new float[2];

        #region shader property IDs

        private static readonly int s_idHzbTexture = Shader.PropertyToID("_HZBTexture");
        private static readonly int s_idOccluderDepthPyramidSize = Shader.PropertyToID("_OccluderDepthPyramidSize");
        private static readonly int s_idDepthSizeInOccluderPixels = Shader.PropertyToID("_DepthSizeInOccluderPixels");
        private static readonly int s_idHzbMipCount = Shader.PropertyToID("_HZBMipCount");
        private static readonly int s_idViewProjMatrix = Shader.PropertyToID("_ViewProjMatrix");
        private static readonly int s_idFacingDirWorldSpace = Shader.PropertyToID("_FacingDirWorldSpace");
        private static readonly int s_idRadialDirWorldSpace = Shader.PropertyToID("_RadialDirWorldSpace");
        private static readonly int s_idHzbSource = Shader.PropertyToID("_HZBSource");
        internal static readonly int idHzbDepthConvention = Shader.PropertyToID("_HZBDepthConvention");
        internal static Vector4 hzbDepthConvention
        {
            get
            {
                bool glClipDepth = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore ||
                                   SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
                return new Vector4(SystemInfo.usesReversedZBuffer ? 1f : 0f,
                    SystemInfo.graphicsUVStartsAtTop ? 1f : 0f, glClipDepth ? 0.5f : 1f, glClipDepth ? 0.5f : 0f);
            }
        }
        private static readonly int s_idHzbMipOut = Shader.PropertyToID("_HZBMipOut");
        private static readonly int s_idHzbCameraPosition = Shader.PropertyToID("_HZBCameraPosition");
        private static readonly int s_idHzbPixelExpansion = Shader.PropertyToID("_HZBPixelExpansion");
        private static readonly int s_idHzbTranslationScale = Shader.PropertyToID("_HZBTranslationScale");
        private static readonly int s_idHzbTailStartMip = Shader.PropertyToID("_HZBTailStartMip");
        private static readonly int s_idHzbTailMipCount = Shader.PropertyToID("_HZBTailMipCount");

        // must set the entire HLSL array at once via the base property name
        private static readonly int s_idOccluderMipBounds = Shader.PropertyToID("_OccluderMipBounds");

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

        internal bool isGPUOcclusionEnabled => SystemInfo.supportsComputeShaders &&
                                               Config.useGPUOcclusionCulling &&
                                               _occlusionShader != null &&
                                               _hzbDownscaleSPDKernel >= 0;

        // expose per-camera HZB state for render graph
        internal RTHandle pendingHZBRTHandle => _currentCullingContext?.hzbState?.hzbRTHandle;
        internal ComputeShader occlusionShader => _occlusionShader;
        internal int hzbDownscaleKernel => _hzbDownscaleSPDKernel;
        internal int HZBResolveMSAAKernel(bool textureArray) => textureArray ? _hzbResolveMSAAArrayKernel : _hzbResolveMSAA2DKernel;
        internal int idHzbSource => s_idHzbSource;

        // expose for render graph TextureHandle binding
        internal ComputeShader cullingShaderRef => _cullingShader;
        internal int cullChunksKernelRef => _cullChunksKernel;
        internal int cullInstancesKernelRef => _cullInstancesKernel;
        internal static int idHzbTextureRef => s_idHzbTexture;
        internal static int idHzbMipOutRef => s_idHzbMipOut;
        internal int hzbDownscaleTailKernel => _hzbDownscaleTailKernel;


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
            _hzbResolveMSAA2DKernel = _occlusionShader.HasKernel("HZBResolveMSAA2D") ? _occlusionShader.FindKernel("HZBResolveMSAA2D") : -1;
            _hzbResolveMSAAArrayKernel = _occlusionShader.HasKernel("HZBResolveMSAAArray") ? _occlusionShader.FindKernel("HZBResolveMSAAArray") : -1;
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

            if (!isGPUOcclusionEnabled)
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
                    _renderCycleCounter - state.lastSeenFrame > k_hzbEvictFrames)
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
            if (!isGPUOcclusionEnabled || camera.cameraType != CameraType.Game) return;
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
            for (int i = 0; i < k_maxOccluderMips; i++)
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
            int topMipW = math.max(1, screenWidth >> k_firstDepthMipIndex);
            int topMipH = math.max(1, screenHeight >> k_firstDepthMipIndex);

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
            cmd.SetComputeVectorParam(shader, idHzbDepthConvention, hzbDepthConvention);
            var pyramidSize = new Vector4(hzbState.atlasWidth, hzbState.atlasHeight, 1f / hzbState.atlasWidth, 1f / hzbState.atlasHeight);
            cmd.SetComputeVectorParam(shader, s_idOccluderDepthPyramidSize, pyramidSize);
            _floatParams2[0] = hzbState.topMipWidth; _floatParams2[1] = hzbState.topMipHeight;
            cmd.SetComputeFloatParams(shader, s_idDepthSizeInOccluderPixels, _floatParams2);
            cmd.SetComputeIntParam(shader, s_idHzbMipCount, hzbState.mipCount);
            cmd.SetComputeIntParams(shader, s_idOccluderMipBounds, hzbState.mipBoundsFlat);
        }

        private bool _warnedHZBSizeMismatch;

        bool _hzbSourceTexArray;

        // Match the bound depth texture's dimension; different cameras can use different types.
        internal void SetHZBSourceTexArray(bool texArray)
        {
            if (_occlusionShader == null || texArray == _hzbSourceTexArray) return;
            _hzbSourceTexArray = texArray;
            if (texArray) _occlusionShader.EnableKeyword("HZB_SOURCE_TEXARRAY");
            else _occlusionShader.DisableKeyword("HZB_SOURCE_TEXARRAY");
        }

        /// <summary>
        /// Builds the atlas for this camera snapshot. The kernel queries the actual source
        /// texture extent independently of the active viewport.
        /// </summary>
        internal void ExecuteHZBBuild(CommandBuffer cmd, HZBBuildContext camera, int depthWidth, int depthHeight,
            bool skipHZBOutputBinding = false)
        {
            var hzbState = camera.state;
            if (_occlusionShader == null || _hzbDownscaleSPDKernel < 0 || hzbState == null)
                return;

            // frozen data path - do not rebuild, would overwrite game camera's atlas with scene view depth
            if (camera.frozen)
                return;

            // texture pre-created on main thread; skip if atlas missing or dimensions changed
            if (hzbState.hzbAtlas == null) return;
            int expectedW = math.max(1, depthWidth >> k_firstDepthMipIndex);
            int expectedH = math.max(1, depthHeight >> k_firstDepthMipIndex);
            if (hzbState.topMipWidth != expectedW || hzbState.topMipHeight != expectedH)
            {
                if (!_warnedHZBSizeMismatch)
                {
                    _warnedHZBSizeMismatch = true;
                    Debug.LogWarning($"[FraxinusGPUBatcher] HZB atlas size mismatch (atlas {hzbState.topMipWidth}x{hzbState.topMipHeight} vs depth {expectedW}x{expectedH}); occlusion disabled this frame. EnsureHZBAtlas must be called with the build's depth dims.");
                }
                return;
            }

            const int k_spdTile = 16;
            const int k_spdMipLevels = 5; // mips 0-4 in one dispatch
            cmd.SetComputeVectorParam(_occlusionShader, idHzbDepthConvention, hzbDepthConvention);

            cmd.SetComputeIntParams(_occlusionShader, s_idOccluderMipBounds, hzbState.mipBoundsFlat);
            cmd.SetComputeIntParam(_occlusionShader, s_idHzbMipCount, hzbState.mipCount);
            if (!skipHZBOutputBinding)
                cmd.SetComputeTextureParam(_occlusionShader, _hzbDownscaleSPDKernel, s_idHzbMipOut, hzbState.hzbAtlas);

            int groupsX = (hzbState.topMipWidth + k_spdTile - 1) / k_spdTile;
            int groupsY = (hzbState.topMipHeight + k_spdTile - 1) / k_spdTile;

#if DEBUG
            cmd.BeginSample("FraxinusGPUBatcher.HZBDownscaleSPD");
#endif
            cmd.DispatchCompute(_occlusionShader, _hzbDownscaleSPDKernel, groupsX, groupsY, 1);
#if DEBUG
            cmd.EndSample("FraxinusGPUBatcher.HZBDownscaleSPD");
#endif

            // mips 5+: separate dispatch, single workgroup
            if (k_spdMipLevels < hzbState.mipCount)
            {
                if (!skipHZBOutputBinding)
                    cmd.SetComputeTextureParam(_occlusionShader, _hzbDownscaleTailKernel, s_idHzbMipOut, hzbState.hzbAtlas);
                cmd.SetComputeIntParam(_occlusionShader, s_idHzbTailStartMip, k_spdMipLevels);
                cmd.SetComputeIntParam(_occlusionShader, s_idHzbTailMipCount, hzbState.mipCount);
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
