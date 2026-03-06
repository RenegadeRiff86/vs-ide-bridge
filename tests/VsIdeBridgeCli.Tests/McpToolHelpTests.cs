using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class McpToolHelpTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task ToolHelp_AllTools_HaveDescriptionSchemaAndExample()
    {
        using var response = await CallToolAsync("tool_help", new { });
        var items = response.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.NotEmpty(items);
        Assert.True(items.Length >= 50, "Expected a broad MCP tool catalog.");

        foreach (var item in items)
        {
            var name = item.GetProperty("name").GetString();
            var description = item.GetProperty("description").GetString();
            var inputSchema = item.GetProperty("inputSchema");
            var example = item.GetProperty("example").GetString();

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.Equal("object", inputSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(example));

            using var parsedExample = JsonDocument.Parse(example!);
            Assert.True(parsedExample.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
        }
    }

    [Fact]
    public async Task ToolHelp_ContainsNewTools_AndUpdatedSchemas()
    {
        using var response = await CallToolAsync("tool_help", new { });
        var toolMap = response.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("items")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("name").GetString() ?? string.Empty,
                item => item);

        string[] requiredTools =
        [
            "ready",
            "tool_help",
            "help",
            "debug_threads",
            "debug_stack",
            "debug_locals",
            "debug_modules",
            "debug_watch",
            "debug_exceptions",
            "diagnostics_snapshot",
            "build_configurations",
            "set_build_configuration",
            "count_references",
            "bridge_health",
            "nuget_restore",
            "nuget_add_package",
            "nuget_remove_package",
            "conda_install",
            "conda_remove",
        ];

        foreach (var tool in requiredTools)
        {
            Assert.True(toolMap.ContainsKey(tool), $"Expected MCP tool '{tool}' to be present.");
        }

        AssertContainsSchemaProperty(toolMap["errors"], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap["errors"], "quick");
        AssertContainsSchemaProperty(toolMap["warnings"], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap["warnings"], "quick");
        AssertContainsSchemaProperty(toolMap["build"], "platform");
        AssertContainsSchemaProperty(toolMap["open_solution"], "wait_for_ready");
        AssertContainsSchemaProperty(toolMap["open_file"], "allow_disk_fallback");
        AssertContainsSchemaProperty(toolMap["find_files"], "path");
        AssertContainsSchemaProperty(toolMap["find_files"], "extensions");
        AssertContainsSchemaProperty(toolMap["find_files"], "max_results");
        AssertContainsSchemaProperty(toolMap["find_files"], "include_non_project");
        AssertContainsSchemaProperty(toolMap["read_file"], "reveal_in_editor");
        AssertContainsSchemaProperty(toolMap["nuget_restore"], "path");
        AssertContainsSchemaProperty(toolMap["nuget_add_package"], "project");
        AssertContainsSchemaProperty(toolMap["nuget_add_package"], "package");
        AssertContainsSchemaProperty(toolMap["nuget_add_package"], "version");
        AssertContainsSchemaProperty(toolMap["nuget_remove_package"], "project");
        AssertContainsSchemaProperty(toolMap["nuget_remove_package"], "package");
        AssertContainsSchemaProperty(toolMap["conda_install"], "packages");
        AssertContainsSchemaProperty(toolMap["conda_install"], "channels");
        AssertContainsSchemaProperty(toolMap["conda_install"], "yes");
        AssertContainsSchemaProperty(toolMap["conda_remove"], "packages");
        AssertContainsSchemaProperty(toolMap["conda_remove"], "yes");

        AssertBridgeMetadata(toolMap["state"], "state");
        AssertBridgeMetadata(toolMap["ready"], "ready");
        AssertBridgeMetadata(toolMap["find_files"], "find-files");
        AssertBridgeMetadata(toolMap["open_file"], "open-document");
        AssertBridgeMetadata(toolMap["debug_threads"], "debug-threads");
        AssertBridgeMetadata(toolMap["diagnostics_snapshot"], "diagnostics-snapshot");
        AssertBridgeMetadata(toolMap["set_build_configuration"], "set-build-configuration");
        AssertBridgeMetadata(toolMap["count_references"], "count-references");
    }

    [Theory]
    [InlineData("help")]
    [InlineData("tool_help")]
    [InlineData("bridge_health")]
    [InlineData("count_references")]
    [InlineData("set_build_configuration")]
    [InlineData("diagnostics_snapshot")]
    [InlineData("nuget_add_package")]
    [InlineData("nuget_remove_package")]
    [InlineData("conda_install")]
    [InlineData("conda_remove")]
    public async Task ToolHelp_FocusedLookup_ReturnsSingleMatch(string toolName)
    {
        using var response = await CallToolAsync("tool_help", new { name = toolName });
        var structured = response.RootElement.GetProperty("result").GetProperty("structuredContent");
        var count = structured.GetProperty("count").GetInt32();
        var items = structured.GetProperty("items");

        Assert.Equal(1, count);
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(toolName, items[0].GetProperty("name").GetString());
    }

    private static void AssertContainsSchemaProperty(JsonElement tool, string propertyName)
    {
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.True(properties.TryGetProperty(propertyName, out _), $"Expected schema property '{propertyName}'.");
    }

    private static void AssertBridgeMetadata(JsonElement tool, string expectedCommand)
    {
        Assert.True(tool.TryGetProperty("bridgeCommand", out var bridgeCommand));
        Assert.Equal(expectedCommand, bridgeCommand.GetString());

        Assert.True(tool.TryGetProperty("bridgeExample", out var bridgeExample));
        Assert.False(string.IsNullOrWhiteSpace(bridgeExample.GetString()));
    }

    private static async Task<JsonDocument> CallToolAsync(string toolName, object arguments)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments,
            },
        };

        var cliDll = ResolveCliDllPath();
        if (!File.Exists(cliDll))
        {
            throw new FileNotFoundException($"CLI binary not found. Build first: {cliDll}");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{cliDll}\" mcp-server --tools-only",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start MCP server process.");
        }

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        await process.StandardInput.WriteLineAsync(requestJson).ConfigureAwait(false);
        process.StandardInput.Close();

        var readLineTask = process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(readLineTask, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
        if (completed != readLineTask)
        {
            throw new TimeoutException("Timed out waiting for MCP response.");
        }

        var responseLine = await readLineTask.ConfigureAwait(false);
        var errorText = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"MCP process exited with code {process.ExitCode}: {errorText}");
        }

        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException($"No MCP response received. stderr: {errorText}");
        }

        return JsonDocument.Parse(responseLine);
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
