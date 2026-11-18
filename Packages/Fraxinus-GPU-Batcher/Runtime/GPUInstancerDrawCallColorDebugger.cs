using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Can be added to a scene to displays Draw Calls with different colors for debugging purposes. Only works if the prototype's shader has a _Color property in it.
    /// </summary>
    public class GPUInstancerDrawCallColorDebugger : MonoBehaviour
    {
        [System.NonSerialized]
        public GPUInstancerManager gPUIManager;
        public List<Color> drawCallColors = new List<Color> { Color.red, Color.blue, Color.yellow, Color.green, Color.magenta };
        public bool removeMainTextureWhenColored = false;

        private Dictionary<Material, Color> _previousColors;
        private Dictionary<Material, Texture> _previousTextures;

        void OnEnable()
        {
            StartCoroutine(ColorLODs());
        }

        void OnDisable()
        {
            if (IsManagerUsable(gPUIManager))
                ResetColors();
        }

        private void Reset()
        {
        }

        IEnumerator ColorLODs()
        {
            while (!TryResolveManager() || !gPUIManager.isInitialized)
                yield return null;
            ChangeDrawCallColors();
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

        public void ChangeDrawCallColors()
        {
            if (!IsManagerUsable(gPUIManager) || gPUIManager.runtimeDataList == null)
                return;
            _previousColors = new Dictionary<Material, Color>();
            if (removeMainTextureWhenColored)
                _previousTextures = new Dictionary<Material, Texture>();
            else
                _previousTextures = null;
            int drawCallCount = 0;
            foreach (GPUInstancerRuntimeData runtimeData in gPUIManager.runtimeDataList)
            {
                if (runtimeData == null || runtimeData.instanceLODs == null)
                    continue;
                for (int l = 0; l < runtimeData.instanceLODs.Count; l++)
                {
                    if (runtimeData.instanceLODs[l] == null || runtimeData.instanceLODs[l].renderers == null)
                        continue;
                    for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                    {
                        if (runtimeData.instanceLODs[l].renderers[r] == null ||
                            runtimeData.instanceLODs[l].renderers[r].materials == null)
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
                                material.color = drawCallCount < (drawCallColors == null ? 0 : drawCallColors.Count) ? drawCallColors[drawCallCount] : Random.ColorHSV();
                            }
                            if (material.HasProperty("_BaseColor"))
                            {
                                if (!_previousColors.ContainsKey(material))
                                    _previousColors.Add(material, material.GetColor("_BaseColor"));
                                material.SetColor("_BaseColor", drawCallCount < (drawCallColors == null ? 0 : drawCallColors.Count) ? drawCallColors[drawCallCount] : Random.ColorHSV());
                            }
                            if (_previousTextures != null && material.HasProperty("_MainTex"))
                            {
                                if (!_previousTextures.ContainsKey(material))
                                    _previousTextures.Add(material, material.mainTexture);
                                material.mainTexture = null;
                            }
                            if (_previousTextures != null && material.HasProperty("_BaseMap"))
                            {
                                if (!_previousTextures.ContainsKey(material))
                                    _previousTextures.Add(material, material.GetTexture("_BaseMap"));
                                material.SetTexture("_BaseMap", null);
                            }
                            if (_previousTextures != null && material.HasProperty("_BaseColorMap"))
                            {
                                if (!_previousTextures.ContainsKey(material))
                                    _previousTextures.Add(material, material.GetTexture("_BaseColorMap"));
                                material.SetTexture("_BaseColorMap", null);
                            }
                            drawCallCount++;
                        }
                    }
                }
            }
        }

        void ResetColors()
        {
            if (_previousColors == null || !IsManagerUsable(gPUIManager) || gPUIManager.runtimeDataList == null)
                return;

            foreach (GPUInstancerRuntimeData runtimeData in gPUIManager.runtimeDataList)
            {
                if (runtimeData == null || runtimeData.instanceLODs == null)
                    continue;
                for (int l = 0; l < runtimeData.instanceLODs.Count; l++)
                {
                    if (runtimeData.instanceLODs[l] == null || runtimeData.instanceLODs[l].renderers == null)
                        continue;
                    for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                    {
                        if (runtimeData.instanceLODs[l].renderers[r] == null ||
                            runtimeData.instanceLODs[l].renderers[r].materials == null)
                            continue;
                        for (int m = 0; m < runtimeData.instanceLODs[l].renderers[r].materials.Count; m++)
                        {
                            Material material = runtimeData.instanceLODs[l].renderers[r].materials[m];
                            if (material == null)
                                continue;
                            if (material.HasProperty("_Color") && _previousColors.ContainsKey(material))
                            {
                                material.color = _previousColors[material];
                            }
                            if (material.HasProperty("_BaseColor") && _previousColors.ContainsKey(material))
                            {
                                material.SetColor("_BaseColor", _previousColors[material]);
                            }
                            if (_previousTextures != null && material.HasProperty("_MainTex") && _previousTextures.ContainsKey(material))
                            {
                                material.mainTexture = _previousTextures[material];
                            }
                            if (_previousTextures != null && material.HasProperty("_BaseMap") && _previousTextures.ContainsKey(material))
                            {
                                material.SetTexture("_BaseMap", _previousTextures[material]);
                            }
                            if (_previousTextures != null && material.HasProperty("_BaseColorMap") && _previousTextures.ContainsKey(material))
                            {
                                material.SetTexture("_BaseColorMap", _previousTextures[material]);
                            }
                        }
                    }
                }
            }
        }
    }
}
