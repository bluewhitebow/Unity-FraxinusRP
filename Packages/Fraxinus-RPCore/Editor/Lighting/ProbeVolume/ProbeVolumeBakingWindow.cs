using UnityEditor;
using UnityEngine;

namespace UnityEngine.Rendering
{
    internal sealed class ProbeVolumeBakingWindow : EditorWindow
    {
        ProbeVolumeBakingPanel m_Panel;

        [MenuItem("Window/Rendering/Probe Volumes")]
        static void OpenWindow() => Open();

        internal static ProbeVolumeBakingWindow Open()
        {
            return GetWindow<ProbeVolumeBakingWindow>("Probe Volumes");
        }

        void OnEnable()
        {
            m_Panel = new ProbeVolumeBakingPanel();
            m_Panel.OnEnable();
            titleContent = m_Panel.titleContent;
            EditorApplication.update += RepaintWhileBaking;
        }

        void OnDisable()
        {
            EditorApplication.update -= RepaintWhileBaking;
            m_Panel?.OnDisable();
            m_Panel = null;
        }

        void RepaintWhileBaking()
        {
            if (ProbeVolumeBakingProvider.isRunning)
                Repaint();
        }

        void OnGUI()
        {
            m_Panel.RefreshBackendTitle();
            if (titleContent.text != m_Panel.titleContent.text)
                titleContent = m_Panel.titleContent;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(m_Panel.titleContent, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                m_Panel.OnHeaderSettingsGUI();
            }

            m_Panel.OnGUI();

            using (new EditorGUILayout.HorizontalScope())
            {
                m_Panel.OnSummaryGUI();
                GUILayout.FlexibleSpace();
                m_Panel.OnBakeButtonGUI();
            }
        }
    }
}
