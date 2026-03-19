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
    private const string CreateSolutionToolName = "create_solution";
    private const string FindFilesToolName = "find_files";
    private const string VsOpenToolName = "vs_open";
    private const string WaitForInstanceToolName = "wait_for_instance";
    private const string WarningsToolName = "warnings";
    private const string SearchSymbolsToolName = "search_symbols";
    private const string FindTextToolName = "find_text";
    private const string FindTextBatchToolName = "find_text_batch";
    private const string ReadFileBatchToolName = "read_file_batch";
    private const string ExecuteCommandToolName = "execute_command";
    private const string FormatDocumentToolName = "format_document";
    private const string GitMergeToolName = "git_merge";
    private const string QueryProjectItemsToolName = "query_project_items";
    private const string QueryProjectPropertiesToolName = "query_project_properties";
    private const string QueryProjectConfigurationsToolName = "query_project_configurations";
    private const string QueryProjectReferencesToolName = "query_project_references";
    private const string QueryProjectOutputsToolName = "query_project_outputs";
    private const string ShellExecToolName = "shell_exec";
    private const string SetVersionToolName = "set_version";
    private const string UiSettingsToolName = "ui_settings";
    private const string PythonListEnvsToolName = "python_list_envs";
    private const string PythonEnvInfoToolName = "python_env_info";
    private const string PythonSetActiveEnvToolName = "python_set_active_env";
    private const string PythonListPackagesToolName = "python_list_packages";
    private const string PythonReplToolName = "python_repl";
    private const string PythonRunFileToolName = "python_run_file";
    private const string PythonInstallPackageToolName = "python_install_package";
    private const string PythonRemovePackageToolName = "python_remove_package";
    private const string PythonCreateEnvToolName = "python_create_env";
    private const string ApprovedArgumentLiteral = "approved";
    private const string PersistApprovalArgumentLiteral = "persist_approval";
    private const string AnnotationsPropertyName = "annotations";
    private const string DescriptionPropertyName = "description";
    private const string DestructiveHintPropertyName = "destructiveHint";
    private const string ReadOnlyHintPropertyName = "readOnlyHint";
    private const string TimeoutMillisecondsArgumentName = "timeout_ms";
    private const string TitlePropertyName = "title";
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

        foreach (var toolEntry in items)
        {
            var name = toolEntry.GetProperty("name").GetString();
            var description = toolEntry.GetProperty("description").GetString();
            var inputSchema = toolEntry.GetProperty("inputSchema");
            var example = toolEntry.GetProperty("example").GetString();
            var title = toolEntry.GetProperty(TitlePropertyName).GetString();

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.False(string.IsNullOrWhiteSpace(title));
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
            UiSettingsToolName,
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
            VsOpenToolName,
            WaitForInstanceToolName,
            "nuget_restore",
            NugetAddPackageToolName,
            "nuget_remove_package",
            CondaInstallToolName,
            "conda_remove",
            CreateSolutionToolName,
            ExecuteCommandToolName,
            FormatDocumentToolName,
            GitMergeToolName,
            "list_projects",
            QueryProjectItemsToolName,
            QueryProjectPropertiesToolName,
            QueryProjectConfigurationsToolName,
            QueryProjectReferencesToolName,
            QueryProjectOutputsToolName,
            FindTextBatchToolName,
            ReadFileBatchToolName,
            PythonListEnvsToolName,
            PythonEnvInfoToolName,
            PythonSetActiveEnvToolName,
            PythonListPackagesToolName,
            PythonReplToolName,
            PythonRunFileToolName,
            PythonInstallPackageToolName,
            PythonRemovePackageToolName,
            PythonCreateEnvToolName,
        ];

        foreach (var tool in requiredTools)
        {
            Assert.True(toolMap.ContainsKey(tool), $"Expected MCP tool '{tool}' to be present.");
        }

        AssertContainsSchemaProperty(toolMap["errors"], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap["errors"], "quick");
        AssertContainsSchemaProperty(toolMap["errors"], "severity");
        AssertContainsSchemaProperty(toolMap["errors"], "group_by");
        AssertContainsSchemaProperty(toolMap["errors"], TimeoutMillisecondsArgumentName);
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "quick");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "severity");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], "group_by");
        AssertContainsSchemaProperty(toolMap[WarningsToolName], TimeoutMillisecondsArgumentName);
        AssertContainsSchemaProperty(toolMap["build"], "platform");
        AssertContainsSchemaProperty(toolMap["build"], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap["build"], "require_clean_diagnostics");
        AssertContainsSchemaProperty(toolMap["build_errors"], "wait_for_intellisense");
        AssertContainsSchemaProperty(toolMap["build_errors"], "require_clean_diagnostics");
        AssertContainsSchemaProperty(toolMap["open_solution"], "wait_for_ready");
        Assert.Contains("advanced recovery tool", toolMap["bind_instance"].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        AssertContainsSchemaProperty(toolMap[VsOpenToolName], "solution");
        AssertContainsSchemaProperty(toolMap[VsOpenToolName], "devenv_path");
        AssertContainsSchemaProperty(toolMap[WaitForInstanceToolName], "solution");
        AssertContainsSchemaProperty(toolMap[WaitForInstanceToolName], "timeout_ms");
        Assert.Contains("exact solution path", toolMap["open_solution"].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already open in Visual Studio", toolMap["bind_solution"].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only after vs_open", toolMap[WaitForInstanceToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        AssertContainsSchemaProperty(toolMap[CreateSolutionToolName], "directory");
        AssertContainsSchemaProperty(toolMap[CreateSolutionToolName], "name");
        AssertContainsSchemaProperty(toolMap[CreateSolutionToolName], "wait_for_ready");
        AssertContainsSchemaProperty(toolMap[QueryProjectItemsToolName], "project");
        AssertContainsSchemaProperty(toolMap[QueryProjectItemsToolName], "path");
        AssertContainsSchemaProperty(toolMap[QueryProjectItemsToolName], "max");
        AssertContainsSchemaProperty(toolMap[QueryProjectPropertiesToolName], "project");
        AssertContainsSchemaProperty(toolMap[QueryProjectPropertiesToolName], "names");
        AssertContainsSchemaProperty(toolMap[QueryProjectConfigurationsToolName], "project");
        AssertContainsSchemaProperty(toolMap[QueryProjectReferencesToolName], "project");
        AssertContainsSchemaProperty(toolMap[QueryProjectReferencesToolName], "include_framework");
        AssertContainsSchemaProperty(toolMap[QueryProjectReferencesToolName], "declared_only");
        AssertContainsSchemaProperty(toolMap[QueryProjectOutputsToolName], "project");
        AssertContainsSchemaProperty(toolMap[QueryProjectOutputsToolName], "configuration");
        AssertContainsSchemaProperty(toolMap[QueryProjectOutputsToolName], "platform");
        AssertContainsSchemaProperty(toolMap[QueryProjectOutputsToolName], "target_framework");
        AssertContainsSchemaProperty(toolMap["open_file"], "allow_disk_fallback");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "path");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "extensions");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "max_results");
        AssertContainsSchemaProperty(toolMap[FindFilesToolName], "include_non_project");
        AssertContainsSchemaProperty(toolMap[FindTextToolName], "project");
        AssertContainsSchemaProperty(toolMap[FindTextToolName], "results_window");
        AssertContainsSchemaProperty(toolMap[FindTextToolName], "regex");
        AssertContainsSchemaProperty(toolMap[FindTextBatchToolName], "queries");
        AssertContainsSchemaProperty(toolMap[FindTextBatchToolName], "results_window");
        AssertContainsSchemaProperty(toolMap[FindTextBatchToolName], "max_queries_per_chunk");
        AssertContainsSchemaProperty(toolMap[FindTextBatchToolName], "regex");
        AssertContainsSchemaProperty(toolMap[ExecuteCommandToolName], "command");
        AssertContainsSchemaProperty(toolMap[ExecuteCommandToolName], "args");
        AssertContainsSchemaProperty(toolMap[ExecuteCommandToolName], "document");
        AssertContainsSchemaProperty(toolMap[ExecuteCommandToolName], "select_word");
        AssertContainsSchemaProperty(toolMap[FormatDocumentToolName], "file");
        AssertContainsSchemaProperty(toolMap[FormatDocumentToolName], "line");
        AssertContainsSchemaProperty(toolMap[FormatDocumentToolName], "column");
        AssertContainsSchemaProperty(toolMap["set_breakpoint"], "trace_message");
        AssertContainsSchemaProperty(toolMap["set_breakpoint"], "continue_execution");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "scope");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "project");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "path");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "max");
        AssertContainsSchemaProperty(toolMap[SearchSymbolsToolName], "match_case");
        AssertContainsSchemaProperty(toolMap["read_file"], "reveal_in_editor");
        AssertContainsSchemaProperty(toolMap[ReadFileBatchToolName], "ranges");
        AssertContainsSchemaProperty(toolMap["nuget_restore"], "path");
        AssertContainsSchemaProperty(toolMap[NugetAddPackageToolName], "project");
        AssertContainsSchemaProperty(toolMap[NugetAddPackageToolName], "package");
        AssertContainsSchemaProperty(toolMap[NugetAddPackageToolName], "version");
        AssertContainsSchemaProperty(toolMap["nuget_remove_package"], "project");
        AssertContainsSchemaProperty(toolMap["nuget_remove_package"], "package");
        AssertContainsSchemaProperty(toolMap[GitMergeToolName], "source");
        AssertContainsSchemaProperty(toolMap[GitMergeToolName], "ff_only");
        AssertContainsSchemaProperty(toolMap[GitMergeToolName], "no_ff");
        AssertContainsSchemaProperty(toolMap[GitMergeToolName], "squash");
        AssertContainsSchemaProperty(toolMap[GitMergeToolName], "message");
        AssertContainsSchemaProperty(toolMap[CondaInstallToolName], "packages");
        AssertContainsSchemaProperty(toolMap[CondaInstallToolName], "channels");
        AssertContainsSchemaProperty(toolMap[CondaInstallToolName], "yes");
        AssertContainsSchemaProperty(toolMap["conda_remove"], "packages");
        AssertContainsSchemaProperty(toolMap["conda_remove"], "yes");
        AssertContainsSchemaProperty(toolMap[PythonEnvInfoToolName], "path");
        AssertContainsSchemaProperty(toolMap[PythonSetActiveEnvToolName], "path");
        AssertContainsSchemaProperty(toolMap[PythonListPackagesToolName], "path");
        AssertContainsSchemaProperty(toolMap[PythonReplToolName], "code");
        AssertDoesNotContainSchemaProperty(toolMap[PythonReplToolName], ApprovedArgumentLiteral);
        AssertDoesNotContainSchemaProperty(toolMap[PythonReplToolName], PersistApprovalArgumentLiteral);
        AssertContainsSchemaProperty(toolMap[PythonReplToolName], "cwd");
        AssertContainsSchemaProperty(toolMap[PythonReplToolName], TimeoutMillisecondsArgumentName);
        AssertContainsSchemaProperty(toolMap[PythonRunFileToolName], "file");
        AssertContainsSchemaProperty(toolMap[PythonRunFileToolName], "args");
        AssertDoesNotContainSchemaProperty(toolMap[PythonRunFileToolName], ApprovedArgumentLiteral);
        AssertDoesNotContainSchemaProperty(toolMap[PythonRunFileToolName], PersistApprovalArgumentLiteral);
        AssertContainsSchemaProperty(toolMap[PythonInstallPackageToolName], "packages");
        AssertDoesNotContainSchemaProperty(toolMap[PythonInstallPackageToolName], ApprovedArgumentLiteral);
        AssertDoesNotContainSchemaProperty(toolMap[PythonInstallPackageToolName], PersistApprovalArgumentLiteral);
        AssertContainsSchemaProperty(toolMap[PythonRemovePackageToolName], "packages");
        AssertDoesNotContainSchemaProperty(toolMap[PythonRemovePackageToolName], ApprovedArgumentLiteral);
        AssertContainsSchemaProperty(toolMap[PythonCreateEnvToolName], "path");
        AssertContainsSchemaProperty(toolMap[PythonCreateEnvToolName], "base_path");
        AssertDoesNotContainSchemaProperty(toolMap[PythonCreateEnvToolName], ApprovedArgumentLiteral);
        AssertDoesNotContainSchemaProperty(toolMap[PythonCreateEnvToolName], PersistApprovalArgumentLiteral);
        Assert.Contains("Visual Studio approval popup", toolMap[PythonReplToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Visual Studio approval popup", toolMap[PythonInstallPackageToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restricted scratch mode", toolMap[PythonReplToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Allow Bridge Python Unrestricted Execution", toolMap[PythonReplToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Allow Bridge Python Execution", toolMap[PythonReplToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Allow Bridge Python Environment Mutation", toolMap[PythonInstallPackageToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        AssertContainsSchemaProperty(toolMap[ShellExecToolName], "exe");
        AssertContainsSchemaProperty(toolMap[ShellExecToolName], "cwd");
        AssertContainsSchemaProperty(toolMap[ShellExecToolName], TimeoutMillisecondsArgumentName);
        AssertContainsSchemaProperty(toolMap[SetVersionToolName], "version");
        Assert.Contains("approval", toolMap[ShellExecToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approval", toolMap[SetVersionToolName].GetProperty(DescriptionPropertyName).GetString(), StringComparison.OrdinalIgnoreCase);
        AssertToolTitle(toolMap[FindTextToolName], "Text Search");
        AssertToolTitle(toolMap[FindTextBatchToolName], "Batched Text Search");
        AssertToolTitle(toolMap["read_file"], "Read File Slice");
        AssertToolTitle(toolMap[ReadFileBatchToolName], "Read File Slices");
        AssertToolTitle(toolMap["errors"], "Error List Diagnostics");
        AssertToolTitle(toolMap[WarningsToolName], "Warning List Diagnostics");
        AssertToolTitle(toolMap["apply_diff"], "Apply Editor Patch");
        AssertToolTitle(toolMap[UiSettingsToolName], "UI Settings");
        AssertToolTitle(toolMap[PythonListEnvsToolName], "Python List Environments");
        AssertToolTitle(toolMap[ShellExecToolName], "Shell Exec");
        AssertReadOnlyHint(toolMap[FindTextToolName]);
        AssertReadOnlyHint(toolMap[FindTextBatchToolName]);
        AssertReadOnlyHint(toolMap["read_file"]);
        AssertReadOnlyHint(toolMap[ReadFileBatchToolName]);
        AssertReadOnlyHint(toolMap["errors"]);
        AssertReadOnlyHint(toolMap[WarningsToolName]);
        AssertReadOnlyHint(toolMap[UiSettingsToolName]);
        AssertReadOnlyHint(toolMap[PythonListEnvsToolName]);
        AssertDestructiveHint(toolMap["apply_diff"]);
        AssertDestructiveHint(toolMap[ShellExecToolName]);

        AssertBridgeMetadata(toolMap["state"], "state");
        AssertBridgeMetadata(toolMap[UiSettingsToolName], "ui-settings");
        AssertBridgeMetadata(toolMap["ready"], "ready");
        AssertBridgeMetadata(toolMap[FindFilesToolName], "find-files");
        AssertBridgeMetadata(toolMap[FindTextBatchToolName], "find-text-batch");
        AssertBridgeMetadata(toolMap[ReadFileBatchToolName], "document-slices");
        AssertBridgeMetadata(toolMap["open_file"], "open-document");
        AssertBridgeMetadata(toolMap[CreateSolutionToolName], "create-solution");
        AssertBridgeMetadata(toolMap[ExecuteCommandToolName], "execute-command");
        AssertBridgeMetadata(toolMap[QueryProjectItemsToolName], "query-project-items");
        AssertBridgeMetadata(toolMap[QueryProjectPropertiesToolName], "query-project-properties");
        AssertBridgeMetadata(toolMap[QueryProjectConfigurationsToolName], "query-project-configurations");
        AssertBridgeMetadata(toolMap[QueryProjectReferencesToolName], "query-project-references");
        AssertBridgeMetadata(toolMap[QueryProjectOutputsToolName], "query-project-outputs");
        AssertBridgeMetadata(toolMap["debug_threads"], "debug-threads");
        AssertBridgeMetadata(toolMap["diagnostics_snapshot"], "diagnostics-snapshot");
        AssertBridgeMetadata(toolMap["set_build_configuration"], "set-build-configuration");
        AssertBridgeMetadata(toolMap["count_references"], "count-references");
    }

    [Theory]
    [InlineData("help")]
    [InlineData(ToolHelpName)]
    [InlineData("bridge_health")]
    [InlineData(UiSettingsToolName)]
    [InlineData(VsOpenToolName)]
    [InlineData(WaitForInstanceToolName)]
    [InlineData("count_references")]
    [InlineData("set_build_configuration")]
    [InlineData("diagnostics_snapshot")]
    [InlineData(ExecuteCommandToolName)]
    [InlineData(FormatDocumentToolName)]
    [InlineData(FindTextBatchToolName)]
    [InlineData(ReadFileBatchToolName)]
    [InlineData(QueryProjectConfigurationsToolName)]
    [InlineData(QueryProjectReferencesToolName)]
    [InlineData(NugetAddPackageToolName)]
    [InlineData("nuget_remove_package")]
    [InlineData(CondaInstallToolName)]
    [InlineData("conda_remove")]
    [InlineData(ShellExecToolName)]
    [InlineData(SetVersionToolName)]
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
        using var mcpSessionProcess = StartMcpProcess();

        await WriteRequestAsync(mcpSessionProcess, new
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

        using var initialize = await ReadResponseAsync(mcpSessionProcess);
        Assert.Equal(JsonRpcVersion, initialize.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());

        await WriteRequestAsync(mcpSessionProcess, new
        {
            jsonrpc = JsonRpcVersion,
            method = "notifications/initialized",
        });

        await WriteRequestAsync(mcpSessionProcess, new
        {
            jsonrpc = JsonRpcVersion,
            id = 2,
            method = "tools/list",
        });

        using var toolsList = await ReadResponseAsync(mcpSessionProcess);
        var tools = toolsList.RootElement.GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= MinimumToolCount);

        await WriteRequestAsync(mcpSessionProcess, new
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

        using var toolHelp = await ReadResponseAsync(mcpSessionProcess);
        var itemCount = toolHelp.RootElement
            .GetProperty("result")
            .GetProperty(StructuredContentPropertyName)
            .GetProperty("count")
            .GetInt32();
        Assert.True(itemCount >= MinimumToolCount);

        mcpSessionProcess.StandardInput.Close();
        var stderr = await mcpSessionProcess.StandardError.ReadToEndAsync();
        await mcpSessionProcess.WaitForExitAsync();

        Assert.Equal(0, mcpSessionProcess.ExitCode);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task McpServer_PersistentSession_ServesMultiplePipelinedRequests()
    {
        using var mcpSessionProcess = StartMcpProcess();

        await WriteRequestAsync(mcpSessionProcess, new
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

        using var initialize = await ReadResponseAsync(mcpSessionProcess);
        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());

        await WriteRequestAsync(mcpSessionProcess, new
        {
            jsonrpc = JsonRpcVersion,
            method = "notifications/initialized",
        });

        await WriteRequestAsync(mcpSessionProcess, new { jsonrpc = JsonRpcVersion, id = 2, method = "tools/list" });
        await WriteRequestAsync(mcpSessionProcess, new
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
        await WriteRequestAsync(mcpSessionProcess, new
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

        using var first = await ReadResponseAsync(mcpSessionProcess);
        using var second = await ReadResponseAsync(mcpSessionProcess);
        using var third = await ReadResponseAsync(mcpSessionProcess);

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

    private static void AssertDoesNotContainSchemaProperty(JsonElement tool, string propertyName)
    {
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.False(properties.TryGetProperty(propertyName, out _), $"Did not expect schema property '{propertyName}'.");
    }

    private static void AssertToolTitle(JsonElement tool, string expectedTitle)
    {
        Assert.True(tool.TryGetProperty(TitlePropertyName, out var title));
        Assert.Equal(expectedTitle, title.GetString());
    }

    private static void AssertReadOnlyHint(JsonElement tool)
    {
        Assert.True(tool.TryGetProperty(AnnotationsPropertyName, out var annotations));
        Assert.True(annotations.TryGetProperty(ReadOnlyHintPropertyName, out var readOnlyHint));
        Assert.True(readOnlyHint.GetBoolean());
    }

    private static void AssertDestructiveHint(JsonElement tool)
    {
        Assert.True(tool.TryGetProperty(AnnotationsPropertyName, out var annotations));
        Assert.True(annotations.TryGetProperty(DestructiveHintPropertyName, out var destructiveHint));
        Assert.True(destructiveHint.GetBoolean());
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
        using var mcpProcess = StartMcpProcess();
        await WriteRequestAsync(mcpProcess, new
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

        using var response = await ReadResponseAsync(mcpProcess);
        var json = response.RootElement.GetRawText();

        mcpProcess.StandardInput.Close();
        var errorText = await mcpProcess.StandardError.ReadToEndAsync();
        await mcpProcess.WaitForExitAsync();

        if (mcpProcess.ExitCode != 0)
        {
            throw new InvalidOperationException($"MCP process exited with code {mcpProcess.ExitCode}: {errorText}");
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

        var mcpProcess = new Process
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

        if (!mcpProcess.Start())
        {
            mcpProcess.Dispose();
            throw new InvalidOperationException("Failed to start MCP server process.");
        }

        return mcpProcess;
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





