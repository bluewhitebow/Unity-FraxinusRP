using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.Reflection;

namespace Fraxinus.GPUBatcher.Editor
{

[InitializeOnLoad]
public static class TerrainTreeSelector
{
    struct TreeCache
    {
        public Vector3 worldPos;
        public Bounds worldBounds;
        public int instanceIndex;
        public int prototypeIndex;
        public float rotation;
        public float widthScale;
        public float heightScale;
    }

    static List<TreeCache> s_cache = new List<TreeCache>();
    static Mesh[] s_protoMeshes;
    static Vector3[] s_protoScales;
    static Terrain s_cachedTerrain;
    static int s_cachedTreeCount;
    delegate bool IntersectRayMeshDelegate(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit);
    static IntersectRayMeshDelegate s_intersectRayMesh;
    static TerrainTreeProxy s_activeProxy;
    static PivotMode s_savedPivotMode;

    // track drag state
    static int s_lastHotControl;
    static bool s_wasDragging;

    // screen-space radius around handle center where tree picking is suppressed
    const float k_handleGuardRadius = 120f;

    // scratch list for GetComponentsInChildren, avoids allocation per raycast
    static readonly List<MeshFilter> s_meshFilterScratch = new List<MeshFilter>();

    // reusable scratch list for treeInstances copies, avoids allocation per edit
    static List<TreeInstance> s_treeListScratch;

    internal static List<TreeInstance> RentTreeList(TerrainData data)
    {
        if (s_treeListScratch == null)
            s_treeListScratch = new List<TreeInstance>();
        s_treeListScratch.Clear();
        s_treeListScratch.AddRange(data.treeInstances);
        return s_treeListScratch;
    }

    static TerrainTreeSelector()
    {
        if (!GPUBatcherRenderer.canSubmitDraws) return;
        // unsubscribe first to prevent duplicate handlers across domain reloads
        SceneView.duringSceneGui -= OnSceneGUI;
        Selection.selectionChanged -= OnSelectionChanged;
        EditorApplication.update -= OnEditorUpdate;
        EditorSceneManager.sceneSaving -= OnSceneSaving;
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;

        SceneView.duringSceneGui += OnSceneGUI;
        Selection.selectionChanged += OnSelectionChanged;
        EditorApplication.update += OnEditorUpdate;
        EditorSceneManager.sceneSaving += OnSceneSaving;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;

        var mi = typeof(HandleUtility).GetMethod(
            "IntersectRayMesh",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new System.Type[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4), typeof(RaycastHit).MakeByRefType() },
            null
        );

        if (mi == null)
            Debug.LogError("TerrainTreeSelector: Could not find HandleUtility.IntersectRayMesh via reflection");
        else
            s_intersectRayMesh = (IntersectRayMeshDelegate)System.Delegate.CreateDelegate(typeof(IntersectRayMeshDelegate), mi);
    }

    static bool IntersectRayMesh(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit)
    {
        if (s_intersectRayMesh == null) { hit = default; return false; }
        return s_intersectRayMesh(ray, mesh, matrix, out hit);
    }

    static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
    {
        FlushProxy();
    }

    static void OnBeforeAssemblyReload()
    {
        FlushProxy();
        s_cache.Clear();
        s_cachedTerrain = null;
        s_cachedTreeCount = -1;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
            FlushProxy();
    }

    static void ResetProxyState()
    {
        s_activeProxy = null;
        Tools.pivotMode = s_savedPivotMode;
    }

    static void FlushProxy()
    {
        if (s_activeProxy != null)
        {
            s_activeProxy.ConvertBackToTree(false);
            ResetProxyState();
        }
    }

