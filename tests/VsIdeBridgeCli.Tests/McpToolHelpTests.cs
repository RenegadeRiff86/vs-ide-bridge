using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class McpToolHelpTests
{
    private const string ToolHelpName = "tool_help";
    private const string StructuredContentPropertyName = "structuredContent";
    private const string JsonRpcVersion = "2.0";
    private const int MinimumToolCount = 50;
    private const string NugetAddPackageToolName = "nuget_add_package";
    private const string CondaInstallToolName = "conda_install";
    private const string FindFilesToolName = "find_files";
    private const string WarningsToolName = "warnings";
    private const string SearchSymbolsToolName = "search_symbols";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static JsonElement GetStructuredContent(JsonDocument response)
    {
        return response.RootElement
            .GetProperty("result")
            .GetProperty(StructuredContentPropertyName);
    }

    [Fact]
    public async Task ToolHelp_AllTools_HaveDescriptionSchemaAndExample()
    {
        using var response = await CallToolAsync(ToolHelpName, new { });
        var items = GetStructuredContent(response)
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.NotEmpty(items);
        Assert.True(items.Length >= MinimumToolCount, "Expected a broad MCP tool catalog.");

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
        using var response = await CallToolAsync(ToolHelpName, new { });
        var toolMap = GetStructuredContent(response)
            .GetProperty("items")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("name").GetString() ?? string.Empty,
                item => item);

        string[] requiredTools =
        [
            "ready",
            ToolHelpName,
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
            NugetAddPackageToolName,
            "nuget_remove_package",
            CondaInstallToolName,
            "conda_remove",
        ];

        foreach (var tool in requiredTools)
        {
            Assert.True(toolMap.ContainsKey(tool), $"Expected MCP tool '{tool}' to be present.");
        }

        AssertContainsSchemaProperty(toolMap["errors"], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap["errors"], "quick");
        AssertContainsSchemaProperty(toolMap["errors"], "severity");
        AssertContainsSchemaProperty(toolMap["errors"], "group_by");
        AssertContainsSchemaProperty(toolMap["errors"], "timeout_ms");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "quick");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "severity");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "group_by");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "timeout_ms");
        AssertContainsSchemaProperty(toolMap["build"], "platform");
        AssertContainsSchemaProperty(toolMap["open_solution"], "wait_for_ready");
        AssertContainsSchemaProperty(toolMap["open_file"], "allow_disk_fallback");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "path");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "extensions");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "max_results");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "include_non_project");
        AssertContainsSchemaProperty(toolMap["find_text"], "project");
        AssertContainsSchemaProperty(toolMap["find_text"], "results_window");
        AssertContainsSchemaProperty(toolMap["find_text"], "regex");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "scope");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "project");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "path");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "max");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "match_case");
        AssertContainsSchemaProperty(toolMap["read_file"], "reveal_in_editor");
        AssertContainsSchemaProperty(toolMap["nuget_restore"], "path");
        AssertContainsSchemaProperty(toolMap[NugetAddPackageToolName], "project");
        AssertContainsSchemaProperty(toolMap[NugetAddPackageToolName], "package");
        AssertContainsSchemaProperty(toolMap[NugetAddPackageToolName], "version");
        AssertContainsSchemaProperty(toolMap["nuget_remove_package"], "project");
        AssertContainsSchemaProperty(toolMap["nuget_remove_package"], "package");
        AssertContainsSchemaProperty(toolMap[CondaInstallToolName], "packages");
        AssertContainsSchemaProperty(toolMap[CondaInstallToolName], "channels");
        AssertContainsSchemaProperty(toolMap[CondaInstallToolName], "yes");
        AssertContainsSchemaProperty(toolMap["conda_remove"], "packages");
        AssertContainsSchemaProperty(toolMap["conda_remove"], "yes");

        AssertBridgeMetadata(toolMap["state"], "state");
        AssertBridgeMetadata(toolMap["ready"], "ready");
        AssertBridgeMetadata(toolMap[FindFilesToolName], "find-files");
        AssertBridgeMetadata(toolMap["open_file"], "open-document");
        AssertBridgeMetadata(toolMap["debug_threads"], "debug-threads");
        AssertBridgeMetadata(toolMap["diagnostics_snapshot"], "diagnostics-snapshot");
        AssertBridgeMetadata(toolMap["set_build_configuration"], "set-build-configuration");
        AssertBridgeMetadata(toolMap["count_references"], "count-references");
    }

    [Theory]
    [InlineData("help")]
    [InlineData(ToolHelpName)]
    [InlineData("bridge_health")]
    [InlineData("count_references")]
    [InlineData("set_build_configuration")]
    [InlineData("diagnostics_snapshot")]
    [InlineData(NugetAddPackageToolName)]
    [InlineData("nuget_remove_package")]
    [InlineData(CondaInstallToolName)]
    [InlineData("conda_remove")]
    public async Task ToolHelp_FocusedLookup_ReturnsSingleMatch(string toolName)
    {
        using var response = await CallToolAsync(ToolHelpName, new { name = toolName });
        var structured = GetStructuredContent(response);
        var count = structured.GetProperty("count").GetInt32();
        var items = structured.GetProperty("items");

        Assert.Equal(1, count);
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(toolName, items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task McpServer_PersistentSession_ServesMultipleSequentialRequests()
    {
        using var process = StartMcpProcess();

        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "xunit", version = "1.0.0" },
            },
        });

        using var initialize = await ReadResponseAsync(process);
        Assert.Equal(JsonRpcVersion, initialize.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());

        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            method = "notifications/initialized",
        });

        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            id = 2,
            method = "tools/list",
        });

        using var toolsList = await ReadResponseAsync(process);
        var tools = toolsList.RootElement.GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= MinimumToolCount);

        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = ToolHelpName,
                arguments = new { },
            },
        });

        using var toolHelp = await ReadResponseAsync(process);
        var itemCount = toolHelp.RootElement
            .GetProperty("result")
            .GetProperty(StructuredContentPropertyName)
            .GetProperty("count")
            .GetInt32();
        Assert.True(itemCount >= MinimumToolCount);

        process.StandardInput.Close();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task McpServer_PersistentSession_ServesMultiplePipelinedRequests()
    {
        using var process = StartMcpProcess();

        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "xunit", version = "1.0.0" },
            },
        });

        using var initialize = await ReadResponseAsync(process);
        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());

        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            method = "notifications/initialized",
        });

        await WriteRequestAsync(process, new { jsonrpc = JsonRpcVersion, id = 2, method = "tools/list" });
        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = ToolHelpName,
                arguments = new { },
            },
        });
        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "help",
                arguments = new { name = ToolHelpName },
            },
        });

        using var first = await ReadResponseAsync(process);
        using var second = await ReadResponseAsync(process);
        using var third = await ReadResponseAsync(process);

        var responses = new[] { first, second, third }
            .ToDictionary(document => document.RootElement.GetProperty("id").GetInt32());

        Assert.True(responses[2].RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() >= MinimumToolCount);
        Assert.True(responses[3].RootElement.GetProperty("result").GetProperty(StructuredContentPropertyName).GetProperty("count").GetInt32() >= MinimumToolCount);
        Assert.Equal(1, responses[4].RootElement.GetProperty("result").GetProperty(StructuredContentPropertyName).GetProperty("count").GetInt32());
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
        using var process = StartMcpProcess();
        await WriteRequestAsync(process, new
        {
            jsonrpc = JsonRpcVersion,
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments,
            },
        });

        using var response = await ReadResponseAsync(process);
        var json = response.RootElement.GetRawText();

        process.StandardInput.Close();
        var errorText = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"MCP process exited with code {process.ExitCode}: {errorText}");
        }

        return JsonDocument.Parse(json);
    }

    private static Process StartMcpProcess()
    {
        var cliDll = ResolveCliDllPath();
        if (!File.Exists(cliDll))
        {
            throw new FileNotFoundException($"CLI binary not found. Build first: {cliDll}");
        }

        var process = new Process
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
            process.Dispose();
            throw new InvalidOperationException("Failed to start MCP server process.");
        }

        return process;
    }

    private static Task WriteRequestAsync(Process process, object request)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        return process.StandardInput.WriteLineAsync(requestJson);
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process)
    {
        var readLineTask = process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(readLineTask, Task.Delay(TimeSpan.FromSeconds(20)));
        if (completed != readLineTask)
        {
            throw new TimeoutException("Timed out waiting for MCP response.");
        }

        var responseLine = await readLineTask;
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            var errorText = await process.StandardError.ReadToEndAsync();
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



