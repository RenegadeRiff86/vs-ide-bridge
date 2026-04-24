using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService.SystemTools;

internal static class ShellExecTool
{
    private const int DefaultTimeoutMs = 60_000;

    public static async Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string executable = args?["exe"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument 'exe'.");
        }

        string arguments = args?["args"]?.GetValue<string>() ?? string.Empty;
        string workingDirectory = ResolveWorkingDirectory(args, bridge);
        int timeoutMs = args?["timeout_ms"]?.GetValue<int?>() ?? DefaultTimeoutMs;
        int tailLines  = args?["tail_lines"]?.GetValue<int?>() ?? 0;
        int headLines  = args?["head_lines"]?.GetValue<int?>() ?? 0;
        int maxLines   = args?["max_lines"]?.GetValue<int?>() ?? 200;
        bool useMaxCap = headLines <= 0 && tailLines <= 0;
        int effectiveHead = useMaxCap ? maxLines : headLines;

        ProcessStartInfo processStartInfo = new()
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(processStartInfo)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError, $"Failed to start process '{executable}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();
        Task completedTask = await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false);

        if (!ReferenceEquals(completedTask, waitTask))
        {
            TryKill(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"Process '{executable}' timed out after {timeoutMs} ms.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        bool outputTruncated = useMaxCap && (
            stdout.Split('\n').Length > maxLines || stderr.Split('\n').Length > maxLines);

        JsonObject payload = new()
        {
            ["success"] = process.ExitCode == 0,
            ["exitCode"] = process.ExitCode,
            ["command"] = executable,
            ["workingDirectory"] = workingDirectory,
            ["args"] = arguments,
            ["stdout"] = Truncate(stdout, effectiveHead, tailLines),
            ["stderr"] = Truncate(stderr, effectiveHead, tailLines),
            ["outputTruncated"] = outputTruncated,
        };

        bool success = process.ExitCode == 0;
        string successText = outputTruncated
            ? $"Process '{executable}' exited with code {process.ExitCode}. Output was truncated."
            : $"Process '{executable}' exited with code {process.ExitCode}.";
        return ToolResultFormatter.StructuredToolResult(payload, args, isError: !success, successText: successText);
    }

    private static string ResolveWorkingDirectory(JsonObject? args, BridgeConnection bridge)
    {
        string? explicitWorkingDirectory = args?["cwd"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(explicitWorkingDirectory))
        {
            return explicitWorkingDirectory;
        }

        return ServiceToolPaths.ResolveSolutionDirectory(bridge);
    }

    private static string Truncate(string text, int headLines, int tailLines)
    {
        if (string.IsNullOrEmpty(text) || (headLines <= 0 && tailLines <= 0))
            return text;

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (headLines > 0 && tailLines > 0)
        {
            int headEnd = Math.Min(headLines, lines.Length);
            int tailStart = Math.Max(headEnd, lines.Length - tailLines);
            if (tailStart <= headEnd)
                return string.Join(Environment.NewLine, lines);
            int omitted = tailStart - headEnd;
            return string.Join(Environment.NewLine, lines[..headEnd])
                + $"{Environment.NewLine}... ({omitted} lines omitted) ...{Environment.NewLine}"
                + string.Join(Environment.NewLine, lines[tailStart..]);
        }

        if (headLines > 0)
            return string.Join(Environment.NewLine, lines[..Math.Min(headLines, lines.Length)]);

        int startIndex = Math.Max(0, lines.Length - tailLines);
        return string.Join(Environment.NewLine, lines[startIndex..]);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            McpServerLog.WriteException("failed to terminate shell-exec child process", ex);
        }
        catch (Win32Exception ex)
        {
            McpServerLog.WriteException("failed to terminate shell-exec child process", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException("failed to terminate shell-exec child process", ex);
        }
    }
}
