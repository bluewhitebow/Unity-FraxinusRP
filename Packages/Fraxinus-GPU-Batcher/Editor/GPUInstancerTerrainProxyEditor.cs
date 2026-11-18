using Fraxinus.GPUBatcher;
using UnityEditor;
using UnityEngine;
using UnityEditorBase = UnityEditor.Editor;
using System.Collections.Generic;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUInstancerTerrainProxy))]
    public class GPUInstancerTerrainProxyEditor : UnityEditorBase
    {
        private GPUInstancerTerrainProxy terrainProxy;

        public override void OnInspectorGUI()
        {
            terrainProxy = (GPUInstancerTerrainProxy)target;

            GPUInstancerProjectSettings settings = GPUInstancerProfileEditorUtility.GetProjectSettings();
            DrawProfileBinding("Detail Profile", true, terrainProxy.detailProfileId, settings);
            DrawProfileBinding("Tree Profile", false, terrainProxy.treeProfileId, settings);
            EditorGUILayout.HelpBox(GPUInstancerEditorConstants.HELPTEXT_terrainProxyWarning, MessageType.Warning);
        }

        private void DrawProfileBinding(string label, bool detail, string currentId, GPUInstancerProjectSettings settings)
        {
            List<GPUInstancerProfile> profiles = new List<GPUInstancerProfile>();
            if (settings != null && settings.runtimeConfig != null && settings.runtimeConfig.profiles != null)
            {
                foreach (GPUInstancerProfile profile in settings.runtimeConfig.profiles)
                {
                    if (profile == null)
                        continue;
                    if (detail && profile is GPUInstancerDetailProfile)
                        profiles.Add(profile);
                    else if (!detail && profile is GPUInstancerTreeProfile)
                        profiles.Add(profile);
                }
            }

            string[] options = new string[profiles.Count + 1];
            options[0] = "None";
            int selected = 0;
            for (int i = 0; i < profiles.Count; i++)
            {
                options[i + 1] = profiles[i].displayName + (profiles[i].enabled ? string.Empty : " (Disabled)");
                if (!string.IsNullOrEmpty(currentId) && profiles[i].id == currentId)
                    selected = i + 1;
            }

            EditorGUILayout.BeginHorizontal();
            int newSelected = EditorGUILayout.Popup(label, selected, options);
            if (newSelected != selected)
            {
                SetProfileBinding(newSelected == 0 ? null : profiles[newSelected - 1], detail);
            }
            if (GUILayout.Button("+", GUILayout.Width(24f)))
            {
                GPUInstancerProfile profile = detail
                    ? GPUInstancerProfileEditorUtility.AddDetailProfile()
                    : GPUInstancerProfileEditorUtility.AddTreeProfile();
                SetProfileBinding(profile, detail);
                GPUInstancerProfileEditorUtility.SelectProfile(profile);
            }
            EditorGUILayout.EndHorizontal();

            if (selected > 0 && profiles[selected - 1] != null)
            {
                GPUInstancerProfile profile = profiles[selected - 1];
                if (GUILayout.Button("Go To " + profile.displayName + " Profile"))
                    GPUInstancerProfileEditorUtility.SelectProfile(profile);
            }
            else if (!string.IsNullOrEmpty(currentId))
            {
                EditorGUILayout.HelpBox("The referenced profile ID is missing from Project Settings.", MessageType.Warning);
            }
        }

        private void SetProfileBinding(GPUInstancerProfile profile, bool detail)
        {
            // A preview manager can remain alive while the inspector edits a
            // Proxy (editor mode does not invoke OnDisable/OnEnable). Detach
            // only the channel being replaced before changing its stable ID;
            // the sibling Tree/Detail channel must keep rendering.
            GPUInstancerTerrainBinder.Unbind(terrainProxy, detail);

            Undo.RecordObject(terrainProxy, "Change GPU Instancer terrain profile");
            terrainProxy.SetProfileId(profile, detail);

            EditorUtility.SetDirty(terrainProxy);
            if (Application.isPlaying)
                GPUInstancerTerrainBinder.Bind(terrainProxy);
        }
    }
}
