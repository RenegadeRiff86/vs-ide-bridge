using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Globalization;

namespace VsIdeBridgeLauncher
{
    internal static class Program
    {
        private const string DevenvPathOption = "--devenv-path";
        private const string SolutionOption = "--solution";
        private const string ResultFileOption = "--result-file";
        private const string BrokerPipeOption = "--broker-pipe";
        private const int BrokerConnectPollMilliseconds = 250;
        private const int BrokerIdleTimeoutMilliseconds = 300_000;
        private const int StartupValidationTimeoutMilliseconds = 30_000;
        private const int StartupPollMilliseconds = 250;
        private const int ProcessExitWaitMilliseconds = 5000;
        private static readonly char[] QuoteArgumentSpecialCharacters = [' ', '\t', '"'];

        private static int Main(string[] args)
        {
            string resultFile = null;
            try
            {
                return Run(args, ref resultFile);
            }
            catch (ArgumentException ex)
            {
                return LauncherResultWriter.Fail(resultFile, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return LauncherResultWriter.Fail(resultFile, ex.Message);
            }
            catch (IOException ex)
            {
                return LauncherResultWriter.Fail(resultFile, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return LauncherResultWriter.Fail(resultFile, ex.Message);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return LauncherResultWriter.Fail(resultFile, ex.Message);
            }
        }

        private static int Run(string[] args, ref string resultFile)
        {
            Dictionary<string, string> options = ParseOptions(args);
            if (!options.TryGetValue(DevenvPathOption, out string devenvPath) || string.IsNullOrWhiteSpace(devenvPath))
            {
                return LauncherResultWriter.Fail(resultFile, "Missing required argument '--devenv-path'.");
            }

            options.TryGetValue(ResultFileOption, out resultFile);
            options.TryGetValue(SolutionOption, out string solutionPath);

            if (options.TryGetValue(BrokerPipeOption, out string brokerPipeName) && !string.IsNullOrWhiteSpace(brokerPipeName))
            {
                return RunBroker(brokerPipeName);
            }

            LaunchAttemptResult launchAttempt = LaunchVisualStudio(devenvPath, solutionPath);
            if (!launchAttempt.IsSuccessful || !launchAttempt.ProcessId.HasValue)
            {
                TryTerminate(launchAttempt.LaunchedProcesses);
                return LauncherResultWriter.Fail(resultFile, launchAttempt.Error ?? "Visual Studio launch failed.");
            }

            LauncherResultWriter.WriteResult(resultFile, true, launchAttempt.ProcessId, null);
            return 0;
        }

        private static int RunBroker(string pipeName)
        {
            DateTime lastActivityUtc = DateTime.UtcNow;

            while (true)
            {
                using NamedPipeServerStream server = new(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

                if (!TryWaitForBrokerConnection(server, ref lastActivityUtc))
                {
                    return 0;
                }

                HandleBrokerConnection(server);
            }
        }

        private static bool TryWaitForBrokerConnection(NamedPipeServerStream server, ref DateTime lastActivityUtc)
        {
            IAsyncResult waitResult = server.BeginWaitForConnection(null, null);
            while (!waitResult.AsyncWaitHandle.WaitOne(BrokerConnectPollMilliseconds))
            {
                if ((DateTime.UtcNow - lastActivityUtc).TotalMilliseconds >= BrokerIdleTimeoutMilliseconds)
                {
                    return false;
                }
            }

            server.EndWaitForConnection(waitResult);
            lastActivityUtc = DateTime.UtcNow;
            return true;
        }

        private static void HandleBrokerConnection(NamedPipeServerStream server)
        {
            // leaveOpen=true on both: the caller owns the server stream lifetime.
            StreamReader reader = new(server, Encoding.UTF8, false, 1024, true);
            StreamWriter writer = new(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };

            BrokerRequest request = ParseBrokerRequest(reader.ReadLine());
            if (!request.IsValid)
            {
                writer.Write("error\t");
                writer.WriteLine(request.ErrorMessage);
                return;
            }

            LaunchAttemptResult launchAttempt = LaunchVisualStudio(request.DevenvPath, request.SolutionPath);
            if (!launchAttempt.IsSuccessful || !launchAttempt.ProcessId.HasValue)
            {
                writer.Write("error\t");
                writer.WriteLine((launchAttempt.Error ?? "Visual Studio launch failed.").Replace('\r', ' ').Replace('\n', ' '));
                TryTerminate(launchAttempt.LaunchedProcesses);
                return;
            }

            writer.Write("ok\t");
            writer.WriteLine(launchAttempt.ProcessId.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static BrokerRequest ParseBrokerRequest(string request)
        {
            if (string.IsNullOrWhiteSpace(request))
            {
                return BrokerRequest.Invalid("Empty launch request.");
            }

            string[] parts = request.Split('\t');
            if (parts.Length < 2 || !string.Equals(parts[0], "launch", StringComparison.OrdinalIgnoreCase))
            {
                return BrokerRequest.Invalid("Unsupported broker request.");
            }

            string devenvPath = DecodePipeValue(parts[1]);
            string solutionPath = parts.Length > 2 ? DecodePipeValue(parts[2]) : string.Empty;
            return BrokerRequest.Valid(devenvPath, solutionPath);
        }

        private static LaunchAttemptResult LaunchVisualStudio(string devenvPath, string solutionPath)
        {
            if (!File.Exists(devenvPath))
            {
                return LaunchAttemptResult.Failed($"devenv.exe not found: {devenvPath}", []);
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = devenvPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(devenvPath)
            };

            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                startInfo.Arguments = QuoteArgument(solutionPath);
            }

            HashSet<int> existingProcessIds = CaptureExistingDevenvProcessIds();

            using Process process = Process.Start(startInfo);
            if (process == null)
            {
                return LaunchAttemptResult.Failed("Visual Studio launch failed: Process.Start returned null.", []);
            }

            LaunchAttemptResult launchAttempt = WaitForVisualStudioStartup(process, existingProcessIds);
            if (!launchAttempt.IsSuccessful)
            {
                TryTerminate(process);
            }

            return launchAttempt;
        }

        private static string DecodePipeValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            byte[] bytes = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(bytes);
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"Missing value for argument '{argument}'.");
                }

                options[argument] = args[++index];
            }

            return options;
        }

