using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Can be added to a scene to displays LOD levels with different colors for debugging purposes. Only works if the prototype's shader has a _Color property in it.
    /// </summary>
    public class GPUInstancerLODColorDebugger : MonoBehaviour
    {
        [System.NonSerialized]
        public GPUInstancerManager gPUIManager;
        public List<Color> lODColors = new List<Color> { Color.red, Color.blue, Color.yellow  };

        private Dictionary<Material, Color> _previousColors;

        void OnEnable()
        {
            StartCoroutine(ColorLODs());
        }

        void OnDisable()
        {
            if (IsManagerUsable(gPUIManager))
                ResetColors();
        }

        // Managers are process-local runtime objects and cannot be serialized
        // on this component. Resolve the first active channel unless code has
        // explicitly assigned one for this session.

        IEnumerator ColorLODs()
        {
            while (!TryResolveManager() || !gPUIManager.isInitialized)
                yield return null;
            ChangeLODColors();
        }

        private bool TryResolveManager()
        {
            if (IsManagerUsable(gPUIManager))
                return true;

            gPUIManager = null;
            IReadOnlyList<GPUInstancerManager> managers = GPUInstancerManagerHub.activeManagers;
            for (int i = 0; i < managers.Count; i++)
            {
                if (!IsManagerUsable(managers[i]))
                    continue;
                gPUIManager = managers[i];
                return true;
            }
            return false;
        }

        private static bool IsManagerUsable(GPUInstancerManager manager)
        {
            return manager != null && !manager.isDisposed;
        }

        public void ChangeLODColors()
        {
            if (!IsManagerUsable(gPUIManager) || gPUIManager.runtimeDataList == null || lODColors == null)
                return;
            _previousColors = new Dictionary<Material, Color>();
            foreach (GPUInstancerRuntimeData runtimeData in gPUIManager.runtimeDataList)
            {
                if (runtimeData == null || runtimeData.instanceLODs == null)
                    continue;
                for (int l = 1; l < runtimeData.instanceLODs.Count && l <= lODColors.Count; l++)
                {
                    if (runtimeData.instanceLODs[l] == null || runtimeData.instanceLODs[l].renderers == null)
                        continue;
                    for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                    {
                        if (runtimeData.instanceLODs[l].renderers[r] == null ||
                            runtimeData.instanceLODs[l].renderers[r].materials == null ||
                            runtimeData.instanceLODs[l].renderers[r].mpb == null)
                            continue;
                        for (int m = 0; m < runtimeData.instanceLODs[l].renderers[r].materials.Count; m++)
                        {
                            Material material = runtimeData.instanceLODs[l].renderers[r].materials[m];
                            if (material == null)
                                continue;
                            if (material.HasProperty("_Color"))
                            {
                                if (!_previousColors.ContainsKey(material))
                                    _previousColors.Add(material, material.color);
                                runtimeData.instanceLODs[l].renderers[r].mpb.SetColor("_Color", lODColors[l - 1]);
                            }
                            if (material.HasProperty("_BaseColor"))
                            {
                                if (!_previousColors.ContainsKey(material))
                                    _previousColors.Add(material, material.GetColor("_BaseColor"));
                                runtimeData.instanceLODs[l].renderers[r].mpb.SetColor("_BaseColor", lODColors[l - 1]);
                            }
                        }
                    }
                }
            }
        }

        void ResetColors()
        {
            if (_previousColors == null || !IsManagerUsable(gPUIManager) || gPUIManager.runtimeDataList == null || lODColors == null)
                return;

            foreach (GPUInstancerRuntimeData runtimeData in gPUIManager.runtimeDataList)
            {
                if (runtimeData == null || runtimeData.instanceLODs == null)
                    continue;
                for (int l = 1; l < runtimeData.instanceLODs.Count && l <= lODColors.Count; l++)
                {
                    if (runtimeData.instanceLODs[l] == null || runtimeData.instanceLODs[l].renderers == null)
                        continue;
                    for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                    {
                        if (runtimeData.instanceLODs[l].renderers[r] == null ||
                            runtimeData.instanceLODs[l].renderers[r].materials == null ||
                            runtimeData.instanceLODs[l].renderers[r].mpb == null)
                            continue;
                        for (int m = 0; m < runtimeData.instanceLODs[l].renderers[r].materials.Count; m++)
                        {
                            Material material = runtimeData.instanceLODs[l].renderers[r].materials[m];
                            if (material == null)
                                continue;
                            if (material.HasProperty("_Color") && _previousColors.ContainsKey(material))
                                runtimeData.instanceLODs[l].renderers[r].mpb.SetColor("_Color", _previousColors[material]);
                            if (material.HasProperty("_BaseColor") && _previousColors.ContainsKey(material))
                                runtimeData.instanceLODs[l].renderers[r].mpb.SetColor("_BaseColor", _previousColors[material]);
                        }
                    }
                }
            }
        }
    }
}
