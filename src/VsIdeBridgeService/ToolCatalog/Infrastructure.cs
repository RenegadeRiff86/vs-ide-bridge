using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static readonly Lazy<ToolRegistry> DefinitionRegistry = new(BuildDefinitionRegistry);
    private const int MaxSearchHintTools = 4;
    private const string HintListProjectsTool = "list_projects";
    private const string HintOpenFileTool = "open_file";
    private const string HintReadFileTool = "read_file";
    private const string HintVsStateTool = "vs_state";
    private const string HintWarningsTool = "warnings";

    private static ToolRegistry BuildDefinitionRegistry()
    {
        return new ToolRegistry(CreateEntries().Select(static entry => entry.Definition));
    }

    private static ToolEntry EnrichSearchHints(ToolEntry entry)
    {
        JsonObject? searchHints = NormalizeSearchHints(
            entry.Definition.SearchHints ?? GetDefaultSearchHints(entry.Name, entry.Category));

        if (searchHints is null)
        {
            return entry;
        }

        if (entry.Definition.SearchHints is not null
            && JsonNode.DeepEquals(entry.Definition.SearchHints, searchHints))
        {
            return entry;
        }

        return entry.WithDefinition(entry.Definition.WithSearchHints(searchHints));
    }

    private static JsonObject? GetDefaultSearchHints(string toolName, string category)
    {
        return toolName switch
        {
            "list_documents" => BuildSearchHints(
                workflow: [("activate_document", "Switch to one of the open documents"), (HintReadFileTool, "Read the file behind an open document")],
                related: [("list_tabs", "See open tabs and the active tab"), (HintOpenFileTool, "Open another file by path")]),
            "list_tabs" => BuildSearchHints(
                workflow: [("activate_document", "Switch to a tab you want to inspect"), (HintReadFileTool, "Read the file behind a selected tab")],
                related: [("list_documents", "List open documents with paths"), ("close_document", "Close a specific tab")]),
            "activate_document" => BuildSearchHints(
                workflow: [(HintReadFileTool, "Read the active document after switching"), ("file_outline", "Inspect the active file structure")],
                related: [("list_tabs", "See which tabs are currently open"), (HintOpenFileTool, "Open a file by path instead")]),
            "close_document" => BuildSearchHints(
                related: [("list_tabs", "Inspect current open tabs"), ("activate_document", "Focus the tab before closing it")]),
            "save_document" => BuildSearchHints(
                workflow: [("reload_document", "Reload if the file was also changed outside VS"), ("errors", "Check diagnostics after saving")],
                related: [("format_document", "Format before saving"), ("apply_diff", "Make targeted edits first")]),
            "reload_document" => BuildSearchHints(
                workflow: [(HintReadFileTool, "Verify the latest file contents"), ("errors", "Check diagnostics after reloading")],
                related: [("save_document", "Save current editor changes first"), ("apply_diff", "Make another targeted edit")]),
            "format_document" => BuildSearchHints(
                workflow: [("save_document", "Save the formatted file"), ("errors", "Check diagnostics after formatting")],
                related: [(HintReadFileTool, "Read the current file slice first"), ("apply_diff", "Make targeted edits before formatting")]),
            "list_windows" => BuildSearchHints(
                workflow: [("activate_window", "Bring a specific tool window forward"), ("capture_vs_window", "Capture the current VS window")],
                related: [(HintVsStateTool, "Check the current IDE state"), ("bridge_health", "Confirm the bound instance")]),
            "activate_window" => BuildSearchHints(
                workflow: [("capture_vs_window", "Capture the VS UI after focusing the window"), (HintVsStateTool, "Confirm the active solution and document")],
                related: [("list_windows", "Inspect available VS windows"), ("bridge_health", "Confirm the bound instance first")]),
            "open_solution" => BuildSearchHints(
                workflow: [(HintVsStateTool, "Confirm the switched solution"), ("wait_for_ready", "Wait for IntelliSense to load after switching")],
                related: [("bind_solution", "Bind to an already-open matching solution"), ("list_instances", "Inspect visible VS instances")]),
            "create_solution" => BuildSearchHints(
                workflow: [(HintListProjectsTool, "Confirm the new solution contents"), ("create_project", "Add a first project to the solution")],
                related: [("open_solution", "Switch to an existing solution instead"), (HintVsStateTool, "Confirm the active solution")]),
            "search_solutions" => BuildSearchHints(
                workflow: [("open_solution", "Open a discovered solution"), ("bind_solution", "Bind to a matching open solution")],
                related: [("list_instances", "See currently open VS instances"), (HintVsStateTool, "Check the currently bound solution")]),
            "create_project" => BuildSearchHints(
                workflow: [(HintListProjectsTool, "Confirm the new project was added"), ("query_project_items", "Inspect the project contents")],
                related: [("add_project", "Add an existing project instead"), ("set_startup_project", "Make the new project the startup project")]),
            "add_project" => BuildSearchHints(
                workflow: [(HintListProjectsTool, "Confirm the project was added"), ("query_project_items", "Inspect the project contents")],
                related: [("create_project", "Create a new project instead"), ("remove_project", "Remove a project from the solution")]),
            "remove_project" => BuildSearchHints(
                workflow: [(HintListProjectsTool, "Confirm the project was removed")],
                related: [("add_project", "Add an existing project"), ("create_project", "Create a replacement project")]),
            "rename_project" => BuildSearchHints(
                workflow: [(HintListProjectsTool, "Confirm the renamed project"), ("set_startup_project", "Update startup selection if needed")],
                related: [("query_project_properties", "Inspect project metadata"), ("query_project_items", "Inspect project contents")]),
            "set_startup_project" => BuildSearchHints(
                workflow: [(HintVsStateTool, "Confirm the active startup project"), ("debug_start", "Start debugging the selected startup project")],
                related: [(HintListProjectsTool, "List available projects"), ("query_project_outputs", "Inspect what the project builds")]),
            "query_project_configurations" => BuildSearchHints(
                workflow: [("set_build_configuration", "Activate one of the project configurations"), ("build", "Build using the chosen configuration")],
                related: [("build_configurations", "List solution-wide configurations"), ("query_project_properties", "Inspect project metadata")]),
            "query_project_outputs" => BuildSearchHints(
                workflow: [("build", "Build before checking the output artifact"), (HintOpenFileTool, "Open the produced file if it is in the workspace")],
                related: [("query_project_properties", "Inspect output-related MSBuild properties"), ("set_startup_project", "Choose the project for run/debug")]),
            "scan_project_dependencies" => BuildSearchHints(
                workflow: [("nuget_add_package", "Add a missing package"), ("nuget_remove_package", "Remove an unused package")],
                related: [("query_project_references", "Inspect project references"), (HintWarningsTool, "Review dependency-related warnings")]),
            "python_get_startup_file" => BuildSearchHints(
                workflow: [(HintOpenFileTool, "Open the startup file"), ("python_set_startup_file", "Change the startup file")],
                related: [("python_sync_env", "Sync the active interpreter to the project"), ("python_set_project_env", "Select a different interpreter")]),
            "python_set_startup_file" => BuildSearchHints(
                workflow: [("python_get_startup_file", "Verify the selected startup file"), ("debug_start", "Run the Python startup file under the debugger")],
                related: [(HintOpenFileTool, "Open the startup file"), ("python_set_project_env", "Change the active interpreter")]),
            "python_sync_env" => BuildSearchHints(
                workflow: [("python_env_info", "Inspect the synced interpreter"), ("python_list_packages", "Check installed packages")],
                related: [("python_set_project_env", "Choose a specific interpreter"), ("python_get_startup_file", "Check the startup file")]),
            _ => GetCategoryDefaultSearchHints(category),
        };
    }

    private static JsonObject? GetCategoryDefaultSearchHints(string category)
    {
        return category switch
        {
            "debug" => BuildSearchHints(
                related: [("debug_stack", "Inspect the current call stack"), ("debug_threads", "Inspect available debugger threads")]),
            "git" => BuildSearchHints(
                related: [("git_status", "Inspect working tree state"), ("git_diff_unstaged", "Review unstaged changes"), ("git_diff_staged", "Review staged changes")]),
            "python" => BuildSearchHints(
                related: [("python_list_envs", "List available Python interpreters"), ("python_env_info", "Inspect one interpreter"), ("python_list_packages", "Inspect installed packages")]),
            "projects" => BuildSearchHints(
                related: [(HintListProjectsTool, "List loaded projects"), ("query_project_items", "Inspect project contents"), ("query_project_properties", "Inspect MSBuild metadata")]),
            Core => BuildSearchHints(
                related: [("tool_help", "Get focused help for one tool"), ("bridge_health", "Inspect binding and discovery state")]),
            Search => BuildSearchHints(
                related: [("find_files", "Locate files by name"), ("search_symbols", "Search named definitions"), (HintReadFileTool, "Read the matched file contents")]),
            "documents" => BuildSearchHints(
                related: [(HintReadFileTool, "Read file contents"), ("apply_diff", "Make targeted edits"), (HintOpenFileTool, "Open a file in the editor")]),
            _ => null,
        };
    }

    private static JsonObject? NormalizeSearchHints(JsonObject? searchHints)
    {
        if (searchHints is null)
        {
            return null;
        }

        JsonObject normalized = [];
        JsonArray? workflow = NormalizeSearchHintGroup(searchHints["workflow"] as JsonArray);
        JsonArray? related = NormalizeSearchHintGroup(searchHints["related"] as JsonArray);

        if (workflow is not null)
        {
            normalized["workflow"] = workflow;
        }

        if (related is not null)
        {
            normalized["related"] = related;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static JsonArray? NormalizeSearchHintGroup(JsonArray? items)
    {
        if (items is null)
        {
            return null;
        }

        JsonArray normalized = [];
        HashSet<string> seenTools = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonNode? item in items)
        {
            if (item is not JsonObject hint)
            {
                continue;
            }

            string? tool = hint["tool"]?.GetValue<string>();
            string? reason = hint["reason"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            if (!seenTools.Add(tool))
            {
                continue;
            }

            normalized.Add(new JsonObject
            {
                ["tool"] = tool,
                ["reason"] = reason,
            });

            if (normalized.Count >= MaxSearchHintTools)
            {
                break;
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    // Build structured search_hints from workflow and related tool lists.
    private static JsonObject BuildSearchHints(
        IEnumerable<(string tool, string reason)>? workflow = null,
        IEnumerable<(string tool, string reason)>? related = null)
    {
        JsonObject hints = [];
        if (workflow is not null)
        {
            JsonArray workflowArray = [];
            foreach ((string tool, string reason) in workflow)
                workflowArray.Add(new JsonObject { ["tool"] = tool, ["reason"] = reason });
            hints["workflow"] = workflowArray;
        }

        if (related is not null)
        {
            JsonArray relatedArray = [];
            foreach ((string tool, string reason) in related)
                relatedArray.Add(new JsonObject { ["tool"] = tool, ["reason"] = reason });
            hints["related"] = relatedArray;
        }

        return hints;
    }

    // Send one bridge command and wrap the bridge response as an MCP tool result.
    private static ToolEntry BridgeTool(
        ToolDefinition definition,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs,
        JsonObject? searchHints = null)
        => new(searchHints is not null ? definition.WithSearchHints(searchHints) : definition,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args);
            });

    private static ToolEntry BridgeTool(
        string name,
        string description,
        JsonObject schema,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs,
        string category = "core",
        string? title = null,
        JsonObject? annotations = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        string? summary = null,
        bool? readOnly = null,
        bool? mutating = null,
        bool? destructive = null,
        JsonObject? searchHints = null,
        JsonObject? outputSchema = null)
        => new(name, description, schema, category,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args, dataOnly: outputSchema is not null);
            },
            title,
            annotations,
            outputSchema: outputSchema,
            aliases,
            tags,
            bridgeCommand: pipeCommand,
            summary,
            readOnly,
            mutating,
            destructive,
            searchHints);

    // Send one bridge command but keep the MCP tool distinct from the pipe
    // command alias. Use this for convenience wrappers that target an existing
    // bridge command with reshaped/defaulted arguments.
    private static ToolEntry BridgeWrapperTool(
        string name,
        string description,
        JsonObject schema,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs,
        string category = "core",
        string? title = null,
        JsonObject? annotations = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        string? summary = null,
        bool? readOnly = null,
        bool? mutating = null,
        bool? destructive = null,
        JsonObject? searchHints = null,
        JsonObject? outputSchema = null)
        => new(name, description, schema, category,
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(response, args, dataOnly: outputSchema is not null);
            },
            title,
            annotations,
            outputSchema: outputSchema,
            aliases,
            tags,
            bridgeCommand: null,
            summary,
            readOnly,
            mutating,
            destructive,
            searchHints);

    private static JsonNode BridgeResult(JsonObject response, JsonObject? args = null, bool dataOnly = false)
    {
        bool success = (response["Success"] ?? response["success"])?.GetValue<bool>() ?? false;
        bool isError = ToolResultFormatter.ShouldTreatAsError(response, !success);
        return ToolResultFormatter.StructuredToolResult(response, args, isError: isError, dataOnly: dataOnly);
    }
}

