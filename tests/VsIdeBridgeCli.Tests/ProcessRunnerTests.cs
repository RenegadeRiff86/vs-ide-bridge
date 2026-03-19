using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class ProcessRunnerTests
{
    private const int OutputLineCount = 200;
    private const int TimeoutMilliseconds = 5000;
    private const int TimeoutTestMilliseconds = 100;
    private const int PollIntervalMilliseconds = 10;
    private const string TempGitFileName = "tracked.txt";

    [Fact]
    public async Task RunAsync_CapturesRedirectedOutput_WithoutTimingOut()
    {
        var startInfo = CreatePowerShellStartInfo($"$i=1; while ($i -le {OutputLineCount}) {{ Write-Output \"line-$i\"; $i++ }}");

        var runResult = await ProcessRunner.RunAsync(startInfo, TimeoutMilliseconds, PollIntervalMilliseconds);

        Assert.True(runResult.Success);
        Assert.Equal(0, runResult.ExitCode);
        Assert.Contains("line-1", runResult.Stdout, StringComparison.Ordinal);
        Assert.Contains($"line-{OutputLineCount}", runResult.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsTimeoutResult_ForLongRunningProcess()
    {
        var startInfo = CreatePowerShellStartInfo("Start-Sleep -Seconds 30");

        var timeoutResult = await ProcessRunner.RunAsync(startInfo, TimeoutTestMilliseconds, PollIntervalMilliseconds);

        Assert.False(timeoutResult.Success);
        Assert.Equal(-1, timeoutResult.ExitCode);
        Assert.Contains("timed out", timeoutResult.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_CapturesFailureOutput_ForNonZeroExitCode()
    {
        var startInfo = CreatePowerShellStartInfo("[Console]::Error.WriteLine('bridge-failure'); exit 3");

        var failureResult = await ProcessRunner.RunAsync(startInfo, TimeoutMilliseconds, PollIntervalMilliseconds);

        Assert.False(failureResult.Success);
        Assert.Equal(3, failureResult.ExitCode);
        Assert.Contains("bridge-failure", failureResult.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ClosesRedirectedStandardInput()
    {
        var startInfo = CreatePowerShellStartInfo("$value = [Console]::In.ReadToEnd(); if ([string]::IsNullOrEmpty($value)) { Write-Output 'stdin-closed'; exit 0 }; exit 7");
        startInfo.RedirectStandardInput = true;

        var stdinResult = await ProcessRunner.RunAsync(startInfo, TimeoutMilliseconds, PollIntervalMilliseconds);

        Assert.True(stdinResult.Success, stdinResult.Stderr);
        Assert.Equal(0, stdinResult.ExitCode);
        Assert.Contains("stdin-closed", stdinResult.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunJsonAsync_ReturnsStructuredProcessMetadata()
    {
        var startInfo = CreatePowerShellStartInfo("Write-Output 'bridge-json'");

        JsonObject result = await ProcessRunner.RunJsonAsync(startInfo, TimeoutMilliseconds);

        Assert.True(result["success"]?.GetValue<bool>());
        Assert.Equal(0, result["exitCode"]?.GetValue<int>());
        Assert.Equal(startInfo.FileName, result["command"]?.GetValue<string>());
        Assert.Equal(startInfo.Arguments, result["args"]?.GetValue<string>());
        Assert.Contains("bridge-json", result["stdout"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CompletesGitStatusAgainstTemporaryRepository()
    {
        var gitPath = GetGitPath();
        Assert.False(string.IsNullOrWhiteSpace(gitPath), "Git executable not found.");

        var tempDirectory = CreateTemporaryDirectory();
        try
        {
            var initResult = await ProcessRunner.RunAsync(CreateProcessStartInfo(gitPath!, "init", tempDirectory), TimeoutMilliseconds, PollIntervalMilliseconds);
            Assert.True(initResult.Success, initResult.Stderr);

            await File.WriteAllTextAsync(Path.Combine(tempDirectory, TempGitFileName), "bridge runner test");

            var statusResult = await ProcessRunner.RunAsync(CreateProcessStartInfo(gitPath!, "status --porcelain=v1 --branch", tempDirectory), TimeoutMilliseconds, PollIntervalMilliseconds);

            Assert.True(statusResult.Success, statusResult.Stderr);
            Assert.Contains("##", statusResult.Stdout, StringComparison.Ordinal);
            Assert.Contains($"?? {TempGitFileName}", statusResult.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CompletesGitStatusAgainstCurrentRepository()
    {
        var gitPath = GetGitPath();
        Assert.False(string.IsNullOrWhiteSpace(gitPath), "Git executable not found.");

        var repositoryRoot = GetRepositoryRoot();
        var arguments = $"--no-pager -c safe.directory=\"{repositoryRoot}\" status --porcelain=v1 --branch";
        var statusResult = await ProcessRunner.RunAsync(CreateProcessStartInfo(gitPath!, arguments, repositoryRoot), TimeoutMilliseconds, PollIntervalMilliseconds);

        Assert.True(statusResult.Success, statusResult.Stderr);
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string command)
    {
        return new ProcessStartInfo
        {
            FileName = GetPowerShellPath(),
            Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_NO_PAGER"] = "1";
        return startInfo;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"VsIdeBridgeCli.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? GetGitPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "git.exe"),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VsIdeBridge.sln"))
                && Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root for debugger-targeted git status test.");
    }

    private static string GetPowerShellPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }
}
