using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Experimental.Rendering;
using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;


#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    public abstract class ScriptableRenderer : BaseGraphProcessor
    {
        private static partial class Profiling
        {
            private const string k_name = nameof(ScriptableRenderer);
            public static readonly ProfilingSampler setPerCameraShaderVariables = new ProfilingSampler($"{k_name}.{nameof(SetPerCameraShaderVariables)}");
            public static readonly ProfilingSampler recordRenderGraph = new ProfilingSampler($"Record Render Graph");
            public static readonly ProfilingSampler setupCamera = new ProfilingSampler($"Setup Camera Parameters");
            public static readonly ProfilingSampler initRenderGraphFrame = new ProfilingSampler($"Initialize Render Graph frame settings");
            public static readonly ProfilingSampler clearRenderingState = new ProfilingSampler($"{k_name}.{nameof(ClearRenderingState)}");
            public static readonly ProfilingSampler drawGizmos = new ProfilingSampler($"DrawGizmos");
            public static readonly ProfilingSampler drawWireOverlay = new ProfilingSampler($"DrawWireOverlay");

            internal static readonly ProfilingSampler setEditorTarget = new ProfilingSampler($"Set Editor Target");
        }

#if UNITY_SWITCH || UNITY_ANDROID
        public GraphicsFormat depthStencilFormatDefault = GraphicsFormat.D24_UNorm_S8_UInt;
        public int depthBufferBits = 24;
#else
        public GraphicsFormat depthStencilFormatDefault = GraphicsFormat.D32_SFloat_S8_UInt;
        public int depthBufferBits = 32;
#endif
        public virtual RTHandle currentRenderGraphCameraColorHandle { get => TextureHandle.nullHandle; }

        public virtual RTHandle nextRenderGraphCameraColorHandle { get => TextureHandle.nullHandle; }

        public static bool requiresIntermediateAttachments;


        public class RenderingFeatures
        {
            public bool msaa { get; set; } = true;
        }

        private struct InjectPassInfo
        {
            public Type injectorType;
            public InjectionPoint point;
            public bool finalInjectPostPass;
            public ContextScopeFilter scopeFilter;
        }

        private static Dictionary<Type, List<InjectPassInfo>> s_passToInjectorMap;
        private static List<InjectPassInfo> s_fallbackInjectors;
        private static List<Type> s_moduleTypes;
        private static IReadOnlyList<Type> s_injectPassTypes;

        /// <summary>Automatically discovered injectable modules shared by runtime and graph tools.</summary>
        public static IReadOnlyList<Type> injectPassTypes
        {
            get
            {
                CachePassModuleTypes();
                return s_injectPassTypes;
            }
        }

        private readonly List<(InjectPass pass, ContextScopeFilter filter)> _fallbackInjections = new();
        private readonly List<string> _appliedDisabledInjectPasses = new();
        private int _mainFallbackCount;
        private int _subFallbackCount;

        public RenderingFeatures supportedRenderingFeatures { get; set; } = new RenderingFeatures();

        internal UltraRendererGraph graph => base._graph as UltraRendererGraph;

        ContextContainer _frameData = new();
        internal ContextContainer frameData => _frameData;

        internal DebugHandler debugHandler { get; }

        internal static ScriptableRenderer current = null;

        internal bool useRenderPassEnabled = false;

        protected List<BaseNode> _sortedNodes = new List<BaseNode>();
        protected List<BaseNode> _nodesConnectivity = new List<BaseNode>();

        private static Plane[] s_planes = new Plane[6];
        private static Vector4[] s_vectorPlanes = new Vector4[6];
        internal bool useDepthPriming { get; set; } = false;

        internal bool stripShadowsOffVariants { get; set; } = false;

        internal bool stripAdditionalLightOffVariants { get; set; } = false;

        protected RenderingMode _renderingMode;

        internal virtual RenderingMode renderingMode => _renderingMode;

        internal RenderingLayerSetting renderinglayerSetting = new();

        private Dictionary<Type, IRendererModule> _moduleContainer = new Dictionary<Type, IRendererModule>();

        Material _debugBlitMaterial = Blitter.GetBlitMaterial(TextureXR.dimension);

        public virtual GraphicsFormat cameraDepthTextureFormat { get => depthStencilFormatDefault; }
        public virtual GraphicsFormat cameraDepthAttachmentFormat { get => depthStencilFormatDefault; }

        public virtual bool accurateGbufferNormals { get => false; }

        public List<BaseNode> sortedNodes
        {
            get => _sortedNodes;
        }

        public ScriptableRenderer(ScriptableRendererGraph graph) : base(graph)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            debugHandler = new DebugHandler();
#endif

            SetupPassModules();
            InjectPassToRenderPassNode();
            graph.injectPassSettingsChanged += InjectPassToRenderPassNode;
        }

        protected override void Dispose(bool dispose)
        {
            graph.injectPassSettingsChanged -= InjectPassToRenderPassNode;
            debugHandler?.Dispose();

            foreach (var kvp in _moduleContainer)
                kvp.Value.Dispose();

            _moduleContainer.Clear();

            GC.SuppressFinalize(this);
        }

        private void SetupPassModules()
        {
            CachePassModuleTypes();

            foreach (var type in s_moduleTypes)
            {
                if (_moduleContainer.ContainsKey(type))
                    continue;
                try
                {
                    var instance = Activator.CreateInstance(type) as IRendererModule;
                    if (instance != null)
                        _moduleContainer[type] = instance;
                }
                catch (Exception ex)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.LogWarning($"Failed to auto-register pass {type.Name}: {ex.Message}");
#endif
                }
            }

        }

        private static void CachePassModuleTypes()
        {
            if (s_moduleTypes != null)
                return;

            // Types and attributes are shared by all renderers in this scripting domain.
            // Only the module instances and graph bindings belong to a renderer.
            s_moduleTypes = new List<Type>();
            s_passToInjectorMap = new Dictionary<Type, List<InjectPassInfo>>();
            s_fallbackInjectors = new List<InjectPassInfo>();
            var injectTypes = new List<Type>();

            var passTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); }
                    catch { return Type.EmptyTypes; }
                })
                .Where(t => (typeof(BuiltinPass).IsAssignableFrom(t) || typeof(InjectPass).IsAssignableFrom(t))
                            && !t.IsAbstract && !t.ContainsGenericParameters
                            && t != typeof(BuiltinPass)
                            && t != typeof(InjectPass));

            foreach (var type in passTypes)
            {
                if (type.GetConstructor(Type.EmptyTypes) == null)
                    continue;
                s_moduleTypes.Add(type);

                if (typeof(InjectPass).IsAssignableFrom(type))
                {
                    var attributes = type.GetCustomAttributes(typeof(InjectPassAttribute), true);
                    if (attributes.OfType<InjectPassAttribute>().Any(attribute => attribute.targetPassType != null))
                        injectTypes.Add(type);
                    foreach (InjectPassAttribute attr in attributes)
                    {
                        if (attr?.targetPassType != null)
                        {
                            var info = new InjectPassInfo
                            {
                                injectorType = type,
                                point = attr.point,
                                finalInjectPostPass = attr.finalInjectPostPass,
                                scopeFilter = attr.scopeFilter
                            };

                            if (!s_passToInjectorMap.TryGetValue(attr.targetPassType, out var list))
                            {
                                list = new List<InjectPassInfo>();
                                s_passToInjectorMap[attr.targetPassType] = list;
                            }
                            list.Add(info);

                            if (attr.finalInjectPostPass)
                            {
                                int index = s_fallbackInjectors.FindIndex(candidate => candidate.injectorType == type);
                                if (index < 0)
                                    s_fallbackInjectors.Add(info);
                                else
                                {
                                    var fallback = s_fallbackInjectors[index];
                                    fallback.scopeFilter |= info.scopeFilter;
                                    s_fallbackInjectors[index] = fallback;
                                }
                            }
                        }
                    }
                }
            }
            s_injectPassTypes = injectTypes.AsReadOnly();
        }

        private void ConfigureFallbackInjections()
        {
            _fallbackInjections.Clear();
            _mainFallbackCount = _subFallbackCount = 0;
            foreach (var info in s_fallbackInjectors)
            {
                if (!graph.IsInjectPassEnabled(info.injectorType))
                    continue;
                if (!_moduleContainer.TryGetValue(info.injectorType, out var module) || !(module is InjectPass pass))
                    continue;
                pass.fallbackScopeFilter = info.scopeFilter;
                _fallbackInjections.Add((pass, info.scopeFilter));
                if ((info.scopeFilter & ContextScopeFilter.Main) != 0) ++_mainFallbackCount;
                if ((info.scopeFilter & ContextScopeFilter.Sub) != 0) ++_subFallbackCount;
            }
        }


        internal virtual void ReleaseRenderTargets() { }

        public T GetModule<T>() where T : IRendererModule
        {
            if (_moduleContainer.TryGetValue(typeof(T), out var module))
                return (T)module;
            else
                throw new KeyNotFoundException($"No object of type {typeof(T).Name} found in the coreContainer.");
        }

        public T SetModule<T>(T obj) where T : IRendererModule
        {
            _moduleContainer[typeof(T)] = obj;
            if (obj is InjectPass)
            {
                InjectPassToRenderPassNode();
            }
            return obj;
        }

        public T GetPass<T>() where T : BaseNode
        {
            return _sortedNodes.FirstOrDefault(n => n is T) as T;
        }

        void SetPerCameraShaderVariables(RasterCommandBuffer cmd, CameraData cameraData, Vector2Int cameraTargetSizeCopy, bool isTargetFlipped)
        {
            using var profScope = new ProfilingScope(Profiling.setPerCameraShaderVariables);

            Camera camera = cameraData.camera;

            float scaledCameraTargetWidth = (float)cameraTargetSizeCopy.x;
            float scaledCameraTargetHeight = (float)cameraTargetSizeCopy.y;
            float cameraWidth = cameraData.isSubContext ? cameraData.pixelWidth : camera.pixelWidth;
            float cameraHeight = cameraData.isSubContext ? cameraData.pixelHeight : camera.pixelHeight;

            if (camera.allowDynamicResolution && !cameraData.isSubContext)
            {
                scaledCameraTargetWidth *= ScalableBufferManager.widthScaleFactor;
                scaledCameraTargetHeight *= ScalableBufferManager.heightScaleFactor;
            }

            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;
            float invNear = Mathf.Approximately(near, 0.0f) ? 0.0f : 1.0f / near;
            float invFar = Mathf.Approximately(far, 0.0f) ? 0.0f : 1.0f / far;
            float isOrthographic = camera.orthographic ? 1.0f : 0.0f;

            // From http://www.humus.name/temp/Linearize%20depth.txt
            // But as depth component textures on OpenGL always return in 0..1 range (as in D3D), we have to use
            // the same constants for both D3D and OpenGL here.
            // OpenGL would be this:
            // zc0 = (1.0 - far / near) / 2.0;
            // zc1 = (1.0 + far / near) / 2.0;
            // D3D is this:
            float zc0 = 1.0f - far * invNear;
            float zc1 = far * invNear;

            Vector4 zBufferParams = new Vector4(zc0, zc1, zc0 * invFar, zc1 * invFar);
            if (SystemInfo.usesReversedZBuffer)
            {
                zBufferParams.y += zBufferParams.x;
                zBufferParams.x = -zBufferParams.x;
                zBufferParams.w += zBufferParams.z;
                zBufferParams.z = -zBufferParams.z;
            }

            // Projection flip sign logic is very deep in GfxDevice::SetInvertProjectionMatrix
            // This setup is tailored especially for overlay camera game view
            // For other scenarios this will be overwritten correctly by SetupCameraProperties
            float projectionFlipSign = isTargetFlipped ? -1.0f : 1.0f;
            Vector4 projectionParams = new Vector4(projectionFlipSign, near, far, 1.0f * invFar);
            cmd.SetGlobalVector(ShaderPropertyId.projectionParams, projectionParams);

            Vector4 orthoParams = new Vector4(camera.orthographicSize * cameraData.aspectRatio, camera.orthographicSize, 0.0f, isOrthographic);

            // Camera and Screen variables as described in https://docs.unity3d.com/Manual/SL-UnityShaderVariables.html
            cmd.SetGlobalVector(ShaderPropertyId.worldSpaceCameraPos, cameraData.worldSpaceCameraPos);
            cmd.SetGlobalVector(ShaderPropertyId.screenParams, new Vector4(cameraWidth, cameraHeight, 1.0f + 1.0f / cameraWidth, 1.0f + 1.0f / cameraHeight));
            cmd.SetGlobalVector(ShaderPropertyId.scaledScreenParams, new Vector4(scaledCameraTargetWidth, scaledCameraTargetHeight, 1.0f + 1.0f / scaledCameraTargetWidth, 1.0f + 1.0f / scaledCameraTargetHeight));
            cmd.SetGlobalVector(ShaderPropertyId.zBufferParams, zBufferParams);
            cmd.SetGlobalVector(ShaderPropertyId.orthoParams, orthoParams);

            cmd.SetGlobalVector(ShaderPropertyId.screenSize, new Vector4(scaledCameraTargetWidth, scaledCameraTargetHeight, 1.0f / scaledCameraTargetWidth, 1.0f / scaledCameraTargetHeight));
            cmd.SetKeyword(ShaderGlobalKeywords.SCREEN_COORD_OVERRIDE, cameraData.useScreenCoordOverride);
            cmd.SetGlobalVector(ShaderPropertyId.screenSizeOverride, cameraData.screenSizeOverride);
            cmd.SetGlobalVector(ShaderPropertyId.screenCoordScaleBias, cameraData.screenCoordScaleBias);

            // { w / RTHandle.maxWidth, h / RTHandle.maxHeight } : xy = currFrame, zw = prevFrame
            // TODO(@sandy-carter) set to RTHandles.rtHandleProperties.rtHandleScale once dynamic scaling is set up
            cmd.SetGlobalVector(ShaderPropertyId.rtHandleScale, Vector4.one);

            //Set per camera matrices.
            SetCameraMatrices(cmd, cameraData, true, isTargetFlipped);
        }

        void SetPerCameraClippingPlaneProperties(RasterCommandBuffer cmd, in CameraData cameraData, bool isTargetFlipped)
        {
            Matrix4x4 projectionMatrix = cameraData.GetGPUProjectionMatrix(isTargetFlipped);
            Matrix4x4 viewMatrix = cameraData.GetViewMatrix();

            Matrix4x4 viewProj = CoreMatrixUtils.MultiplyProjectionMatrix(projectionMatrix, viewMatrix, cameraData.camera.orthographic);
            Plane[] planes = s_planes;
            GeometryUtility.CalculateFrustumPlanes(viewProj, planes);

            Vector4[] cameraWorldClipPlanes = s_vectorPlanes;
            for (int i = 0; i < planes.Length; ++i)
                cameraWorldClipPlanes[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);

            cmd.SetGlobalVectorArray(ShaderPropertyId.cameraWorldClipPlanes, cameraWorldClipPlanes);
        }

        void SetPerCameraBillboardProperties(RasterCommandBuffer cmd,  CameraData cameraData)
        {
            Matrix4x4 worldToCameraMatrix = cameraData.GetViewMatrix();
            Vector3 cameraPos = cameraData.worldSpaceCameraPos;

            cmd.SetKeyword(ShaderGlobalKeywords.BillboardFaceCameraPos, QualitySettings.billboardsFaceCameraPosition);

            Vector3 billboardTangent;
            Vector3 billboardNormal;
            float cameraXZAngle;
            CalculateBillboardProperties(worldToCameraMatrix, out billboardTangent, out billboardNormal, out cameraXZAngle);

            cmd.SetGlobalVector(ShaderPropertyId.billboardNormal, new Vector4(billboardNormal.x, billboardNormal.y, billboardNormal.z, 0.0f));
            cmd.SetGlobalVector(ShaderPropertyId.billboardTangent, new Vector4(billboardTangent.x, billboardTangent.y, billboardTangent.z, 0.0f));
            cmd.SetGlobalVector(ShaderPropertyId.billboardCameraParams, new Vector4(cameraPos.x, cameraPos.y, cameraPos.z, cameraXZAngle));
        }

        private static void CalculateBillboardProperties(
            in Matrix4x4 worldToCameraMatrix,
            out Vector3 billboardTangent,
            out Vector3 billboardNormal,
            out float cameraXZAngle)
        {
            Matrix4x4 cameraToWorldMatrix = worldToCameraMatrix;
            cameraToWorldMatrix = cameraToWorldMatrix.transpose;

            Vector3 cameraToWorldMatrixAxisX = new Vector3(cameraToWorldMatrix.m00, cameraToWorldMatrix.m10, cameraToWorldMatrix.m20);
            Vector3 cameraToWorldMatrixAxisY = new Vector3(cameraToWorldMatrix.m01, cameraToWorldMatrix.m11, cameraToWorldMatrix.m21);
            Vector3 cameraToWorldMatrixAxisZ = new Vector3(cameraToWorldMatrix.m02, cameraToWorldMatrix.m12, cameraToWorldMatrix.m22);

            Vector3 front = cameraToWorldMatrixAxisZ;

            Vector3 worldUp = Vector3.up;
            Vector3 cross = Vector3.Cross(front, worldUp);
            billboardTangent = !Mathf.Approximately(cross.sqrMagnitude, 0.0f)
                ? cross.normalized
                : cameraToWorldMatrixAxisX;

            billboardNormal = Vector3.Cross(worldUp, billboardTangent);
            billboardNormal = !Mathf.Approximately(billboardNormal.sqrMagnitude, 0.0f)
                ? billboardNormal.normalized
                : cameraToWorldMatrixAxisY;

            // SpeedTree generates billboards starting from looking towards X- and rotates counter clock-wisely
            Vector3 worldRight = new Vector3(0, 0, 1);
            // signed angle is calculated on X-Z plane
            float s = worldRight.x * billboardTangent.z - worldRight.z * billboardTangent.x;
            float c = worldRight.x * billboardTangent.x + worldRight.z * billboardTangent.z;
            cameraXZAngle = Mathf.Atan2(s, c);

            // convert to [0,2PI)
            if (cameraXZAngle < 0)
                cameraXZAngle += 2 * Mathf.PI;
        }

        internal static void SetCameraMatrices(RasterCommandBuffer cmd, CameraData cameraData, bool setInverseMatrices, bool isTargetFlipped)
        {
            // NOTE: the UltraRP default main view/projection matrices are the CameraData view/projection matrices.
            Matrix4x4 viewMatrix = cameraData.GetViewMatrix();
            Matrix4x4 projectionMatrix = cameraData.GetProjectionMatrix(); // Jittered, non-gpu

            // TODO: Investigate why SetViewAndProjectionMatrices is causing y-flip / winding order issue
            // for now using cmd.SetViewProjecionMatrices
            //SetViewAndProjectionMatrices(cmd, viewMatrix, cameraData.GetDeviceProjectionMatrix(), setInverseMatrices);

            // Set the default view/projection, note: projectionMatrix will be set as a gpu-projection (gfx api adjusted) for rendering.
            cmd.SetViewProjectionMatrices(viewMatrix, projectionMatrix);

            if (setInverseMatrices)
            {
                Matrix4x4 gpuProjectionMatrix = cameraData.GetGPUProjectionMatrix(isTargetFlipped); // TODO: invProjection might NOT match the actual projection (invP*P==I) as the target flip logic has diverging paths.
                Matrix4x4 inverseViewMatrix = Matrix4x4.Inverse(viewMatrix);
                Matrix4x4 inverseProjectionMatrix = Matrix4x4.Inverse(gpuProjectionMatrix);
                Matrix4x4 inverseViewProjection = inverseViewMatrix * inverseProjectionMatrix;

                // There's an inconsistency in handedness between unity_matrixV and unity_WorldToCamera
                // Unity changes the handedness of unity_WorldToCamera (see Camera::CalculateMatrixShaderProps)
                // we will also change it here to avoid breaking existing shaders. (case 1257518)
                Matrix4x4 worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)) * viewMatrix;
                Matrix4x4 cameraToWorldMatrix = worldToCameraMatrix.inverse;
                cmd.SetGlobalMatrix(ShaderPropertyId.worldToCameraMatrix, worldToCameraMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.cameraToWorldMatrix, cameraToWorldMatrix);

                cmd.SetGlobalMatrix(ShaderPropertyId.inverseViewMatrix, inverseViewMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.inverseProjectionMatrix, inverseProjectionMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.inverseViewAndProjectionMatrix, inverseViewProjection);
            }

            // TODO: Add SetPerCameraClippingPlaneProperties here once we are sure it correctly behaves in overlay camera for some time
        }

        #region Interface

        public virtual int SupportedCameraStackingTypes()
        {
            return 0;
        }

        public bool SupportsCameraStackingType(CameraRenderType cameraRenderType)
        {
            return (SupportedCameraStackingTypes() & 1 << (int)cameraRenderType) != 0;
        }

        protected internal virtual bool SupportsMotionVectors()
        {
            return false;
        }

        public override void UpdateComputeOrder(IEnumerable<BaseNode> computedNodes) 
        {
            _sortedNodes.Clear();

            foreach (var node in graph.nodes)
                node.excuteOrder = -1;

            var targetNodes = new HashSet<BaseNode>();
            if (graph.postNode != null)
                targetNodes.Add(graph.postNode);
            if (graph.drivenNode != null)
                targetNodes.Add(graph.drivenNode);
            
            AddPairedContextNodesToTargets(targetNodes);

            HashSet<BaseNode> finalNodes = new HashSet<BaseNode>();
            foreach (var node in targetNodes)
            {
                foreach (var dep in GetNodeDependencies(node))
                {
                    if (graph.nodes.Contains(dep) && dep.computeOrder >= 0)
                        finalNodes.Add(dep);
                }
            }
            
            RemoveUnpairedContextNodes(finalNodes);

            _sortedNodes = finalNodes
                .OrderBy(d =>
                {
                    if (d is ScriptableRenderPassNode pass)
                        return pass.renderOrder == RenderOrder.BeforeDrivenPass ? -2 : (int)pass.renderOrder;
                    return -1;
                })
                .ThenBy(d => d.computeOrder)
                .ToList();

            InjectPassToRenderPassNode();
        }
        

        private void AddPairedContextNodesToTargets(HashSet<BaseNode> targetNodes)
        {
            foreach (var node in graph.nodes)
            {
                if (node is ContextEndNode endNode && endNode.computeOrder >= 0)
                {
                    var beginNode = FindPairedContextBegin(endNode);
                    if (beginNode != null && beginNode.computeOrder >= 0)
                        targetNodes.Add(endNode);
                }
            }
        }
        
        private ContextBeginNode FindPairedContextBegin(ContextEndNode endNode)
        {
            var visited = new HashSet<BaseNode>();
            var queue = new Queue<(BaseNode node, int nesting)>();
            foreach (var input in endNode.GetInputNodes())
                queue.Enqueue((input, 0));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (!visited.Add(item.node))
                    continue;
                int nesting = item.nesting;
                if (item.node is ContextEndNode)
                    nesting++;
                if (item.node is ContextBeginNode begin)
                {
                    if (nesting == 0)
                        return begin;
                    nesting--;
                }
                foreach (var input in item.node.GetInputNodes())
                    queue.Enqueue((input, nesting));
            }
            return null;
        }

        private void RemoveUnpairedContextNodes(HashSet<BaseNode> nodes)
        {
            var toRemove = new List<BaseNode>();
            
            foreach (var node in nodes)
            {
                if (node is ContextEndNode endNode)
                {
                    var beginNode = FindPairedContextBegin(endNode);
                    if (beginNode == null || beginNode.computeOrder < 0)
                        toRemove.Add(endNode);
                }
                else if (node is ContextBeginNode beginNode)
                {
                    bool hasPairedEnd = false;
                    foreach (var other in nodes)
                    {
                        if (other is ContextEndNode otherEnd && FindPairedContextBegin(otherEnd) == beginNode)
                        {
                            hasPairedEnd = true;
                            break;
                        }
                    }
                    if (!hasPairedEnd)
                        toRemove.Add(beginNode);
                }
            }
            
            foreach (var node in toRemove)
                nodes.Remove(node);
        }

        public override void UpdateConnectivity()
        {
            _nodesConnectivity.Clear();
            _nodesConnectivity.AddRange(GraphUtils.DepthFirstSort(graph, graph.postNode));
        }

        private readonly Dictionary<ScriptableRenderPassNode, ContextBeginNode> _nodeContexts = new();
        private readonly Dictionary<ScriptableRenderPassNode, ContextBeginNode> _nodeAfterContexts = new();
        private readonly HashSet<(InjectPass pass, ContextBeginNode scope)> _primaryInjectionContexts = new();
        private readonly HashSet<InjectPass> _boundInjectPasses = new();

        internal bool IsSameContext(ScriptableRenderPassNode first, ScriptableRenderPassNode second)
        {
            _nodeContexts.TryGetValue(first, out var a);
            _nodeContexts.TryGetValue(second, out var b);
            return ReferenceEquals(a, b);
        }

        protected void InjectPassToRenderPassNode()
        {
            if (s_passToInjectorMap is null)
                return;

            ConfigureFallbackInjections();
            _appliedDisabledInjectPasses.Clear();
            foreach (var typeId in graph.disabledInjectPasses)
                _appliedDisabledInjectPasses.Add(typeId);

            _nodeContexts.Clear();
            _nodeAfterContexts.Clear();
            _primaryInjectionContexts.Clear();
            _boundInjectPasses.Clear();
            foreach (var node in graph.nodes)
                if (node is ScriptableRenderPassNode passNode)
                    passNode.ClearAllInjectPasses();

            var scopes = new Stack<ContextBeginNode>();
            foreach (var item in _sortedNodes)
            {
                if (!(item is ScriptableRenderPassNode pass)) continue;
                _nodeContexts[pass] = scopes.Count > 0 ? scopes.Peek() : null;
                if (pass is ContextBeginNode begin) scopes.Push(begin);
                else if (pass is ContextEndNode && scopes.Count > 0) scopes.Pop();
                _nodeAfterContexts[pass] = scopes.Count > 0 ? scopes.Peek() : null;
            }

            // Candidate selection is cached here; execution is deduplicated per scope each frame.

            foreach (var node in _sortedNodes)
            {
                if (node is ScriptableRenderPassNode passNode)
                {
                    if (s_passToInjectorMap.TryGetValue(passNode.GetType(), out var infos))
                    {
                        foreach (var info in infos)
                        {
                            TryInjectPass(passNode, info);
                        }
                    }
                }
            }

            var postNode = graph.postNode;
            if (postNode != null && _fallbackInjections.Count > 0 && _nodeContexts.TryGetValue(postNode, out var postScope))
            {
                postNode.SetInjectionFallbackRenderer(this);
                var scopeFilter = postScope == null ? ContextScopeFilter.Main : ContextScopeFilter.Sub;
                foreach (var fallback in _fallbackInjections)
                {
                    if ((fallback.filter & scopeFilter) != 0)
                        _boundInjectPasses.Add(fallback.pass);
                    // A primary node owns the visible binding. Only passes without
                    // a primary in this scope are shown at the fallback location.
                    if ((fallback.filter & scopeFilter) != 0 &&
                        !_primaryInjectionContexts.Contains((fallback.pass, postScope)))
                        postNode.AddFallbackInjectPass(fallback.pass);
                }
            }
        }

        private bool TryInjectPass(ScriptableRenderPassNode targetNode, InjectPassInfo info)
        {
            if (!graph.IsInjectPassEnabled(info.injectorType))
                return false;

            var contexts = info.point == InjectionPoint.Before ? _nodeContexts : _nodeAfterContexts;
            contexts.TryGetValue(targetNode, out var scope);
            var scopeFilter = scope == null ? ContextScopeFilter.Main : ContextScopeFilter.Sub;
            if ((info.scopeFilter & scopeFilter) == 0)
                return false;

            if (_moduleContainer.TryGetValue(info.injectorType, out var module))
            {
                if (module is InjectPass injector && injector.IsEligibleToInject(this, targetNode))
                {
                    targetNode.AddInjectPass(injector, info.point, info.scopeFilter);
                    _primaryInjectionContexts.Add((injector, scope));
                    _boundInjectPasses.Add(injector);
                    return true;
                }
            }
            return false;
        }

        internal void RefreshInjectPassSettings()
        {
            // Undo and deserialization update serialized fields without invoking
            // the setter. Compare exact values before camera requirements are
            // collected, and only rebuild bindings when the settings changed.
            var disabled = graph.disabledInjectPasses;
            if (disabled.Count == _appliedDisabledInjectPasses.Count)
            {
                bool matches = true;
                for (int i = 0; i < disabled.Count; ++i)
                    if (disabled[i] != _appliedDisabledInjectPasses[i])
                    {
                        matches = false;
                        break;
                    }
                if (matches)
                    return;
            }
            InjectPassToRenderPassNode();
        }

        internal bool PrepareInjectPasses(RenderGraph renderGraph, ContextContainer frameData)
        {
            bool requiresFinalBlit = false;
            foreach (var module in _moduleContainer.Values)
                if (module is InjectPass pass)
                    pass.ResetFrame(renderGraph, frameData);

            foreach (var module in _moduleContainer.Values)
                if (module is InjectPass pass && _boundInjectPasses.Contains(pass) && graph.IsInjectPassEnabled(pass.GetType()))
                    requiresFinalBlit |= pass.PrepareFrame(renderGraph, frameData);

            var rendering = frameData.Get<RenderingData>();
            rendering.renderPassInputs.requiresDepthTexture |= rendering.renderPassInputs.requiresMotionVectors;
            rendering.renderPassInputs.requiresIntermediateTexture |= requiresFinalBlit;
            return requiresFinalBlit;
        }

        internal void ProcessFallbackInjections(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            var state = frameData.GetOrCreate<ContextStateManager>();
            int count = state.IsInSubContext() ? _subFallbackCount : _mainFallbackCount;
            if (!state.HasPendingFallbacks(count))
                return;

            // Preserve one ordered fallback queue, including primaries skipped by
            // SupportedInCurrentFrame. Nothing is re-registered during rendering.
            foreach (var fallback in _fallbackInjections)
                if (graph.IsInjectPassEnabled(fallback.pass.GetType()) && state.TryBeginInjection(fallback.pass, fallback.filter))
                    fallback.pass.Execute(renderGraph, context, frameData);
        }

        public sealed override void Run() => throw new Exception("Do not use it, use RecordRenderGraph instead");

        public void RecordRenderGraph(RenderGraph renderGraph, ScriptableRenderContext context)
        {
            using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RecordRenderGraph)))
            {
                var contextState = frameData.GetOrCreate<ContextStateManager>();
                contextState.Reset();
                OnBeginRenderGraphFrame();
                InitRenderGraphFrame(renderGraph);
                contextState.EnsureMainContext(frameData);

                using (new ProfilingScope(Profiling.recordRenderGraph))
                {
                    OnRecordRenderGraph(renderGraph, context);
                }

                OnEndRenderGraphFrame();

#if UNITY_EDITOR
                CameraData cameraData = frameData.Get<CameraData>();
                if (cameraData.isSceneViewCamera)
                    SetEditorTarget(renderGraph);
#endif
            }
        }

        internal void FinishRenderGraphRendering(CommandBuffer cmd)
        {
            CameraData cameraData = frameData.Get<CameraData>();
            foreach (var coreCell in _moduleContainer.Values)
                coreCell.OnCameraCleanup(cmd);

            OnFinishRenderGraphRendering(cmd);
            // InternalFinishRenderingCommon(cmd, cameraData.resolveFinalTarget);
        }

        protected virtual void OnFinishRenderGraphRendering(CommandBuffer cmd) { }

        public virtual void StartRenderGraphRendering(in CameraData cameraData) { }

        public virtual void OnBeginRenderGraphFrame() { }

        public virtual void OnEndRenderGraphFrame() { }

        public virtual void OnRecordRenderGraph(RenderGraph renderGraph, ScriptableRenderContext context) { }

        public virtual void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters, ref CameraData cameraData) { }

        public virtual void OnPreCullRenderPasses(in CameraData cameraData) { }

        #endregion

        List<BaseNode> GetNodeDependencies(BaseNode node)
        {
            HashSet<BaseNode> dependencies = new HashSet<BaseNode>();
            Stack<BaseNode> inputNodes = new Stack<BaseNode>(node.GetInputNodes());

            dependencies.Add(node);

            while (inputNodes.Count > 0)
            {
                var child = inputNodes.Pop();
                if (!dependencies.Add(child))
                    continue;
                
                if (child.computeOrder >= 0)
                {
                    foreach (var parent in child.GetInputNodes())
                        inputNodes.Push(parent);
                }

                foreach (var parent in child.GetInputNodes())
                    inputNodes.Push(parent);

                // Max dependencies on a node, maybe we can put a warning if it's reached?
                if (dependencies.Count > 2000)
                    break;
            }

            return dependencies.OrderBy(d => d.computeOrder).ToList();
        }

        private class PassData
        {
            internal ScriptableRenderer renderer;
            internal CameraData cameraData;
            internal bool isTargetBackbuffer;

            // The size of the camera target changes during the frame so we must make a copy of it here to preserve its record-time value.
            internal Vector2Int cameraTargetSizeCopy;
        };

        private void InitRenderGraphFrame(RenderGraph renderGraph)
        {
            using (var builder = renderGraph.AddUnsafePass<PassData>("InitFrame", out var passData, Profiling.initRenderGraphFrame))
            {
                passData.renderer = this;

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext rgContext) =>
                {
                    UnsafeCommandBuffer cmd = rgContext.cmd;
#if UNITY_EDITOR
                    float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#else
                    float time = Time.time;
#endif
                    float deltaTime = Time.deltaTime;
                    float smoothDeltaTime = Time.smoothDeltaTime;

                    ClearRenderingState(cmd);
                    SetShaderTimeValues(cmd, time, deltaTime, smoothDeltaTime);
                });
            }
        }
       
        internal void SetupRenderGraphCameraProperties(RenderGraph renderGraph, bool isTargetBackbuffer)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("SetupCameraProperties", out var passData, Profiling.setupCamera))
            {
                passData.renderer = this;
                passData.cameraData = frameData.Get<CameraData>();
                passData.cameraTargetSizeCopy = new Vector2Int(passData.cameraData.cameraTargetDescriptor.width, passData.cameraData.cameraTargetDescriptor.height);
                passData.isTargetBackbuffer = isTargetBackbuffer;

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    bool yFlip = !SystemInfo.graphicsUVStartsAtTop || data.isTargetBackbuffer;

                    // - Camera billboard properties.
                    // - Camera frustum planes: unity_CameraWorldClipPlanes[6]
                    // - _ProjectionParams.x logic is deep inside GfxDevice
                    if (data.cameraData.renderType == CameraRenderType.Base)
                    {
                        context.cmd.SetupCameraProperties(data.cameraData.camera);
                        data.renderer.SetPerCameraShaderVariables(cmd, data.cameraData, data.cameraTargetSizeCopy, !yFlip);
                    }
                    else
                    {
                        data.renderer.SetPerCameraShaderVariables(cmd, data.cameraData, data.cameraTargetSizeCopy, !yFlip);
                        data.renderer.SetPerCameraClippingPlaneProperties(cmd, in data.cameraData, !yFlip);
                        data.renderer.SetPerCameraBillboardProperties(cmd, data.cameraData);
                    }

#if UNITY_EDITOR
                    float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
#else
                    float time = Time.time;
#endif
                    float deltaTime = Time.deltaTime;
                    float smoothDeltaTime = Time.smoothDeltaTime;

                    // Reset shader time variables as they were overridden in SetupCameraProperties.
                    // If we don't do it we might have a mismatch between shadows and main rendering
                    SetShaderTimeValues(cmd, time, deltaTime, smoothDeltaTime);
                });
            }
        }

        private class DrawGizmosPassData
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            public RendererListHandle gizmoRenderList;
#else
            public Camera camera;
            public GizmoSubset gizmoSubset;