internal static class ToolResultFormatter
{
    private const string FormatterWarningsCommand = "warnings";

    internal static bool ShouldTreatAsError(JsonObject response, bool defaultIsError)
    {
        if (defaultIsError)
        {
            return true;
        }

        string? command = response["Command"]?.GetValue<string>();
        if (!string.Equals(command, FormatterWarningsCommand, StringComparison.OrdinalIgnoreCase)
            || response["Data"] is not JsonObject data)
        {
            return false;
        }

        int totalCount = data["totalCount"]?.GetValue<int>()
            ?? data["count"]?.GetValue<int>()
            ?? 0;

        return totalCount > 0;
    }

    internal static JsonNode StructuredToolResult(
        JsonObject response,
        JsonObject? args = null,
        bool isError = false,
        string? successText = null,
        bool dataOnly = false)
    {
        string text;
        if (isError)
        {
            // Show the human-readable error message, not the full JSON blob.
            string? errorCode = response["Error"]?["code"]?.GetValue<string>();
            string? errorMsg = response["Error"]?["message"]?.GetValue<string>();
            string? summary = response["Summary"]?.GetValue<string>();
            // If no actual error message exists (e.g. warnings flagged as error for attention),
            // use the diagnostics success text so row data is visible in the response.
            string? diagnosticsText = errorMsg is null ? CreateDiagnosticsSuccessText(response) : null;
            string baseText = diagnosticsText ?? errorMsg ?? summary ?? response.ToJsonString();
            string? hint = GetErrorHint(errorCode);
            text = hint is null ? baseText : baseText + " " + hint;
        }
        else if (WantsFullSuccessPayload(args))
        {
            text = response.ToJsonString();
        }
        else if (successText != null)
        {
            // Append list data even when the caller supplied explicit summary text.
            string? listText = CreateDataListText(response);
            text = string.IsNullOrWhiteSpace(listText) ? successText : successText + "\n" + listText;
        }
        else
        {
            text = CreateSuccessText(response);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["isError"] = isError,
            ["structuredContent"] = dataOnly ? ExtractDataContent(response) : CreateStructuredContent(response, args),
        };
    }

