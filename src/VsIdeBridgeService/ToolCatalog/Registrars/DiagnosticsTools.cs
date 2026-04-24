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
    private const string MessagesTool = "messages";
    private const string DiagnosticsSnapshotCommand = "diagnostics-snapshot";

    private const string DefaultMaxRows = "10";
    private const int DefaultCompactDiagnosticsRows = 10;
    private const int DefaultCompactDiagnosticsStateItems = 10;
    private const string Refresh = "refresh";
    private const string ResponseWarningsProperty = "Warnings";
    private const string PassiveDiagnosticsReadDescription = "Read the current passive diagnostics snapshot immediately. This may be stale relative to the live Error List.";
    private const string RefreshDiagnosticsDescription = "Force the Error List to refresh before reading when you need a fresh UI read (default false).";
    private const string PassiveSnapshotStaleWarning = "Using the passive diagnostics snapshot. This list may be stale relative to the current Visual Studio Error List. Use refresh=true for a fresh UI read.";
    private const string TimedOutDirectReadWarning = "The direct Error List read timed out, so the bridge fell back to diagnostics_snapshot instead of failing outright.";
    private const string QuickFallbackStaleWarning = "The live Error List read was interrupted, so the bridge fell back to a quick diagnostics read. This result may be slightly stale relative to the current UI.";
    private const string SuppressedWarningCode = "BP1044";
    private const int SuppressionPromptUserThreshold = 10;

    private static ToolEntry CreateErrorsTool()
    {
        ToolDefinition errorsDefinition = ToolDefinitionCatalog.Errors(
            ObjectSchema(
                Opt(Severity, "Optional severity filter."),
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before a live filtered or refresh read (default false)."),
                OptBool(Quick, PassiveDiagnosticsReadDescription),
                OptBool(Refresh, RefreshDiagnosticsDescription),
                OptInt(Max, "Max rows to return. Defaults to 10 when no filters are set."),
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

                bool useQuick = ShouldUsePassiveDiagnosticsRead(args);
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
                JsonObject response = await SendDiagnosticsCommandWithSnapshotFallbackAsync(
                        bridge,
                        id,
                        "errors",
                        errorArgs,
                        args)
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
                OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before a live filtered or refresh read (default false)."),
                OptBool(Quick, PassiveDiagnosticsReadDescription),
                OptBool(Refresh, RefreshDiagnosticsDescription),
                OptInt(Max, "Max rows to return. Defaults to 10 when no filters are set."),
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

                bool useQuick = ShouldUsePassiveDiagnosticsRead(args);
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
                JsonObject response = await SendDiagnosticsCommandWithSnapshotFallbackAsync(
                        bridge,
                        id,
                        Warnings,
                        warningArgs,
                        args)
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
                    OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before a live filtered or refresh read (default false)."),
                    OptBool(Quick, PassiveDiagnosticsReadDescription),
                    OptBool(Refresh, RefreshDiagnosticsDescription),
                    OptInt(Max, "Max rows to return. Defaults to 10 when no filters are set."),
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

                bool useQuick = ShouldUsePassiveDiagnosticsRead(args);
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
                JsonObject response = await SendDiagnosticsCommandWithSnapshotFallbackAsync(
                        bridge,
                        id,
                        MessagesTool,
                        messageArgs,
                        args)
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
            "This is a passive snapshot and may be stale relative to the current Error List. " +
            "With wait_for_intellisense=false it prefers the fast current snapshot; true is slower but fresher.",
            ObjectSchema(OptBool(WaitForIntellisense, "Wait for IntelliSense readiness (default false).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                bool waitForIntellisense = args?[WaitForIntellisense]?.GetValue<bool>() ?? false;
                string snapshotArgs = Build(
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
                    (Quick, waitForIntellisense ? null : "true"));
                JsonObject response = await bridge.SendAsync(id, DiagnosticsSnapshotCommand, snapshotArgs)
                    .ConfigureAwait(false);
                AddPassiveSnapshotWarning(response);
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

        int suppressionWarningCount = CountDiagnosticCode(data, SuppressedWarningCode);
        int maxRows = args?[Max]?.GetValue<int>() ?? DefaultCompactDiagnosticsRows;
        CompactDiagnosticsNode(data, maxRows);
        AddSuppressionRepairPrompt(response, suppressionWarningCount);
    }

    private static void AddSuppressionRepairPrompt(JsonObject response, int suppressionWarningCount)
    {
        if (suppressionWarningCount <= 0)
        {
            return;
        }

        string summaryPrompt = suppressionWarningCount > SuppressionPromptUserThreshold
            ? $"User action: ask before fixing {suppressionWarningCount} in-source warning suppressions ({SuppressedWarningCode})."
            : $"Model action: fix {suppressionWarningCount} in-source warning suppression(s) ({SuppressedWarningCode}) now instead of editing around them.";

        string detailPrompt = suppressionWarningCount > SuppressionPromptUserThreshold
            ? $"Detected {suppressionWarningCount} in-source warning suppressions ({SuppressedWarningCode}). Ask the user before making a broad suppression cleanup pass, then remove them in focused batches and fix the underlying warnings instead of keeping the suppressions."
            : $"Detected {suppressionWarningCount} in-source warning suppression(s) ({SuppressedWarningCode}). Fix them now by removing the suppressions and addressing the underlying warnings instead of editing around them.";

        AppendResponseWarning(response, detailPrompt);
        AppendSummaryPrompt(response, summaryPrompt);
    }

    private static void AppendResponseWarning(JsonObject response, string warningText)
    {
        JsonArray warnings = response[ResponseWarningsProperty] as JsonArray is { } existingWarnings
            ? (JsonArray)existingWarnings.DeepClone()
            : [];

        if (!warnings.Any(node => string.Equals(node?.GetValue<string>(), warningText, StringComparison.Ordinal)))
        {
            warnings.Add(warningText);
        }

        response[ResponseWarningsProperty] = warnings;
    }

    private static void AppendSummaryPrompt(JsonObject response, string promptText)
    {
        string? summary = response["Summary"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(summary))
        {
            response["Summary"] = promptText;
            return;
        }

        if (!summary.Contains(promptText, StringComparison.Ordinal))
        {
            response["Summary"] = string.Concat(summary, " ", promptText);
        }
    }

    private static int CountDiagnosticCode(JsonNode? node, string code)
    {
        return node switch
        {
            JsonObject obj => CountDiagnosticCode(obj, code),
            JsonArray arr => arr.Sum(item => CountDiagnosticCode(item, code)),
            _ => 0,
        };
    }

    private static int CountDiagnosticCode(JsonObject obj, string code)
    {
        int count = string.Equals(obj[Code]?.GetValue<string>(), code, StringComparison.Ordinal)
            ? 1
            : 0;

        foreach ((string _, JsonNode? child) in obj)
        {
            count += CountDiagnosticCode(child, code);
        }

        return count;
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

    private static async Task<JsonObject> SendDiagnosticsCommandWithSnapshotFallbackAsync(
        BridgeConnection bridge,
        JsonNode? id,
        string command,
        string argsText,
        JsonObject? args)
    {
        JsonObject response;
        try
        {
            response = await bridge.SendAsync(id, command, argsText).ConfigureAwait(false);
        }
        catch (McpRequestException ex) when (IsInterruptedDiagnosticsException(ex))
        {
            JsonObject? quickFallbackFromException = await TryGetQuickDiagnosticsFallbackAsync(bridge, id, command, args)
                .ConfigureAwait(false);
            if (quickFallbackFromException is not null)
            {
                return quickFallbackFromException;
            }

            JsonObject recoverySnapshotResponse = await bridge.SendAsync(id, DiagnosticsSnapshotCommand, BuildDiagnosticsSnapshotArgs(args)).ConfigureAwait(false);
            JsonObject? fallbackFromException = CreateDiagnosticsResultFromSnapshot(recoverySnapshotResponse, command, interruptedDirectRead: true);
            if (fallbackFromException is not null)
            {
                return fallbackFromException;
            }

            throw;
        }

        if (!IsInterruptedDiagnosticsResponse(response))
        {
            return response;
        }

        JsonObject? quickFallback = await TryGetQuickDiagnosticsFallbackAsync(bridge, id, command, args)
            .ConfigureAwait(false);
        if (quickFallback is not null)
        {
            return quickFallback;
        }

        JsonObject fallbackSnapshotResponse = await bridge.SendAsync(id, DiagnosticsSnapshotCommand, BuildDiagnosticsSnapshotArgs(args)).ConfigureAwait(false);
        JsonObject? fallback = CreateDiagnosticsResultFromSnapshot(fallbackSnapshotResponse, command, interruptedDirectRead: true);
        return fallback ?? response;
    }

    private static bool ShouldUsePassiveDiagnosticsRead(JsonObject? args)
    {
        if (args?[Quick] is JsonNode quickNode)
        {
            return quickNode.GetValue<bool>();
        }

        return args?[Refresh]?.GetValue<bool>() != true;
    }

    private static async Task<JsonObject?> TryGetQuickDiagnosticsFallbackAsync(
        BridgeConnection bridge,
        JsonNode? id,
        string command,
        JsonObject? args)
    {
        try
        {
            JsonObject quickResponse = await bridge.SendAsync(id, command, BuildQuickDiagnosticsArgs(args)).ConfigureAwait(false);
            if (quickResponse["Success"]?.GetValue<bool>() != true)
            {
                return null;
            }

            JsonArray warnings = quickResponse[ResponseWarningsProperty] as JsonArray is { } existingWarnings
                ? (JsonArray)existingWarnings.DeepClone()
                : [];
            warnings.Add($"Fell back to a quick '{command}' read after the direct live read was interrupted.");
            warnings.Add(QuickFallbackStaleWarning);
            quickResponse[ResponseWarningsProperty] = warnings;

            JsonObject data = quickResponse["Data"] as JsonObject ?? [];
            data["Cache"] = new JsonObject
            {
                ["source"] = "quick-direct-fallback",
                ["kind"] = command,
                ["mayBeStale"] = true,
                ["capturedAtUtc"] = quickResponse[FinishedAtUtcProperty]?.DeepClone(),
            };
            quickResponse["Data"] = data;
            return quickResponse;
        }
        catch (McpRequestException ex) when (IsInterruptedDiagnosticsException(ex))
        {
            return null;
        }
    }

    private static bool IsInterruptedDiagnosticsResponse(JsonObject response)
    {
        if (response["Success"]?.GetValue<bool>() == true)
        {
            return false;
        }

        string? summary = response["Summary"]?.GetValue<string>();
        if (string.Equals(summary, "The operation was canceled.", StringComparison.Ordinal))
        {
            return true;
        }

        string? errorMessage = response["Error"]?["message"]?.GetValue<string>();
        return string.Equals(errorMessage, "Bridge server interrupted: The operation was canceled.", StringComparison.Ordinal)
            || IsTimedOutDiagnosticsMessage(errorMessage)
            || IsTimedOutDiagnosticsMessage(summary);
    }

    private static bool IsInterruptedDiagnosticsException(McpRequestException ex)
        => string.Equals(ex.Message, "Bridge server interrupted: The operation was canceled.", StringComparison.Ordinal)
            || string.Equals(ex.Message, "The operation was canceled.", StringComparison.Ordinal)
            || IsTimedOutDiagnosticsMessage(ex.Message);

    private static bool IsTimedOutDiagnosticsMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
            && (message.Contains("Timed out waiting for VS bridge response", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Visual Studio may be blocked", StringComparison.OrdinalIgnoreCase));

    private static string BuildQuickDiagnosticsArgs(JsonObject? args)
        => Build(
            (Severity, OptionalString(args, Severity)),
            (Code, OptionalString(args, Code)),
            (Project, OptionalString(args, Project)),
            (Path, OptionalString(args, Path)),
            (Text, OptionalString(args, Text)),
            ("group-by", OptionalString(args, GroupBy)),
            (Max, OptionalText(args, Max)),
            (Quick, "true"),
            (Refresh, "false"),
            (WaitForIntellisenseHyphen, "false"));

    private static string BuildDiagnosticsSnapshotArgs(JsonObject? args)
    {
        bool waitForIntellisense = args?[WaitForIntellisense]?.GetValue<bool>() ?? false;
        return Build(
            BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, false, true),
            (Quick, waitForIntellisense ? null : "true"));
    }

    private static JsonObject? CreateDiagnosticsResultFromSnapshot(JsonObject snapshotResponse, string command, bool interruptedDirectRead)
    {
        if (snapshotResponse["Success"]?.GetValue<bool>() != true || snapshotResponse["Data"] is not JsonObject snapshotData)
        {
            return null;
        }

        string bucketName = command switch
        {
            "errors" => "errors",
            "warnings" => "warnings",
            MessagesTool => MessagesTool,
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(bucketName) || snapshotData[bucketName] is not JsonObject bucket)
        {
            return null;
        }

        JsonArray warnings = snapshotResponse[ResponseWarningsProperty] as JsonArray is { } existingWarnings
            ? (JsonArray)existingWarnings.DeepClone()
            : [];
        warnings.Add(PassiveSnapshotStaleWarning);
        if (interruptedDirectRead)
        {
            warnings.Add($"Fell back to diagnostics_snapshot after the direct '{command}' read was interrupted.");
            warnings.Add(TimedOutDirectReadWarning);
        }

        int count = bucket["count"]?.GetValue<int>() ?? 0;
        string itemLabel = command switch
        {
            "errors" => "Error List row(s)",
            "warnings" => "warning row(s)",
            "messages" => "message row(s)",
            _ => "row(s)",
        };

        return new JsonObject
        {
            ["SchemaVersion"] = snapshotResponse["SchemaVersion"]?.DeepClone(),
            ["Command"] = command,
            ["RequestId"] = snapshotResponse["RequestId"]?.DeepClone(),
            ["Success"] = true,
            ["StartedAtUtc"] = snapshotResponse["StartedAtUtc"]?.DeepClone(),
            [FinishedAtUtcProperty] = snapshotResponse[FinishedAtUtcProperty]?.DeepClone(),
            ["Summary"] = $"Captured {count} {itemLabel}.",
            [ResponseWarningsProperty] = warnings,
            ["Error"] = null,
            ["Data"] = bucket.DeepClone(),
            ["Cache"] = BuildPassiveSnapshotCache(command, snapshotResponse, snapshotData),
        };
    }

    private static void AddPassiveSnapshotWarning(JsonObject response)
    {
        JsonArray warnings = response[ResponseWarningsProperty] as JsonArray is { } existingWarnings
            ? (JsonArray)existingWarnings.DeepClone()
            : [];
        warnings.Add(PassiveSnapshotStaleWarning);
        response[ResponseWarningsProperty] = warnings;
    }

    private static JsonObject BuildPassiveSnapshotCache(string command, JsonObject snapshotResponse, JsonObject snapshotData)
    {
        JsonObject cache = new()
        {
            ["source"] = "diagnostics-snapshot",
            ["kind"] = command,
            ["mayBeStale"] = true,
        };

        JsonNode? capturedAtUtc = snapshotData["lastCompletedUtc"]?.DeepClone()
            ?? snapshotResponse[FinishedAtUtcProperty]?.DeepClone();
        if (capturedAtUtc is not null)
        {
            cache["capturedAtUtc"] = capturedAtUtc;
        }

        return cache;
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

        CompactPreviewArray(obj, "openDocuments", "openDocumentCount", DefaultCompactDiagnosticsStateItems);
        CompactPreviewArray(obj, "documents", "documentCount", DefaultCompactDiagnosticsStateItems);

        foreach ((string _, JsonNode? child) in obj)
        {
            CompactDiagnosticsNode(child, maxRows);
        }
    }

    private static void CompactPreviewArray(JsonObject obj, string propertyName, string totalCountPropertyName, int maxItems)
    {
        if (obj[propertyName] is not JsonArray items)
        {
            return;
        }

        int originalCount = items.Count;
        if (originalCount == 0)
        {
            obj[totalCountPropertyName] ??= 0;
            return;
        }

        obj[totalCountPropertyName] ??= originalCount;
        if (originalCount <= maxItems)
        {
            return;
        }

        JsonArray compactItems = [];
        for (int i = 0; i < maxItems; i++)
        {
            compactItems.Add(items[i]?.DeepClone());
        }

        obj[propertyName] = compactItems;
        obj[$"{propertyName}Truncated"] = true;
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
            "Build the active solution explicitly. Use this when you want the solution-wide build command rather than the generic build entry. By default it starts in the background and returns immediately so large solution builds do not block the bridge. Set wait_for_completion=true to wait for completion. Set errors_only=true only when waiting.",
            "build",
            includeProject: false,
            defaultWaitForCompletion: false,
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check errors after the user reports the build finished")],
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
                OptBool(RequireCleanDiagnostics, "When false, bypasses the pre-build dirty-diagnostics guard (default true)."),
                OptInt(Max, "Max error rows to return (default 20).")),
            Diagnostics,
            async (id, args, bridge) =>
            {
                string buildArgs = Build(
                    (Project, OptionalString(args, Project)),
                    (Configuration, OptionalString(args, Configuration)),
                    (Platform, OptionalString(args, Platform)),
                    (WaitForCompletionHyphen, "true"),
                    BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, true, true),
                    BoolArg("require-clean-diagnostics", args, RequireCleanDiagnostics, true, true));

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
