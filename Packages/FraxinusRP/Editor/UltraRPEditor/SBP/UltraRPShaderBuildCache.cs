using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEditor.Rendering;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    // Optional integration: this assembly exists only when SBP is installed.
    // Addressables' normal SBP pipeline invokes PostPacking before cached writes.
    [InitializeOnLoad]
    [VersionedCallback(1)]
    internal sealed class UltraRPSBPShaderCallbacks : IPreprocessShaders
    {
        static UltraRPSBPShaderCallbacks() => UltraRPShaderBuildCache.InstallCallbacks();
        public UltraRPSBPShaderCallbacks() => UltraRPShaderBuildCache.InstallCallbacks();
        public int callbackOrder => int.MinValue + 1;
        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data) { }
    }

    public static class UltraRPShaderBuildCache
    {
        sealed class CallbackState
        {
            public Func<IBuildParameters, IBuildResults, ReturnCode> scripts;
            public Func<IBuildParameters, IDependencyData, IWriteData, ReturnCode> packing;
            public Func<IBuildParameters, IDependencyData, IWriteData, IBuildResults, ReturnCode> writing;
        }

        sealed class OperationState
        {
            public Hash128 original;
            public Hash128 applied;
        }

        static readonly ConditionalWeakTable<BuildCallbacks, CallbackState> s_callbacks = new();
        static readonly ConditionalWeakTable<IWriteOperation, OperationState> s_operations = new();

        internal static void InstallCallbacks()
        {
            var callbacks = ContentPipeline.BuildCallbacks;
            if (callbacks == null)
                return;
            var state = s_callbacks.GetValue(callbacks, _ => new CallbackState());
            if (state.scripts == null || callbacks.PostScriptsCallbacks != state.scripts)
            {
                var previous = callbacks.PostScriptsCallbacks;
                state.scripts = (parameters, results) =>
                {
                    // This task runs for EVERY standard SBP build after platform
                    // switching and before dependency calculation, even when
                    // Unity reuses callback instances with identical flags.
                    var result = previous == null ? ReturnCode.Success : previous(parameters, results);
                    if (result < ReturnCode.Success)
                    {
                        UltraRPShaderBuildSession.EndScriptableBuild("SBP script callback failed");
                        return result;
                    }
                    UltraRPShaderBuildSession.BeginScriptableBuild(parameters.Target,
                        Path.GetDirectoryName(parameters.GetOutputFilePathForIdentifier("FraxinusShaderBuildReport.json")));
                    return result;
                };
                callbacks.PostScriptsCallbacks = state.scripts;
            }
            if (state.packing == null || callbacks.PostPackingCallback != state.packing)
            {
                var previous = callbacks.PostPackingCallback;
                state.packing = (parameters, dependencies, writeData) =>
                {
                    var result = previous == null ? ReturnCode.Success : previous(parameters, dependencies, writeData);
                    if (result < ReturnCode.Success)
                    {
                        UltraRPShaderBuildSession.EndScriptableBuild("SBP packing callback failed");
                        return result;
                    }
                    Apply(writeData);
                    return result;
                };
                callbacks.PostPackingCallback = state.packing;
            }
            if (state.writing == null || callbacks.PostWritingCallback != state.writing)
            {
                var previous = callbacks.PostWritingCallback;
                state.writing = (parameters, dependencies, writeData, results) =>
                {
                    string status = "SBP writing callback failed";
                    try
                    {
                        var result = previous == null ? ReturnCode.Success : previous(parameters, dependencies, writeData, results);
                        if (result >= ReturnCode.Success)
                            status = "SBP writing completed";
                        return result;
                    }
                    finally
                    {
                        UltraRPShaderBuildSession.EndScriptableBuild(status);
                    }
                };
                callbacks.PostWritingCallback = state.writing;
            }
        }

        /// <summary>
        /// Adds shader-strip settings to write-cache keys without clearing any
        /// compiler cache. Call after generating write commands and before
        /// WriteSerializedFiles in custom SBP task lists that omit PostPacking.
        /// Standard SBP/Addressables callbacks call this automatically.
        /// </summary>
        public static void Apply(IWriteData writeData)
        {
            if (writeData == null)
                throw new ArgumentNullException(nameof(writeData));
            UltraRPShaderBuildSession.EnsureForShaderCallbacks();
            var configuration = UltraRPShaderBuildConfiguration.GetHash();
            if (!configuration.isValid)
                return;
            foreach (var operation in writeData.WriteOperations)
            {
                var state = s_operations.GetValue(operation, _ => new OperationState());
                // Preserve any upstream dependency changes while remaining
                // idempotent if another callback forwards through this wrapper.
                if (operation.DependencyHash != state.applied)
                    state.original = operation.DependencyHash;
                // SBP 1.x recursively reflects unsupported scalar types. A raw
                // System.Guid contains byte fields, whose reflected value loops
                // back to byte; pass the supported string representation instead.
                state.applied = HashingMethods.Calculate(state.original, configuration,
                    typeof(UltraRPShaderBuildCache).Assembly.ManifestModule.ModuleVersionId.ToString()).ToHash128();
                operation.DependencyHash = state.applied;
            }
        }
    }
}