    private const int MaxDiagnosticTextRows = 10;
    private const int MaxDiagnosticPreviewRows = 2;

    private static bool WantsFullSuccessPayload(JsonObject? args)
        => args?["verbose"]?.GetValue<bool?>() == true
            || args?["full"]?.GetValue<bool?>() == true;

    private static JsonNode CreateStructuredContent(JsonObject response, JsonObject? args)
    {
        if (WantsFullSuccessPayload(args) || !ShouldCompactDiagnosticsStructuredContent(response))
        {
            return response.DeepClone();
        }

        JsonObject compact = new()
        {
            ["SchemaVersion"] = response["SchemaVersion"]?.DeepClone(),
            ["Command"] = response["Command"]?.DeepClone(),
            ["RequestId"] = response["RequestId"]?.DeepClone(),
            ["Success"] = response["Success"]?.DeepClone() ?? true,
            ["Summary"] = response["Summary"]?.DeepClone(),
            ["Warnings"] = response["Warnings"]?.DeepClone() ?? new JsonArray(),
            ["Error"] = response["Error"]?.DeepClone(),
            ["Data"] = response["Data"]?.DeepClone(),
        };

        if (response["Cache"] is JsonNode cache)
        {
            compact["Cache"] = cache.DeepClone();
        }

        if (response["BindingNotice"] is JsonNode bindingNotice)
        {
            compact["BindingNotice"] = bindingNotice.DeepClone();
        }

        return compact;
    }

