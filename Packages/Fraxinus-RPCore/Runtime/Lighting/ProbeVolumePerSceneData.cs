using System;
using System.Collections.Generic;
using UnityEngine.Serialization;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering
{
    /// <summary>
    /// A component that stores baked probe volume state and data references. Normally hidden in the hierarchy.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("")] // Hide.
    public class ProbeVolumePerSceneData : MonoBehaviour, ISerializationCallbackReceiver
    {
        /// <summary>The baking set this scene is part of.</summary>
        public ProbeVolumeBakingSet bakingSet => serializedBakingSet;

        // Warning: this is the baking set this scene was part of during last bake
        // It shouldn't be used while baking as the scene may have been moved since then
        [SerializeField, FormerlySerializedAs("bakingSet")] internal ProbeVolumeBakingSet serializedBakingSet;
        [SerializeField] internal string sceneGUID = "";

        // Light.bakingOutput is not serialized in Unity 2022 scenes. Keep both
        // outputs here so standalone APV can restore its state after scene reload.
        [Serializable]
        struct SerializedLightBakingOutput
        {
            public bool isBaked;
            public LightmapBakeType lightmapBakeType;
            public MixedLightingMode mixedLightingMode;
            public int probeOcclusionLightIndex, occlusionMaskChannel;

            public SerializedLightBakingOutput(LightBakingOutput output)
            {
                isBaked = output.isBaked;
                lightmapBakeType = output.lightmapBakeType;
                mixedLightingMode = output.mixedLightingMode;
                probeOcclusionLightIndex = output.probeOcclusionLightIndex;
                occlusionMaskChannel = output.occlusionMaskChannel;
            }

            public LightBakingOutput ToOutput() => new LightBakingOutput
            {
                isBaked = isBaked, lightmapBakeType = lightmapBakeType,
                mixedLightingMode = mixedLightingMode,
                probeOcclusionLightIndex = probeOcclusionLightIndex, occlusionMaskChannel = occlusionMaskChannel
            };

            public bool Matches(LightBakingOutput output) => isBaked == output.isBaked
                && lightmapBakeType == output.lightmapBakeType && mixedLightingMode == output.mixedLightingMode
                && probeOcclusionLightIndex == output.probeOcclusionLightIndex && occlusionMaskChannel == output.occlusionMaskChannel;
        }

        [Serializable]
        struct StandaloneLightBakingState
        {
            public Light light;
            public SerializedLightBakingOutput previous, applied;

            [NonSerialized] bool m_Initialized, m_Applied, m_Relinquished;
            [NonSerialized] SerializedLightBakingOutput m_RuntimePrevious;

            public void Update(bool enableAPV)
            {
                if (light == null)
                    return;

                var current = light.bakingOutput;
                if (!m_Initialized)
                {
                    m_Initialized = true;
                    if (applied.Matches(current))
                    {
                        m_Applied = true;
                        m_RuntimePrevious = previous;
                    }
                    else if (previous.Matches(current) || new SerializedLightBakingOutput(default).Matches(current))
                    {
                        // Scene reload can reset the native property. Preserve
                        // that actual baseline instead of resurrecting old native
                        // lightmap output when APV later releases this light.
                        m_RuntimePrevious = new SerializedLightBakingOutput(current);
                    }
                    else
                        m_Relinquished = true;
                }

                if (m_Relinquished)
                    return;

                // Once the user or native lightmapper changes our expected
                // output, abandon ownership even if a later value matches again.
                if (!(m_Applied ? applied : m_RuntimePrevious).Matches(current))
                {
                    m_Relinquished = true;
                    return;
                }

                // The authoring bake type is editor-only in Unity 2022. Players
                // restore the serialized applied output; the ownership checks
                // above still relinquish it when runtime code changes the output.
#if UNITY_EDITOR
                bool sameBakeType = light.lightmapBakeType == applied.lightmapBakeType;
#else
                bool sameBakeType = true;
#endif
                bool apply = enableAPV && sameBakeType;
                if (apply != m_Applied)
                    light.bakingOutput = apply ? applied.ToOutput() : m_RuntimePrevious.ToOutput();
                m_Applied = apply;
                if (!sameBakeType)
                    m_Relinquished = true;
            }
        }

        [SerializeField] List<StandaloneLightBakingState> m_StandaloneLightBakingStates = new List<StandaloneLightBakingState>();

#if UNITY_EDITOR
        internal void SetStandaloneLightBakingOutputs(IReadOnlyList<Light> lights)
        {
            RestoreStandaloneLightBakingOutputs();
            m_StandaloneLightBakingStates.Clear();
            if (lights != null)
            {
                var recorded = new HashSet<Light>();
                foreach (var light in lights)
                {
                    if (light == null || light.gameObject.scene != gameObject.scene
                        || light.lightmapBakeType == LightmapBakeType.Realtime || !recorded.Add(light))
                        continue;
                    var applied = new LightBakingOutput
                    {
                        isBaked = true, lightmapBakeType = light.lightmapBakeType,
                        mixedLightingMode = MixedLightingMode.IndirectOnly,
                        probeOcclusionLightIndex = -1, occlusionMaskChannel = -1
                    };
                    m_StandaloneLightBakingStates.Add(new StandaloneLightBakingState
                    {
                        light = light, previous = new SerializedLightBakingOutput(light.bakingOutput),
                        applied = new SerializedLightBakingOutput(applied)
                    });
                }
            }
            UpdateStandaloneLightBakingOutputs();
            EditorUtility.SetDirty(this);
        }
#endif

        void RestoreStandaloneLightBakingOutputs()
        {
            UpdateStandaloneLightBakingOutputs(false);
        }

        void UpdateStandaloneLightBakingOutputs()
        {
            var context = ProbeReferenceVolumeProvider.context;
            bool enableAPV = isActiveAndEnabled && context?.backend.id == APVProbeReferenceVolumeBackend.backendId &&
                serializedBakingSet != null &&
                (serializedBakingSet.GetBakedData(APVProbeReferenceVolumeBackend.backendId)?.HasBakedData(null, false) ?? false);
            UpdateStandaloneLightBakingOutputs(enableAPV);
        }

        void UpdateStandaloneLightBakingOutputs(bool enableAPV)
        {
            for (int i = 0; i < m_StandaloneLightBakingStates.Count; ++i)
            {
                var state = m_StandaloneLightBakingStates[i];
                state.Update(enableAPV);
                m_StandaloneLightBakingStates[i] = state;
            }
        }

        // All code bellow is only kept in order to be able to cleanup obsolete data.
        [Serializable]
        internal struct ObsoletePerScenarioData
        {
            public int sceneHash;
            public TextAsset cellDataAsset; // Contains L0 L1 SH data
            public TextAsset cellOptionalDataAsset; // Contains L2 SH data
        }


#if UNITY_EDITOR
        void DeleteAsset(Object asset)
        {
            if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long instanceID))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }
