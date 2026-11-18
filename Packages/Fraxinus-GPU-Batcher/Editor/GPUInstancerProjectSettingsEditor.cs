using System;
using System.Collections.Generic;
using Fraxinus.GPUBatcher;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Fraxinus.GPUBatcher.Editor
{
    /// <summary>
    /// Inspector for the project-owned GPU Instancer settings asset.
    ///
    /// Runtime channels are managed references below <c>runtimeConfig</c>;
    /// this editor deliberately keeps the list on the one settings asset and
    /// never creates a manager asset. All channels are edited as Profiles.
    /// </summary>
    [CustomEditor(typeof(GPUInstancerProjectSettings), true)]
    public sealed class GPUInstancerProjectSettingsEditor : UnityEditor.Editor
    {
        private static string s_focusProfileId;
        private static UnityEditor.Editor s_projectSettingsEditor;
        private static Vector2 s_projectSettingsScroll;

        private SerializedProperty _runtimeConfig;
        private SerializedProperty _profiles;
        // Profile-owned managers are runtime objects.  Keep removals queued
        // while drawing the SerializedProperty tree and flush them only after
        // ApplyModifiedProperties has committed the profile edit.  Destroying
        // a manager while Unity is still iterating a managed-reference
        // property can invalidate the iterator (and, in turn, lose the edit).
        private readonly List<GPUInstancerProfile> _pendingDriverRemovals =
            new List<GPUInstancerProfile>();

        /// <summary>Requests that the next settings inspector expansion selects a profile.</summary>
        public static void FocusProfile(string profileId)
        {
            s_focusProfileId = profileId;
        }

#if UNITY_2019_1_OR_NEWER
        [SettingsProvider]
        public static SettingsProvider CreateProjectSettingsProvider()
        {
            return new SettingsProvider("Project/Graphics/Fraxinus GPU Batcher", SettingsScope.Project)
            {
                label = "Fraxinus GPU Batcher",
                guiHandler = _ =>
                {
                    GPUInstancerProjectSettings settings = GPUInstancerProfileEditorUtility.GetProjectSettings();
                    if (settings == null)
                        return;

                    UnityEditor.Editor.CreateCachedEditor(
                        settings,
                        typeof(GPUInstancerProjectSettingsEditor),
                        ref s_projectSettingsEditor);
                    if (s_projectSettingsEditor == null)
                        return;

                    s_projectSettingsScroll = EditorGUILayout.BeginScrollView(s_projectSettingsScroll);
                    s_projectSettingsEditor.OnInspectorGUI();
                    EditorGUILayout.EndScrollView();
                },
                deactivateHandler = () =>
                {
                    if (s_projectSettingsEditor != null)
                        UnityEngine.Object.DestroyImmediate(s_projectSettingsEditor);
                    s_projectSettingsEditor = null;
                    s_projectSettingsScroll = Vector2.zero;
                },
                keywords = new HashSet<string>(new[]
                {
                    "Fraxinus", "GPU", "GPUBatcher", "Instancer", "Graphics", "Compute"
                })
            };
        }
#endif

        private void OnEnable()
        {
            _runtimeConfig = serializedObject.FindProperty("runtimeConfig");
            _profiles = _runtimeConfig != null ? _runtimeConfig.FindPropertyRelative("profiles") : null;
        }

        public override void OnInspectorGUI()
        {
            _pendingDriverRemovals.Clear();
            serializedObject.Update();
            EditorGUILayout.LabelField("GPU Instancer Project Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space(2f);
            DrawPropertiesExcluding(serializedObject, "runtimeConfig");

            DrawInstancersSection();

            if (serializedObject.ApplyModifiedProperties())
                EditorUtility.SetDirty(target);

            FlushPendingDriverRemovals();
        }

        private void DrawInstancersSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Instancers", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Add Prefab", GUILayout.Width(90f)))
                    AddProfileFromInspector(typeof(GPUInstancerPrefabProfile));
                if (GUILayout.Button("Add Tree", GUILayout.Width(75f)))
                    AddProfileFromInspector(typeof(GPUInstancerTreeProfile));
                if (GUILayout.Button("Add Detail", GUILayout.Width(80f)))
                    AddProfileFromInspector(typeof(GPUInstancerDetailProfile));
            }
            EditorGUILayout.EndHorizontal();

            if (_profiles == null)
            {
                EditorGUILayout.HelpBox("Runtime profile data is unavailable. Reimport the settings asset to repair it.", MessageType.Error);
                EditorGUILayout.EndVertical();
                return;
            }

            if (_profiles.arraySize == 0)
            {
                EditorGUILayout.HelpBox("No instancer profiles have been configured.", MessageType.Info);
            }

            for (int i = 0; i < _profiles.arraySize; i++)
            {
                SerializedProperty element = _profiles.GetArrayElementAtIndex(i);
                // A managed-reference delete can invalidate the property
                // iterator immediately.  Stop this frame's traversal and let
                // the next repaint rebuild it from the committed array.
                if (DrawProfileElement(element, i))
                    break;
            }

            EditorGUILayout.EndVertical();
        }

        private bool DrawProfileElement(SerializedProperty element, int index)
        {
            GPUInstancerProfile profile = element.managedReferenceValue as GPUInstancerProfile;
            string title = profile == null
                ? "Missing Profile"
                : profile.displayName + (profile.enabled ? string.Empty : " (Disabled)");
            bool expanded = element.isExpanded;
            bool removed = false;
            bool reordered = false;

            EditorGUILayout.BeginVertical(EditorStyles.textArea);
            EditorGUILayout.BeginHorizontal();

            SerializedProperty enabled = element.FindPropertyRelative("_enabled");
            if (enabled != null)
            {
                bool wasEnabled = enabled.boolValue;
                bool nowEnabled;
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                    nowEnabled = EditorGUILayout.Toggle(wasEnabled, GUILayout.Width(18f));
                enabled.boolValue = nowEnabled;
                if (wasEnabled && !nowEnabled)
                    QueueDriverRemoval(profile);
            }

            if (GUILayout.Button(title, EditorStyles.foldout, GUILayout.MinWidth(100f)))
                element.isExpanded = !element.isExpanded;

            if (profile != null)
                EditorGUILayout.LabelField(profile.GetType().Name, EditorStyles.miniLabel, GUILayout.Width(150f));

            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(Application.isPlaying || index == 0))
            {
                if (GUILayout.Button("Up", EditorStyles.miniButtonLeft, GUILayout.Width(24f)))
                {
                    MoveProfile(index, index - 1);
                    reordered = true;
                }
            }
            using (new EditorGUI.DisabledScope(Application.isPlaying || index >= _profiles.arraySize - 1))
            {
                if (GUILayout.Button("Down", EditorStyles.miniButtonMid, GUILayout.Width(24f)))
                {
                    MoveProfile(index, index + 1);
                    reordered = true;
                }
            }
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("X", EditorStyles.miniButtonRight, GUILayout.Width(24f)))
                {
                    RemoveProfile(index);
                    removed = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!removed && !reordered && expanded)
            {
                if (profile == null)
                {
                    EditorGUILayout.PropertyField(element, GUIContent.none, true);
                }
                else
                {
                    GPUInstancerProfileDrawer.DrawProfileFields(element);
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2f);

            if (!removed && !reordered && !string.IsNullOrEmpty(s_focusProfileId) && profile != null && profile.id == s_focusProfileId)
            {
                element.isExpanded = true;
                s_focusProfileId = null;
            }

            return removed || reordered;
        }

        private void AddProfileFromInspector(Type type)
        {
            // Flush any edits made in the current frame before mutating the
            // managed-reference list directly through the config helper.
            serializedObject.ApplyModifiedProperties();
            GPUInstancerProfile profile = GPUInstancerProfileEditorUtility.AddProfile(target as GPUInstancerProjectSettings, type);
            if (profile == null)
                return;

            // Refresh the managed-reference array immediately so the new
            // profile is visible without waiting for an asset reimport.
            serializedObject.Update();
            _runtimeConfig = serializedObject.FindProperty("runtimeConfig");
            _profiles = _runtimeConfig != null ? _runtimeConfig.FindPropertyRelative("profiles") : null;
            s_focusProfileId = profile.id;
            GUIUtility.ExitGUI();
        }

        private void MoveProfile(int from, int to)
        {
            if (_profiles == null || from < 0 || to < 0 || from >= _profiles.arraySize || to >= _profiles.arraySize)
                return;
            Undo.RecordObject(target, "Reorder GPU Instancer profile");
            _profiles.MoveArrayElement(from, to);
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        private void RemoveProfile(int index)
        {
            if (_profiles == null || index < 0 || index >= _profiles.arraySize)
                return;
            GPUInstancerProfile profile = _profiles.GetArrayElementAtIndex(index).managedReferenceValue as GPUInstancerProfile;

            // A deleted Terrain Profile must not leave scene Proxies pointing
            // at a now-missing stable ID. Clear only this channel (the sibling
            // Tree/Detail binding remains intact); Undo.RecordObject calls in
            // the helper make the scene-side change recoverable together with
            // the Profile deletion.
            if (profile is GPUInstancerTreeProfile || profile is GPUInstancerDetailProfile)
            {
                GPUInstancerManager rawManager;
                GPUInstancerTerrainManager terrainManager = null;
                if (GPUInstancerDriver.TryGet(profile, out rawManager))
                    terrainManager = rawManager as GPUInstancerTerrainManager;
                GPUInstancerProfileDrawer.UnbindTerrainProfile(
                    profile, terrainManager, profile is GPUInstancerDetailProfile);
            }
            QueueDriverRemoval(profile);
            Undo.RecordObject(target, "Remove GPU Instancer profile");
            int oldSize = _profiles.arraySize;
            _profiles.DeleteArrayElementAtIndex(index);
            // Unity's managed-reference arrays can require a second delete
            // when the first operation only nulls the reference.  Only issue
            // that second delete when the array size did not change; checking
            // the next element for null can accidentally delete a neighbouring
            // profile that was already null.
            if (_profiles.arraySize == oldSize && index < _profiles.arraySize)
                _profiles.DeleteArrayElementAtIndex(index);
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        private void QueueDriverRemoval(GPUInstancerProfile profile)
        {
            if (profile != null && !_pendingDriverRemovals.Contains(profile))
                _pendingDriverRemovals.Add(profile);
        }

        private void FlushPendingDriverRemovals()
        {
            if (_pendingDriverRemovals.Count == 0)
                return;

            for (int i = 0; i < _pendingDriverRemovals.Count; i++)
            {
                GPUInstancerProfile profile = _pendingDriverRemovals[i];
                if (profile == null)
                    continue;
                GPUInstancerDriver.Remove(profile);
            }
            _pendingDriverRemovals.Clear();
        }
    }

    /// <summary>Editor-side profile creation and persistence helpers.</summary>
    public static class GPUInstancerProfileEditorUtility
    {
        public static GPUInstancerProjectSettings GetProjectSettings()
        {
            GPUInstancerProjectSettings settings = GPUInstancerConstants.projectSettings;
            if (settings == null)
                settings = GPUInstancerProjectSettings.GetDefaultGPUInstancerProjectSettings();
            return settings;
        }

        public static GPUInstancerProfile AddProfile(Type profileType, string displayName = null)
        {
            return AddProfile(GetProjectSettings(), profileType, displayName);
        }

        public static GPUInstancerProfile AddProfile(GPUInstancerProjectSettings settings, Type profileType, string displayName = null)
        {
            if (settings == null || profileType == null || !typeof(GPUInstancerProfile).IsAssignableFrom(profileType))
                return null;

            GPUInstancerProfile profile = CreateProfile(profileType);
            if (profile == null)
                return null;
            if (!string.IsNullOrWhiteSpace(displayName))
                profile.displayName = displayName;

            if (settings.runtimeConfig == null)
            {
                Debug.LogError("GPU Instancer Project Settings has no runtimeConfig. Create the settings asset from the package template.", settings);
                return null;
            }
            Undo.RecordObject(settings, "Add GPU Instancer profile");
            settings.runtimeConfig.AddProfile(profile);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            return profile;
        }

        public static T AddProfile<T>(string displayName = null) where T : GPUInstancerProfile, new()
        {
            // Preserve the requested concrete type.  Calling the convenience
            // overload that omits profileType would fall back to the default
            // Prefab profile, making AddTreeProfile/AddDetailProfile silently
            // create the wrong channel.
            GPUInstancerProjectSettings settings = GetProjectSettings();
            if (settings == null)
                return null;

            T profile = new T();
            if (!string.IsNullOrWhiteSpace(displayName))
                profile.displayName = displayName;
            if (settings.runtimeConfig == null)
            {
                Debug.LogError("GPU Instancer Project Settings has no runtimeConfig. Create the settings asset from the package template.", settings);
                return null;
            }

            Undo.RecordObject(settings, "Add GPU Instancer profile");
            settings.runtimeConfig.AddProfile(profile);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            return profile;
        }

        public static GPUInstancerProfile CreateProfile(Type profileType)
        {
            // Keep construction explicit.  Runtime code and editor menus both
            // avoid Activator/reflection-based manager or profile discovery.
            if (profileType == typeof(GPUInstancerPrefabProfile))
                return new GPUInstancerPrefabProfile();
            if (profileType == typeof(GPUInstancerTreeProfile))
                return new GPUInstancerTreeProfile();
            if (profileType == typeof(GPUInstancerDetailProfile))
                return new GPUInstancerDetailProfile();
            return null;
        }

        /// <summary>
        /// Returns true only for a GPUInstancerPrefab component that belongs
        /// to a regular/variant prefab asset.  EditorUtility.IsPersistent on
        /// its own is not sufficient here: arbitrary persistent objects (and
        /// sub-assets) can otherwise be copied into the embedded project
        /// Profile during manager synchronisation.
        /// </summary>
        internal static bool IsPersistentPrefabComponent(GPUInstancerPrefab instance)
        {
            if (instance == null || !EditorUtility.IsPersistent(instance))
                return false;

            GameObject prefabObject = instance.gameObject;
            if (prefabObject == null || !EditorUtility.IsPersistent(prefabObject))
                return false;

#if UNITY_2018_3_OR_NEWER
            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(prefabObject);
            return prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant;
#else
            // Before the prefab asset API was introduced, a persistent
            // component on a prefab asset is the strongest available signal.
            return true;
#endif
        }

        /// <summary>Checks that a prototype and its prefab source are asset-backed.</summary>
        internal static bool IsPersistentPrototypeAsset(GPUInstancerPrototype prototype)
        {
            if (prototype == null || !EditorUtility.IsPersistent(prototype))
                return false;
            return prototype.prefabObject == null || EditorUtility.IsPersistent(prototype.prefabObject);
        }

        public static GPUInstancerPrefabProfile AddPrefabProfile(string displayName = null)
        {
            return AddProfile<GPUInstancerPrefabProfile>(displayName);
        }

        public static GPUInstancerTreeProfile AddTreeProfile(string displayName = null)
        {
            return AddProfile<GPUInstancerTreeProfile>(displayName);
        }

        public static GPUInstancerDetailProfile AddDetailProfile(string displayName = null)
        {
            return AddProfile<GPUInstancerDetailProfile>(displayName);
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }

        /// <summary>
        /// Rebinds terrain prototypes when a Profile referenced an immutable
        /// TerrainSettings asset (for example the package default) and the
        /// editor copied that asset into the project. Unity's
        /// AssetDatabase.CopyAsset copies subassets, but managed profile
        /// references still point at the original subasset; leaving those
        /// references in place would make the Profile mutate a
        /// read-only package asset at edit time.  Terrain prototypes carry a
        /// stable <c>prototypeIndex</c>, so use the source/destination asset
        /// paths plus the concrete prototype type and index as the remap key.
        /// </summary>
        // Kept internal so the managed-reference Profile drawer can normalize
        // an immutable/package TerrainSettings reference before it exposes
        // controls that mutate the settings or its prototype sub-assets.
        internal static void RemapTerrainProfilePrototypes(
            GPUInstancerProfile profile,
            GPUInstancerTerrainSettings sourceTerrainSettings,
            GPUInstancerTerrainSettings destinationTerrainSettings)
        {
            if (profile == null || sourceTerrainSettings == null ||
                destinationTerrainSettings == null ||
                ReferenceEquals(sourceTerrainSettings, destinationTerrainSettings) ||
                profile.prototypeList == null || profile.prototypeList.Count == 0)
                return;

            string sourcePath = NormalizeAssetPath(AssetDatabase.GetAssetPath(sourceTerrainSettings));
            string destinationPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(destinationTerrainSettings));
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationPath) ||
                string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                return;

            bool isTreeProfile = profile is GPUInstancerTreeProfile;
            bool isDetailProfile = profile is GPUInstancerDetailProfile;
            if (!isTreeProfile && !isDetailProfile)
                return;

            UnityEngine.Object[] destinationAssets;
            destinationAssets = AssetDatabase.LoadAllAssetsAtPath(destinationPath);

            Dictionary<int, GPUInstancerPrototype> destinationByIndex =
                new Dictionary<int, GPUInstancerPrototype>();
            if (destinationAssets != null)
            {
                for (int i = 0; i < destinationAssets.Length; i++)
                {
                    GPUInstancerPrototype destinationPrototype = isTreeProfile
                        ? destinationAssets[i] as GPUInstancerTreePrototype
                        : destinationAssets[i] as GPUInstancerDetailPrototype;
                    if (destinationPrototype == null)
                        continue;

                    int prototypeIndex = isTreeProfile
                        ? ((GPUInstancerTreePrototype)destinationPrototype).prototypeIndex
                        : ((GPUInstancerDetailPrototype)destinationPrototype).prototypeIndex;
                    // A malformed TerrainSettings file can contain duplicate
                    // indices. Keep the first deterministic match and leave
                    // an unmatched source reference intact rather than
                    // silently binding it to the wrong prototype.
                    if (!destinationByIndex.ContainsKey(prototypeIndex))
                        destinationByIndex.Add(prototypeIndex, destinationPrototype);
                }
            }

            for (int i = 0; i < profile.prototypeList.Count; i++)
            {
                GPUInstancerPrototype sourcePrototype = profile.prototypeList[i];
                if (sourcePrototype == null ||
                    (isTreeProfile && !(sourcePrototype is GPUInstancerTreePrototype)) ||
                    (isDetailProfile && !(sourcePrototype is GPUInstancerDetailPrototype)))
                    continue;

                // Only remap subassets from the TerrainSettings file that was
                // copied. Profiles may also contain project-owned prototypes
                // that happen to use the same index.
                string prototypePath = NormalizeAssetPath(AssetDatabase.GetAssetPath(sourcePrototype));
                if (!string.Equals(prototypePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                int prototypeIndex = isTreeProfile
                    ? ((GPUInstancerTreePrototype)sourcePrototype).prototypeIndex
                    : ((GPUInstancerDetailPrototype)sourcePrototype).prototypeIndex;
                GPUInstancerPrototype replacement;
                if (destinationByIndex.TryGetValue(prototypeIndex, out replacement) && replacement != null)
                    profile.prototypeList[i] = replacement;
            }
        }

        // Returns the original object when it is a writable project asset and
        // otherwise creates/reuses a project-owned copy.  Profile drawers call
        // this when a Profile points at a package default; editing such a
        // reference directly would dirty a read-only package file.
        internal static GPUInstancerTerrainSettings GetPersistentTerrainSettings(
            GPUInstancerTerrainSettings terrainSettings)
        {
            if (terrainSettings == null || !EditorUtility.IsPersistent(terrainSettings))
                return null;

            string sourcePath = AssetDatabase.GetAssetPath(terrainSettings);
            if (string.IsNullOrEmpty(sourcePath))
                return null;

            bool immutable = GPUInstancerConstants.IsPackageAssetPath(sourcePath) ||
                !AssetDatabase.IsOpenForEdit(sourcePath);
            if (!immutable)
                return terrainSettings;

            // A Profile may reference the package's default terrain settings.
            // Editable Profile data must live in the project,
            // including all prototype subassets, so copy the complete asset
            // file instead of retaining the package reference.
            string sourceGuid = terrainSettings.terrainDataGUID;
            string[] existingGuids = AssetDatabase.FindAssets("t:GPUInstancerTerrainSettings");
            for (int i = 0; i < existingGuids.Length; i++)
            {
                string existingPath = AssetDatabase.GUIDToAssetPath(existingGuids[i]);
                if (string.IsNullOrEmpty(existingPath) ||
                    GPUInstancerConstants.IsPackageAssetPath(existingPath) ||
                    !AssetDatabase.IsOpenForEdit(existingPath))
                    continue;
                GPUInstancerTerrainSettings existing =
                    AssetDatabase.LoadAssetAtPath<GPUInstancerTerrainSettings>(existingPath);
                if (existing != null && existing != terrainSettings &&
                    existing.terrainDataGUID == sourceGuid)
                    return existing;
            }

            string folder = GPUInstancerConstants.GetDefaultPath() +
                GPUInstancerConstants.PROTOTYPES_TERRAIN_PATH;
            EnsureAssetFolder(folder);
            string baseName = string.IsNullOrEmpty(terrainSettings.name)
                ? "TerrainSettings"
                : terrainSettings.name;
            string destination = AssetDatabase.GenerateUniqueAssetPath(
                folder + baseName + ".asset");
            if (!AssetDatabase.CopyAsset(sourcePath, destination))
            {
                Debug.LogWarning(
                    "GPU Instancer could not copy immutable TerrainSettings asset '" +
                    sourcePath + "' into the project.");
                return null;
            }

            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
            GPUInstancerTerrainSettings copy =
                AssetDatabase.LoadAssetAtPath<GPUInstancerTerrainSettings>(destination);
            if (copy == null)
            {
                Debug.LogWarning(
                    "GPU Instancer copied TerrainSettings asset but could not load the writable copy: " +
                    destination);
            }
            return copy;
        }

        private static void EnsureAssetFolder(string folder)
        {
            string normalized = folder.Replace('\\', '/').TrimEnd('/');
            string[] parts = normalized.Split('/');
            string current = parts.Length > 0 ? parts[0] : string.Empty;
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        public static void SelectProfile(GPUInstancerProfile profile)
        {
            if (profile == null)
                return;
            GPUInstancerProjectSettings settings = GetProjectSettings();
            GPUInstancerProjectSettingsEditor.FocusProfile(profile.id);
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }
    }

    /// <summary>
    /// Small managed-reference drawer used by the settings inspector.  It is
    /// intentionally generic so derived profiles automatically expose newly
    /// added serialized fields without bespoke drawer updates.
    /// </summary>
    [CustomPropertyDrawer(typeof(GPUInstancerProfile), true)]
    public sealed class GPUInstancerProfileDrawer : PropertyDrawer
    {
        // Terrain objects are scene bindings, not project configuration.  The
        // settings inspector therefore keeps the currently selected terrain in
        // an editor-only cache keyed by the stable profile id.  Nothing in this
        // dictionary is serialized into GPUInstancerProjectSettings.
        private static readonly Dictionary<string, Terrain> s_previewTerrains =
            new Dictionary<string, Terrain>(StringComparer.Ordinal);

        // Object picker state is likewise editor-session state.  Keeping the
        // profile id alongside the reference prevents a picker result from a
        // previous managed-reference instance being applied to a rehydrated
        // profile with the same inspector open.
        private static int s_prefabPickerControlId = -1;
        private static string s_prefabPickerProfileId;
        private static GPUInstancerPrefabProfile s_prefabPickerProfile;

        // These values are deliberately editor-session state.  The old
        // Manager inspectors kept foldouts, icon/text mode and prototype
        // selection on the transient Manager object; Profiles are project
        // data and must not acquire those fields just to preserve the UI.
        private static readonly Dictionary<string, bool> s_prototypeFoldouts =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> s_prototypeTextModes =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<GPUInstancerPrototype>> s_selectedPrototypes =
            new Dictionary<string, HashSet<GPUInstancerPrototype>>(StringComparer.Ordinal);

        // Simulation switches used to live on the serialized Manager inspector.
        // A Profile is project data and must not gain those editor-only fields,
        // so retain them in a per-profile editor-session bucket instead.  The
        // values are copied to the transient Driver-owned Manager whenever one
        // is available; TerrainProxy consequently observes the same switches
        // without ever dirtying GPUInstancerProjectSettings.
        private static readonly Dictionary<string, bool> s_keepSimulationLive =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> s_updateSimulation =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public static void DrawProfileFields(SerializedProperty profileProperty)
        {
            if (profileProperty == null)
                return;

            GPUInstancerProfile profile = profileProperty.managedReferenceValue as GPUInstancerProfile;
            if (profile == null)
                return;

            DrawSimulationControls(profile, profileProperty);

            // Configuration is read-only while the game is running.  The
            // runtime block below remains visible, but editing the embedded
            // managed reference in Play Mode would otherwise dirty the
            // project asset and diverge from the manager that is already
            // registered by the Driver.
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                DrawSceneSettingsBox(profileProperty);
                DrawPrototypeCollection(profileProperty, profile);

                SerializedProperty iterator = profileProperty.Copy();
                SerializedProperty end = iterator.GetEndProperty();
                bool enterChildren = true;
                int profileDepth = profileProperty.depth;

                while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
                {
                    enterChildren = false;
                    if (iterator.depth != profileDepth + 1)
                        continue;
                    if (iterator.name == "id")
                    {
                        EditorGUILayout.LabelField("Profile ID", iterator.stringValue, EditorStyles.miniLabel);
                        continue;
                    }
                    // The enabled switch is rendered once in the profile
                    // header; drawing the private backing field again produces
                    // two controls that can disagree during an edit.
                    if (iterator.name == "_enabled")
                        continue;

                    // These fields have dedicated, safe editors below.  In
                    // particular, a regular ObjectField for prefabList or a
                    // raw array for prototypeList would allow scene objects
                    // to leak into the project settings asset.  The common
                    // scene/culling fields are also drawn by
                    // DrawSceneSettingsBox; skip them here so every setting
                    // has one (and only one) control in the Profile UI.
                    if (iterator.name == "prototypeList" || iterator.name == "id")
                        continue;
                    if (iterator.name == "autoSelectCamera" ||
                        iterator.name == "isFrustumCulling" ||
                        iterator.name == "isOcclusionCulling" ||
                        iterator.name == "minCullingDistance" ||
                        iterator.name == "useFloatingOriginHandler" ||
                        iterator.name == "applyFloatingOriginRotationAndScale" ||
                        iterator.name == "layerMask" ||
                        iterator.name == "lightProbeDisabled")
                        continue;
                    if (profile is GPUInstancerPrefabProfile &&
                        (iterator.name == "prefabList" || iterator.name == "registeredPrefabs"))
                        continue;
                    if (profile is GPUInstancerTreeProfile &&
                        (iterator.name == "terrainSettings" || iterator.name == "initializeWithCoroutine"))
                        continue;
                    if (profile is GPUInstancerDetailProfile &&
                        (iterator.name == "terrainSettings" || iterator.name == "detailLayer" ||
                         iterator.name == "runInThreads"))
                        continue;
                    EditorGUILayout.PropertyField(iterator, true);
                }

                // Keep the specialised drawer names as explicit dispatch
                // points for channel-specific affordances.
                if (profile is GPUInstancerPrefabProfile)
                    GPUInstancerPrefabProfileDrawer.Draw(profileProperty);
                else if (profile is GPUInstancerTreeProfile)
                    GPUInstancerTreeProfileDrawer.Draw(profileProperty);
                else if (profile is GPUInstancerDetailProfile)
                    GPUInstancerDetailProfileDrawer.Draw(profileProperty);
            }

            // Keep this outside the disabled configuration scope: it is a
            // read-only view of the transient runtime manager in Play Mode.
            DrawRuntimeDebug(profile);
        }

        private static string GetProfileSessionKey(GPUInstancerProfile profile)
        {
            if (profile == null)
                return string.Empty;
            return profile.id ?? string.Empty;
        }

        private static void DrawSceneSettingsBox(SerializedProperty profileProperty)
        {
            if (profileProperty == null)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Scene Settings", EditorStyles.miniBoldLabel);

            SerializedProperty autoCamera = profileProperty.FindPropertyRelative("autoSelectCamera");
            SerializedProperty frustum = profileProperty.FindPropertyRelative("isFrustumCulling");
            SerializedProperty occlusion = profileProperty.FindPropertyRelative("isOcclusionCulling");
            SerializedProperty minDistance = profileProperty.FindPropertyRelative("minCullingDistance");
            SerializedProperty floatingOrigin = profileProperty.FindPropertyRelative("useFloatingOriginHandler");
            SerializedProperty floatingRotation = profileProperty.FindPropertyRelative("applyFloatingOriginRotationAndScale");
            SerializedProperty layerMask = profileProperty.FindPropertyRelative("layerMask");
            SerializedProperty lightProbes = profileProperty.FindPropertyRelative("lightProbeDisabled");

            if (autoCamera != null)
                EditorGUILayout.PropertyField(autoCamera, new GUIContent("Auto Select Camera"));
            if (frustum != null)
                EditorGUILayout.PropertyField(frustum, new GUIContent("Frustum Culling"));
            if (occlusion != null)
                EditorGUILayout.PropertyField(occlusion, new GUIContent("Occlusion Culling"));
            if (minDistance != null)
            {
                float value = Mathf.Clamp(EditorGUILayout.Slider(
                    new GUIContent("Minimum Culling Distance"), minDistance.floatValue, 0f, 100f), 0f, 100f);
                minDistance.floatValue = value;
            }
            if (floatingOrigin != null)
                EditorGUILayout.PropertyField(floatingOrigin, new GUIContent("Use Floating Origin Handler"));
            if (floatingRotation != null)
            {
                using (new EditorGUI.DisabledScope(floatingOrigin != null && !floatingOrigin.boolValue))
                    EditorGUILayout.PropertyField(floatingRotation, new GUIContent("Apply Floating Origin Rotation / Scale"));
            }
            if (lightProbes != null)
                EditorGUILayout.PropertyField(lightProbes, new GUIContent("Disable Light Probes"));
            if (layerMask != null)
                EditorGUILayout.PropertyField(layerMask, new GUIContent("Layer Mask"));

            EditorGUILayout.EndVertical();
        }

        private static void DrawPrototypeCollection(
            SerializedProperty profileProperty,
            GPUInstancerProfile profile)
        {
            SerializedProperty list = profileProperty.FindPropertyRelative("prototypeList");
            if (list == null || profile == null)
                return;

            string key = GetProfileSessionKey(profile);
            bool expanded;
            if (!s_prototypeFoldouts.TryGetValue(key, out expanded))
                expanded = true;
            bool textMode;
            if (!s_prototypeTextModes.TryGetValue(key, out textMode))
                textMode = false;
            HashSet<GPUInstancerPrototype> selected;
            if (!s_selectedPrototypes.TryGetValue(key, out selected) || selected == null)
            {
                selected = new HashSet<GPUInstancerPrototype>();
                s_selectedPrototypes[key] = selected;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            expanded = EditorGUILayout.Foldout(expanded, "Prototypes", true, EditorStyles.foldout);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(textMode ? "Icon Mode" : "Text Mode", EditorStyles.miniButton, GUILayout.Width(75f)))
                textMode = !textMode;
            EditorGUILayout.EndHorizontal();
            s_prototypeFoldouts[key] = expanded;
            s_prototypeTextModes[key] = textMode;

            if (expanded)
            {
                // Drop selections for entries removed by GeneratePrototypes or
                // managed-reference rehydration.
                selected.RemoveWhere(item => item == null || !ContainsPrototype(list, item));

                if (list.arraySize == 0)
                {
                    EditorGUILayout.HelpBox("No prototypes have been generated for this Profile.", MessageType.Info);
                }
                else
                {
                    int rowCount = Mathf.Max(1, Mathf.FloorToInt(
                        (EditorGUIUtility.currentViewWidth - 30f) / (textMode ? 200f : 80f)));
                    int drawn = 0;
                    EditorGUILayout.BeginHorizontal();
                    for (int i = 0; i < list.arraySize; i++)
                    {
                        GPUInstancerPrototype prototype =
                            list.GetArrayElementAtIndex(i).objectReferenceValue as GPUInstancerPrototype;
                        if (prototype == null)
                            continue;

                        if (drawn > 0 && drawn % rowCount == 0)
                        {
                            EditorGUILayout.EndHorizontal();
                            EditorGUILayout.BeginHorizontal();
                        }

                        GUIContent content = GetPrototypeContent(prototype, textMode);
                        bool isSelected = selected.Contains(prototype);
                        Color oldColor = GUI.backgroundColor;
                        if (isSelected)
                            GUI.backgroundColor = GPUInstancerEditorConstants.Colors.lightGreen;
                        float width = textMode ? 200f : 80f;
                        float height = textMode ? 30f : 80f;
                        if (GUILayout.Button(content, GUILayout.Width(width), GUILayout.Height(height)))
                        {
                            bool multi = Event.current != null &&
                                (Event.current.control || Event.current.command);
                            if (!multi)
                                selected.Clear();
                            if (isSelected && multi)
                                selected.Remove(prototype);
                            else
                                selected.Add(prototype);
                        }
                        GUI.backgroundColor = oldColor;
                        drawn++;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                if (selected.Count > 0)
                {
                    EditorGUILayout.LabelField(
                        selected.Count == 1 ? "Selected Prototype" : "Selected Prototypes",
                        selected.Count.ToString(), EditorStyles.miniLabel);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Edit Selected", EditorStyles.miniButton))
                    {
                        GPUInstancerPrototype first = null;
                        foreach (GPUInstancerPrototype item in selected)
                        {
                            first = item;
                            break;
                        }
                        if (first != null)
                        {
                            Selection.activeObject = first;
                            EditorGUIUtility.PingObject(first);
                        }
                    }
                    if (GUILayout.Button("Remove From Profile", EditorStyles.miniButton))
                    {
                        RemoveSelectedPrototypes(profileProperty, profile, selected);
                        selected.Clear();
                        GUIUtility.ExitGUI();
                    }
                    EditorGUILayout.EndHorizontal();
                }

                if (HasInvalidPrototypeReferences(list))
                {
                    EditorGUILayout.HelpBox(
                        "One or more prototype references are missing or non-persistent. They cannot be used by a Project Profile.",
                        MessageType.Warning);
                    if (GUILayout.Button("Remove Invalid References", EditorStyles.miniButton))
                    {
                        RemoveInvalidPrototypes(profileProperty, profile);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static bool ContainsPrototype(SerializedProperty list, GPUInstancerPrototype prototype)
        {
            if (list == null || prototype == null)
                return false;
            for (int i = 0; i < list.arraySize; i++)
            {
                if (ReferenceEquals(list.GetArrayElementAtIndex(i).objectReferenceValue, prototype))
                    return true;
            }
            return false;
        }

        private static GUIContent GetPrototypeContent(GPUInstancerPrototype prototype, bool textMode)
        {
            Texture2D preview = null;
            if (!textMode && prototype != null)
            {
                preview = GPUInstancerDefines.previewCache.GetPreview(prototype);
                if (preview == null && prototype.prefabObject != null)
                    preview = AssetPreview.GetAssetPreview(prototype.prefabObject);
                if (preview == null)
                    preview = AssetPreview.GetAssetPreview(prototype);
            }

            string name = prototype == null ? "Missing Prototype" : prototype.ToString();
            if (textMode)
                return new GUIContent(name, name);
            // Keep the constructor compatible with the Unity editor API versions
            // supported by this package (some versions do not expose the
            // three-argument GUIContent overload for Texture2D).  Assign the
            // tooltip explicitly instead.
            GUIContent content = new GUIContent(name, preview);
            content.tooltip = name;
            return content;
        }

        private static bool HasInvalidPrototypeReferences(SerializedProperty list)
        {
            if (list == null)
                return false;
            for (int i = 0; i < list.arraySize; i++)
            {
                GPUInstancerPrototype prototype =
                    list.GetArrayElementAtIndex(i).objectReferenceValue as GPUInstancerPrototype;
                if (prototype == null || !EditorUtility.IsPersistent(prototype) ||
                    (prototype.prefabObject != null && !EditorUtility.IsPersistent(prototype.prefabObject)))
                    return true;
            }
            return false;
        }

        private static void RemoveSelectedPrototypes(
            SerializedProperty property,
            GPUInstancerProfile profile,
            HashSet<GPUInstancerPrototype> selected)
        {
            if (profile == null || selected == null || selected.Count == 0)
                return;
            GPUInstancerProjectSettings settings = GetSettings(property);
            RecordSettings(settings, "Remove GPU Instancer prototypes from profile");
            ApplySerializedProfileChanges(property);
            List<GPUInstancerPrototype> clean = new List<GPUInstancerPrototype>();
            if (profile.prototypeList != null)
            {
                for (int i = 0; i < profile.prototypeList.Count; i++)
                {
                    GPUInstancerPrototype prototype = profile.prototypeList[i];
                    if (prototype != null && !selected.Contains(prototype))
                        clean.Add(prototype);
                }
            }
            profile.prototypeList = clean;
            MarkSettingsDirty(settings);
        }

        private static void RemoveInvalidPrototypes(SerializedProperty property, GPUInstancerProfile profile)
        {
            if (profile == null)
                return;
            GPUInstancerProjectSettings settings = GetSettings(property);
            RecordSettings(settings, "Remove invalid GPU Instancer prototypes");
            ApplySerializedProfileChanges(property);
            List<GPUInstancerPrototype> clean = new List<GPUInstancerPrototype>();
            if (profile.prototypeList != null)
            {
                for (int i = 0; i < profile.prototypeList.Count; i++)
                {
                    GPUInstancerPrototype prototype = profile.prototypeList[i];
                    if (prototype != null && EditorUtility.IsPersistent(prototype) &&
                        (prototype.prefabObject == null || EditorUtility.IsPersistent(prototype.prefabObject)))
                        clean.Add(prototype);
                }
            }
            profile.prototypeList = clean;
            MarkSettingsDirty(settings);
        }

        private static void DrawSimulationControls(GPUInstancerProfile profile, SerializedProperty profileProperty)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Preview", EditorStyles.miniBoldLabel);
            GPUInstancerManager manager;
            // Looking up an existing preview is side-effect free.  A manager
            // is created only after the user presses Simulate.
            if (!GPUInstancerDriver.TryGet(profile, out manager))
                manager = null;
            if (!Application.isPlaying && manager != null && manager.editorLoopState == null)
                manager.editorLoopState = new GPUInstancerLoopScheduler.EditorLoopState(manager);

            // Keep the transient manager synchronized with the session values
            // before drawing the button.  This matters for terrain managers:
            // DetailManager.ResetConfiguration can establish its own default,
            // but an explicit user choice in this drawer must win for the rest
            // of the preview session.
            ApplySimulationSessionState(profile, manager);

            bool simulating = manager != null && manager.editorLoopState != null && manager.editorLoopState.simulateAtEditor;
            // Editor simulation is a Scene-view preview. Runtime managers in
            // Play Mode are driven by the scheduler and must not be replaced
            // or reconfigured by this button.
            using (new EditorGUI.DisabledScope(!profile.enabled || Application.isPlaying))
            {
                if (GUILayout.Button(simulating ? "Stop" : "Simulate", GUILayout.Width(72f)))
                {
                    bool requestExitGui = false;
                    ApplySerializedProfileChanges(profileProperty);
                        if (manager == null && profile.enabled)
                        {
                            // Preview always goes through the Driver and a
                            // real Manager; profiles themselves never receive
                            // Tick/Initialize calls.
                            manager = GPUInstancerDriver.GetOrCreate(profile);
                            if (!Application.isPlaying && manager != null && manager.editorLoopState == null)
                                manager.editorLoopState = new GPUInstancerLoopScheduler.EditorLoopState(manager);
                        }
                        if (manager != null && manager.editorLoopState != null)
                        {
                            ApplySimulationSessionState(profile, manager);
                            bool canSimulate = true;
                            RefreshRuntimeManagerFromProfile(profile, manager);
                            // Terrain managers have no useful scene preview
                            // until a Terrain is bound. Resolve the current
                            // editor terrain immediately before starting
                            // simulation. The helper only writes a profile id
                            // to the scene Proxy; it never stores the Terrain
                            // object in the Profile.
                            if (!simulating &&
                                (profile is GPUInstancerTreeProfile || profile is GPUInstancerDetailProfile))
                                canSimulate = EnsureTerrainBinding(profile, profileProperty, manager);

                            if (canSimulate)
                            {
                                if (simulating)
                                {
                                    manager.editorLoopState.StopSimulation();
                                    // An explicit Stop also disables the live
                                    // restart hook, otherwise TerrainProxy's
                                    // editor Update would immediately start the
                                    // preview again on the next frame.
                                    manager.keepSimulationLive = false;
                                    SetSimulationSessionValue(s_keepSimulationLive, profile, false);
                                }
                                else
                                    manager.editorLoopState.StartSimulation();

                                // Simulation flags live on the transient
                                // manager's EditorLoopState. Do not mark the
                                // settings asset dirty.
                                requestExitGui = true;
                            }
                        }

                    if (requestExitGui)
                        GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();

            // These are deliberately outside the serialized Profile property
            // tree.  They are editor-session controls for the transient preview
            // Manager without persisting simulation state into project assets.
            bool keepSimulationLive = GetSimulationSessionValue(
                s_keepSimulationLive, profile, manager != null && manager.keepSimulationLive);
            bool newKeepSimulationLive;
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                newKeepSimulationLive = EditorGUILayout.Toggle("Keep Simulation Live", keepSimulationLive);
            if (newKeepSimulationLive != keepSimulationLive)
            {
                SetSimulationSessionValue(s_keepSimulationLive, profile, newKeepSimulationLive);
                if (manager != null)
                    manager.keepSimulationLive = newKeepSimulationLive;
            }

            if (newKeepSimulationLive)
            {
                bool updateSimulation = GetSimulationSessionValue(
                    s_updateSimulation, profile, manager == null || manager.updateSimulation);
                bool newUpdateSimulation;
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                    newUpdateSimulation = EditorGUILayout.Toggle(
                        new GUIContent("Update Simulation On Change", "Update Simulation On Change"),
                        updateSimulation);
                if (newUpdateSimulation != updateSimulation)
                {
                    SetSimulationSessionValue(s_updateSimulation, profile, newUpdateSimulation);
                    if (manager != null)
                        manager.updateSimulation = newUpdateSimulation;
                }

                EditorGUILayout.HelpBox(
                    "Simulation is kept alive. The simulation might not show every change on terrain details. " +
                    "Changing some prototype settings during the simulation can also cause errors. Please stop " +
                    "and start the simulation again to solve these issues.",
                    MessageType.Warning);
            }
        }

        private static bool GetSimulationSessionValue(
            Dictionary<string, bool> values,
            GPUInstancerProfile profile,
            bool defaultValue)
        {
            string key = GetProfileSessionKey(profile);
            if (string.IsNullOrEmpty(key))
                return defaultValue;
            bool value;
            if (!values.TryGetValue(key, out value))
            {
                value = defaultValue;
                values[key] = value;
            }
            return value;
        }

        private static void SetSimulationSessionValue(
            Dictionary<string, bool> values,
            GPUInstancerProfile profile,
            bool value)
        {
            string key = GetProfileSessionKey(profile);
            if (!string.IsNullOrEmpty(key))
                values[key] = value;
        }

        private static void ApplySimulationSessionState(
            GPUInstancerProfile profile,
            GPUInstancerManager manager)
        {
            if (profile == null || manager == null)
                return;

            // Detail initialization disables live simulation when its terrain
            // settings report a validation error.  Do not immediately restore
            // a stale session preference here: TerrainProxy.Update would then
            // retry the failed preview every editor frame.  The user can turn
            // the option back on after fixing the warning.
            GPUInstancerDetailManager detailManager = manager as GPUInstancerDetailManager;
            if (detailManager != null && detailManager.terrainSettings != null &&
                !string.IsNullOrEmpty(detailManager.terrainSettings.warningText))
            {
                manager.keepSimulationLive = false;
                SetSimulationSessionValue(s_keepSimulationLive, profile, false);
                return;
            }

            bool keep = GetSimulationSessionValue(
                s_keepSimulationLive, profile, manager.keepSimulationLive);
            bool update = GetSimulationSessionValue(
                s_updateSimulation, profile, manager.updateSimulation);
            manager.keepSimulationLive = keep;
            manager.updateSimulation = update;
        }

        private static void DrawRuntimeDebug(GPUInstancerProfile profile)
        {
            if (!Application.isPlaying || profile == null)
                return;

            GPUInstancerManager manager;
            if (!GPUInstancerDriver.TryGet(profile, out manager) || manager == null)
            {
                EditorGUILayout.HelpBox("No runtime manager is currently bound to this Profile.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Runtime Debug (read-only)", EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Active", manager.isActive);
                EditorGUILayout.Toggle("Initialized", manager.isInitialized);
                int prototypeCount = manager.runtimeDataList == null ? 0 : manager.runtimeDataList.Count;
                EditorGUILayout.IntField("Runtime Data", prototypeCount);

                int instanceCount = 0;
                if (manager.runtimeDataList != null)
                {
                    for (int i = 0; i < manager.runtimeDataList.Count; i++)
                    {
                        GPUInstancerRuntimeData runtimeData = manager.runtimeDataList[i];
                        if (runtimeData != null)
                            instanceCount += runtimeData.instanceCount;
                    }
                }
                EditorGUILayout.IntField("Instance Count", instanceCount);

                Camera camera = manager.cameraData == null ? null : manager.cameraData.mainCamera;
                EditorGUILayout.ObjectField("Camera", camera, typeof(Camera), true);

                GPUInstancerTerrainManager terrainManager = manager as GPUInstancerTerrainManager;
                if (terrainManager != null)
                {
                    List<Terrain> terrains = terrainManager.GetTerrains();
                    EditorGUILayout.IntField("Bound Terrains", terrains == null ? 0 : terrains.Count);
                }
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Commits ordinary SerializedProperty edits before an editor action
        /// mutates the managed profile directly.  This is important for the
        /// Object Picker and Generate buttons: both operate on a transient
        /// manager and then copy the resulting persistent data back to the
        /// settings asset.
        /// </summary>
        internal static void ApplySerializedProfileChanges(SerializedProperty profileProperty)
        {
            if (profileProperty != null && profileProperty.serializedObject != null)
                profileProperty.serializedObject.ApplyModifiedProperties();
        }

        internal static GPUInstancerProjectSettings GetSettings(SerializedProperty profileProperty)
        {
            GPUInstancerProjectSettings settings = null;
            if (profileProperty != null && profileProperty.serializedObject != null)
                settings = profileProperty.serializedObject.targetObject as GPUInstancerProjectSettings;
            return settings != null ? settings : GPUInstancerProfileEditorUtility.GetProjectSettings();
        }

        internal static void RecordSettings(GPUInstancerProjectSettings settings, string undoName)
        {
            if (settings != null && !Application.isPlaying)
                Undo.RecordObject(settings, undoName);
        }

        internal static void MarkSettingsDirty(GPUInstancerProjectSettings settings)
        {
            if (settings == null || Application.isPlaying)
                return;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        internal static void BeginPrefabPicker(GPUInstancerPrefabProfile profile, int controlId)
        {
            if (profile == null)
                return;
            profile.InitializeAuthoringData();
            s_prefabPickerProfile = profile;
            s_prefabPickerProfileId = profile.id;
            s_prefabPickerControlId = controlId;
            EditorGUIUtility.ShowObjectPicker<GameObject>(null, false, "t:prefab", controlId);
        }

        internal static void HandlePrefabPickerEvent(
            GPUInstancerPrefabProfile profile,
            SerializedProperty profileProperty)
        {
            Event current = Event.current;
            if (current == null || current.type != EventType.ExecuteCommand ||
                current.commandName != "ObjectSelectorClosed" ||
                s_prefabPickerControlId < 0 ||
                EditorGUIUtility.GetObjectPickerControlID() != s_prefabPickerControlId)
                return;

            UnityEngine.Object pickedObject = EditorGUIUtility.GetObjectPickerObject();
            bool belongsToProfile = s_prefabPickerProfile != null &&
                profile != null &&
                (ReferenceEquals(s_prefabPickerProfile, profile) ||
                 (!string.IsNullOrEmpty(s_prefabPickerProfileId) &&
                  s_prefabPickerProfileId == profile.id));
            s_prefabPickerControlId = -1;

            if (!belongsToProfile || pickedObject == null)
                return;

            ApplySerializedProfileChanges(profileProperty);
            AddPrefabPickerObject(profile, profileProperty, pickedObject);
            // The managed-reference iterator may have been rehydrated by the
            // manager's GeneratePrototypes call. Repaint on the next GUI pass
            // instead of continuing with stale SerializedProperty handles.
            GUIUtility.ExitGUI();
        }

        private static void AddPrefabPickerObject(
            GPUInstancerPrefabProfile profile,
            SerializedProperty profileProperty,
            UnityEngine.Object pickedObject)
        {
            GameObject prefab;
            if (!(pickedObject is GameObject) ||
                !GPUInstancerPrefabProfileDrawer.TryGetPersistentPrefab((GameObject)pickedObject, out prefab))
            {
                Debug.LogWarning("GPU Instancer Profiles accept prefab assets only; scene objects were ignored.");
                return;
            }

            GPUInstancerProjectSettings settings = GetSettings(profileProperty);
            RecordSettings(settings, "Add GPU Instancer prefab prototype");

            GPUInstancerPrefabManager manager = GPUInstancerDriver.GetOrCreate(profile) as GPUInstancerPrefabManager;
            if (manager == null)
                return;

            // GetOrCreate may return a manager that was created before the
            // current SerializedProperty edit. Refresh its editor collections
            // explicitly; SetProfile intentionally avoids copying collections
            // on every frame to keep runtime lists isolated.
            manager.RefreshProfileCollections();

            GPUInstancerPrefabManagerEditor.AddPickerObject(manager, prefab);
            SyncPrefabProfileFromManager(profile, manager);
            MarkSettingsDirty(settings);
        }

        internal static void GeneratePrefabPrototypes(
            GPUInstancerPrefabProfile profile,
            SerializedProperty profileProperty)
        {
            if (profile == null || !profile.enabled)
                return;

            GPUInstancerProjectSettings settings = GetSettings(profileProperty);
            RecordSettings(settings, "Generate GPU Instancer prefab prototypes");
            SanitizePrefabList(profile);

            GPUInstancerPrefabManager manager = GPUInstancerDriver.GetOrCreate(profile) as GPUInstancerPrefabManager;
            if (manager == null)
                return;
            manager.RefreshProfileCollections();

            manager.GeneratePrototypes(true);
            SyncPrefabProfileFromManager(profile, manager);
            MarkSettingsDirty(settings);
        }

        /// <summary>
        /// A preview manager can survive several inspector repaints.  The
        /// runtime manager intentionally copies profile collections only once,
        /// so refresh the editor-preview copy immediately before simulation.
        /// The lists remain manager-owned and are never assigned back to the
        /// Profile from this method.
        /// </summary>
        private static void RefreshRuntimeManagerFromProfile(
            GPUInstancerProfile profile,
            GPUInstancerManager manager)
        {
            if (profile == null || manager == null)
                return;

            manager.RefreshProfileCollections();

            GPUInstancerPrefabManager prefabManager = manager as GPUInstancerPrefabManager;
            GPUInstancerPrefabProfile prefabProfile = profile as GPUInstancerPrefabProfile;
            if (prefabManager != null && prefabProfile != null)
            {
                prefabManager.prefabList = prefabProfile.prefabList == null
                    ? new List<GameObject>()
                    : new List<GameObject>(prefabProfile.prefabList);
                prefabManager.registeredPrefabs = CloneRegisteredPrefabs(prefabProfile.registeredPrefabs);
                prefabManager.enableMROnManagerDisable = prefabProfile.enableMROnManagerDisable;
                prefabManager.enableMROnRemoveInstance = prefabProfile.enableMROnRemoveInstance;
            }
        }

        private static List<RegisteredPrefabsData> CloneRegisteredPrefabs(
            List<RegisteredPrefabsData> source)
        {
            List<RegisteredPrefabsData> result = new List<RegisteredPrefabsData>();
            if (source == null)
                return result;
            for (int i = 0; i < source.Count; i++)
            {
                RegisteredPrefabsData entry = source[i];
                if (entry == null || entry.prefabPrototype == null)
                    continue;
                RegisteredPrefabsData clone = new RegisteredPrefabsData(entry.prefabPrototype);
                if (entry.registeredPrefabs != null)
                {
                    for (int j = 0; j < entry.registeredPrefabs.Count; j++)
                    {
                        GPUInstancerPrefab instance = entry.registeredPrefabs[j];
                        if (GPUInstancerProfileEditorUtility.IsPersistentPrefabComponent(instance))
                            clone.registeredPrefabs.Add(instance);
                    }
                }
                result.Add(clone);
            }
            return result;
        }

        private static void SanitizePrefabList(GPUInstancerPrefabProfile profile)
        {
            if (profile == null)
                return;
            if (profile.prefabList == null)
                profile.prefabList = new List<GameObject>();

            List<GameObject> clean = new List<GameObject>();
            for (int i = 0; i < profile.prefabList.Count; i++)
            {
                GameObject normalized;
                if (!GPUInstancerPrefabProfileDrawer.TryGetPersistentPrefab(profile.prefabList[i], out normalized))
                    continue;
                if (!clean.Contains(normalized))
                    clean.Add(normalized);
            }
            profile.prefabList = clean;
        }

        private static void SyncPrefabProfileFromManager(
            GPUInstancerPrefabProfile profile,
            GPUInstancerPrefabManager manager)
        {
            if (profile == null || manager == null)
                return;

            profile.prefabList = new List<GameObject>();
            if (manager.prefabList != null)
            {
                for (int i = 0; i < manager.prefabList.Count; i++)
                {
                    GameObject normalized;
                    if (GPUInstancerPrefabProfileDrawer.TryGetPersistentPrefab(manager.prefabList[i], out normalized) &&
                        !profile.prefabList.Contains(normalized))
                        profile.prefabList.Add(normalized);
                }
            }

            // Generated prototypes are project assets in edit mode. Keep a
            // persistent-only list as a final guard against an accidental
            // transient object reference entering the settings asset.
            profile.prototypeList = new List<GPUInstancerPrototype>();
            if (manager.prototypeList != null)
            {
                for (int i = 0; i < manager.prototypeList.Count; i++)
                {
                    GPUInstancerPrototype prototype = manager.prototypeList[i];
                    if (IsPersistentPrototype(prototype))
                        profile.prototypeList.Add(prototype);
                }
            }

            profile.registeredPrefabs = new List<RegisteredPrefabsData>();
            if (manager.registeredPrefabs == null)
                return;
            for (int i = 0; i < manager.registeredPrefabs.Count; i++)
            {
                RegisteredPrefabsData source = manager.registeredPrefabs[i];
                if (source == null || !IsPersistentPrototype(source.prefabPrototype))
                    continue;
                RegisteredPrefabsData copy = new RegisteredPrefabsData(source.prefabPrototype);
                if (source.registeredPrefabs != null)
                {
                    for (int j = 0; j < source.registeredPrefabs.Count; j++)
                    {
                        GPUInstancerPrefab instance = source.registeredPrefabs[j];
                        if (GPUInstancerProfileEditorUtility.IsPersistentPrefabComponent(instance))
                            copy.registeredPrefabs.Add(instance);
                    }
                }
                profile.registeredPrefabs.Add(copy);
            }
        }

        private static bool IsPersistentPrototype(GPUInstancerPrototype prototype)
        {
            if (prototype == null || !EditorUtility.IsPersistent(prototype))
                return false;
            // A prototype asset generated from a scene object can itself be
            // persistent while still pointing at that scene object. Such a
            // reference cannot be resolved from a project settings asset and
            // must never be copied into a Profile.
            return prototype.prefabObject == null || EditorUtility.IsPersistent(prototype.prefabObject);
        }

        internal static GPUInstancerTerrainManager GetTerrainManager(
            GPUInstancerProfile profile,
            SerializedProperty profileProperty)
        {
            if (profile == null || !profile.enabled)
                return null;
            // GetOrCreate is deliberately used directly here. Bootstrapping
            // the whole project config from an inspector repaint would create
            // every enabled Prefab channel as a side effect of merely opening
            // a Tree/Detail Profile; preview creation must stay opt-in.
            GPUInstancerTerrainManager manager = GPUInstancerDriver.GetOrCreate(profile) as GPUInstancerTerrainManager;
            if (profile is GPUInstancerTreeProfile && !(manager is GPUInstancerTreeManager))
                return null;
            if (profile is GPUInstancerDetailProfile && !(manager is GPUInstancerDetailManager))
                return null;
            return manager;
        }

        internal static bool EnsureTerrainBinding(
            GPUInstancerProfile profile,
            SerializedProperty profileProperty,
            GPUInstancerManager existingManager = null)
        {
            if (profile == null)
                return false;
            GPUInstancerTerrainManager manager = existingManager as GPUInstancerTerrainManager;
            if (manager == null)
                manager = GetTerrainManager(profile, profileProperty);
            if (manager == null)
                return false;

            Terrain terrain = GetPreviewTerrain(profile, manager);
            if (terrain == null || EditorUtility.IsPersistent(terrain) ||
                (terrain.gameObject != null && EditorUtility.IsPersistent(terrain.gameObject)))
            {
                return false;
            }

            BindTerrain(profile, profileProperty, manager, terrain);
            return manager.terrain != null || manager.GetTerrains().Count > 0;
        }

        private static Terrain GetPreviewTerrain(
            GPUInstancerProfile profile,
            GPUInstancerTerrainManager manager)
        {
            if (manager != null && manager.terrain != null)
                return manager.terrain;

            if (profile != null)
            {
                if (!Application.isPlaying)
                    profile.InitializeAuthoringData();
                if (string.IsNullOrEmpty(profile.id))
                    return null;
                Terrain cached;
                if (s_previewTerrains.TryGetValue(profile.id, out cached))
                {
                    if (cached != null)
                        return cached;
                    s_previewTerrains.Remove(profile.id);
                }
            }

            GameObject selected = Selection.activeGameObject;
            Terrain selectedTerrain = selected != null ? selected.GetComponent<Terrain>() : null;
            if (selectedTerrain != null)
                return selectedTerrain;
            return Terrain.activeTerrain;
        }

        internal static void DrawTerrainBinding(
            GPUInstancerProfile profile,
            SerializedProperty profileProperty,
            bool detail)
        {
            if (profile == null)
                return;
            if ((detail && !(profile is GPUInstancerDetailProfile)) ||
                (!detail && !(profile is GPUInstancerTreeProfile)))
                return;
            if (!Application.isPlaying)
                profile.InitializeAuthoringData();
            if (string.IsNullOrEmpty(profile.id))
                return;

            GPUInstancerTerrainManager manager = null;
            GPUInstancerManager rawManager;
            if (GPUInstancerDriver.TryGet(profile, out rawManager))
                manager = rawManager as GPUInstancerTerrainManager;

            Terrain currentTerrain = GetPreviewTerrain(profile, manager);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Terrain Binding (scene only)", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            Terrain selectedTerrain;
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                selectedTerrain = (Terrain)EditorGUILayout.ObjectField(
                    "Preview Terrain", currentTerrain, typeof(Terrain), true);
            }
            if (EditorGUI.EndChangeCheck())
            {
                if (selectedTerrain != null &&
                    !EditorUtility.IsPersistent(selectedTerrain) &&
                    (selectedTerrain.gameObject == null || !EditorUtility.IsPersistent(selectedTerrain.gameObject)))
                {
                    s_previewTerrains[profile.id] = selectedTerrain;
                    if (profile.enabled)
                    {
                        ApplySerializedProfileChanges(profileProperty);
                        manager = manager ?? GetTerrainManager(profile, profileProperty);
                        if (manager != null)
                        {
                            BindTerrain(profile, profileProperty, manager, selectedTerrain);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                else if (selectedTerrain != null)
                {
                    EditorGUILayout.HelpBox("Only a Terrain instance from the open scene can be bound.", MessageType.Warning);
                }
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!profile.enabled || Application.isPlaying))
            {
                if (GUILayout.Button("Use Active Terrain"))
                {
                    Terrain active = Terrain.activeTerrain;
                    if (active != null && !EditorUtility.IsPersistent(active))
                    {
                        ApplySerializedProfileChanges(profileProperty);
                        s_previewTerrains[profile.id] = active;
                        manager = manager ?? GetTerrainManager(profile, profileProperty);
                        if (manager != null)
                            BindTerrain(profile, profileProperty, manager, active);
                        GUIUtility.ExitGUI();
                    }
                }
                if (GUILayout.Button("Bind Terrain"))
                {
                    ApplySerializedProfileChanges(profileProperty);
                    manager = manager ?? GetTerrainManager(profile, profileProperty);
                    if (manager != null && EnsureTerrainBinding(profile, profileProperty, manager))
                        GUIUtility.ExitGUI();
                }
            }
            // Unbinding is a scene operation and remains available for a
            // disabled Profile.  Disabling a channel intentionally preserves
            // its proxy IDs so it can be enabled again; an explicit Unbind,
            // on the other hand, must remove those persisted IDs or OnEnable
            // would immediately recreate the same binding.
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Unbind"))
                {
                    ApplySerializedProfileChanges(profileProperty);
                    UnbindTerrainProfile(profile, manager, detail);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (manager != null)
            {
                List<Terrain> terrains = manager.GetTerrains();
                if (terrains != null && terrains.Count > 0)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        for (int i = 0; i < terrains.Count; i++)
                            EditorGUILayout.ObjectField(i == 0 ? "Main Terrain" : "Additional Terrain", terrains[i], typeof(Terrain), true);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("No Terrain is currently bound to this Profile.", MessageType.Info);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Press Bind Terrain or Simulate to create the transient runtime manager.", MessageType.Info);
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Removes the scene-side bindings for one Profile/channel while
        /// preserving the other channel on each TerrainProxy and all
        /// project-owned TerrainSettings/prototype configuration.
        /// </summary>
        internal static void UnbindTerrainProfile(
            GPUInstancerProfile profile,
            GPUInstancerTerrainManager manager,
            bool detail)
        {
            if (profile == null)
                return;

            if (!Application.isPlaying && string.IsNullOrEmpty(profile.id))
                profile.InitializeAuthoringData();

            // Stop an editor preview first. Otherwise its next editor update
            // would repeatedly try to initialize the now terrain-less engine.
            if (manager != null && manager.editorLoopState != null &&
                manager.editorLoopState.simulateAtEditor)
                manager.editorLoopState.StopSimulation();

            GPUInstancerTerrainProxy[] proxies =
                Resources.FindObjectsOfTypeAll<GPUInstancerTerrainProxy>();
            for (int i = 0; i < proxies.Length; i++)
            {
                GPUInstancerTerrainProxy proxy = proxies[i];
                if (!IsEditableSceneProxy(proxy))
                    continue;

                string boundId = detail ? proxy.detailProfileId : proxy.treeProfileId;
                GPUInstancerTerrainManager runtimeManager = detail
                    ? (GPUInstancerTerrainManager)proxy.runtimeDetailManager
                    : proxy.runtimeTreeManager;

                bool idMatches = !string.IsNullOrEmpty(profile.id) &&
                    string.Equals(boundId, profile.id, StringComparison.Ordinal);
                bool runtimeMatches = ReferenceEquals(runtimeManager, manager) ||
                    (runtimeManager != null && ReferenceEquals(runtimeManager.profile, profile));
                if (!idMatches && !runtimeMatches)
                    continue;

                // Resolve/detach through the old ID before clearing it. This
                // also covers a domain reload where the transient reference
                // was lost but the manager is still registered.
                GPUInstancerTerrainBinder.Unbind(proxy, detail);

                Undo.RecordObject(proxy, detail
                    ? "Unbind GPU Instancer Detail Profile"
                    : "Unbind GPU Instancer Tree Profile");
                if (idMatches)
                    proxy.SetProfileId(null, detail);
                proxy.SetRuntimeManager(null, detail);

                EditorUtility.SetDirty(proxy);
            }

            // TryGet may repair a SerializeReference rehydration that occurred
            // while ApplyModifiedProperties ran immediately before this call.
            if (manager == null &&
                GPUInstancerDriver.TryGet(profile, out GPUInstancerManager resolved))
                manager = resolved as GPUInstancerTerrainManager;

            // Setup(null) is the all-terrain flush for this one manager. Its
            // profile-backed path deliberately retains TerrainSettings and
            // prototype configuration while restoring Unity terrain distances.
            if (manager != null)
                manager.SetupManagerWithTerrain(null);

            if (!string.IsNullOrEmpty(profile.id))
                s_previewTerrains.Remove(profile.id);
            SceneView.RepaintAll();
        }

        private static bool IsEditableSceneProxy(GPUInstancerTerrainProxy proxy)
        {
            if (proxy == null || proxy.gameObject == null || EditorUtility.IsPersistent(proxy))
                return false;
            if ((proxy.hideFlags & HideFlags.DontSave) != 0 ||
                (proxy.gameObject.hideFlags & HideFlags.DontSave) != 0)
                return false;
            return proxy.gameObject.scene.IsValid() && proxy.gameObject.scene.isLoaded &&
                !EditorSceneManager.IsPreviewScene(proxy.gameObject.scene);
        }

        internal static void DrawTerrainGlobalValues(
            GPUInstancerProfile profile,
            SerializedProperty profileProperty,
            bool detail)
        {
            if (profile == null)
                return;

            GPUInstancerTerrainSettings terrainSettings = null;
            SerializedProperty terrainSettingsProperty =
                profileProperty == null ? null : profileProperty.FindPropertyRelative("terrainSettings");
            if (terrainSettingsProperty != null)
                terrainSettings = terrainSettingsProperty.objectReferenceValue as GPUInstancerTerrainSettings;
            if (terrainSettings == null)
            {
                terrainSettings = detail
                    ? (profile as GPUInstancerDetailProfile)?.terrainSettings
                    : (profile as GPUInstancerTreeProfile)?.terrainSettings;
            }

            // TerrainSettings is an asset reference held by a managed Profile,
            // so it is quite possible for a Profile authored by an older
            // package version to still point at the package's default asset.
            // Never expose mutating controls for that object: Undo.RecordObject
            // and a direct field assignment would otherwise attempt to dirty a
            // read-only package file.  Prefer a writable project copy and remap
            // any Terrain prototype sub-assets at the same time.  The direct
            // field assignment is intentional here; the SerializedProperty is
            // updated as well, but Unity does not necessarily update a managed
            // reference's boxed object until ApplyModifiedProperties runs later
            // in the parent inspector.
            bool terrainSettingsEditable = true;
            if (!Application.isPlaying && terrainSettings != null)
            {
                string terrainSettingsPath = AssetDatabase.GetAssetPath(terrainSettings);
                if (!string.IsNullOrEmpty(terrainSettingsPath))
                    terrainSettingsPath = terrainSettingsPath.Replace('\\', '/');
                bool immutableTerrainSettings = !string.IsNullOrEmpty(terrainSettingsPath) &&
                    (GPUInstancerConstants.IsPackageAssetPath(terrainSettingsPath) ||
                     !AssetDatabase.IsOpenForEdit(terrainSettingsPath));
                if (immutableTerrainSettings)
                {
                    GPUInstancerTerrainSettings sourceTerrainSettings = terrainSettings;
                    GPUInstancerTerrainSettings editableTerrainSettings =
                        GPUInstancerProfileEditorUtility.GetPersistentTerrainSettings(sourceTerrainSettings);
                    if (editableTerrainSettings == null)
                    {
                        terrainSettingsEditable = false;
                    }
                    else if (!ReferenceEquals(sourceTerrainSettings, editableTerrainSettings))
                    {
                        GPUInstancerProjectSettings settings = GetSettings(profileProperty);
                        RecordSettings(settings, "Copy immutable GPU Instancer terrain settings");

                        if (profileProperty != null)
                        {
                            SerializedProperty editableProperty =
                                profileProperty.FindPropertyRelative("terrainSettings");
                            if (editableProperty != null)
                                editableProperty.objectReferenceValue = editableTerrainSettings;
                        }

                        if (profile is GPUInstancerTreeProfile treeProfile)
                            treeProfile.terrainSettings = editableTerrainSettings;
                        else if (profile is GPUInstancerDetailProfile detailProfile)
                            detailProfile.terrainSettings = editableTerrainSettings;

                        GPUInstancerProfileEditorUtility.RemapTerrainProfilePrototypes(
                            profile, sourceTerrainSettings, editableTerrainSettings);
                        terrainSettings = editableTerrainSettings;
                        MarkSettingsDirty(settings);
                    }
                }
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(detail ? "Detail Global Values" : "Tree Global Values", EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Terrain Settings", terrainSettings, typeof(GPUInstancerTerrainSettings), false);

            if (terrainSettings == null)
            {
                EditorGUILayout.HelpBox("Bind a Terrain to create the Terrain Settings asset.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            if (!terrainSettingsEditable)
            {
                EditorGUILayout.HelpBox(
                    "The referenced Terrain Settings asset is read-only (often a package asset) and could not be copied to a writable project asset. " +
                    "Bind a scene Terrain or assign a writable Terrain Settings asset before editing these values.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!terrainSettingsEditable))
            {
                float maxDistance = detail ? terrainSettings.maxDetailDistance : terrainSettings.maxTreeDistance;
                const float maxLimit = 2500f;
                string distanceLabel = detail ? "Max Detail Distance" : "Max Tree Distance";
                float newDistance = EditorGUILayout.Slider(distanceLabel, maxDistance, 0f, maxLimit);
                if (terrainSettingsEditable && !Mathf.Approximately(newDistance, maxDistance))
                {
                    Undo.RecordObject(terrainSettings, "Change GPU Instancer terrain distance");
                    if (detail)
                        terrainSettings.maxDetailDistance = newDistance;
                    else
                        terrainSettings.maxTreeDistance = newDistance;
                    ApplyTerrainDistanceToPrototypes(profile, maxDistance, newDistance, detail);
                    EditorUtility.SetDirty(terrainSettings);
                }

                if (detail)
                {
                    float newDensity = EditorGUILayout.Slider("Detail Density", terrainSettings.detailDensity, 0f, 1f);
                    if (terrainSettingsEditable && !Mathf.Approximately(newDensity, terrainSettings.detailDensity))
                    {
                        Undo.RecordObject(terrainSettings, "Change GPU Instancer detail density");
                        float oldDensity = terrainSettings.detailDensity;
                        terrainSettings.detailDensity = newDensity;
                        ApplyTerrainDensityToPrototypes(profile, oldDensity, newDensity);
                        EditorUtility.SetDirty(terrainSettings);
                    }
                }
            }

            if (detail)
            {
                SerializedProperty detailLayer = profileProperty == null
                    ? null
                    : profileProperty.FindPropertyRelative("detailLayer");
                if (detailLayer != null)
                    EditorGUILayout.PropertyField(detailLayer, new GUIContent("Detail Texture Layer"));
                SerializedProperty runInThreads = profileProperty == null
                    ? null
                    : profileProperty.FindPropertyRelative("runInThreads");
                if (runInThreads != null)
                    EditorGUILayout.PropertyField(runInThreads, new GUIContent("Run Initialization In Threads"));

                using (new EditorGUI.DisabledScope(!terrainSettingsEditable))
                {
                    Vector2 windVector = EditorGUILayout.Vector2Field("Wind Vector", terrainSettings.windVector);
                    Texture2D healthyNoise = (Texture2D)EditorGUILayout.ObjectField(
                        "Healthy/Dry Noise Texture", terrainSettings.healthyDryNoiseTexture, typeof(Texture2D), false);
                    Texture2D windNoise = (Texture2D)EditorGUILayout.ObjectField(
                        "Wind Wave Normal Texture", terrainSettings.windWaveNormalTexture, typeof(Texture2D), false);
                    bool autoCellSize = EditorGUILayout.Toggle("Auto SP Cell Size", terrainSettings.autoSPCellSize);
                    int preferredCellSize = terrainSettings.preferedSPCellSize;
                    if (!autoCellSize)
                        preferredCellSize = EditorGUILayout.IntSlider(
                            "Preferred SP Cell Size", preferredCellSize, 25, 500);

                    if (terrainSettingsEditable &&
                        (windVector != terrainSettings.windVector ||
                         !ReferenceEquals(healthyNoise, terrainSettings.healthyDryNoiseTexture) ||
                         !ReferenceEquals(windNoise, terrainSettings.windWaveNormalTexture) ||
                         autoCellSize != terrainSettings.autoSPCellSize ||
                         preferredCellSize != terrainSettings.preferedSPCellSize))
                    {
                        Undo.RecordObject(terrainSettings, "Change GPU Instancer terrain settings");
                        terrainSettings.windVector = windVector;
                        terrainSettings.healthyDryNoiseTexture = healthyNoise;
                        terrainSettings.windWaveNormalTexture = windNoise;
                        terrainSettings.autoSPCellSize = autoCellSize;
                        terrainSettings.preferedSPCellSize = preferredCellSize;
                        EditorUtility.SetDirty(terrainSettings);
                    }
                }
            }
            else
            {
                SerializedProperty initialize = profileProperty == null
                    ? null
                    : profileProperty.FindPropertyRelative("initializeWithCoroutine");
                if (initialize != null)
                    EditorGUILayout.PropertyField(initialize, new GUIContent("Initialize With Coroutine"));
            }

            if (!string.IsNullOrEmpty(terrainSettings.warningText))
                EditorGUILayout.HelpBox(terrainSettings.warningText, MessageType.Warning);
            EditorGUILayout.EndVertical();
        }

        private static void ApplyTerrainDistanceToPrototypes(
            GPUInstancerProfile profile,
            float oldDistance,
            float newDistance,
            bool detail)
        {
            if (profile == null || profile.prototypeList == null)
                return;
            for (int i = 0; i < profile.prototypeList.Count; i++)
            {
                GPUInstancerPrototype prototype = profile.prototypeList[i];
                if (prototype == null ||
                    (!Mathf.Approximately(prototype.maxDistance, oldDistance) && prototype.maxDistance <= newDistance))
                    continue;
                prototype.maxDistance = newDistance;
                EditorUtility.SetDirty(prototype);
            }
        }

        private static void ApplyTerrainDensityToPrototypes(
            GPUInstancerProfile profile,
            float oldDensity,
            float newDensity)
        {
            if (profile == null || profile.prototypeList == null)
                return;
            for (int i = 0; i < profile.prototypeList.Count; i++)
            {
                GPUInstancerDetailPrototype prototype = profile.prototypeList[i] as GPUInstancerDetailPrototype;
                if (prototype == null ||
                    (!Mathf.Approximately(prototype.detailDensity, oldDensity) && prototype.detailDensity <= newDensity))
                    continue;
                prototype.detailDensity = newDensity;
                EditorUtility.SetDirty(prototype);
            }
        }

        private static void BindTerrain(
            GPUInstancerProfile profile,
            SerializedProperty profileProperty,
            GPUInstancerTerrainManager manager,
            Terrain terrain)
        {
            if (profile == null || manager == null || terrain == null)
                return;
            bool detail = profile is GPUInstancerDetailProfile;
            bool tree = profile is GPUInstancerTreeProfile;
            if (!detail && !tree)
                return;

            // Tree/Detail runtime data uses the Terrain prototype index as its
            // address. Reject an additional Terrain whose layout differs from
            // the profile/manager before it is queued, matching the toolbar
            // path's guard and preventing silent cross-terrain corruption.
            if (!GPUInstancerEditorConstants.IsTerrainProfileCompatibleForBinding(
                    profile, terrain, detail))
            {
                Debug.LogWarning(
                    "GPU Instancer did not bind Terrain '" + terrain.name +
                    "' because its " + (detail ? "detail" : "tree") +
                    " prototypes do not match the Profile.", terrain);
                return;
            }
            profile.InitializeAuthoringData();
            s_previewTerrains[profile.id] = terrain;

            List<Terrain> boundTerrains = manager.GetTerrains();
            if (boundTerrains == null || boundTerrains.Count == 0)
            {
                manager.SetupManagerWithTerrain(terrain);
            }
            else if (manager.terrain == terrain)
            {
                // SetupManagerWithTerrain has useful editor-side Proxy repair,
                // but calling it for an already bound terrain would regenerate
                // terrain data unnecessarily. AddProxyToTerrain only updates
                // the persisted profile id and is safe here.
                manager.AddProxyToTerrain();
            }
            else if (!boundTerrains.Contains(terrain))
            {
                manager.AddTerrain(terrain);
                if (manager.additionalTerrains == null)
                    manager.additionalTerrains = new List<Terrain>();
                if (!manager.additionalTerrains.Contains(terrain))
                    manager.additionalTerrains.Add(terrain);
                BindProxyForTerrain(profile, manager, terrain, detail);
            }

            GPUInstancerProjectSettings settings = GetSettings(profileProperty);
            MarkSettingsDirty(settings);
        }

        private static void BindProxyForTerrain(
            GPUInstancerProfile profile,
            GPUInstancerTerrainManager manager,
            Terrain terrain,
            bool detail)
        {
            if (Application.isPlaying || terrain == null)
                return;
            GPUInstancerTerrainProxy proxy = terrain.GetComponent<GPUInstancerTerrainProxy>();
            if (proxy == null)
            {
                Undo.RecordObject(terrain.gameObject, "Add GPU Instancer Terrain Proxy");
                proxy = terrain.gameObject.AddComponent<GPUInstancerTerrainProxy>();
                if (proxy != null)
                    Undo.RegisterCreatedObjectUndo(proxy, "Add GPU Instancer Terrain Proxy");
            }
            if (proxy == null)
                return;
            Undo.RecordObject(proxy, "Bind GPU Instancer Terrain Profile");
            proxy.SetProfileId(profile, detail);
            proxy.SetRuntimeManager(manager, detail);
            EditorUtility.SetDirty(proxy);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.LabelField(position, label);
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }

    /// <summary>Prefab channel profile affordances.</summary>
    public static class GPUInstancerPrefabProfileDrawer
    {
        public static void Draw(SerializedProperty property)
        {
            GPUInstancerPrefabProfile profile = property != null
                ? property.managedReferenceValue as GPUInstancerPrefabProfile
                : null;
            if (profile == null)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Prefab Channel", EditorStyles.miniBoldLabel);

            DrawPrefabList(property, profile);

            int persistentRegistered = 0;
            int sceneRegistered = 0;
            if (profile.registeredPrefabs != null)
            {
                for (int i = 0; i < profile.registeredPrefabs.Count; i++)
                {
                    RegisteredPrefabsData data = profile.registeredPrefabs[i];
                    if (data == null || data.registeredPrefabs == null)
                        continue;
                    for (int j = 0; j < data.registeredPrefabs.Count; j++)
                    {
                        GPUInstancerPrefab instance = data.registeredPrefabs[j];
                        if (instance == null)
                            continue;
                        if (GPUInstancerProfileEditorUtility.IsPersistentPrefabComponent(instance))
                            persistentRegistered++;
                        else
                            sceneRegistered++;
                    }
                }
            }

            EditorGUILayout.LabelField(
                "Registered Prefab Assets",
                persistentRegistered.ToString(),
                EditorStyles.miniLabel);
            if (sceneRegistered > 0)
            {
                EditorGUILayout.HelpBox(
                    sceneRegistered + " scene instance reference(s) are not valid in Project Settings.",
                    MessageType.Warning);
                if (!Application.isPlaying && GUILayout.Button("Remove Scene References"))
                {
                    GPUInstancerProfileDrawer.ApplySerializedProfileChanges(property);
                    RemoveSceneRegisteredPrefabs(profile, property);
                    GUIUtility.ExitGUI();
                }
            }

            DrawRegisteredPrefabRuntimeBox(profile);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!profile.enabled || Application.isPlaying))
            {
                if (GUILayout.Button("Object Picker"))
                {
                    GPUInstancerProfileDrawer.ApplySerializedProfileChanges(property);
                    int pickerId = GUIUtility.GetControlID(FocusType.Passive) + 100;
                    GPUInstancerProfileDrawer.BeginPrefabPicker(profile, pickerId);
                }
                if (GUILayout.Button("Generate Prototypes"))
                {
                    GPUInstancerProfileDrawer.ApplySerializedProfileChanges(property);
                    GPUInstancerProfileDrawer.GeneratePrefabPrototypes(profile, property);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();

            GPUInstancerProfileDrawer.HandlePrefabPickerEvent(profile, property);
            EditorGUILayout.EndVertical();
        }

        private static void DrawRegisteredPrefabRuntimeBox(GPUInstancerPrefabProfile profile)
        {
            if (profile == null)
                return;

            GPUInstancerManager rawManager;
            if (!GPUInstancerDriver.TryGet(profile, out rawManager) || rawManager == null)
                return;

            List<GPUInstancerRuntimeData> runtimeData = rawManager.runtimeDataList;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Registered Prefabs", EditorStyles.miniBoldLabel);
            if (runtimeData == null || runtimeData.Count == 0)
            {
                EditorGUILayout.LabelField("No registered prefabs.", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            int totalInstances = 0;
            int totalDrawCalls = 0;
            for (int i = 0; i < runtimeData.Count; i++)
            {
                GPUInstancerRuntimeData data = runtimeData[i];
                if (data == null)
                    continue;

                int drawCalls = 0;
                if (data.instanceLODs != null)
                {
                    for (int lod = 0; lod < data.instanceLODs.Count; lod++)
                    {
                        GPUInstancerPrototypeLOD runtimeLod = data.instanceLODs[lod];
                        if (runtimeLod == null || runtimeLod.renderers == null)
                            continue;
                        for (int renderer = 0; renderer < runtimeLod.renderers.Count; renderer++)
                        {
                            GPUInstancerRenderer gpuiRenderer = runtimeLod.renderers[renderer];
                            if (gpuiRenderer != null && gpuiRenderer.materials != null)
                                drawCalls += gpuiRenderer.materials.Count;
                        }
                    }
                }

                totalInstances += data.instanceCount;
                totalDrawCalls += drawCalls;
                string name = data.prototype == null ? "Prototype" : data.prototype.ToString();
                EditorGUILayout.LabelField(
                    name + "  |  Instances: " + data.instanceCount + "  |  Draw Calls: " + drawCalls,
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField(
                "Total  |  Instances: " + totalInstances + "  |  Draw Calls: " + totalDrawCalls,
                EditorStyles.boldLabel);
            EditorGUILayout.EndVertical();
        }

        private static void DrawPrefabList(SerializedProperty property, GPUInstancerPrefabProfile profile)
        {
            SerializedProperty prefabList = property.FindPropertyRelative("prefabList");
            if (prefabList == null)
                return;

            EditorGUILayout.LabelField("Prefab Prototypes", EditorStyles.miniBoldLabel);
            for (int i = 0; i < prefabList.arraySize; i++)
            {
                SerializedProperty element = prefabList.GetArrayElementAtIndex(i);
                GameObject before = element.objectReferenceValue as GameObject;
                EditorGUILayout.BeginHorizontal();
                GameObject after = (GameObject)EditorGUILayout.ObjectField(
                    before, typeof(GameObject), false);
                if (after != before)
                {
                    GameObject normalized;
                    if (TryGetPersistentPrefab(after, out normalized))
                        element.objectReferenceValue = normalized;
                    else if (after != null)
                        EditorGUILayout.HelpBox("Only prefab assets can be stored in a Profile.", MessageType.Warning);
                    else
                        element.objectReferenceValue = null;
                }
                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22f)))
                {
                    prefabList.DeleteArrayElementAtIndex(i);
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Add Prefab Asset", EditorStyles.miniButton))
            {
                prefabList.arraySize++;
                prefabList.GetArrayElementAtIndex(prefabList.arraySize - 1).objectReferenceValue = null;
            }
        }

        internal static bool TryGetPersistentPrefab(GameObject candidate, out GameObject normalized)
        {
            normalized = candidate;
            if (candidate == null)
                return false;

            if (!EditorUtility.IsPersistent(normalized) && PrefabUtility.IsPartOfPrefabInstance(normalized))
            {
                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(normalized);
                if (source != null)
                    normalized = source;
            }

            if (!EditorUtility.IsPersistent(normalized))
                return false;

            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(normalized);
            return prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant;
        }

        private static void RemoveSceneRegisteredPrefabs(GPUInstancerPrefabProfile profile, SerializedProperty property)
        {
            if (profile.registeredPrefabs == null)
                return;
            GPUInstancerProjectSettings settings = GPUInstancerProfileDrawer.GetSettings(property);
            GPUInstancerProfileDrawer.RecordSettings(settings, "Remove scene prefab references");
            for (int i = profile.registeredPrefabs.Count - 1; i >= 0; i--)
            {
                RegisteredPrefabsData data = profile.registeredPrefabs[i];
                if (data == null)
                {
                    profile.registeredPrefabs.RemoveAt(i);
                    continue;
                }
                if (data.registeredPrefabs == null)
                    continue;
                data.registeredPrefabs.RemoveAll(instance =>
                    !GPUInstancerProfileEditorUtility.IsPersistentPrefabComponent(instance));
            }
            GPUInstancerProfileDrawer.MarkSettingsDirty(settings);
        }
    }

    /// <summary>Tree channel profile affordances.</summary>
    public static class GPUInstancerTreeProfileDrawer
    {
        public static void Draw(SerializedProperty property)
        {
            GPUInstancerTreeProfile profile = property != null
                ? property.managedReferenceValue as GPUInstancerTreeProfile
                : null;
            if (profile == null)
                return;

            GPUInstancerProfileDrawer.DrawTerrainBinding(profile, property, false);
            GPUInstancerProfileDrawer.DrawTerrainGlobalValues(profile, property, false);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!profile.enabled || Application.isPlaying))
            {
                if (GUILayout.Button("Generate Terrain Prototypes"))
                {
                    ApplyAndGenerateTerrain(profile, property, false);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void ApplyAndGenerateTerrain(GPUInstancerTreeProfile profile, SerializedProperty property, bool detail)
        {
            GPUInstancerProfileDrawer.ApplySerializedProfileChanges(property);
            GPUInstancerTerrainManager manager = GPUInstancerProfileDrawer.GetTerrainManager(profile, property);
            if (manager == null || !GPUInstancerProfileDrawer.EnsureTerrainBinding(profile, property, manager))
                return;
            GPUInstancerProjectSettings settings = GPUInstancerProfileDrawer.GetSettings(property);
            GPUInstancerProfileDrawer.RecordSettings(settings, "Generate tree prototypes");
            manager.GeneratePrototypes(true);
            GPUInstancerProfileDrawer.MarkSettingsDirty(settings);
        }
    }

    /// <summary>Detail channel profile affordances.</summary>
    public static class GPUInstancerDetailProfileDrawer
    {
        public static void Draw(SerializedProperty property)
        {
            GPUInstancerDetailProfile profile = property != null
                ? property.managedReferenceValue as GPUInstancerDetailProfile
                : null;
            if (profile == null)
                return;

            GPUInstancerProfileDrawer.DrawTerrainBinding(profile, property, true);
            GPUInstancerProfileDrawer.DrawTerrainGlobalValues(profile, property, true);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!profile.enabled || Application.isPlaying))
            {
                if (GUILayout.Button("Generate Terrain Prototypes"))
                {
                    ApplyAndGenerateTerrain(profile, property, true);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void ApplyAndGenerateTerrain(GPUInstancerDetailProfile profile, SerializedProperty property, bool detail)
        {
            GPUInstancerProfileDrawer.ApplySerializedProfileChanges(property);
            GPUInstancerTerrainManager manager = GPUInstancerProfileDrawer.GetTerrainManager(profile, property);
            if (manager == null || !GPUInstancerProfileDrawer.EnsureTerrainBinding(profile, property, manager))
                return;
            GPUInstancerProjectSettings settings = GPUInstancerProfileDrawer.GetSettings(property);
            GPUInstancerProfileDrawer.RecordSettings(settings, "Generate detail prototypes");
            manager.GeneratePrototypes(true);
            GPUInstancerProfileDrawer.MarkSettingsDirty(settings);
        }
    }
}