    private static JsonObject ExtractDataContent(JsonObject response)
    {
        if (response["Data"]?.DeepClone() is not JsonObject data)
            return [];
        data.Remove("queue");
        return data;
    }

    private static bool ShouldCompactDiagnosticsStructuredContent(JsonObject response)
        => response["Data"] is JsonObject
            && response["Command"]?.GetValue<string>() is "errors" or FormatterWarningsCommand or "messages" or "diagnostics-snapshot";

    private static string? GetErrorHint(string? code) => code switch
    {
        "document_not_found"     => "Fix: use find_files to locate the correct path, or verify the file is part of the loaded solution.",
        "file_not_found"         => "Fix: use find_files or glob to locate the correct file path.",
        "project_not_found"      => "Fix: call list_projects to see all loaded projects and their names.",
        "solution_not_open"      => "Fix: open a solution first with open_solution, or use bind_solution if one is already loaded.",
        "invalid_arguments"      => "Fix: call tool_help with the tool name to see correct parameters and examples.",
        "invalid_json"           => "Fix: check the argument is valid JSON — no trailing commas, unescaped characters, or mismatched brackets.",
        "not_in_break_mode"      => "Fix: the debugger must be paused — use debug_break to pause, or set a breakpoint with set_breakpoint then debug_start.",
        "thread_not_found"       => "Fix: call debug_threads to list available thread IDs.",
        "dirty_diagnostics"      => "Fix: call errors or warnings to get current diagnostics before retrying.",
        "unsupported_operation"  => "Fix: call tool_help with the tool name to check prerequisites or whether a different tool applies.",
        "timeout"                => "Fix: the operation timed out — try again, or reduce scope (e.g. search a subdirectory instead of the full solution).",
        _                        => null,
    };

