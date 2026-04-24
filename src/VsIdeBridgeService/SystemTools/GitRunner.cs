using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService.SystemTools;

/// <summary>
/// Runs git commands as a subprocess and returns structured MCP results.
/// All git tools share this runner for consistent timeout, env, and response shaping.
/// </summary>
internal static class GitRunner
{
    private const int DefaultTimeoutMs = 30_000;
    private const int NetworkTimeoutMs = 120_000;

    /// <summary>
    /// Execute a git command in the repo root and return an MCP-formatted result.
    /// </summary>
    public static async Task<JsonNode> RunAsync(
        JsonNode? id,
        string repoDirectory,
        string gitArgs,
        int timeoutMs = DefaultTimeoutMs)
    {
        string gitExe = ResolveGitExecutable();

        // Suppress prompts and pager output that would hang a headless process.
        string fullArgs = $"--no-pager -c safe.directory=\"{repoDirectory}\" {gitArgs}";

        ProcessStartInfo psi = new()
        {
            FileName = gitExe,
            Arguments = fullArgs,
            WorkingDirectory = repoDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Prevent git from opening a credential dialog or SSH prompt.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_NO_PAGER"] = "1";
        psi.Environment["GIT_ASKPASS"] = "echo";

        using Process process = Process.Start(psi)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to start git process at '{gitExe}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();

        if (!ReferenceEquals(
                await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false),
                waitTask))
        {
            TryKill(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"git {gitArgs} timed out after {timeoutMs} ms.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        bool success = process.ExitCode == 0;

        JsonObject payload = new()
        {
            ["success"] = success,
            ["exitCode"] = process.ExitCode,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
        };

        string successText = $"Git command completed with exit code {process.ExitCode}.";
        return ToolResultFormatter.StructuredToolResult(payload, isError: !success, successText: successText);
    }

    /// <summary>Network operations (push/pull/fetch/merge) get a longer timeout.</summary>
    public static Task<JsonNode> RunNetworkAsync(
        JsonNode? id,
        string repoDirectory,
        string gitArgs)
        => RunAsync(id, repoDirectory, gitArgs, NetworkTimeoutMs);

    // ── Git executable resolution ─────────────────────────────────────────────

    private static volatile string? _resolvedGitExe;

    public static string ResolveGitExecutable()
    {
        if (_resolvedGitExe is not null)
            return _resolvedGitExe;

        // 1. Prefer git on PATH.
        if (TryFindOnPath("git.exe", out string? fromPath))
            return _resolvedGitExe = fromPath!;
        if (TryFindOnPath("git", out string? fromPathUnix))
            return _resolvedGitExe = fromPathUnix!;

        // 2. Common Windows install locations.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] candidates =
        [
            Path.Combine(programFiles,   "Git", "cmd", "git.exe"),
            Path.Combine(programFilesX86,"Git", "cmd", "git.exe"),
            Path.Combine(programFiles,   "Git", "bin", "git.exe"),
            Path.Combine(programFilesX86,"Git", "bin", "git.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return _resolvedGitExe = candidate;
        }

        throw new InvalidOperationException(
            "git executable not found. Install Git for Windows or ensure git.exe is on PATH.");
    }

    private static bool TryFindOnPath(string name, out string? fullPath)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            fullPath = null;
            return false;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }

        fullPath = null;
        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            McpServerLog.WriteException("failed to terminate git child process", ex);
        }
        catch (Win32Exception ex)
        {
            McpServerLog.WriteException("failed to terminate git child process", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException("failed to terminate git child process", ex);
        }
    }
}