    static void RebuildCache(Terrain terrain)
    {
        s_cache.Clear();
        s_cachedTerrain = terrain;

        TerrainData data = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;
        TreeInstance[] trees = data.treeInstances;
        TreePrototype[] protos = data.treePrototypes;  // cache, each access allocates
        s_cachedTreeCount = trees.Length;

        s_protoMeshes = new Mesh[protos.Length];
        s_protoScales = new Vector3[protos.Length];
        for (int p = 0; p < protos.Length; p++)
        {
            MeshFilter mf = GetLOD0MeshFilter(protos[p].prefab);
            s_protoMeshes[p] = mf != null ? mf.sharedMesh : null;
            s_protoScales[p] = protos[p].prefab != null ? protos[p].prefab.transform.localScale : Vector3.one;
        }

        for (int i = 0; i < trees.Length; i++)
        {
            TreeInstance tree = trees[i];
            Mesh mesh = tree.prototypeIndex >= 0 && tree.prototypeIndex < s_protoMeshes.Length
                ? s_protoMeshes[tree.prototypeIndex] : null;
            if (mesh == null) continue;

            Vector3 worldPos = Vector3.Scale(tree.position, data.size) + terrainPos;
            Bounds localBounds = mesh.bounds;

            // prefab root scale matches the registerer's rendered scale (widthScale * prefab localScale)
            Vector3 scale = Vector3.Scale(new Vector3(tree.widthScale, tree.heightScale, tree.widthScale),
                s_protoScales[tree.prototypeIndex]);
            Vector3 scaledCenter = Vector3.Scale(localBounds.center, scale) + worldPos;
            Vector3 scaledExtents = Vector3.Scale(localBounds.extents, scale);

            s_cache.Add(new TreeCache
            {
                worldPos = worldPos,
                worldBounds = new Bounds(scaledCenter, scaledExtents * 2f),
                instanceIndex = i,
                prototypeIndex = tree.prototypeIndex,
                rotation = tree.rotation,
                widthScale = tree.widthScale,
                heightScale = tree.heightScale
            });
        }
    }

    static void EnsureCache(Terrain terrain)
    {
        if (s_cachedTerrain != terrain || s_cachedTreeCount != terrain.terrainData.treeInstanceCount)
            RebuildCache(terrain);
    }

    static Renderer[] GetLOD0Renderers(GameObject prefab)
    {
        LODGroup lodGroup = prefab.GetComponent<LODGroup>();
        if (lodGroup != null)
        {
            LOD[] lods = lodGroup.GetLODs();
            if (lods.Length > 0)
                return lods[0].renderers;
        }
        return prefab.GetComponentsInChildren<Renderer>();
    }

