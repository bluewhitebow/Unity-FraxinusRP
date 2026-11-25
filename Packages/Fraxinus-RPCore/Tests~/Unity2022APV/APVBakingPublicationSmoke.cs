// Copy into Assets/Editor in the disposable Unity 2022 test project.
// APV_PUBLICATION_SMOKE=1 Unity.exe -batchmode -projectPath ...
//   -executeMethod APVBakingPublicationSmoke.Run -logFile ...
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class APVBakingPublicationSmoke
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    const string Folder = "Assets/APVPublicationSmoke";
    static Type s_TransactionType;
    static readonly List<string> Checks = new List<string>();

    [Serializable]
    sealed class Report
    {
        public bool passed;
        public string error;
        public string[] checks;
    }

    public static void Run()
    {
        if (!Application.isBatchMode || Environment.GetEnvironmentVariable("APV_PUBLICATION_SMOKE") != "1")
            throw new InvalidOperationException("Run only in a disposable batch project with APV_PUBLICATION_SMOKE=1.");

        var report = new Report();
        try
        {
            s_TransactionType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEngine.Rendering.APVBakingFileTransaction"))
                .First(type => type != null);
            if (AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "APVPublicationSmoke");
            string first = Folder + "/First.bytes", second = Folder + "/Second.bytes", added = Folder + "/Added.bytes";
            WriteAsset(first, "old-first");
            WriteAsset(second, "old-second");
            var firstGuid = AssetDatabase.AssetPathToGUID(first);
            var secondGuid = AssetDatabase.AssetPathToGUID(second);
            var firstMeta = File.ReadAllBytes(first + ".meta");
            var secondMeta = File.ReadAllBytes(second + ".meta");

            using (var transaction = NewTransaction())
            {
                Stage(transaction, first, "new-first");
                bool threw = false;
                try
                {
                    Invoke(transaction, "Stage", second, (Action<FileStream>)(stream =>
                    {
                        stream.WriteByte(17);
                        throw new IOException("Injected staging writer failure");
                    }));
                }
                catch (IOException) { threw = true; }
                Require(threw, "Injected staging write failed.");
            }
            Require(File.ReadAllText(first) == "old-first" && File.ReadAllText(second) == "old-second",
                "A staging writer failure preserves every original payload.");

            using (var transaction = NewTransaction())
            {
                Stage(transaction, first, "new-first");
                Stage(transaction, second, "new-second");
                bool threw = false;
                // Permit backup reads, deny replacement of the second output. The
                // first replacement succeeds before this controlled I/O failure.
                using (var lockedFile = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { Invoke(transaction, "Publish"); }
                    catch (IOException) { threw = true; }
                    catch (UnauthorizedAccessException) { threw = true; }
                    Require(threw && File.ReadAllText(first) == "new-first",
                        "A locked later payload fails after an earlier replacement.");
                }
                Invoke(transaction, "Rollback");
                Invoke(transaction, "ImportRestoredFiles");
            }
            Require(File.ReadAllText(first) == "old-first" && File.ReadAllText(second) == "old-second",
                "Partial publication restores every original payload.");
            Require(firstMeta.SequenceEqual(File.ReadAllBytes(first + ".meta")) &&
                secondMeta.SequenceEqual(File.ReadAllBytes(second + ".meta")) &&
                firstGuid == AssetDatabase.AssetPathToGUID(first) && secondGuid == AssetDatabase.AssetPathToGUID(second),
                "Rollback preserves existing .meta bytes and asset GUIDs.");

            using (var transaction = NewTransaction())
            {
                Stage(transaction, first, "generation-one");
                Stage(transaction, added, "new-asset");
                Invoke(transaction, "Publish");
                Invoke(transaction, "ImportPublishedFiles");
                Stage(transaction, first, "generation-two");
                Invoke(transaction, "Publish");
                Invoke(transaction, "ImportPublishedFiles");
                Require(File.ReadAllText(first) == "generation-two" && File.Exists(added + ".meta"),
                    "Dilation-style repeated publication and new asset import succeeded.");
                Invoke(transaction, "Rollback");
                Invoke(transaction, "ImportRestoredFiles");
            }
            Require(File.ReadAllText(first) == "old-first" && !File.Exists(added) && !File.Exists(added + ".meta") &&
                AssetDatabase.LoadAssetAtPath<TextAsset>(added) == null,
                "Repeated publication rolls back to the original generation and removes a newly imported asset.");

            using (var transaction = NewTransaction())
            {
                Stage(transaction, first, "committed-first");
                Stage(transaction, second, "committed-second");
                Invoke(transaction, "Publish");
                Invoke(transaction, "ImportPublishedFiles");
                Invoke(transaction, "Commit");
            }
            Require(AssetDatabase.LoadAssetAtPath<TextAsset>(first).text == "committed-first" &&
                AssetDatabase.LoadAssetAtPath<TextAsset>(second).text == "committed-second" &&
                firstGuid == AssetDatabase.AssetPathToGUID(first) && secondGuid == AssetDatabase.AssetPathToGUID(second),
                "Successful commit publishes both files without changing GUIDs.");
            report.passed = true;
        }
        catch (Exception exception) { report.error = exception.ToString(); }
        finally
        {
            report.checks = Checks.ToArray();
            File.WriteAllText("apv-publication-result.json", JsonUtility.ToJson(report, true));
            AssetDatabase.DeleteAsset(Folder);
            EditorApplication.Exit(report.passed ? 0 : 1);
        }
    }

    static IDisposable NewTransaction() => (IDisposable)Activator.CreateInstance(s_TransactionType, true);
    static void Stage(object transaction, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Invoke(transaction, "Stage", path, (Action<FileStream>)(stream => stream.Write(bytes, 0, bytes.Length)));
    }

    static void Invoke(object target, string name, params object[] arguments)
    {
        try { s_TransactionType.GetMethod(name, Flags).Invoke(target, arguments); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    static void WriteAsset(string path, string text)
    {
        File.WriteAllText(path, text);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
        Checks.Add(message);
    }
}
