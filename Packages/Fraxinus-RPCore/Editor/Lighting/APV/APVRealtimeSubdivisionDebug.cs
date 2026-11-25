using System.Collections;
using System.Linq;
using UnityEditor;

namespace UnityEngine.Rendering
{
    [InitializeOnLoad]
    internal static class APVRealtimeSubdivisionDebug
    {
        static double s_LastSubdivisionTime;
        static double s_LastRefreshTime;
        static IEnumerator s_CurrentSubdivision;

        static APVRealtimeSubdivisionDebug()
        {
            EditorApplication.update -= UpdateRealtimeSubdivisionDebug;
            EditorApplication.update += UpdateRealtimeSubdivisionDebug;
        }

        static void UpdateRealtimeSubdivisionDebug()
        {
            // Do not resolve the public compatibility facade here.  On
            // Unity 2022 that first access discovers APV backends by
            // scanning every loaded assembly and constructing them.  The
            // editor update callback runs even when no probe volume is
            // active, so that lookup used to put the reflection scan on
            // the first frame of every empty scene.  An initialized
            // provider context is the only state in which this debug
            // update can do useful work.
            var context = ProbeReferenceVolumeProvider.context;
            if (context == null || context.volume == null)
                return;

            var apv = context.volume as APVProbeReferenceVolume;
            if (apv == null)
                return;

            var debugDisplay = apv.probeVolumeDebug;
            if (debugDisplay == null || !debugDisplay.realtimeSubdivision)
                return;

            // Avoid killing the GPU when Unity is in background and runInBackground is disabled
            if (!Application.runInBackground && !UnityEditorInternal.InternalEditorUtility.isApplicationActive)
                return;

            // update is called 200 times per second so we bring down the update rate to 60hz to avoid overloading the GPU
            if (Time.realtimeSinceStartupAsDouble - s_LastRefreshTime < 1.0f / 60.0f)
                return;
            s_LastRefreshTime = Time.realtimeSinceStartupAsDouble;

            if (Time.realtimeSinceStartupAsDouble - s_LastSubdivisionTime > debugDisplay.subdivisionDelayInSeconds)
            {
                var probeVolume = GameObject.FindFirstObjectByType<ProbeVolume>();
                if (probeVolume == null || !probeVolume.isActiveAndEnabled)
                    return;

                var profile = ProbeVolumeBakingSet.GetBakingSetForScene(probeVolume.gameObject.scene);
                if (profile == null)
                    return;

                if (s_CurrentSubdivision == null)
                {
                    // Start a new Subdivision
                    s_CurrentSubdivision = Subdivide();
                }

                // Step the subdivision with the amount of cell per frame in debug menu
                int updatePerFrame = debugDisplay.subdivisionCellUpdatePerFrame;
                // From simplification level 5 and higher, the cost of calculating one cell is very high, so we adjust that number.
                if (profile.simplificationLevels > 4)
                    updatePerFrame = (int)Mathf.Max(1, updatePerFrame / Mathf.Pow(9, profile.simplificationLevels - 4));
                for (int i = 0; i < updatePerFrame; i++)
                {
                    if (!s_CurrentSubdivision.MoveNext())
                    {
                        s_LastSubdivisionTime = Time.realtimeSinceStartupAsDouble;
                        s_CurrentSubdivision = null;
                        break;
                    }
                }

                IEnumerator Subdivide()
                {
                    var ctx = new ProbeSubdivisionContext();
                    ctx.Initialize(profile, profile.layout);
                    var contributors = GIContributors.Find(GIContributors.ContributorFilter.All);

                    // Cull all the cells that are not visible (we don't need them for realtime debug)
                    ctx.cells.RemoveAll(c =>
                    {
                        return probeVolume.ShouldCullCell(c.position);
                    });

                    Camera activeCamera = Camera.current ?? SceneView.lastActiveSceneView?.camera;

                    // Sort cells by camera distance to compute the closest cells first
                    if (activeCamera != null)
                    {
                        var cameraPos = activeCamera.transform.position;
                        ctx.cells.Sort((c1, c2) =>
                        {
                            float c1Distance = Vector3.Distance(cameraPos, c1.bounds.center);
                            float c2Distance = Vector3.Distance(cameraPos, c2.bounds.center);

                            return c1Distance.CompareTo(c2Distance);
                        });
                    }

                    // Progressively update cells:
                    var cells = ctx.cells.ToList();

                    // Remove all the cells that was not updated to prevent ghosting
                    foreach (var cellBounds in apv.realtimeSubdivisionInfo.Keys.ToList())
                    {
                        if (!cells.Any(c => c.bounds.Equals(cellBounds)))
                            apv.realtimeSubdivisionInfo.Remove(cellBounds);
                    }

                    // Subdivide visible cells
                    foreach (var cell in cells)
                    {
                        // Override the cell list to only compute one cell
                        ctx.cells.Clear();
                        ctx.cells.Add(cell);

                        bool canceledByUser = false;
                        var result = ProbeVolumePlacement.BakeBricks(ctx, contributors, ref canceledByUser);

                        if (result.cells.Count != 0)
                            apv.realtimeSubdivisionInfo[cell.bounds] = result.cells[0].bricks;
                        else
                            apv.realtimeSubdivisionInfo.Remove(cell.bounds);

                        yield return null;
                    }

                    yield break;
                }
            }
        }
    }

}