    static MeshFilter GetLOD0MeshFilter(GameObject prefab)
    {
        Renderer[] renderers = GetLOD0Renderers(prefab);
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;
            MeshFilter mf = renderer.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) return mf;
        }
        return null;
    }

    static void OnSelectionChanged()
    {
        if (s_activeProxy == null) return;

        if (Selection.activeGameObject == null || Selection.activeGameObject.GetComponent<TerrainTreeProxy>() == null)
        {
            s_activeProxy.ConvertBackToTree(true);
            ResetProxyState();
        }
    }

    static void OnEditorUpdate()
    {
        if (s_activeProxy == null) return;

        // enforce pivot mode, user can't switch to center while proxy is active
        if (Tools.pivotMode != PivotMode.Pivot)
            Tools.pivotMode = PivotMode.Pivot;

        Transform t = s_activeProxy.transform;

        // enforce Y-only rotation
        Vector3 euler = t.eulerAngles;
        if (Mathf.Abs(euler.x) > 0.01f || Mathf.Abs(euler.z) > 0.01f)
        {
            t.rotation = Quaternion.Euler(0f, euler.y, 0f);
        }

        // enforce uniform XZ scale
        Vector3 scale = t.localScale;
        float avgXZ = (scale.x + scale.z) * 0.5f;
        if (Mathf.Abs(scale.x - scale.z) > 0.01f)
        {
            t.localScale = new Vector3(avgXZ, scale.y, avgXZ);
        }
    }

    static void OnSceneGUI(SceneView sceneView)
    {
        Event e = Event.current;

        // detect drag completion on the proxy
        if (s_activeProxy != null)
        {
            int hotControl = GUIUtility.hotControl;

            if (s_lastHotControl != 0 && hotControl == 0 && s_wasDragging)
            {
                // drag just ended, write tree back to terrain
                s_activeProxy.WriteToTerrain();
                s_wasDragging = false;
            }

            if (hotControl != 0 && Selection.activeGameObject == s_activeProxy.gameObject)
            {
                s_wasDragging = true;
            }

            s_lastHotControl = hotControl;
        }

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            // check config toggle
            var config = GPUBatcherConfig.instance;
            if (config != null && !config.enableTerrainTreeSelection) return;

            // don't interfere if Unity has GUI control
            if (GUIUtility.hotControl != 0) return;

            // skip if clicking near a transform handle gizmo
            if (IsNearHandle(e.mousePosition)) return;

            if (Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<TerrainTreeProxy>() != null)
                return;

            // only allow tree selection when transform tools are active
            if (!ShouldAllowTreeSelection())
                return;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null) return;

            EnsureCache(terrain);

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            int hitIndex = RaycastTreeMeshes(ray, e.mousePosition);

            if (hitIndex >= 0)
            {
                if (s_activeProxy != null)
                {
                    s_activeProxy.ConvertBackToTree(true);
                    ResetProxyState();
                }

                SelectTreeAsProxy(terrain, hitIndex);

                e.Use();
                sceneView.Repaint();
            }
        }

        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace))
        {
            if (s_activeProxy != null && Selection.activeGameObject == s_activeProxy.gameObject)
            {
                s_activeProxy.DeleteTree();
                ResetProxyState();
                e.Use();
                sceneView.Repaint();
            }
        }
    }

    static void SelectTreeAsProxy(Terrain terrain, int treeIndex)
    {
        TerrainData data = terrain.terrainData;
        TreeInstance tree = data.treeInstances[treeIndex];
        TreePrototype proto = data.treePrototypes[tree.prototypeIndex];

        Vector3 worldPos = Vector3.Scale(tree.position, data.size) + terrain.transform.position;
        Quaternion rotation = Quaternion.Euler(0f, tree.rotation * Mathf.Rad2Deg, 0f);
        Vector3 scale = new Vector3(tree.widthScale, tree.heightScale, tree.widthScale);

        // remove tree from terrain silently
        var trees = RentTreeList(data);
        trees.RemoveAt(treeIndex);
        data.treeInstances = trees.ToArray();
        EditorUtility.ClearDirty(data);
        InvalidateCache();

        // spawn proxy
        GameObject proxy = (GameObject)PrefabUtility.InstantiatePrefab(proto.prefab);
        proxy.name = $"[Tree Proxy] {proto.prefab.name}";
        proxy.hideFlags = HideFlags.DontSave;
        proxy.transform.position = worldPos;
        proxy.transform.rotation = rotation;
        proxy.transform.localScale = scale;

        foreach (Transform child in proxy.GetComponentsInChildren<Transform>())
            child.gameObject.hideFlags = HideFlags.DontSave;

        LODGroup lod = proxy.GetComponent<LODGroup>();
        if (lod != null)
            lod.ForceLOD(0);

        TerrainTreeProxy proxyComp = proxy.AddComponent<TerrainTreeProxy>();
        proxyComp.terrain = terrain;
        proxyComp.prototypeIndex = tree.prototypeIndex;
        proxyComp.originalColor = tree.color;
        proxyComp.originalLightmapColor = tree.lightmapColor;
        proxyComp.originalPosition = worldPos;
        proxyComp.originalRotationY = tree.rotation;
        proxyComp.originalWidthScale = tree.widthScale;
        proxyComp.originalHeightScale = tree.heightScale;
        proxyComp.terrainTreeIndex = -1; // not in terrain yet

        s_activeProxy = proxyComp;
        s_wasDragging = false;
        s_lastHotControl = 0;
        Selection.activeGameObject = proxy;
        s_savedPivotMode = Tools.pivotMode;
        Tools.pivotMode = PivotMode.Pivot;

        if (GPUBatcherConfig.instance != null && GPUBatcherConfig.instance.enableDebugLogging)
            Debug.Log($"Selected tree #{treeIndex}: {proto.prefab.name}");
    }

    static void InvalidateCache()
    {
        s_cachedTreeCount = -1;
    }

    static bool ShouldAllowTreeSelection()
    {
        // only allow tree selection when standard transform tools are active
        // prevents interference with terrain painting tools, which set Tools.current to None
        return Tools.current == Tool.Move ||
               Tools.current == Tool.Rotate ||
               Tools.current == Tool.Scale ||
               Tools.current == Tool.Transform ||
               Tools.current == Tool.Rect;
    }

    static bool IsNearHandle(Vector2 mousePosition)
    {
        if (Selection.activeTransform == null) return false;
        Vector2 handleScreen = HandleUtility.WorldToGUIPoint(Tools.handlePosition);
        return Vector2.Distance(handleScreen, mousePosition) < k_handleGuardRadius;
    }

    static int RaycastTreeMeshes(Ray ray, Vector2 mousePosition)
    {
        // get what Unity would normally pick
        GameObject pickedObject = HandleUtility.PickGameObject(mousePosition, false);

        // calculate distance to picked object if it's a non-terrain object
        float pickedObjectDistance = float.MaxValue;
        if (pickedObject != null && pickedObject.GetComponent<Terrain>() == null)
        {
            // get the mesh from the picked object and raycast against it for actual distance
            s_meshFilterScratch.Clear();
            pickedObject.GetComponentsInChildren(s_meshFilterScratch);
            foreach (MeshFilter mf in s_meshFilterScratch)
            {
                if (mf == null || mf.sharedMesh == null) continue;

                Matrix4x4 worldToLocal = mf.transform.worldToLocalMatrix;
                Ray localRay = new Ray(
                    worldToLocal.MultiplyPoint3x4(ray.origin),
                    worldToLocal.MultiplyVector(ray.direction).normalized
                );

                if (IntersectRayMesh(localRay, mf.sharedMesh, Matrix4x4.identity, out RaycastHit hit))
                {
                    Vector3 worldHitPoint = mf.transform.localToWorldMatrix.MultiplyPoint3x4(localRay.GetPoint(hit.distance));
                    float dist = Vector3.Distance(ray.origin, worldHitPoint);
                    if (dist < pickedObjectDistance)
                        pickedObjectDistance = dist;
                }
            }
        }

        float closestDist = float.MaxValue;
        int closestIndex = -1;

        for (int i = 0; i < s_cache.Count; i++)
        {
            TreeCache cached = s_cache[i];

            if (!cached.worldBounds.IntersectRay(ray, out float boundsDist))
                continue;

            if (boundsDist > closestDist)
                continue;

            // skip if bounds are farther than the picked object
            if (boundsDist >= pickedObjectDistance)
                continue;

            Mesh mesh = cached.prototypeIndex >= 0 && cached.prototypeIndex < s_protoMeshes.Length
                ? s_protoMeshes[cached.prototypeIndex] : null;
            if (mesh == null) continue;

            Matrix4x4 treeMatrix = Matrix4x4.TRS(
                cached.worldPos,
                Quaternion.Euler(0f, cached.rotation * Mathf.Rad2Deg, 0f),
                Vector3.Scale(new Vector3(cached.widthScale, cached.heightScale, cached.widthScale),
                    s_protoScales[cached.prototypeIndex])
            );

            Matrix4x4 inv = treeMatrix.inverse;
            Ray localRay = new Ray(
                inv.MultiplyPoint3x4(ray.origin),
                inv.MultiplyVector(ray.direction).normalized
            );

            if (IntersectRayMesh(localRay, mesh, Matrix4x4.identity, out RaycastHit meshHit))
            {
                Vector3 worldHitPoint = treeMatrix.MultiplyPoint3x4(localRay.GetPoint(meshHit.distance));
                float worldDist = Vector3.Distance(ray.origin, worldHitPoint);

                // only register tree hit if it's closer than the picked object
                if (worldDist < closestDist && worldDist < pickedObjectDistance)
                {
                    closestDist = worldDist;
                    closestIndex = cached.instanceIndex;
                }
            }
        }

        return closestIndex;
    }
}

