using System;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.Analytics
{
#if UNITY_6000_0_OR_NEWER
    // Unity 6 analytics implementation.
    internal class VolumeProfileUsageAnalytic
    {
        [AnalyticInfo(eventName: "uVolumeProfileUsageAnalytic", version: 4, vendorKey: "unity.srp")]
        class Analytic : IAnalytic
        {
            public Analytic(Volume volume, VolumeProfile volumeProfile)
            {
                using (GenericPool<Data>.Get(out var data))
                {
                    data.volume_name = Hash128.Compute(volume.name).ToString();
                    data.scene_name = EditorSceneManager.GetActiveScene().GetGUID();
                    data.volume_profile_asset_guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(volumeProfile.GetInstanceID()));
                    m_Data = data;
                }
            }

            [System.Diagnostics.DebuggerDisplay("{volume_name} - {scene_name}- {volume_profile_asset_guid}")]
            [Serializable]
            class Data : IAnalytic.IData
            {
                public string volume_name;
                public string scene_name;
                public string volume_profile_asset_guid;
            }

            public bool TryGatherData(out IAnalytic.IData data, out Exception error)
            {
                data = m_Data;
                error = null;
                return true;
            }

            Data m_Data;
        };

        public static void Send(Volume volume, VolumeProfile volumeProfile)
        {
            if (volume == null || volumeProfile == null)
                return;

            AnalyticsUtils.SendData(new Analytic(volume, volumeProfile));
        }
    }
#else
    // Unity 2022 legacy EditorAnalytics implementation.
    internal class VolumeProfileUsageAnalytic
    {
        [System.Diagnostics.DebuggerDisplay("{volume_name} - {scene_name}- {volume_profile_asset_guid}")]
        class Data
        {
            internal const string k_EventName = "uVolumeProfileUsageAnalytic";
            internal const int k_Version = 4;

            public string volume_name;
            public string scene_name;
            public string volume_profile_asset_guid;
        }

        public static void Send(Volume volume, VolumeProfile volumeProfile)
        {
            if (volume == null || volumeProfile == null ||
                !AnalyticsUtils.TryRegisterEvent(Data.k_EventName, Data.k_Version))
                return;

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
                return;

            using (GenericPool<Data>.Get(out var data))
            {
                data.volume_name = Hash128.Compute(volume.name).ToString();
                data.scene_name = AssetDatabase.AssetPathToGUID(scene.path);
                data.volume_profile_asset_guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(volumeProfile.GetInstanceID()));
                AnalyticsUtils.SendData(data, Data.k_EventName, Data.k_Version);
            }
        }
    }
#endif
}
