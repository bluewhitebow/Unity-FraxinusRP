using System;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.Analytics
{
#if UNITY_6000_0_OR_NEWER
    // Unity 6 analytics implementation.
    internal class VolumePriorityUsageAnalytic
    {
        [AnalyticInfo(eventName: "uVolumePriorityUsageAnalyticData", version: 2, vendorKey: "unity.srp")]
        class Analytic : IAnalytic
        {
            public Analytic(Volume volume, string guid)
            {
                using (GenericPool<Data>.Get(out var data))
                {
                    data.volume_name = Hash128.Compute(volume.name).ToString();
                    data.scene_name = guid;
                    data.priority = volume.priority;
                    m_Data = data;
                }
            }

            [System.Diagnostics.DebuggerDisplay("{volume_name} - {scene_name} - {priority}")]
            [Serializable]
            class Data : IAnalytic.IData
            {
                public string volume_name;
                public string scene_name;
                public float priority;
            }

            public bool TryGatherData(out IAnalytic.IData data, out Exception error)
            {
                data = m_Data;
                error = null;
                return true;
            }

            Data m_Data;
        };

        public static void Send(Volume volume)
        {
            if (volume == null)
                return;

            var sceneGUID = EditorSceneManager.GetActiveScene().GetGUID();
            GUID guid = new GUID(sceneGUID);
            if (guid.Empty())
                return;

            AnalyticsUtils.SendData(new Analytic(volume, sceneGUID));
        }
    }
#else
    // Unity 2022 legacy EditorAnalytics implementation.
    internal class VolumePriorityUsageAnalytic
    {
        [System.Diagnostics.DebuggerDisplay("{volume_name} - {scene_name} - {priority}")]
        class Data
        {
            internal const string k_EventName = "uVolumePriorityUsageAnalyticData";
            internal const int k_Version = 2;

            public string volume_name;
            public string scene_name;
            public float priority;
        }

        public static void Send(Volume volume)
        {
            if (volume == null || !AnalyticsUtils.TryRegisterEvent(Data.k_EventName, Data.k_Version))
                return;

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
                return;

            using (GenericPool<Data>.Get(out var data))
            {
                data.volume_name = Hash128.Compute(volume.name).ToString();
                data.scene_name = AssetDatabase.AssetPathToGUID(scene.path);
                data.priority = volume.priority;
                AnalyticsUtils.SendData(data, Data.k_EventName, Data.k_Version);
            }
        }
    }
#endif
}