        private static LaunchAttemptResult WaitForVisualStudioStartup(Process process, HashSet<int> existingProcessIds)
        {
            List<Process> launchedProcesses = [];
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < StartupValidationTimeoutMilliseconds)
            {
                process.Refresh();
                int? primaryExitCode = process.HasExited ? process.ExitCode : (int?)null;
                LauncherProcessCatalog catalog = CaptureLaunchedDevenvProcesses(existingProcessIds, launchedProcesses);
                LauncherStartupEvaluation evaluation = LauncherProcessSelection.EvaluateStartupProgress(
                    process.Id,
                    process.HasExited,
                    primaryExitCode,
                    catalog.Snapshots,
                    existingProcessIds,
                    timedOut: false,
                    timeoutMilliseconds: StartupValidationTimeoutMilliseconds);

                if (evaluation.State == LauncherStartupState.Succeeded)
                {
                    return LaunchAttemptResult.Succeeded(evaluation.ActiveProcessId, launchedProcesses);
                }

                if (evaluation.State == LauncherStartupState.Failed)
                {
                    return LaunchAttemptResult.Failed(evaluation.Error, launchedProcesses);
                }

                if (catalog.ProcessesById.TryGetValue(evaluation.ActiveProcessId, out Process launchedProcess))
                {
                    process = launchedProcess;
                }

                Thread.Sleep(StartupPollMilliseconds);
            }

            process.Refresh();
            LauncherProcessCatalog timedOutCatalog = CaptureLaunchedDevenvProcesses(existingProcessIds, launchedProcesses);
            LauncherStartupEvaluation timedOutEvaluation = LauncherProcessSelection.EvaluateStartupProgress(
                process.Id,
                process.HasExited,
                process.HasExited ? process.ExitCode : (int?)null,
                timedOutCatalog.Snapshots,
                existingProcessIds,
                timedOut: true,
                timeoutMilliseconds: StartupValidationTimeoutMilliseconds);
            return LaunchAttemptResult.Failed(timedOutEvaluation.Error, launchedProcesses);
        }

