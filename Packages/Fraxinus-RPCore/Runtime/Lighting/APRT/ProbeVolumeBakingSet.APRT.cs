namespace UnityEngine.Rendering
{
    internal enum APRTLightingModel { DiffuseTransfer = 0, APRTV = 1 }

    public sealed partial class ProbeVolumeBakingSet
    {
        /// <summary>Use baked APRT lighting scenarios instead of live scene lights and sky.</summary>
        [SerializeField] public bool aprtUseLightingScenarios;

        /// <summary>The selected lighting scenario for this Baking Set's APRT backend.</summary>
        public string aprtLightingScenario
        {
            get => GetLightingScenario(APRTProbeReferenceVolumeBackend.backendId);
            set
            {
                if (TryGetActiveAPRTVolume(out var volume)) volume.lightingScenario = value;
            }
        }

        /// <summary>The current APRT blend target, or null when no blend target is selected.</summary>
        public string aprtOtherScenario => GetOtherScenario(APRTProbeReferenceVolumeBackend.backendId);

        /// <summary>The blend weight of the current APRT target scenario, from zero to one.</summary>
        public float aprtScenarioBlendingFactor
        {
            get => GetScenarioBlendingFactor(APRTProbeReferenceVolumeBackend.backendId);
            set
            {
                if (TryGetActiveAPRTVolume(out var volume)) volume.scenarioBlendingFactor = value;
            }
        }

        /// <summary>Blend the active APRT scenario toward another baked scenario. Pass null to stop blending.</summary>
        public void BlendAPRTLightingScenario(string otherScenario, float blendingFactor)
        {
            if (TryGetActiveAPRTVolume(out var volume)) volume.BlendLightingScenario(otherScenario, blendingFactor);
        }

        bool TryGetActiveAPRTVolume(out APRTProbeReferenceVolume volume)
        {
            volume = APRTProbeReferenceVolume.instance.GetBakingSetRuntime(this);
            if (volume != null) return true;
            Debug.LogWarning("Activate a scene from this APRT Baking Set before changing its lighting scenario.", this);
            return false;
        }

        // APRT validates endpoints through its loaded atlas before reaching this
        // state update. Avoid repeating full transport/SH scans for each weight.
        internal void SetAPRTScenarioBlend(string other, float factor, APRTProbeReferenceVolume volume)
        {
            string backend = APRTProbeReferenceVolumeBackend.backendId;
            if (other == GetLightingScenario(backend) || string.IsNullOrEmpty(other)) other = null;
            if (other == null) factor = 0;
            bool changed = other != GetOtherScenario(backend);
            EnsureBackendScenarioState();
            m_BackendOtherScenarios[backend] = other;
            m_BackendScenarioBlendingFactors[backend] = Mathf.Clamp01(factor);
            volume.ScenarioBlendingChanged(changed);
        }
        // New bakes use one APRTV model; per-camera controls live in APRTOptions.
        [HideInInspector, SerializeField] internal APRTLightingModel aprtLightingModel = APRTLightingModel.APRTV;
        [HideInInspector, SerializeField, Range(0, 10)] internal float aprtIndirectIntensity = .56f;
        [HideInInspector, SerializeField, Range(1, 15)] internal float aprtIndirectContrast = 15;
        [HideInInspector, SerializeField, Range(0, 5)] internal float aprtSaturationBoost = 1.49f;
        [HideInInspector, SerializeField, Range(0, 1)] internal float aprtShadowGIVisibility = .009f;
        [HideInInspector, SerializeField, Range(.25f, 8)] internal float aprtSkyVisibilityPower = .32f;
        [HideInInspector, SerializeField, Range(0, 15)] internal float aprtFloorBoost = 2.5f;
        [HideInInspector, SerializeField, Range(1, 10)] internal float aprtFloorPower = 2;
        [HideInInspector, SerializeField, Range(0, 5)] internal float aprtReferenceSkyIntensity;
        [SerializeField, Range(0, 10)] internal float aprtBakeSaturationPriority = 10;
        [SerializeField, Range(0, 5)] internal float aprtBakeCenterWeight = 5;
        [SerializeField, Range(8, 128)] internal int aprtCaptureResolution = 64;
        [SerializeField, Min(1)] internal float aprtCaptureDistance = 100;
        // Baking-only controls. Static surfaces contribute one colored sky
        // reflection; runtime still evaluates a single per-probe transfer.
        [SerializeField] public bool aprtBakeSkyBounce = false;
        [SerializeField, Range(64, 1024)] public int aprtSkyBouncePrimarySamples = 128;
        [SerializeField, Range(16, 256)] public int aprtSkyBounceSkySamples = 64;
        // Serialized names are retained for existing Baking Sets.
        [HideInInspector, SerializeField, Range(32, 4096)] internal int aprtSampleCount = 512;
        // Legacy streaming setting, retained for asset compatibility and ignored by APRT.
        [HideInInspector, SerializeField, Range(64, 65536)] internal int aprtMaxResidentProbes = 65536;
        [HideInInspector, SerializeField, Min(0)] internal float aprtSkyIntensity = 1.0f;
        [HideInInspector, SerializeField, Range(0, 50)] internal float aprtGIIntensity = 1.0f;
        [HideInInspector, SerializeField, Range(0, 4)] internal float aprtBounceIntensity = 1.0f;
        [HideInInspector, SerializeField] internal bool aprtNormalizeReceiverWeights;
        [HideInInspector, SerializeField] internal bool aprtAddAmbientLighting = false;
        [HideInInspector, SerializeField] internal bool aprtStabilizeFeedback;
        [HideInInspector, SerializeField] internal bool aprtLeakReduction = false;
        [HideInInspector, SerializeField, Range(0, 1)] internal float aprtProbeInvalidityThreshold = .25f;
        [HideInInspector, SerializeField, Min(0)] internal float aprtVisibilityBias = .01f;
        [HideInInspector, SerializeField, Min(0)] internal float aprtNormalBias = 0.0f;
        [HideInInspector, SerializeField, Range(0.01f, 1)] internal float aprtTemporalBlend = 1.0f;
        // Retained for old assets; APRTV now always integrates the scene skybox.
        [HideInInspector, SerializeField] internal Cubemap aprtSkyTexture;

        internal APRTBakedData aprtBakedData
        {
            get
            {
                var data = GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
                if (data == null)
                {
                    data = new APRTBakedData();
                    AddOrReplaceBakedData(data);
                }
                return data;
            }
        }

        internal void CommitAPRTBakedData(APRTBakedData data)
        {
            if (data == null || !data.HasValidSharedData())
                throw new System.ArgumentException("Invalid APRT bake; the previous data has been retained.", nameof(data));
            var previous = GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
            if (previous != null && !ReferenceEquals(previous, data) && previous.lightingScenarios != null)
            {
                bool sameLayout = data.HasSameProbeLayout(previous);
                foreach (var scenario in previous.lightingScenarios)
                    if (scenario != null && scenario.IsValid() && data.FindLightingScenario(scenario.name) == null)
                    {
                        if (sameLayout && previous.HasBakedLightingScenario(scenario.name))
                            data.SetBakedLightingScenario(scenario);
                        else if ((scenario.bakedDirectionalSH == null || scenario.bakedDirectionalSH.Length == 0) &&
                            (scenario.streamableAssets == null || scenario.streamableAssets.Count == 0))
                            data.SetLightingScenario(scenario);
                    }
            }
            AddOrReplaceBakedData(data);
        }
    }
}
