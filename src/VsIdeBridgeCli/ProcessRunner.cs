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
        var result = await RunAsync(startInfo, timeoutMs).ConfigureAwait(false);
        return new JsonObject
        {
            ["success"] = result.Success,
            ["exitCode"] = result.ExitCode,
            ["command"] = result.Command,
            ["workingDirectory"] = result.WorkingDirectory,
            ["args"] = result.Arguments,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
        };
    }

    internal static async Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = DefaultTimeoutMilliseconds, int pollIntervalMs = DefaultPollIntervalMilliseconds)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        if (process.StartInfo.RedirectStandardInput)
        {
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var effectiveTimeout = timeoutMs <= 0 ? DefaultTimeoutMilliseconds : timeoutMs;
        var effectivePollInterval = pollIntervalMs <= 0 ? DefaultPollIntervalMilliseconds : pollIntervalMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(effectiveTimeout);

        while (!process.HasExited)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var killError = TryKillProcess(process);
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

        process.WaitForExit();
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessRunResult(process.ExitCode == 0, process.ExitCode, startInfo.FileName, startInfo.WorkingDirectory, startInfo.Arguments, stdout, stderr);
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