        private static HashSet<int> CaptureExistingDevenvProcessIds()
        {
            HashSet<int> processIds = [];
            Process[] processes = Process.GetProcessesByName("devenv");
            for (int index = 0; index < processes.Length; index++)
            {
                using Process existingProcess = processes[index];
                processIds.Add(existingProcess.Id);
            }

            return processIds;
        }

        private static LauncherProcessCatalog CaptureLaunchedDevenvProcesses(HashSet<int> existingProcessIds, List<Process> launchedProcesses)
        {
            Process[] processes = Process.GetProcessesByName("devenv");
            Dictionary<int, Process> candidatesById = [];
            List<LauncherProcessSnapshot> snapshots = [];

            for (int index = 0; index < processes.Length; index++)
            {
                Process candidate = processes[index];
                try
                {
                    if (existingProcessIds.Contains(candidate.Id))
                    {
                        candidate.Dispose();
                        continue;
                    }

                    if (!TryTrackLaunchedProcess(candidate, launchedProcesses))
                    {
                        continue;
                    }

                    candidatesById[candidate.Id] = candidate;
                    snapshots.Add(new LauncherProcessSnapshot(
                        candidate.Id,
                        candidate.StartTime,
                        HasBridgeDiscoveryFile(candidate.Id),
                        HasMainWindow(candidate)));
                }
                catch (InvalidOperationException)
                {
                    candidate.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    candidate.Dispose();
                }
            }

            return new LauncherProcessCatalog(candidatesById, snapshots);
        }

        private static bool TryTrackLaunchedProcess(Process candidate, List<Process> launchedProcesses)
        {
            for (int index = 0; index < launchedProcesses.Count; index++)
            {
                if (launchedProcesses[index].Id == candidate.Id)
                {
                    candidate.Dispose();
                    return false;
                }
            }

            launchedProcesses.Add(candidate);
            return true;
        }

        private static bool HasBridgeDiscoveryFile(int processId)
        {
            foreach (string path in LauncherProcessSelection.BuildDiscoveryFileCandidates(GetTempRoots(), processId))
            {
                if (File.Exists(path))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> GetTempRoots()
        {
            string[] candidates =
            [
                Environment.GetEnvironmentVariable("TEMP"),
                Environment.GetEnvironmentVariable("TMP"),
                Path.GetTempPath(),
            ];

            IReadOnlyList<string> normalized = LauncherProcessSelection.NormalizeTempRoots(candidates);
            for (int index = 0; index < normalized.Count; index++)
            {
                yield return normalized[index];
            }
        }

        private static bool HasMainWindow(Process process)
        {
            return process.MainWindowHandle != IntPtr.Zero ||
                   !string.IsNullOrWhiteSpace(process.MainWindowTitle);
        }

        private static void TryTerminate(Process process)
        {
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(ProcessExitWaitMilliseconds);
                }
            }
            catch (InvalidOperationException ex)
            {
                Trace.WriteLine(ex.Message);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Trace.WriteLine(ex.Message);
            }
        }

        private static void TryTerminate(List<Process> processes)
        {
            for (int index = 0; index < processes.Count; index++)
            {
                Process process = processes[index];
                TryTerminate(process);
                process.Dispose();
            }
        }

        private sealed class LaunchAttemptResult
        {
            private LaunchAttemptResult(bool isSuccessful, int? processId, string error, List<Process> launchedProcesses)
            {
                IsSuccessful = isSuccessful;
                ProcessId = processId;
                Error = error;
                LaunchedProcesses = launchedProcesses;
            }

            public bool IsSuccessful { get; }

            public int? ProcessId { get; }

            public string Error { get; }

            public List<Process> LaunchedProcesses { get; }

