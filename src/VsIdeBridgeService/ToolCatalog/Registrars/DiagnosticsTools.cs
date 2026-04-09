using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;
using VsIdeBridgeService.Diagnostics;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string Severity = "severity";
    private const string WaitForIntellisense = "wait_for_intellisense";
    private const string Quick = "quick";
    private const string Max = "max";
    private const string Code = "code";
    private const string Project = "project";
    private const string Path = "path";
    private const string Text = "text";
    private const string GroupBy = "group_by";
    private const string WaitForCompletion = "wait_for_completion";
    private const string WaitForIntellisenseHyphen = "wait-for-intellisense";
    private const string WaitForCompletionHyphen = "wait-for-completion";
    private const string Configuration = "configuration";
    private const string Platform = "platform";
    private const string ErrorsOnly = "errors_only";
    private const string Warnings = "warnings";
    private const string RequireCleanDiagnostics = "require_clean_diagnostics";
    private const string Diagnostics = "diagnostics";
    private const string Git = "git";
    private const string FileArg = "file";
    private const string Line = "line";
    private const string BuildSolutionTool = "build_solution";
    private const string Column = "column";
    private const string Query = "query";
    private const string Documents = "documents";
    private const string Message = "message";
    private const string Paths = "paths";
    private const string PostCheck = "post_check";
    private const string Scope = "scope";
    private const string Search = "search";
    private const string Debug = "debug";
    private const string Python = "python";
    private const string Core = "core";
    private const string SystemCategory = "system";

    private const string DefaultMaxRows = "50";
    private const int DefaultCompactDiagnosticsRows = 50;
    private const string Refresh = "refresh";

    private static ToolEntry CreateErrorsTool()
    {
        ToolDefinition errorsDefinition = ToolDefinitionCatalog.Errors(
            ObjectSchema(
                Opt(Severity, "Optional severity filter."),
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness first (default true)."),
                OptBool(Quick, "Read current snapshot immediately (default false)."),
                OptBool(Refresh, "Force the Error List to refresh before reading (default false)."),
                OptInt(Max, "Max rows to return. Defaults to 50 when no filters are set."),
                Opt(Code, "Optional diagnostic code prefix filter."),
                Opt(Project, ProjectFilterDesc),
                Opt(Path, "Optional path filter."),
                Opt(Text, "Optional message text filter."),
                Opt(GroupBy, "Optional grouping mode.")))
            .WithSearchHints(BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file containing the error"), ("goto_definition", "Navigate to the error location"), ("apply_diff", "Fix the error")],
                related: [(Warnings, "Check warnings instead"), ("diagnostics_snapshot", "Get a combined IDE + error snapshot"), ("build_errors", "Run MSBuild directly for a definitive build result")]));
        return new(errorsDefinition,
            async (id, args, bridge) =>
            {
                if (bridge.DocumentDiagnostics.TryGetCachedErrors(args, out JsonObject cachedErrors))
                {
                    return BridgeResult(cachedErrors);
                }

                bool hasFilters = args?[Code] is not null || args?[Severity] is not null
                    || args?[Project] is not null || args?[Path] is not null || args?[Text] is not null;
                string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : (hasFilters ? null : DefaultMaxRows);

                bool useQuick = args?[Quick]?.GetValue<bool>() ?? false;
                string? severityValue = OptionalString(args, Severity) ?? "Error";
                string errorArgs = Build(
                    (Severity, severityValue),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    BoolArg(Quick, args, Quick, useQuick, true),
                    BoolArg(Refresh, args, Refresh, false, true),
                    (Max, maxValue),
                    (Code, OptionalString(args, Code)),
                    (Project, OptionalString(args, Project)),
                    (Path, OptionalString(args, Path)),
                    (Text, OptionalString(args, Text)),
                    ("group-by", OptionalString(args, GroupBy)));
                JsonObject response = await bridge.SendAsync(id, "errors", errorArgs)
                    .ConfigureAwait(false);
                CompactDiagnosticsResponse(response, args);
                return BridgeResult(response);
            });
    }

    private static ToolEntry CreateWarningsTool()
    {
        return new(Warnings,
            "Capture warning rows with optional code/path/project filters.",
            ObjectSchema(
                Opt(Severity, "Optional severity filter."),
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness first (default true)."),
                OptBool(Quick, "Read current snapshot immediately (default false)."),
                OptBool(Refresh, "Force the Error List to refresh before reading (default false)."),
                OptInt(Max, "Max rows to return. Defaults to 50 when no filters are set."),
                Opt(Code, "Optional warning code prefix filter."),
                Opt(Project, ProjectFilterDesc),
                Opt(Path, "Optional path filter."),
                Opt(Text, "Optional message text filter."),
                Opt(GroupBy, "Optional grouping mode (e.g. code).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                if (bridge.DocumentDiagnostics.TryGetCachedWarnings(args, out JsonObject cachedWarnings))
                {
                    return BridgeResult(cachedWarnings);
                }

                bool hasFilters = args?[Code] is not null || args?[Severity] is not null
                    || args?[Project] is not null || args?[Path] is not null || args?[Text] is not null;
                string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : (hasFilters ? null : DefaultMaxRows);

                bool useQuick = args?[Quick]?.GetValue<bool>() ?? false;
                string warningArgs = Build(
                    (Severity, OptionalString(args, Severity)),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    BoolArg(Quick, args, Quick, useQuick, true),
                    BoolArg(Refresh, args, Refresh, false, true),
                    (Max, maxValue),
                    (Code, OptionalString(args, Code)),
                    (Project, OptionalString(args, Project)),
                    (Path, OptionalString(args, Path)),
                    (Text, OptionalString(args, Text)),
                    ("group-by", OptionalString(args, GroupBy)));
                JsonObject response = await bridge.SendAsync(id, Warnings, warningArgs)
                    .ConfigureAwait(false);
                CompactDiagnosticsResponse(response, args);
                return BridgeResult(response);
            },
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file with the warning"), ("goto_definition", "Navigate to the warning location")],
                related: [("errors", "Check errors instead"), ("diagnostics_snapshot", "Get a combined IDE + diagnostics snapshot")]));
    }

    private static ToolEntry CreateMessagesTool()
    {
        ToolDefinition messagesDefinition = ToolDefinitionCatalog.Messages(
                ObjectSchema(
                    Opt(Severity, "Optional severity filter."),
                    OptBool(WaitForIntellisense, "Wait for IntelliSense readiness first (default true)."),
                    OptBool(Quick, "Read current snapshot immediately (default false)."),
                    OptBool(Refresh, "Force the Error List to refresh before reading (default false)."),
                    OptInt(Max, "Max rows to return. Defaults to 50 when no filters are set."),
                    Opt(Code, "Optional message code prefix filter."),
                    Opt(Project, ProjectFilterDesc),
                    Opt(Path, "Optional path filter."),
                    Opt(Text, "Optional message text filter."),
                    Opt(GroupBy, "Optional grouping mode (e.g. code).")))
            .WithSearchHints(
                BuildSearchHints(
                    workflow: [(ReadFileTool, "Read the file behind the message"), ("goto_definition", "Navigate to the message location")],
                related: [(Warnings, "Check warnings instead"), ("errors", "Check errors instead"), ("diagnostics_snapshot", "Get a combined IDE + diagnostics snapshot")]));

        return new(messagesDefinition,
            async (id, args, bridge) =>
            {
                if (bridge.DocumentDiagnostics.TryGetCachedMessages(args, out JsonObject cachedMessages))
                {
                    return BridgeResult(cachedMessages);
                }

                bool hasFilters = args?[Code] is not null || args?[Severity] is not null
                    || args?[Project] is not null || args?[Path] is not null || args?[Text] is not null;
                string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : (hasFilters ? null : DefaultMaxRows);

                bool useQuick = args?[Quick]?.GetValue<bool>() ?? false;
                string? severityValue = OptionalString(args, Severity) ?? "Message";
                string messageArgs = Build(
                    (Severity, severityValue),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    BoolArg(Quick, args, Quick, useQuick, true),
                    BoolArg(Refresh, args, Refresh, false, true),
                    (Max, maxValue),
                    (Code, OptionalString(args, Code)),
                    (Project, OptionalString(args, Project)),
                    (Path, OptionalString(args, Path)),
                    (Text, OptionalString(args, Text)),
                    ("group-by", OptionalString(args, GroupBy)));
                JsonObject response = await bridge.SendAsync(id, "messages", messageArgs)
                    .ConfigureAwait(false);
                CompactDiagnosticsResponse(response, args);
                return BridgeResult(response);
            });
    }

    private static IEnumerable<ToolEntry> DiagnosticsTools() =>
        ErrorDiagnosticsTools()
            .Concat(BuildDiagnosticsTools());

    private static IEnumerable<ToolEntry> ErrorDiagnosticsTools()
    {
        yield return CreateErrorsTool();
        yield return CreateWarningsTool();
        yield return CreateMessagesTool();

        yield return new("diagnostics_snapshot",
            "One-shot snapshot combining IDE state, build status, debugger mode, and error/warning counts. " +
            "Use at the start of a session or after a build instead of calling errors + vs_state separately. " +
            "With wait_for_intellisense=false it prefers the fast current snapshot; true is slower but fresher.",
            ObjectSchema(OptBool(WaitForIntellisense, "Wait for IntelliSense readiness (default false).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool waitForIntellisense = args?[WaitForIntellisense]?.GetValue<bool>() ?? false;
                string snapshotArgs = Build(
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    (Quick, waitForIntellisense ? null : "true"));
                JsonObject response = await bridge.SendAsync(id, "diagnostics-snapshot", snapshotArgs)
                    .ConfigureAwait(false);
                CompactDiagnosticsResponse(response, args);
                return BridgeResult(response, args);
            },
            searchHints: BuildSearchHints(
                related: [("errors", "Get only errors"), (Warnings, "Get only warnings"), ("vs_state", "Check IDE state"), ("build", "Trigger a build")]));
    }

    private static void CompactDiagnosticsResponse(JsonObject response, JsonObject? args)
    {
        if (WantsFullDiagnosticsPayload(args) || response["Data"] is not JsonObject data)
        {
            return;
        }

        int maxRows = args?[Max]?.GetValue<int>() ?? DefaultCompactDiagnosticsRows;
        CompactDiagnosticsNode(data, maxRows);
    }

    private static bool WantsFullDiagnosticsPayload(JsonObject? args)
        => args?["verbose"]?.GetValue<bool?>() == true
            || args?["full"]?.GetValue<bool?>() == true;

    private static void CompactDiagnosticsNode(JsonNode? node, int maxRows)
    {
        switch (node)
        {
            case JsonObject obj:
                CompactDiagnosticsObject(obj, maxRows);
                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    CompactDiagnosticsNode(item, maxRows);
                }
                break;
        }
    }

    private static void CompactDiagnosticsObject(JsonObject obj, int maxRows)
    {
        if (obj["rows"] is JsonArray rows)
        {
            int count = obj["count"]?.GetValue<int>() ?? rows.Count;
            int totalCount = obj["totalCount"]?.GetValue<int>() ?? count;
            bool truncated = count < totalCount || rows.Count > maxRows;

            if (rows.Count > maxRows)
            {
                JsonArray compactRows = [];
                for (int i = 0; i < maxRows; i++)
                {
                    compactRows.Add(rows[i]?.DeepClone());
                }

                obj["rows"] = compactRows;
                obj["count"] = maxRows;
            }

            if (truncated)
            {
                obj["truncated"] = true;
            }
        }

        foreach ((string _, JsonNode? child) in obj)
        {
            CompactDiagnosticsNode(child, maxRows);
        }
    }

    private static IEnumerable<ToolEntry> BuildDiagnosticsTools()
        => BuildConfigurationTools()
            .Concat(BuildExecutionTools())
            .Append(CreateRunCodeAnalysisTool());

    private static IEnumerable<ToolEntry> BuildConfigurationTools()
    {
        yield return BridgeTool("build_configurations",
            "List available solution build configurations and platforms.",
            EmptySchema(), "build-configurations", _ => Empty(), Diagnostics,
            searchHints: BuildSearchHints(
                related: [("build", "Trigger a build"), ("set_build_configuration", "Activate a configuration")]));

        yield return BridgeTool("set_build_configuration",
            "Activate one build configuration/platform pair.",
            ObjectSchema(
                Opt(Configuration, "Build configuration (e.g. Debug, Release)."),
                Opt(Platform, "Build platform (e.g. x64).")),
            "set-build-configuration",
            a => Build(
                (Configuration, OptionalString(a, Configuration)),
                (Platform, OptionalString(a, Platform))),
            Diagnostics,
            searchHints: BuildSearchHints(
                workflow: [("build", "Build with the new configuration")],
                related: [("build_configurations", "List available configurations")]));
    }

    private static IEnumerable<ToolEntry> BuildExecutionTools()
    {

        yield return CreateBuildTool(
            "build",
            "Build the solution or a specific project. Omit project to build the entire solution. Use list_projects to discover project names. Set errors_only=true to return the build summary plus only error rows.",
            "build",
            includeProject: true,
            defaultWaitForCompletion: true,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after building"), ("build_errors", "Run MSBuild directly for a definitive result")],
                related: [("rebuild", "Clean then build"), (BuildSolutionTool, "Build the solution explicitly")]));

        yield return CreateBuildTool(
            "rebuild",
            "Rebuild the active solution inside Visual Studio. This performs a clean step before building and is heavier than build. By default it starts in the background and returns immediately. Set wait_for_completion=true to wait for completion. Set errors_only=true only when waiting.",
            "rebuild",
            includeProject: false,
            defaultWaitForCompletion: false,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after rebuilding")],
                related: [("build", "Build without cleaning"), ("rebuild_solution", "Rebuild the solution explicitly")]));

        yield return CreateBuildTool(
             BuildSolutionTool,
            "Build the active solution explicitly. Use this when you want the solution-wide build command rather than the generic build entry. Set errors_only=true to keep the response compact.",
            "build",
            includeProject: false,
            defaultWaitForCompletion: true,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after building")],
                related: [("build", "Build a specific project"), ("rebuild_solution", "Rebuild the solution")]));

        yield return CreateBuildTool(
            "rebuild_solution",
            "Rebuild the active solution explicitly. Use this when you want the solution-wide rebuild command by name. By default it starts in the background and returns immediately. Set wait_for_completion=true to wait for completion. Set errors_only=true only when waiting.",
            "rebuild-solution",
            includeProject: false,
            defaultWaitForCompletion: false,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after rebuilding")],
                related: [("rebuild", "Generic rebuild"), (BuildSolutionTool, "Build without cleaning")]));

        yield return new("build_errors",
            "Build the active solution through Visual Studio and return only compiler errors as structured JSON. " +
            "Equivalent to build_solution with errors_only=true. " +
            "Use build/build_solution for the full build response including warnings and messages.",
            ObjectSchema(
                Opt(Project, "Project name to build (e.g. VsIdeBridgeInstaller). Omit to build the entire solution."),
                Opt(Configuration, "Optional build configuration (e.g. Release)."),
                Opt(Platform, "Optional build platform (e.g. x64)."),
                OptInt(Max, "Max error rows to return (default 20).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                string buildArgs = Build(
                    (Project, OptionalString(args, Project)),
                    (Configuration, OptionalString(args, Configuration)),
                    (Platform, OptionalString(args, Platform)),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, true, true),
                    BoolArg("require-clean-diagnostics", args, RequireCleanDiagnostics, false, true));

                JsonObject response = await bridge.SendAsync(id, "build", buildArgs).ConfigureAwait(false);

                JsonObject diagnosticsSnapshot = await bridge.DocumentDiagnostics
                    .QueueRefreshAndWaitForSnapshotAsync("build-errors-post-build", clearCached: true)
                    .ConfigureAwait(false);
                response["diagnosticsSnapshot"] = diagnosticsSnapshot;

                JsonObject? errorDiagnostics = await TryCaptureErrorDiagnosticsAsync(id, args, bridge).ConfigureAwait(false);
                if (errorDiagnostics is not null)
                {
                    response["errorDiagnostics"] = errorDiagnostics;
                }
                response["errorsOnly"] = true;
                return BridgeResult(response);
            },
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file with the build error"), ("goto_definition", "Navigate to the error location"), ("apply_diff", "Fix the error")],
                related: [("errors", "Check IDE error list instead"), (BuildSolutionTool, "Build solution with full output")]));
    }

    private static ToolEntry CreateRunCodeAnalysisTool()
    {
        const string TimeoutMs = "timeout_ms";
        return new("run_code_analysis",
            "Run VS code analysis on the solution using the SDK build infrastructure. " +
            "By default this starts analysis and returns immediately so large solutions do not block the MCP call. " +
            "Set wait_for_completion=true to wait for completion and return a paged slice of the Error List.",
            ObjectSchema(
                OptInt(TimeoutMs, "Timeout in milliseconds (default 300000)."),
                OptBool(WaitForCompletion, "When false, start analysis and return immediately (default false). Set true to wait for completion and capture diagnostics."),
                OptInt(Max, "Max diagnostic rows to return in this response (default 50). Call errors to fetch further rows.")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool waitForCompletion = OptionalBool(args, WaitForCompletion, false);
                string analysisArgs = Build(
                    ("timeout-ms", OptionalText(args, TimeoutMs)),
                    BoolArg(WaitForCompletionHyphen, args, WaitForCompletion, false, true));
                JsonObject response = await bridge.SendAsync(id, "run-code-analysis", analysisArgs).ConfigureAwait(false);
                if (!waitForCompletion)
                {
                    return BridgeResult(response);
                }

                JsonObject diagnosticsSnapshot = await bridge.DocumentDiagnostics
                    .QueueRefreshAndWaitForSnapshotAsync("run-code-analysis-post-build", clearCached: true)
                    .ConfigureAwait(false);
                response["diagnosticsSnapshot"] = diagnosticsSnapshot;

                JsonObject? diagnostics = await TryCaptureAnalysisDiagnosticsAsync(id, args, bridge).ConfigureAwait(false);
                if (diagnostics is not null)
                {
                    response["diagnostics"] = diagnostics;
                }
                return BridgeResult(response);
            },
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file with the analysis finding")],
                related: [(BuildSolutionTool, "Build instead of analysing"), ("build_errors", "Build and return errors only")]));
    }

    private static ToolEntry CreateBuildTool(string name, string description, string pipeCommand, bool includeProject, bool defaultWaitForCompletion = true, JsonObject? searchHints = null)
    {
        List<(string Name, JsonObject Schema, bool Required)> properties =
        [
            Opt(Configuration, "Optional build configuration (e.g. Release)."),
            Opt(Platform, "Optional build platform (e.g. x64)."),
            OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before building (default true)."),
            OptBool(WaitForCompletion, $"When false, start the operation and return immediately (default {(defaultWaitForCompletion ? "true" : "false")}). Set true to wait for completion."),
            OptBool(RequireCleanDiagnostics, "When false, bypasses the pre-build dirty-diagnostics guard (default false)."),
            OptBool(ErrorsOnly, "When true, return the build summary plus only error rows so warnings and messages do not flood the response."),
            OptInt(Max, "Max error rows to return when errors_only is true (default 50)."),
        ];
        if (includeProject)
        {
            properties.Insert(0, Opt(Project, "Project name to build (e.g. VsIdeBridgeInstaller). Omit to build the entire solution."));
        }

        return new(name,
            description,
            ObjectSchema([.. properties]),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool waitForCompletion = OptionalBool(args, WaitForCompletion, defaultWaitForCompletion);
                bool errorsOnly = args?[ErrorsOnly]?.GetValue<bool>() ?? false;
                if (!waitForCompletion && errorsOnly)
                {
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, $"'{ErrorsOnly}' requires '{WaitForCompletion}=true'.");
                }

                if (includeProject && !waitForCompletion && !string.IsNullOrWhiteSpace(OptionalString(args, Project)))
                {
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, $"'{WaitForCompletion}=false' is supported only for solution-wide builds.");
                }

                JsonObject? preBuild = errorsOnly || !waitForCompletion ? null : await TryCapturePreBuildDiagnosticsAsync(id, bridge).ConfigureAwait(false);

                string buildArgs = Build(
                    (Project, includeProject ? OptionalString(args, Project) : null),
                    (Configuration, OptionalString(args, Configuration)),
                    (Platform, OptionalString(args, Platform)),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, true, true),
                    BoolArg(WaitForCompletionHyphen, args, WaitForCompletion, defaultWaitForCompletion, true),
                    BoolArg("require-clean-diagnostics", args, RequireCleanDiagnostics, false, true));

                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs).ConfigureAwait(false);

                if (!waitForCompletion)
                {
                    return BridgeResult(response);
                }

                JsonObject diagnosticsSnapshot = await bridge.DocumentDiagnostics
                    .QueueRefreshAndWaitForSnapshotAsync($"{name}-post-build", clearCached: true)
                    .ConfigureAwait(false);
                response["diagnosticsSnapshot"] = diagnosticsSnapshot;

                if (errorsOnly)
                {
                    JsonObject? errorDiagnostics = await TryCaptureErrorDiagnosticsAsync(id, args, bridge).ConfigureAwait(false);
                    if (errorDiagnostics is not null)
                    {
                        response["errorDiagnostics"] = errorDiagnostics;
                    }

                    response["errorsOnly"] = true;
                }
                else if (preBuild is not null)
                {
                    response["preBuildDiagnostics"] = preBuild;
                }

                return BridgeResult(response);
            },
            searchHints: searchHints);
    }

    private static async Task<JsonObject?> TryCapturePreBuildDiagnosticsAsync(JsonNode? id, BridgeConnection bridge)
    {
        try
        {
            if (!bridge.DocumentDiagnostics.TryGetCachedErrors(null, out JsonObject pre))
            {
                Task<JsonObject> preTask = bridge.SendAsync(id, "errors", new JsonObject
                {
                    ["quick"] = true,
                });
                if (await Task.WhenAny(preTask, Task.Delay(3_000)).ConfigureAwait(false) != preTask)
                {
                    throw new OperationCanceledException("Pre-build snapshot timed out.");
                }

                pre = preTask.Result;
            }

            bool preSuccess = pre["Success"]?.GetValue<bool>() ?? false;
            if (!preSuccess)
            {
                return null;
            }

            int errorCount = pre["Data"]?["errorCount"]?.GetValue<int>() ?? 0;
            int warningCount = pre["Data"]?["warningCount"]?.GetValue<int>() ?? 0;
            int messageCount = pre["Data"]?["messageCount"]?.GetValue<int>() ?? 0;
            return errorCount > 0 || warningCount > 0 || messageCount > 0 ? pre : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonObject?> TryCaptureErrorDiagnosticsAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        try
        {
            string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : DefaultMaxRows;
            return await bridge.SendAsync(
                id,
                "errors",
                Build(
                    (Severity, "Error"),
                    (Quick, "true"),
                    (WaitForIntellisenseHyphen, "false"),
                    (Max, maxValue))).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonObject?> TryCaptureAnalysisDiagnosticsAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        try
        {
            string? maxValue = args?[Max] is not null ? OptionalText(args, Max) : "50";
            return await bridge.SendAsync(
                id,
                "errors",
                Build(
                    (Quick, "true"),
                    (WaitForIntellisenseHyphen, "false"),
                    (Max, maxValue))).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
