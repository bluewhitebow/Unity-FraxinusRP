using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Rendering;
#endif

namespace Fraxinus.GPUBatcher
{
    public static class GPUInstancerLoopScheduler
    {
        private sealed class TickMarker { }
        private sealed class PrepareFrameMarker { }

        private sealed class ManagerReferenceComparer : IEqualityComparer<GPUInstancerManager>
        {
            public bool Equals(GPUInstancerManager x, GPUInstancerManager y) => ReferenceEquals(x, y);

            public int GetHashCode(GPUInstancerManager value) => ReferenceEquals(value, null) ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }

        private sealed class ScheduledCoroutine
        {
            public readonly GPUInstancerManager owner;
            public readonly IEnumerator routine;
            public bool isExecuting;
            public bool disposeRequested;
            public bool isDisposed;

            public ScheduledCoroutine(GPUInstancerManager owner, IEnumerator routine)
            {
                this.owner = owner;
                this.routine = routine;
            }
        }

        private static readonly List<ScheduledCoroutine> s_coroutines = new List<ScheduledCoroutine>();
        private static readonly List<ScheduledCoroutine> s_coroutineSnapshot = new List<ScheduledCoroutine>();
        private static readonly HashSet<GPUInstancerManager> s_cancellingOwners = new HashSet<GPUInstancerManager>(new ManagerReferenceComparer());
        private static bool s_isClearingCoroutines;
        private static bool s_isRunningCoroutines;
        private static bool s_installed;

#if UNITY_EDITOR
        private const string k_sceneViewCameraName = "SceneCamera";
        internal static string sceneViewCameraName => k_sceneViewCameraName;
        private static readonly List<EditorLoopState> s_editorLoopStates = new List<EditorLoopState>();
        private static GPUInstancerCameraData s_sceneViewCameraData = new GPUInstancerCameraData(null);
        private static bool s_editorCallbacksInstalled;
#endif

        public static bool isInstalled => s_installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Uninstall();
            GPUInstancerDriver.ResetStatics();
            ClearCoroutines();
            s_cancellingOwners.Clear();
#if UNITY_EDITOR
            s_editorLoopStates.Clear();
            EditorApplication.update -= RunEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            s_editorCallbacksInstalled = false;
            s_sceneViewCameraData = new GPUInstancerCameraData(null);
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Install()
        {
            if (s_installed)
                return;

            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveSystem(ref loop, typeof(TickMarker));
            RemoveSystem(ref loop, typeof(PrepareFrameMarker));

            InsertAfter(ref loop, typeof(Update.ScriptRunBehaviourUpdate), new PlayerLoopSystem
            {
                type = typeof(TickMarker),
                updateDelegate = RunTick
            });
            InsertAfter(ref loop, typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate), new PlayerLoopSystem
            {
                type = typeof(PrepareFrameMarker),
                updateDelegate = RunPrepareFrame
            });

            PlayerLoop.SetPlayerLoop(loop);
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
            s_installed = true;
#if GPUI_FRAXINUS
            GPUInstancerFraxinusRenderBridge.Install();
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterConfiguredManagers()
        {
            GPUInstancerDriver.Bootstrap();
            GPUInstancerTerrainBinder.BindLoadedProxies();
        }

        public static void Uninstall()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            bool changed = RemoveSystem(ref loop, typeof(TickMarker));
            changed |= RemoveSystem(ref loop, typeof(PrepareFrameMarker));
            if (changed)
                PlayerLoop.SetPlayerLoop(loop);

            Application.quitting -= Shutdown;
            s_installed = false;
#if GPUI_FRAXINUS
            GPUInstancerFraxinusRenderBridge.Uninstall();
#endif
        }

        public static void StartCoroutine(IEnumerator routine)
        {
            StartCoroutine(null, routine);
        }