public class TerrainTreeProxy : MonoBehaviour
{
    [HideInInspector] public Terrain terrain;
    [HideInInspector] public int prototypeIndex;
    [HideInInspector] public Color32 originalColor;
    [HideInInspector] public Color32 originalLightmapColor;
    [HideInInspector] public Vector3 originalPosition;
    [HideInInspector] public float originalRotationY;
    [HideInInspector] public float originalWidthScale;
    [HideInInspector] public float originalHeightScale;
    [HideInInspector] public int terrainTreeIndex; // -1 if not written yet

    TreeInstance BuildTreeInstance()
    {
        Vector3 local = transform.position - terrain.transform.position;
        Vector3 size = terrain.terrainData.size;

        return new TreeInstance
        {
            position = new Vector3(local.x / size.x, local.y / size.y, local.z / size.z),
            rotation = transform.eulerAngles.y * Mathf.Deg2Rad,
            widthScale = (transform.localScale.x + transform.localScale.z) * 0.5f,
            heightScale = transform.localScale.y,
            prototypeIndex = prototypeIndex,
            color = originalColor,
            lightmapColor = originalLightmapColor
        };
    }

    public bool WasModified()
    {
        float avgXZ = (transform.localScale.x + transform.localScale.z) * 0.5f;
        return Vector3.Distance(transform.position, originalPosition) > 0.001f
            || Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, originalRotationY * Mathf.Rad2Deg)) > 0.06f
            || Mathf.Abs(avgXZ - originalWidthScale) > 0.001f
            || Mathf.Abs(transform.localScale.y - originalHeightScale) > 0.001f;
    }

    // write current transform to terrain data, called when a drag finishes
    public void WriteToTerrain()
    {
        if (terrain == null) return;

        TerrainData data = terrain.terrainData;
        TreeInstance newTree = BuildTreeInstance();

        Undo.RegisterCompleteObjectUndo(data, "Modify Terrain Tree");

        var trees = TerrainTreeSelector.RentTreeList(data);

        if (terrainTreeIndex >= 0 && terrainTreeIndex < trees.Count)
        {
            trees[terrainTreeIndex] = newTree;
        }
        else
        {
            // first write, append and store index
            terrainTreeIndex = trees.Count;
            trees.Add(newTree);
        }

        data.treeInstances = trees.ToArray();
    }

    // ensures final state is in terrain then destroys proxy, called on deselect
    public void ConvertBackToTree(bool allowDirty)
    {
        if (terrain == null) return;

        TerrainData data = terrain.terrainData;
        TreeInstance newTree = BuildTreeInstance();
        bool modified = WasModified();

        var trees = TerrainTreeSelector.RentTreeList(data);

        if (terrainTreeIndex >= 0 && terrainTreeIndex < trees.Count)
        {
            // update existing entry with final position
            if (modified && allowDirty)
                Undo.RegisterCompleteObjectUndo(data, "Modify Terrain Tree");

            trees[terrainTreeIndex] = newTree;
        }
        else
        {
            // never written, add it
            if (modified && allowDirty)
                Undo.RegisterCompleteObjectUndo(data, "Modify Terrain Tree");

            trees.Add(newTree);
        }

        data.treeInstances = trees.ToArray();

        if (!modified || !allowDirty)
            EditorUtility.ClearDirty(data);

        DestroyImmediate(gameObject);
    }

    public void DeleteTree()
    {
        if (terrain != null)
        {
            TerrainData data = terrain.terrainData;
            Undo.RegisterCompleteObjectUndo(data, "Delete Terrain Tree");

            if (terrainTreeIndex >= 0 && terrainTreeIndex < data.treeInstanceCount)
            {
                var trees = TerrainTreeSelector.RentTreeList(data);
                trees.RemoveAt(terrainTreeIndex);
                data.treeInstances = trees.ToArray();
            }
        }

        if (GPUBatcherConfig.instance != null && GPUBatcherConfig.instance.enableDebugLogging)
            Debug.Log($"Deleted terrain tree: {gameObject.name}");
        DestroyImmediate(gameObject);
    }
}

