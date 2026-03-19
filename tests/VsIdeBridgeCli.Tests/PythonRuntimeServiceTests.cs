using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace VsIdeBridgeCli.Tests;

[Collection(PythonRuntimeServiceTests.CollectionName)]
public sealed class PythonRuntimeServiceTests
{
    internal const string CollectionName = "PythonRuntimeServiceTests";
    private const string ApprovalRequiredPropertyName = "approvalRequired";
    private const int TestTimeoutMilliseconds = 1000;

    [Fact]
    public async Task ExecuteSnippetAsync_ReturnsApprovalRequired_WhenApprovalMissing()
    {
        using var stateScope = new PythonRuntimeStateScope();

        var approvalCheckResult = await PythonRuntimeService.ExecuteSnippetAsync(
            "print('bridge')",
            interpreterPath: GetFakeInterpreterPath(),
            workingDirectory: stateScope.WorkingDirectory,
            timeoutMs: TestTimeoutMilliseconds,
            approved: false);

        Assert.False(approvalCheckResult["success"]?.GetValue<bool>() ?? true);
        Assert.True(approvalCheckResult[ApprovalRequiredPropertyName]?.GetValue<bool>());
        Assert.False(approvalCheckResult["approvalGranted"]?.GetValue<bool>() ?? true);
        Assert.Equal("required", approvalCheckResult["approvalChoice"]?.GetValue<string>());

        var scope = Assert.IsType<JsonObject>(approvalCheckResult["approvalScope"]);
        Assert.Equal("python_repl", scope["tool"]?.GetValue<string>());
        Assert.Equal("restricted", scope["executionMode"]?.GetValue<string>());
        Assert.Contains("restricted scratch mode", approvalCheckResult["message"]?.GetValue<string>() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteSnippetAsync_DoesNotPersistApproval_ForFutureRuns()
    {
        using var stateScope = new PythonRuntimeStateScope();
        var interpreterPath = GetFakeInterpreterPath();

        var explicitResult = await PythonRuntimeService.ExecuteSnippetAsync(
            "print('bridge')",
            interpreterPath,
            stateScope.WorkingDirectory,
            timeoutMs: TestTimeoutMilliseconds,
            approved: true);

        Assert.Null(explicitResult["approvalChoice"]);
        Assert.Null(explicitResult[ApprovalRequiredPropertyName]);

        var followUpResult = await PythonRuntimeService.ExecuteSnippetAsync(
            "print('bridge')",
            interpreterPath,
            stateScope.WorkingDirectory,
            timeoutMs: TestTimeoutMilliseconds,
            approved: false);

        Assert.Equal("required", followUpResult["approvalChoice"]?.GetValue<string>());
        Assert.True(followUpResult[ApprovalRequiredPropertyName]?.GetValue<bool>());
    }

    [Fact]
    public async Task InstallPackagesAsync_ReturnsApprovalRequired_ForMutatingOperation()
    {
        using var stateScope = new PythonRuntimeStateScope();

        var installApprovalResult = await PythonRuntimeService.InstallPackagesAsync(
            ["requests"],
            interpreterPath: GetFakeInterpreterPath(),
            timeoutMs: TestTimeoutMilliseconds,
            approved: false);

        Assert.False(installApprovalResult["success"]?.GetValue<bool>() ?? true);
        Assert.True(installApprovalResult[ApprovalRequiredPropertyName]?.GetValue<bool>());
        Assert.Contains("modify", installApprovalResult["message"]?.GetValue<string>() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var scope = Assert.IsType<JsonObject>(installApprovalResult["approvalScope"]);
        Assert.True(scope["mutating"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RunFileAsync_ReportsUnrestrictedExecutionMode_WhenRequested()
    {
        using var stateScope = new PythonRuntimeStateScope();

        var runFileResult = await PythonRuntimeService.RunFileAsync(
            filePath: "script.py",
            scriptArguments: ["--check"],
            interpreterPath: GetFakeInterpreterPath(),
            workingDirectory: stateScope.WorkingDirectory,
            timeoutMs: TestTimeoutMilliseconds,
            approved: false,
            allowUnrestrictedExecution: true);

        Assert.False(runFileResult["success"]?.GetValue<bool>() ?? true);
        Assert.True(runFileResult[ApprovalRequiredPropertyName]?.GetValue<bool>());
        Assert.Contains("modify files", runFileResult["message"]?.GetValue<string>() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var scope = Assert.IsType<JsonObject>(runFileResult["approvalScope"]);
        Assert.Equal("unrestricted", scope["executionMode"]?.GetValue<string>());
    }

    [Fact]
    public void BuildRestrictedBootstrapScript_ContainsMutationGuards()
    {
        var script = PythonRuntimeService.BuildRestrictedBootstrapScript("file", @"C:\repo\script.py", ["--check"]);

        Assert.Contains("sys.addaudithook", script, StringComparison.Ordinal);
        Assert.Contains("socket.", script, StringComparison.Ordinal);
        Assert.Contains("subprocess", script, StringComparison.Ordinal);
        Assert.Contains("Bridge restricted Python execution blocked", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEnvironmentsAsync_IncludesConfiguredManagedRuntime()
    {
        using var stateScope = new PythonRuntimeStateScope();
        var interpreterPath = GetFakeInterpreterPath();
        stateScope.WritePythonRuntimeConfig("managed", interpreterPath);

        var envsResult = await PythonRuntimeService.ListEnvironmentsAsync();
        var envs = Assert.IsType<JsonArray>(envsResult["envs"]);

        Assert.Contains(envs, env => PathMatches(env, interpreterPath));
    }

    [Fact]
    public async Task ListEnvironmentsAsync_DoesNotIncludeConfiguredManagedRuntime_WhenProvisioningSkipped()
    {
        using var stateScope = new PythonRuntimeStateScope();
        var interpreterPath = GetFakeInterpreterPath();
        stateScope.WritePythonRuntimeConfig("skip", interpreterPath);

        var envsResult = await PythonRuntimeService.ListEnvironmentsAsync();
        var envs = Assert.IsType<JsonArray>(envsResult["envs"]);

        Assert.DoesNotContain(envs, env => PathMatches(env, interpreterPath));
    }

    private static string GetFakeInterpreterPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "where.exe");
    }

    private static bool PathMatches(JsonNode? env, string interpreterPath)
    {
        var path = env?["path"]?.GetValue<string>();
        return string.Equals(path, interpreterPath, StringComparison.OrdinalIgnoreCase);
    }

    [CollectionDefinition(CollectionName, DisableParallelization = true)]
    public sealed class PythonRuntimeServiceCollectionDefinition;

    private sealed class PythonRuntimeStateScope : IDisposable
    {
        private readonly string? previousConfigDirectory;
        private readonly string? previousStateDirectory;

        internal PythonRuntimeStateScope()
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "VsIdeBridgeCli.Tests.PythonRuntime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
            previousConfigDirectory = Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_CONFIG_DIR");
            previousStateDirectory = Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_PYTHON_STATE_DIR");
            Environment.SetEnvironmentVariable("VS_IDE_BRIDGE_CONFIG_DIR", WorkingDirectory);
            Environment.SetEnvironmentVariable("VS_IDE_BRIDGE_PYTHON_STATE_DIR", WorkingDirectory);
        }

        internal string WorkingDirectory { get; }

        internal void WritePythonRuntimeConfig(string provisioningMode, string? managedEnvironmentPath = null, string? managedBaseInterpreterPath = null)
        {
            var config = new JsonObject
            {
                ["provisioningMode"] = provisioningMode,
            };

            if (!string.IsNullOrWhiteSpace(managedEnvironmentPath))
            {
                config["managedEnvironmentPath"] = managedEnvironmentPath;
            }

            if (!string.IsNullOrWhiteSpace(managedBaseInterpreterPath))
            {
                config["managedBaseInterpreterPath"] = managedBaseInterpreterPath;
            }

            File.WriteAllText(Path.Combine(WorkingDirectory, "python-runtime.json"), config.ToJsonString());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("VS_IDE_BRIDGE_CONFIG_DIR", previousConfigDirectory);
            Environment.SetEnvironmentVariable("VS_IDE_BRIDGE_PYTHON_STATE_DIR", previousStateDirectory);
            try
            {
                if (Directory.Exists(WorkingDirectory))
                {
                    Directory.Delete(WorkingDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup for test-owned temporary state.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup for test-owned temporary state.
            }
        }
    }
}
