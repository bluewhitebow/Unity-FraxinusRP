using JetBrains.Annotations;
using System;
using UnityEditor;
using UnityEngine.Analytics;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra.Analytics
{
    // schema = com.unity3d.data.schemas.editor.analytics.uAssetReimporterAnalytic_v2
    // taxonomy = editor.analytics.uAssetReimporterAnalytic.v2
    internal class AssetReimporterAnalytic
    {
#if UNITY_6000_0_OR_NEWER
        [AnalyticInfo(eventName: "uAssetReimporterAnalytic", vendorKey: "unity.srp", maxEventsPerHour:100, maxNumberOfElements:1000)]
        class Analytic : IAnalytic
        {
            public Analytic(double duration, uint numberOfAssets, string assetType)
            {
                using (GenericPool<Data>.Get(out var data))
                {
                    data.duration = duration;
                    data.num_assets = numberOfAssets;
                    data.asset_type = assetType;
                }
            }

            [System.Diagnostics.DebuggerDisplay("{duration} - {asset_type} - {num_assets}")]
            [Serializable]
            class Data : IAnalytic.IData
            {
                internal const string k_EventName = "";
                internal const int k_Version = 2;

                // Naming convention for analytics data
                public uint num_assets;
                public double duration;
                public string asset_type;
            }

            public bool TryGatherData(out IAnalytic.IData data, out Exception error)
            {
                data = m_Data;
                error = null;
                return true;
            }
            Data m_Data;

        }

        public static void Send<T>(double duration, uint numberOfAssets)
        {
            Analytic analytic = new Analytic(duration, numberOfAssets, typeof(T).ToString());
            EditorAnalytics.SendAnalytic(analytic);
        }
#else
        const int k_MaxEventsPerHour = 100;
        const int k_MaxNumberOfElements = 1000;
        const string k_VendorKey = "unity.srp";

        [System.Diagnostics.DebuggerDisplay("{duration} - {asset_type} - {num_assets}")]
        class Data
        {
            internal const string k_EventName = "uAssetReimporterAnalytic";
            internal const int k_Version = 2;

            public uint num_assets;
            public double duration;
            public string asset_type;
        }

        public static void Send<T>(double duration, uint numberOfAssets)
        {
            if (!EditorAnalytics.enabled ||
                EditorAnalytics.RegisterEventWithLimit(Data.k_EventName,
                    k_MaxEventsPerHour, k_MaxNumberOfElements, k_VendorKey,
                    Data.k_Version) != AnalyticsResult.Ok)
                return;

            using (GenericPool<Data>.Get(out var data))
            {
                data.duration = duration;
                data.num_assets = numberOfAssets;
                data.asset_type = typeof(T).ToString();
                EditorAnalytics.SendEventWithLimit(Data.k_EventName, data,
                    Data.k_Version);
            }
        }
#endif
    }
}
