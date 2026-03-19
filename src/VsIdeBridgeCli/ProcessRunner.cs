using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VsIdeBridgeCli;

internal static class ProcessRunner
{
    internal const int DefaultTimeoutMilliseconds = 60_000;
    internal const int DefaultPollIntervalMilliseconds = 50;

    internal static Task<JsonObject> RunJsonAsync(string command, string arguments, string workingDirectory, int timeoutMs = DefaultTimeoutMilliseconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return RunJsonAsync(startInfo, timeoutMs);
    }

    internal static async Task<JsonObject> RunJsonAsync(ProcessStartInfo startInfo, int timeoutMs = DefaultTimeoutMilliseconds)
    {
        var runResult = await RunAsync(startInfo, timeoutMs).ConfigureAwait(false);
        return new JsonObject
        {
            ["success"] = runResult.Success,
            ["exitCode"] = runResult.ExitCode,
            ["command"] = runResult.Command,
            ["workingDirectory"] = runResult.WorkingDirectory,
            ["args"] = runResult.Arguments,
            ["stdout"] = runResult.Stdout,
            ["stderr"] = runResult.Stderr,
        };
    }

    internal static async Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = DefaultTimeoutMilliseconds, int pollIntervalMs = DefaultPollIntervalMilliseconds)
    {
        using var childProcess = new Process { StartInfo = startInfo };
        childProcess.Start();

        if (childProcess.StartInfo.RedirectStandardInput)
        {
            childProcess.StandardInput.Close();
        }

        var stdoutTask = childProcess.StandardOutput.ReadToEndAsync();
        var stderrTask = childProcess.StandardError.ReadToEndAsync();

        var effectiveTimeout = timeoutMs <= 0 ? DefaultTimeoutMilliseconds : timeoutMs;
        var effectivePollInterval = pollIntervalMs <= 0 ? DefaultPollIntervalMilliseconds : pollIntervalMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(effectiveTimeout);

        while (!childProcess.HasExited)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var killError = TryKillProcess(childProcess);
                return new ProcessRunResult(
                    false,
                    -1,
                    startInfo.FileName,
                    startInfo.WorkingDirectory,
                    startInfo.Arguments,
                    string.Empty,
                    CreateTimeoutMessage(effectiveTimeout, killError));
            }

            await Task.Delay(effectivePollInterval).ConfigureAwait(false);
        }

        childProcess.WaitForExit();
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessRunResult(childProcess.ExitCode == 0, childProcess.ExitCode, startInfo.FileName, startInfo.WorkingDirectory, startInfo.Arguments, stdout, stderr);
    }

    internal sealed record ProcessRunResult(
        bool Success,
        int ExitCode,
        string? Command,
        string? WorkingDirectory,
        string? Arguments,
        string Stdout,
        string Stderr);

    private static string CreateTimeoutMessage(int effectiveTimeout, string? killError)
    {
        var timeoutMessage = $"Process timed out after {effectiveTimeout}ms and was killed.";
        return string.IsNullOrWhiteSpace(killError)
            ? timeoutMessage
            : $"{timeoutMessage} Kill error: {killError}";
    }

    private static string? TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
