using Microsoft.VisualStudio.OperationProgress;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class ReadinessService
{
    private const int DefaultReadinessTimeoutMilliseconds = 120_000;
    private const int PollIntervalMilliseconds = 500;
    private const int StableStatusBarSampleCount = 2;

    public async Task<JObject> WaitForReadyAsync(IdeCommandContext context, int timeoutMilliseconds, bool afterEdit = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        if (context.Dte.Solution?.IsOpen != true)
        {
            throw new CommandErrorException("solution_not_open", "No solution is open.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        await context.Logger.LogAsync("IDE Bridge: waiting for IntelliSense readiness", context.CancellationToken).ConfigureAwait(true);
        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds <= 0 ? DefaultReadinessTimeoutMilliseconds : timeoutMilliseconds);
        var service = await context.Package.GetServiceAsync(typeof(SVsOperationProgressStatusService)).ConfigureAwait(true) as IVsOperationProgressStatusService;
        var stage = service?.GetStageStatusForSolutionLoad(CommonOperationProgressStageIds.Intellisense);
        var statusbar = await context.Package.GetServiceAsync(typeof(SVsStatusbar)).ConfigureAwait(true) as IVsStatusbar;
        var deadline = startedAt.Add(timeout);

        // After an edit, VS needs a moment to schedule IntelliSense re-analysis.
        // Wait one poll interval so IsInProgress has a chance to transition from
        // idle to active before we evaluate readiness.
        if (afterEdit)
        {
            await Task.Delay(PollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        }

        var readyStatusSamples = 0;
        var lastStatusBarText = string.Empty;
        var statusBarReady = false;
        var intellisenseCompleted = stage?.IsInProgress == false;
        var satisfiedBy = intellisenseCompleted ? "intellisense" : "pending";

        while (DateTimeOffset.UtcNow < deadline)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

            if (stage is not null)
            {
                intellisenseCompleted = !stage.IsInProgress;
                if (intellisenseCompleted)
                {
                    satisfiedBy = "intellisense";
                    break;
                }
            }

            lastStatusBarText = TryGetStatusBarText(statusbar);
            statusBarReady = IsReadyStatusText(lastStatusBarText);
            readyStatusSamples = statusBarReady ? readyStatusSamples + 1 : 0;
            if (readyStatusSamples >= StableStatusBarSampleCount)
            {
                satisfiedBy = "status-bar";
                break;
            }

            await Task.Delay(PollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
        }

        var timedOut = satisfiedBy == "pending";
        if (timedOut)
        {
            satisfiedBy = "timeout";
        }

        await context.Logger.LogAsync($"IDE Bridge: IntelliSense ready (satisfiedBy={satisfiedBy})", context.CancellationToken).ConfigureAwait(true);

        return new JObject
        {
            ["solutionPath"] = context.Dte.Solution.FullName,
            ["serviceAvailable"] = service is not null,
            ["intellisenseStageAvailable"] = stage is not null,
            ["intellisenseCompleted"] = intellisenseCompleted,
            ["timedOut"] = timedOut,
            ["elapsedMilliseconds"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            ["isInProgress"] = stage?.IsInProgress ?? false,
            ["statusBarAvailable"] = statusbar is not null,
            ["statusBarText"] = lastStatusBarText,
            ["statusBarReady"] = statusBarReady,
            ["readyStatusSamples"] = readyStatusSamples,
            ["satisfiedBy"] = satisfiedBy,
        };
    }

    private static string TryGetStatusBarText(IVsStatusbar? statusbar)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (statusbar is null)
        {
            return string.Empty;
        }

        try
        {
            statusbar.GetText(out var text);
            return text?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsReadyStatusText(string text)
    {
        return string.Equals(text.Trim(), "Ready", StringComparison.OrdinalIgnoreCase);
    }
}
