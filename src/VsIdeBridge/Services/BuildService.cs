using System;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class BuildService
{
    private readonly ReadinessService _readinessService;

    public BuildService(ReadinessService readinessService)
    {
        _readinessService = readinessService;
    }

    public async Task<JObject> BuildSolutionAsync(IdeCommandContext context, int timeoutMilliseconds, string? configuration, string? platform)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var dte = context.Dte;
        if (dte.Solution is null || !dte.Solution.IsOpen)
        {
            throw new CommandErrorException("solution_not_open", "No solution is open.");
        }

        var solutionBuild = dte.Solution.SolutionBuild;
        if (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
        {
            throw new CommandErrorException("build_in_progress", "A build is already in progress.");
        }

        TryActivateConfiguration(solutionBuild, configuration, platform);

        var startedAt = DateTimeOffset.UtcNow;
        solutionBuild.Build(true);

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds <= 0 ? 600000 : timeoutMilliseconds);
        while (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new CommandErrorException("timeout", "Timed out waiting for the build to finish.");
            }

            await Task.Delay(500, context.CancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        }

        return new JObject
        {
            ["solutionPath"] = dte.Solution.FullName,
            ["activeConfiguration"] = solutionBuild.ActiveConfiguration?.Name ?? string.Empty,
            ["activePlatform"] = (solutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName ?? string.Empty,
            ["lastBuildInfo"] = solutionBuild.LastBuildInfo,
            ["succeeded"] = solutionBuild.LastBuildInfo == 0,
            ["elapsedMilliseconds"] = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
        };
    }

    public async Task<JObject> BuildAndCaptureErrorsAsync(IdeCommandContext context, int timeoutMilliseconds, bool waitForIntellisense)
    {
        var build = await BuildSolutionAsync(context, timeoutMilliseconds, null, null).ConfigureAwait(true);
        if (waitForIntellisense)
        {
            build["readiness"] = await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds).ConfigureAwait(true);
        }

        return build;
    }

    private static void TryActivateConfiguration(SolutionBuild solutionBuild, string? configuration, string? platform)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (string.IsNullOrWhiteSpace(configuration) && string.IsNullOrWhiteSpace(platform))
        {
            return;
        }

        foreach (SolutionConfiguration2 item in solutionBuild.SolutionConfigurations)
        {
            if (!string.IsNullOrWhiteSpace(configuration) &&
                !string.Equals(item.Name, configuration, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(platform) &&
                !string.Equals(item.PlatformName, platform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item.Activate();
            return;
        }
    }
}
