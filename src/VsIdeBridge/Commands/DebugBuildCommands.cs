using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class DebugBuildCommands
{
    private const string CountKey = "count";
    private const string TimeoutMillisecondsArgument = "timeout-ms";
    private const string WaitForIntellisenseArgument = "wait-for-intellisense";
    private const string WaitForCompletionArgument = "wait-for-completion";
    private const string RequireCleanDiagnosticsArgument = "require-clean-diagnostics";
    private const int DefaultDebuggerTimeoutMilliseconds = 120000;
    private const int MinimumBuildErrorsTimeoutMilliseconds = 5000;
    private const int DefaultBuildTimeoutMilliseconds = 600000;
    private const int DefaultBlockingDiagnosticsMax = 50;
    private const string DirtyDiagnosticsCode = "dirty_diagnostics";

    private static CommandExecutionResult CreateCapturedResult(string itemLabel, JObject data)
    {
        return new CommandExecutionResult($"Captured {data[CountKey]} {itemLabel}.", data);
    }

    private static CommandExecutionResult CreateStartedResult(string operationLabel, JObject data)
    {
        return new CommandExecutionResult(
            $"{operationLabel} started in the background and the bridge is released. Prompt the user to reply when Visual Studio finishes, then read warnings, errors, messages, or diagnostics_snapshot.",
            data);
    }

    private static int GetQuickDiagnosticsTimeout(bool quick)
    {
        return quick ? MinimumBuildErrorsTimeoutMilliseconds : DefaultDebuggerTimeoutMilliseconds;
    }

    private static int GetBuildErrorsTimeout(CommandArguments args)
    {
        int timeout = args.GetInt32(TimeoutMillisecondsArgument, DefaultBuildTimeoutMilliseconds);
        if (timeout < MinimumBuildErrorsTimeoutMilliseconds)
        {
            throw new CommandErrorException(
                "invalid_timeout",
                $"'{TimeoutMillisecondsArgument}' must be at least {MinimumBuildErrorsTimeoutMilliseconds}ms for build_errors.");
        }

        return timeout;
    }

    private static int GetPreflightDiagnosticsTimeout(int timeoutMilliseconds)
    {
        return Math.Max(MinimumBuildErrorsTimeoutMilliseconds, Math.Min(timeoutMilliseconds, DefaultDebuggerTimeoutMilliseconds));
    }

    private static int GetTotalSeverityCount(JObject diagnostics, string severity)
    {
        return diagnostics["totalSeverityCounts"]?[severity]?.Value<int>() ?? 0;
    }

    private static (int ErrorCount, int WarningCount, int MessageCount) GetSeverityCounts(JObject diagnostics)
    {
        return (
            GetTotalSeverityCount(diagnostics, "Error"),
            GetTotalSeverityCount(diagnostics, "Warning"),
            GetTotalSeverityCount(diagnostics, "Message"));
    }

    private static string FormatBlockingDiagnosticsSummary(int errorCount, int warningCount, int messageCount)
    {
        static string FormatSegment(int count, string singularLabel)
        {
            return count == 1
                ? $"1 {singularLabel}"
                : $"{count} {singularLabel}s";
        }

        return string.Join(", ",
            [
                FormatSegment(errorCount, "error"),
                FormatSegment(warningCount, "warning"),
                FormatSegment(messageCount, "message"),
            ]);
    }

    private static async Task EnsureCleanDiagnosticsAsync(IdeCommandContext context, CommandArguments args, int timeoutMilliseconds, bool quickPreflight = false)
    {
        if (!args.GetBoolean(RequireCleanDiagnosticsArgument, true))
        {
            return;
        }

        int preflightTimeout = GetPreflightDiagnosticsTimeout(timeoutMilliseconds);
        JObject diagnostics = quickPreflight
            ? await GetDiagnosticsWithFallbackAsync(
                context,
                waitForIntellisense: false,
                preflightTimeout,
                quickSnapshot: true,
                query: new ErrorListQuery { Max = args.GetNullableInt32("max") ?? DefaultBlockingDiagnosticsMax }).ConfigureAwait(true)
            : await GetDiagnosticsSnapshotAsync(
                context,
                args,
                preflightTimeout,
                args.GetBoolean(WaitForIntellisenseArgument, true)).ConfigureAwait(true);

        ThrowIfDiagnosticsPresent(diagnostics, "Build blocked by existing diagnostics", args);
    }

    private static async Task<JObject> GetDiagnosticsSnapshotAsync(IdeCommandContext context, CommandArguments args, int timeoutMilliseconds, bool waitForIntellisense)
    {
        return await GetDiagnosticsWithFallbackAsync(
            context,
            waitForIntellisense,
            timeoutMilliseconds,
            quickSnapshot: true,
            query: new ErrorListQuery { Max = args.GetNullableInt32("max") ?? DefaultBlockingDiagnosticsMax }).ConfigureAwait(true);
    }

    private static async Task<JObject> GetDiagnosticsWithFallbackAsync(
        IdeCommandContext context,
        bool waitForIntellisense,
        int timeoutMilliseconds,
        bool quickSnapshot,
        ErrorListQuery? query = null,
        bool forceRefresh = false)
    {
        try
        {
            return await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                waitForIntellisense,
                timeoutMilliseconds,
                quickSnapshot,
                query,
                includeBuildOutputFallback: true,
                forceRefresh: forceRefresh).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!quickSnapshot)
        {
            return await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                waitForIntellisense: false,
                timeoutMilliseconds,
                quickSnapshot: true,
                query,
                includeBuildOutputFallback: true,
                forceRefresh: false).ConfigureAwait(true);
        }
    }

    private static void ThrowIfDiagnosticsPresent(JObject diagnostics, string summaryPrefix, CommandArguments args, JObject? extraData = null)
    {
        (int errorCount, int warningCount, int messageCount) = GetSeverityCounts(diagnostics);
        if (errorCount == 0 && warningCount == 0 && messageCount == 0)
        {
            return;
        }

        JObject commandData = new()
        {
            ["requireCleanDiagnostics"] = args.GetBoolean(RequireCleanDiagnosticsArgument, true),
            ["diagnostics"] = diagnostics,
            ["blockingCounts"] = new JObject
            {
                ["errors"] = errorCount,
                ["warnings"] = warningCount,
                ["messages"] = messageCount,
            },
        };

        if (extraData is not null)
        {
            foreach (JProperty property in extraData.Properties())
            {
                commandData[property.Name] = property.Value;
            }
        }

        throw new CommandErrorException(
            DirtyDiagnosticsCode,
            $"{summaryPrefix}: {FormatBlockingDiagnosticsSummary(errorCount, warningCount, messageCount)}. Fix them first or set --{RequireCleanDiagnosticsArgument} false to override.",
            commandData);
    }

}
