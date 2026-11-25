using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering
{
    internal partial class APVProbeReferenceVolume : TProbeReferenceVolume<
        APVProbeReferenceVolume,
        APVCellData,
        APVCellData.PerScenarioData, 
        APVCell,
        APVProbeBrickPool,
        APVProbeBrickBlendingPool,
        APVDataLocation>
    {

        const int k_probesPerBatch = 511;

        Mesh m_DebugMesh;
        Mesh debugMesh
        {
            get
            {
                if (m_DebugMesh == null)
                {
                    m_DebugMesh = DebugShapes.instance.BuildCustomSphereMesh(0.5f, 9, 8); // (longSubdiv + 1) * latSubdiv + 2 = 82
                    m_DebugMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 9999999.9f); // dirty way of disabling culling (objects spawned at (0.0, 0.0, 0.0) but vertices moved in vertex shader)
                }
                return m_DebugMesh;
            }
        }

        Material m_DebugMaterial;

        // Sample position debug
        Mesh m_DebugProbeSamplingMesh; // mesh with 8 quads, 1 arrow and 2 locators
        Material m_ProbeSamplingDebugMaterial; // Used to draw probe sampling information (quad with weight, arrow, locator)
        Material m_ProbeSamplingDebugMaterial02; // Used to draw probe sampling information (shaded probes)

        Texture m_DisplayNumbersTexture;

        Mesh m_DebugOffsetMesh;
        Material m_DebugOffsetMaterial;
        Plane[] m_DebugFrustumPlanes = new Plane[6];

        protected override bool TryCreateDebugRenderData()
        {
            if (!RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<ProbeVolumeDebugResources>(out var debugResources))
                return false;

#if !UNITY_EDITOR // On non editor builds, we need to check if the standalone build contains debug shaders
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<ShaderStrippingSetting>(out var shaderStrippingSetting) && shaderStrippingSetting.stripRuntimeDebugShaders)
                return false;
#endif
            m_DebugMaterial = CoreUtils.CreateEngineMaterial(debugResources.probeVolumeDebugShader);
            m_DebugMaterial.enableInstancing = true;

            // Probe Sampling Debug Mesh : useful to show additional information concerning probe sampling for a specific fragment
            // - Arrow : Debug fragment position and normal
            // - Locator : Debug sampling position
            // - 8 Quads : Debug probes weights
            m_DebugProbeSamplingMesh = debugResources.probeSamplingDebugMesh;
            m_DebugProbeSamplingMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 9999999.9f); // dirty way of disabling culling (objects spawned at (0.0, 0.0, 0.0) but vertices moved in vertex shader)
            m_ProbeSamplingDebugMaterial = CoreUtils.CreateEngineMaterial(debugResources.probeVolumeSamplingDebugShader);
            m_ProbeSamplingDebugMaterial02 = CoreUtils.CreateEngineMaterial(debugResources.probeVolumeDebugShader);
            m_ProbeSamplingDebugMaterial02.enableInstancing = true;

            probeSamplingDebugData.positionNormalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Vector4)));

            m_DisplayNumbersTexture = debugResources.numbersDisplayTex;

            m_DebugOffsetMesh = Resources.GetBuiltinResource<Mesh>("pyramid.fbx");
            m_DebugOffsetMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 9999999.9f); // dirty way of disabling culling (objects spawned at (0.0, 0.0, 0.0) but vertices moved in vertex shader)
            m_DebugOffsetMaterial = CoreUtils.CreateEngineMaterial(debugResources.probeVolumeOffsetDebugShader);
            m_DebugOffsetMaterial.enableInstancing = true;
            m_DebugFragmentationMaterial = CoreUtils.CreateEngineMaterial(debugResources.probeVolumeFragmentationDebugShader);

            return true;
        }

        protected override void CleanupDebugRenderData()
        {
            CoreUtils.Destroy(m_DebugMaterial);
            CoreUtils.Destroy(m_ProbeSamplingDebugMaterial);
            CoreUtils.Destroy(m_ProbeSamplingDebugMaterial02);
            CoreUtils.Destroy(m_DebugOffsetMaterial);
            CoreUtils.SafeRelease(probeSamplingDebugData?.positionNormalBuffer);
        }

        public override string debugPanelName => "APV Probe Debug Panel";


        protected override void RegisterDebug(ref List<DebugUI.Widget> widgetList)
        {
            base.RegisterDebug(ref widgetList);

            var adjustmentContainer = new DebugUI.Container()
            {
                displayName = "Probe Adjustment Volumes"
            };
            adjustmentContainer.children.Add(new DebugUI.BoolField
            {
                displayName = "Auto Display Probes",
                tooltip = "When enabled and a Probe Adjustment Volumes is selected, automatically display the probes.",
                getter = () => probeVolumeDebug.autoDrawProbes,
                setter = value => probeVolumeDebug.autoDrawProbes = value,
                onValueChanged = RefreshDebug
            });
            adjustmentContainer.children.Add(new DebugUI.BoolField
            {
                displayName = "Isolate Affected",
                tooltip = "When enabled, only displayed probes in the influence of the currently selected Probe Adjustment Volumes.",
                getter = () => probeVolumeDebug.isolationProbeDebug,
                setter = value => probeVolumeDebug.isolationProbeDebug = value,
                onValueChanged = RefreshDebug
            });
            widgetList.Add(adjustmentContainer);

            const float kProbeSizeMin = 0.05f, kProbeSizeMax = 10.0f;
            const float kOffsetSizeMin = 0.001f, kOffsetSizeMax = 0.1f;

            var probeContainer = new DebugUI.Container()
            {
                displayName = "Probe Visualization"
            };

            probeContainer.children.Add(new DebugUI.BoolField { displayName = "Display Probes", tooltip = "Render the debug view showing probe positions. Use the shading mode to determine which type of lighting data to visualize.", getter = () => probeVolumeDebug.drawProbes, setter = value => probeVolumeDebug.drawProbes = value, onValueChanged = RefreshDebug });
            {
                var probeContainerChildren = new DebugUI.Container()
                {
                    isHiddenCallback = () => !probeVolumeDebug.drawProbes
                };

                probeContainerChildren.children.Add(new DebugUI.EnumField
                {
                    displayName = "Probe Shading Mode",
                    tooltip = "Choose which lighting data to show in the probe debug visualization.",
                    getter = () => (int)probeVolumeDebug.probeShading,
                    setter = value => probeVolumeDebug.probeShading = (DebugProbeShadingMode)value,
                    autoEnum = typeof(DebugProbeShadingMode),
                    getIndex = () => (int)probeVolumeDebug.probeShading,
                    setIndex = value => probeVolumeDebug.probeShading = (DebugProbeShadingMode)value,
                });
                probeContainerChildren.children.Add(new DebugUI.FloatField { displayName = "Debug Size", tooltip = "The size of probes shown in the debug view.", getter = () => probeVolumeDebug.probeSize, setter = value => probeVolumeDebug.probeSize = value, min = () => kProbeSizeMin, max = () => kProbeSizeMax });

                var exposureCompensation = new DebugUI.FloatField
                {
                    displayName = "Exposure Compensation",
                    tooltip = "Modify the brightness of probe visualizations. Decrease this number to make very bright probes more visible.",
                    getter = () => probeVolumeDebug.exposureCompensation,
                    setter = value => probeVolumeDebug.exposureCompensation = value,
                    isHiddenCallback = () =>
                    {
                        return probeVolumeDebug.probeShading switch
                        {
                            DebugProbeShadingMode.SH => false,
                            DebugProbeShadingMode.SHL0 => false,
                            DebugProbeShadingMode.SHL0L1 => false,
                            DebugProbeShadingMode.SkyOcclusionSH => false,
                            DebugProbeShadingMode.SkyDirection => false,
                            DebugProbeShadingMode.ProbeOcclusion => false,
                            _ => true
                        };
                    }
                };
                probeContainerChildren.children.Add(exposureCompensation);

                probeContainerChildren.children.Add(new DebugUI.IntField
                {
                    displayName = "Max Subdivisions Displayed",
                    tooltip = "The highest (most dense) probe subdivision level displayed in the debug view.",
                    getter = () => probeVolumeDebug.maxSubdivToVisualize,
                    setter = (v) => probeVolumeDebug.maxSubdivToVisualize = Mathf.Max(0, Mathf.Min(v, GetMaxSubdivision() - 1)),
                    min = () => 0,
                    max = () => Mathf.Max(0, GetMaxSubdivision() - 1),
                });

                probeContainerChildren.children.Add(new DebugUI.IntField
                {
                    displayName = "Min Subdivisions Displayed",
                    tooltip = "The lowest (least dense) probe subdivision level displayed in the debug view.",
                    getter = () => probeVolumeDebug.minSubdivToVisualize,
                    setter = (v) => probeVolumeDebug.minSubdivToVisualize = Mathf.Max(v, 0),
                    min = () => 0,
                    max = () => Mathf.Max(0, GetMaxSubdivision() - 1),
                });

                probeContainer.children.Add(probeContainerChildren);
            }

            probeContainer.children.Add(new DebugUI.BoolField
            {
                displayName = "Debug Probe Sampling",
                tooltip = "Render the debug view displaying how probes are sampled for a selected pixel. Use the viewport overlay 'SelectPixel' button or Ctrl+Click on the viewport to select the debugged pixel",
                getter = () => probeVolumeDebug.drawProbeSamplingDebug,
                setter = value =>
                {
                    probeVolumeDebug.drawProbeSamplingDebug = value;
                    probeSamplingDebugData.update = ProbeSamplingDebugUpdate.Once;
                    probeSamplingDebugData.forceScreenCenterCoordinates = true;
                },
            });

            var drawProbeSamplingDebugChildren = new DebugUI.Container()
            {
                isHiddenCallback = () => !probeVolumeDebug.drawProbeSamplingDebug
            };

            drawProbeSamplingDebugChildren.children.Add(new DebugUI.FloatField { displayName = "Debug Size", tooltip = "The size of gizmos shown in the debug view.", getter = () => probeVolumeDebug.probeSamplingDebugSize, setter = value => probeVolumeDebug.probeSamplingDebugSize = value, min = () => kProbeSizeMin, max = () => kProbeSizeMax });
            drawProbeSamplingDebugChildren.children.Add(new DebugUI.BoolField { displayName = "Debug With Sampling Noise", tooltip = "Enable Sampling Noise for this debug view. It should be enabled for accuracy but it can make results more difficult to read", getter = () => probeVolumeDebug.debugWithSamplingNoise, setter = value => { probeVolumeDebug.debugWithSamplingNoise = value; }, onValueChanged = RefreshDebug });
            probeContainer.children.Add(drawProbeSamplingDebugChildren);

            probeContainer.children.Add(new DebugUI.BoolField
            {
                displayName = "Virtual Offset Debug",
                tooltip = "Enable Virtual Offset debug visualization. Indicates the offsets applied to probe positions. These are used to capture lighting when probes are considered invalid.",
                getter = () => probeVolumeDebug.drawVirtualOffsetPush,
                setter = value =>
                {
                    probeVolumeDebug.drawVirtualOffsetPush = value;

                    if (probeVolumeDebug.drawVirtualOffsetPush && probeVolumeDebug.drawProbes && m_CurrentBakingSet != null)
                    {
                        // If probes are being drawn when enabling offset, automatically scale them down to a reasonable size so the arrows aren't obscured by the probes.
                        var searchDistance = CellSize(0) * MinBrickSize() / ProbeBrickPoolBase.brickCellCount * m_CurrentBakingSet.settings.virtualOffsetSettings.searchMultiplier + m_CurrentBakingSet.settings.virtualOffsetSettings.outOfGeoOffset;
                        probeVolumeDebug.probeSize = Mathf.Min(probeVolumeDebug.probeSize, Mathf.Clamp(searchDistance, kProbeSizeMin, kProbeSizeMax));
                    }
                }
            });

            var drawVirtualOffsetDebugChildren = new DebugUI.Container()
            {
                isHiddenCallback = () => !probeVolumeDebug.drawVirtualOffsetPush
            };

            var voOffset = new DebugUI.FloatField
            {
                displayName = "Debug Size",
                tooltip = "Modify the size of the arrows used in the virtual offset debug visualization.",
                getter = () => probeVolumeDebug.offsetSize,
                setter = value => probeVolumeDebug.offsetSize = value,
                min = () => kOffsetSizeMin,
                max = () => kOffsetSizeMax,
                isHiddenCallback = () => !probeVolumeDebug.drawVirtualOffsetPush
            };
            drawVirtualOffsetDebugChildren.children.Add(voOffset);
            probeContainer.children.Add(drawVirtualOffsetDebugChildren);

            probeContainer.children.Add(new DebugUI.FloatField { displayName = "Debug Draw Distance", tooltip = "How far from the Scene Camera to draw probe debug visualizations. Large distances can impact Editor performance.", getter = () => probeVolumeDebug.probeCullingDistance, setter = value => probeVolumeDebug.probeCullingDistance = value, min = () => 0.0f });
            widgetList.Add(probeContainer);
        }

        bool ShouldCullCell(Vector3 cellPosition, Transform cameraTransform, Plane[] frustumPlanes)
        {
            var volumeAABB = GetCellBounds(cellPosition);
            var cellSize = MaxBrickSize();

            // We do coarse culling with cell, finer culling later.
            float distanceRoundedUpWithCellSize = Mathf.CeilToInt(probeVolumeDebug.probeCullingDistance / cellSize) * cellSize;

            if (Vector3.Distance(cameraTransform.position, volumeAABB.center) > distanceRoundedUpWithCellSize)
                return true;

            return !GeometryUtility.TestPlanesAABB(frustumPlanes, volumeAABB);
        }

        static Vector4[] s_BoundsArray = new Vector4[16 * 3];

        static void UpdateDebugFromSelection(ref Vector4[] _AdjustmentVolumeBounds, ref int _AdjustmentVolumeCount)
        {
            if (ProbeVolumeDebug.s_ActiveAdjustmentVolumes == 0)
                return;

#if UNITY_EDITOR
            foreach (var touchup in Selection.GetFiltered<ProbeAdjustmentVolume>(SelectionMode.Unfiltered))
            {
                if (!touchup.isActiveAndEnabled) continue;

                Volume volume = new Volume(Matrix4x4.TRS(touchup.transform.position, touchup.transform.rotation, touchup.GetExtents()), 0, 0);
                volume.CalculateCenterAndSize(out Vector3 center, out var _);

                if (touchup.shape == ProbeAdjustmentVolume.Shape.Sphere)
                {
                    volume.Z.x = float.MaxValue;
                    volume.X.x = touchup.radius;
                }
                else
                {
                    volume.X *= 0.5f;
                    volume.Y *= 0.5f;
                    volume.Z *= 0.5f;
                }

                _AdjustmentVolumeBounds[_AdjustmentVolumeCount * 3 + 0] = new Vector4(center.x, center.y, center.z, volume.Z.x);
                _AdjustmentVolumeBounds[_AdjustmentVolumeCount * 3 + 1] = new Vector4(volume.X.x, volume.X.y, volume.X.z, volume.Z.y);
                _AdjustmentVolumeBounds[_AdjustmentVolumeCount * 3 + 2] = new Vector4(volume.Y.x, volume.Y.y, volume.Y.z, volume.Z.z);

                if (++_AdjustmentVolumeCount == 16)
                    break;
            }
#endif
        }

        Bounds GetCellBounds(Vector3 cellPosition)
        {
            var cellSize = MaxBrickSize();
            var cellOffset = ProbeOffset() + ProbeVolumeDebug.currentOffset;
            Vector3 cellCenterWS = cellOffset + cellPosition * cellSize + Vector3.one * (cellSize / 2.0f);

            return new Bounds(cellCenterWS, cellSize * Vector3.one);
        }

        bool ShouldCullCell(Vector3 cellPosition, Vector4[] adjustmentVolumeBounds, int adjustmentVolumeCount)
        {
            var cellAABB = GetCellBounds(cellPosition);

            for (int touchup = 0; touchup < adjustmentVolumeCount; touchup++)
            {
                Vector3 center = adjustmentVolumeBounds[touchup * 3 + 0];

                if (adjustmentVolumeBounds[touchup * 3].w == float.MaxValue) // sphere
                {
                    var diameter = adjustmentVolumeBounds[touchup * 3 + 1].x * 2.0f;
                    Bounds bounds = new Bounds(center, new Vector3(diameter, diameter, diameter));

                    if (bounds.Intersects(cellAABB))
                        return false;
                }
                else
                {
                    Volume volume = new Volume();
                    volume.X = adjustmentVolumeBounds[touchup * 3 + 1];
                    volume.Y = adjustmentVolumeBounds[touchup * 3 + 2];
                    volume.Z = new Vector3(adjustmentVolumeBounds[touchup * 3 + 0].w, adjustmentVolumeBounds[touchup * 3 + 1].w, adjustmentVolumeBounds[touchup * 3 + 2].w);

                    volume.corner = center - volume.X - volume.Y - volume.Z;
                    volume.X *= 2.0f;
                    volume.Y *= 2.05f;
                    volume.Z *= 2.0f;

                    if (ProbeVolumePositioning.OBBAABBIntersect(volume, cellAABB, volume.CalculateAABB()))
                        return false;
                }
            }
            return true;
        }

        protected override void DrawProbeDebug(Camera camera, Texture exposureTexture)
        {
            bool drawProbes = probeVolumeDebug.drawProbes;
            bool debugDraw = drawProbes ||
                             probeVolumeDebug.drawVirtualOffsetPush ||
                             probeVolumeDebug.drawProbeSamplingDebug;

            int adjustmentVolumeCount = 0;
            Vector4[] adjustmentVolumeBounds = s_BoundsArray;
            if (!debugDraw && probeVolumeDebug.autoDrawProbes)
            {
                UpdateDebugFromSelection(ref adjustmentVolumeBounds, ref adjustmentVolumeCount);
                drawProbes |= adjustmentVolumeCount != 0;
            }

            if (!debugDraw && !drawProbes)
                return;

            GeometryUtility.CalculateFrustumPlanes(camera, m_DebugFrustumPlanes);

            m_DebugMaterial.shaderKeywords = null;

            // This is to force the rendering not to draw to the depth pre pass and still behave.
            // They are going to be rendered opaque anyhow, just using the transparent render queue to make sure
            // they properly behave w.r.t fog.
            m_DebugMaterial.renderQueue = (int)RenderQueue.Transparent;
            m_DebugOffsetMaterial.renderQueue = (int)RenderQueue.Transparent;
            m_ProbeSamplingDebugMaterial.renderQueue = (int)RenderQueue.Transparent;
            m_ProbeSamplingDebugMaterial02.renderQueue = (int)RenderQueue.Transparent;

            m_DebugMaterial.SetVector("_DebugEmptyProbeData", APVDefinitions.debugEmptyColor);

            if (probeVolumeDebug.drawProbeSamplingDebug)
            {
                m_ProbeSamplingDebugMaterial.SetInt("_ShadingMode", (int)probeVolumeDebug.probeShading);
                m_ProbeSamplingDebugMaterial.SetInt("_RenderingLayerMask", (int)probeVolumeDebug.samplingRenderingLayer);
                m_ProbeSamplingDebugMaterial.SetVector("_DebugArrowColor", new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
                m_ProbeSamplingDebugMaterial.SetVector("_DebugLocator01Color", new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
                m_ProbeSamplingDebugMaterial.SetVector("_DebugLocator02Color", new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                m_ProbeSamplingDebugMaterial.SetFloat("_ProbeSize", probeVolumeDebug.probeSamplingDebugSize);
                m_ProbeSamplingDebugMaterial.SetTexture("_NumbersTex", m_DisplayNumbersTexture);
                m_ProbeSamplingDebugMaterial.SetInt("_DebugSamplingNoise", Convert.ToInt32(probeVolumeDebug.debugWithSamplingNoise));
                m_ProbeSamplingDebugMaterial.SetInt("_ForceDebugNormalViewBias", 0); // Add a secondary locator to show intermediate position (with no Anti-Leak) when Anti-Leak is active

                m_ProbeSamplingDebugMaterial.SetBuffer("_positionNormalBuffer", probeSamplingDebugData.positionNormalBuffer);

                Graphics.DrawMesh(m_DebugProbeSamplingMesh, new Vector4(0.0f, 0.0f, 0.0f, 1.0f), Quaternion.identity, m_ProbeSamplingDebugMaterial, 0, camera);
                Graphics.ClearRandomWriteTargets();
            }

            // Sanitize the min max subdiv levels with what is available
            int minAvailableSubdiv = cells.Count > 0 ? GetMaxSubdivision() - 1 : 0;
            foreach (var cell in cells.Values)
            {
                minAvailableSubdiv = Mathf.Min(minAvailableSubdiv, cell.desc.minSubdiv);
            }

            int maxSubdivToVisualize = Mathf.Max(0, Mathf.Min(probeVolumeDebug.maxSubdivToVisualize, GetMaxSubdivision() - 1));
            int minSubdivToVisualize = Mathf.Clamp(probeVolumeDebug.minSubdivToVisualize, minAvailableSubdiv, maxSubdivToVisualize);
            m_MaxSubdivVisualizedIsMaxAvailable = maxSubdivToVisualize == GetMaxSubdivision() - 1;

            bool adjustmentCulling = drawProbes && !probeVolumeDebug.drawProbes && probeVolumeDebug.isolationProbeDebug;
            foreach (var cell in cells.Values)
            {
                if (ShouldCullCell(cell.desc.position, camera.transform, m_DebugFrustumPlanes))
                    continue;

                if (adjustmentCulling && ShouldCullCell(cell.desc.position, adjustmentVolumeBounds, adjustmentVolumeCount))
                    continue;

                var debug = CreateInstancedProbes(cell);

                if (debug == null)
                    continue;

                for (int i = 0; i < debug.probeBuffers.Count; ++i)
                {
                    var props = debug.props[i];
                    props.SetInt("_ShadingMode", (int)probeVolumeDebug.probeShading);
                    props.SetFloat("_ExposureCompensation", probeVolumeDebug.exposureCompensation);
                    props.SetFloat("_ProbeSize", probeVolumeDebug.probeSize);
                    props.SetFloat("_CullDistance", probeVolumeDebug.probeCullingDistance);
                    props.SetInt("_MaxAllowedSubdiv", maxSubdivToVisualize);
                    props.SetInt("_MinAllowedSubdiv", minSubdivToVisualize);
                    props.SetFloat("_ValidityThreshold", m_CurrentBakingSet.settings.dilationSettings.dilationValidityThreshold);
                    props.SetInt("_RenderingLayerMask", probeVolumeDebug.visibleLayers);
                    props.SetFloat("_OffsetSize", probeVolumeDebug.offsetSize);
                    props.SetTexture("_ExposureTexture", exposureTexture);

                    if (drawProbes)
                    {
                        m_DebugMaterial.SetVectorArray("_TouchupVolumeBounds", adjustmentVolumeBounds);
                        m_DebugMaterial.SetInt("_AdjustmentVolumeCount", probeVolumeDebug.isolationProbeDebug ? adjustmentVolumeCount : 0);
                        m_DebugMaterial.SetVector("_ScreenSize", new Vector4(camera.pixelWidth, camera.pixelHeight, 1.0f / camera.pixelWidth, 1.0f / camera.pixelHeight));

                        var probeBuffer = debug.probeBuffers[i];
                        m_DebugMaterial.SetInt("_DebugProbeVolumeSampling", 0);
                        m_DebugMaterial.SetBuffer("_positionNormalBuffer", probeSamplingDebugData.positionNormalBuffer);
                        Graphics.DrawMeshInstanced(debugMesh, 0, m_DebugMaterial, probeBuffer, probeBuffer.Length, props, ShadowCastingMode.Off, false, 0, camera, LightProbeUsage.Off, null);
                    }

                    if (probeVolumeDebug.drawProbeSamplingDebug)
                    {
                        var probeBuffer = debug.probeBuffers[i];
                        m_ProbeSamplingDebugMaterial02.SetInt("_DebugProbeVolumeSampling", 1);
                        props.SetInt("_ShadingMode", (int)DebugProbeShadingMode.SH);
                        props.SetFloat("_ProbeSize", probeVolumeDebug.probeSamplingDebugSize);
                        props.SetInt("_DebugSamplingNoise", Convert.ToInt32(probeVolumeDebug.debugWithSamplingNoise));
                        props.SetInt("_RenderingLayerMask", (int)probeVolumeDebug.samplingRenderingLayer);
                        m_ProbeSamplingDebugMaterial02.SetBuffer("_positionNormalBuffer", probeSamplingDebugData.positionNormalBuffer);
                        Graphics.DrawMeshInstanced(debugMesh, 0, m_ProbeSamplingDebugMaterial02, probeBuffer, probeBuffer.Length, props, ShadowCastingMode.Off, false, 0, camera, LightProbeUsage.Off, null);
                    }

                    if (probeVolumeDebug.drawVirtualOffsetPush)
                    {
                        m_DebugOffsetMaterial.SetVectorArray("_TouchupVolumeBounds", adjustmentVolumeBounds);
                        m_DebugOffsetMaterial.SetInt("_AdjustmentVolumeCount", probeVolumeDebug.isolationProbeDebug ? adjustmentVolumeCount : 0);

                        var offsetBuffer = debug.offsetBuffers[i];
                        Graphics.DrawMeshInstanced(m_DebugOffsetMesh, 0, m_DebugOffsetMaterial, offsetBuffer, offsetBuffer.Length, props, ShadowCastingMode.Off, false, 0, camera, LightProbeUsage.Off, null);
                    }
                }
            }
        }

        CellInstancedDebugProbes CreateInstancedProbes(APVCell cell)
        {
            if (cell.debugProbes != null)
                return cell.debugProbes;

            if (HasActiveStreamingRequest(cell))
                return null;

            int maxSubdiv = GetMaxSubdivision() - 1;

            if (!cell.data.bricks.IsCreated || cell.data.bricks.Length == 0 || !cell.data.probePositions.IsCreated || !cell.loaded)
                return null;

            List<Matrix4x4[]> probeBuffers = new List<Matrix4x4[]>();
            List<Matrix4x4[]> offsetBuffers = new List<Matrix4x4[]>();
            List<MaterialPropertyBlock> props = new List<MaterialPropertyBlock>();
            var chunks = cell.poolInfo.chunkList;

            Vector4[] texels = new Vector4[k_probesPerBatch];
            float[] layer = new float[k_probesPerBatch];
            float[] validity = new float[k_probesPerBatch];
            float[] dilationThreshold = new float[k_probesPerBatch];
            float[] relativeSize = new float[k_probesPerBatch];
            float[] touchupUpVolumeAction = cell.data.touchupVolumeInteraction.Length > 0 ? new float[k_probesPerBatch] : null;
            Vector4[] offsets = cell.data.offsetVectors.Length > 0 ? new Vector4[k_probesPerBatch] : null;

            List<Matrix4x4> probeBuffer = new List<Matrix4x4>();
            List<Matrix4x4> offsetBuffer = new List<Matrix4x4>();

            var debugData = new CellInstancedDebugProbes();
            debugData.probeBuffers = probeBuffers;
            debugData.offsetBuffers = offsetBuffers;
            debugData.props = props;

            var chunkSizeInProbes = ProbeBrickPoolBase.GetChunkSizeInProbeCount();

            var loc = ProbeBrickPoolBase.ProbeCountToDataLocSize(chunkSizeInProbes);
            float baseThreshold = m_CurrentBakingSet.settings.dilationSettings.dilationValidityThreshold;
            int idxInBatch = 0;
            int globalIndex = 0;
            int brickCount = cell.desc.probeCount / ProbeBrickPoolBase.brickProbeCountTotal;
            int bx = 0, by = 0, bz = 0;
            for (int brickIndex = 0; brickIndex < brickCount; ++brickIndex)
            {
                Debug.Assert(bz < loc.z);

                int brickSize = cell.data.bricks[brickIndex].subdivisionLevel;
                int chunkIndex = brickIndex / ProbeBrickPoolBase.GetChunkSizeInBrickCount();
                var chunk = chunks[chunkIndex];
                Vector3Int brickStart = new Vector3Int(chunk.x + bx, chunk.y + by, chunk.z + bz);

                for (int z = 0; z < ProbeBrickPoolBase.brickProbeCountPerDim; ++z)
                {
                    for (int y = 0; y < ProbeBrickPoolBase.brickProbeCountPerDim; ++y)
                    {
                        for (int x = 0; x < ProbeBrickPoolBase.brickProbeCountPerDim; ++x)
                        {
                            Vector3Int texelLoc = new Vector3Int(brickStart.x + x, brickStart.y + y, brickStart.z + z);

                            int probeFlatIndex = chunkIndex * chunkSizeInProbes + (bx + x) + loc.x * ((by + y) + loc.y * (bz + z));
                            var position = cell.data.probePositions[probeFlatIndex] - ProbeOffset(); // Offset is applied in shader

                            probeBuffer.Add(Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * (0.3f * (brickSize + 1))));
                            validity[idxInBatch] = cell.data.validity[probeFlatIndex];
                            dilationThreshold[idxInBatch] = baseThreshold;
                            texels[idxInBatch] = new Vector4(texelLoc.x, texelLoc.y, texelLoc.z, brickSize);
                            relativeSize[idxInBatch] = (float)brickSize / (float)maxSubdiv;

                            layer[idxInBatch] = Unity.Mathematics.math.asfloat(cell.data.layer.Length > 0 ? cell.data.layer[probeFlatIndex] : 0xFFFFFFFF);

                            if (touchupUpVolumeAction != null)
                            {
                                touchupUpVolumeAction[idxInBatch] = cell.data.touchupVolumeInteraction[probeFlatIndex];
                                dilationThreshold[idxInBatch] = touchupUpVolumeAction[idxInBatch] > 1.0f ? touchupUpVolumeAction[idxInBatch] - 1.0f : baseThreshold;
                            }

                            if (offsets != null)
                            {
                                const float kOffsetThresholdSqr = 1e-6f;

                                var offset = cell.data.offsetVectors[probeFlatIndex];
                                offsets[idxInBatch] = offset;

                                if (offset.sqrMagnitude < kOffsetThresholdSqr)
                                {
                                    offsetBuffer.Add(Matrix4x4.identity);
                                }
                                else
                                {
                                    var orientation = Quaternion.LookRotation(-offset);
                                    var scale = new Vector3(0.5f, 0.5f, offset.magnitude);
                                    offsetBuffer.Add(Matrix4x4.TRS(position + offset, orientation, scale));
                                }
                            }
                            idxInBatch++;

                            if (probeBuffer.Count >= k_probesPerBatch || globalIndex == cell.desc.probeCount - 1)
                            {
                                idxInBatch = 0;
                                MaterialPropertyBlock prop = new MaterialPropertyBlock();

                                prop.SetFloatArray("_Validity", validity);
                                prop.SetFloatArray("_RenderingLayer", layer);
                                prop.SetFloatArray("_DilationThreshold", dilationThreshold);
                                prop.SetFloatArray("_TouchupedByVolume", touchupUpVolumeAction);
                                prop.SetFloatArray("_RelativeSize", relativeSize);
                                prop.SetVectorArray("_IndexInAtlas", texels);

                                if (offsets != null)
                                    prop.SetVectorArray("_Offset", offsets);

                                props.Add(prop);

                                probeBuffers.Add(probeBuffer.ToArray());
                                probeBuffer.Clear();

                                offsetBuffers.Add(offsetBuffer.ToArray());
                                offsetBuffer.Clear();
                            }

                            globalIndex++;
                        }
                    }
                }

                bx += ProbeBrickPoolBase.brickProbeCountPerDim;
                if (bx >= loc.x)
                {
                    bx = 0;
                    by += ProbeBrickPoolBase.brickProbeCountPerDim;
                    if (by >= loc.y)
                    {
                        by = 0;
                        bz += ProbeBrickPoolBase.brickProbeCountPerDim;
                        if (bz >= loc.z)
                        {
                            bx = 0;
                            by = 0;
                            bz = 0;
                        }
                    }
                }
            }

            return debugData;
        }

    }
}
