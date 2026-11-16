using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace UnityEngine.Rendering
{
    // Standalone APV scheduling is intentionally separate from the native
    // Lightmapping callbacks. It drives the same APV post-processing and
    // serialization code one editor update at a time.
    internal sealed partial class APVProbeVolumeBaking
    {
        bool m_StandaloneBakeActive;
        // Preparation owns resources before PrepareBaking permits the session
        // to become active. Track it so exceptions also release those resources.
        bool m_StandalonePrepareActive;
        int m_StandaloneProgressTaskID = -1;

        bool StandalonePathActive => m_StandaloneBakeActive || m_StandalonePrepareActive;

        void PostProcessStandaloneLighting()
        {
            var irradiance = s_BakeData.lightingJob.irradiance;
            Span<float> coefficients = stackalloc float[27];
            for (int i = 0; i < irradiance.Length; ++i)
            {
                var sh = irradiance[i];
                for (int channel = 0; channel < 3; ++channel)
                    for (int c = 0; c < 9; ++c)
                        coefficients[channel * 9 + c] = sh[channel, c];
                var scales = StandaloneAPVDeringing.ComputeBandScales(coefficients);
                for (int channel = 0; channel < 3; ++channel)
                {
                    for (int c = 1; c < 4; ++c) sh[channel, c] *= scales.l1;
                    for (int c = 4; c < 9; ++c) sh[channel, c] *= scales.l2;
                }
                irradiance[i] = sh;
            }

            // Seam reconstruction addresses the placement grid, not the
            // displaced ray origins used by virtual offset. Rebuild the exact
            // original positions to avoid float round-off from undoing offsets.
            var gridPositions = new NativeArray<Vector3>(s_BakeData.probeCount, Allocator.Temp);
            try
            {
                foreach (var cell in m_BakingBatch.cells)
                    for (int i = 0; i < cell.probePositions.Length; ++i)
                        gridPositions[s_BakeData.positionRemap[cell.probeIndices[i]]] = cell.probePositions[i];
                FixSeams(s_BakeData.positionRemap, gridPositions, irradiance,
                    s_BakeData.lightingJob.validity, s_BakeData.layerMaskJob.renderingLayerMasks);
            }
            finally { gridPositions.Dispose(); }
        }

        bool ValidateStandaloneScene(LightingSettings lightingSettings)
        {
            var lod0Renderers = new HashSet<Renderer>();
            foreach (var lodGroup in UnityEngine.Object.FindObjectsOfType<LODGroup>(true))
            {
                var levels = lodGroup.GetLODs();
                if (levels.Length == 0)
                    continue;
                foreach (var renderer in levels[0].renderers)
                    if (renderer != null)
                        lod0Renderers.Add(renderer);
            }

            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || !renderer.gameObject.scene.isLoaded)
                    continue;

                var flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
                if ((flags & StaticEditorFlags.ContributeGI) == 0)
                    continue;
                if (renderer.isPartOfStaticBatch)
                {
                    Debug.LogError("Standalone APV preflight rejected the bake: static-batched contributing renderers are unsupported.");
                    return false;
                }

                var lodGroup = renderer.GetComponentInParent<LODGroup>();
                if (lodGroup != null && !lod0Renderers.Contains(renderer))
                    continue;

                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    Debug.LogError($"Standalone APV preflight rejected the bake: contributing renderer '{renderer.name}' has no MeshFilter mesh.");
                    return false;
                }

                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                {
                    Debug.LogError($"Standalone APV preflight rejected the bake: renderer '{renderer.name}' has no materials.");
                    return false;
                }
                for (int submesh = 0; submesh < filter.sharedMesh.subMeshCount; ++submesh)
                {
                    var material = materials[Mathf.Min(submesh, materials.Length - 1)];
                    if (!StandaloneAPVMaterialResources.TryValidate(material, out var reason))
                    {
                        Debug.LogError("Standalone APV preflight rejected the bake: " + reason);
                        return false;
                    }
                }
            }

            foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (!light.isActiveAndEnabled || !light.gameObject.scene.isLoaded || light.lightmapBakeType == LightmapBakeType.Realtime)
                    continue;
                if (light.type != LightType.Directional && light.type != LightType.Point && light.type != LightType.Spot)
                {
                    Debug.LogError($"Standalone APV preflight rejected the bake: light '{light.name}' uses unsupported type {light.type}.");
                    return false;
                }
                if (light.lightmapBakeType != LightmapBakeType.Mixed)
                    continue;
                if (lightingSettings.mixedBakeMode != MixedLightingMode.IndirectOnly)
                {
                    Debug.LogError("Standalone APV preflight rejected the bake: Mixed lights require IndirectOnly mode; shadowmask/subtractive direct semantics are not supported.");
                    return false;
                }
            }

            return true;
        }

        internal override bool BakeStandaloneAsync()
        {
#if !HAS_FRAXINUS_LIGHT_TRANSPORT
            Debug.LogError("Standalone APV baking requires the Fraxinus unified ray-tracing backend.");
            return false;
#endif
            // The Unity 2022 Builtin Sky manager may report Lightmapping.isRunning
            // while it initializes its environment cache. Standalone APV owns no
            // native bake callbacks, so this transient flag is not a dependency.
            if (StandalonePathActive)
                return false;

            var lightingSettings = ProbeVolumeBakingWorkflow.GetLightingSettings();
            if (lightingSettings == null)
            {
                Debug.LogError("Standalone APV baking requires Lighting Settings.");
                return false;
            }

            if (!ValidateStandaloneScene(lightingSettings))
                return false;

            m_StandalonePrepareActive = true;

            try
            {
                if (!PrepareBaking())
                {
                    // PrepareBaking may have allocated placement or baker
                    // resources before a validation failure. Run the common
                    // cleanup path so the provider cannot retain a half-ready
                    // session.
                    if (StandalonePathActive)
                        CleanBakeData();
                    return false;
                }

                // PrepareBaking() intentionally rejects an already-running
                // baker. Mark the session active only after placement and job
                // allocation have completed.
                volume.checksDuringBakeAction = CheckPVChanges;
                m_StandaloneBakeActive = true;
                m_StandalonePrepareActive = false;
                m_StandaloneProgressTaskID = Progress.Start("Standalone APV Bake");
                Progress.RegisterCancelCallback(m_StandaloneProgressTaskID, Cancel);
                EditorApplication.update += UpdateStandaloneBake;
                return true;
            }
            catch
            {
                if (StandalonePathActive)
                    CleanBakeData();
                throw;
            }
        }

        void UpdateStandaloneBake()
        {
            if (!m_StandaloneBakeActive)
            {
                EditorApplication.update -= UpdateStandaloneBake;
                RemoveStandaloneProgress();
                return;
            }

            float progress = 0.0f;
            bool done = false;
            try
            {
                BakeDelegate(ref progress, ref done);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                CleanBakeData();
                done = true;
            }

            // FinalizeBake/CleanBakeData marks the session inactive. Do not
            // report through a progress handle which cleanup may already have
            // released, since Unity logs Progress(-1) in that case.
            if (done || !m_StandaloneBakeActive)
            {
                EditorApplication.update -= UpdateStandaloneBake;
                RemoveStandaloneProgress();
                return;
            }

            if (m_StandaloneProgressTaskID != -1)
                Progress.Report(m_StandaloneProgressTaskID, Mathf.Clamp01(progress), s_BakeData.step.ToString());
        }

        void RemoveStandaloneProgress()
        {
            if (m_StandaloneProgressTaskID == -1)
                return;

            int task = m_StandaloneProgressTaskID;
            m_StandaloneProgressTaskID = -1;
            Progress.Remove(task);
        }

    }
}
