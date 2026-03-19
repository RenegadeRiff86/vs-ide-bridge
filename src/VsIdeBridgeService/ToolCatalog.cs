using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

// Builds the canonical tool execution registry for the Windows service MCP server.
// This service is the primary MCP host; CLI hosting is backup only.
// Bridge commands route through the VS named pipe. Service-hosted tools stay local when
// there is no real bridge command behind the MCP surface.
internal static class ToolCatalog
{
    private const string FileDesc   = "Absolute or solution-relative file path.";
    private const string LineDesc   = "1-based line number.";
    private const string ColumnDesc = "1-based column number.";
    private const string ProjectFilterDesc = "Optional project name or path filter.";
    private static readonly Lazy<ToolRegistry> DefinitionRegistry = new(BuildDefinitionRegistry);

    public static ToolExecutionRegistry CreateRegistry()
    {
        return new ToolExecutionRegistry(CreateEntries());
    }

    private static IReadOnlyList<ToolEntry> CreateEntries()
    {
        return new IEnumerable<ToolEntry>[]
        {
            // ── core: discovery + binding ────────────────────────────────────────────
            CoreTools(),
            // ── search + read ────────────────────────────────────────────────────────
            SearchTools(),
            // ── diagnostics + build ──────────────────────────────────────────────────
            DiagnosticsTools(),
            // ── semantic navigation ──────────────────────────────────────────────────
            SemanticTools(),
            // ── editor / document management ────────────────────────────────────────
            DocumentTools(),
            // ── debug ────────────────────────────────────────────────────────────────
            DebugTools(),
            // ── project management ───────────────────────────────────────────────────
            ProjectTools(),
        }.SelectMany(static group => group).ToArray();
    }

    private static ToolRegistry BuildDefinitionRegistry()
    {
        return new ToolRegistry(CreateEntries().Select(static entry => entry.Definition));
    }

    // ── BridgeTool shorthand ─────────────────────────────────────────────────────