            public static LaunchAttemptResult Succeeded(int processId, List<Process> launchedProcesses)
            {
                return new LaunchAttemptResult(true, processId, null, launchedProcesses);
            }

            public static LaunchAttemptResult Failed(string error, List<Process> launchedProcesses)
            {
                return new LaunchAttemptResult(false, null, error, launchedProcesses);
            }
        }

        private sealed class LauncherProcessCatalog(Dictionary<int, Process> processesById, List<LauncherProcessSnapshot> snapshots)
        {
            public Dictionary<int, Process> ProcessesById { get; } = processesById;

            public List<LauncherProcessSnapshot> Snapshots { get; } = snapshots;
        }

        private sealed class BrokerRequest
        {
            private BrokerRequest(bool isValid, string devenvPath, string solutionPath, string errorMessage)
            {
                IsValid = isValid;
                DevenvPath = devenvPath;
                SolutionPath = solutionPath;
                ErrorMessage = errorMessage;
            }

            public bool IsValid { get; }

            public string DevenvPath { get; }

            public string SolutionPath { get; }

            public string ErrorMessage { get; }

            public static BrokerRequest Valid(string devenvPath, string solutionPath)
            {
                return new BrokerRequest(true, devenvPath, solutionPath, string.Empty);
            }

            public static BrokerRequest Invalid(string errorMessage)
            {
                return new BrokerRequest(false, string.Empty, string.Empty, errorMessage);
            }
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(QuoteArgumentSpecialCharacters) < 0)
            {
                return value;
            }

            return string.Concat("\"", value.Replace("\\", "\\\\").Replace("\"", "\\\""), "\"");
        }

    }

    internal static class LauncherResultWriter
    {
        public static int Fail(string resultFile, string error)
        {
            string detailedError = AppendLaunchEnvironmentDetails(error);
            WriteResult(resultFile, false, null, detailedError);
            Console.Error.WriteLine(detailedError);
            return 1;
        }

        public static void WriteResult(string resultFile, bool success, int? pid, string error)
        {
            if (string.IsNullOrWhiteSpace(resultFile))
            {
                return;
            }

            string directory = Path.GetDirectoryName(resultFile);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            StringBuilder payload = new();
            payload.Append('{');
            payload.Append("\"success\":");
            payload.Append(success ? "true" : "false");
            payload.Append(',');
            payload.Append("\"pid\":");
            payload.Append(pid.HasValue ? pid.Value.ToString(CultureInfo.InvariantCulture) : "null");
            payload.Append(',');
            payload.Append("\"error\":");
            payload.Append(error == null ? "null" : QuoteJson(error));
            payload.Append('}');

            File.WriteAllText(resultFile, payload.ToString());
        }

        private static string AppendLaunchEnvironmentDetails(string error)
        {
            StringBuilder builder = new();
            builder.Append(error);
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Launcher environment:");
            AppendEnvironmentValue(builder, "CurrentDirectory", Environment.CurrentDirectory);
            AppendEnvironmentValue(builder, "SystemRoot", Environment.GetEnvironmentVariable("SystemRoot"));
            AppendEnvironmentValue(builder, "windir", Environment.GetEnvironmentVariable("windir"));
            AppendEnvironmentValue(builder, "USERPROFILE", Environment.GetEnvironmentVariable("USERPROFILE"));
            AppendEnvironmentValue(builder, "APPDATA", Environment.GetEnvironmentVariable("APPDATA"));
            AppendEnvironmentValue(builder, "LOCALAPPDATA", Environment.GetEnvironmentVariable("LOCALAPPDATA"));
            AppendEnvironmentValue(builder, "TEMP", Environment.GetEnvironmentVariable("TEMP"));
            AppendEnvironmentValue(builder, "TMP", Environment.GetEnvironmentVariable("TMP"));
            return builder.ToString();
        }

        private static void AppendEnvironmentValue(StringBuilder builder, string name, string value)
        {
            builder.Append("  ");
            builder.Append(name);
            builder.Append(" = ");
            builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "<null>" : value);
        }

        private static string QuoteJson(string value)
        {
            StringBuilder builder = new();
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(character);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }

}
