using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal struct AdditionalLightsShadowAtlasLayout
    {
        internal struct ShadowResolutionRequest
        {
            public int visibleLightIndex;
            public int perLightShadowSliceIndex;
            public int requestedResolution;
            public int offsetX;             // x coordinate of the square area allocated in the atlas for this shadow map
            public int offsetY;             // y coordinate of the square area allocated in the atlas for this shadow map
            public int allocatedResolution; // width of the square area allocated in the atlas for this shadow map

            [Flags]
            private enum SettingsOptions : ushort
            {
                None = 0,
                SoftShadow = (1 << 0),
                PointLightShadow = (1 << 1),
                All = 0xFFFF
            }
            private SettingsOptions _shadowProperties;

            public bool softShadow
            {
                // otherwise it's hard-shadow (no filtering)
                get => _shadowProperties.HasFlag(SettingsOptions.SoftShadow);
                set
                {
                    if (value)
                        _shadowProperties |= SettingsOptions.SoftShadow;
                    else
                        _shadowProperties &= ~SettingsOptions.SoftShadow;
                }
            }

            public bool pointLightShadow
            {
                // otherwise it's spot light shadow (1 shadow slice instead of 6)
                get => _shadowProperties.HasFlag(SettingsOptions.PointLightShadow);
                set
                {
                    if (value)
                        _shadowProperties |= SettingsOptions.PointLightShadow;
                    else
                        _shadowProperties &= ~SettingsOptions.PointLightShadow;
                }
            }

            public ShadowResolutionRequest(int visibleLightIndex, int perLightShadowSliceIndex, int requestedResolution,  bool softShadowVal, bool pointLightShadowVal)
            {
                this.visibleLightIndex = visibleLightIndex;
                this.perLightShadowSliceIndex = perLightShadowSliceIndex;
                this.requestedResolution = requestedResolution;
                _shadowProperties = SettingsOptions.None;
                _shadowProperties = softShadowVal ? _shadowProperties |= SettingsOptions.SoftShadow : _shadowProperties;
                _shadowProperties = pointLightShadowVal ? _shadowProperties |= SettingsOptions.PointLightShadow : _shadowProperties;

                offsetX = 0;
                offsetY = 0;
                allocatedResolution = 0;
            }
        }

        static List<RectInt> s_unusedAtlasSquareAreas; // This list tracks space available in the atlas
        static List<ShadowResolutionRequest> s_shadowResolutionRequests; // intermediate array used to compute the final resolution of each shadow slice rendered in the frame
        static float[] s_visibleLightIndexToCameraSquareDistance; // stores for each shadowed additional light its (squared) distance to camera ; used to sub-sort shadow requests according to how close their casting light is
        static Func<ShadowResolutionRequest, ShadowResolutionRequest, int> s_compareShadowResolutionRequest;
        static ShadowResolutionRequest[] s_sortedShadowResolutionRequests;

        NativeArray<ShadowResolutionRequest> _sortedShadowResolutionRequests;
        NativeArray<int> _visibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex; // for each visible light, store the index of its first shadow slice in m_SortedShadowResolutionRequests (for quicker access)
        int _totalShadowSlicesCount;
        int _totalShadowResolutionRequestCount;
        bool _tooManyShadowMaps;
        int _shadowSlicesScaleFactor;
        int _atlasSize;

        public AdditionalLightsShadowAtlasLayout(LightData lightData, ShadowData shadowData, CameraData cameraData)
        {
            bool useStructuredBuffer = RenderingUtils.useStructuredBuffer;
            NativeArray<VisibleLight> visibleLights = lightData.visibleLights;
            int numberOfVisibleLights = visibleLights.Length;

            if (s_unusedAtlasSquareAreas == null)
                s_unusedAtlasSquareAreas = new List<RectInt>();

            if (s_shadowResolutionRequests == null)
                s_shadowResolutionRequests = new List<ShadowResolutionRequest>();

            if (s_visibleLightIndexToCameraSquareDistance == null || s_visibleLightIndexToCameraSquareDistance.Length < numberOfVisibleLights)
                s_visibleLightIndexToCameraSquareDistance = new float[numberOfVisibleLights];

            if (s_compareShadowResolutionRequest == null)
                s_compareShadowResolutionRequest = CreateCompareShadowResolutionRequesPredicate();

            if (!useStructuredBuffer)
            {
                int newCapacity = UltraRenderPipeline.maxVisibleAdditionalLights;

                if (s_unusedAtlasSquareAreas.Capacity < newCapacity)
                    s_unusedAtlasSquareAreas.Capacity = newCapacity;

                if (s_shadowResolutionRequests.Count < numberOfVisibleLights)
                {
                    s_shadowResolutionRequests.Capacity = numberOfVisibleLights;
                    int diff = numberOfVisibleLights - s_shadowResolutionRequests.Count + 1;
                    for (int i = 0; i < diff; i++)
                        s_shadowResolutionRequests.Add(new ShadowResolutionRequest());
                }

            }

            s_unusedAtlasSquareAreas.Clear();

            ushort totalShadowResolutionRequestsCount = 0; // Number of shadow slices that we would need for all shadowed additional (punctual) lights in the scene. We might have to ignore some of those requests if they do not fit in the shadow atlas.
            for (int visibleLightIndex = 0; visibleLightIndex < visibleLights.Length; ++visibleLightIndex)
            {
                // Skip main directional light as it is not packed into the shadow atlas
                if (visibleLightIndex == lightData.mainLightIndex)
                {
                    s_visibleLightIndexToCameraSquareDistance[visibleLightIndex] = float.MaxValue;
                    continue;
                }

                ref VisibleLight vl = ref visibleLights.UnsafeElementAt(visibleLightIndex);
                Light light = vl.light;
                if (light == null)
                {
                    s_visibleLightIndexToCameraSquareDistance[visibleLightIndex] = float.MaxValue;
                    continue;
                }
                LightType lightType = vl.lightType;
                LightShadows lightShadows = light.shadows;
                float shadowStrength = light.shadowStrength;
                if (!ShadowUtils.IsValidShadowCastingLight(lightData, visibleLightIndex, lightType, lightShadows, shadowStrength))
                {
                    s_visibleLightIndexToCameraSquareDistance[visibleLightIndex] = float.MaxValue;
                    continue;
                }

                bool softShadows = (lightShadows == LightShadows.Soft);
                bool pointLightShadow = (lightType == LightType.Point);
                ushort visibleLightIndexUshort = (ushort)visibleLightIndex;
                ushort requestedResolution = (ushort)shadowData.resolution[visibleLightIndex];
                int shadowSlicesCountForThisLight = ShadowUtils.GetPunctualLightShadowSlicesCount(lightType);
                for (ushort perLightShadowSliceIndex = 0; perLightShadowSliceIndex < shadowSlicesCountForThisLight; ++perLightShadowSliceIndex)
                {
                    if (totalShadowResolutionRequestsCount >= s_shadowResolutionRequests.Count)
                        s_shadowResolutionRequests.Add(new ShadowResolutionRequest());

                    ShadowResolutionRequest request = s_shadowResolutionRequests[totalShadowResolutionRequestsCount];
                    request.visibleLightIndex = visibleLightIndexUshort;
                    request.perLightShadowSliceIndex = perLightShadowSliceIndex;
                    request.requestedResolution = requestedResolution;
                    request.softShadow = softShadows;
                    request.pointLightShadow = pointLightShadow;
                    s_shadowResolutionRequests[totalShadowResolutionRequestsCount] = request;
                    totalShadowResolutionRequestsCount++;
                }

                // mark this light as casting shadows
                s_visibleLightIndexToCameraSquareDistance[visibleLightIndex] = (cameraData.worldSpaceCameraPos - light.transform.position).sqrMagnitude;
            }

            if (s_sortedShadowResolutionRequests == null || s_sortedShadowResolutionRequests.Length < totalShadowResolutionRequestsCount)
                s_sortedShadowResolutionRequests = new ShadowResolutionRequest[totalShadowResolutionRequestsCount];

            for (int i = 0; i < totalShadowResolutionRequestsCount; ++i)
                s_sortedShadowResolutionRequests[i] = s_shadowResolutionRequests[i];

            using (new ProfilingScope(Sorting.quickSortSampler))
            {
                Sorting.QuickSort(s_sortedShadowResolutionRequests, 0, totalShadowResolutionRequestsCount - 1, s_compareShadowResolutionRequest);
            }

            _sortedShadowResolutionRequests = new NativeArray<ShadowResolutionRequest>(s_sortedShadowResolutionRequests, Allocator.Temp);

            // To avoid visual artifacts when there is not enough place in the atlas, we remove shadow slices that would be allocated a too small resolution.
            // When not using structured buffers, m_AdditionalLightShadowSliceIndexTo_WorldShadowMatrix.Length maps to _AdditionalLightsWorldToShadow in Shadows.hlsl
            // In that case we have to limit its size because uniform buffers cannot be higher than 64kb for some platforms.
            int totalShadowSlicesCount = useStructuredBuffer ? totalShadowResolutionRequestsCount : Math.Min(totalShadowResolutionRequestsCount, UltraRenderPipeline.maxVisibleAdditionalLights);  // Number of shadow slices that we will actually be able to fit in the shadow atlas without causing visual artifacts.
            // Requests are grouped by light. The uniform-buffer limit must not split a point
            // light's six faces: culling and sampling always access the complete cubemap.
            if (totalShadowSlicesCount > 0 && totalShadowSlicesCount < totalShadowResolutionRequestsCount &&
                _sortedShadowResolutionRequests[totalShadowSlicesCount - 1].visibleLightIndex ==
                _sortedShadowResolutionRequests[totalShadowSlicesCount].visibleLightIndex)
            {
                totalShadowSlicesCount -= _sortedShadowResolutionRequests[totalShadowSlicesCount - 1].perLightShadowSliceIndex + 1;
            }
            int atlasSize = shadowData.additionalLightsShadowmapWidth;

            // Find biggest end index in m_SortedShadowResolutionRequests array, under which all shadow requests can be allocated a big enough shadow atlas slot, to not cause rendering artifacts
            bool allShadowsAfterStartIndexHaveEnoughResolution = false;
            int estimatedScaleFactor = 1;
            while (!allShadowsAfterStartIndexHaveEnoughResolution && totalShadowSlicesCount > 0)
            {
                ShadowResolutionRequest request = _sortedShadowResolutionRequests[totalShadowSlicesCount - 1];
                estimatedScaleFactor = EstimateScaleFactorNeededToFitAllShadowsInAtlas(_sortedShadowResolutionRequests, totalShadowSlicesCount, atlasSize);

                // check if resolution of the least priority shadow slice request would be acceptable
                if (request.requestedResolution >= estimatedScaleFactor * ShadowUtils.MinimalPunctualLightShadowResolution(request.softShadow))
                    allShadowsAfterStartIndexHaveEnoughResolution = true;
                else // Skip shadow requests for this light ; their resolution is too small to look any good
                    totalShadowSlicesCount -= ShadowUtils.GetPunctualLightShadowSlicesCount(request.pointLightShadow ? LightType.Point : LightType.Spot);
            }

            for (int sortedArrayIndex = totalShadowSlicesCount; sortedArrayIndex < _sortedShadowResolutionRequests.Length; ++sortedArrayIndex)
                _sortedShadowResolutionRequests[sortedArrayIndex] = default; // Reset entries that we cannot fit in the atlas

            _visibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex = new NativeArray<int>(visibleLights.Length, Allocator.Temp);

            // Reset the reverse lookup array
            for (int visibleLightIndex = 0; visibleLightIndex < _visibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex.Length; ++visibleLightIndex)
                _visibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[visibleLightIndex] = -1;

            // Update the reverse lookup array (starting from the end of the array, in order to use index of slice#0 in case a same visibleLight has several shadowSlices)
            for (int sortedArrayIndex = totalShadowSlicesCount - 1; sortedArrayIndex >= 0; --sortedArrayIndex)
            {
                int visibleLightIndex = s_sortedShadowResolutionRequests[sortedArrayIndex].visibleLightIndex;
                _visibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[visibleLightIndex] = sortedArrayIndex;
            }

            // Assigns to each of the first totalShadowSlicesCount items in m_SortedShadowResolutionRequests a location in the shadow atlas based on requested resolutions.
            // If necessary, scales down shadow maps active in the frame, to make all of them fit in the atlas.
            bool allShadowSlicesFitInAtlas = false;
            bool tooManyShadows = false;
            int shadowSlicesScaleFactor = estimatedScaleFactor;

            while (!allShadowSlicesFitInAtlas && !tooManyShadows)
            {
                s_unusedAtlasSquareAreas.Clear();
                s_unusedAtlasSquareAreas.Add(new RectInt(0, 0, atlasSize, atlasSize));
                allShadowSlicesFitInAtlas = true;

                for (int shadowRequestIndex = 0; shadowRequestIndex < totalShadowSlicesCount; ++shadowRequestIndex)
                {
                    var resolution = _sortedShadowResolutionRequests[shadowRequestIndex].requestedResolution / shadowSlicesScaleFactor;

                    if (resolution < ShadowUtils.MinimalPunctualLightShadowResolution(_sortedShadowResolutionRequests[shadowRequestIndex].softShadow))
                    {
                        tooManyShadows = true;
                        break;
                    }

                    bool foundSpaceInAtlas = false;

                    // Try to find free space in the atlas
                    for (int unusedAtlasSquareAreaIndex = 0; unusedAtlasSquareAreaIndex < s_unusedAtlasSquareAreas.Count; ++unusedAtlasSquareAreaIndex)
                    {
                        RectInt atlasArea = s_unusedAtlasSquareAreas[unusedAtlasSquareAreaIndex];
                        int atlasAreaWidth = atlasArea.width;
                        if (atlasAreaWidth < resolution)
                            continue;

                        int atlasAreaHeight = atlasArea.height;
                        int atlasAreaX = atlasArea.x;
                        int atlasAreaY = atlasArea.y;

                        // we can use this atlas area for the shadow request
                        ref ShadowResolutionRequest shadowRequest = ref _sortedShadowResolutionRequests.UnsafeElementAtMutable(shadowRequestIndex);
                        shadowRequest.offsetX = (ushort)atlasAreaX;
                        shadowRequest.offsetY = (ushort)atlasAreaY;
                        shadowRequest.allocatedResolution = (ushort)resolution;

                        // this atlas space is not available anymore, so remove it from the list
                        s_unusedAtlasSquareAreas.RemoveAt(unusedAtlasSquareAreaIndex);

                        // make sure to split space so that the rest of this square area can be used
                        int remainingShadowRequestsCount = totalShadowSlicesCount - shadowRequestIndex - 1; // (no need to add more than that)
                        int newSquareAreasCount = 0;
                        int newSquareAreaWidth = resolution; // we split the area in squares of same size
                        int newSquareAreaHeight = resolution;
                        int newSquareAreaX = atlasAreaX;
                        int newSquareAreaY = atlasAreaY;

                        while (newSquareAreasCount < remainingShadowRequestsCount)
                        {
                            newSquareAreaX += newSquareAreaWidth;
                            if (newSquareAreaX + newSquareAreaWidth > (atlasAreaX + atlasAreaWidth))
                            {
                                newSquareAreaX = atlasAreaX;
                                newSquareAreaY += newSquareAreaHeight;
                                if (newSquareAreaY + newSquareAreaHeight > (atlasAreaY + atlasAreaHeight))
                                    break;
                            }

                            // replace the space we removed previously by new smaller squares (inserting them in this order ensures shadow maps will be packed at the side of the atlas, without gaps)
                            s_unusedAtlasSquareAreas.Insert(unusedAtlasSquareAreaIndex + newSquareAreasCount, new RectInt(newSquareAreaX, newSquareAreaY, newSquareAreaWidth, newSquareAreaHeight));
                            ++newSquareAreasCount;
                        }

                        foundSpaceInAtlas = true;
                        break;
                    }

                    if (!foundSpaceInAtlas)
                    {
                        allShadowSlicesFitInAtlas = false;
                        break;
                    }
                }

                if (!allShadowSlicesFitInAtlas && !tooManyShadows)
                    shadowSlicesScaleFactor *= 2;
            }

            _tooManyShadowMaps = tooManyShadows;
            _shadowSlicesScaleFactor = shadowSlicesScaleFactor;
            _totalShadowSlicesCount = totalShadowSlicesCount;
            _totalShadowResolutionRequestCount = totalShadowResolutionRequestsCount;
            _atlasSize = atlasSize;
        }

        public int GetTotalShadowSlicesCount() => _totalShadowSlicesCount;

        public int GetTotalShadowResolutionRequestCount() => _totalShadowResolutionRequestCount;

        public bool HasTooManyShadowMaps() => _tooManyShadowMaps;

        public int GetShadowSlicesScaleFactor() => _shadowSlicesScaleFactor;

        public int GetAtlasSize() => _atlasSize;

        public bool HasSpaceForLight(int originalVisibleLightIndex)
        {
            return _visibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[originalVisibleLightIndex] != -1;
        }

        public ShadowResolutionRequest GetSortedShadowResolutionRequest(int sortedShadowResolutionRequestIndex)
        {
            return _sortedShadowResolutionRequests[sortedShadowResolutionRequestIndex];
        }

        public ShadowResolutionRequest GetSliceShadowResolutionRequest(int originalVisibleLightIndex, int sliceIndex)
        {
            int sortedShadowResolutionRequestIndex = _visibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[originalVisibleLightIndex];
            return _sortedShadowResolutionRequests[sortedShadowResolutionRequestIndex + sliceIndex];
        }

        public static void ClearStaticCaches()
        {
            s_unusedAtlasSquareAreas = null;
            s_shadowResolutionRequests = null;
            s_visibleLightIndexToCameraSquareDistance = null;
            s_compareShadowResolutionRequest = null;
            s_sortedShadowResolutionRequests = null;
        }

        static int EstimateScaleFactorNeededToFitAllShadowsInAtlas(in NativeArray<ShadowResolutionRequest> shadowResolutionRequests, int endIndex, int atlasSize)
        {
            long totalTexelsInShadowAtlas = atlasSize * atlasSize;

            long totalTexelsInShadowRequests = 0;
            for (int shadowRequestIndex = 0; shadowRequestIndex < endIndex; ++shadowRequestIndex)
                totalTexelsInShadowRequests += shadowResolutionRequests[shadowRequestIndex].requestedResolution * shadowResolutionRequests[shadowRequestIndex].requestedResolution;

            int estimatedScaleFactor = 1;
            while (totalTexelsInShadowRequests > totalTexelsInShadowAtlas * estimatedScaleFactor * estimatedScaleFactor)
                estimatedScaleFactor *= 2;

            return estimatedScaleFactor;
        }

        // Sort array in decreasing requestedResolution order,
        // sub-sorting in "HardShadow > SoftShadow" and then "Spot > Point",
        //   i.e place last requests that will be removed in priority to make room for the others,
        //   because their resolution is too small to produce good-looking shadows ; or because they take relatively more space in the atlas )
        // sub-sub-sorting in light distance to camera
        // then grouping in increasing visibleIndex (and sub-sorting each group in ShadowSliceIndex order)
        static Func<ShadowResolutionRequest, ShadowResolutionRequest, int> CreateCompareShadowResolutionRequesPredicate()
        {
            return (ShadowResolutionRequest curr, ShadowResolutionRequest other) =>
            {
                return (((curr.requestedResolution > other.requestedResolution)
                         || (curr.requestedResolution == other.requestedResolution && !curr.softShadow && other.softShadow)
                         || (curr.requestedResolution == other.requestedResolution && curr.softShadow == other.softShadow && !curr.pointLightShadow && other.pointLightShadow)
                         || (curr.requestedResolution == other.requestedResolution && curr.softShadow == other.softShadow && curr.pointLightShadow == other.pointLightShadow && s_visibleLightIndexToCameraSquareDistance[curr.visibleLightIndex] < s_visibleLightIndexToCameraSquareDistance[other.visibleLightIndex])
                         || (curr.requestedResolution == other.requestedResolution && curr.softShadow == other.softShadow && curr.pointLightShadow == other.pointLightShadow && s_visibleLightIndexToCameraSquareDistance[curr.visibleLightIndex] == s_visibleLightIndexToCameraSquareDistance[other.visibleLightIndex] && curr.visibleLightIndex < other.visibleLightIndex)
                         || (curr.requestedResolution == other.requestedResolution && curr.softShadow == other.softShadow && curr.pointLightShadow == other.pointLightShadow && s_visibleLightIndexToCameraSquareDistance[curr.visibleLightIndex] == s_visibleLightIndexToCameraSquareDistance[other.visibleLightIndex] && curr.visibleLightIndex == other.visibleLightIndex && curr.perLightShadowSliceIndex < other.perLightShadowSliceIndex)))
                    ? -1 : 1;
            };
        }
    }

}
