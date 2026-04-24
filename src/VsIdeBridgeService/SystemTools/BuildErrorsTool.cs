using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService.SystemTools;

internal static partial class BuildErrorsTool
{
    private const int DefaultMax = 20;
    private const int BuildTimeoutMs = 120_000;
    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+)(?:,(?<column>\d+))?\):\s(?<severity>error|warning)\s(?<code>[^:]+):\s(?<message>.*?)(?:\s\[(?<project>.+)\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildDiagnosticPattern();

    [GeneratedRegex(@"^(?<project>.+?)\s*:\s*(?<severity>error|warning)\s(?<code>[^:]+):\s(?<message>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StructuredDiagnosticPattern();

    public static async Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string configuration = args?["configuration"]?.GetValue<string>() ?? "Release";
        string? projectArg = args?["project"]?.GetValue<string>();
        int max = args?["max"]?.GetValue<int?>() ?? DefaultMax;

        string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
        string target = string.IsNullOrWhiteSpace(projectArg)
            ? FindSolutionFile(solutionDir)
            : ResolveProjectPath(solutionDir, projectArg);

        if (!File.Exists(target))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Build target '{target}' was not found.");
        }

        Stopwatch sw = Stopwatch.StartNew();
        BuildRunResult buildRun = await RunCliBuildAsync(id, target, configuration)
            .ConfigureAwait(false);
        sw.Stop();

        List<JsonObject> allErrors = buildRun.Errors;
        int totalErrors = allErrors.Count;
        bool truncated = totalErrors > max;

        JsonArray errorArray = [];
        foreach (JsonObject error in truncated ? allErrors.GetRange(0, max) : allErrors)
            errorArray.Add(error);

        JsonObject payload = new()
        {
            ["success"] = totalErrors == 0 && buildRun.ResultCode == BuildRunCode.Success,
            ["errorCount"] = totalErrors,
            ["truncated"] = truncated,
            ["errors"] = errorArray,
            ["buildDuration"] = $"{sw.Elapsed.TotalSeconds:F1}s",
            ["configuration"] = configuration,
            ["target"] = Path.GetFileName(target),
            ["resultCode"] = buildRun.ResultCode.ToString(),
        };

        string successText = totalErrors == 0
            ? $"Build succeeded with 0 errors in {sw.Elapsed.TotalSeconds:F1}s."
            : null!;

        return ToolResultFormatter.StructuredToolResult(
            payload,
            args,
            isError: totalErrors > 0,
            successText: totalErrors == 0 ? successText : null);
    }

    private static async Task<BuildRunResult> RunCliBuildAsync(JsonNode? id, string target, string configuration)
    {
        string dotnet = ResolveDotnetExecutable(id);
        string arguments = $"build {QuoteArg(target)} -c {QuoteArg(configuration)} -nologo -v:minimal -p:GenerateFullPaths=true";

        ProcessStartInfo startInfo = new()
        {
            FileName = dotnet,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to start process '{dotnet}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();

        Task finished = await Task.WhenAny(waitTask, Task.Delay(BuildTimeoutMs)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, waitTask))
        {
            TryKillProcess(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"'{dotnet} {arguments}' timed out after {BuildTimeoutMs / 1000}s.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        string combinedOutput = string.Join(Environment.NewLine, new[] { stdout, stderr }
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        List<JsonObject> errors = ParseCliBuildErrors(combinedOutput);

        BuildRunCode resultCode = process.ExitCode == 0 ? BuildRunCode.Success : BuildRunCode.Failure;
        return new BuildRunResult(resultCode, errors);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            McpServerLog.WriteException("failed to terminate timed-out build-errors process", ex);
        }
        catch (Win32Exception ex)
        {
            McpServerLog.WriteException("failed to terminate timed-out build-errors process", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException("failed to terminate timed-out build-errors process", ex);
        }
    }

    private static List<JsonObject> ParseCliBuildErrors(string buildOutputText)
    {
        List<JsonObject> diagnostics = [];
        string[] lines = buildOutputText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            JsonObject? diagnostic = TryParseMsBuildDiagnostic(line) ?? TryParseStructuredDiagnostic(line);
            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
        }

        return diagnostics;
    }

    private static JsonObject? TryParseMsBuildDiagnostic(string line)
    {
        Match match = MsBuildDiagnosticPattern().Match(line);
        if (!match.Success || !string.Equals(match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase))
            return null;

        string filePath = match.Groups["file"].Value;
        string project = match.Groups["project"].Value;
        return CreateCliDiagnostic(
            filePath,
            int.TryParse(match.Groups["line"].Value, out int lineNumber) ? lineNumber : 1,
            int.TryParse(match.Groups["column"].Value, out int columnNumber) ? columnNumber : 1,
            match.Groups["code"].Value,
            match.Groups["message"].Value,
            project,
            project);
    }

    private static JsonObject? TryParseStructuredDiagnostic(string line)
    {
        Match match = StructuredDiagnosticPattern().Match(line);
        if (!match.Success || !string.Equals(match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase))
            return null;

        string project = match.Groups["project"].Value;
        return CreateCliDiagnostic(
            string.Empty,
            1,
            1,
            match.Groups["code"].Value,
            match.Groups["message"].Value,
            project,
            project);
    }

    private static JsonObject CreateCliDiagnostic(
        string filePath,
        int lineNumber,
        int columnNumber,
        string code,
        string message,
        string project,
        string projectPath)
        => new()
        {
            ["file"] = string.IsNullOrWhiteSpace(filePath) ? string.Empty : Path.GetFileName(filePath),
            ["path"] = filePath,
            ["line"] = lineNumber,
            ["column"] = columnNumber,
            ["code"] = code,
            ["message"] = message,
            ["project"] = project,
            ["projectPath"] = projectPath,
        };

    private static string ResolveDotnetExecutable(JsonNode? id)
    {
        string? explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitHost) && File.Exists(explicitHost))
            return explicitHost;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate = Path.Combine(programFiles, "dotnet", "dotnet.exe");
        if (File.Exists(candidate))
            return candidate;

        candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe");
        if (File.Exists(candidate))
            return candidate;

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "Could not locate dotnet.exe for build_errors.");
    }

    private static string QuoteArg(string value)
        => $"\"{value.Replace("\"", "\\\"")}\"";

    private enum BuildRunCode
    {
        Success,
        Failure,
    }

    private sealed record BuildRunResult(BuildRunCode ResultCode, List<JsonObject> Errors);

    private static string FindSolutionFile(string directory)
    {
        string[] slnFiles = Directory.GetFiles(directory, "*.sln");
        if (slnFiles.Length > 0)
            return slnFiles[0];

        string[] slnxFiles = Directory.GetFiles(directory, "*.slnx");
        if (slnxFiles.Length > 0)
            return slnxFiles[0];

        return directory;
    }

    private static string ResolveProjectPath(string solutionDir, string projectArg)
    {
        if (Path.IsPathRooted(projectArg))
            return projectArg;

        string relative = Path.Combine(solutionDir, projectArg);
        if (File.Exists(relative))
            return relative;

        string[] projectPatterns = ["*.csproj", "*.vbproj", "*.fsproj", "*.vcxproj", "*.pyproj", "*.sqlproj", "*.wapproj"];
        foreach (string pattern in projectPatterns)
        {
            foreach (string proj in Directory.EnumerateFiles(solutionDir, pattern, SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(proj).Equals(projectArg, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(proj).Equals(projectArg, StringComparison.OrdinalIgnoreCase))
                    return proj;
            }
        }

        return relative;
    }
}