#endif
            public TextureHandle color;
            public TextureHandle depth;
        };

        internal void DrawRenderGraphGizmos(RenderGraph renderGraph, ContextContainer frameData, TextureHandle color, TextureHandle depth, GizmoSubset gizmoSubset)
        {
#if UNITY_EDITOR
            CameraData cameraData = frameData.Get<CameraData>();

            if (!Handles.ShouldRenderGizmos() || cameraData.camera.sceneViewFilterMode == Camera.SceneViewFilterMode.ShowFiltered)
                return;

            // We cannot draw gizmo rendererlists from an raster pass as the gizmo rendering triggers the  MonoBehaviour.OnDrawGizmos or MonoBehaviour.OnDrawGizmosSelected callbacks that could run arbitrary graphics code
            // like SetRenderTarget, texture and resource loading, ...
            using (var builder = renderGraph.AddUnsafePass<DrawGizmosPassData>("Draw Gizmos Pass", out var passData,
                Profiling.drawGizmos))
            {
                builder.UseTexture(color, AccessFlags.Write);
                builder.UseTexture(depth, AccessFlags.ReadWrite);

                passData.color = color;
                passData.depth = depth;
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
                passData.gizmoRenderList = renderGraph.CreateGizmoRendererList(cameraData.camera, gizmoSubset);
                builder.UseRendererList(passData.gizmoRenderList);
#else
                // Unity 2022 draws through the render context; the compatibility
                // bridge flushes the target binding before invoking the callbacks.
                passData.camera = cameraData.camera;
                passData.gizmoSubset = gizmoSubset;
#endif
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((DrawGizmosPassData data, UnsafeGraphContext rgContext) =>
                {
                    using (new ProfilingScope(rgContext.cmd, Profiling.drawGizmos))
                    {
                        rgContext.cmd.SetRenderTarget(data.color, data.depth);
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
                        rgContext.cmd.DrawRendererList(data.gizmoRenderList);
#else
                        rgContext.cmd.DrawGizmos(data.camera, data.gizmoSubset);
#endif
                    }
                });
            }
#endif
        }

        private class DrawWireOverlayPassData
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            public RendererListHandle wireOverlayList;
#else
            public Camera camera;
            public TextureHandle color;
