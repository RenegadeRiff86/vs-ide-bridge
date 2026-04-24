using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> NugetTools()
    {
        yield return new("scan_project_dependencies",
            "Inspect one project's declared dependencies and current NuGet warning rows.",
            ObjectSchema(
                Req("project", "Project name or relative path."),
                OptBool("include_framework", "Include framework references (default true)."),
                OptInt("max_warnings", "Maximum NuGet warning rows to include (default 20).")),
            "project",
            async (id, args, bridge) =>
            {
                string project = RequireNugetArg(id, args, "project");
                bool includeFramework = args?["include_framework"]?.GetValue<bool?>() ?? true;
                int maxWarnings = args?["max_warnings"]?.GetValue<int?>() ?? 20;

                JsonObject referencesResponse = await bridge.SendAsync(
                    id,
                    "query-project-references",
                    Build(
                        (Project, project),
                        ("declared-only", "true"),
                        ("include-framework", includeFramework ? "true" : "false")))
                    .ConfigureAwait(false);

                JsonObject warningsResponse = await bridge.SendAsync(
                    id,
                    "warnings",
                    Build(
                        (Project, project),
                        ("code", "NU"),
                        ("quick", "true"),
                        (Max, maxWarnings.ToString())))
                    .ConfigureAwait(false);

                JsonObject payload = BuildDependencyScanPayload(project, includeFramework, referencesResponse, warningsResponse);
                return ToolResultFormatter.StructuredToolResult(payload, args, successText: CreateDependencyScanSummary(payload));
            },
            summary: "Inspect one project's dependencies and highlight likely package issues.",
            readOnly: true,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [("nuget_add_package", "Add a missing or outdated package"), ("nuget_remove_package", "Remove an unused package")],
                related: [("query_project_references", "Raw reference list without health analysis"), ("errors", "Check NuGet restore errors")]));

        yield return new("nuget_add_package",
            "Add a NuGet package to a project using 'dotnet add package'. " +
            "The project must be in the open solution.",
            ObjectSchema(
                Req("project", "Project name or relative path (e.g. \"MyLib\" or \"src/MyLib/MyLib.csproj\")."),
                Req("package", "NuGet package id, e.g. \"Newtonsoft.Json\"."),
                Opt("version", "Optional version constraint, e.g. \"13.0.3\".")),
            "project",
            async (id, args, bridge) =>
            {
                string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                string project = RequireNugetArg(id, args, "project");
                string package = RequireNugetArg(id, args, "package");
                string? version = args?["version"]?.GetValue<string>();
                string versionArg = string.IsNullOrWhiteSpace(version) ? string.Empty
                    : $" --version {version}";
                string dotnetArgs = $"add \"{project}\" package {package}{versionArg}";
                return await RunDotnetAsync(id, dotnetArgs, solutionDir, timeoutMs: 120_000)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("scan_project_dependencies", "Verify the package was added"), ("build", "Build after adding the package")],
                related: [("nuget_remove_package", "Remove a package"), ("query_project_references", "List current references")]));

        yield return new("nuget_remove_package",
            "Remove a NuGet package from a project using 'dotnet remove package'.",
            ObjectSchema(
                Req("project", "Project name or relative path."),
                Req("package", "NuGet package id to remove.")),
            "project",
            async (id, args, bridge) =>
            {
                string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                string project = RequireNugetArg(id, args, "project");
                string package = RequireNugetArg(id, args, "package");
                return await RunDotnetAsync(id, $"remove \"{project}\" package {package}",
                    solutionDir, timeoutMs: 30_000)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow: [("scan_project_dependencies", "Verify the package was removed"), ("build", "Build after removing the package")],
                related: [("nuget_add_package", "Add a package"), ("query_project_references", "List current references")]));
    }

    // ── NuGet helpers ─────────────────────────────────────────────────────────

    private static async Task<JsonNode> RunDotnetAsync(
        JsonNode? id, string dotnetArgs, string workingDirectory, int timeoutMs)
    {
        string dotnet = ResolveDotnet();

        ProcessStartInfo psi = new()
        {
            FileName = dotnet,
            Arguments = dotnetArgs,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to start dotnet process at '{dotnet}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();

        if (!ReferenceEquals(
                await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false),
                waitTask))
        {
            TryKillProcess(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"'dotnet {dotnetArgs}' timed out after {timeoutMs} ms.");
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

        string exitText = $"dotnet command completed with exit code {process.ExitCode}.";
        string outputText = !string.IsNullOrWhiteSpace(stdout) ? stdout.TrimEnd()
            : !string.IsNullOrWhiteSpace(stderr) ? $"stderr: {stderr.TrimEnd()}"
            : string.Empty;
        string successText = string.IsNullOrWhiteSpace(outputText)
            ? exitText
            : exitText + "\n" + outputText;
        return ToolResultFormatter.StructuredToolResult(payload, isError: !success, successText: successText);
    }

    private static volatile string? _resolvedDotnet;

    private static string ResolveDotnet()
    {
        if (_resolvedDotnet is not null) return _resolvedDotnet;

        // Prefer dotnet on PATH (the normal case).
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(System.IO.Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (string name in new[] { "dotnet.exe", "dotnet" })
                {
                    string candidate = System.IO.Path.Combine(dir, name);
                    if (File.Exists(candidate)) return _resolvedDotnet = candidate;
                }
            }
        }

        // Fallback: well-known install path on Windows.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string wellKnown = System.IO.Path.Combine(programFiles, "dotnet", "dotnet.exe");
        if (File.Exists(wellKnown)) return _resolvedDotnet = wellKnown;

        // Last resort: rely on PATH lookup by name only.
        return _resolvedDotnet = "dotnet";
    }

    private static string RequireNugetArg(JsonNode? id, JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Missing required argument '{name}'.");
        return value;
    }

    private static void TryKillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException ex) { McpServerLog.WriteException("failed to terminate NuGet child process", ex); }
        catch (Win32Exception ex) { McpServerLog.WriteException("failed to terminate NuGet child process", ex); }
        catch (NotSupportedException ex) { McpServerLog.WriteException("failed to terminate NuGet child process", ex); }
    }

    private static JsonObject BuildDependencyScanPayload(
        string requestedProject,
        bool includeFramework,
        JsonObject referencesResponse,
        JsonObject warningsResponse)
    {
        JsonObject referencesData = referencesResponse["Data"] as JsonObject ?? [];
        JsonArray references = referencesData["references"] as JsonArray ?? [];
        JsonObject warningsData = warningsResponse["Data"] as JsonObject ?? [];
        JsonArray warningRows = warningsData["rows"] as JsonArray ?? [];

        JsonArray dependencies = [];
        JsonArray issues = [];
        int packageCount = 0;
        int projectCount = 0;
        int frameworkCount = 0;
        int assemblyCount = 0;

        foreach (JsonNode? node in references)
        {
            if (node is not JsonObject reference)
            {
                continue;
            }

            JsonObject normalized = NormalizeDependency(reference);
            dependencies.Add(normalized);

            switch (normalized["kind"]?.GetValue<string>())
            {
                case "package": packageCount++; break;
                case "project": projectCount++; break;
                case "framework": frameworkCount++; break;
                case "assembly": assemblyCount++; break;
            }

            AddDependencyIssues(normalized, issues);
        }

        JsonArray nugetWarnings = [];
        foreach (JsonNode? node in warningRows)
        {
            if (node is not JsonObject warning)
            {
                continue;
            }

            nugetWarnings.Add(new JsonObject
            {
                ["code"] = warning["code"]?.DeepClone(),
                ["message"] = warning["message"]?.DeepClone(),
                ["file"] = warning["file"]?.DeepClone(),
                ["line"] = warning["line"]?.DeepClone(),
                ["project"] = warning["project"]?.DeepClone(),
            });
        }

        return new JsonObject
        {
            ["success"] = true,
            ["project"] = referencesData["project"]?.DeepClone() ?? requestedProject,
            ["uniqueName"] = referencesData["uniqueName"]?.DeepClone(),
            ["declaredOnly"] = referencesData["declaredOnly"]?.DeepClone() ?? true,
            ["includeFramework"] = includeFramework,
            ["counts"] = new JsonObject
            {
                ["total"] = dependencies.Count,
                ["packages"] = packageCount,
                ["projects"] = projectCount,
                ["frameworks"] = frameworkCount,
                ["assemblies"] = assemblyCount,
                ["issues"] = issues.Count,
                ["nugetWarnings"] = nugetWarnings.Count,
            },
            ["dependencies"] = dependencies,
            ["issues"] = issues,
            ["nugetWarnings"] = nugetWarnings,
        };
    }

    private static JsonObject NormalizeDependency(JsonObject reference)
    {
        JsonObject metadata = reference["metadata"] as JsonObject ?? [];
        string? name = FirstNonEmpty(
            reference["name"]?.GetValue<string>(),
            reference["identity"]?.GetValue<string>(),
            metadata["Include"]?.GetValue<string>(),
            metadata["Update"]?.GetValue<string>());

        string? version = FirstNonEmpty(
            reference["version"]?.GetValue<string>(),
            metadata["Version"]?.GetValue<string>());

        return new JsonObject
        {
            ["name"] = name ?? string.Empty,
            ["identity"] = reference["identity"]?.DeepClone(),
            ["kind"] = reference["kind"]?.DeepClone(),
            ["isOverride"] = metadata["Update"] is not null,
            ["origin"] = reference["origin"]?.DeepClone(),
            ["declaredItemType"] = reference["declaredItemType"]?.DeepClone(),
            ["version"] = version,
            ["path"] = reference["path"]?.DeepClone(),
            ["declared"] = reference["declared"]?.DeepClone(),
            ["metadata"] = metadata.DeepClone(),
        };
    }

    private static void AddDependencyIssues(JsonObject dependency, JsonArray issues)
    {
        string kind = dependency["kind"]?.GetValue<string>() ?? string.Empty;
        string name = dependency["name"]?.GetValue<string>() ?? string.Empty;
        string? path = dependency["path"]?.GetValue<string>();
        string? version = dependency["version"]?.GetValue<string>();
        bool isOverride = dependency["isOverride"]?.GetValue<bool?>() ?? false;

        if (kind == "package" && !isOverride && string.IsNullOrWhiteSpace(version))
        {
            issues.Add(CreateDependencyIssue(name, kind, "missing_version", "Package reference does not expose a version in project metadata."));
        }

        if ((kind == "project" || kind == "assembly") && !string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            issues.Add(CreateDependencyIssue(name, kind, "missing_path", $"Referenced file does not exist: {path}"));
        }
    }

    private static JsonObject CreateDependencyIssue(string name, string kind, string code, string message)
        => new()
        {
            ["name"] = name,
            ["kind"] = kind,
            ["code"] = code,
            ["message"] = message,
        };

    private static string CreateDependencyScanSummary(JsonObject payload)
    {
        JsonObject counts = payload["counts"] as JsonObject ?? [];
        int total = counts["total"]?.GetValue<int>() ?? 0;
        int issues = counts["issues"]?.GetValue<int>() ?? 0;
        int warnings = counts["nugetWarnings"]?.GetValue<int>() ?? 0;
        string project = payload["project"]?.GetValue<string>() ?? "project";
        return $"Scanned {total} declared dependencies for '{project}'. Found {issues} dependency issue(s) and {warnings} NuGet warning row(s).";
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
