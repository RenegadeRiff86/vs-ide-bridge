using System.Diagnostics;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class CliHelpMetadataTests
{
    [Theory]
    [InlineData("ready", "Canonical: Tools.IdeWaitForReady", "vs-ide-bridge ready --timeout-ms 120000")]
    [InlineData("find-files", "Canonical: Tools.IdeFindFiles", "vs-ide-bridge find-files --query \"CMakeLists.txt\"")]
    [InlineData("apply-diff", "Canonical: Tools.IdeApplyUnifiedDiff", "vs-ide-bridge apply-diff --patch-file \"C:\\temp\\change.diff\"")]
    [InlineData("set-breakpoint", "Canonical: Tools.IdeSetBreakpoint", "vs-ide-bridge set-breakpoint --file \"C:\\repo\\src\\foo.cpp\" --line 42")]
    [InlineData("close", "Canonical: Tools.IdeCloseIde", "vs-ide-bridge close-ide")]
    public async Task HelpTopics_IncludeCatalogMetadata(string topic, string expectedCanonical, string expectedExample)
    {
        var cliDll = ResolveCliDllPath();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{cliDll}\" help {topic}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        Assert.True(process.Start(), "Failed to start CLI process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Metadata", stdout);
        Assert.Contains(expectedCanonical, stdout);
        Assert.Contains(expectedExample, stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");
    }

    [Fact]
    public async Task ApplyDiffHelp_ShowsVisibleByDefaultBehavior()
    {
        var cliDll = ResolveCliDllPath();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{cliDll}\" help apply-diff",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        Assert.True(process.Start(), "Failed to start CLI process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("--open-changed-files <true|false> (default: true)", stdout);
        Assert.Contains("Changed files are opened by default", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");
    }

    [Fact]
    public async Task CatalogHelp_DescribesStandardizedCatalogPayload()
    {
        var cliDll = ResolveCliDllPath();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{cliDll}\" help catalog",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        Assert.True(process.Start(), "Failed to start CLI process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("schemaVersion, generatedAtUtc, catalog.commands[]", stdout);
        Assert.Contains("name, canonicalName, description, example, aliases", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"Unexpected stderr: {stderr}");
    }

    private static string ResolveCliDllPath()
    {
        var localOutput = Path.Combine(AppContext.BaseDirectory, "vs-ide-bridge.dll");
        if (File.Exists(localOutput))
        {
            return localOutput;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VsIdeBridge.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("Unable to locate repository root (VsIdeBridge.sln).");
        }

        var debugPath = Path.Combine(
            dir.FullName,
            "src",
            "VsIdeBridgeCli",
            "bin",
            "Debug",
            "net8.0",
            "vs-ide-bridge.dll");

        if (File.Exists(debugPath))
        {
            return debugPath;
        }

        var releasePath = Path.Combine(
            dir.FullName,
            "src",
            "VsIdeBridgeCli",
            "bin",
            "Release",
            "net8.0",
            "vs-ide-bridge.dll");

        return File.Exists(releasePath) ? releasePath : debugPath;
    }
}
