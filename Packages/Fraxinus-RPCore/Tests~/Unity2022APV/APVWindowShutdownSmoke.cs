// Copy into Assets/Editor in the disposable Unity 2022 test project.
// APV_WINDOW_SHUTDOWN_SMOKE=1 Unity.exe -batchmode -force-d3d11 -projectPath ...
//   -executeMethod APVWindowShutdownSmoke.Run -logFile ...
// Do not add -quit. Verify exit code 0 and the absence of native fatal errors
// in the complete log; the JSON is necessarily written before native shutdown.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class APVWindowShutdownSmoke
{
    const string ReportPath = "apv-window-shutdown-result.json";
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly List<string> Errors = new List<string>();
    static EditorWindow s_Window;
    static Type s_WindowType, s_PanelType, s_WorkflowType;
    static int s_Frame;
    static readonly Report Result = new Report();

    [Serializable]
    sealed class Report
    {
        public bool opened, normallyClosed, reopened, exitRequested, quittingObserved;
        public string exception;
        public string[] errors;
    }

    public static void Run()
    {
        if (!Application.isBatchMode || Environment.GetEnvironmentVariable("APV_WINDOW_SHUTDOWN_SMOKE") != "1")
            throw new InvalidOperationException("Run only in a disposable batch project with APV_WINDOW_SHUTDOWN_SMOKE=1.");

        Application.logMessageReceived += OnLog;
        EditorApplication.quitting += OnQuitting;
        try
        {
            s_WindowType = FindType("UnityEngine.Rendering.ProbeVolumeBakingWindow");
            s_PanelType = FindType("UnityEngine.Rendering.ProbeVolumeBakingPanel");
            s_WorkflowType = FindType("UnityEngine.Rendering.ProbeVolumeBakingWorkflow");
            s_Window = EditorWindow.GetWindow(s_WindowType);
            Require(s_Window != null && s_WindowType.GetField("m_Panel", Flags).GetValue(s_Window) != null,
                "The Probe Volumes window did not initialize its panel.");
            Result.opened = true;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Fail(exception); }
    }

    static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType(name)).First(type => type != null);

    static void Tick()
    {
        try
        {
            ++s_Frame;
            if (s_Frame == 5)
            {
                s_Window.Close();
                Require(s_PanelType.GetField("instance", Flags).GetValue(null) == null,
                    "Normal window close did not release the active panel.");
                Require(s_WorkflowType.GetField("current", Flags).GetValue(null) == null,
                    "Normal window close did not release the active baking context.");
                Result.normallyClosed = true;
            }
            if (s_Frame == 10)
            {
                s_Window = EditorWindow.GetWindow(s_WindowType);
                Require(s_Window != null && s_WindowType.GetField("m_Panel", Flags).GetValue(s_Window) != null,
                    "Reopening the Probe Volumes window failed.");
                Result.reopened = true;
            }
            if (s_Frame == 20)
            {
                Require(Errors.Count == 0, "The window lifecycle logged errors.");
                Result.exitRequested = true;
                EditorApplication.update -= Tick;
                WriteReport();
                Debug.Log("APV_WINDOW_SHUTDOWN_SMOKE exiting with the Probe Volumes window open.");
                // This intentionally leaves the window open. Unity will invoke
                // its OnDisable after beginning native manager teardown.
                EditorApplication.Exit(0);
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    static void OnQuitting()
    {
        Result.quittingObserved = true;
        WriteReport();
    }

    static void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
            Errors.Add(condition + "\n" + stackTrace);
    }

    static void Fail(Exception exception)
    {
        Result.exception = exception.ToString();
        EditorApplication.update -= Tick;
        WriteReport();
        EditorApplication.Exit(1);
    }

    static void WriteReport()
    {
        Result.errors = Errors.ToArray();
        File.WriteAllText(ReportPath, JsonUtility.ToJson(Result, true));
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
