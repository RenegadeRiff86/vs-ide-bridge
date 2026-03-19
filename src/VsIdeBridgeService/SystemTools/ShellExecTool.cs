using System;
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
        int tailLines = args?["tail_lines"]?.GetValue<int?>() ?? 0;

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

        JsonObject payload = new()
        {
            ["success"] = process.ExitCode == 0,
            ["exitCode"] = process.ExitCode,
            ["command"] = executable,
            ["workingDirectory"] = workingDirectory,
            ["args"] = arguments,
            ["stdout"] = Tail(stdout, tailLines),
            ["stderr"] = Tail(stderr, tailLines),
        };

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(),
                },
            },
            ["isError"] = process.ExitCode != 0,
            ["structuredContent"] = payload,
        };
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

    private static string Tail(string text, int tailLines)
    {
        if (tailLines <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        string[] normalizedLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int startIndex = Math.Max(0, normalizedLines.Length - tailLines);
        string[] selectedLines = normalizedLines[startIndex..];
        return string.Join(Environment.NewLine, selectedLines);
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
        catch
        {
        }
    }
}
