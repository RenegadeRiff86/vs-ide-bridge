using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeCli;

// Builds the fallback CLI McpToolRegistry.
// The Windows service is the primary MCP host; the CLI exists only as backup hosting.
// ListTools() and CallToolAsync() both derive from this registry.
internal static partial class CliApp
{
    private static partial class McpServer
    {
        private static readonly McpToolRegistry Registry = BuildRegistry();

        // Wrap a bridge pipe response as an MCP tools/call result node.
        private static JsonNode BridgeCallResult(JsonObject response) =>
            new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = response.ToJsonString(),
                    },
                },
                ["isError"] = !ResponseFormatter.IsSuccess(response),
                [StructuredContentPropertyName] = response.DeepClone(),
            };

        private static JsonNode ToolResult(JsonNode response, bool isError = false) =>
            new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = response.ToJsonString(),
                    },
                },
                ["isError"] = isError,
                [StructuredContentPropertyName] = response.DeepClone(),
            };

        // Shorthand for the common case: send one bridge pipe command and wrap the result.
        // buildArgs receives (id, args) so validation errors can throw McpRequestException.
        private static McpToolEntry BridgeTool(
            ToolDefinition definition,
            string pipeCommand,
            Func<JsonNode?, JsonObject?, string> buildArgs)
            => new(definition,
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(
                        id, binding, pipeCommand, buildArgs(id, args)).ConfigureAwait(false);
                    return BridgeCallResult(r);
                });

        private static McpToolEntry BridgeTool(
            ToolDefinition definition,
            string pipeCommand,
            Func<JsonObject?, string> buildArgs)
            => BridgeTool(definition, pipeCommand, (_, a) => buildArgs(a));

        private static McpToolEntry BridgeTool(
            string name,
            string description,
            JsonObject schema,
            string pipeCommand,
            Func<JsonNode?, JsonObject?, string> buildArgs,
            string category = "core",
            string? title = null,
            JsonObject? annotations = null,
            IEnumerable<string>? aliases = null,
            IEnumerable<string>? tags = null,
            string? summary = null,
            bool? readOnly = null,
            bool? mutating = null,
            bool? destructive = null)
            => new(name, description, schema, category,
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(
                        id, binding, pipeCommand, buildArgs(id, args)).ConfigureAwait(false);
                    return BridgeCallResult(r);
                }, title, annotations, null, aliases, tags, pipeCommand, summary, readOnly, mutating, destructive);

        // Overload when buildArgs does not need id (most tools).
        private static McpToolEntry BridgeTool(
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
            bool? destructive = null)
            => BridgeTool(name, description, schema, pipeCommand,
                (_, a) => buildArgs(a), category, title, annotations, aliases, tags, summary, readOnly, mutating, destructive);

        private static McpToolRegistry BuildRegistry() => new(
        [
            // ── core ─────────────────────────────────────────────────────────────────

            new("bridge_health",
                "Get binding health, discovery source, and last round-trip metrics.",
                EmptySchema(), "core",
                (id, _, binding) => BridgeHealthAsync(id, binding)),

            new("list_instances",
                "List live VS IDE Bridge instances visible to this MCP server.",
                EmptySchema(), "core",
                (_, _, binding) => ListInstancesAsync(binding)),

            new("bind_instance",
                "Bind this MCP session to one specific Visual Studio bridge instance by exact " +
                "instance id, process id, or pipe name. Advanced recovery tool: use this only " +
                "when normal solution-based selection is ambiguous or incorrect.",
                ObjectSchema(
                    OptionalStringProperty("instance_id", "Optional exact bridge instance id."),
                    OptionalIntegerProperty("pid", "Optional Visual Studio process id."),
                    OptionalStringProperty("pipe_name", "Optional exact bridge pipe name."),
                    OptionalStringProperty("solution_hint", "Optional solution path or name substring.")),
                "core",
                async (id, args, binding) =>
                    (JsonNode)ToolResult(await binding.BindAsync(id, args).ConfigureAwait(false))),

            new("bind_solution",
                "Bind this MCP session to an already-open Visual Studio bridge instance whose " +
                "solution matches a name or path hint. Use this when the solution is already " +
                "open in Visual Studio; do not use it to open a solution file from disk.",
                ObjectSchema(RequiredStringProperty(SolutionArgumentName,
                    "Solution name or path substring to match.")),
                "core",
                async (id, args, binding) =>
                {
                    JsonObject bindArgs = new JsonObject
                    {
                        ["solution_hint"] = args?[SolutionArgumentName]?.DeepClone()
                            ?? args?["solution_hint"]?.DeepClone(),
                    };
                    return (JsonNode)ToolResult(
                        await binding.BindAsync(id, bindArgs).ConfigureAwait(false));
                }),

            BridgeTool(BridgeStateToolName,
                "Capture current Visual Studio bridge state: VS version, solution path, " +
                "build mode, active document, and debug mode.",
                EmptySchema(), "state", static (_, _) => string.Empty, "core"),

            BridgeTool(WaitForReadyArgumentName,
                "Block until Visual Studio and IntelliSense are fully loaded. Call this after " +
                "open_solution or vs_open before running any semantic tools.",
                EmptySchema(), "ready", static (_, _) => string.Empty, "core"),

            BridgeTool(
                ToolDefinitionCatalog.ReadFile(
                    ObjectSchema(
                        RequiredStringProperty(FileArgumentName,
                            AbsoluteOrSolutionRelativeFilePathDescription),
                        OptionalIntegerProperty(StartLineArgumentName,
                            "First 1-based line to read. Use with end_line for a range."),
                        OptionalIntegerProperty(EndLineArgumentName,
                            "Last 1-based line to read (inclusive). Use with start_line."),
                        OptionalIntegerProperty(LineArgumentName,
                            "Anchor 1-based line. Use with context_before/context_after."),
                        OptionalIntegerProperty(ContextBeforeArgumentName,
                            "Lines before anchor (default 10)."),
                        OptionalIntegerProperty(ContextAfterArgumentName,
                            "Lines after anchor (default 30)."),
                        OptionalBooleanProperty("reveal_in_editor",
                            "Whether to reveal the slice in the editor (default true)."))),
                "document-slice", static (_, a) => BuildReadFileArgs(a)),

            BridgeTool(
                ToolDefinitionCatalog.ReadFileBatch(
                    ObjectSchema(
                        ("ranges", ArraySchema(
                            "Ranges to read in order.",
                            ObjectSchema(
                                RequiredStringProperty(FileArgumentName,
                                    AbsoluteOrSolutionRelativeFilePathDescription),
                                OptionalIntegerProperty(StartLineArgumentName,
                                    "First 1-based line to read. Use with end_line for a range."),
                                OptionalIntegerProperty(EndLineArgumentName,
                                    "Last 1-based line to read (inclusive). Use with start_line."),
                                OptionalIntegerProperty(LineArgumentName,
                                    "Anchor 1-based line. Use with context_before/context_after."),
                                OptionalIntegerProperty(ContextBeforeArgumentName,
                                    "Lines before anchor when line is used."),
                                OptionalIntegerProperty(ContextAfterArgumentName,
                                    "Lines after anchor when line is used.")),
                            minItems: 1),
                        true))),
                "document-slices", static (_, a) => BuildReadFileBatchArgs(a)),

            new(
                ToolDefinitionCatalog.ApplyDiff(
                    ObjectSchema(
                        RequiredStringProperty("patch",
                            "Patch text. Preferred: editor patch format " +
                            "(*** Begin Patch\\n*** Update File: path\\n@@\\n context\\n-old\\n+new\\n" +
                            "*** End Patch). Also accepted: unified diff with @@ headers."),
                        OptionalBooleanProperty("post_check",
                            "Run wait_for_ready and errors after applying diff (default true)."))),
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(
                        id, binding, "apply-diff", BuildApplyDiffArgs(args)).ConfigureAwait(false);
                    if (GetBoolean(args, "post_check", true))
                    {
                        JsonObject ready = await SendBridgeAsync(
                            id, binding, "ready", string.Empty).ConfigureAwait(false);
                        JsonObject errors = await SendBridgeAsync(
                            id, binding, "errors",
                            "--wait-for-intellisense true").ConfigureAwait(false);
                        r["postCheck"] = new JsonObject
                            { ["ready"] = ready, ["errors"] = errors };
                    }

                    return BridgeCallResult(r);
                }),

            new(
                ToolDefinitionCatalog.WriteFile(
                    ObjectSchema(
                        RequiredStringProperty(FileArgumentName,
                            AbsoluteOrSolutionRelativeFilePathDescription),
                        RequiredStringProperty("content",
                            "Full UTF-8 text content to write to the file."),
                        OptionalBooleanProperty("post_check",
                            "Run wait_for_ready and errors after writing (default true)."))),
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(
                        id, binding, "write-file", BuildWriteFileArgs(args)).ConfigureAwait(false);
                    if (GetBoolean(args, "post_check", true))
                    {
                        JsonObject ready = await SendBridgeAsync(
                            id, binding, "ready", string.Empty).ConfigureAwait(false);
                        JsonObject errors = await SendBridgeAsync(
                            id, binding, "errors",
                            "--wait-for-intellisense true").ConfigureAwait(false);
                        r["postCheck"] = new JsonObject
                            { ["ready"] = ready, ["errors"] = errors };
                    }

                    return BridgeCallResult(r);
                }),

            BridgeTool(
                ToolDefinitionCatalog.FindText(
                    ObjectSchema(
                        RequiredStringProperty(QueryArgumentName, "Search text or regex pattern."),
                        OptionalStringProperty(PathArgumentName,
                            "Optional path or directory filter (solution-relative or absolute)."),
                        OptionalStringProperty("scope",
                            "Scope: solution (default), project, or document."),
                        OptionalStringProperty(ProjectArgumentName, OptionalProjectFilterDescription),
                        OptionalIntegerProperty("results_window",
                            "Optional Find Results window number."),
                        OptionalBooleanProperty(MatchCaseArgumentName,
                            "Case-sensitive match (default false)."),
                        OptionalBooleanProperty("whole_word",
                            "Match whole word only (default false)."),
                        OptionalBooleanProperty("regex",
                            "Treat query as a regular expression (default false)."))),
                "find-text", static (_, a) => BuildFindTextArgs(a)),

            BridgeTool(
                ToolDefinitionCatalog.FileOutline(
                    ObjectSchema(RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription))),
                "file-outline",
                static (_, a) => BuildSingleStringSwitchArg(a, FileArgumentName, FileArgumentName)),

            BridgeTool(
                ToolDefinitionCatalog.Errors(
                    ObjectSchema(
                        OptionalStringProperty(SeverityArgumentName, "Optional severity filter."),
                        OptionalBooleanProperty(WaitForIntellisenseArgumentName,
                            "Wait for IntelliSense readiness first (default true)."),
                        OptionalBooleanProperty("quick",
                            "Read current snapshot immediately without stability polling (default false)."),
                        OptionalIntegerProperty("max", "Optional max rows."),
                        OptionalStringProperty("code", "Optional diagnostic code prefix filter."),
                        OptionalStringProperty("project", OptionalProjectFilterDescription),
                        OptionalStringProperty("path", "Optional path filter."),
                        OptionalStringProperty("text", "Optional message text filter."),
                        OptionalStringProperty("group_by", "Optional grouping mode."),
                        OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                            "Optional wait timeout in milliseconds."))),
                "errors", static (_, a) => BuildDiagnosticsArgs(a)),

            new("build",
                "Trigger a solution build with optional configuration/platform and return full " +
                "errors/warnings. Refuses to build if any diagnostics already exist. " +
                "Use build_errors for a lightweight errors-only variant.",
                ObjectSchema(
                    OptionalStringProperty(ConfigurationArgumentName,
                        "Optional build configuration (e.g. Debug, Release)."),
                    OptionalStringProperty(PlatformArgumentName,
                        "Optional build platform (e.g. x64)."),
                    OptionalBooleanProperty(WaitForIntellisenseArgumentName,
                        "Wait for IntelliSense readiness before checking for blocking " +
                        "diagnostics (default true).")),
                "core",
                async (id, args, binding) =>
                {
                    // Capture pre-existing diagnostics so the LLM knows what was already broken.
                    JsonNode? preBuildDiagnostics = null;
                    try
                    {
                        JsonObject preErrors = await SendBridgeAsync(
                            id, binding, "errors", "--quick").ConfigureAwait(false);
                        if (ResponseFormatter.IsSuccess(preErrors))
                        {
                            int ec = preErrors["Data"]?["errorCount"]?.GetValue<int>() ?? 0;
                            int wc = preErrors["Data"]?["warningCount"]?.GetValue<int>() ?? 0;
                            int mc = preErrors["Data"]?["messageCount"]?.GetValue<int>() ?? 0;
                            if (ec > 0 || wc > 0 || mc > 0)
                                preBuildDiagnostics = preErrors;
                        }
                    }
                    catch
                    {
                        // Non-fatal: proceed even if pre-check fails.
                    }

                    JsonObject r = await SendBridgeAsync(
                        id, binding, "build", BuildBuildArgs(args)).ConfigureAwait(false);
                    if (preBuildDiagnostics is not null)
                        r["preBuildDiagnostics"] = preBuildDiagnostics;
                    return BridgeCallResult(r);
                }),

            // ── search ───────────────────────────────────────────────────────────────

            BridgeTool(
                ToolDefinitionCatalog.FindFiles(
                ObjectSchema(
                    RequiredStringProperty("query", "File name or path fragment."),
                    OptionalStringProperty("path", "Optional path fragment filter."),
                    OptionalStringArrayProperty("extensions",
                        "Optional extension filters like ['.cmake','.txt']."),
                    OptionalIntegerProperty("max_results",
                        $"Optional max result count (default {DefaultLargeMaxCount})."),
                    OptionalBooleanProperty("include_non_project",
                        "Include disk files under solution root that are not in projects " +
                        "(default true)."))),
                "find-files", static (_, a) => BuildFindFilesArgs(a)),

            BridgeTool(
                ToolDefinitionCatalog.FindTextBatch(
                    ObjectSchema(
                        RequiredStringArrayProperty("queries", "Queries to search for in order."),
                        OptionalStringProperty("scope",
                            "Optional scope: solution, project, document, or open."),
                        OptionalStringProperty("project", OptionalProjectFilterDescription),
                        OptionalStringProperty("path", "Optional path or directory filter."),
                        OptionalIntegerProperty("results_window",
                            "Optional Find Results window number."),
                        OptionalIntegerProperty("max_queries_per_chunk",
                            $"Optional max query count per internal chunk " +
                            $"(default {DefaultMaxQueriesPerChunk})."),
                        OptionalBooleanProperty(MatchCaseArgumentName,
                            "Case-sensitive match (default false)."),
                        OptionalBooleanProperty("whole_word",
                            "Match whole word only (default false)."),
                        OptionalBooleanProperty("regex",
                            "Treat queries as regular expressions (default false)."))),
                "find-text-batch", static (_, a) => BuildFindTextBatchArgs(a)),

            BridgeTool(
                ToolDefinitionCatalog.SearchSymbols(
                    ObjectSchema(
                        RequiredStringProperty("query", "Symbol search text."),
                        OptionalStringProperty("kind", "Optional symbol kind filter."),
                        OptionalStringProperty("scope",
                            "Optional scope: solution, project, document, or open."),
                        OptionalStringProperty("project", OptionalProjectFilterDescription),
                        OptionalStringProperty("path", "Optional path or directory filter."),
                        OptionalIntegerProperty("max", "Optional max result count."),
                        OptionalBooleanProperty(MatchCaseArgumentName,
                            "Case-sensitive match (default false)."))),
                "search-symbols", static (_, a) => BuildSearchSymbolsArgs(a)),

            BridgeTool(
                ToolDefinitionCatalog.FindReferences(FileLineColumnSchema()),
                "find-references", static (_, a) => BuildFileLineColumnArgs(a)),

            BridgeTool(
                CountReferencesToolName,
                "Count how many references exist for the symbol at file/line/column. Faster " +
                "than find_references when you only need the count.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription),
                    RequiredIntegerProperty("line", OneBasedLineNumberDescription),
                    RequiredIntegerProperty("column", "1-based column number."),
                    OptionalBooleanProperty(ActivateWindowArgumentName,
                        "Activate references window while counting (default true)."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        "Optional window wait timeout in milliseconds.")),
                "count-references", static (_, a) => BuildCountReferencesArgs(a), "search"),

            BridgeTool(
                GotoDefinitionToolName,
                "Navigate the editor cursor to the definition of the symbol at file/line/column. " +
                "Use peek_definition or symbol_info to read the definition without navigating.",
                FileLineColumnSchema(),
                "goto-definition", static (_, a) => BuildFileLineColumnArgs(a), "search"),

            BridgeTool(
                GotoImplementationToolName,
                "Navigate the editor cursor to one implementation of the interface/abstract " +
                "member at file/line/column. Use find_references for all implementations.",
                FileLineColumnSchema(),
                "goto-implementation", static (_, a) => BuildFileLineColumnArgs(a), "search"),

            BridgeTool(
                ToolDefinitionCatalog.PeekDefinition(FileLineColumnSchema()),
                "peek-definition", static (_, a) => BuildFileLineColumnArgs(a)),

            BridgeTool(
                CallHierarchyToolName,
                "Open the Call Hierarchy view for the symbol at file/line/column, showing " +
                "callers and callees.",
                FileLineColumnSchema(),
                "call-hierarchy", static (_, a) => BuildFileLineColumnArgs(a), "search"),

            BridgeTool(
                ToolDefinitionCatalog.SymbolInfo(
                    ObjectSchema(
                        RequiredStringProperty(FileArgumentName,
                            AbsoluteOrSolutionRelativeFilePathDescription),
                        RequiredIntegerProperty("line", OneBasedLineNumberDescription),
                        RequiredIntegerProperty("column", "1-based column number."))),
                "quick-info", static (_, a) => BuildFileLineColumnArgs(a)),

            // ── diagnostics ──────────────────────────────────────────────────────────

            BridgeTool(
                "build_errors",
                "Trigger a build and return only the errors list after completion. Use build " +
                "when you need the full build result; use build_errors when you only care about " +
                "the error list. Refuses to build if any diagnostics already exist.",
                ObjectSchema(
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        "Optional build timeout in milliseconds."),
                    OptionalIntegerProperty(MaxArgumentName, "Optional max error rows."),
                    OptionalBooleanProperty(WaitForIntellisenseArgumentName,
                        "Wait for IntelliSense readiness before checking for blocking " +
                        "diagnostics (default true).")),
                "build-errors", static (id, a) => BuildBuildErrorsArgs(id, a), "diagnostics"),

            BridgeTool(
                "build_configurations",
                "List solution build configurations/platforms.",
                EmptySchema(),
                "build-configurations", static (_, _) => string.Empty, "diagnostics"),

            BridgeTool(
                SetBuildConfigurationToolName,
                "Activate one build configuration/platform pair.",
                ObjectSchema(
                    RequiredStringProperty(ConfigurationArgumentName,
                        "Build configuration name (e.g. Debug)."),
                    OptionalStringProperty(PlatformArgumentName,
                        "Optional platform (e.g. x64).")),
                "set-build-configuration",
                static (_, a) => BuildConfigurationPlatformArgs(a), "diagnostics"),

            BridgeTool(
                WarningsToolName,
                "Read current Warning List rows. Same as errors but pre-filtered to warnings. " +
                "Use errors with severity filter for mixed results.",
                ObjectSchema(
                    OptionalStringProperty(SeverityArgumentName, "Optional severity filter."),
                    OptionalBooleanProperty(WaitForIntellisenseArgumentName,
                        "Wait for IntelliSense readiness first (default true)."),
                    OptionalBooleanProperty("quick",
                        "Read current snapshot immediately without stability polling " +
                        "(default false)."),
                    OptionalIntegerProperty("max", "Optional max rows."),
                    OptionalStringProperty("code", "Optional diagnostic code prefix filter."),
                    OptionalStringProperty("project", OptionalProjectFilterDescription),
                    OptionalStringProperty("path", "Optional path filter."),
                    OptionalStringProperty("text", "Optional message text filter."),
                    OptionalStringProperty("group_by", "Optional grouping mode."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        "Optional wait timeout in milliseconds.")),
                WarningsToolName, static (_, a) => BuildDiagnosticsArgs(a),
                "diagnostics", "Warning List Diagnostics", ReadOnlyToolAnnotations()),

            BridgeTool(
                DiagnosticsSnapshotToolName,
                "Capture a comprehensive IDE snapshot: build state, active document, errors, " +
                "warnings, and debug status — all in one call. Use this for a quick health " +
                "check instead of calling errors + bridge_state separately.",
                ObjectSchema(
                    OptionalBooleanProperty(WaitForIntellisenseArgumentName,
                        "Wait for IntelliSense before diagnostics (default true)."),
                    OptionalBooleanProperty(QuickArgumentName,
                        "Use quick diagnostics snapshot mode (default false)."),
                    OptionalIntegerProperty(MaxArgumentName,
                        "Optional max diagnostics rows for errors/warnings.")),
                "diagnostics-snapshot",
                static (_, a) => BuildDiagnosticsSnapshotToolArgs(a), "diagnostics"),

            // ── documents ────────────────────────────────────────────────────────────

            BridgeTool(
                OpenFileToolName,
                "Open an absolute path, solution-relative path, or solution item name and " +
                "optional line/column. Each call opens a new editor tab — close it with " +
                "close_document when done to keep the tab bar tidy.",
                ObjectSchema(
                    RequiredStringProperty("file",
                        "Absolute path, solution-relative path, or solution item name."),
                    OptionalIntegerProperty("line", "Optional 1-based line number."),
                    OptionalIntegerProperty("column", "Optional 1-based column number."),
                    OptionalBooleanProperty("allow_disk_fallback",
                        "Allow disk fallback under solution root when solution items do not " +
                        "match (default true).")),
                "open-document", static (_, a) => BuildOpenFileArgs(a), "documents"),

            BridgeTool(
                ListDocumentsToolName,
                "List all documents currently loaded in the IDE document table (may include " +
                "background-loaded files). Use list_tabs to list only visible editor tabs.",
                EmptySchema(),
                "list-documents", static (_, _) => string.Empty, "documents"),

            BridgeTool(
                ListTabsToolName,
                "List visible editor tabs. The tab bar scrolls when more than ~7 tabs are open. " +
                "If count exceeds 7, close tabs you are done with using close_document or " +
                "close_others. Use list_documents for all loaded documents.",
                EmptySchema(),
                "list-tabs", static (_, _) => string.Empty, "documents"),

            BridgeTool(
                "activate_document",
                "Activate one open document by path or name.",
                ObjectSchema(RequiredStringProperty("query", "Document path or name fragment.")),
                "activate-document",
                static (_, a) => BuildSingleStringSwitchArg(a, QueryArgumentName, QueryArgumentName),
                "documents"),

            BridgeTool(
                "close_document",
                "Close one matching document, or all open documents. " +
                "Use { all: true, save: true } to save and close all tabs before a build. " +
                "Prefer this over close_file when closing multiple documents.",
                ObjectSchema(
                    OptionalStringProperty("query", "Document path or name fragment."),
                    OptionalBooleanProperty("all", "Close all open documents (default false)."),
                    OptionalBooleanProperty("save", "Save before closing (default false).")),
                "close-document", static (_, a) => BuildCloseDocumentArgs(a), "documents"),

            BridgeTool(
                SaveDocumentToolName,
                "Save one document or all open documents.",
                ObjectSchema(
                    OptionalStringProperty("file",
                        "File path to save, or omit for active document."),
                    OptionalBooleanProperty("all", "Save all open documents (default false).")),
                "save-document", static (_, a) => BuildSaveDocumentArgs(a), "documents"),

            BridgeTool(
                "reload_document",
                "Reload a document from disk inside Visual Studio, forcing IntelliSense to " +
                "re-analyse it. Use this after an external tool modifies a file outside the " +
                "editor, or to clear stale diagnostics.",
                ObjectSchema(RequiredStringProperty("file",
                    "Absolute or solution-relative file path to reload.")),
                "reload-document",
                static (_, a) => BuildArgs(
                    (FileArgumentName, GetOptionalStringArgument(a, FileArgumentName))),
                "documents"),

            BridgeTool(
                "close_file",
                "Close one open file tab by path or name. Prefer close_document which supports " +
                "closing all documents at once.",
                ObjectSchema(
                    OptionalStringProperty("file", "File path."),
                    OptionalStringProperty("query", "Name fragment."),
                    OptionalBooleanProperty("save", "Save before closing (default false).")),
                "close-file", static (_, a) => BuildCloseFileArgs(a), "documents"),

            BridgeTool(
                "close_others",
                "Close all editor tabs except the currently active one — use this to quickly " +
                "tidy the tab bar when too many tabs are open.",
                ObjectSchema(OptionalBooleanProperty("save",
                    "Save before closing (default false).")),
                "close-others", static (_, a) => BuildSaveOnlyArgs(a), "documents"),

            BridgeTool(
                ListWindowsToolName,
                "List open Visual Studio tool windows and document windows " +
                "(e.g. Solution Explorer, Error List, Output). Not OS windows.",
                ObjectSchema(OptionalStringProperty("query", "Optional caption filter.")),
                "list-windows",
                static (_, a) => BuildSingleStringSwitchArg(a, QueryArgumentName, QueryArgumentName),
                "documents"),

            BridgeTool(
                ActivateWindowArgumentName,
                "Bring a Visual Studio tool window or document window to the foreground by " +
                "caption fragment (e.g. 'Solution Explorer', 'Output').",
                ObjectSchema(RequiredStringProperty("window", "Window caption fragment.")),
                "activate-window",
                static (_, a) => BuildSingleStringSwitchArg(a, "window", "window"),
                "documents"),

            BridgeTool(
                "format_document",
                "Format the current document, or a specific file after opening/positioning it.",
                ObjectSchema(
                    OptionalStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription),
                    OptionalIntegerProperty(LineArgumentName, "Optional 1-based line number."),
                    OptionalIntegerProperty(ColumnArgumentName,
                        "Optional 1-based column number.")),
                "execute-command", static (_, a) => BuildFormatDocumentArgs(a), "documents"),

            new(OpenSolutionToolName,
                "Open a specific existing .sln or .slnx file in the current Visual Studio " +
                "instance without opening a new window. Do not call bind_solution afterward " +
                "unless you need to switch to a different already-open instance.",
                ObjectSchema(
                    RequiredStringProperty(SolutionArgumentName,
                        "Absolute path to the .sln or .slnx file to open."),
                    OptionalBooleanProperty(WaitForReadyArgumentName,
                        "Wait for readiness after opening the solution (default true).")),
                "documents",
                (id, args, binding) => OpenSolutionAsync(id, args, binding)),

            new(CreateSolutionToolName,
                "Create and open a new solution in the current Visual Studio instance.",
                ObjectSchema(
                    RequiredStringProperty(DirectoryArgumentName,
                        "Absolute directory where the new solution should be created."),
                    RequiredStringProperty(NameArgumentName,
                        "Solution name. '.sln' is optional."),
                    OptionalBooleanProperty(WaitForReadyArgumentName,
                        "Wait for readiness after opening the solution (default true).")),
                "documents",
                (id, args, binding) => CreateSolutionAsync(id, args, binding)),

            BridgeTool(
                SearchSolutionsToolName,
                "Search for solution files (.sln/.slnx) on disk under a given root directory. " +
                "Defaults to %USERPROFILE%\\source\\repos.",
                ObjectSchema(
                    OptionalStringProperty(PathArgumentName,
                        "Root directory to search (default: %USERPROFILE%\\source\\repos)."),
                    OptionalStringProperty(QueryArgumentName,
                        "Filter by solution name (case-insensitive substring)."),
                    OptionalIntegerProperty("max_depth",
                        "Max directory depth to recurse (default 6)."),
                    OptionalIntegerProperty("max",
                        $"Max results to return (default {DefaultLargeMaxCount}).")),
                "search-solutions",
                static (_, a) => BuildSearchSolutionsToolArgs(a), "documents"),

            // ── debug ────────────────────────────────────────────────────────────────

            BridgeTool(
                "set_breakpoint",
                "Set a breakpoint at file/line with optional condition, hit count, tracepoint " +
                "message, and reveal in editor.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription),
                    RequiredIntegerProperty(LineArgumentName, OneBasedLineNumberDescription),
                    OptionalIntegerProperty(ColumnArgumentName,
                        "1-based column number (default 1)."),
                    OptionalStringProperty("condition", "Breakpoint condition expression."),
                    OptionalStringProperty("condition_type",
                        "Condition type: 'when-true' (default) or 'changed'."),
                    OptionalIntegerProperty("hit_count", "Hit count value (default 0 = ignore)."),
                    OptionalStringProperty("hit_type",
                        "Hit count type: 'none' (default), 'equal', 'multiple', " +
                        "'greater-or-equal'."),
                    OptionalStringProperty("trace_message",
                        "Optional tracepoint message to log when the breakpoint is hit."),
                    OptionalBooleanProperty("continue_execution",
                        "If true, do not break when the breakpoint is hit."),
                    OptionalBooleanProperty("reveal",
                        "Reveal breakpoint location in editor (default true).")),
                "set-breakpoint",
                static (_, a) => BuildArgs(
                [
                    (FileArgumentName, GetOptionalStringArgument(a, FileArgumentName)),
                    (LineArgumentName, GetOptionalArgumentText(a, LineArgumentName)),
                    (ColumnArgumentName, GetOptionalArgumentText(a, ColumnArgumentName)),
                    ("condition", GetOptionalStringArgument(a, "condition")),
                    ("condition-type", GetOptionalStringArgument(a, "condition_type")),
                    ("hit-count", GetOptionalArgumentText(a, "hit_count")),
                    ("hit-type", GetOptionalStringArgument(a, "hit_type")),
                    ("trace-message", GetOptionalStringArgument(a, "trace_message")),
                    .. BuildBooleanArgs(a, ("continue-execution", "continue_execution", false, true)),
                    .. BuildBooleanArgs(a, ("reveal", "reveal", true, true)),
                ]),
                "debug"),

            BridgeTool(
                ListBreakpointsToolName,
                "List all breakpoints in the current debug session.",
                EmptySchema(),
                "list-breakpoints", static (_, _) => string.Empty, "debug"),

            BridgeTool(
                "remove_breakpoint",
                "Remove a breakpoint by file and line number.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription),
                    RequiredIntegerProperty(LineArgumentName, OneBasedLineNumberDescription)),
                "remove-breakpoint",
                static (_, a) => BuildArgs(
                    (FileArgumentName, GetOptionalStringArgument(a, FileArgumentName)),
                    (LineArgumentName, GetOptionalArgumentText(a, LineArgumentName))),
                "debug"),

            BridgeTool(
                ClearBreakpointsToolName,
                "Remove all breakpoints.",
                EmptySchema(),
                "clear-breakpoints", static (_, _) => string.Empty, "debug"),

            BridgeTool(
                "enable_breakpoint",
                "Enable a disabled breakpoint at file/line.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription),
                    RequiredIntegerProperty(LineArgumentName, OneBasedLineNumberDescription)),
                "enable-breakpoint",
                static (_, a) => BuildArgs(
                    (FileArgumentName, GetOptionalStringArgument(a, FileArgumentName)),
                    (LineArgumentName, GetOptionalArgumentText(a, LineArgumentName))),
                "debug"),

            BridgeTool(
                "disable_breakpoint",
                "Disable a breakpoint at file/line without removing it.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription),
                    RequiredIntegerProperty(LineArgumentName, OneBasedLineNumberDescription)),
                "disable-breakpoint",
                static (_, a) => BuildArgs(
                    (FileArgumentName, GetOptionalStringArgument(a, FileArgumentName)),
                    (LineArgumentName, GetOptionalArgumentText(a, LineArgumentName))),
                "debug"),

            BridgeTool(
                "enable_all_breakpoints",
                "Enable all breakpoints.",
                EmptySchema(),
                "enable-all-breakpoints", static (_, _) => string.Empty, "debug"),

            BridgeTool(
                "disable_all_breakpoints",
                "Disable all breakpoints without removing them.",
                EmptySchema(),
                "disable-all-breakpoints", static (_, _) => string.Empty, "debug"),

            BridgeTool(
                DebugStackToolName,
                "Get debugger stack frames for current or selected thread.",
                ObjectSchema(
                    OptionalIntegerProperty("thread_id", "Optional debugger thread id."),
                    OptionalIntegerProperty("max_frames",
                        "Optional max frames (default 100).")),
                "debug-stack", static (_, a) => BuildDebugStackArgs(a), "debug"),

            BridgeTool(
                DebugLocalsToolName,
                "Get local variables for the current stack frame.",
                ObjectSchema(OptionalIntegerProperty(MaxArgumentName,
                    $"Optional max locals (default {DefaultLargeMaxCount}).")),
                "debug-locals", static (_, a) => BuildDebugLocalsArgs(a), "debug"),

            BridgeTool(
                DebugWatchToolName,
                "Evaluate one watch expression in break mode.",
                ObjectSchema(
                    RequiredStringProperty("expression", "Debugger watch expression."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        "Optional evaluation timeout milliseconds (default 1000).")),
                "debug-watch", static (_, a) => BuildDebugWatchArgs(a), "debug"),

            BridgeTool(
                DebugThreadsToolName,
                "Get debugger thread snapshot.",
                EmptySchema(),
                "debug-threads", static (_, _) => string.Empty, "debug"),

            BridgeTool(
                DebugModulesToolName,
                "Get debugger modules snapshot (best effort).",
                EmptySchema(),
                "debug-modules", static (_, _) => string.Empty, "debug"),

            BridgeTool(
                DebugExceptionsToolName,
                "Get debugger exception settings snapshot (best effort).",
                EmptySchema(),
                "debug-exceptions", static (_, _) => string.Empty, "debug"),

            // ── git ──────────────────────────────────────────────────────────────────

            new("git_status", "Get repository status in porcelain mode.",
                EmptySchema(), "git",
                (id, args, b) => CallGitToolAsync(id, "git_status", args, b)),

            new("git_add", "Stage files. Use ['.'] to stage all changes.",
                ObjectSchema(RequiredStringArrayProperty("paths",
                    "File paths, globs, or '.' to stage all.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_add", args, b)),

            new("git_commit", "Create a commit from staged changes.",
                ObjectSchema(RequiredStringProperty("message", "Commit message.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_commit", args, b)),

            new("git_commit_amend", "Amend the previous commit. Optionally replace the message.",
                ObjectSchema(
                    OptionalStringProperty("message", "Optional replacement commit message."),
                    OptionalBooleanProperty("no_edit",
                        "If true, keep the current commit message.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_commit_amend", args, b)),

            new("git_diff_staged",
                "Show staged diff with optional context lines.",
                ObjectSchema(OptionalIntegerProperty("context", DefaultContextLineCountDescription)),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_diff_staged", args, b)),

            new("git_diff_unstaged",
                "Show unstaged diff with optional context lines.",
                ObjectSchema(OptionalIntegerProperty("context", DefaultContextLineCountDescription)),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_diff_unstaged", args, b)),

            new("git_log", "Show recent commits in a compact machine-friendly format.",
                ObjectSchema(OptionalIntegerProperty("max_count",
                    $"Optional max commit count (default {DefaultGitLogMaxCount}).")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_log", args, b)),

            new("git_show", "Show metadata and patch for a specific commit.",
                ObjectSchema(RequiredStringProperty("revision",
                    "Commit-ish (hash, HEAD~1, tag).")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_show", args, b)),

            new("git_current_branch", "Get current branch name (short).",
                EmptySchema(), "git",
                (id, args, b) => CallGitToolAsync(id, "git_current_branch", args, b)),

            new("git_branch_list", "List local and remote branches.",
                EmptySchema(), "git",
                (id, args, b) => CallGitToolAsync(id, "git_branch_list", args, b)),

            new("git_checkout", "Checkout an existing branch or revision.",
                ObjectSchema(RequiredStringProperty("target",
                    "Branch name or revision to checkout.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_checkout", args, b)),

            new("git_create_branch", "Create and switch to a new branch.",
                ObjectSchema(
                    RequiredStringProperty("name", "New branch name."),
                    OptionalStringProperty("start_point",
                        "Optional start point (default HEAD).")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_create_branch", args, b)),

            new(GitMergeToolName, "Merge a branch or revision into the current branch.",
                ObjectSchema(
                    RequiredStringProperty("source",
                        "Branch name or revision to merge into the current branch."),
                    OptionalBooleanProperty("ff_only",
                        "If true, require a fast-forward merge."),
                    OptionalBooleanProperty("no_ff",
                        "If true, create a merge commit even when a fast-forward is possible."),
                    OptionalBooleanProperty("squash",
                        "If true, squash changes into the working tree without a merge commit."),
                    OptionalStringProperty("message", "Optional merge commit message.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, GitMergeToolName, args, b)),

            new("git_fetch", "Fetch updates from remotes.",
                ObjectSchema(
                    OptionalStringProperty("remote", "Optional remote name."),
                    OptionalBooleanProperty("all", "If true, fetch all remotes."),
                    OptionalBooleanProperty("prune",
                        "If true, prune deleted remote refs (default true).")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_fetch", args, b)),

            new(GitPullToolName, "Pull updates from a remote branch.",
                ObjectSchema(
                    OptionalStringProperty("remote",
                        "Optional remote name (default current tracking remote)."),
                    OptionalStringProperty("branch", "Optional branch name.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, GitPullToolName, args, b)),

            new(GitPushToolName, "Push current branch to remote.",
                ObjectSchema(
                    OptionalStringProperty("remote",
                        "Optional remote name (default current tracking remote)."),
                    OptionalStringProperty("branch", "Optional branch name."),
                    OptionalBooleanProperty("set_upstream",
                        "If true, pass --set-upstream.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, GitPushToolName, args, b)),

            new("git_remote_list", "List configured remotes with URLs.",
                EmptySchema(), "git",
                (id, args, b) => CallGitToolAsync(id, "git_remote_list", args, b)),

            new("git_reset", "Unstage paths while keeping working tree changes.",
                ObjectSchema(OptionalStringArrayProperty("paths",
                    "File paths or globs to unstage.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_reset", args, b)),

            new("git_restore", "Restore paths from HEAD in the working tree.",
                ObjectSchema(RequiredStringArrayProperty("paths",
                    "File paths or globs to restore.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_restore", args, b)),

            new("git_stash_push",
                "Stash local changes. Optionally include untracked files and a message.",
                ObjectSchema(
                    OptionalStringProperty("message", "Optional stash message."),
                    OptionalBooleanProperty("include_untracked",
                        "If true, include untracked files.")),
                "git",
                (id, args, b) => CallGitToolAsync(id, "git_stash_push", args, b)),

            new("git_stash_pop", "Apply and drop the latest stash entry.",
                EmptySchema(), "git",
                (id, args, b) => CallGitToolAsync(id, "git_stash_pop", args, b)),

            new("git_stash_list", "List stash entries.",
                EmptySchema(), "git",
                (id, args, b) => CallGitToolAsync(id, "git_stash_list", args, b)),

            new("git_tag_list", "List tags sorted by version-aware refname.",
                EmptySchema(), "git",
                (id, args, b) => CallGitToolAsync(id, "git_tag_list", args, b)),

            new("github_issue_search", "Search open or closed GitHub issues.",
                ObjectSchema(
                    OptionalStringProperty(QueryArgumentName, "Free-text search query."),
                    OptionalStringProperty("state", "open, closed, or all."),
                    OptionalStringProperty("repo",
                        "Optional owner/repo. Defaults to git origin repo."),
                    OptionalIntegerProperty("limit",
                        $"Max results (default {DefaultGitHubIssueSearchLimit}).")),
                "git",
                (id, args, b) => CallGitHubToolAsync(id, "github_issue_search", args, b)),

            new("github_issue_close",
                "Close a GitHub issue by number and optionally add a comment.",
                ObjectSchema(
                    RequiredIntegerProperty("issue_number", "Issue number to close."),
                    OptionalStringProperty("repo",
                        "Optional owner/repo. Defaults to git origin repo."),
                    OptionalStringProperty("comment", "Optional closing comment.")),
                "git",
                (id, args, b) => CallGitHubToolAsync(id, "github_issue_close", args, b)),

            // ── python ───────────────────────────────────────────────────────────────

            new("python_repl",
                "Execute a Python snippet using the selected interpreter. By default the " +
                "bridge runs Python in restricted scratch mode. Unrestricted execution " +
                "requires IDE Bridge > Allow Bridge Python Unrestricted Execution.",
                ObjectSchema(
                    RequiredStringProperty("code", "Python code to execute."),
                    OptionalStringProperty(PathArgumentName, OptionalInterpreterPathDescription),
                    OptionalStringProperty("cwd",
                        "Working directory for execution."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        DefaultPythonToolTimeoutDescription)),
                "python",
                (id, args, b) => CallPythonToolAsync(id, "python_repl", args, b)),

            new("python_run_file",
                "Execute an existing Python file using the selected interpreter. By default " +
                "the bridge runs Python in restricted scratch mode.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName, "Existing Python file to execute."),
                    OptionalStringArrayProperty("args",
                        "Optional arguments to pass to the script."),
                    OptionalStringProperty(PathArgumentName, OptionalInterpreterPathDescription),
                    OptionalStringProperty("cwd", "Working directory for execution."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        DefaultPythonToolTimeoutDescription)),
                "python",
                (id, args, b) => CallPythonToolAsync(id, "python_run_file", args, b)),

            new(PythonListEnvsToolName,
                "Discover bridge-managed CPython, system Python installs, virtual environments, " +
                "and conda environments that the bridge can attach to.",
                EmptySchema(), "python",
                (id, args, b) => CallPythonToolAsync(id, PythonListEnvsToolName, args, b)),

            new("python_set_active_env",
                "Select the Python interpreter that bridge Python tools will target. " +
                "Provide either 'path' (full interpreter path) or 'name' (environment name). " +
                "Does not install or modify the environment.",
                ObjectSchema(
                    OptionalStringProperty(PathArgumentName,
                        "Full interpreter path to select."),
                    OptionalStringProperty("name",
                        "Environment name to select (e.g. 'superslicer' for a conda env).")),
                "python",
                (id, args, b) => CallPythonToolAsync(id, "python_set_active_env", args, b)),

            new(PythonSetProjectEnvToolName,
                "Set the active Python interpreter for the open Visual Studio Python project. " +
                "Affects IntelliSense and debugging inside VS. Use python_set_active_env to " +
                "set the interpreter for bridge Python tools instead.",
                ObjectSchema(
                    OptionalStringProperty(PathArgumentName,
                        "Full interpreter path. Omit to auto-detect a conda env matching " +
                        "the project name."),
                    OptionalStringProperty(ProjectArgumentName,
                        "Python project name or path. Defaults to the active project.")),
                "python",
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(id, binding, "set-python-project-env",
                        BuildArgs(
                            (PathArgumentName, GetOptionalStringArgument(args, PathArgumentName)),
                            (ProjectArgumentName, GetOptionalStringArgument(args, ProjectArgumentName))
                        )).ConfigureAwait(false);
                    return BridgeCallResult(r);
                }),

            new(PythonListPackagesToolName,
                "List installed packages from the selected interpreter.",
                ObjectSchema(OptionalStringProperty(PathArgumentName,
                    "Optional interpreter path to query instead of the selected interpreter.")),
                "python",
                (id, args, b) => CallPythonToolAsync(id, PythonListPackagesToolName, args, b)),

            new(PythonInstallPackageToolName,
                "Install one or more Python packages into the selected interpreter environment. " +
                "Requires a Visual Studio approval popup unless IDE Bridge > Allow Bridge " +
                "Python Environment Mutation is enabled.",
                ObjectSchema(
                    RequiredStringArrayProperty(PackagesArgumentName,
                        "One or more Python package specs to install."),
                    OptionalStringProperty(PathArgumentName, OptionalInterpreterPathDescription),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        DefaultPythonToolTimeoutDescription)),
                "python",
                (id, args, b) => CallPythonToolAsync(id, PythonInstallPackageToolName, args, b)),

            new("python_install_requirements",
                "Install Python packages from a requirements.txt file. Requires a Visual " +
                "Studio approval popup unless IDE Bridge > Allow Bridge Python Environment " +
                "Mutation is enabled.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName,
                        "Path to the requirements.txt file."),
                    OptionalStringProperty(PathArgumentName, OptionalInterpreterPathDescription),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        DefaultPythonToolTimeoutDescription)),
                "python",
                (id, args, b) =>
                    CallPythonToolAsync(id, "python_install_requirements", args, b)),

            new(PythonRemovePackageToolName,
                "Remove one or more Python packages from the selected interpreter environment.",
                ObjectSchema(
                    RequiredStringArrayProperty(PackagesArgumentName,
                        "One or more Python package names to remove."),
                    OptionalStringProperty(PathArgumentName, OptionalInterpreterPathDescription),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        DefaultPythonToolTimeoutDescription)),
                "python",
                (id, args, b) => CallPythonToolAsync(id, PythonRemovePackageToolName, args, b)),

            new(PythonCreateEnvToolName,
                "Create a new virtual environment using the selected or an explicit base " +
                "interpreter. Requires IDE Bridge > Allow Bridge Python Environment Mutation.",
                ObjectSchema(
                    RequiredStringProperty(PathArgumentName,
                        "Directory where the new environment should be created."),
                    OptionalStringProperty(BasePathArgumentName,
                        "Optional base interpreter path."),
                    OptionalStringProperty("cwd", "Working directory."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        DefaultPythonToolTimeoutDescription)),
                "python",
                (id, args, b) => CallPythonToolAsync(id, PythonCreateEnvToolName, args, b)),

            new(PythonEnvInfoToolName,
                "Inspect one Python interpreter or environment. Defaults to the selected " +
                "interpreter, then the managed runtime, then the first discovered interpreter.",
                ObjectSchema(OptionalStringProperty(PathArgumentName,
                    "Optional interpreter path to inspect.")),
                "python",
                (id, args, b) => CallPythonToolAsync(id, PythonEnvInfoToolName, args, b)),

            new("python_set_startup_file",
                "Set the startup file for the active Python project (the file that runs on F5).",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName,
                        "Path to the Python file to set as startup."),
                    OptionalStringProperty(ProjectArgumentName,
                        "Python project name. Defaults to the active project.")),
                "python",
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(id, binding, "set-python-startup-file",
                        BuildArgs(
                            (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                            (ProjectArgumentName, GetOptionalStringArgument(args, ProjectArgumentName))
                        )).ConfigureAwait(false);
                    return BridgeCallResult(r);
                }),

            new("python_get_startup_file",
                "Get the startup file configured for the active Python project.",
                ObjectSchema(OptionalStringProperty(ProjectArgumentName,
                    "Python project name. Defaults to the active project.")),
                "python",
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(id, binding, "get-python-startup-file",
                        BuildArgs((ProjectArgumentName,
                            GetOptionalStringArgument(args, ProjectArgumentName))
                        )).ConfigureAwait(false);
                    return BridgeCallResult(r);
                }),

            new("python_sync_env",
                "Sync the active bridge Python interpreter to the open Visual Studio Python " +
                "project. Equivalent to python_set_project_env with the currently selected " +
                "bridge interpreter.",
                ObjectSchema(OptionalStringProperty(ProjectArgumentName,
                    "Python project name. Defaults to the active project.")),
                "python",
                async (id, args, binding) =>
                {
                    JsonObject r = await SendBridgeAsync(id, binding, "set-python-project-env",
                        BuildArgs(
                            (PathArgumentName, PythonRuntimeService.LoadActiveInterpreterPath()),
                            (ProjectArgumentName, GetOptionalStringArgument(args, ProjectArgumentName))
                        )).ConfigureAwait(false);
                    return BridgeCallResult(r);
                }),

            new("nuget_restore",
                "Restore NuGet packages with dotnet restore for the active solution or a " +
                "specific path.",
                ObjectSchema(OptionalStringProperty(PathArgumentName,
                    "Optional solution/project path. Defaults to the active bridge solution.")),
                "python",
                (id, args, b) => CallNuGetToolAsync(id, "nuget_restore", args, b)),

            new(NugetAddPackageToolName,
                "Add a NuGet package reference to a project via dotnet add package.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName,
                        "Project path (.csproj/.fsproj/.vbproj), absolute or " +
                        "solution-relative."),
                    RequiredStringProperty("package", "NuGet package id to add."),
                    OptionalStringProperty("version", "Optional package version."),
                    OptionalStringProperty("source", "Optional package source (name or URL)."),
                    OptionalBooleanProperty("prerelease",
                        "If true, include prerelease versions."),
                    OptionalBooleanProperty("no_restore",
                        "If true, skip restore after adding the package.")),
                "python",
                (id, args, b) => CallNuGetToolAsync(id, NugetAddPackageToolName, args, b)),

            new(NugetRemovePackageToolName,
                "Remove a NuGet package reference from a project via dotnet remove package.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName,
                        "Project path (.csproj/.fsproj/.vbproj)."),
                    RequiredStringProperty("package", "NuGet package id to remove.")),
                "python",
                (id, args, b) => CallNuGetToolAsync(id, NugetRemovePackageToolName, args, b)),

            new(CondaInstallToolName,
                "Install one or more packages into a conda environment.",
                ObjectSchema(
                    RequiredStringArrayProperty(PackagesArgumentName,
                        "One or more conda package specs (e.g. ['numpy','cmake>=3.29'])."),
                    OptionalStringProperty("name",
                        "Optional environment name (-n/--name)."),
                    OptionalStringProperty("prefix",
                        "Optional environment prefix path (--prefix)."),
                    OptionalStringArrayProperty("channels",
                        "Optional channels to add with --channel."),
                    OptionalBooleanProperty("dry_run", "If true, run with --dry-run."),
                    OptionalBooleanProperty("yes", "Auto-confirm install (default true).")),
                "python",
                (id, args, b) => CallCondaToolAsync(id, CondaInstallToolName, args, b)),

            new(CondaRemoveToolName,
                "Remove one or more packages from a conda environment.",
                ObjectSchema(
                    RequiredStringArrayProperty(PackagesArgumentName,
                        "One or more package names to remove."),
                    OptionalStringProperty("name", "Optional environment name (-n/--name)."),
                    OptionalStringProperty("prefix",
                        "Optional environment prefix path (--prefix)."),
                    OptionalBooleanProperty("dry_run", "If true, run with --dry-run."),
                    OptionalBooleanProperty("yes", "Auto-confirm remove (default true).")),
                "python",
                (id, args, b) => CallCondaToolAsync(id, CondaRemoveToolName, args, b)),

            // ── project ──────────────────────────────────────────────────────────────

            BridgeTool(
                ListProjectsToolName,
                "List all projects in the current solution, including startup-project flags.",
                EmptySchema(),
                "list-projects", static (_, _) => string.Empty, "project"),

            BridgeTool(
                AddProjectToolName,
                "Add an existing project file to the current solution.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName,
                        "Absolute project path (.csproj/.vcxproj/.fsproj)."),
                    OptionalStringProperty("solution_folder",
                        "Optional solution folder name to add the project under.")),
                "add-project",
                static (_, a) => BuildArgs(
                    (ProjectArgumentName, GetOptionalStringArgument(a, ProjectArgumentName)),
                    ("solution-folder", GetOptionalStringArgument(a, "solution_folder"))),
                "project"),

            BridgeTool(
                CreateProjectToolName,
                "Create a new project from a Visual Studio template and add it to the " +
                "current solution. Template accepts common aliases (classlib, console, wpf, " +
                "winforms, web, webapi, test, xunit, nunit, mstest) or any VS template name.",
                ObjectSchema(
                    RequiredStringProperty("name", "Project name."),
                    OptionalStringProperty("template",
                        "Template alias or VS template name (default ClassLibrary)."),
                    OptionalStringProperty("language",
                        "Language: CSharp (default), VisualBasic, FSharp, or VC."),
                    OptionalStringProperty("directory",
                        "Absolute directory for the project. Defaults to " +
                        "<solutionDir>/src/<name>."),
                    OptionalStringProperty("solution_folder",
                        "Optional solution folder name.")),
                "create-project",
                static (_, a) => BuildCreateProjectToolArgs(a), "project"),

            BridgeTool(
                RemoveProjectToolName,
                "Remove a project from the current solution by name or path.",
                ObjectSchema(RequiredStringProperty(ProjectArgumentName,
                    ProjectArgumentDescription)),
                "remove-project",
                static (_, a) => BuildArgs(
                    (ProjectArgumentName, GetOptionalStringArgument(a, ProjectArgumentName))),
                "project"),

            BridgeTool(
                AddFileToProjectToolName,
                "Add an existing file to a project without creating it on disk.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName,
                        "Project name, unique name, or full path."),
                    RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription)),
                "add-file-to-project",
                static (_, a) => BuildArgs(
                    (ProjectArgumentName, GetOptionalStringArgument(a, ProjectArgumentName)),
                    (FileArgumentName, GetOptionalStringArgument(a, FileArgumentName))),
                "project"),

            BridgeTool(
                RemoveFileFromProjectToolName,
                "Remove a file from a project without deleting the file from disk.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName,
                        "Project name, unique name, or full path."),
                    RequiredStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription)),
                "remove-file-from-project",
                static (_, a) => BuildArgs(
                    (ProjectArgumentName, GetOptionalStringArgument(a, ProjectArgumentName)),
                    (FileArgumentName, GetOptionalStringArgument(a, FileArgumentName))),
                "project"),

            BridgeTool(
                QueryProjectItemsToolName,
                "List items in one project with paths, item types, and VS item metadata.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName, ProjectArgumentDescription),
                    OptionalStringProperty(PathArgumentName,
                        "Optional file or directory filter within the project."),
                    OptionalIntegerProperty("max",
                        "Optional max item count (default 500).")),
                "query-project-items",
                static (_, a) => BuildQueryProjectItemsToolArgs(a), "project"),

            BridgeTool(
                QueryProjectPropertiesToolName,
                "Read project properties such as TargetFramework, AssemblyName, OutputType, " +
                "or RootNamespace.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName, ProjectArgumentDescription),
                    OptionalStringArrayProperty("names",
                        "Optional property names to read. Omit to return all accessible " +
                        "properties.")),
                "query-project-properties",
                static (_, a) => BuildQueryProjectPropertiesToolArgs(a), "project"),

            BridgeTool(
                QueryProjectConfigurationsToolName,
                "List available project configurations and platforms, including the active one.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName, ProjectArgumentDescription)),
                "query-project-configurations",
                static (_, a) => BuildQueryProjectConfigurationsToolArgs(a), "project"),

            BridgeTool(
                QueryProjectReferencesToolName,
                "List project references for one project. By default returns resolved " +
                "references with framework assemblies omitted; pass declared_only for a " +
                "cleaner project-file view.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName, ProjectArgumentDescription),
                    OptionalBooleanProperty("include_framework",
                        "Include framework/reference-assembly items (default false)."),
                    OptionalBooleanProperty("declared_only",
                        "Return only references declared in the project file (default false).")),
                "query-project-references",
                static (_, a) => BuildQueryProjectReferencesToolArgs(a), "project"),

            BridgeTool(
                QueryProjectOutputsToolName,
                "Resolve the primary output artifact and output directory for one project " +
                "using the active or requested build shape.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName, ProjectArgumentDescription),
                    OptionalStringProperty(ConfigurationArgumentName,
                        "Optional build configuration (e.g. Release)."),
                    OptionalStringProperty(PlatformArgumentName,
                        "Optional build platform (e.g. x64)."),
                    OptionalStringProperty("target_framework",
                        "Optional target framework moniker.")),
                "query-project-outputs",
                static (_, a) => BuildQueryProjectOutputsToolArgs(a), "project"),

            BridgeTool(
                SetStartupProjectToolName,
                "Set the current solution startup project.",
                ObjectSchema(RequiredStringProperty(ProjectArgumentName,
                    "Project name, unique name, or full path.")),
                "set-startup-project",
                static (_, a) => BuildArgs(
                    (ProjectArgumentName, GetOptionalStringArgument(a, ProjectArgumentName))),
                "project"),

            new("set_version",
                "Update the version string across all version files " +
                "(Directory.Build.props, source.extension.vsixmanifest, and " +
                "installer/inno/vs-ide-bridge.iss). Keeps every version location in sync.",
                ObjectSchema(RequiredStringProperty("version",
                    "New version string, e.g. '2.1.0'.")),
                "project",
                (id, args, b) => CallSetVersionToolAsync(id, args, b)),

            // ── system ───────────────────────────────────────────────────────────────

            BridgeTool(
                "execute_command",
                "Execute a Visual Studio command, optionally after positioning the caret.",
                ObjectSchema(
                    RequiredStringProperty("command",
                        "Visual Studio command name, e.g. 'Edit.FormatDocument'."),
                    OptionalStringProperty("args",
                        "Optional command arguments string passed to Visual Studio."),
                    OptionalStringProperty(FileArgumentName,
                        AbsoluteOrSolutionRelativeFilePathDescription),
                    OptionalStringProperty("document",
                        "Optional open-document query to position before running the command."),
                    OptionalIntegerProperty(LineArgumentName,
                        "Optional 1-based line number."),
                    OptionalIntegerProperty(ColumnArgumentName,
                        "Optional 1-based column number."),
                    OptionalBooleanProperty("select_word",
                        "If true, select the word at the caret before executing.")),
                "execute-command", static (_, a) => BuildExecuteCommandArgs(a), "system"),

            new(ShellExecToolName,
                "Execute a process and capture its stdout, stderr, and exit code. " +
                "Requires Visual Studio to be running — do not use after vs_close. " +
                "Working directory defaults to the solution directory.",
                ObjectSchema(
                    RequiredStringProperty("exe",
                        "Executable path or name (e.g. 'powershell', 'cmd', 'ISCC.exe')."),
                    OptionalStringProperty("args",
                        "Arguments string to pass to the executable."),
                    OptionalStringProperty("cwd", "Working directory."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        "Timeout in milliseconds (default 60000)."),
                    OptionalIntegerProperty("tail_lines",
                        "If set, truncate stdout and stderr to the last N lines each.")),
                "system",
                (id, args, b) => CallShellExecToolAsync(id, args, b)),

            BridgeTool(
                UiSettingsToolName,
                "Read current IDE Bridge UI/security settings. Read-only.",
                EmptySchema(), "ui-settings", static (_, _) => string.Empty, "system"),

            new("vs_open",
                "Launch a new Visual Studio instance, optionally opening a solution file. " +
                "Returns immediately — call wait_for_instance next before using any other tools.",
                ObjectSchema(
                    OptionalStringProperty(SolutionArgumentName,
                        "Absolute path to a .sln or .slnx file to open."),
                    OptionalStringProperty("devenv_path",
                        $"Explicit path to {DevenvExeFileName}. Auto-detected if omitted.")),
                "system",
                async (id, args, _) =>
                    (JsonNode)await CallVsOpenToolAsync(id, args).ConfigureAwait(false)),

            new("vs_close",
                "Close a Visual Studio instance. WARNING: all bridge tools stop working " +
                "immediately after this call. Use vs_open + wait_for_instance to reconnect.",
                ObjectSchema(
                    OptionalIntegerProperty("process_id",
                        "VS process ID to close. Defaults to the bound instance."),
                    OptionalBooleanProperty("force",
                        "If true, forcibly kill the process (default false).")),
                "system",
                (id, args, b) => Task.FromResult<JsonNode>(CallVsCloseTool(id, args, b))),

            new("wait_for_instance",
                "Wait for a newly launched Visual Studio bridge instance to appear and " +
                "become ready. Always call this immediately after vs_open.",
                ObjectSchema(
                    OptionalStringProperty(SolutionArgumentName,
                        "Optional absolute path to the .sln or .slnx file you expect."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName,
                        $"How long to wait in milliseconds (default {VsOpenDiscoveryTimeoutMilliseconds}).")),
                "system",
                async (id, args, b) =>
                    (JsonNode)await CallWaitForInstanceToolAsync(id, args, b).ConfigureAwait(false)),

            new(ToolDefinitionCatalog.ListTools(EmptySchema()),
                (_, _, _) => Task.FromResult<JsonNode>(ToolResult(ListToolsCompact()))),

            new(ToolDefinitionCatalog.ListToolCategories(EmptySchema()),
                (_, _, _) => Task.FromResult<JsonNode>(ToolResult(ListToolCategories()))),

            new(
                ToolDefinitionCatalog.ListToolsByCategory(
                    ObjectSchema(
                        RequiredStringProperty("category",
                            "Category name: core, search, diagnostics, documents, debug, " +
                            "git, python, project, or system."))),
                (_, args, _) => Task.FromResult<JsonNode>(ToolResult(ListToolsByCategory(args)))),

            new(
                ToolDefinitionCatalog.RecommendTools(
                    ObjectSchema(
                        RequiredStringProperty("task",
                            "Natural-language description of what you want to do."))),
                (_, args, _) => Task.FromResult<JsonNode>(ToolResult(RecommendTools(args)))),

            new(
                ToolDefinition.CreateLegacy(
                    ToolHelpToolName,
                    "system",
                    "Return MCP tool help. Pass name for one tool, category for a group " +
                    "(core/search/diagnostics/documents/debug/git/python/project/system), or " +
                    "omit both for the category index.",
                    ObjectSchema(
                        OptionalStringProperty("name", "Optional tool name for focused help."),
                        OptionalStringProperty("category",
                            "Optional category: core, search, diagnostics, documents, debug, " +
                            "git, python, project, or system.")),
                    aliases: [HelpToolName],
                    bridgeCommand: "help",
                    summary: "Read tool help by name or category."),
                (id, args, _) => Task.FromResult<JsonNode>(
                    ToolHelp(id,
                        GetOptionalStringArgument(args, "name"),
                        GetOptionalStringArgument(args, "category")))),
        ]);
    }
}
