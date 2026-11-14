using System;
using System.Collections.Generic;
using System.Linq;

namespace FraxinusEditor.Rendering.Ultra
{
    // Compare only callbacks observed in both builds. A cache hit is not evidence
    // that a shader disappeared, nor is a complete callback log a full build census.
    internal static class UltraRPShaderBuildComparison
    {
        internal const long MinimumIncrease = 1024;

        [Serializable]
        internal sealed class Baseline
        {
            public string acceptedUtc;
            public string acceptance;
            public UltraRPShaderBuildReport.Report report;
        }

        [Serializable]
        internal sealed class PassDelta
        {
            public string shaderPath;
            public string shaderName;
            public string kind;
            public string passName;
            public string passType;
            public string comparison;
            public long previousRetained = -1;
            public long currentRetained;
            public long increase;
            public double ratio;
            public bool growthWarning;
            public List<string> callbackCoverage = new List<string>();
        }

        [Serializable]
        internal sealed class Result
        {
            public string currentStartedUtc;
            public string currentStatus;
            public string baselineStartedUtc;
            public string baselineAcceptance;
            public string compatibility;
            public bool graphicsAPIsVerified;
            public string countMeaning = "Observed final managed callback counts, not native compiled variants or package bytes. Incremental builds may omit cached shaders. Only matching Shader+Pass callback stage/index multiplicities are compared; missing callbacks are never counted as removals. Matching coverage does not prove identical build content.";
            public int comparablePasses;
            public int unmatchedCurrentPasses;
            public int baselinePassesNotObserved;
            public List<PassDelta> passes = new List<PassDelta>();
        }

        sealed class Group
        {
            internal readonly PassDelta row;
            internal readonly List<string> coverage = new List<string>();
            internal bool complete = true;

            internal Group(UltraRPShaderBuildReport.Batch batch)
            {
                row = new PassDelta { shaderPath = batch.shaderPath, shaderName = batch.shaderName,
                    kind = batch.kind, passName = batch.passName, passType = batch.passType };
            }
        }

        internal static bool IsComplete(UltraRPShaderBuildReport.Report report)
            => report != null && !string.IsNullOrEmpty(report.finishedUtc) &&
               report.batches != null && report.batches.Count > 0 &&
               report.completedBatches == report.batches.Count &&
               report.batches.TrueForAll(batch => batch != null && batch.finalRetained >= 0);

        internal static bool CanAccept(UltraRPShaderBuildReport.Report report, bool explicitlyAccepted)
            => IsComplete(report) && (IsSuccessful(report.status) ||
                (explicitlyAccepted && string.Equals(report.status, "Unknown", StringComparison.OrdinalIgnoreCase)));

        internal static bool IsSuccessful(string status)
            => string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "bundle succeeded", StringComparison.OrdinalIgnoreCase);

        internal static bool ShouldAutoAccept(UltraRPShaderBuildReport.Report report, Baseline baseline)
        {
            if (!CanAccept(report, false)) return false;
            if (baseline == null) return true;
            var comparison = Compare(report, baseline);
            // A partial incremental build must not erase the other baseline
            // passes. Intentional growth/new coverage can be accepted explicitly.
            return comparison.compatibility == "Comparable observed passes" &&
                comparison.baselinePassesNotObserved == 0 && comparison.unmatchedCurrentPasses == 0 &&
                comparison.passes.TrueForAll(pass => !pass.growthWarning);
        }

        internal static string PassKey(PassDelta row)
            => string.Join("\u001f", row.kind, row.shaderPath, row.shaderName, row.passName, row.passType);

        internal static string Compatibility(UltraRPShaderBuildReport.Report current, UltraRPShaderBuildReport.Report previous)
        {
            if (previous == null) return "No accepted baseline";
            if (current.target != previous.target) return "Different build targets";
            if (current.buildKind != previous.buildKind) return "Different build kinds";
            if (!string.IsNullOrEmpty(current.graphicsAPIs) && !string.IsNullOrEmpty(previous.graphicsAPIs) &&
                current.graphicsAPIs != previous.graphicsAPIs) return "Different graphics APIs";
            if (current.schemaVersion >= 2 && previous.schemaVersion >= 2 &&
                current.developmentBuild != previous.developmentBuild) return "Different development build settings";
            return "Comparable observed passes";
        }

        internal static Result Compare(UltraRPShaderBuildReport.Report current, Baseline baseline)
        {
            var previous = baseline?.report;
            var result = new Result
            {
                currentStartedUtc = current.startedUtc, currentStatus = current.status,
                baselineStartedUtc = previous?.startedUtc, baselineAcceptance = baseline?.acceptance,
                compatibility = Compatibility(current, previous),
                graphicsAPIsVerified = previous != null && !string.IsNullOrEmpty(previous.graphicsAPIs) &&
                    !string.IsNullOrEmpty(current.graphicsAPIs)
            };
            var currentGroups = Aggregate(current);
            var previousGroups = previous == null ? new Dictionary<string, Group>() : Aggregate(previous);
            bool compatible = result.compatibility == "Comparable observed passes";
            foreach (var pair in currentGroups)
            {
                var group = pair.Value;
                var row = group.row;
                row.callbackCoverage.AddRange(group.coverage);
                result.passes.Add(row);
                if (!compatible)
                    row.comparison = result.compatibility;
                else if (!group.complete)
                    row.comparison = "Current callbacks incomplete";
                else if (!previousGroups.TryGetValue(pair.Key, out var old))
                    row.comparison = "No baseline callbacks for this pass";
                else if (!old.complete || !group.coverage.SequenceEqual(old.coverage))
                    row.comparison = "Different callback coverage; no count delta";
                else
                {
                    row.comparison = "Comparable observed callbacks";
                    row.previousRetained = old.row.currentRetained;
                    row.increase = row.currentRetained - row.previousRetained;
                    row.ratio = row.previousRetained > 0 ? (double)row.currentRetained / row.previousRetained : 0;
                    row.growthWarning = row.increase >= MinimumIncrease &&
                        (row.previousRetained == 0 || row.currentRetained / 2 >= row.previousRetained);
                    ++result.comparablePasses;
                    continue;
                }
                ++result.unmatchedCurrentPasses;
            }
            if (compatible)
                result.baselinePassesNotObserved = previousGroups.Keys.Count(key => !currentGroups.ContainsKey(key));
            result.passes.Sort((a, b) => b.increase.CompareTo(a.increase));
            return result;
        }

        static Dictionary<string, Group> Aggregate(UltraRPShaderBuildReport.Report report)
        {
            var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            if (report.batches == null) return groups;
            foreach (var batch in report.batches)
            {
                if (batch == null) continue;
                var candidate = new Group(batch);
                string key = PassKey(candidate.row);
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = candidate);
                group.coverage.Add(string.Join("/", batch.shaderStage, batch.subshaderIndex, batch.passIndex));
                group.complete &= batch.finalRetained >= 0;
                if (batch.finalRetained >= 0) group.row.currentRetained += batch.finalRetained;
            }
            foreach (var group in groups.Values) group.coverage.Sort(StringComparer.Ordinal);
            return groups;
        }
    }
}