    private static string CreateSuccessText(JsonObject response)
    {
        string bindingNoticePrefix = CreateBindingNoticePrefix(response);

        string? diagnosticsText = CreateDiagnosticsSuccessText(response);
        if (!string.IsNullOrWhiteSpace(diagnosticsText))
        {
            return bindingNoticePrefix + diagnosticsText;
        }

        string? summary = response["Summary"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            string? command = response["Command"]?.GetValue<string>();
            string summaryText = string.IsNullOrWhiteSpace(command)
                ? summary
                : $"{command}: {summary}";
            string? listText = CreateDataListText(response);
            return bindingNoticePrefix + (string.IsNullOrWhiteSpace(listText)
                ? summaryText
                : summaryText + "\n" + listText);
        }

        string? commandName = response["Command"]?.GetValue<string>();
        string fallbackText = string.IsNullOrWhiteSpace(commandName)
            ? "Command completed successfully."
            : $"{commandName}: completed successfully.";
        string? fallbackListText = CreateDataListText(response);
        return bindingNoticePrefix + (string.IsNullOrWhiteSpace(fallbackListText)
            ? fallbackText
            : fallbackText + "\n" + fallbackListText);
    }

    // Extracts and renders list data from bridge Data for commands that return item collections.
    // Skips diagnostics commands which have their own rendering.
    private static string? CreateDataListText(JsonObject response)
    {
        string? command = response["Command"]?.GetValue<string>();
        if (command is FormatterWarningsCommand or "errors" or "diagnostics-snapshot")
            return null;

        // Check Data object first (bridge-piped tools), then fall back to top-level fields
        // (service-side tools like glob, python_list_envs that write directly to the payload).
        JsonObject searchTarget = response["Data"] as JsonObject ?? response;

        foreach (string field in new[] { "files", "matches", "results", "rows", "items", "symbols", "references", "projects", "interpreters", "modules", "frames", "locals", "branches", "tags" })
        {
            if (searchTarget[field] is JsonArray arr && arr.Count > 0)
                return RenderDataList(arr, maxItems: 100);
        }

        return null;
    }