[CustomEditor(typeof(TerrainTreeProxy))]
internal class TerrainTreeProxyEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        TerrainTreeProxy proxy = (TerrainTreeProxy)target;

        EditorGUILayout.HelpBox(
            "Terrain Tree Proxy\n\n" +
            "This is a temporary GameObject representing a terrain tree.\n" +
            "Click elsewhere or select another object to place it back.\n\n" +
            "• Rotation is locked to Y-axis\n" +
            "• X/Z scale are linked (widthScale)\n" +
            "• Press Delete to remove the tree",
            MessageType.Info
        );

        EditorGUILayout.Space();

        if (proxy.terrain != null)
        {
            TerrainData data = proxy.terrain.terrainData;
            var treeProtos = data.treePrototypes;
            if (proxy.prototypeIndex < treeProtos.Length)
            {
                TreePrototype proto = treeProtos[proxy.prototypeIndex];
                EditorGUILayout.LabelField("Prefab", proto.prefab.name);
            }
            EditorGUILayout.LabelField("Terrain", proxy.terrain.name);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Y Rotation", $"{proxy.transform.eulerAngles.y:F1}°");
        EditorGUILayout.LabelField("Width Scale", $"{(proxy.transform.localScale.x + proxy.transform.localScale.z) * 0.5f:F2}");
        EditorGUILayout.LabelField("Height Scale", $"{proxy.transform.localScale.y:F2}");

        EditorGUILayout.Space();
        GUI.color = proxy.WasModified() ? Color.yellow : Color.green;
        EditorGUILayout.LabelField("Status", proxy.WasModified() ? "Modified" : "Unchanged");
        GUI.color = Color.white;
    }
}

} // namespace Fraxinus.GPUBatcher.Editor