#endif

        internal void Clear()
        {
            RestoreStandaloneLightBakingOutputs();
            m_StandaloneLightBakingStates.Clear();
            QueueSceneRemoval();
            serializedBakingSet = null;

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        internal void QueueSceneLoading(string backendId = null)
        {
            if (serializedBakingSet == null)
                return;

            ProbeReferenceVolumeProvider.ForEachBackend(backend =>
            {
                if (backendId != null && backend.id != backendId)
                    return;
                #if UNITY_EDITOR
                // The legacy scene flag describes APV placement only.
                var bakedData = serializedBakingSet.GetSceneBakeData(sceneGUID, addIfMissing: false);
                if (bakedData != null && bakedData.hasProbeVolume == false && backend.id == APVProbeReferenceVolumeBackend.backendId)
                    return;
                #endif

                if (backend.id != APVProbeReferenceVolumeBackend.backendId)
                {
                    if (serializedBakingSet.GetBakedData(backend.id) == null ||
                        serializedBakingSet.GetSceneCellIndexList(backend.id, sceneGUID) == null)
                        return;
                }
                backend.volume?.AddPendingSceneLoading(sceneGUID, serializedBakingSet);
            });
        }

        internal void Clear(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
            {
                Clear();
                return;
            }

            if (backendId == APVProbeReferenceVolumeBackend.backendId)
            {
                RestoreStandaloneLightBakingOutputs();
                m_StandaloneLightBakingStates.Clear();
            }
            if (serializedBakingSet != null && ProbeReferenceVolumeProvider.TryGetBackend(backendId, out var backend))
                backend.volume?.AddPendingSceneRemoval(sceneGUID);

            // The scene-to-set reference is shared. The other backend may still
            // need it after this backend's payload has been removed.
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        internal void QueueSceneRemoval(string backendId = null)
        {
            if (serializedBakingSet != null)
            {
                ProbeReferenceVolumeProvider.ForEachBackend(backend =>
                {
                    if (backendId != null && backend.id != backendId)
                        return;
                    backend.volume?.AddPendingSceneRemoval(sceneGUID);
                });
            }
        }

        void OnEnable()
        {
            #if UNITY_EDITOR
            // In the editor, always refresh the GUID as it may become out of date is scene is duplicated or other weird things
            // This field is serialized, so it will be available in standalones, where it can't change anymore.
            // Only change the GUID if the new one is valid.
            var newGUID = gameObject.scene.GetGUID();
            if (newGUID != sceneGUID && new GUID(newGUID) != default)
            {
                sceneGUID = newGUID;
                EditorUtility.SetDirty(this);
            }
            #endif

            ProbeReferenceVolumeProvider.runtimeBackendChanged += UpdateStandaloneLightBakingOutputs;
            UpdateStandaloneLightBakingOutputs();
            ProbeReferenceVolumeProvider.ForEachBackend(backend =>
            {
                backend.volume?.RegisterPerSceneData(this);
            });
        }

        void OnDisable()
        {
            ProbeReferenceVolumeProvider.runtimeBackendChanged -= UpdateStandaloneLightBakingOutputs;
            RestoreStandaloneLightBakingOutputs();
            QueueSceneRemoval();
            ProbeReferenceVolumeProvider.ForEachBackend(backend =>
            {
                backend.volume?.UnregisterPerSceneData(this);
            });
        }

        private void OnValidate()
        {

        }

        internal void Initialize(string backendId = null)
        {
            ProbeReferenceVolumeProvider.ForEachBackend(backend =>
            {
                if (backendId != null && backend.id != backendId)
                    return;
                backend.volume?.RegisterBakingSet(this);
            });

            QueueSceneRemoval(backendId);
            QueueSceneLoading(backendId);
        }

        internal bool ResolveCellData()
        {
            if (serializedBakingSet != null)
                return serializedBakingSet.ResolveCellData(serializedBakingSet.GetSceneCellIndexList(sceneGUID));

            return false;
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            // Unity 6 moved the per-scenario dictionaries to ProbeVolumeBakingSet.
            // Keeping the callback contract lets Unity 2022 components deserialize safely.
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        /// <summary>
        /// Unity 2022 build hook for stripping probe support data. Unity 6 owns the data on the
        /// baking set and strips it centrally in ProbeVolumeBuildProcessor, so no per-scene
        /// mutation is required here.
        /// </summary>
        public void StripSupportData()
        {
        }
    }
}
