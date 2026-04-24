using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string SearchDescriptionProperty = "description";
    private const string SearchExtensionsProperty = "extensions";
    private const string SearchFileOutlineTool = "file_outline";
    private const string SearchFindTextTool = "find_text";
    private const string MatchCase = "match_case";
    private const string SearchReadFileTool = "read_file";
    private const string SearchSymbolsTool = "search_symbols";
    private const string SearchReadFileBatchTool = "read_file_batch";

    private static IEnumerable<ToolEntry> SearchTools()
        =>
        FileReadTools()
            .Concat(TextSearchTools())
            .Concat(CodeExplorationTools());

    private static IEnumerable<ToolEntry> FileReadTools()
    {
        yield return BridgeTool(
            ToolDefinitionCatalog.ReadFile(
                ObjectSchema(
                    Req("file", FileDesc),
                    OptInt("start_line", "First 1-based line to read. Use with end_line."),
                    OptInt("end_line", "Last 1-based line to read (inclusive). Use with start_line."),
                    OptInt("line", "Anchor 1-based line. Use with context_before/context_after."),
                    OptInt("context_before", "Lines before anchor (default 10)."),
                    OptInt("context_after", "Lines after anchor (default 30)."),
                    OptBool("reveal_in_editor", "Reveal slice in editor (default true).")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("apply_diff", "Apply targeted edits to the file"), ("file_outline", "Get symbol structure of the file")],
                    related: [("read_file_batch", "Read multiple slices or files in one call — pass start_line/end_line per range"), ("file_outline", "Get symbol line numbers first, then re-call read_file with start_line/end_line to read only that slice"), ("find_text", "Search for text to locate the right line before slicing")])),
            "document-slice",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("start-line", OptionalText(a, "start_line")),
                ("end-line", OptionalText(a, "end_line")),
                (Line, OptionalText(a, Line)),
                ("context-before", OptionalText(a, "context_before")),
                ("context-after", OptionalText(a, "context_after")),
                BoolArg("reveal-in-editor", a, "reveal_in_editor", true, true)));

        yield return BridgeTool(
            ToolDefinitionCatalog.ReadFileBatch(
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
                        true))))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("apply_diff", "Apply changes after reading multiple files")],
                    related: [("read_file", "Read a single slice with start_line/end_line"), ("file_outline", "Get line numbers for each symbol first, then batch the slices here"), ("find_text_batch", "Search multiple patterns to discover line numbers before batching")])),
            "document-slices",
            a => Build(("ranges", a?["ranges"]?.ToJsonString())));
    }

    private static IEnumerable<ToolEntry> TextSearchTools()
        =>
        FileDiscoveryTools()
            .Concat(TextPatternTools())
            .Concat(SymbolSearchTools());

    private static IEnumerable<ToolEntry> FileDiscoveryTools()
    {
        yield return new(
            ToolDefinitionCatalog.FindFiles(
                ObjectSchema(
                    Req(Query, "File name or path fragment. Use instead of Glob/find/ls when you know the filename or a partial path. For glob patterns like **/*.cs use the glob tool instead."),
                    Opt(Path, "Optional path fragment filter."),
                    OptArr(SearchExtensionsProperty, "Optional extension filters like ['.cmake','.txt']."),
                    OptBool("include_non_project", "Include disk files under solution root that are not in projects (default true)."),
                    OptInt("max_results", "Optional max result count (default 200)."),
                    Opt(Scope, "Optional scope: solution (default), project, document, or open.")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(SearchReadFileTool, "Read the found file"), (SearchReadFileBatchTool, "Read multiple found files at once")],
                    related: [("glob", "Find files by glob pattern"), (SearchFindTextTool, "Search file contents"), (SearchSymbolsTool, "Search by symbol name")]))
                .WithOutputSchema(BuildFindFilesOutputSchema()),
            async (id, args, bridge) =>
            {
                JsonObject response = await bridge.SendAsync(id, "find-files", Build(
                    (Query, OptionalString(args, Query)),
                    (Path, OptionalString(args, Path)),
                    (SearchExtensionsProperty, OptionalStringArray(args, SearchExtensionsProperty)),
                    BoolArg("include-non-project", args, "include_non_project", true, true),
                    ("max-results", OptionalText(args, "max_results")),
                    (Scope, OptionalString(args, Scope)))).ConfigureAwait(false);

                bool success = response["Success"]?.GetValue<bool>() ?? false;
                if (!success)
                {
                    bool isError = ToolResultFormatter.ShouldTreatAsError(response, defaultIsError: true);
                    return ToolResultFormatter.StructuredToolResult(response, args, isError: isError);
                }

                JsonObject data = response["Data"] as JsonObject ?? [];
                int count = data["count"]?.GetValue<int>() ?? 0;
                string query = data[Query]?.GetValue<string>() ?? OptionalString(args, Query) ?? string.Empty;
                string text = count == 0
                    ? $"find_files: no file(s) found for '{query}'. If you expected a pattern match, try glob instead."
                    : ToolResultFormatter.StructuredToolResult(response, args).AsObject()["content"]?[0]?["text"]?.GetValue<string>()
                        ?? $"find_files: found {count} file(s).";

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
                    ["isError"] = false,
                    ["structuredContent"] = StripBridgeEnvelopeFields(data),
                };
            });
    }

    private static IEnumerable<ToolEntry> TextPatternTools()
    {
        yield return BridgeTool(
            ToolDefinitionCatalog.FindText(
                ObjectSchema(
                    Req(Query, "Search text or regex pattern."),
                    Opt(Path, "Optional path or directory filter."),
                    Opt(Scope, "Scope: solution (default), project, or document."),
                    Opt(Project, ProjectFilterDesc),
                    OptBool(MatchCase, "Case-sensitive match (default false)."),
                    OptBool("whole_word", "Match whole word only (default false)."),
                    OptBool("regex", "Treat query as a regular expression (default false).")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(SearchReadFileTool, "Read the file at the matched location"), ("goto_definition", "Navigate to the definition of the match")],
                    related: [("find_text_batch", "Search multiple patterns at once"), (SearchSymbolsTool, "Search by symbol name")])),
            "find-text",
            a => Build(
                (Query, OptionalString(a, Query)),
                (Path, OptionalString(a, Path)),
                (Scope, OptionalString(a, Scope)),
                (Project, OptionalString(a, Project)),
                BoolArg("match-case", a, MatchCase, false, true),
                BoolArg("whole-word", a, "whole_word", false, true),
                BoolArg("regex", a, "regex", false, true)));

        yield return BridgeTool(
            ToolDefinitionCatalog.FindTextBatch(
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
                    Opt(Scope, "Optional scope: solution, project, document, or open."),
                    Opt(Project, ProjectFilterDesc),
                    Opt(Path, "Optional path filter."),
                    OptInt("results_window", "Optional Find Results window number."),
                    OptInt("max_queries_per_chunk", "Optional max query count per chunk (default 5)."),
                    OptBool(MatchCase, "Case-sensitive match (default false)."),
                    OptBool("whole_word", "Match whole word only (default false)."),
                    OptBool("regex", "Treat queries as regular expressions (default false).")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(SearchReadFileTool, "Read the files at matched locations"), (SearchReadFileBatchTool, "Read multiple matched files at once")],
                    related: [(SearchFindTextTool, "Search a single pattern"), (SearchSymbolsTool, "Search by symbol name")])),
            "find-text-batch",
            a => Build(
                ("queries", a?["queries"]?.ToJsonString()),
                (Scope, OptionalString(a, Scope)),
                (Project, OptionalString(a, Project)),
                (Path, OptionalString(a, Path)),
                ("results-window", OptionalText(a, "results_window")),
                ("max-queries-per-chunk", OptionalText(a, "max_queries_per_chunk")),
                BoolArg("match-case", a, MatchCase, false, true),
                BoolArg("whole-word", a, "whole_word", false, true),
                BoolArg("regex", a, "regex", false, true)));
    }

    private static IEnumerable<ToolEntry> SymbolSearchTools()
    {
        yield return BridgeTool(
            ToolDefinitionCatalog.SearchSymbols(
                ObjectSchema(
                    Req(Query, "Symbol search text."),
                    Opt("kind", "Optional symbol kind filter."),
                    Opt(Scope, "Optional scope: solution, project, document, or open."),
                    Opt(Project, ProjectFilterDesc),
                    Opt(Path, "Optional path or directory filter."),
                    OptInt(Max, "Optional max result count."),
                    OptBool(MatchCase, "Case-sensitive match (default false).")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("goto_definition", "Navigate to the found symbol"), (SearchReadFileTool, "Read the file containing the symbol"), ("find_references", "Find all usages of the symbol")],
                    related: [(SearchFileOutlineTool, "Get all symbols in a known file"), (SearchFindTextTool, "Search by text instead of symbol name")])),
            "search-symbols",
            a => Build(
                (Query, OptionalString(a, Query)),
                ("kind", OptionalString(a, "kind")),
                (Scope, OptionalString(a, Scope)),
                (Project, OptionalString(a, Project)),
                (Path, OptionalString(a, Path)),
                (Max, OptionalText(a, Max)),
                BoolArg("match-case", a, MatchCase, false, true)));
    }

    private static JsonObject BuildFindFilesOutputSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", [SearchDescriptionProperty] = "The file name or path fragment search query." },
                ["pathFilter"] = new JsonObject { ["type"] = "string", [SearchDescriptionProperty] = "Optional path fragment filter applied." },
                [SearchExtensionsProperty] = new JsonObject
                {
                    ["type"] = "array",
                    [SearchDescriptionProperty] = "File extension filters applied.",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
                ["includeNonProject"] = new JsonObject { ["type"] = "boolean", [SearchDescriptionProperty] = "Whether non-project files under solution root were included." },
                ["count"] = new JsonObject { ["type"] = "integer", [SearchDescriptionProperty] = "Number of files found." },
                ["matches"] = new JsonObject
                {
                    ["type"] = "array",
                    [SearchDescriptionProperty] = "Array of file match objects sorted by relevance score.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", [SearchDescriptionProperty] = "Absolute file system path to the file." },
                            ["name"] = new JsonObject { ["type"] = "string", [SearchDescriptionProperty] = "Just the file name component without directory." },
                            ["project"] = new JsonObject { ["type"] = "string", [SearchDescriptionProperty] = "The project unique name if the file is in a project, empty string otherwise." },
                            ["score"] = new JsonObject { ["type"] = "number", [SearchDescriptionProperty] = "Relevance score for sorting results (higher is better match)." },
                            ["source"] = new JsonObject { ["type"] = "string", [SearchDescriptionProperty] = "Source of the match: 'project' or 'disk'." },
                        },
                        ["required"] = new JsonArray { "path", "name", "project", "score", "source" },
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new JsonArray { "query", "pathFilter", SearchExtensionsProperty, "includeNonProject", "count", "matches" },
            ["additionalProperties"] = false,
        };

    // Strip bridge-internal envelope fields the infrastructure appends to every Data payload
    // (e.g. "queue" timing info) so the result passes the strict output schema validation.
    private static JsonObject StripBridgeEnvelopeFields(JsonObject data)
    {
        JsonObject clone = (JsonObject)data.DeepClone();
        clone.Remove("queue");
        return clone;
    }

    private static IEnumerable<ToolEntry> CodeExplorationTools()
    {
        yield return BridgeTool("search_solutions",
            "Search for solution files (.sln/.slnx) on disk under a given root directory. " +
            "Defaults to %USERPROFILE%\\source\\repos.",
            ObjectSchema(
                Opt(Path, "Root directory to search."),
                Opt(Query, "Filter by solution name (case-insensitive substring)."),
                OptInt("max_depth", "Max directory depth to recurse (default 6)."),
                OptInt(Max, "Max results to return (default 200).")),
            "search-solutions",
            a => Build(
                (Path, OptionalString(a, Path)),
                (Query, OptionalString(a, Query)),
                ("max-depth", OptionalText(a, "max_depth")),
                (Max, OptionalText(a, Max))),
            Search,
            searchHints: BuildSearchHints(
                workflow: [("open_solution", "Open the found solution")],
                related: [("list_instances", "List already-open instances"), ("bind_solution", "Bind to an open solution")]));

        yield return BridgeTool("smart_context",
            "First call for open-ended code exploration. " +
            "Collects focused context for a natural-language query — searches symbols, usages, and related definitions. " +
            "Prefer over read_file + find_text when you don't know exactly where to look. It is more expensive than direct read/search tools and avoids populating the VS Find Results window unless explicitly requested.",
            ObjectSchema(
                Req(Query, "Natural-language description of what you are looking for."),
                OptInt("max_contexts", "Max context blocks to return (default 3).")),
            "smart-context",
            a => Build(
                (Query, OptionalString(a, Query)),
                ("max-contexts", OptionalText(a, "max_contexts"))),
            Search,
            searchHints: BuildSearchHints(
                workflow: [(SearchReadFileTool, "Read a file from the returned context"), ("apply_diff", "Edit code after exploring")],
                related: [(SearchFindTextTool, "Search for specific text"), (SearchSymbolsTool, "Search by symbol name")]));

        yield return BridgeTool("file_symbols",
            "List symbols in one file with optional kind filtering. " +
            "More targeted than file_outline — use when you want only functions, " +
            "only classes, etc.",
            ObjectSchema(
                Req("file", FileDesc),
                Opt("kind", "Optional symbol kind filter (e.g. function, class, member).")),
            "file-symbols",
            a => Build(
                ("file", OptionalString(a, "file")),
                ("kind", OptionalString(a, "kind"))),
            Search,
            searchHints: BuildSearchHints(
                workflow: [(SearchReadFileTool, "Read the implementation of a listed symbol"), ("goto_definition", "Navigate to the symbol definition")],
                related: [(SearchFileOutlineTool, "Get the full outline with hierarchy"), (SearchSymbolsTool, "Search symbols across the solution")]));

        yield return new("glob",
            "Find files by glob pattern — use instead of Glob. " +
            "Supports **/*.cs, src/**/*.tsx, *.sln and any pattern supported by " +
            "Microsoft.Extensions.FileSystemGlobbing. Root defaults to solution directory.",
            ObjectSchema(
                Req("pattern", "Glob pattern, e.g. \"**/*.cs\", \"src/**/*.tsx\", \"*.sln\"."),
                Opt(Path, "Root directory. Defaults to solution directory."),
                OptInt(Max, "Max results (default 200).")),
            Search,
            (id, args, bridge) => SystemTools.GlobTool.ExecuteAsync(id, args, bridge),
            aliases: ["find_by_pattern", "glob_files", "list_files"],
            summary: "Find files by glob pattern — use instead of Glob.",
            readOnly: true,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [(SearchReadFileTool, "Read one of the matched files"), ("read_file_batch", "Read multiple matched files at once")],
                related: [("find_files", "Find files by name fragment"), (SearchFindTextTool, "Search file contents")]));
    }
}