        public static void StartCoroutine(GPUInstancerManager owner, IEnumerator routine)
        {
            if (routine == null)
                return;

            if (owner != null && (owner.isDisposed || s_cancellingOwners.Contains(owner)))
            {
                DisposeCoroutine(routine);
                return;
            }

            if (s_isClearingCoroutines)
            {
                DisposeCoroutine(routine);
                return;
            }

            s_coroutines.Add(new ScheduledCoroutine(owner, routine));
        }

        internal static void CancelCoroutines(GPUInstancerManager owner)
        {
            if (ReferenceEquals(owner, null))
                return;

            bool ownsCancellationScope = s_cancellingOwners.Add(owner);
            try
            {
                for (int i = s_coroutines.Count - 1; i >= 0; i--)
                {
                    ScheduledCoroutine scheduled = s_coroutines[i];
                    if (scheduled == null || !ReferenceEquals(scheduled.owner, owner))
                        continue;

                    s_coroutines.RemoveAt(i);
                    RequestCoroutineDispose(scheduled);
                }
            }
            finally
            {
                if (ownsCancellationScope)
                    s_cancellingOwners.Remove(owner);
            }
        }

#if UNITY_EDITOR
        internal static void RegisterEditorLoopState(EditorLoopState state)
        {
            if (state == null || state.manager == null || state.manager.isDisposed ||
                s_editorLoopStates.Contains(state))
                return;

            s_editorLoopStates.Add(state);
        }

        internal static void StartEditorPreview(EditorLoopState state)
        {
            if (state == null || state.manager == null || state.manager.isDisposed ||
                (Application.isPlaying && !EditorApplication.isPaused))
            {
                if (state != null && (state.manager == null || state.manager.isDisposed))
                    s_editorLoopStates.Remove(state);
                return;
            }

            RegisterEditorLoopState(state);
            InstallEditorCallbacks();
            if (!state.manager.isActive)
                state.manager.Activate();

            state.initializingInstances = true;
            state.simulateAtEditor = true;
            FindSceneViewCamera();
            SceneView.RepaintAll();
        }

        internal static void StopEditorPreview(EditorLoopState state, bool remove = false)
        {
            if (state == null)
                return;

            bool managerUnavailable = state.manager == null || state.manager.isDisposed;
            if (!Application.isPlaying && !managerUnavailable)
                state.manager.ClearInstancingData();

            state.simulateAtEditor = false;
            state.initializingInstances = false;
            if (remove || managerUnavailable)
                s_editorLoopStates.Remove(state);

            UninstallEditorCallbacksIfUnused();
            SceneView.RepaintAll();
        }

        private static void InstallEditorCallbacks()
        {
            if (s_editorCallbacksInstalled)
                return;

            EditorApplication.update -= RunEditorUpdate;
            EditorApplication.update += RunEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            EditorApplication.pauseStateChanged += OnPauseStateChanged;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            s_editorCallbacksInstalled = true;
        }

        private static void UninstallEditorCallbacksIfUnused()
        {
            for (int i = s_editorLoopStates.Count - 1; i >= 0; i--)
            {
                EditorLoopState state = s_editorLoopStates[i];
                if (state == null || state.manager == null || state.manager.isDisposed)
                {
                    s_editorLoopStates.RemoveAt(i);
                    continue;
                }
                if (state.simulateAtEditor)
                    return;
            }

            EditorApplication.update -= RunEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            s_editorCallbacksInstalled = false;
        }

        private static void RunEditorUpdate()
        {
            if (Application.isPlaying && !EditorApplication.isPaused)
                return;

            FindSceneViewCamera();
            RunTick();
        }

        private static void TickEditorManagers()
        {
            for (int i = s_editorLoopStates.Count - 1; i >= 0; i--)
            {
                EditorLoopState state = s_editorLoopStates[i];
                if (state == null || state.manager == null || state.manager.isDisposed)
                {
                    s_editorLoopStates.RemoveAt(i);
                    continue;
                }
                if (!state.simulateAtEditor)
                    continue;

                GPUInstancerManager manager = state.manager;
                if (state.initializingInstances)
                {
                    manager.Initialize();
                    if (!manager.isInitialized)
                        manager.InitializeRuntimeDataAndBuffers();
                    state.initializingInstances = false;
                }
                else if (!manager.isInitialized)
                {
                    state.initializingInstances = true;
                    continue;
                }

                manager.Tick();
            }
        }

