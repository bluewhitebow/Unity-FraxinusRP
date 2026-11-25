#if UNITY_EDITOR
using UnityEditor;
#endif

#if UNITY_EDITOR && UNITY_2023_2_OR_NEWER
using System;
using UnityEngine.Analytics;
#endif

#if UNITY_EDITOR && !UNITY_2023_2_OR_NEWER
using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine.SceneManagement;
#endif

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Setup a specific render pipeline on scene loading.
    /// This need to be used with caution as it will change project configuration.
    /// </summary>
#if UNITY_EDITOR
    [ExecuteAlways]
#endif
    public class SceneRenderPipeline : MonoBehaviour
    {
#if UNITY_EDITOR
        [SerializeField] bool firstTimeCreated = true;

        /// <summary>
        /// Scriptable Render Pipeline Asset to setup on scene load.
        /// </summary>
        public RenderPipelineAsset renderPipelineAsset;

        void Awake()
        {
            if (firstTimeCreated)
            {
                renderPipelineAsset = GraphicsSettings.defaultRenderPipeline;
                firstTimeCreated = false;
            }

            //Send analytics each time to find usage in content dl on the asset store too
            SceneRenderPipelineAnalytic.SendAnalytic(this);
        }

        void OnEnable()
        {
            GraphicsSettings.defaultRenderPipeline = renderPipelineAsset;
        }


#if UNITY_2023_2_OR_NEWER
        [AnalyticInfo(eventName: "sceneRenderPipelineAssignment", vendorKey: "unity.srp", maxEventsPerHour: 10, maxNumberOfElements: 1000)]
        class SceneRenderPipelineAnalytic : IAnalytic
        {

            public SceneRenderPipelineAnalytic(string guid)
            {
                m_Data = new Data
                {
                    scene_guid = guid
                };
            }

            [System.Diagnostics.DebuggerDisplay("{scene_guid}")]
            [Serializable]
            internal struct Data : IAnalytic.IData
            {
                // Naming convention for analytics data
                public string scene_guid;
            };

            public bool TryGatherData(out IAnalytic.IData data, out Exception error)
            {
                data = m_Data;
                error = null;
                return true;
            }

            static public void SendAnalytic(SceneRenderPipeline sender)
            {
                SceneRenderPipelineAnalytic analytic = new SceneRenderPipelineAnalytic(sender.gameObject.scene.GetGUID());
                EditorAnalytics.SendAnalytic(analytic);
            }

            Data m_Data;
        }
#else
        static class SceneRenderPipelineAnalytic
        {
            public static void SendAnalytic(SceneRenderPipeline sender)
            {
                if (!EditorAnalyticsExtensions.IsEnabled)
                    return;

                const string eventName = "sceneRenderPipelineAssignment";
                const int maxEventsPerHour = 10;
                const int maxNumberOfElements = 1000;
                const string vendorKey = "unity.srp";

                if (!EditorAnalyticsExtensions.RegisterEventWithLimit(eventName, maxEventsPerHour, maxNumberOfElements, vendorKey))
                    return;

                var data = new Data
                {
                    scene_guid = SceneExtensions.GetGUID(sender.gameObject.scene)
                };
                EditorAnalyticsExtensions.SendEventWithLimit(eventName, data);
            }

            [System.Diagnostics.DebuggerDisplay("{scene_guid}")]
            [Serializable]
            struct Data
            {
                public string scene_guid;
            }

            static class SceneExtensions
            {
                static readonly PropertyInfo s_SceneGUID = typeof(Scene).GetProperty("guid", BindingFlags.NonPublic | BindingFlags.Instance);

                public static string GetGUID(Scene scene)
                {
                    if (s_SceneGUID == null)
                        return string.Empty;

                    try
                    {
                        return s_SceneGUID.GetValue(scene, null) as string ?? string.Empty;
                    }
                    catch (Exception)
                    {
                        return string.Empty;
                    }
                }
            }

            static class EditorAnalyticsExtensions
            {
                // EditorAnalytics methods return AnalyticsResult, which lives in
                // UnityEngine.UnityAnalyticsModule. That optional module is not
                // always referenced by the 2022 Core runtime assembly. Build
                // delegates through reflection and convert the enum result to int
                // (AnalyticsResult.Ok is zero) to keep the dependency optional.
                static readonly Func<string, int, int, string, int> s_RegisterEventWithLimit;
                static readonly Func<string, object, int> s_SendEventWithLimit;
                static readonly PropertyInfo s_Enabled;

                public static bool IsEnabled
                {
                    get
                    {
                        if (s_Enabled == null)
                            return false;

                        try
                        {
                            return (bool)s_Enabled.GetValue(null, null);
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }
                }

                public static bool RegisterEventWithLimit(string eventName, int maxEventsPerHour, int maxItems, string vendorKey)
                {
                    try
                    {
                        return s_RegisterEventWithLimit != null &&
                               s_RegisterEventWithLimit(eventName, maxEventsPerHour, maxItems, vendorKey) == 0;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                public static bool SendEventWithLimit(string eventName, object parameters)
                {
                    try
                    {
                        return s_SendEventWithLimit != null && s_SendEventWithLimit(eventName, parameters) == 0;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                static EditorAnalyticsExtensions()
                {
                    s_Enabled = typeof(EditorAnalytics).GetProperty("enabled", BindingFlags.Static | BindingFlags.Public);

                    var editorAnalyticsType = typeof(EditorAnalytics);
                    var registerMethod = editorAnalyticsType.GetMethod(
                        "RegisterEventWithLimit",
                        BindingFlags.Static | BindingFlags.Public,
                        null,
                        new[] { typeof(string), typeof(int), typeof(int), typeof(string) },
                        null);
                    var sendMethod = editorAnalyticsType.GetMethod(
                        "SendEventWithLimit",
                        BindingFlags.Static | BindingFlags.Public,
                        null,
                        new[] { typeof(string), typeof(object) },
                        null);

                    if (registerMethod != null)
                    {
                        var eventNameParameter = Expression.Parameter(typeof(string), "eventName");
                        var maxEventsPerHourParameter = Expression.Parameter(typeof(int), "maxEventsPerHour");
                        var maxItemsParameter = Expression.Parameter(typeof(int), "maxItems");
                        var vendorKeyParameter = Expression.Parameter(typeof(string), "vendorKey");
                        s_RegisterEventWithLimit = Expression.Lambda<Func<string, int, int, string, int>>(
                            Expression.Convert(
                                Expression.Call(registerMethod, eventNameParameter, maxEventsPerHourParameter, maxItemsParameter, vendorKeyParameter),
                                typeof(int)),
                            eventNameParameter, maxEventsPerHourParameter, maxItemsParameter, vendorKeyParameter).Compile();
                    }

                    if (sendMethod != null)
                    {
                        var eventNameParameter = Expression.Parameter(typeof(string), "eventName");
                        var parametersParameter = Expression.Parameter(typeof(object), "parameters");
                        s_SendEventWithLimit = Expression.Lambda<Func<string, object, int>>(
                            Expression.Convert(
                                Expression.Call(sendMethod, eventNameParameter, parametersParameter),
                                typeof(int)),
                            eventNameParameter, parametersParameter).Compile();
                    }
                }
            }
        }
#endif


#endif
    }
}
