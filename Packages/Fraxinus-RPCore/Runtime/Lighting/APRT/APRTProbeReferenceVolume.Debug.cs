namespace UnityEngine.Rendering
{
    internal sealed partial class APRTProbeReferenceVolume
    {
        internal const string debugPanelName = "APRT Volumes";
        enum APRTDebugMode { Lighting, BakedTransferRGB, SkyVisibility, ProbeValidity, ProbeCoverage, GIOnly }
        APRTDebugMode m_APRTDebugMode;
        DebugUI.Panel m_APRTDebugPanel;
        DebugUI.Container m_APRTDebugWidgets;
        APRTBakedData m_DebugStatisticsData;
        float m_DebugStatisticsThreshold = -1;
        int m_DebugStatisticsFormat, m_DebugStatisticsProbeCount;
        int m_DebugStatisticsAtlasGeneration = -1;
        int m_DebugRejectedProbes;

        // The coordinator owns the panel; its focused runtime owns the bake,
        // relighting state and residency shown by the diagnostics below.
        APRTProbeReferenceVolume debugRuntime => focusedRuntime ?? this;
        APRTBakedData debugBakedData => debugRuntime.currentBakingSet?.GetBakedData(k_BackendId) as APRTBakedData;

        // Debug panel queries never capture an environment or read GPU buffers.
        // The next relighting camera refreshes this radiance cache when needed.
        internal float GetCachedSkyRadianceCoefficientMagnitude()
        {
            if (focusedRuntime is { } runtime) return runtime.GetCachedSkyRadianceCoefficientMagnitude();
            if (m_HasEffectiveSettings) return m_EffectiveSkyRadianceMagnitude;
            var coefficients = m_SkyEnvironment?.radianceCoefficients;
            if (coefficients == null || !m_SkyEnvironment.hasValidSource) return 0;
            return SkyRadianceCoefficientMagnitude(coefficients);
        }

        static float SkyRadianceCoefficientMagnitude(Vector4[] coefficients)
        {
            if (coefficients == null) return 0;
            float magnitude = 0;
            foreach (var coefficient in coefficients)
                for (int channel = 0; channel < 3; ++channel)
                {
                    float value = coefficient[channel];
                    if (!float.IsNaN(value) && !float.IsInfinity(value))
                        magnitude = Mathf.Max(magnitude, Mathf.Abs(value));
                }
            return magnitude;
        }

        APRTLightingModel debugSkyModel => debugBakedData?.formatVersion == APRTBakedData.k_CurrentVersion
            ? debugBakedData.lightingModel : currentBakingSet?.aprtLightingModel ?? APRTLightingModel.APRTV;
        APRTRelightSettings debugRelightSettings => focusedRuntime is { } runtime ? runtime.debugRelightSettings
            : m_HasEffectiveSettings ? EffectiveRelightSettings()
            : APRTRelightSettings.FromBakingSet(currentBakingSet, debugSkyModel);
        float debugSkyIntensity => currentBakingSet == null ? 0 : Intensity(debugRelightSettings.skyIntensity);
        bool debugUsesLightingScenarios => focusedRuntime is { } runtime ? runtime.debugUsesLightingScenarios
            : m_HasEffectiveSettings ? m_LastRelightScenario?.useLightingScenarios == true
            : usesLightingScenarios;
        bool debugUsesBakedLighting => focusedRuntime is { } runtime ? runtime.debugUsesBakedLighting
            : m_HasEffectiveSettings ? m_LastRelightScenario?.useBakedLighting == true
            : debugUsesLightingScenarios && HasBakedLightingMarkers(debugBakedData?.FindLightingScenario(lightingScenario));

        string DebugSkyStatus()
        {
            if (focusedRuntime is { } runtime) return runtime.DebugSkyStatus();
            if (currentBakingSet == null) return "No Baking Set";
            var data = debugBakedData;
            if (data == null || data.formatVersion != APRTBakedData.k_CurrentVersion || data.cells == null || data.cells.Count == 0)
                return "No usable bake";
            if (debugUsesBakedLighting)
            {
                if (Intensity(debugRelightSettings.giIntensity) <= 0) return "Sky disabled - GI Intensity is zero";
                if (!DataHasBeenLoaded() || !ReferenceEquals(data, m_RuntimeData) || m_LastRelightScenario == null)
                    return "Waiting for a camera to initialize baked sky irradiance";
                return GetCachedSkyRadianceCoefficientMagnitude() > 0
                    ? "Ready - baked scenario sky irradiance" : "No sky energy - baked scenario irradiance is zero";
            }
            if (debugSkyIntensity <= 0) return "Sky disabled - Sky Intensity is zero";
            if (!data.hasSkyIrradianceTransfer) return "Bake APRT again for directional sky transfer";
            if (debugUsesLightingScenarios)
            {
                if (Intensity(debugRelightSettings.giIntensity) <= 0) return "Sky disabled - GI Intensity is zero";
                if (!DataHasBeenLoaded() || !ReferenceEquals(data, m_RuntimeData) || m_LastRelightScenario == null)
                    return "Waiting for a camera to initialize captured sky radiance";
                if (GetCachedSkyRadianceCoefficientMagnitude() <= 0)
                    return "No sky energy - captured scenario radiance is zero";
                return "Ready - captured scenario sky irradiance transfer";
            }
            if ((m_HasEffectiveSettings ? m_EffectiveSceneSkybox : RenderSettings.skybox) == null)
                return "No sky source - assign a scene skybox";
            if (Intensity(debugRelightSettings.giIntensity) <= 0) return "Sky disabled - GI Intensity is zero";
            if (!DataHasBeenLoaded() || !ReferenceEquals(data, m_RuntimeData) ||
                (m_HasEffectiveSettings ? !m_EffectiveSkyCacheInitialized : m_SkyEnvironment == null))
                return "Waiting for a camera to initialize sky radiance";
            if (m_HasEffectiveSettings ? !m_EffectiveSkyHasValidSource : !m_SkyEnvironment.hasValidSource) return "Sky source unavailable";
            if (GetCachedSkyRadianceCoefficientMagnitude() <= 0) return "No sky energy - source radiance is zero";
            return "Ready - directional sky irradiance transfer";
        }

        string DebugSkySource()
        {
            if (focusedRuntime is { } runtime) return runtime.DebugSkySource();
            if (debugUsesLightingScenarios)
            {
                if (m_LastRelightScenario == null) return debugUsesBakedLighting
                    ? "Baked scenarios (waiting for a camera)" : "Captured scenarios (waiting for a camera)";
                string source = debugUsesBakedLighting ? "Baked" : "Captured";
                if (string.IsNullOrEmpty(m_LastRelightScenario.otherScenario))
                    return m_LastRelightScenario.activeScenario + " (" + source + " Lighting Scenario)";
                return System.FormattableString.Invariant($"{m_LastRelightScenario.activeScenario} -> {m_LastRelightScenario.otherScenario} ({source} Lighting Scenarios, blend {m_LastRelightScenario.factor:G4})");
            }
            var skybox = m_HasEffectiveSettings ? m_EffectiveSceneSkybox : RenderSettings.skybox;
            return skybox != null ? skybox.name + " (Scene Skybox)" : "None";
        }

        bool DebugScenarioUnavailable(APRTBakedData data, string name)
        {
            if (!currentBakingSet.m_LightingScenarios.Contains(name)) return true;
            var scenario = data.FindLightingScenario(name);
            if (scenario == null) return true;
            // Full validation belongs to the loader. A valid endpoint can be
            // nonresident while its pair is loading or the frame is pinned.
            return HasBakedLightingMarkers(scenario) && ReferenceEquals(data, m_RuntimeData) &&
                m_ValidatedRevision == data.lightingScenarioRevision && !m_ValidBakedScenarios.Contains(name);
        }

        string DebugStatus()
        {
            if (focusedRuntime is { } runtime) return runtime.DebugStatus();
            if (!m_Initialized || !m_Enabled) return "APRT disabled by render pipeline";
            var set = currentBakingSet;
            if (set == null) return "No Baking Set registered for the loaded scene";
            var data = debugBakedData;
            if (data == null || data.formatVersion != APRTBakedData.k_CurrentVersion || data.cells == null || data.cells.Count == 0)
                return "No usable bake - bake APRT for this Baking Set";
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45) return "Compute / shader model 4.5 unavailable";
            if (usesLightingScenarios &&
                (DebugScenarioUnavailable(data, lightingScenario) ||
                 (supportScenarioBlending &&
                  !string.IsNullOrEmpty(otherScenario) && DebugScenarioUnavailable(data, otherScenario))))
                return "Lighting Scenario unavailable - bake the Active scenario";
            if (!ValidIntensity(debugRelightSettings.giIntensity) || debugRelightSettings.giIntensity <= 0)
                return m_APRTDebugMode == APRTDebugMode.Lighting ? "GI Intensity is zero - standard lighting fallback" : "Debug display active; GI Intensity is zero";
            if (m_LoadError != null) return m_LoadError;
            if (!DataHasBeenLoaded() || !ReferenceEquals(data, m_RuntimeData)) return "Waiting for a camera to initialize the bake";
            if (m_APRTDebugMode != APRTDebugMode.Lighting) return "Debug override active - this is not the final lit image";
            return "Ready";
        }

        void SetDebugMode(int value)
        {
            m_APRTDebugMode = (APRTDebugMode)Mathf.Clamp(value, 0, 5);
            foreach (var runtime in m_LoadedSetRuntimes) runtime.m_APRTDebugMode = m_APRTDebugMode;
#if UNITY_EDITOR
            UnityEditor.SceneView.RepaintAll();
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void UpdateDebugValidityStatistics()
        {
            var data = debugBakedData;
            float threshold = 1 - Mathf.Clamp01(currentBakingSet != null ? debugRelightSettings.probeInvalidityThreshold : .25f);
            if (ReferenceEquals(data, m_DebugStatisticsData) && threshold == m_DebugStatisticsThreshold &&
                m_DebugStatisticsFormat == (data?.formatVersion ?? 0) && m_DebugStatisticsProbeCount == (data?.sampleCount ?? 0) &&
                m_DebugStatisticsAtlasGeneration == m_ScenarioAtlasGeneration) return;
            m_DebugStatisticsData = data;
            m_DebugStatisticsThreshold = threshold;
            m_DebugStatisticsFormat = data?.formatVersion ?? 0;
            m_DebugStatisticsProbeCount = data?.sampleCount ?? 0;
            m_DebugStatisticsAtlasGeneration = m_ScenarioAtlasGeneration;
            m_DebugRejectedProbes = 0;
            if (residentCellPayloads != null)
                foreach (var cell in residentCellPayloads)
                    if (cell?.probeValidity != null)
                        foreach (float validity in cell.probeValidity)
                        {
                            if (validity <= 0 || validity < threshold) ++m_DebugRejectedProbes;
                        }
        }

        void InitializeDebug() => UpdateDebugRegistration();

        void UpdateDebugRegistration()
        {
            if (!m_Initialized || !m_Enabled)
            {
                CleanupDebug();
                return;
            }
            if (m_RuntimeOwner != null || m_APRTDebugWidgets != null) return;
            m_APRTDebugWidgets = new DebugUI.Container { displayName = "APRTV" };
            m_APRTDebugWidgets.children.Add(new DebugUI.Value
            {
                displayName = "Status", getter = DebugStatus
            });
            m_APRTDebugWidgets.children.Add(new DebugUI.Value { displayName = "Baking Set", getter = () => currentBakingSet != null ? currentBakingSet.name : "None" });
            m_APRTDebugWidgets.children.Add(new DebugUI.Value { displayName = "Loaded Baking Sets", getter = () => coordinatesBakingSets ? m_LoadedSetRuntimes.Count : currentBakingSet != null ? 1 : 0 });
            m_APRTDebugWidgets.children.Add(new DebugUI.Value { displayName = "Resident probes", getter = () => debugRuntime.probeCount });
            m_APRTDebugWidgets.children.Add(new DebugUI.Value { displayName = "Total GPU memory (MiB)", getter = () => GetVideoMemoryCost() / (1024f * 1024f), formatString = "{0:F2}" });
            m_APRTDebugWidgets.children.Add(new DebugUI.Value { displayName = "Loaded cells", getter = () => debugRuntime.residentCellCount });
            m_APRTDebugWidgets.children.Add(new DebugUI.Value { displayName = "Loaded scenarios", getter = () => debugRuntime.residentScenarioNames.Count });
            m_APRTDebugWidgets.children.Add(CreateProbeDebugWidgets());
            m_APRTDebugWidgets.children.Add(new DebugUI.EnumField
            {
                displayName = "Display view",
                tooltip = "Lighting: normal shading. Transfer RGB: baked transport. Sky Visibility: legacy visibility, black = blocked / white = open. Probe Validity: red = invalid / green = valid. Probe Coverage: green = covered / magenta = missing. GI Only: indirect response before material albedo.",
                getter = () => (int)m_APRTDebugMode,
                setter = SetDebugMode,
                autoEnum = typeof(APRTDebugMode),
                getIndex = () => (int)m_APRTDebugMode,
                setIndex = SetDebugMode
            });
            m_APRTDebugWidgets.children.Add(new DebugUI.Value
            {
                displayName = "Bake compatibility",
                getter = () => "Legacy bake - rebake to use APRTV",
                isHiddenCallback = () => currentBakingSet == null || debugBakedData == null || debugBakedData.lightingModel == APRTLightingModel.APRTV
            });
            var sky = new DebugUI.Foldout { displayName = "Sky lighting", opened = false };
            sky.children.Add(new DebugUI.Value { displayName = "Status", getter = DebugSkyStatus });
            sky.children.Add(new DebugUI.Value { displayName = "Source", getter = DebugSkySource });
            m_APRTDebugWidgets.children.Add(sky);
            var baked = new DebugUI.Foldout { displayName = "Baked data", opened = false };
            baked.children.Add(new DebugUI.Value { displayName = "Baked probes", getter = () => debugBakedData?.sampleCount ?? 0 });
            baked.children.Add(new DebugUI.Value { displayName = "Cells", getter = () => debugBakedData?.cells?.Count ?? 0 });
            baked.children.Add(new DebugUI.Value { displayName = "Probe spacing", getter = () => debugBakedData?.regularGridSpacing ?? 0, isHiddenCallback = () => debugBakedData?.regularGrid != true });
            m_APRTDebugWidgets.children.Add(baked);
            var validity = new DebugUI.Foldout { displayName = "Probe validity", opened = false };
            validity.children.Add(new DebugUI.Value
            {
                displayName = "Acceptance threshold",
                getter = () => 1 - Mathf.Clamp01(currentBakingSet != null ? debugRelightSettings.probeInvalidityThreshold : .25f),
                tooltip = "Threshold used by the raw validity display. Normal GI rejection also depends on the Leak Reduction setting."
            });
            validity.children.Add(new DebugUI.Value
            {
                displayName = "Resident below threshold probes",
                getter = () => { var runtime = debugRuntime; runtime.UpdateDebugValidityStatistics(); return runtime.m_DebugRejectedProbes; },
                tooltip = "Raw validity count among resident decoded probes, including zero-validity probes. Available even when Leak Reduction is off. Nonresident cells are not read for this diagnostic."
            });
            m_APRTDebugWidgets.children.Add(validity);
            m_APRTDebugPanel = DebugManager.instance.GetPanel(debugPanelName, createIfNull: true);
            m_APRTDebugPanel.children.Add(m_APRTDebugWidgets);
        }

        void CleanupDebug()
        {
            CleanupProbeDebugResources();
            if (m_APRTDebugWidgets == null) return;
            m_APRTDebugPanel.children.Remove(m_APRTDebugWidgets);
            if (m_APRTDebugPanel.children.Count == 0) DebugManager.instance.RemovePanel(m_APRTDebugPanel);
            m_APRTDebugWidgets = null;
            m_APRTDebugPanel = null;
        }

        // URP's APRTProbeDebugPass draws spheres after the relight pass, with
        // explicit RenderGraph buffer reads. Emitting them here before culling
        // would hide those dependencies and could display another camera's SH.
        public override void RenderDebug(Camera camera, ProbeVolumesOptions options, Texture exposureTexture) { }
        public override bool IsProbeSamplingDebugEnabled() => false;
        public override bool GetProbeSamplingDebugResources(Camera camera, out GraphicsBuffer resultBuffer, out Vector2 coords)
        {
            resultBuffer = null;
            coords = default;
            return false;
        }
    }
}
