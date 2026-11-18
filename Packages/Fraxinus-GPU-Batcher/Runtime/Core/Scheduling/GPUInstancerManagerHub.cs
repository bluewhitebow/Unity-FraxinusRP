using System;
using System.Collections.Generic;

namespace Fraxinus.GPUBatcher
{
    public static class GPUInstancerManagerHub
    {
        private static readonly List<GPUInstancerManager> s_managers = new List<GPUInstancerManager>();
        private static readonly List<GPUInstancerManager> s_snapshot = new List<GPUInstancerManager>();

        public static IReadOnlyList<GPUInstancerManager> activeManagers
        {
            get
            {
                RemoveDisposedManagers();
                return s_managers;
            }
        }


        public static bool IsRegistered(GPUInstancerManager manager)
        {
            if (manager == null)
                return false;

            if (!manager.isDisposed)
                return s_managers.Contains(manager);

            s_managers.Remove(manager);
            GPUInstancerLoopScheduler.CancelCoroutines(manager);
            return false;
        }

        public static void Register(GPUInstancerManager manager)
        {
            if (manager == null)
                return;
            if (manager.isDisposed)
            {
                s_managers.Remove(manager);
                GPUInstancerLoopScheduler.CancelCoroutines(manager);
                throw new ObjectDisposedException(manager.name ?? manager.GetType().Name);
            }
            if (s_managers.Contains(manager))
                return;

            bool added = false;
            try
            {
                manager.Initialize();
                s_managers.Add(manager);
                added = true;

                manager.runtimeDataInitializedDuringLastActivate = false;
                manager.Activate();
                GPUInstancerLoopScheduler.Install();
            }
            catch
            {
                if (added)
                    s_managers.Remove(manager);
                try
                {
                    manager.Dispose();
                }
                catch (Exception cleanupException)
                {
                    UnityEngine.Debug.LogException(cleanupException, manager.diagnosticContext);
                }
                throw;
            }
        }

        public static bool Unregister(GPUInstancerManager manager)
        {
            if (manager == null || !s_managers.Remove(manager))
                return false;

            GPUInstancerLoopScheduler.CancelCoroutines(manager);
            if (manager.isDisposed)
                return true;

            manager.Deactivate();
            manager.MarkDeactivationCompleted();
            return true;
        }

        public static void Clear()
        {
            CopyToSnapshot();
            s_managers.Clear();
            try
            {
                for (int i = 0; i < s_snapshot.Count; i++)
                {
                    GPUInstancerManager manager = s_snapshot[i];
                    if (manager == null)
                        continue;

                    GPUInstancerLoopScheduler.CancelCoroutines(manager);
                    if (manager.isDisposed)
                        continue;

                    try
                    {
                        manager.Deactivate();
                        manager.MarkDeactivationCompleted();
                    }
                    catch (Exception exception)
                    {
                        UnityEngine.Debug.LogException(exception, manager.diagnosticContext);
                    }
                }
            }
            finally
            {
                s_snapshot.Clear();
            }
        }

        internal static void Tick()
        {
            ForEach(manager => manager.Tick());
        }

        internal static void PrepareFrame()
        {
            ForEach(manager => manager.PrepareFrame());
        }

        internal static void RenderForCamera(UnityEngine.Camera camera)
        {
            ForEach(manager => manager.RenderForCamera(camera));
        }

        private static void ForEach(Action<GPUInstancerManager> action)
        {
            CopyToSnapshot();
            for (int i = 0; i < s_snapshot.Count; i++)
            {
                GPUInstancerManager manager = s_snapshot[i];
                if (manager == null || manager.isDisposed || !manager.isActive)
                    continue;

                try
                {
                    action(manager);
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogException(exception, manager.diagnosticContext);
                }
            }
            s_snapshot.Clear();
        }

        private static void CopyToSnapshot()
        {
            s_snapshot.Clear();
            RemoveDisposedManagers();
            s_snapshot.AddRange(s_managers);
        }

        private static void RemoveDisposedManagers()
        {
            for (int i = s_managers.Count - 1; i >= 0; i--)
            {
                GPUInstancerManager manager = s_managers[i];
                if (manager != null && !manager.isDisposed)
                    continue;

                s_managers.RemoveAt(i);
                if (manager != null)
                    GPUInstancerLoopScheduler.CancelCoroutines(manager);
            }
        }
    }
}