    // Send one bridge command, wrap response as MCP result.
    private static ToolEntry BridgeTool(
        ToolDefinition definition,
        string pipeCommand,
        Func<JsonObject?, string> buildArgs)
        => new(definition,
            async (id, args, bridge) =>
            {
                JsonObject r = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(r);
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
        bool? destructive = null)
        => new(name, description, schema, category,
            async (id, args, bridge) =>
            {
                JsonObject r = await bridge.SendAsync(id, pipeCommand, buildArgs(args))
                    .ConfigureAwait(false);
                return BridgeResult(r);
            },
            title,
            annotations,
            null,
            aliases,
            tags,
            pipeCommand,
            summary,
            readOnly,
            mutating,
            destructive);

    private static JsonNode BridgeResult(JsonObject response)
    {
        bool success = response["Success"]?.GetValue<bool>() ?? false;
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = response.ToJsonString(),
                },
            },
            ["isError"] = !success,
            ["structuredContent"] = response.DeepClone(),
        };
    }

    private static JsonNode ToolResult(JsonObject response, bool isError = false)
    {
        return new JsonObject
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
            ["structuredContent"] = response.DeepClone(),
        };
    }

    private static string EncodeUtf8ToBase64(string? text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));
    }

    private static string BuildApplyDiffArgs(JsonObject? args)
    {
        return Build(
            ("patch-text-base64", EncodeUtf8ToBase64(OptionalString(args, "patch"))),
            ("open-changed-files", "true"),
            ("save-changed-files", "true"));
    }

    private static string BuildWriteFileArgs(JsonObject? args)
    {
        return Build(
            ("file", OptionalString(args, "file")),
            ("content-base64", EncodeUtf8ToBase64(OptionalString(args, "content"))));
    }

    // ── core ─────────────────────────────────────────────────────────────────────

    private static IEnumerable<ToolEntry> CoreTools()
    {
        yield return new("bridge_health",
            "Get binding health, discovery source, and last round-trip metrics.",
            EmptySchema(), "core",
            (id, _, bridge) => BridgeHealthAsync(id, bridge));

        yield return new("list_instances",
            "List live VS IDE Bridge instances visible to this MCP server.",
            EmptySchema(), "core",
            (_, _, bridge) => ListInstancesAsync(bridge));

        yield return new("bind_instance",
            "Bind this MCP session to one specific Visual Studio bridge instance by " +
            "instance id, process id, or pipe name.",
            ObjectSchema(
                Opt("instance_id", "Optional exact bridge instance id."),
                OptInt("pid", "Optional Visual Studio process id."),
                Opt("pipe_name", "Optional exact bridge pipe name."),
                Opt("solution_hint", "Optional solution path or name substring.")),
            "core",
            async (id, args, bridge) =>
                (JsonNode)ToolResult(await bridge.BindAsync(id, args).ConfigureAwait(false)));

        yield return new("bind_solution",
            "Bind this MCP session to a VS instance whose solution matches a name or path hint.",
            ObjectSchema(Req("solution", "Solution name or path substring to match.")),
            "core",
            async (id, args, bridge) =>
            {
                JsonObject bindArgs = new() { ["solution_hint"] = args?["solution"]?.DeepClone() };
                return (JsonNode)ToolResult(await bridge.BindAsync(id, bindArgs).ConfigureAwait(false));
            });

        yield return BridgeTool("bridge_state",
            "Capture current Visual Studio state: version, solution path, build mode, " +
            "active document, and debug mode.",
            EmptySchema(), "state", _ => Empty());

        yield return BridgeTool("wait_for_ready",
            "Block until Visual Studio and IntelliSense are fully loaded. Call this after " +
            "open_solution or vs_open before running any semantic tools.",
            EmptySchema(), "ready", _ => Empty());

        yield return new(ToolDefinitionCatalog.ListTools(EmptySchema()),
            (_, _, _) => Task.FromResult<JsonNode>(
                ToolResult(DefinitionRegistry.Value.BuildCompactToolsList())));

        yield return new(ToolDefinitionCatalog.ListToolCategories(EmptySchema()),
            (_, _, _) => Task.FromResult<JsonNode>(
                ToolResult(DefinitionRegistry.Value.BuildCategoryList())));

        yield return new(
            ToolDefinitionCatalog.ListToolsByCategory(
                ObjectSchema(Req("category", "Category name."))),
            (_, args, _) => Task.FromResult<JsonNode>(
                ToolResult(
                    DefinitionRegistry.Value.BuildToolsByCategory(
                        args?["category"]?.GetValue<string>() ?? string.Empty))));

        yield return new(
            ToolDefinitionCatalog.RecommendTools(
                ObjectSchema(Req("task", "Natural-language description of what you want to do."))),
            (_, args, _) => Task.FromResult<JsonNode>(
                ToolResult(
                    DefinitionRegistry.Value.RecommendTools(
                        args?["task"]?.GetValue<string>() ?? string.Empty))));

        yield return BridgeTool("tool_help",
            "Return MCP tool help. Pass name for one tool, category for a group, or omit both for the category index.",
            ObjectSchema(
                Opt("name", "Optional tool name for focused help."),
                Opt("category", "Optional category: core, search, diagnostics, documents, debug, git, python, project, or system.")),
            "help",
            a => Build(
                ("name", OptionalString(a, "name")),
                ("category", OptionalString(a, "category"))),
            "system",
            aliases: new[] { "help" });

        yield return new("vs_open",
            "Launch a new Visual Studio instance, optionally opening a solution file. Returns immediately — call wait_for_instance next.",
            ObjectSchema(
                Opt("solution", "Absolute path to a .sln or .slnx file to open."),
                Opt("devenv_path", "Explicit path to devenv.exe. Auto-detected if omitted.")),
            "system",
            (id, args, _) => VsOpenAsync(id, args));

        yield return new("wait_for_instance",
            "Wait for a newly launched Visual Studio bridge instance to appear and become ready. Always call after vs_open.",
            ObjectSchema(
                Opt("solution", "Optional absolute path to the .sln or .slnx file you expect."),
                OptInt("timeout_ms", "How long to wait in milliseconds (default 60000).")),
            "system",
            (id, args, bridge) => WaitForInstanceAsync(id, args, bridge));

        yield return BridgeTool("ui_settings",
            "Read current IDE Bridge UI and security settings.",
            EmptySchema(), "ui-settings", _ => Empty());
    }

    // ── search + read ─────────────────────────────────────────────────────────────

    private static IEnumerable<ToolEntry> SearchTools()
    {
        yield return BridgeTool(ToolDefinitionCatalog.ReadFile(
            ObjectSchema(
                Req("file", FileDesc),
                OptInt("start_line", "First 1-based line to read. Use with end_line."),
                OptInt("end_line", "Last 1-based line to read (inclusive). Use with start_line."),
                OptInt("line", "Anchor 1-based line. Use with context_before/context_after."),
                OptInt("context_before", "Lines before anchor (default 10)."),
                OptInt("context_after", "Lines after anchor (default 30)."),
                OptBool("reveal_in_editor", "Reveal slice in editor (default true)."))),
            "document-slice",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("start-line", OptionalText(a, "start_line")),
                ("end-line", OptionalText(a, "end_line")),
                ("line", OptionalText(a, "line")),
                ("context-before", OptionalText(a, "context_before")),
                ("context-after", OptionalText(a, "context_after")),
                BoolArg("reveal-in-editor", a, "reveal_in_editor", true, true)));

        yield return BridgeTool(ToolDefinitionCatalog.ReadFileBatch(
            ObjectSchema(
                (("ranges",
                    new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ranges to read in order.",
                        ["items"] = ObjectSchema(
                            Req("file", FileDesc),
                            OptInt("start_line", "First 1-based line."),
                            OptInt("end_line", "Last 1-based line."),
                            OptInt("line", "Anchor line."),
                            OptInt("context_before", "Lines before anchor."),
                            OptInt("context_after", "Lines after anchor.")),
                        ["minItems"] = 1,
                    },
                    true)))),
            "document-slices",
            a => Build(("ranges", a?["ranges"]?.ToJsonString())));

        yield return BridgeTool(ToolDefinitionCatalog.FindFiles(
            ObjectSchema(
                Req("query", "File name or path fragment."),
                Opt("path", "Optional path fragment filter."),
                OptArr("extensions", "Optional extension filters like ['.cmake','.txt']."),
                OptBool("include_non_project", "Include disk files under solution root that are not in projects (default true)."),
                OptInt("max_results", "Optional max result count (default 200)."),
                Opt("scope", "Optional scope: solution (default), project, document, or open."))),
            "find-files",
            a => Build(
                ("query", OptionalString(a, "query")),
                ("path", OptionalString(a, "path")),
                ("extensions", a?["extensions"]?.ToJsonString()),
                BoolArg("include-non-project", a, "include_non_project", true, true),
                ("max-results", OptionalText(a, "max_results")),
                ("scope", OptionalString(a, "scope"))));

        yield return BridgeTool(ToolDefinitionCatalog.FindText(
            ObjectSchema(
                Req("query", "Search text or regex pattern."),
                Opt("path", "Optional path or directory filter."),
                Opt("scope", "Scope: solution (default), project, or document."),
                Opt("project", ProjectFilterDesc),
                OptBool("match_case", "Case-sensitive match (default false)."),
                OptBool("whole_word", "Match whole word only (default false)."),
                OptBool("regex", "Treat query as a regular expression (default false)."))),
            "find-text",
            a => Build(
                ("query", OptionalString(a, "query")),
                ("path", OptionalString(a, "path")),
                ("scope", OptionalString(a, "scope")),
                ("project", OptionalString(a, "project")),
                BoolArg("match-case", a, "match_case", false, true),
                BoolArg("whole-word", a, "whole_word", false, true),
                BoolArg("regex", a, "regex", false, true)));

        yield return BridgeTool(ToolDefinitionCatalog.FindTextBatch(
            ObjectSchema(
                (("queries",
                    new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Queries to search for in order.",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["minItems"] = 1,
                    },
                    true)),
                Opt("scope", "Optional scope: solution, project, document, or open."),
                Opt("project", ProjectFilterDesc),
                Opt("path", "Optional path filter."),
                OptInt("results_window", "Optional Find Results window number."),
                OptInt("max_queries_per_chunk", "Optional max query count per chunk (default 5)."),
                OptBool("match_case", "Case-sensitive match (default false)."),
                OptBool("whole_word", "Match whole word only (default false)."),
                OptBool("regex", "Treat queries as regular expressions (default false)."))),
            "find-text-batch",
            a => Build(
                ("queries", a?["queries"]?.ToJsonString()),
                ("scope", OptionalString(a, "scope")),
                ("project", OptionalString(a, "project")),
                ("path", OptionalString(a, "path")),
                ("results-window", OptionalText(a, "results_window")),
                ("max-queries-per-chunk", OptionalText(a, "max_queries_per_chunk")),
                BoolArg("match-case", a, "match_case", false, true),
                BoolArg("whole-word", a, "whole_word", false, true),
                BoolArg("regex", a, "regex", false, true)));

        yield return BridgeTool(ToolDefinitionCatalog.SearchSymbols(
            ObjectSchema(
                Req("query", "Symbol search text."),
                Opt("kind", "Optional symbol kind filter."),
                Opt("scope", "Optional scope: solution, project, document, or open."),
                Opt("project", ProjectFilterDesc),
                Opt("path", "Optional path or directory filter."),
                OptInt("max", "Optional max result count."),
                OptBool("match_case", "Case-sensitive match (default false)."))),
            "search-symbols",
            a => Build(
                ("query", OptionalString(a, "query")),
                ("kind", OptionalString(a, "kind")),
                ("scope", OptionalString(a, "scope")),
                ("project", OptionalString(a, "project")),
                ("path", OptionalString(a, "path")),
                ("max", OptionalText(a, "max")),
                BoolArg("match-case", a, "match_case", false, true)));

        yield return BridgeTool("search_solutions",
            "Search for solution files (.sln/.slnx) on disk under a given root directory. " +
            "Defaults to %USERPROFILE%\\source\\repos.",
            ObjectSchema(
                Opt("path", "Root directory to search."),
                Opt("query", "Filter by solution name (case-insensitive substring)."),
                OptInt("max_depth", "Max directory depth to recurse (default 6)."),
                OptInt("max", "Max results to return (default 200).")),
            "search-solutions",
            a => Build(
                ("path", OptionalString(a, "path")),
                ("query", OptionalString(a, "query")),
                ("max-depth", OptionalText(a, "max_depth")),
                ("max", OptionalText(a, "max"))),
            "search");
    }

    // ── diagnostics + build ───────────────────────────────────────────────────────

    private static IEnumerable<ToolEntry> DiagnosticsTools()
    {
        yield return BridgeTool(ToolDefinitionCatalog.Errors(
            ObjectSchema(
                Opt("severity", "Optional severity filter."),
                OptBool("wait_for_intellisense", "Wait for IntelliSense readiness first (default true)."),
                OptBool("quick", "Read current snapshot immediately (default false)."),
                OptInt("max", "Optional max rows."),
                Opt("code", "Optional diagnostic code prefix filter."),
                Opt("project", ProjectFilterDesc),
                Opt("path", "Optional path filter."),
                Opt("text", "Optional message text filter."),
                Opt("group_by", "Optional grouping mode."))),
            "errors",
            a => Build(
                ("severity", OptionalString(a, "severity")),
                BoolArg("wait-for-intellisense", a, "wait_for_intellisense", true, true),
                BoolArg("quick", a, "quick", false, true),
                ("max", OptionalText(a, "max")),
                ("code", OptionalString(a, "code")),
                ("project", OptionalString(a, "project")),
                ("path", OptionalString(a, "path")),
                ("text", OptionalString(a, "text")),
                ("group-by", OptionalString(a, "group_by"))));

        yield return BridgeTool("warnings",
            "Capture warning rows with optional code/path/project filters.",
            ObjectSchema(
                Opt("code", "Optional warning code prefix filter."),
                Opt("project", ProjectFilterDesc),
                Opt("path", "Optional path filter."),
                Opt("group_by", "Optional grouping mode (e.g. code).")),
            "warnings",
            a => Build(
                ("code", OptionalString(a, "code")),
                ("project", OptionalString(a, "project")),
                ("path", OptionalString(a, "path")),
                ("group-by", OptionalString(a, "group_by"))),
            "diagnostics");

        yield return BridgeTool("diagnostics_snapshot",
            "Aggregate IDE state, debugger state, build state, and current errors/warnings.",
            ObjectSchema(OptBool("wait_for_intellisense", "Wait for IntelliSense readiness (default false).")),
            "diagnostics-snapshot",
            a => Build(BoolArg("wait-for-intellisense", a, "wait_for_intellisense", false, true)),
            "diagnostics");

        yield return BridgeTool("build_configurations",
            "List available solution build configurations and platforms.",
            EmptySchema(), "build-configurations", _ => Empty(), "diagnostics");

        yield return BridgeTool("set_build_configuration",
            "Activate one build configuration/platform pair.",
            ObjectSchema(
                Opt("configuration", "Build configuration (e.g. Debug, Release)."),
                Opt("platform", "Build platform (e.g. x64).")),
            "set-build-configuration",
            a => Build(
                ("configuration", OptionalString(a, "configuration")),
                ("platform", OptionalString(a, "platform"))),
            "diagnostics");

        yield return new("build",
            "Trigger a solution build and return errors/warnings.",
            ObjectSchema(
                Opt("configuration", "Optional build configuration."),
                Opt("platform", "Optional build platform."),
                OptBool("wait_for_intellisense",
                    "Wait for IntelliSense readiness before building (default true).")),
            "diagnostics",
            async (id, args, bridge) =>
            {
                // Capture pre-existing diagnostics so the caller knows what was already broken.
                JsonNode? preBuild = null;
                try
                {
                    JsonObject pre = await bridge.SendAsync(id, "errors", "--quick")
                        .ConfigureAwait(false);
                    bool preSuccess = pre["Success"]?.GetValue<bool>() ?? false;
                    if (preSuccess)
                    {
                        int ec = pre["Data"]?["errorCount"]?.GetValue<int>() ?? 0;
                        int wc = pre["Data"]?["warningCount"]?.GetValue<int>() ?? 0;
                        int mc = pre["Data"]?["messageCount"]?.GetValue<int>() ?? 0;
                        if (ec > 0 || wc > 0 || mc > 0) preBuild = pre;
                    }
                }
                catch { /* non-fatal */ }

                string buildArgs = Build(
                    ("configuration", OptionalString(args, "configuration")),
                    ("platform", OptionalString(args, "platform")),
                    BoolArg("wait-for-intellisense", args, "wait_for_intellisense", true, true));

                JsonObject r = await bridge.SendAsync(id, "build", buildArgs).ConfigureAwait(false);
                if (preBuild is not null) r["preBuildDiagnostics"] = preBuild;
                return BridgeResult(r);
            });

        yield return new("build_errors",
            "Build then capture Error List rows in one call.",
            ObjectSchema(
                OptInt("max", "Optional max rows."),
                OptInt("timeout_ms", "Optional timeout in milliseconds."),
                OptBool("wait_for_intellisense", "Wait for IntelliSense readiness (default true).")),
            "diagnostics",
            async (id, args, bridge) =>
            {
                string buildArgs = Build(
                    ("max", OptionalText(args, "max")),
                    ("timeout-ms", OptionalText(args, "timeout_ms")),
                    BoolArg("wait-for-intellisense", args, "wait_for_intellisense", true, true));
                JsonObject r = await bridge.SendAsync(id, "build-errors", buildArgs)
                    .ConfigureAwait(false);
                return BridgeResult(r);
            });
    }

    // ── semantic navigation ───────────────────────────────────────────────────────

    private static IEnumerable<ToolEntry> SemanticTools()
    {
        yield return BridgeTool(ToolDefinitionCatalog.FileOutline(
            ObjectSchema(Req("file", FileDesc))),
            "file-outline",
            a => Build(("file", OptionalString(a, "file"))));

        yield return BridgeTool(ToolDefinitionCatalog.SymbolInfo(
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc), ReqInt("column", ColumnDesc))),
            "quick-info",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column"))));

        yield return BridgeTool(ToolDefinitionCatalog.FindReferences(
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc), ReqInt("column", ColumnDesc))),
            "find-references",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column"))));

        yield return BridgeTool("count_references",
            "Run Find All References and return the exact count.",
            ObjectSchema(
                Req("file", FileDesc),
                ReqInt("line", LineDesc),
                ReqInt("column", ColumnDesc),
                OptBool("activate_window", "Activate references window while counting (default true)."),
                OptInt("timeout_ms", "Optional window wait timeout in milliseconds.")),
            "count-references",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column")),
                BoolArg("activate-window", a, "activate_window", true, true),
                ("timeout-ms", OptionalText(a, "timeout_ms"))),
            "search");

        yield return BridgeTool("call_hierarchy",
            "Open Call Hierarchy for the symbol at a file/line/column.",
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc), ReqInt("column", ColumnDesc)),
            "call-hierarchy",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column"))),
            "search");

        yield return BridgeTool("goto_definition",
            "Navigate to the definition of the symbol at a file/line/column.",
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc), ReqInt("column", ColumnDesc)),
            "goto-definition",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column"))),
            "search");

        yield return BridgeTool("goto_implementation",
            "Navigate to an implementation of the symbol at a file/line/column.",
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc), ReqInt("column", ColumnDesc)),
            "goto-implementation",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column"))),
            "search");

        yield return BridgeTool(ToolDefinitionCatalog.PeekDefinition(
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc), ReqInt("column", ColumnDesc))),
            "peek-definition",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column"))));

    }

    // ── editor / document management ─────────────────────────────────────────────

    private static IEnumerable<ToolEntry> DocumentTools()
    {
        yield return new(
            ToolDefinitionCatalog.ApplyDiff(
                ObjectSchema(
                    Req("patch", "Patch text in editor patch or unified diff format."),
                    OptBool("post_check",
                        "Run wait_for_ready and errors after applying (default true)."))),
            async (id, args, bridge) =>
            {
                JsonObject r = await bridge.SendAsync(
                    id,
                    "apply-diff",
                    BuildApplyDiffArgs(args))
                    .ConfigureAwait(false);
                if (ArgBuilder.OptionalBool(args, "post_check", true))
                {
                    JsonObject ready = await bridge.SendAsync(id, "ready", Empty())
                        .ConfigureAwait(false);
                    JsonObject errors = await bridge.SendAsync(id, "errors",
                        "--wait-for-intellisense true").ConfigureAwait(false);
                    r["postCheck"] = new JsonObject { ["ready"] = ready, ["errors"] = errors };
                }

                return BridgeResult(r);
            });

        yield return new(
            ToolDefinitionCatalog.WriteFile(
                ObjectSchema(
                    Req("file", FileDesc),
                    Req("content", "Full UTF-8 text content to write."),
                    OptBool("post_check", "Run wait_for_ready and errors after writing (default true)."))),
            async (id, args, bridge) =>
            {
                string writeArgs = BuildWriteFileArgs(args);
                JsonObject r = await bridge.SendAsync(id, "write-file", writeArgs)
                    .ConfigureAwait(false);
                if (ArgBuilder.OptionalBool(args, "post_check", true))
                {
                    JsonObject ready = await bridge.SendAsync(id, "ready", Empty())
                        .ConfigureAwait(false);
                    JsonObject errors = await bridge.SendAsync(id, "errors",
                        "--wait-for-intellisense true").ConfigureAwait(false);
                    r["postCheck"] = new JsonObject { ["ready"] = ready, ["errors"] = errors };
                }
                return BridgeResult(r);
            });

        yield return BridgeTool("open_file",
            "Open a document by absolute path, solution-relative path, or solution item name.",
            ObjectSchema(
                Req("file", FileDesc),
                OptInt("line", "Optional 1-based line number to navigate to."),
                OptInt("column", "Optional 1-based column number.")),
            "open-document",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column"))));

        yield return BridgeTool("close_file",
            "Close one open file tab by path or query.",
            ObjectSchema(Opt("file", "File path to close."), Opt("query", "Tab caption query.")),
            "close-file",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("query", OptionalString(a, "query"))));

        yield return BridgeTool("close_document",
            "Close one or more open tabs by query.",
            ObjectSchema(Req("query", "Tab caption query."), OptBool("all", "Close all matching tabs.")),
            "close-document",
            a => Build(
                ("query", OptionalString(a, "query")),
                BoolArg("all", a, "all", false, true)));

        yield return BridgeTool("close_others",
            "Close all tabs except the active tab.",
            ObjectSchema(OptBool("save", "Save before closing (default false).")),
            "close-others",
            a => Build(BoolArg("save", a, "save", false, true)));

        yield return BridgeTool("save_document",
            "Save one document by path or save all open documents.",
            ObjectSchema(Opt("file", "File to save. Omit to save all.")),
            "save-document",
            a => Build(("file", OptionalString(a, "file"))));

        yield return BridgeTool("reload_document",
            "Reload a document from disk in the editor.",
            ObjectSchema(Req("file", FileDesc)),
            "open-document",
            a => Build(("file", OptionalString(a, "file"))));

        yield return BridgeTool("list_documents",
            "List open documents.",
            EmptySchema(), "list-documents", _ => Empty(), "documents");

        yield return BridgeTool("list_tabs",
            "List open editor tabs and identify the active tab.",
            EmptySchema(), "list-tabs", _ => Empty(), "documents");

        yield return BridgeTool("activate_document",
            "Activate an open document tab by query.",
            ObjectSchema(Req("query", "File name or tab caption fragment.")),
            "activate-document",
            a => Build(("query", OptionalString(a, "query"))));

        yield return BridgeTool("list_windows",
            "List Visual Studio tool windows (Solution Explorer, Error List, Output, etc.).",
            ObjectSchema(Opt("query", "Optional caption filter.")),
            "list-windows",
            a => Build(("query", OptionalString(a, "query"))));

        yield return BridgeTool("activate_window",
            "Bring a Visual Studio tool window to the foreground by caption fragment.",
            ObjectSchema(Req("window", "Window caption fragment.")),
            "activate-window",
            a => Build(("window", OptionalString(a, "window"))));

        yield return BridgeTool("execute_command",
            "Execute an arbitrary Visual Studio command with optional arguments.",
            ObjectSchema(
                Req("command", "Visual Studio command name (e.g. Edit.FormatDocument)."),
                Opt("args", "Optional command arguments string."),
                Opt("file", FileDesc),
                Opt("document", "Optional open-document query to position before running."),
                OptInt("line", "Optional 1-based line number."),
                OptInt("column", "Optional 1-based column number."),
                OptBool("select_word", "If true, select the word at the caret before executing.")),
            "execute-command",
            a => Build(
                ("name", OptionalString(a, "command")),
                ("args", OptionalString(a, "args")),
                ("file", OptionalString(a, "file")),
                ("document", OptionalString(a, "document")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column")),
                BoolArg("select-word", a, "select_word", false, true)));

        yield return BridgeTool("format_document",
            "Format the current document or a specific file.",
            ObjectSchema(
                Opt("file", FileDesc),
                OptInt("line", "Optional 1-based line."),
                OptInt("column", "Optional 1-based column.")),
            "execute-command",
            a =>
            {
                string? file = OptionalString(a, "file");
                if (string.IsNullOrWhiteSpace(file))
                    return Build(("name", "Edit.FormatDocument"));
                return Build(
                    ("name", "Edit.FormatDocument"),
                    ("file", file),
                    ("line", OptionalText(a, "line")),
                    ("column", OptionalText(a, "column")));
            });

        yield return BridgeTool("open_solution",
            "Open a specific existing .sln or .slnx file in the current Visual Studio instance.",
            ObjectSchema(
                Req("solution", "Absolute path to the .sln or .slnx file."),
                OptBool("wait_for_ready", "Wait for readiness after opening (default true).")),
            "open-solution",
            a => Build(
                ("solution", OptionalString(a, "solution")),
                BoolArg("wait-for-ready", a, "wait_for_ready", true, true)));

        yield return BridgeTool("create_solution",
            "Create and open a new solution in the current Visual Studio instance.",
            ObjectSchema(
                Req("directory", "Absolute directory where the solution should be created."),
                Req("name", "Solution name ('.sln' is optional.)")),
            "create-solution",
            a => Build(
                ("directory", OptionalString(a, "directory")),
                ("name", OptionalString(a, "name"))));

        yield return new("vs_close",
            "Close a Visual Studio instance by process id, or the currently bound instance.",
            ObjectSchema(
                OptInt("process_id", "Process ID of the VS instance to close. Defaults to bound instance."),
                OptBool("force", "Kill the process instead of gracefully closing (default false).")),
            "system",
            (id, args, bridge) => VsCloseAsync(id, args, bridge));

        yield return new("shell_exec",
            "Execute a process and capture its stdout, stderr, and exit code. Prefer build, build_errors, execute_command, or project tools first; use this when no bridge-native tool fits. Working directory defaults to the solution directory.",
            ObjectSchema(
                Req("exe", "Executable path or name (e.g. 'powershell', 'cmd', 'ISCC.exe')."),
                Opt("args", "Arguments string to pass to the executable."),
                Opt("cwd", "Working directory."),
                OptInt("timeout_ms", "Timeout in milliseconds (default 60000)."),
                OptInt("tail_lines", "If set, truncate stdout and stderr to the last N lines each.")),
            "system",
            (id, args, bridge) => ShellExecTool.ExecuteAsync(id, args, bridge));

        yield return new("set_version",
            "Update the version string across all version files in the solution.",
            ObjectSchema(
                Req("version", "New version string (e.g. 2.1.0).")),
            "system",
            (id, args, bridge) => SetVersionTool.ExecuteAsync(id, args, bridge));
    }

    // ── debug ─────────────────────────────────────────────────────────────────────

    private static IEnumerable<ToolEntry> DebugTools()
    {
        yield return BridgeTool("set_breakpoint",
            "Set a breakpoint at file/line with optional condition and hit count.",
            ObjectSchema(
                Req("file", FileDesc),
                ReqInt("line", LineDesc),
                OptInt("column", "1-based column (default 1)."),
                Opt("condition", "Breakpoint condition expression."),
                OptInt("hit_count", "Hit count (default 0 = ignore).")),
            "set-breakpoint",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line")),
                ("column", OptionalText(a, "column")),
                ("condition", OptionalString(a, "condition")),
                ("hit-count", OptionalText(a, "hit_count"))),
            "debug");

        yield return BridgeTool("list_breakpoints",
            "List all breakpoints in the current debug session.",
            EmptySchema(), "list-breakpoints", _ => Empty(), "debug");

        yield return BridgeTool("remove_breakpoint",
            "Remove a breakpoint by file and line number.",
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc)),
            "remove-breakpoint",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line"))),
            "debug");

        yield return BridgeTool("clear_breakpoints",
            "Remove all breakpoints.",
            EmptySchema(), "clear-breakpoints", _ => Empty(), "debug");

        yield return BridgeTool("enable_breakpoint",
            "Enable a disabled breakpoint at file/line.",
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc)),
            "enable-breakpoint",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line"))),
            "debug");

        yield return BridgeTool("disable_breakpoint",
            "Disable a breakpoint at file/line.",
            ObjectSchema(Req("file", FileDesc), ReqInt("line", LineDesc)),
            "disable-breakpoint",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("line", OptionalText(a, "line"))),
            "debug");

        yield return BridgeTool("enable_all_breakpoints",
            "Enable all breakpoints.",
            EmptySchema(), "enable-all-breakpoints", _ => Empty(), "debug");

        yield return BridgeTool("disable_all_breakpoints",
            "Disable all breakpoints.",
            EmptySchema(), "disable-all-breakpoints", _ => Empty(), "debug");

        yield return BridgeTool("debug_threads",
            "List debugger threads for the active debug session.",
            EmptySchema(), "debug-threads", _ => Empty(), "debug");

        yield return BridgeTool("debug_stack",
            "Capture stack frames for the current or selected debugger thread.",
            ObjectSchema(
                OptInt("thread_id", "Optional thread ID."),
                OptInt("max_frames", "Optional max frame count.")),
            "debug-stack",
            a => Build(
                ("thread-id", OptionalText(a, "thread_id")),
                ("max-frames", OptionalText(a, "max_frames"))),
            "debug");

        yield return BridgeTool("debug_locals",
            "Capture local variables for the active stack frame.",
            ObjectSchema(OptInt("max", "Optional max variable count.")),
            "debug-locals",
            a => Build(("max", OptionalText(a, "max"))),
            "debug");

        yield return BridgeTool("debug_modules",
            "Capture debugger module snapshot.",
            EmptySchema(), "debug-modules", _ => Empty(), "debug");

        yield return BridgeTool("debug_watch",
            "Evaluate one debugger watch expression in break mode.",
            ObjectSchema(Req("expression", "Expression to evaluate.")),
            "debug-watch",
            a => Build(("expression", OptionalString(a, "expression"))),
            "debug");

        yield return BridgeTool("debug_exceptions",
            "Capture debugger exception group/settings snapshot.",
            EmptySchema(), "debug-exceptions", _ => Empty(), "debug");
    }

    // ── project management ────────────────────────────────────────────────────────

    private static IEnumerable<ToolEntry> ProjectTools()
    {
        yield return BridgeTool("list_projects",
            "List all projects in the open solution.",
            EmptySchema(), "list-projects", _ => Empty(), "project");

        yield return BridgeTool("query_project_items",
            "List items in a project with file paths, kinds, and item types.",
            ObjectSchema(
                Req("project", "Project name or path."),
                Opt("path", "Optional path filter."),
                OptInt("max", "Max items to return (default 200).")),
            "query-project-items",
            a => Build(
                ("project", OptionalString(a, "project")),
                ("path", OptionalString(a, "path")),
                ("max", OptionalText(a, "max"))),
            "project");

        yield return BridgeTool("query_project_properties",
            "Read MSBuild project properties from one project.",
            ObjectSchema(
                Req("project", "Project name or path."),
                OptArr("names", "Property names to read.")),
            "query-project-properties",
            a => Build(
                ("project", OptionalString(a, "project")),
                ("names", OptionalStringArray(a, "names"))),
            "project");

        yield return BridgeTool("query_project_configurations",
            "List project configurations and platforms for one project.",
            ObjectSchema(Req("project", "Project name or path.")),
            "query-project-configurations",
            a => Build(("project", OptionalString(a, "project"))),
            "project");

        yield return BridgeTool("query_project_references",
            "List project references for one project.",
            ObjectSchema(
                Req("project", "Project name or path."),
                OptBool("declared_only", "Return only declared (project-file) references."),
                OptBool("include_framework",
                    "Include framework assembly references (default false).")),
            "query-project-references",
            a => Build(
                ("project", OptionalString(a, "project")),
                BoolArg("declared-only", a, "declared_only", false, true),
                BoolArg("include-framework", a, "include_framework", false, true)),
            "project");

        yield return BridgeTool("query_project_outputs",
            "Resolve the primary output artifact and output directory for one project.",
            ObjectSchema(
                Req("project", "Project name or path."),
                Opt("configuration", "Build configuration."),
                Opt("target_framework", "Target framework moniker.")),
            "query-project-outputs",
            a => Build(
                ("project", OptionalString(a, "project")),
                ("configuration", OptionalString(a, "configuration")),
                ("target-framework", OptionalString(a, "target_framework"))),
            "project");

        yield return BridgeTool("add_project",
            "Add an existing or new project to the solution.",
            ObjectSchema(
                Req("project", "Absolute path to the project file."),
                Opt("solution_folder", "Optional solution folder name.")),
            "add-project",
            a => Build(
                ("path", OptionalString(a, "project")),
                ("solution-folder", OptionalString(a, "solution_folder"))),
            "project");

        yield return BridgeTool("remove_project",
            "Remove a project from the solution by name or path.",
            ObjectSchema(Req("project", "Project name or path to remove.")),
            "remove-project",
            a => Build(("project", OptionalString(a, "project"))),
            "project");

        yield return BridgeTool("create_project",
            "Create a new project and add it to the open solution.",
            ObjectSchema(
                Req("name", "New project name."),
                Opt("template", "Project template name or identifier."),
                Opt("language", "Programming language (e.g. C#, VB, F#)."),
                Opt("directory", "Directory to create the project in."),
                Opt("solution_folder", "Optional solution folder name.")),
            "create-project",
            a => Build(
                ("name", OptionalString(a, "name")),
                ("template", OptionalString(a, "template")),
                ("language", OptionalString(a, "language")),
                ("directory", OptionalString(a, "directory")),
                ("solution-folder", OptionalString(a, "solution_folder"))),
            "project");

        yield return BridgeTool("set_startup_project",
            "Set the solution startup project by name or path.",
            ObjectSchema(Req("project", "Project name or path.")),
            "set-startup-project",
            a => Build(("project", OptionalString(a, "project"))),
            "project");

        yield return BridgeTool("add_file_to_project",
            "Add an existing file to a project.",
            ObjectSchema(
                Req("project", "Project name or path."),
                Req("file", "Absolute path to the file.")),
            "add-file-to-project",
            a => Build(
                ("project", OptionalString(a, "project")),
                ("file", OptionalString(a, "file"))),
            "project");

        yield return BridgeTool("remove_file_from_project",
            "Remove a file from a project.",
            ObjectSchema(
                Req("project", "Project name or path."),
                Req("file", "File path to remove.")),
            "remove-file-from-project",
            a => Build(
                ("project", OptionalString(a, "project")),
                ("file", OptionalString(a, "file"))),
            "project");

        yield return BridgeTool("python_set_project_env",
            "Set the active Python interpreter for the open project in Visual Studio.",
            ObjectSchema(Req("path", "Absolute path to the Python interpreter.")),
            "set-python-project-env",
            a => Build(("path", OptionalString(a, "path"))),
            "project");
    }

    // ── custom tool handlers ──────────────────────────────────────────────────────

    private static Task<JsonNode> BridgeHealthAsync(JsonNode? id, BridgeConnection bridge)
    {
        BridgeInstance? inst = bridge.CurrentInstance;
        JsonObject health = new()
        {
            ["success"] = true,
            ["discoveryMode"] = bridge.Mode.ToString(),
            ["currentSolutionPath"] = bridge.CurrentSolutionPath,
            ["bound"] = inst is not null,
        };

        if (inst is not null)
        {
            health["instance"] = new JsonObject
            {
                ["instanceId"] = inst.InstanceId,
                ["pipeName"] = inst.PipeName,
                ["processId"] = inst.ProcessId,
                ["solutionPath"] = inst.SolutionPath ?? string.Empty,
                ["source"] = inst.Source,
            };
        }

        return Task.FromResult((JsonNode)BridgeResult(health));
    }

    private static async Task<JsonNode> VsOpenAsync(JsonNode? id, JsonObject? args)
    {
        string? solution = args?["solution"]?.GetValue<string>();
        string? explicitDevenv = args?["devenv_path"]?.GetValue<string>();
        string devenvPath = string.IsNullOrWhiteSpace(explicitDevenv)
            ? ResolveDevenvPath(id)
            : explicitDevenv;

        string ps = string.IsNullOrWhiteSpace(solution)
            ? $"$p=Start-Process -FilePath '{QuotePsLiteral(devenvPath)}' -PassThru; Write-Output $p.Id"
            : $"$p=Start-Process -FilePath '{QuotePsLiteral(devenvPath)}'" +
              $" -ArgumentList @('{QuotePsLiteral(solution!)}') -PassThru; Write-Output $p.Id";

        ProcessStartInfo psi = new()
        {
            FileName = GetPowerShellPath(),
            Arguments = $"-NoProfile -NonInteractive -Command \"{ps}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process proc = Process.Start(psi)!;
        string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        bool ok = int.TryParse(stdout.Trim(), out int pid) && pid > 0;
        if (!ok)
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Visual Studio launch failed. stderr: {stderr.Trim()}");

        return BridgeResult(new JsonObject
        {
            ["Success"] = true,
            ["pid"] = pid,
            ["devenv_path"] = devenvPath,
            ["solution"] = solution ?? string.Empty,
        });
    }

    private static async Task<JsonNode> WaitForInstanceAsync(
        JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string? solutionHint = args?["solution"]?.GetValue<string>();
        int timeoutMs = args?["timeout_ms"]?.GetValue<int?>() ?? 60_000;

        IReadOnlyList<BridgeInstance> existing =
            await VsDiscovery.ListAsync(bridge.Mode).ConfigureAwait(false);
        HashSet<string> existingIds = existing
            .Select(i => i.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using CancellationTokenSource cts = new(timeoutMs);
        while (!cts.Token.IsCancellationRequested)
        {
            IReadOnlyList<BridgeInstance> current =
                await VsDiscovery.ListAsync(bridge.Mode).ConfigureAwait(false);
            BridgeInstance? found = current.FirstOrDefault(i =>
                !existingIds.Contains(i.InstanceId) &&
                (solutionHint is null ||
                 (i.SolutionPath?.Contains(solutionHint, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (i.SolutionName?.Contains(solutionHint, StringComparison.OrdinalIgnoreCase) ?? false)));

            if (found is not null)
            {
                return BridgeResult(new JsonObject
                {
                    ["Success"] = true,
                    ["instanceId"] = found.InstanceId,
                    ["pipeName"] = found.PipeName,
                    ["processId"] = found.ProcessId,
                    ["solutionPath"] = found.SolutionPath ?? string.Empty,
                });
            }

            try { await Task.Delay(500, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            $"No new VS instance appeared within {timeoutMs} ms.");
    }

    private static Task<JsonNode> VsCloseAsync(
        JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        int pid = args?["process_id"]?.GetValue<int?>() ??
                  bridge.CurrentInstance?.ProcessId ?? 0;
        bool force = args?["force"]?.GetValue<bool?>() ?? false;
        if (pid <= 0)
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                "No process_id specified and no VS instance bound.");
        try
        {
            Process proc = Process.GetProcessById(pid);
            if (force)
                proc.Kill();
            else
                proc.CloseMainWindow();
            return Task.FromResult(BridgeResult(new JsonObject
            {
                ["Success"] = true,
                ["processId"] = pid,
                ["forced"] = force,
            }));
        }
        catch (Exception ex)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to close VS process {pid}: {ex.Message}");
        }
    }

    private static string ResolveDevenvPath(JsonNode? id)
    {
        string vswhereExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhereExe))
        {
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = vswhereExe,
                    Arguments = "-latest -prerelease -requires Microsoft.Component.MSBuild" +
                                " -find Common7\\IDE\\devenv.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process proc = Process.Start(psi)!;
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                    return output;
            }
            catch { /* fall through */ }
        }
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(pf, "Microsoft Visual Studio", "18", "Community",    "Common7", "IDE", "devenv.exe"),
            Path.Combine(pf, "Microsoft Visual Studio", "18", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(pf, "Microsoft Visual Studio", "18", "Enterprise",   "Common7", "IDE", "devenv.exe"),
            Path.Combine(pf, "Microsoft Visual Studio", "2022", "Community",    "Common7", "IDE", "devenv.exe"),
            Path.Combine(pf, "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(pf, "Microsoft Visual Studio", "2022", "Enterprise",   "Common7", "IDE", "devenv.exe"),
        ];
        foreach (string c in candidates)
            if (File.Exists(c)) return c;
        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "devenv.exe not found. Install Visual Studio or pass 'devenv_path' explicitly.");
    }

    private static string QuotePsLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string GetPowerShellPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

    private static async Task<JsonNode> ListInstancesAsync(BridgeConnection bridge)
    {
        IReadOnlyList<BridgeInstance> instances = await VsDiscovery
            .ListAsync(bridge.Mode).ConfigureAwait(false);

        JsonArray arr = new();
        foreach (BridgeInstance inst in instances)
        {
            arr.Add(new JsonObject
            {
                ["instanceId"] = inst.InstanceId,
                ["pipeName"] = inst.PipeName,
                ["processId"] = inst.ProcessId,
                ["solutionPath"] = inst.SolutionPath ?? string.Empty,
                ["solutionName"] = inst.SolutionName ?? string.Empty,
                ["source"] = inst.Source,
            });
        }

        JsonObject result = new() { ["success"] = true, ["instances"] = arr };
        return BridgeResult(result);
    }
}