        private static void FindSceneViewCamera()
        {
            Camera camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            if (camera == null && Camera.current != null && Camera.current.name == k_sceneViewCameraName)
                camera = Camera.current;
            if (camera == null || s_sceneViewCameraData.mainCamera == camera)
                return;

            s_sceneViewCameraData.SetCamera(camera);
            s_sceneViewCameraData.renderOnlySelectedCamera = true;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            RenderEditorCamera(camera);
        }

        private static void RenderEditorCamera(Camera camera)
        {
            if ((Application.isPlaying && !EditorApplication.isPaused) || camera == null)
                return;

            for (int i = 0; i < s_editorLoopStates.Count; i++)
            {
                EditorLoopState state = s_editorLoopStates[i];
                if (state == null || !state.simulateAtEditor || state.initializingInstances ||
                    state.manager == null || state.manager.isDisposed || !state.manager.isInitialized)
                    continue;

                if (s_sceneViewCameraData.mainCamera == camera)
                    state.manager.UpdateBuffers(s_sceneViewCameraData);
                else if (state.manager.cameraData.mainCamera == camera)
                    state.manager.UpdateBuffers(state.manager.cameraData);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            for (int i = 0; i < s_editorLoopStates.Count; i++)
                StopEditorPreview(s_editorLoopStates[i]);
        }

        private static void OnPauseStateChanged(PauseState state)
        {
            if (!Application.isPlaying)
                return;

            for (int i = 0; i < s_editorLoopStates.Count; i++)
            {
                if (state == PauseState.Paused)
                    StartEditorPreview(s_editorLoopStates[i]);
                else
                    StopEditorPreview(s_editorLoopStates[i]);
            }
        }
#endif

        private static void RunTick()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying || EditorApplication.isPaused)
            {
                TickEditorManagers();
                RunCoroutines();
                return;
            }
#endif
            GPUInstancerManagerHub.Tick();
            RunCoroutines();
        }

        private static void RunCoroutines()
        {
            if (s_isRunningCoroutines || s_coroutines.Count == 0)
                return;

            s_isRunningCoroutines = true;
            s_coroutineSnapshot.Clear();
            s_coroutineSnapshot.AddRange(s_coroutines);
            try
            {
                for (int i = s_coroutineSnapshot.Count - 1; i >= 0; i--)
                {
                    ScheduledCoroutine scheduled = s_coroutineSnapshot[i];
                    if (!ContainsScheduledCoroutine(scheduled))
                        continue;

                    bool finished = scheduled == null || scheduled.routine == null ||
                        (scheduled.owner != null && scheduled.owner.isDisposed);
                    Exception failure = null;
                    if (!finished)
                    {
                        scheduled.isExecuting = true;
                        try
                        {
                            finished = !scheduled.routine.MoveNext();
                        }
                        catch (Exception exception)
                        {
                            finished = true;
                            failure = exception;
                        }
                        finally
                        {
                            scheduled.isExecuting = false;
                        }
                    }

                    if (finished)
                        RemoveScheduledCoroutine(scheduled);

                    if (finished || (scheduled != null && scheduled.disposeRequested))
                        RequestCoroutineDispose(scheduled);

                    if (failure != null)
                        Debug.LogException(failure, scheduled.owner?.diagnosticContext);
                }
            }
            finally
            {
                s_coroutineSnapshot.Clear();
                s_isRunningCoroutines = false;
            }
        }

        private static bool ContainsScheduledCoroutine(ScheduledCoroutine scheduled)
        {
            for (int i = 0; i < s_coroutines.Count; i++)
            {
                if (ReferenceEquals(s_coroutines[i], scheduled))
                    return true;
            }
            return false;
        }

        private static void RemoveScheduledCoroutine(ScheduledCoroutine scheduled)
        {
            for (int i = s_coroutines.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(s_coroutines[i], scheduled))
                    continue;
                s_coroutines.RemoveAt(i);
                return;
            }
        }

        private static void RequestCoroutineDispose(ScheduledCoroutine scheduled)
        {
            if (scheduled == null || scheduled.isDisposed)
                return;
            if (scheduled.isExecuting)
            {
                scheduled.disposeRequested = true;
                return;
            }

            scheduled.isDisposed = true;
            scheduled.disposeRequested = false;
            DisposeCoroutine(scheduled.routine);
        }

        private static void ClearCoroutines()
        {
            if (s_coroutines.Count == 0)
                return;

            s_isClearingCoroutines = true;
            try
            {
                ScheduledCoroutine[] pending = s_coroutines.ToArray();
                s_coroutines.Clear();
                for (int i = pending.Length - 1; i >= 0; i--)
                    RequestCoroutineDispose(pending[i]);
            }
            finally
            {
                s_isClearingCoroutines = false;
            }
        }

        private static void DisposeCoroutine(IEnumerator routine)
        {
            IDisposable disposable = routine as IDisposable;
            if (disposable == null)
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static void RunPrepareFrame()
        {
            GPUInstancerManagerHub.PrepareFrame();
        }

        private static void Shutdown()
        {
            GPUInstancerDriver.Shutdown();
            ClearCoroutines();
            Uninstall();
        }

        private static void InsertAfter(ref PlayerLoopSystem system, Type target, PlayerLoopSystem insertion)
        {
            if (!TryInsertAfter(ref system, target, insertion))
                throw new InvalidOperationException($"PlayerLoop system '{target.FullName}' was not found.");
        }

        private static bool TryInsertAfter(ref PlayerLoopSystem system, Type target, PlayerLoopSystem insertion)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null)
                return false;

            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type == target)
                {
                    PlayerLoopSystem[] expanded = new PlayerLoopSystem[children.Length + 1];
                    Array.Copy(children, 0, expanded, 0, i + 1);
                    expanded[i + 1] = insertion;
                    Array.Copy(children, i + 1, expanded, i + 2, children.Length - i - 1);
                    system.subSystemList = expanded;
                    return true;
                }

                PlayerLoopSystem child = children[i];
                if (!TryInsertAfter(ref child, target, insertion))
                    continue;
                children[i] = child;
                system.subSystemList = children;
                return true;
            }

            return false;
        }

        private static bool RemoveSystem(ref PlayerLoopSystem system, Type marker)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null)
                return false;

            bool changed = false;
            List<PlayerLoopSystem> kept = null;
            for (int i = 0; i < children.Length; i++)
            {
                PlayerLoopSystem child = children[i];
                if (child.type == marker)
                {
                    kept ??= new List<PlayerLoopSystem>(children);
                    kept.RemoveAll(entry => entry.type == marker);
                    changed = true;
                    break;
                }

                if (RemoveSystem(ref child, marker))
                {
                    children[i] = child;
                    changed = true;
                }
            }

            system.subSystemList = kept != null ? kept.ToArray() : children;
            return changed;
        }
#if UNITY_EDITOR
        public sealed class EditorLoopState
        {
            public GPUInstancerManager manager { get; }
            public bool simulateAtEditor { get; internal set; }
            public bool initializingInstances { get; internal set; }

            public EditorLoopState(GPUInstancerManager manager)
            {
                if (manager == null)
                    throw new ArgumentNullException(nameof(manager));

                if (manager.isDisposed)
                    throw new ObjectDisposedException(manager.name ?? manager.GetType().Name);

                this.manager = manager;
                RegisterEditorLoopState(this);
            }

            public void StartSimulation()
            {
                StartEditorPreview(this);
            }

            public void StopSimulation()
            {
                StopEditorPreview(this);
            }

            public void ClearEditorUpdates()
            {
                StopEditorPreview(this, true);
            }
        }
#endif
    }
}