    private static string RenderDataList(JsonArray arr, int maxItems)
    {
        List<string> lines = [];
        int rendered = 0;
        foreach (JsonNode? item in arr)
        {
            if (rendered >= maxItems)
            {
                lines.Add($"... ({arr.Count - maxItems} more)");
                break;
            }

            string entry = RenderDataListEntry(item);
            if (!string.IsNullOrWhiteSpace(entry))
            {
                lines.Add(entry);
                rendered++;
            }
        }

        return string.Join("\n", lines);
    }

    private static string RenderDataListEntry(JsonNode? item)
    {
        if (item is JsonValue v)
            return v.GetValue<string?>() ?? string.Empty;

        if (item is not JsonObject obj)
            return item?.ToString() ?? string.Empty;

        string? file = obj["file"]?.GetValue<string>() ?? obj["path"]?.GetValue<string>();
        string? lineVal = obj["line"]?.ToString();
        string? text = obj["text"]?.GetValue<string>()
            ?? obj["name"]?.GetValue<string>()
            ?? obj["message"]?.GetValue<string>()
            ?? obj["displayName"]?.GetValue<string>();
        string? kind = obj["kind"]?.GetValue<string>();

        if (file != null && lineVal != null && text != null)
            return $"{file}:{lineVal}: {text}";
        if (file != null && lineVal != null)
            return $"{file}:{lineVal}";
        if (text != null && kind != null)
            return $"[{kind}] {text}";
        if (text != null)
            return text;
        if (file != null)
            return file;

        return obj.ToJsonString();
    }

    private static string CreateBindingNoticePrefix(JsonObject response)
    {
        string? bindingNotice = response["BindingNotice"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(bindingNotice))
        {
            return string.Empty;
        }

        return $"{bindingNotice} ";
    }

    private static string? CreateDiagnosticsSuccessText(JsonObject response)
    {
        string? command = response["Command"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command) || response["Data"] is not JsonObject data)
        {
            return null;
        }

