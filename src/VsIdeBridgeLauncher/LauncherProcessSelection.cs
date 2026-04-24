using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

namespace VsIdeBridgeLauncher
{
    internal static class LauncherProcessSelection
    {
        public static int? SelectNewestLaunchedProcessId(IEnumerable<LauncherProcessSnapshot> snapshots, ISet<int> existingProcessIds)
        {
            LauncherProcessSnapshot? bestSnapshot = null;

            foreach (LauncherProcessSnapshot snapshot in snapshots)
            {
                if (existingProcessIds.Contains(snapshot.ProcessId))
                {
                    continue;
                }

                if (!bestSnapshot.HasValue || IsPreferred(snapshot, bestSnapshot.Value))
                {
                    bestSnapshot = snapshot;
                }
            }

            return bestSnapshot.HasValue ? bestSnapshot.Value.ProcessId : (int?)null;
        }

        public static LauncherStartupEvaluation EvaluateStartupProgress(
            int primaryProcessId,
            bool primaryHasExited,
            int? primaryExitCode,
            IEnumerable<LauncherProcessSnapshot> snapshots,
            ISet<int> existingProcessIds,
            bool timedOut,
            int timeoutMilliseconds)
        {
            List<LauncherProcessSnapshot> snapshotList = [.. snapshots];
            int? launchedProcessId = SelectNewestLaunchedProcessId(snapshotList, existingProcessIds);
            int activeProcessId = launchedProcessId ?? primaryProcessId;

            for (int index = 0; index < snapshotList.Count; index++)
            {
                LauncherProcessSnapshot snapshot = snapshotList[index];
                if (snapshot.ProcessId == activeProcessId && GetReadinessRank(snapshot) > 0)
                {
                    return LauncherStartupEvaluation.Succeeded(activeProcessId);
                }
            }

            if (primaryHasExited && !launchedProcessId.HasValue)
            {
                return LauncherStartupEvaluation.Failed(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Visual Studio exited before startup completed. ExitCode={0}.",
                        primaryExitCode.HasValue ? primaryExitCode.Value.ToString(CultureInfo.InvariantCulture) : "unknown"));
            }

            if (timedOut)
            {
                return LauncherStartupEvaluation.Failed(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Visual Studio launched as PID {0} but never showed a main window or registered the bridge within {1} ms.",
                        activeProcessId,
                        timeoutMilliseconds));
            }

            return LauncherStartupEvaluation.Continue(activeProcessId);
        }

        public static IReadOnlyList<string> NormalizeTempRoots(IEnumerable<string> candidates)
        {
            List<string> tempRoots = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string normalized = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalized.Length == 0)
                {
                    normalized = candidate;
                }

                if (seen.Add(normalized))
                {
                    tempRoots.Add(normalized);
                }
            }

            return tempRoots;
        }

        public static IReadOnlyList<string> BuildDiscoveryFileCandidates(IEnumerable<string> tempRoots, int processId)
        {
            List<string> paths = [];
            foreach (string tempRoot in NormalizeTempRoots(tempRoots))
            {
                paths.Add(Path.Combine(
                    tempRoot,
                    "vs-ide-bridge",
                    "pipes",
                    string.Format(CultureInfo.InvariantCulture, "bridge-{0}.json", processId)));
            }

            return paths;
        }

        private static bool IsPreferred(LauncherProcessSnapshot candidate, LauncherProcessSnapshot currentBest)
        {
            int candidateRank = GetReadinessRank(candidate);
            int currentRank = GetReadinessRank(currentBest);
            if (candidateRank != currentRank)
            {
                return candidateRank > currentRank;
            }

            if (candidate.StartTimeUtc != currentBest.StartTimeUtc)
            {
                return candidate.StartTimeUtc > currentBest.StartTimeUtc;
            }

            return candidate.ProcessId > currentBest.ProcessId;
        }

        private static int GetReadinessRank(LauncherProcessSnapshot snapshot)
        {
            if (snapshot.HasBridgeDiscovery)
            {
                return 2;
            }

            if (snapshot.HasMainWindow)
            {
                return 1;
            }

            return 0;
        }
    }

    internal readonly struct LauncherProcessSnapshot(int processId, DateTime startTimeUtc, bool hasBridgeDiscovery, bool hasMainWindow)
    {
        public int ProcessId { get; } = processId;

        public DateTime StartTimeUtc { get; } = startTimeUtc;

        public bool HasBridgeDiscovery { get; } = hasBridgeDiscovery;

        public bool HasMainWindow { get; } = hasMainWindow;
    }

    internal readonly struct LauncherStartupEvaluation
    {
        private LauncherStartupEvaluation(LauncherStartupState state, int activeProcessId, string error)
        {
            State = state;
            ActiveProcessId = activeProcessId;
            Error = error;
        }

        public LauncherStartupState State { get; }

        public int ActiveProcessId { get; }

        public string Error { get; }

        public static LauncherStartupEvaluation Continue(int activeProcessId)
        {
            return new LauncherStartupEvaluation(LauncherStartupState.Continue, activeProcessId, string.Empty);
        }

        public static LauncherStartupEvaluation Succeeded(int activeProcessId)
        {
            return new LauncherStartupEvaluation(LauncherStartupState.Succeeded, activeProcessId, string.Empty);
        }

        public static LauncherStartupEvaluation Failed(string error)
        {
            return new LauncherStartupEvaluation(LauncherStartupState.Failed, 0, error);
        }
    }

    internal enum LauncherStartupState
    {
        Continue,
        Succeeded,
        Failed,
    }
}
