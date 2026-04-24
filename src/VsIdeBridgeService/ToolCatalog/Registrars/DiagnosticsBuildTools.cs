using System.Text.Json.Nodes;

using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string FinishedAtUtcProperty = "FinishedAtUtc";
    private const int BuildCourtesyWaitMilliseconds = 10_000;
    private const string BackgroundOperationProperty = "backgroundOperation";
    private const string BackgroundOperationStatusCompleted = "completed";
    private const string BackgroundOperationStatusFailed = "failed";

    private static readonly TimeSpan BuildCourtesyPollInterval = TimeSpan.FromMilliseconds(500);

    private static ToolEntry CreateBuildTool(string name, string description, string pipeCommand, bool includeProject, bool defaultWaitForCompletion = true, JsonObject? searchHints = null)
    {
        List<(string Name, JsonObject Schema, bool Required)> properties =
        [
            Opt(Configuration, "Optional build configuration (e.g. Release)."),
            Opt(Platform, "Optional build platform (e.g. x64)."),
            OptBool(WaitForIntellisense, "Wait for IntelliSense readiness before building (default true)."),
            OptBool(WaitForCompletion, $"When false, start the operation and return immediately (default {(defaultWaitForCompletion ? "true" : "false")}). Set true to wait for completion. Solution-wide builds stop waiting after 10 seconds and tell the model to prompt the user before waiting longer."),
            OptBool(RequireCleanDiagnostics, "When false, bypasses the pre-build dirty-diagnostics guard (default true)."),
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
            async (id, args, bridge) => BridgeResult(await ExecuteBuildToolAsync(
                    id,
                    args,
                    bridge,
                    name,
                    pipeCommand,
                    includeProject,
                    defaultWaitForCompletion).ConfigureAwait(false)),
            searchHints: searchHints);
    }

    private static async Task<JsonObject> ExecuteBuildToolAsync(
        JsonNode? id,
        JsonObject? args,
        BridgeConnection bridge,
        string name,
        string pipeCommand,
        bool includeProject,
        bool defaultWaitForCompletion)
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
        bool useCourtesyWait = waitForCompletion && IsSolutionWideBuild(includeProject, args);
        JsonObject response = await bridge.SendAsync(
                id,
                pipeCommand,
                BuildBuildCommandArgs(args, includeProject, waitForCompletion && !useCourtesyWait, defaultWaitForCompletion))
            .ConfigureAwait(false);

        if (!waitForCompletion)
        {
            return response;
        }

        if (useCourtesyWait)
        {
            string expectedOperation = pipeCommand.StartsWith("rebuild", StringComparison.OrdinalIgnoreCase) ? "rebuild" : "build";
            string? expectedStartedAtUtc = response["Data"]?["startedAtUtc"]?.GetValue<string>();

            (JsonObject? backgroundBuildResult, JsonObject? backgroundOperation, bool promptUser) = await WaitForBackgroundBuildAsync(
                    bridge,
                    id,
                    expectedOperation,
                    expectedStartedAtUtc)
                .ConfigureAwait(false);

            if (promptUser)
            {
                AnnotateLongRunningBuildResponse(response, backgroundOperation, errorsOnly);
                return response;
            }

            if (backgroundBuildResult is null)
            {
                ApplyFailedBackgroundBuildResponse(response, backgroundOperation);
            }
            else
            {
                ApplyCompletedBackgroundBuildResponse(response, backgroundBuildResult);
            }
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

        return response;
    }

    private static string BuildBuildCommandArgs(JsonObject? args, bool includeProject, bool waitForCompletion, bool defaultWaitForCompletion)
        => Build(
            (Project, includeProject ? OptionalString(args, Project) : null),
            (Configuration, OptionalString(args, Configuration)),
            (Platform, OptionalString(args, Platform)),
            BoolArg(WaitForIntellisenseHyphen, args, WaitForIntellisense, true, true),
            BoolArg(WaitForCompletionHyphen, args, WaitForCompletion, waitForCompletion, true),
            BoolArg("require-clean-diagnostics", args, RequireCleanDiagnostics, true, true));

    private static bool IsSolutionWideBuild(bool includeProject, JsonObject? args)
        => !includeProject || string.IsNullOrWhiteSpace(OptionalString(args, Project));

    private static async Task<(JsonObject? BuildResult, JsonObject? BackgroundOperation, bool PromptUser)> WaitForBackgroundBuildAsync(
        BridgeConnection bridge,
        JsonNode? id,
        string expectedOperation,
        string? expectedStartedAtUtc)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(BuildCourtesyWaitMilliseconds);
        JsonObject? lastBackgroundOperation = null;

        while (true)
        {
            JsonObject snapshotResponse = await bridge.SendAsync(
                    id,
                    DiagnosticsSnapshotCommand,
                    new JsonObject
                    {
                        [Quick] = true,
                        [WaitForIntellisenseHyphen] = false,
                        [Max] = 1,
                    })
                .ConfigureAwait(false);

            JsonObject? backgroundOperation = snapshotResponse["Data"]?["build"]?[BackgroundOperationProperty] as JsonObject;
            if (backgroundOperation is not null)
            {
                lastBackgroundOperation = (JsonObject)backgroundOperation.DeepClone();
            }

            if (TryResolveBackgroundBuildResult(lastBackgroundOperation, expectedOperation, expectedStartedAtUtc, out JsonObject? buildResult))
            {
                return (buildResult, lastBackgroundOperation, false);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return (null, lastBackgroundOperation, true);
            }

            TimeSpan delay = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(delay < BuildCourtesyPollInterval ? delay : BuildCourtesyPollInterval).ConfigureAwait(false);
        }
    }

    private static bool TryResolveBackgroundBuildResult(
        JsonObject? backgroundOperation,
        string expectedOperation,
        string? expectedStartedAtUtc,
        out JsonObject? buildResult)
    {
        buildResult = null;
        if (backgroundOperation is null)
        {
            return false;
        }

        string? operation = backgroundOperation["operation"]?.GetValue<string>();
        if (!string.Equals(operation, expectedOperation, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? startedAtUtc = backgroundOperation["startedAtUtc"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(expectedStartedAtUtc)
            && !string.Equals(startedAtUtc, expectedStartedAtUtc, StringComparison.Ordinal))
        {
            return false;
        }

        string? status = backgroundOperation["status"]?.GetValue<string>();
        if (string.Equals(status, BackgroundOperationStatusCompleted, StringComparison.OrdinalIgnoreCase))
        {
            buildResult = backgroundOperation["result"] as JsonObject;
            return true;
        }

        return string.Equals(status, BackgroundOperationStatusFailed, StringComparison.OrdinalIgnoreCase);
    }

    private static void AnnotateLongRunningBuildResponse(JsonObject response, JsonObject? backgroundOperation, bool errorsOnly)
    {
        JsonObject data = response["Data"] as JsonObject ?? [];
        data["courtesyWaitExceeded"] = true;
        data["courtesyWaitMilliseconds"] = BuildCourtesyWaitMilliseconds;
        data["promptUserToContinue"] = true;
        data["followUpHint"] = "The build is still running. Prompt the user before waiting longer, then read errors, warnings, messages, or diagnostics_snapshot when it finishes.";
        if (errorsOnly)
        {
            data["errorsOnlyPending"] = true;
        }

        if (backgroundOperation is not null)
        {
            data[BackgroundOperationProperty] = backgroundOperation.DeepClone();
        }

        response["Data"] = data;
        AppendSummaryPrompt(response, "The build is still running after 10 seconds. Prompt the user before waiting longer.");
        AppendResponseWarning(response, "The build is still running after 10 seconds. Prompt the user before waiting longer.");
    }

    private static void ApplyCompletedBackgroundBuildResponse(JsonObject response, JsonObject buildResult)
    {
        string operation = buildResult["operation"]?.GetValue<string>() ?? "build";
        string operationLabel = string.Equals(operation, "rebuild", StringComparison.OrdinalIgnoreCase) ? "Rebuild" : "Build";

        response["Data"] = buildResult.DeepClone();
        response["Summary"] = $"{operationLabel} completed with LastBuildInfo={buildResult["lastBuildInfo"]}.";
        response[FinishedAtUtcProperty] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
    }

    private static void ApplyFailedBackgroundBuildResponse(JsonObject response, JsonObject? backgroundOperation)
    {
        string errorMessage = backgroundOperation?["errorMessage"]?.GetValue<string>()
            ?? "The background build failed before a completed result could be captured.";

        JsonObject data = response["Data"] as JsonObject ?? [];
        if (backgroundOperation is not null)
        {
            data[BackgroundOperationProperty] = backgroundOperation.DeepClone();
        }

        response["Data"] = data;
        response["Success"] = false;
        response["Summary"] = errorMessage;
        response["Error"] = new JsonObject
        {
            ["message"] = errorMessage,
        };
        response[FinishedAtUtcProperty] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        AppendResponseWarning(response, "The background build failed before a completed result could be captured.");
    }

    private static async Task<JsonObject?> TryCapturePreBuildDiagnosticsAsync(JsonNode? id, BridgeConnection bridge)
    {
        try
        {
            if (!bridge.DocumentDiagnostics.TryGetCachedErrors(null, out JsonObject pre))
            {
                Task<JsonObject> preTask = bridge.SendAsync(id, "errors", new JsonObject
                {
                    [Quick] = true,
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

            int errorCount = pre["Data"]?["totalCount"]?.GetValue<int>() ?? pre["Data"]?["count"]?.GetValue<int>() ?? 0;
            return errorCount > 0 ? pre : null;
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
}