#endif
        };

        internal void DrawRenderGraphWireOverlay(RenderGraph renderGraph, ContextContainer frameData, TextureHandle color)
        {
#if UNITY_EDITOR
            CameraData cameraData = frameData.Get<CameraData>();

            if (!cameraData.isSceneViewCamera)
                return;

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            using (var builder = renderGraph.AddRasterRenderPass<DrawWireOverlayPassData>(Profiling.drawWireOverlay.name, out var passData,
                       Profiling.drawWireOverlay))
            {
                builder.SetRenderAttachment(color, 0, AccessFlags.Write);

                passData.wireOverlayList = renderGraph.CreateWireOverlayRendererList(cameraData.camera);
                builder.UseRendererList(passData.wireOverlayList);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (DrawWireOverlayPassData data, RasterGraphContext rgContext) =>
                {
                    using (new ProfilingScope(rgContext.cmd, Profiling.drawWireOverlay))
                    {
                        rgContext.cmd.DrawRendererList(data.wireOverlayList);
                    }
                });
            }
#else
            // Unity 2022 has no wire-overlay RendererList factory. Keep its
            // immediate context draw outside a native raster pass.
            using (var builder = renderGraph.AddUnsafePass<DrawWireOverlayPassData>(Profiling.drawWireOverlay.name, out var passData,
                       Profiling.drawWireOverlay))
            {
                passData.camera = cameraData.camera;
                passData.color = color;
                builder.UseTexture(color, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (DrawWireOverlayPassData data, UnsafeGraphContext rgContext) =>
                {
                    using (new ProfilingScope(rgContext.cmd, Profiling.drawWireOverlay))
                    {
                        rgContext.cmd.SetRenderTarget(data.color);
                        rgContext.cmd.DrawWireOverlay(data.camera);
                    }
                });
            }
