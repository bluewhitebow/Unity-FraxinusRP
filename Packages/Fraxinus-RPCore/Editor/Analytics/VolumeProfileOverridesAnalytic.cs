using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.Analytics
{
#if UNITY_6000_0_OR_NEWER
    // Unity 6's attribute-driven analytics contract.
    internal class VolumeProfileOverridesAnalytic : IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        [AnalyticInfo(eventName: "uVolumeProfileOverridesAnalytic", version: 2, maxEventsPerHour: 1000, vendorKey: "unity.srp")]
        public class Analytic : IAnalytic
        {
            public Analytic(string asset_guid, string type, string[] p)
            {
                m_Data = new Data
                {
                    volume_profile_asset_guid = asset_guid,
                    component_type = type,
                    overrided_parameters = p
                };
            }

            [System.Diagnostics.DebuggerDisplay("{volume_profile_asset_guid} - {component_type} - {overrided_parameters.Length}")]
            [Serializable]
            struct Data : IAnalytic.IData
            {
                public string volume_profile_asset_guid;
                public string component_type;
                public string[] overrided_parameters;
            }

            public bool TryGatherData(out IAnalytic.IData data, out Exception error)
            {
                data = m_Data;
                error = null;
                return true;
            }

            Data m_Data;
        }

        void IPostprocessBuildWithReport.OnPostprocessBuild(BuildReport _)
        {
            SendAnalytic();
        }

        private static readonly string[] k_SearchFolders = new[] { "Assets" };

        [MustUseReturnValue]
        static bool TryGatherData([NotNullWhen(true)] out List<IAnalytic> datas,
            [NotNullWhen(false)] out string warning)
        {
            warning = string.Empty;
            datas = new List<IAnalytic>();

            var volumeProfileGUIDs = AssetDatabase.FindAssets($"t:{nameof(VolumeProfile)} glob:\"**/*.asset\"", k_SearchFolders);
            foreach (var guid in volumeProfileGUIDs)
            {
                var volumeProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (volumeProfile == null)
                    continue;

                foreach (var volumeComponent in volumeProfile.components)
                {
                    var volumeComponentType = volumeComponent.GetType();
                    var defaultVolumeComponent = (VolumeComponent)ScriptableObject.CreateInstance(volumeComponentType);
                    var overrideParameters = volumeComponent.ToNestedColumnWithDefault(defaultVolumeComponent, true);
                    if (overrideParameters.Length == 0)
                        continue;
                    datas.Add(new Analytic(guid, volumeComponent.GetType().Name, overrideParameters));
                }
            }

            return true;
        }

        [MenuItem("internal:Edit/Rendering/Analytics/Send VolumeProfileOverridesAnalytic ", priority = 1)]
        static void SendAnalytic()
        {
            if (!TryGatherData(out var data, out var warning))
                Debug.Log(warning);

            data.ForEach(d => AnalyticsUtils.SendData(d));
        }
    }
#else
    // Unity 2022 legacy EditorAnalytics implementation.
    internal class VolumeProfileOverridesAnalytic : IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        [System.Diagnostics.DebuggerDisplay("{volume_profile_asset_guid} - {component_type} - {overrided_parameters.Length}")]
        struct Data
        {
            internal const string k_EventName = "uVolumeProfileOverridesAnalytic";
            internal const int k_Version = 2;

            public string volume_profile_asset_guid;
            public string component_type;
            public string[] overrided_parameters;
        }

        void IPostprocessBuildWithReport.OnPostprocessBuild(BuildReport _)
        {
            SendAnalytic();
        }

        private static readonly string[] k_SearchFolders = new[] { "Assets" };

        [MustUseReturnValue]
        static bool TryGatherData([NotNullWhen(true)] out List<Data> datas,
            [NotNullWhen(false)] out string warning)
        {
            warning = string.Empty;
            datas = new List<Data>();

            var volumeProfileGUIDs = AssetDatabase.FindAssets($"t:{nameof(VolumeProfile)} glob:\"**/*.asset\"", k_SearchFolders);
            foreach (var guid in volumeProfileGUIDs)
            {
                var volumeProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (volumeProfile == null)
                    continue;

                foreach (var volumeComponent in volumeProfile.components)
                {
                    var volumeComponentType = volumeComponent.GetType();
                    // Unity 2022 does not expose VolumeManager.GetDefaultVolumeComponent.
                    // Create a fresh component instance, matching the 2022 Core package behavior.
                    var defaultVolumeComponent = (VolumeComponent)ScriptableObject.CreateInstance(volumeComponentType);

                    var overrideParameters = volumeComponent.ToNestedColumnWithDefault(defaultVolumeComponent, true);
                    if (overrideParameters.Length == 0)
                        continue;
                    datas.Add(new Data
                    {
                        volume_profile_asset_guid = guid,
                        component_type = volumeComponent.GetType().Name,
                        overrided_parameters = overrideParameters
                    });
                }
            }

            return true;
        }

        [MenuItem("internal:Edit/Rendering/Analytics/Send VolumeProfileOverridesAnalytic ", priority = 1)]
        static void SendAnalytic()
        {
            if (!AnalyticsUtils.TryRegisterEvent(Data.k_EventName, Data.k_Version, maxEventPerHour: 1000))
                return;

            if (!TryGatherData(out var data, out var warning))
                Debug.Log(warning);

            data.ForEach(d => AnalyticsUtils.SendData(d, Data.k_EventName, Data.k_Version));
        }
    }
#endif
}
