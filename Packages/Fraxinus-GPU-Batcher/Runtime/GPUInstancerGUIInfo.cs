using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public class GPUInstancerGUIInfo : MonoBehaviour
    {
        public bool showRenderedAmount;
        public bool showPrototypesSeparate;
        public bool showPrefabManagers = true;
        public bool showDetailManagers = true;
        public bool showTreeManagers = true;

        // Keep the one-item draw helper on the component, not in a static
        // cache. GPUInstancerRuntimeData points back to its owning managed
        // Manager, so a static slot would retain an entire disposed runtime
        // graph after the debug component or scene was unloaded.
        private readonly List<GPUInstancerRuntimeData> _singleRuntimeData =
            new List<GPUInstancerRuntimeData>() { null };

        private void OnGUI()
        {
            if (GPUInstancerManagerHub.activeManagers.Count > 0)
            {
                if (GPUInstancerManager.showRenderedAmount != showRenderedAmount)
                    GPUInstancerManager.showRenderedAmount = showRenderedAmount;

                int startPos = 0;
                int enabledCount = 0;
                string name = "";

                Color oldColor = GUI.color;
                GUI.color = Color.red;

                if (showRenderedAmount)
                {
                    GUI.Label(new Rect(10, Screen.height - 30, 700, 30),
                    "SHOWING RENDERED AMOUNTS! FRAME RATE WILL DROP!");
                    startPos += 30;
                }

                foreach (GPUInstancerManager manager in GPUInstancerManagerHub.activeManagers)
                {
                    if (manager == null)
                        continue;
                    enabledCount = 0;
                    if (manager is GPUInstancerPrefabManager)
                    {
                        if (!showPrefabManagers)
                            continue;
                        name = "prefab";
                        enabledCount = ((GPUInstancerPrefabManager)manager).GetEnabledPrefabCount();
                    }
                    else if (manager is GPUInstancerTreeManager)
                    {
                        if (!showTreeManagers)
                            continue;
                        name = "tree";
                    }
                    else if (manager is GPUInstancerDetailManager)
                    {
                        if (!showDetailManagers)
                            continue;
                        name = "detail";
                    }
                    if (showPrototypesSeparate && manager.runtimeDataList != null)
                    {
                        foreach (GPUInstancerRuntimeData rd in manager.runtimeDataList)
                        {
                            if (rd == null || rd.prototype == null)
                                continue;
                            _singleRuntimeData[0] = rd;
                            DebugOnManagerGUI(rd.prototype.ToString(), _singleRuntimeData, showRenderedAmount, startPos, enabledCount);
                            _singleRuntimeData[0] = null;
                            startPos += GPUInstancerConstants.DEBUG_INFO_SIZE;
                        }
                    }
                    else
                    {
                        DebugOnManagerGUI(name, manager.runtimeDataList, showRenderedAmount, startPos, enabledCount);
                        startPos += GPUInstancerConstants.DEBUG_INFO_SIZE;
                    }
                }

                GUI.color = oldColor;
            }
        }

        private void OnDisable()
        {
            _singleRuntimeData[0] = null;
            if (showRenderedAmount)
                GPUInstancerManager.showRenderedAmount = false;
        }

        private static void DebugOnManagerGUI(string name, List<GPUInstancerRuntimeData> runtimeDataList, bool showRenderedAmount, int startPos, int enabledCount)
        {
            if (runtimeDataList == null || runtimeDataList.Count == 0)
            {
                GUI.Label(new Rect(10, Screen.height - startPos - 25, 700, 30),
                    "There are no " + name + " instance prototypes to render!");
                return;
            }

            int totalInstanceCount = 0;
            for (int i = 0; i < runtimeDataList.Count; i++)
            {
                if (runtimeDataList[i] != null)
                    totalInstanceCount += runtimeDataList[i].instanceCount;
            }

            // show instance counts
            GUI.Label(new Rect(10, Screen.height - startPos - 45, 700, 30),
                "Total " + name + " prototype count: " + runtimeDataList.Count);
            GUI.Label(new Rect(10, Screen.height - startPos - 25, 700, 30),
                "Total " + name + " instance count: " + totalInstanceCount);

            if (showRenderedAmount)
            {
                GUI.Label(new Rect(10, Screen.height - startPos - 65, 700, 30),
                    "Rendered " + name + " instance count: " + GetRenderedAmountsGUITextFromArgs(runtimeDataList));
                GUI.Label(new Rect(10, Screen.height - startPos - 85, 700, 30),
                    "Rendered Shadow " + name + " instance count: " + GetRenderedAmountsGUITextFromArgs(runtimeDataList, true));
            }

            if (enabledCount > 0)
                GUI.Label(new Rect(10, Screen.height - startPos - 105, 700, 30),
                    "Instancing disabled " + name + " instance count: " + enabledCount);
        }

        private static string GetRenderedAmountsGUITextFromArgs<T>(List<T> runtimeData, bool isShadow = false) where T : GPUInstancerRuntimeData
        {
            int totalRendered = 0;
            int maxLodCount = 1;
            for (int i = 0; i < runtimeData.Count; i++)
            {
                if (runtimeData[i] != null && runtimeData[i].instanceLODs != null &&
                    maxLodCount < runtimeData[i].instanceLODs.Count)
                    maxLodCount = runtimeData[i].instanceLODs.Count;
            }

            int[] lodCounts = new int[maxLodCount];
            for (int i = 0; i < runtimeData.Count; i++)
            {
                if (runtimeData[i] == null || runtimeData[i].instanceLODs == null)
                    continue;
                if (isShadow)
                {
                    if (runtimeData[i].shadowArgs != null && runtimeData[i].shadowArgs.Length > 0)
                        for (int lod = 0; lod < runtimeData[i].instanceLODs.Count; lod++)
                        {
                            GPUInstancerPrototypeLOD lodData = runtimeData[i].instanceLODs[lod];
                            if (lodData == null || lodData.argsBufferOffset < 0 ||
                                lodData.argsBufferOffset + 1 >= runtimeData[i].shadowArgs.Length)
                                continue;
                            lodCounts[lod] += (int)runtimeData[i].shadowArgs[lodData.argsBufferOffset + 1];
                        }
                }
                else
                {
                    if (runtimeData[i].args != null && runtimeData[i].args.Length > 0)
                        for (int lod = 0; lod < runtimeData[i].instanceLODs.Count; lod++)
                        {
                            GPUInstancerPrototypeLOD lodData = runtimeData[i].instanceLODs[lod];
                            if (lodData == null || lodData.argsBufferOffset < 0 ||
                                lodData.argsBufferOffset + 1 >= runtimeData[i].args.Length)
                                continue;
                            lodCounts[lod] += (int)runtimeData[i].args[lodData.argsBufferOffset + 1];
                        }
                }
            }
            string lodstr = "";
            for (int lod = 0; lod < lodCounts.Length; lod++)
            {
                totalRendered += lodCounts[lod];
                lodstr += "LOD" + lod + ": " + lodCounts[lod] + (lod == lodCounts.Length - 1 ? "" : ", ");
            }

            return totalRendered + " (" + lodstr + ")";
        }
    }
}
