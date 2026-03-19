using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VsIdeBridgeCli;

internal static class VisualStudioLauncher
{
    private const int LaunchTimeoutMilliseconds = 10_000;
    private const int LaunchPollIntervalMilliseconds = 25;
    private static readonly char[] LineSeparators = ['\r', '\n'];

    internal delegate Task<ProcessRunner.ProcessRunResult> ProcessRunnerDelegate(ProcessStartInfo startInfo, int timeoutMs, int pollIntervalMs);

    internal sealed record LaunchResult(bool Success, int ProcessId, string Launcher, string? Stderr);

    internal static Task<LaunchResult> LaunchAsync(
        string devenvPath,
        string? solutionPath,
        ProcessRunnerDelegate? processRunner = null)
    {
        processRunner ??= ProcessRunner.RunAsync;
        return LaunchCoreAsync(devenvPath, solutionPath, processRunner);
    }

    internal static ProcessStartInfo CreateStartInfo(string devenvPath, string? solutionPath)
    {
        var solutionDirectory = string.IsNullOrWhiteSpace(solutionPath)
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory;

        return new ProcessStartInfo
        {
            FileName = GetPowerShellPath(),
            Arguments = BuildPowerShellArguments(devenvPath, solutionPath),
            WorkingDirectory = solutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    internal static string BuildPowerShellArguments(string devenvPath, string? solutionPath)
    {
        var escapedDevenvPath = QuotePowerShellLiteral(devenvPath);
        var escapedSolutionPath = string.IsNullOrWhiteSpace(solutionPath)
            ? null
            : QuotePowerShellLiteral(solutionPath);
        var command = escapedSolutionPath is null
            ? "$ErrorActionPreference='Stop'; $process = Start-Process -FilePath '" + escapedDevenvPath + "' -PassThru; Write-Output $process.Id"
            : "$ErrorActionPreference='Stop'; $process = Start-Process -FilePath '" + escapedDevenvPath + "' -ArgumentList @('" + escapedSolutionPath + "') -WorkingDirectory '" + QuotePowerShellLiteral(Path.GetDirectoryName(solutionPath!) ?? Environment.CurrentDirectory) + "' -PassThru; Write-Output $process.Id";

        return $"-NoProfile -NonInteractive -Command \"{command}\"";
    }

    internal static bool TryParseProcessId(string? stdout, out int processId)
    {
        processId = 0;
        var candidate = stdout?
            .Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .LastOrDefault(static line => line.Length > 0);

        return int.TryParse(candidate, out processId) && processId > 0;
    }

    private static async Task<LaunchResult> LaunchCoreAsync(
        string devenvPath,
        string? solutionPath,
        ProcessRunnerDelegate processRunner)
    {
        var startInfo = CreateStartInfo(devenvPath, solutionPath);
        var processRunnerResult = await processRunner(startInfo, LaunchTimeoutMilliseconds, LaunchPollIntervalMilliseconds).ConfigureAwait(false);
        var processId = 0;
        var success = processRunnerResult.Success && TryParseProcessId(processRunnerResult.Stdout, out processId);
        return new LaunchResult(success, processId, "powershell-start-process", processRunnerResult.Stderr);
    }

    private static string QuotePowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string GetPowerShellPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }
}
