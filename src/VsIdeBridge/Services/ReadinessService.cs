using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.OperationProgress;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class ReadinessService
{
    public async Task<JObject> WaitForReadyAsync(IdeCommandContext context, int timeoutMilliseconds)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        if (context.Dte.Solution is null || !context.Dte.Solution.IsOpen)
        {
            throw new CommandErrorException("solution_not_open", "No solution is open.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds <= 0 ? 120000 : timeoutMilliseconds);
        var service = await context.Package.GetServiceAsync(typeof(SVsOperationProgressStatusService)).ConfigureAwait(true) as IVsOperationProgressStatusService;
        if (service is null)
        {
            return new JObject
            {
                ["solutionPath"] = context.Dte.Solution.FullName,
                ["serviceAvailable"] = false,
                ["intellisenseCompleted"] = false,
                ["timedOut"] = false,
            };
        }

        var stage = service.GetStageStatusForSolutionLoad(CommonOperationProgressStageIds.Intellisense);
        if (stage is null)
        {
            return new JObject
            {
                ["solutionPath"] = context.Dte.Solution.FullName,
                ["serviceAvailable"] = true,
                ["intellisenseCompleted"] = false,
                ["timedOut"] = false,
            };
        }

        var waitTask = stage.WaitForCompletionAsync();
        var completedTask = await Task.WhenAny(waitTask, Task.Delay(timeout, context.CancellationToken)).ConfigureAwait(false);
        var timedOut = completedTask != waitTask;
        if (!timedOut)
        {
            await waitTask.ConfigureAwait(false);
        }

        return new JObject
        {
            ["solutionPath"] = context.Dte.Solution.FullName,
            ["serviceAvailable"] = true,
            ["intellisenseCompleted"] = !timedOut,
            ["timedOut"] = timedOut,
            ["elapsedMilliseconds"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            ["isInProgress"] = stage.IsInProgress,
        };
    }
}