        return command switch
        {
            FormatterWarningsCommand or "errors" => CreateWarningsOrErrorsSuccessText(command, response, data),
            "diagnostics-snapshot" => CreateDiagnosticsSnapshotSuccessText(response, data),
            _ => null,
        };
    }

    private static string? CreateWarningsOrErrorsSuccessText(string command, JsonObject response, JsonObject data)
    {
        string? summary = response["Summary"]?.GetValue<string>();
        int returnedCount = data["count"]?.GetValue<int>() ?? 0;
        int totalCount = data["totalCount"]?.GetValue<int>() ?? returnedCount;
        bool truncated = data["truncated"]?.GetValue<bool>() ?? false;
        JsonArray? rows = data["rows"] as JsonArray;
        JsonArray? groups = data["groups"] as JsonArray;

        string countText = totalCount == returnedCount
            ? $"rows={returnedCount}"
            : $"rows={returnedCount}, totalCount={totalCount}";

        string truncatedText = truncated ? ", truncated=true" : string.Empty;
        string groupsSuffix = RenderGroupSummary(groups) is { Length: > 0 } gs ? "\n" + gs : string.Empty;
        string rowsText = RenderDiagnosticRowList(rows, MaxDiagnosticTextRows);
        string rowsSuffix = string.IsNullOrWhiteSpace(rowsText) ? string.Empty : "\n" + rowsText;
        string continuationHint = CreateDiagnosticsContinuationHint(command, returnedCount, totalCount, truncated, rows?.Count ?? 0, MaxDiagnosticTextRows);
        string continuationSuffix = string.IsNullOrWhiteSpace(continuationHint) ? string.Empty : $"\n{continuationHint}";

        if (!string.IsNullOrWhiteSpace(summary))
        {
            return $"{command}: {summary} {countText}{truncatedText}.{groupsSuffix}{rowsSuffix}{continuationSuffix}";
        }

        return $"{command}: completed successfully. {countText}{truncatedText}.{groupsSuffix}{rowsSuffix}{continuationSuffix}";
    }

    private static string RenderDiagnosticRowList(JsonArray? rows, int maxRows)
    {
        if (rows is null || rows.Count == 0)
        {
            return string.Empty;
        }

        List<string> entries =
        [
            .. rows
                .OfType<JsonObject>()
                .Take(maxRows)
                .Select(static row => RenderDiagnosticRowEntry(row))
                .Where(static entry => !string.IsNullOrWhiteSpace(entry)),
        ];

        if (rows.Count > maxRows)
        {
            entries.Add($"... ({rows.Count - maxRows} more row(s) in this response; use max, code, path, or project to narrow it.)");
        }

        return string.Join("\n", entries);
    }

    private static string RenderGroupSummary(JsonArray? groups)
    {
        if (groups is null || groups.Count == 0)
        {
            return string.Empty;
        }

        string? groupBy = groups
            .OfType<JsonObject>()
            .FirstOrDefault()?["groupBy"]?.GetValue<string>();

        string entries = string.Join("  ", groups
            .OfType<JsonObject>()
            .Select(static g =>
            {
                string key = g["key"]?.GetValue<string>() ?? "?";
                int count = g["count"]?.GetValue<int>() ?? 0;
                return $"{key}\u00d7{count}";
            }));

        string label = string.IsNullOrWhiteSpace(groupBy)
            ? $"Groups ({groups.Count})"
            : $"Groups by {groupBy} ({groups.Count})";

        return $"{label}: {entries}";
    }

    private static string RenderDiagnosticRowEntry(JsonObject row)
    {
        string? code = row["code"]?.GetValue<string>();
        string? message = row["message"]?.GetValue<string>();
        string? file = row["file"]?.GetValue<string>();
        int? line = row["line"]?.GetValue<int?>();

        string location = string.IsNullOrWhiteSpace(file)
            ? string.Empty
            : $"{System.IO.Path.GetFileName(file)}{(line is int lineNumber ? $":{lineNumber}" : string.Empty)}";

        string codeStr = string.IsNullOrWhiteSpace(code) ? string.Empty : code!;

        if (!string.IsNullOrWhiteSpace(location) && !string.IsNullOrWhiteSpace(message))
            return $"{location}: {(string.IsNullOrWhiteSpace(codeStr) ? string.Empty : codeStr + " ")}{message}";
        if (!string.IsNullOrWhiteSpace(message))
            return $"{(string.IsNullOrWhiteSpace(codeStr) ? string.Empty : codeStr + " ")}{message}";
        if (!string.IsNullOrWhiteSpace(location))
            return string.IsNullOrWhiteSpace(codeStr) ? location : $"{location}: {codeStr}";
        return string.Empty;
    }

    private static string? CreateDiagnosticsSnapshotSuccessText(JsonObject response, JsonObject data)
    {
        string? summary = response["Summary"]?.GetValue<string>();
        string prefix = string.IsNullOrWhiteSpace(summary)
            ? "diagnostics-snapshot: completed successfully."
            : $"diagnostics-snapshot: {summary}";

        JsonObject? warnings = data["warnings"] as JsonObject;
        JsonObject? errors = data["errors"] as JsonObject;

        int warningRows = warnings?["count"]?.GetValue<int>() ?? 0;
        int warningTotal = warnings?["totalCount"]?.GetValue<int>() ?? warningRows;
        bool warningTruncated = warnings?["truncated"]?.GetValue<bool>() ?? false;
        int errorRows = errors?["count"]?.GetValue<int>() ?? 0;
        int errorTotal = errors?["totalCount"]?.GetValue<int>() ?? errorRows;
        bool errorTruncated = errors?["truncated"]?.GetValue<bool>() ?? false;
        string warningPreview = CreateDiagnosticRowPreview(warnings?["rows"] as JsonArray, "warnings", MaxDiagnosticPreviewRows);
        string errorPreview = CreateDiagnosticRowPreview(errors?["rows"] as JsonArray, "errors", MaxDiagnosticPreviewRows);

        string warningText = warningTotal == warningRows
            ? $"warnings.rows={warningRows}"
            : $"warnings.rows={warningRows}, warnings.totalCount={warningTotal}";
        string errorText = errorTotal == errorRows
            ? $"errors.rows={errorRows}"
            : $"errors.rows={errorRows}, errors.totalCount={errorTotal}";

        string truncationText = string.Empty;
        if (warningTruncated || errorTruncated)
        {
            List<string> flags = [];
            if (warningTruncated)
            {
                flags.Add("warnings.truncated=true");
            }

            if (errorTruncated)
            {
                flags.Add("errors.truncated=true");
            }

            truncationText = $", {string.Join(", ", flags)}";
        }

        string previewText = string.Join(" ", new[] { warningPreview, errorPreview }.Where(static text => !string.IsNullOrWhiteSpace(text)));
        string previewSuffix = string.IsNullOrWhiteSpace(previewText)
            ? string.Empty
            : $" {previewText}";
        string continuationHint = "Use errors, warnings, or messages with max, code, path, or project filters for more rows, or full=true for the full payload.";

        return $"{prefix} See warnings.rows and errors.rows for details; {warningText}; {errorText}{truncationText}.{previewSuffix} {continuationHint}";
    }

    private static string CreateDiagnosticRowPreview(JsonArray? rows, string? label = null, int maxRows = 3)
    {
        if (rows is null || rows.Count == 0)
        {
            return string.Empty;
        }

        IEnumerable<string> previews = rows
            .OfType<JsonObject>()
            .Take(maxRows)
            .Select(static row => CreateDiagnosticRowPreviewEntry(row))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry));

        string preview = string.Join("; ", previews);
        if (string.IsNullOrWhiteSpace(preview))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(label)
            ? preview
            : $"Top {label}: {preview}.";
    }

    private static string CreateDiagnosticsContinuationHint(
        string command,
        int returnedCount,
        int totalCount,
        bool truncated,
        int previewedRowCount,
        int maxPreviewRows)
    {
        bool previewTrimmed = previewedRowCount > maxPreviewRows;
        bool moreRowsAvailable = truncated || totalCount > returnedCount || previewTrimmed;

        if (!moreRowsAvailable)
        {
            return "Use max, code, path, or project filters to narrow the list, or full=true for the full payload.";
        }

        return $"More {command} rows are available. Use max to request a larger page, add code/path/project filters to narrow the list, or use full=true for the full payload.";
    }

    private static string CreateDiagnosticRowPreviewEntry(JsonObject row)
    {
        string? code = row["code"]?.GetValue<string>();
        string? file = row["file"]?.GetValue<string>();
        int? line = row["line"]?.GetValue<int?>();

        string location = string.IsNullOrWhiteSpace(file)
            ? string.Empty
            : $"{System.IO.Path.GetFileName(file)}{(line is int lineNumber ? $":{lineNumber}" : string.Empty)}";

        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(location))
        {
            return $"{code} at {location}";
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        return location;
    }
}
