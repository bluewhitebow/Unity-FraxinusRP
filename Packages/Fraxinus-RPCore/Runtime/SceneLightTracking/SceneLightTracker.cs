using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering.Universal
{
    // The engine grants this bridge assembly access to ObjectDispatcher. Keep
    // the native API here so the renderer does not need reflection or a second
    // scene traversal to discover lights created after its initial scan.
    internal sealed class SceneLightTracker : IDisposable
    {
        sealed class TrackedLight
        {
            internal readonly Light light;
            internal readonly GameObject gameObject;
            internal ulong hideFlags;
            internal int directionalIndex = -1;

            internal TrackedLight(Light light)
            {
                this.light = light;
                gameObject = light.gameObject;
                hideFlags = GetHideFlags(this);
            }
        }

        readonly ObjectDispatcher m_Dispatcher = new ObjectDispatcher();
        readonly Action<TypeDispatchData> m_OnChanged;
        readonly HashSet<int> m_KnownIds = new HashSet<int>();
        readonly Dictionary<int, TrackedLight> m_KnownLights = new Dictionary<int, TrackedLight>();
        readonly List<TrackedLight> m_DirectionalLights = new List<TrackedLight>();
        readonly List<Object> m_ChangedObjects = new List<Object>();
        Light[] m_Lights = Array.Empty<Light>();
        bool m_MembershipChanged = true;

        internal SceneLightTracker()
        {
            m_OnChanged = OnChanged;
            m_Dispatcher.EnableTypeTracking<Light>(ObjectDispatcher.TypeTrackingFlags.SceneObjects |
                ObjectDispatcher.TypeTrackingFlags.EditorOnlyObjects);
        }

        internal Light[] GetLights()
        {
            // The callback receives native ID arrays, never a freshly allocated
            // scripting array. Animated light properties do not require a scan.
            m_Dispatcher.DispatchTypeChangesAndClear<Light>(m_OnChanged, false, true);
            // HideFlags changes on a Light or its GameObject do not reliably
            // produce Light events. Only directional lights affect APRT. Keep
            // excluded directionals here too so they can become visible again.
            for (int i = 0; i < m_DirectionalLights.Count; ++i)
            {
                var tracked = m_DirectionalLights[i];
                if (tracked.light == null || tracked.gameObject == null) continue;
                ulong flags = GetHideFlags(tracked);
                if (flags == tracked.hideFlags) continue;
                tracked.hideFlags = flags;
                m_MembershipChanged = true;
            }
            if (m_MembershipChanged)
            {
                // Keep the engine's original unsorted scene order, including
                // inactive lights so activation changes need no new arrays.
                m_Lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < m_Lights.Length; ++i)
                {
                    RememberLight(m_Lights[i]);
                }
                m_MembershipChanged = false;
            }
            return m_Lights;
        }

        void OnChanged(TypeDispatchData changes)
        {
            // Process destruction first, so a reused native ID is a new member.
            for (int i = 0; i < changes.destroyedID.Length; ++i)
            {
                int id = changes.destroyedID[i];
                if (m_KnownIds.Remove(id)) m_MembershipChanged = true;
                if (m_KnownLights.TryGetValue(id, out var tracked)) RemoveDirectional(tracked);
                m_KnownLights.Remove(id);
            }
            if (changes.changedID.Length == 0) return;

            bool hasNewLights = false;
            for (int i = 0; i < changes.changedID.Length; ++i)
            {
                int id = changes.changedID[i];
                if (m_KnownIds.Add(id)) hasNewLights = true;
                else if (m_KnownLights.TryGetValue(id, out var tracked)) UpdateDirectional(tracked);
            }
            if (!hasNewLights) return;
            m_MembershipChanged = true;

            // Track IDs independently of the query result: DontSave/editor
            // lights can be reported by the dispatcher but excluded by Find.
            // Their animated properties must not trigger a scan every frame.
            Resources.InstanceIDToObjectList(changes.changedID, m_ChangedObjects);
            for (int i = 0; i < m_ChangedObjects.Count; ++i)
            {
                if (!(m_ChangedObjects[i] is Light light) || light == null) continue;
                RememberLight(light);
            }
            m_ChangedObjects.Clear();
        }

        void RememberLight(Light light)
        {
            int id = light.GetInstanceID();
            if (!m_KnownLights.TryGetValue(id, out var tracked))
            {
                tracked = new TrackedLight(light);
                m_KnownLights.Add(id, tracked);
            }
            m_KnownIds.Add(id);
            tracked.hideFlags = GetHideFlags(tracked);
            UpdateDirectional(tracked);
        }

        void UpdateDirectional(TrackedLight tracked)
        {
            if (tracked.light == null || tracked.light.type != LightType.Directional)
            {
                RemoveDirectional(tracked);
                return;
            }
            if (tracked.directionalIndex >= 0) return;
            tracked.directionalIndex = m_DirectionalLights.Count;
            m_DirectionalLights.Add(tracked);
            // Point/spot lights are not polled. If one becomes directional,
            // reconcile visibility changes made while it did not affect GI.
            ulong flags = GetHideFlags(tracked);
            if (flags != tracked.hideFlags)
            {
                tracked.hideFlags = flags;
                m_MembershipChanged = true;
            }
        }

        void RemoveDirectional(TrackedLight tracked)
        {
            int index = tracked.directionalIndex;
            if (index < 0) return;
            int last = m_DirectionalLights.Count - 1;
            var moved = m_DirectionalLights[last];
            m_DirectionalLights[index] = moved;
            moved.directionalIndex = index;
            m_DirectionalLights.RemoveAt(last);
            tracked.directionalIndex = -1;
        }

        static ulong GetHideFlags(TrackedLight tracked) =>
            (uint)tracked.light.hideFlags | ((ulong)(uint)tracked.gameObject.hideFlags << 32);

        public void Dispose()
        {
            m_Dispatcher.Dispose();
            m_Lights = Array.Empty<Light>();
            m_KnownIds.Clear();
            m_KnownLights.Clear();
            m_DirectionalLights.Clear();
            m_ChangedObjects.Clear();
        }
    }
}
