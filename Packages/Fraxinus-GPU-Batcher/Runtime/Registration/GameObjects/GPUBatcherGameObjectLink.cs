// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    // notifies the owning GPUBatcherGameObjectGroup on destroy and tracks InstanceLinks
    [AddComponentMenu("Fraxinus GPU Batcher/GPU Batcher GameObject Link")]
    [ExecuteAlways, DisallowMultipleComponent]
    public class GPUBatcherGameObjectLink : MonoBehaviour
    {
#if UNITY_6000_3_OR_NEWER
        static readonly Dictionary<EntityId, InstanceLink> s_instanceMap = new();
        EntityId InstanceMapKey => GetEntityId();
#else
        static readonly Dictionary<int, InstanceLink> s_instanceMap = new();
        int InstanceMapKey => GetInstanceID();
#endif

        [SerializeField, HideInInspector] GPUBatcherGameObjectGroup _group;
        InstanceLink _instanceLink = InstanceLink.invalid;
        [SerializeField, HideInInspector] int _globalProtoIndex = -1;

        public GPUBatcherGameObjectGroup group
        {
            get => _group;
            set => _group = value;
        }

        public InstanceLink instanceLink
        {
            get => _instanceLink;
            internal set
            {
                if (_instanceLink.isValid)
                    s_instanceMap.Remove(InstanceMapKey);

                _instanceLink = value;

                if (_instanceLink.isValid)
                    s_instanceMap[InstanceMapKey] = _instanceLink;
            }
        }

        public int globalProtoIndex
        {
            get => _globalProtoIndex;
            internal set => _globalProtoIndex = value;
        }

        public bool shouldRender => gameObject.activeInHierarchy && enabled;

        void OnEnable()
        {
            bool isClone = false;

            // detect clones from Instantiate (runtime - _instanceLink is memory-copied)
            if (_instanceLink.isValid && !s_instanceMap.ContainsKey(InstanceMapKey))
            {
                _instanceLink = InstanceLink.invalid;
                isClone = _group != null && _globalProtoIndex >= 0;
            }
            // detect clones from editor duplication (_instanceLink not serialized, _globalProtoIndex is)
            else if (!_instanceLink.isValid && _group != null && _globalProtoIndex >= 0)
            {
                isClone = true;
            }

            if (isClone && _group != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    _group.NotifyCloneCreated(this);
                    return;
                }
#endif
                _group.RegisterClonedInstance(this);
            }

            if (_instanceLink.isValid && _group != null && _group.isRegistered)
            {
                _group.registration.SetEnabledUnsafe(_instanceLink, true);

                // sync position - transform changes while disabled aren't tracked
                var t = transform;
                _group.registration.StageMove(_instanceLink,
                    (Unity.Mathematics.float3)t.position,
                    (Unity.Mathematics.quaternion)t.rotation,
                    (Unity.Mathematics.float3)t.lossyScale);
            }
        }

        void OnDisable()
        {
            if (!gameObject.scene.isLoaded) return;
            if (_instanceLink.isValid && _group != null && _group.isRegistered)
                _group.registration.SetEnabledUnsafe(_instanceLink, false);
        }

        // native transform change tracking via ObjectDispatcher
        public static void TryProcessTransformChanges(ChangeTrackerTransformData changes, GPUBatcherRenderer renderer)
        {
            for (int i = 0; i < changes.length; i++)
            {
                var objectId = changes.transformedIDs[i];
                if (!s_instanceMap.TryGetValue(objectId, out InstanceLink link))
                    continue;

                var m = changes.localToWorldMatrices[i];
                var pos = new Unity.Mathematics.float3(m.m03, m.m13, m.m23);
                var rot = new Unity.Mathematics.quaternion(m.rotation.x, m.rotation.y, m.rotation.z, m.rotation.w);
                var scale = (Unity.Mathematics.float3)m.lossyScale;
                renderer.MoveInstance(link.globalBRGSlotIndex, pos, rot, scale);
            }
        }

        void OnDestroy()
        {
            if (_instanceLink.isValid)
            {
                s_instanceMap.Remove(InstanceMapKey);

                if (gameObject.scene.isLoaded && _group != null && _group.isRegistered)
                    _group.registration.RemoveUnsafe(_instanceLink);
            }

            if (!gameObject.scene.isLoaded) return;
            if (_group != null && _group.isRegistered)
                _group.NotifyInstanceDestroyed(this);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ClearStaticState()
        {
            s_instanceMap.Clear();
        }

#if UNITY_EDITOR
        // native renderer change tracking via ObjectDispatcher - notifies group on mesh/material change
        public static void TryProcessRendererChanges(ChangeTrackerTypeData changes)
        {
            HashSet<GPUBatcherGameObjectGroup> dirtyGroups = null;
            for (int i = 0; i < changes.length; i++)
            {
                var mr = changes.changed[i] as MeshRenderer;
                if (mr == null) continue;

                var link = mr.GetComponent<GPUBatcherGameObjectLink>();
                if (link == null || link._group == null || !link._group.isRegistered) continue;

                dirtyGroups ??= new HashSet<GPUBatcherGameObjectGroup>();
                dirtyGroups.Add(link._group);
            }
            if (dirtyGroups != null)
                foreach (var group in dirtyGroups)
                    group.NotifySourceChanged();
        }
#endif
    }
}

