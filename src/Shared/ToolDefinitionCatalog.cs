using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static class ToolDefinitionCatalog
{
    public static ToolDefinition ListTools(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tools",
            "system",
            "List all tools.",
            "Return every tool in a compact scan-friendly format with category and safety flags.",
            parameterSchema,
            tags: ["discovery", "catalog", "list"]);

    public static ToolDefinition ListToolCategories(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tool_categories",
            "system",
            "List tool categories.",
            "Return the available tool categories, counts, and highlighted navigation tools.",
            parameterSchema,
            tags: ["discovery", "categories", "catalog"]);

    public static ToolDefinition ListToolsByCategory(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "list_tools_by_category",
            "system",
            "List tools in one category.",
            "Return the tools for one category in the same compact discovery format.",
            parameterSchema,
            tags: ["discovery", "category", "catalog"]);

    public static ToolDefinition RecommendTools(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "recommend_tools",
            "system",
            "Recommend tools for a task.",
            "Accept a natural-language task and return the best matching tools with short reasons.",
            parameterSchema,
            tags: ["discovery", "recommendation", "task"]);

    public static ToolDefinition ApplyDiff(JsonObject parameterSchema)
        => CreateMutatingTool(
            "apply_diff",
            "documents",
            "Patch code through the editor.",
            "Apply an editor patch or unified diff through the live Visual Studio editor. Prefer this over write_file for targeted edits after using search_symbols, peek_definition, or read_file.",
            parameterSchema,
            bridgeCommand: "apply-diff",
            title: "Apply Editor Patch",
            aliases: ["apply_patch", "patch_file", "patch_code"],
            tags: ["edit", "patch", "diff", "code", "file"],
            destructive: true);

    public static ToolDefinition WriteFile(JsonObject parameterSchema)
        => CreateMutatingTool(
            "write_file",
            "documents",
            "Write one file through the editor.",
            "Write or overwrite a file through the live editor. Prefer apply_diff for targeted edits, and use this when a new file or large replacement makes patching impractical.",
            parameterSchema,
            bridgeCommand: "write-file",
            title: "Write File",
            aliases: ["create_file", "overwrite_file", "replace_file"],
            tags: ["edit", "write", "file", "create", "replace"],
            destructive: true);

    public static ToolDefinition ReadFile(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file",
            "search",
            "Read one file slice.",
            "Take one code slice from a file. Use search_symbols, find_text, file_outline, or peek_definition first to narrow what you need. Use start_line/end_line for a range, or line with context_before/context_after for an anchor. For multiple slices use read_file_batch.",
            parameterSchema,
            bridgeCommand: "document-slice",
            title: "Read File Slice",
            aliases: ["read_code", "read_source", "open_file_slice"],
            tags: ["code", "navigation", "read", "file", "slice"]);

    public static ToolDefinition ReadFileBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file_batch",
            "search",
            "Read several file slices.",
            "Take multiple code slices in one bridge request. Use search_symbols, find_text, file_outline, or peek_definition first, then use this instead of repeated read_file calls when you need several slices.",
            parameterSchema,
            bridgeCommand: "document-slices",
            title: "Read File Slices",
            aliases: ["read_code_batch", "read_source_batch", "open_file_slices"],
            tags: ["code", "navigation", "read", "file", "slice", "batch"]);

    public static ToolDefinition FindFiles(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_files",
            "search",
            "Search files by name or path.",
            "Search Solution Explorer-style files by name or path fragment and return ranked matches. Use this when you want the Solution Explorer search behavior before broader read_file or find_text calls.",
            parameterSchema,
            bridgeCommand: "find-files",
            title: "Solution Explorer File Search",
            aliases: ["solution_explorer_search", "search_solution_explorer", "find_solution_file", "search_files", "find_file_by_name"],
            tags: ["code", "navigation", "files", "path", "solution", "explorer"]);

    public static ToolDefinition FindText(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_text",
            "search",
            "Search text in code.",
            "Full-text search for a single query. Use find_files to narrow by file/path and search_symbols for definitions before broad reading. For multiple queries use find_text_batch.",
            parameterSchema,
            bridgeCommand: "find-text",
            title: "Text Search",
            aliases: ["search_text", "text_search", "grep_text"],
            tags: ["code", "navigation", "search", "text"]);

    public static ToolDefinition FindTextBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_text_batch",
            "search",
            "Search many text queries.",
            "Find text for multiple queries in one bridge round-trip, internally chunked when needed. Pair with find_files or search_symbols first when you already have a likely target area.",
            parameterSchema,
            bridgeCommand: "find-text-batch",
            title: "Batched Text Search",
            aliases: ["search_text_batch", "text_search_batch", "grep_text_batch"],
            tags: ["code", "navigation", "search", "text", "batch"]);

    public static ToolDefinition SearchSymbols(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "search_symbols",
            "search",
            "Search symbol definitions.",
            "Search symbol definitions by name across solution scope. Prefer this before read_file when you are locating code flow or definition boundaries.",
            parameterSchema,
            bridgeCommand: "search-symbols",
            aliases: ["find_symbol", "find_symbols", "symbol_search"],
            tags: ["code", "navigation", "symbols", "definition"]);

    public static ToolDefinition Errors(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "errors",
            "diagnostics",
            "Read current Error List.",
            "Read current Error List diagnostics without triggering a build. After edits or builds, prefer wait_for_ready first and use build_errors when you need a fresh build plus Error List snapshot.",
            parameterSchema,
            bridgeCommand: "errors",
            title: "Error List Diagnostics",
            aliases: ["error_list", "diagnostics", "list_errors"],
            tags: ["diagnostics", "errors", "build", "warnings"]);

    public static ToolDefinition FileOutline(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "file_outline",
            "search",
            "List file symbols.",
            "Get the symbol outline of a file. Use this after find_files or before read_file when you want the shape of a file without scanning the whole body.",
            parameterSchema,
            bridgeCommand: "file-outline",
            aliases: ["document_outline", "outline_file", "list_file_symbols"],
            tags: ["code", "navigation", "outline", "symbols", "file"]);

    public static ToolDefinition SymbolInfo(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "symbol_info",
            "search",
            "Get symbol details at a location.",
            "Resolve symbol information at file/line/column with surrounding context.",
            parameterSchema,
            bridgeCommand: "quick-info",
            aliases: ["quick_info", "get_symbol_info", "symbol_details"],
            tags: ["code", "navigation", "symbol", "info"]);

    public static ToolDefinition PeekDefinition(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "peek_definition",
            "search",
            "Read a definition without navigating.",
            "Return the full definition source and surrounding context of the symbol at file/line/column without navigating the editor. Prefer this before broader read_file calls when you are following symbol flow.",
            parameterSchema,
            bridgeCommand: "peek-definition",
            aliases: ["get_definition", "read_definition", "definition_peek"],
            tags: ["code", "navigation", "definition", "peek"]);

    public static ToolDefinition FindReferences(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "find_references",
            "search",
            "Find references to a symbol.",
            "Run Find All References for the symbol at file/line/column.",
            parameterSchema,
            bridgeCommand: "find-references",
            aliases: ["references", "find_symbol_references", "search_references"],
            tags: ["code", "navigation", "references", "symbol"]);

    private static ToolDefinition CreateMutatingTool(
        string name,
        string category,
        string summary,
        string description,
        JsonObject parameterSchema,
        string? bridgeCommand = null,
        string? title = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        bool destructive = false)
    {
        return CreateTool(
            name,
            category,
            summary,
            description,
            parameterSchema,
            readOnly: false,
            mutating: true,
            destructive: destructive,
            bridgeCommand: bridgeCommand,
            title: title,
            aliases: aliases,
            tags: tags);
    }

    private static ToolDefinition CreateReadOnlyTool(
        string name,
        string category,
        string summary,
        string description,
        JsonObject parameterSchema,
        string? bridgeCommand = null,
        string? title = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null)
    {
        return CreateTool(
            name,
            category,
            summary,
            description,
            parameterSchema,
            readOnly: true,
            mutating: false,
            destructive: false,
            bridgeCommand: bridgeCommand,
            title: title,
            aliases: aliases,
            tags: tags);
    }

    private static ToolDefinition CreateTool(
        string name,
        string category,
        string summary,
        string description,
        JsonObject parameterSchema,
        bool readOnly,
        bool mutating,
        bool destructive,
        string? bridgeCommand = null,
        string? title = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null)
    {
        return new ToolDefinition(
            name,
            category,
            summary,
            description,
            parameterSchema,
            readOnly: readOnly,
            mutating: mutating,
            destructive: destructive,
            aliases: aliases,
            tags: tags,
            bridgeCommand: bridgeCommand,
            title: title);
    }
}
