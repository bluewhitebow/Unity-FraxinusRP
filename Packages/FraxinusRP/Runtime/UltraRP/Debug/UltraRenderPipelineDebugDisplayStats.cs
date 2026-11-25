using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// UltraRP Rendering Debugger Display Stats.
    /// </summary>
    public class UltraRenderPipelineDebugDisplayStats : DebugDisplayStats<UltraRPProfileId>
    {
        private DebugFrameTiming _debugFrameTiming = new();

        private List<UltraRPProfileId> _recordedSamplers = new();

        /// <inheritdoc/>
        public override void EnableProfilingRecorders()
        {
            Debug.Assert(_recordedSamplers.Count == 0);
            _recordedSamplers = GetProfilerIdsToDisplay();
        }

        /// <inheritdoc/>
        public override void DisableProfilingRecorders()
        {
            foreach (var sampler in _recordedSamplers)
                ProfilingSampler.Get(sampler).enableRecording = false;

            _recordedSamplers.Clear();
        }

        /// <inheritdoc/>
        public override void RegisterDebugUI(List<DebugUI.Widget> list)
        {
#if UNITY_ANDROID || UNITY_IPHONE || UNITY_TVOS
            list.Add(new DebugUI.MessageBox
            {
                displayName = "Warning: GPU timings may not be accurate on mobile devices that have tile-based architectures.",
                style = DebugUI.MessageBox.Style.Warning
            });
#endif

            _debugFrameTiming.RegisterDebugUI(list);

            var detailedStatsFoldout = new DebugUI.Foldout
            {
                displayName = "Detailed Stats",
                isHeader = true,
                opened = false,
                children =
                {
                    new DebugUI.BoolField
                    {
                        displayName = "Update every second with average",
                        getter = () => averageProfilerTimingsOverASecond,
                        setter = value => averageProfilerTimingsOverASecond = value
                    },
                    new DebugUI.BoolField
                    {
                        displayName = "Hide empty scopes",
                        tooltip = "Hide profiling scopes where elapsed time in each category is zero",
                        getter = () => hideEmptyScopes,
                        setter = value => hideEmptyScopes = value
                    }
                }
            };
            detailedStatsFoldout.children.Add(BuildDetailedStatsList("Profiling Scopes", _recordedSamplers));
            list.Add(detailedStatsFoldout);
        }

        /// <inheritdoc/>
        public override void Update()
        {
            _debugFrameTiming.UpdateFrameTiming();
            UpdateDetailedStats(_recordedSamplers);
        }
    }
}
