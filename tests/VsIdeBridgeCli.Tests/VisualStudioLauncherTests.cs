using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class VisualStudioLauncherTests
{
    [Fact]
    public void BuildPowerShellArguments_IncludesStartProcessAndSolutionPath()
    {
        var arguments = VisualStudioLauncher.BuildPowerShellArguments(
            @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
            @"C:\repo\VsIdeBridge.sln");

        Assert.Contains("Start-Process", arguments, StringComparison.Ordinal);
        Assert.Contains("VsIdeBridge.sln", arguments, StringComparison.Ordinal);
        Assert.Contains("-PassThru", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseProcessId_ReadsLastNonEmptyLine()
    {
        var parsed = VisualStudioLauncher.TryParseProcessId("launch requested\r\n12345\r\n", out var processId);

        Assert.True(parsed);
        Assert.Equal(12345, processId);
    }

    [Fact]
    public async Task LaunchAsync_UsesProcessRunnerOutputAsPid()
    {
        var launchResult = await VisualStudioLauncher.LaunchAsync(
            @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
            @"C:\repo\VsIdeBridge.sln",
            static (startInfo, _, _) => Task.FromResult(new ProcessRunner.ProcessRunResult(
                true,
                0,
                startInfo.FileName,
                startInfo.WorkingDirectory,
                startInfo.Arguments,
                "24680\r\n",
                string.Empty)));

        Assert.True(launchResult.Success);
        Assert.Equal(24680, launchResult.ProcessId);
        Assert.Equal("powershell-start-process", launchResult.Launcher);
    }

    [Fact]
    public async Task LaunchAsync_FailsWhenRunnerDoesNotReturnPid()
    {
        var launchResult = await VisualStudioLauncher.LaunchAsync(
            @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
            null,
            static (startInfo, _, _) => Task.FromResult(new ProcessRunner.ProcessRunResult(
                true,
                0,
                startInfo.FileName,
                startInfo.WorkingDirectory,
                startInfo.Arguments,
                string.Empty,
                "launch failed")));

        Assert.False(launchResult.Success);
        Assert.Equal(0, launchResult.ProcessId);
        Assert.Equal("launch failed", launchResult.Stderr);
    }
}
