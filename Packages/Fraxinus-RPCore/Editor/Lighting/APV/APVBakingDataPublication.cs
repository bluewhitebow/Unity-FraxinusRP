using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityEngine.Rendering
{
    // Keep the previous generation until every payload, import and metadata update
    // has succeeded. Staging lives outside Assets so Unity never imports half a bake.
    internal sealed class APVBakingFileTransaction : IDisposable
    {
        sealed class Entry
        {
            internal string path, staged, backup;
            internal bool captured, existed, metaExisted, published;
        }

        [Serializable]
        sealed class RecoveryFile
        {
            public string assetPath, backupPath;
            public bool existed, published, backupAvailable;
        }

        [Serializable]
        sealed class RecoveryManifest
        {
            public List<RecoveryFile> files = new List<RecoveryFile>();
        }

        readonly string m_Directory = Path.Combine("Library", "APVBakingStaging", Guid.NewGuid().ToString("N"));
        readonly List<Entry> m_Entries = new List<Entry>();
        bool m_Completed;
        bool m_KeepRecoveryFiles;

        internal string recoveryDirectory => Path.GetFullPath(m_Directory);

        internal void Stage(string path, Action<FileStream> write)
        {
            if (m_Completed)
                throw new InvalidOperationException("This APV publication has already completed.");
            var entry = m_Entries.Find(item => item.path == path);
            if (entry == null)
            {
                entry = new Entry { path = path };
                m_Entries.Add(entry);
            }
            Directory.CreateDirectory(m_Directory);
            entry.staged = Path.Combine(m_Directory, m_Entries.IndexOf(entry) + ".next");
            using (var stream = new FileStream(entry.staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }
        }

        internal void Publish()
        {
            var paths = new List<string>();
            foreach (var entry in m_Entries)
                if (entry.staged != null)
                    paths.Add(entry.path);
            if (!AssetDatabase.MakeEditable(paths.ToArray()))
                throw new IOException("Cannot make APV output files writable: " + string.Join(", ", paths));

            // Capture every old file before changing any destination. File.Exists
            // alone hides access-denied/delete-pending errors, so use GetAttributes.
            foreach (var entry in m_Entries)
            {
                if (entry.staged == null || entry.captured)
                    continue;
                entry.existed = ExistsOrThrow(entry.path);
                entry.metaExisted = ExistsOrThrow(entry.path + ".meta");
                if (entry.existed)
                {
                    entry.backup = Path.Combine(m_Directory, m_Entries.IndexOf(entry) + ".previous");
                    File.Copy(entry.path, entry.backup);
                }
                entry.captured = true;
            }

            foreach (var entry in m_Entries)
            {
                if (entry.staged == null)
                    continue;
                // Never truncate a live payload, and leave its .meta/GUID intact.
                try
                {
                    if (entry.existed || entry.published)
                        File.Replace(entry.staged, entry.path, null);
                    else
                        File.Move(entry.staged, entry.path);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new IOException("Could not publish APV output file '" + entry.path +
                        "'. Publication staging directory: " + recoveryDirectory, exception);
                }
                entry.staged = null;
                entry.published = true;
            }
        }

        internal void ImportPublishedFiles()
        {
            foreach (var entry in m_Entries)
                if (entry.published)
                {
                    AssetDatabase.ImportAsset(entry.path, ImportAssetOptions.ForceSynchronousImport);
                    if (AssetDatabase.LoadAssetAtPath<TextAsset>(entry.path) == null)
                        throw new IOException("Could not import APV output file " + entry.path);
                }
        }

        internal void Commit()
        {
            m_Completed = true;
        }

        internal void PreserveForRecovery(string metadata)
        {
            // If draining runtime readers failed, Dispose must not attempt an
            // unsafe second rollback. Keep the remaining original files instead.
            m_Completed = true;
            m_KeepRecoveryFiles = true;
            Directory.CreateDirectory(m_Directory);
            File.WriteAllText(Path.Combine(m_Directory, "baking-set.json"), metadata);
            var manifest = new RecoveryManifest();
            foreach (var entry in m_Entries)
                manifest.files.Add(new RecoveryFile
                {
                    assetPath = entry.path,
                    backupPath = entry.backup,
                    existed = entry.existed,
                    published = entry.published,
                    backupAvailable = entry.backup != null && File.Exists(entry.backup)
                });
            File.WriteAllText(Path.Combine(m_Directory, "manifest.json"), JsonUtility.ToJson(manifest, true));
        }

        internal void Rollback()
        {
            if (m_Completed)
                return;
            var failures = new List<Exception>();
            for (int i = m_Entries.Count - 1; i >= 0; --i)
            {
                var entry = m_Entries[i];
                if (!entry.published)
                    continue;
                try
                {
                    if (entry.existed)
                    {
                        if (ExistsOrThrow(entry.path))
                            File.Replace(entry.backup, entry.path, null);
                        else
                            File.Move(entry.backup, entry.path);
                    }
                    else
                    {
                        File.Delete(entry.path);
                        if (!entry.metaExisted)
                            File.Delete(entry.path + ".meta");
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(new IOException("Could not restore APV file " + entry.path, exception));
                }
            }
            m_Completed = true;
            if (failures.Count != 0)
            {
                m_KeepRecoveryFiles = true;
                throw new AggregateException("APV publication rollback failed. Recovery files: " + recoveryDirectory, failures);
            }

        }

        internal void ImportRestoredFiles()
        {
            bool removedFiles = false;
            foreach (var entry in m_Entries)
            {
                if (!entry.published)
                    continue;
                if (entry.existed)
                    AssetDatabase.ImportAsset(entry.path, ImportAssetOptions.ForceSynchronousImport);
                else
                    removedFiles = true;
            }
            // Newly created assets can have been imported before a later failure.
            // Reconcile their removal only after all remaining files are restored.
            if (removedFiles)
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        static bool ExistsOrThrow(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new IOException("An APV output path is a directory: " + path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        public void Dispose()
        {
            try
            {
                Rollback();
            }
            finally
            {
                if (!m_KeepRecoveryFiles && Directory.Exists(m_Directory))
                    Directory.Delete(m_Directory, true);
            }
        }
    }

    internal sealed class APVBakingDataPublication : IDisposable
    {
        readonly ProbeVolumeBakingSet m_Set;
        readonly TProbeReferenceVolumeBase m_Volume;
        readonly ProbeVolumeBakedData m_OriginalData;
        readonly string m_DataSnapshot;
        readonly string m_SetSnapshot;
        readonly SerializedDictionary<int, CellDesc> m_Cells = new SerializedDictionary<int, CellDesc>();
        readonly Dictionary<string, List<int>> m_SceneCells = new Dictionary<string, List<int>>();
        readonly List<(ProbeVolumePerSceneData scene, ProbeVolumeBakingSet set)> m_Scenes = new();
        readonly bool m_ClearAssetsOnVolumeClear;
        bool m_Committed;

        internal APVBakingFileTransaction files { get; } = new APVBakingFileTransaction();

        internal APVBakingDataPublication(ProbeVolumeBakingSet set, TProbeReferenceVolumeBase volume)
        {
            var setPath = AssetDatabase.GetAssetPath(set);
            if (!string.IsNullOrEmpty(setPath) && !AssetDatabase.MakeEditable(setPath))
                throw new IOException("Cannot make APV baking set writable: " + setPath);
            m_Set = set;
            m_Volume = volume;
            m_OriginalData = set.bakedData;
            m_DataSnapshot = JsonUtility.ToJson(m_OriginalData);
            m_SetSnapshot = EditorJsonUtility.ToJson(set, true);
            m_ClearAssetsOnVolumeClear = volume.clearAssetsOnVolumeClear;
            foreach (var item in set.cellDescs)
            {
                // Partial placement remapping mutates CellDesc.index in place.
                m_Cells.Add(item.Key, JsonUtility.FromJson<CellDesc>(JsonUtility.ToJson(item.Value)));
            }
            foreach (var item in set.perSceneCellLists)
                m_SceneCells.Add(item.Key, new List<int>(item.Value));
            foreach (var scene in volume.perSceneDataList)
                m_Scenes.Add((scene, scene.serializedBakingSet));
        }

        internal void Commit()
        {
            EditorUtility.SetDirty(m_Set);
            AssetDatabase.SaveAssetIfDirty(m_Set);
            files.Commit();
            m_Committed = true;
        }

        public void Dispose()
        {
            if (m_Committed)
            {
                files.Dispose();
                return;
            }

            try
            {
                // Publication or dilation can have loaded the new generation.
                // Drain it before restoring either files or streamable descriptors.
                foreach (var item in m_Scenes)
                    if (item.scene != null)
                        item.scene.QueueSceneRemoval();
                m_Volume.Clear();
                m_Volume.PerformPendingOperations();
                m_Set.bakedData.CleanupStreamableAssets();
                m_OriginalData.CleanupStreamableAssets();

                files.Rollback();
                JsonUtility.FromJsonOverwrite(m_DataSnapshot, m_Set.bakedData);
                if (!ReferenceEquals(m_OriginalData, m_Set.bakedData))
                    JsonUtility.FromJsonOverwrite(m_DataSnapshot, m_OriginalData);
                m_Set.cellDescs = m_Cells;
                m_Set.perSceneCellLists = m_SceneCells;
                m_Volume.clearAssetsOnVolumeClear = m_ClearAssetsOnVolumeClear;
                foreach (var item in m_Scenes)
                    if (item.scene != null)
                        item.scene.serializedBakingSet = item.set;

                EditorUtility.SetDirty(m_Set);
                AssetDatabase.SaveAssetIfDirty(m_Set);
                files.ImportRestoredFiles();
                m_Volume.EnsureCurrentBakingSet(m_Set);
                foreach (var item in m_Scenes)
                    if (item.scene != null)
                        item.scene.Initialize();
                m_Volume.PerformPendingOperations();
            }
            catch (Exception exception)
            {
                try { files.PreserveForRecovery(m_SetSnapshot); }
                catch (Exception recoveryException) { Debug.LogException(recoveryException); }
                Debug.LogError("Could not completely restore the previous APV bake. Recovery files: " +
                    files.recoveryDirectory + "\n" + exception);
            }
            finally
            {
                files.Dispose();
            }
        }
    }
}