#endif
#endif
        }

        private class DummyData { }

        private void SetEditorTarget(RenderGraph renderGraph)
        {
            using (var builder = renderGraph.AddUnsafePass<DummyData>("SetEditorTarget", out var passData, Profiling.setEditorTarget))
            {
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((DummyData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget,
                        RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,         // color
                        RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare);     // depth
                });
            }
        }

        static void SetShaderTimeValues(IBaseCommandBuffer cmd, float time, float deltaTime, float smoothDeltaTime)
        {
            float timeEights = time / 8f;
            float timeFourth = time / 4f;
            float timeHalf = time / 2f;

            float lastTime = time - ShaderUtils.PersistentDeltaTime;

            // Time values
            Vector4 timeVector = time * new Vector4(1f / 20f, 1f, 2f, 3f);
            Vector4 sinTimeVector = new Vector4(Mathf.Sin(timeEights), Mathf.Sin(timeFourth), Mathf.Sin(timeHalf), Mathf.Sin(time));
            Vector4 cosTimeVector = new Vector4(Mathf.Cos(timeEights), Mathf.Cos(timeFourth), Mathf.Cos(timeHalf), Mathf.Cos(time));
            Vector4 deltaTimeVector = new Vector4(deltaTime, 1f / deltaTime, smoothDeltaTime, 1f / smoothDeltaTime);
            Vector4 timeParametersVector = new Vector4(time, Mathf.Sin(time), Mathf.Cos(time), 0.0f);
            Vector4 lastTimeParametersVector = new Vector4(lastTime, Mathf.Sin(lastTime), Mathf.Cos(lastTime), 0.0f);

            cmd.SetGlobalVector(ShaderPropertyId.time, timeVector);
            cmd.SetGlobalVector(ShaderPropertyId.sinTime, sinTimeVector);
            cmd.SetGlobalVector(ShaderPropertyId.cosTime, cosTimeVector);
            cmd.SetGlobalVector(ShaderPropertyId.deltaTime, deltaTimeVector);
            cmd.SetGlobalVector(ShaderPropertyId.timeParameters, timeParametersVector);
            cmd.SetGlobalVector(ShaderPropertyId.lastTimeParameters, lastTimeParametersVector);
        }

        static void ClearRenderingState(IBaseCommandBuffer cmd)
        {
            using var profScope = new ProfilingScope(Profiling.clearRenderingState);
            // These modes are selected by this camera's shadow passes. Keeping a
            // previous camera/frame's cascade keyword can enable two mutually
            // exclusive compute variants when the main light disappears.
            cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadows, false);
            cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadowCascades, false);
            cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadowScreen, false);
            cmd.SetKeyword(ShaderGlobalKeywords.AdditionalLightShadows, false);
            cmd.SetKeyword(ShaderGlobalKeywords.SoftShadows, false);
            cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsLow, false);
            cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsMedium, false);
            cmd.SetKeyword(ShaderGlobalKeywords.SoftShadowsHigh, false);

            // DBuffer textures belong to the current camera's graph. A camera
            // without an active decal node must not inherit another camera's
            // DBuffer variant after its transient textures have been released.
            // The DBuffer pass enables the required mode again before receivers.
            cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT1, false);
            cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT2, false);
            cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT3, false);
            cmd.SetKeyword(ShaderGlobalKeywords.DecalLayers, false);
        }

        internal bool IsSceneFilteringEnabled(Camera camera)
        {
#if UNITY_EDITOR
            if (CoreUtils.IsSceneFilteringEnabled() && camera.sceneViewFilterMode == Camera.SceneViewFilterMode.ShowFiltered)
                return true;
#endif
            return false;
        }


        internal void SetRenderingLayersGlobalTextures(RenderGraph renderGraph)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();

            if (resourceData.renderingLayersTexture.IsValid() && !renderingData.hasDeferred)
                RenderGraphUtils.SetGlobalTexture(renderGraph, Shader.PropertyToID(renderinglayerSetting.renderingLayersTextureName), resourceData.renderingLayersTexture, "Set Global Rendering Layers Texture");
        }

        private protected int AdjustAndGetScreenMSAASamples(RenderGraph renderGraph, bool useIntermediateColorTarget)
        {
            #if UNITY_EDITOR
                // In the editor, the system render target is always allocated with no msaa
                // See: ConfigureTargetTexture in PlayModeView.cs
                return 1;
            #else
#if UNITY_6000_0_OR_NEWER
                // In the players, when UltraRP main rendering is done to an intermediate target and NRP enabled
                // we disable multisampling for the system backbuffer as a bandwidth optimization
                // doing so, we avoid storing costly msaa samples back to system memory for nothing
                bool canOptimizeScreenMSAASamples = UltraRenderPipeline.canOptimizeScreenMSAASamples
                                                 && useIntermediateColorTarget
                                                 && renderGraph.nativeRenderPassesEnabled
                                                 && Screen.msaaSamples > 1;

                if (canOptimizeScreenMSAASamples)
                {
                    Screen.SetMSAASamples(1);
                }

                // iOS and macOS corner case
                bool screenAPIHasOneFrameDelay = (Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.IPhonePlayer);

                return screenAPIHasOneFrameDelay ? Mathf.Max(UltraRenderPipeline.startFrameScreenMSAASamples, 1) : Mathf.Max(Screen.msaaSamples, 1);
#else
                // The Unity 2022 engine does not expose Screen.msaaSamples/SetMSAASamples.
                // Its backbuffer sample count follows QualitySettings and cannot be adjusted
                // for a single RenderGraph frame.
                return Mathf.Max(QualitySettings.antiAliasing, 1);
#endif
            #endif
        }

        internal virtual bool useNativeRenderPassRenderGraphCompiler { get => false; }

        public virtual bool supportsGPUOcclusion => false;

        public virtual bool isForwardPlus => false;
        public virtual bool usesForwardPlusLightLists => isForwardPlus;

        public virtual void SetupRenderGraphFinalPassDebug(RenderGraph renderGraph, ContextContainer frameData) { }

        public virtual void SetupAfterPostRenderGraphFinalPassDebug(RenderGraph renderGraph, ContextContainer frameData) { }

        class CopyToDebugTexturePassData
        {
            internal TextureHandle src;
            internal TextureHandle dest;
        }

        internal void BlitToDebugTexture(RenderGraph renderGraph, TextureHandle source, TextureHandle destination, bool isSourceTextureColor = false)
        {
            if (source.IsValid())
            {
                if (isSourceTextureColor) // Use AddCopyPass (RasterRenderPass) when we can
                    renderGraph.AddCopyPass(source, destination);
                else // Use direct blit (UnsafePass)
                {
                    var blitMaterialParameters =
                        new UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils.BlitMaterialParameters(
                            source, destination,
                            _debugBlitMaterial, 0);

                    renderGraph.AddBlitPass(blitMaterialParameters);
                }
            }
            else // Texture is invalid, just show a black view
                BlitEmptyTexture(renderGraph, destination);
        }

        private void BlitEmptyTexture(RenderGraph renderGraph, TextureHandle destination, string passName = "Copy To Debug Texture")
        {
            using (var builder = renderGraph.AddRasterRenderPass<CopyToDebugTexturePassData>(passName, out var passData))
            {
                passData.src = renderGraph.defaultResources.blackTexture;
                passData.dest = destination;
                builder.SetRenderAttachment(destination, 0);

                builder.AllowPassCulling(false);
                builder.SetRenderFunc((CopyToDebugTexturePassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.src, new Vector4(1, 1, 0, 0), 0, false);
                });
            }
        }

        public bool DebugHandlerRequireDepthPass(CameraData cameraData)
        {
            if ((debugHandler != null) && debugHandler.IsActiveForCamera(cameraData.isPreviewCamera))
            {
                if (debugHandler.TryGetFullscreenDebugMode(out _))
                    return true;
            }

            return false;
        }
    }
}
