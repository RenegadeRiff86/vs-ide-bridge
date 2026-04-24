using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string FindReferencesTool = "find_references";
    private const string GotoDefinitionTool = "goto_definition";
    private const string SemanticReadFileTool = "read_file";

    private static IEnumerable<ToolEntry> SemanticTools()
        =>
        SemanticStructureTools()
            .Concat(ReferenceAndHierarchyTools())
            .Concat(DefinitionNavigationTools());

    private static IEnumerable<ToolEntry> SemanticStructureTools()
    {
        yield return BridgeTool(
            ToolDefinitionCatalog.FileOutline(
                ObjectSchema(Req(FileArg, FileDesc)))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(SemanticReadFileTool, "Read the full implementation of a symbol"), (GotoDefinitionTool, "Navigate to a symbol's definition"), (FindReferencesTool, "Find all usages of a symbol")],
                    related: [(SearchSymbolsTool, "Search symbols across the solution"), ("find_files", "Locate likely files first"), ("file_symbols", "List symbols filtered by kind")])),
            "file-outline",
            a => Build((FileArg, OptionalString(a, FileArg))));

        yield return BridgeTool(
            ToolDefinitionCatalog.SymbolInfo(
                ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(GotoDefinitionTool, "Navigate to the symbol's definition"), (FindReferencesTool, "Find all usages of the symbol")],
                    related: [("peek_definition", "Peek at the definition inline"), (SearchSymbolsTool, "Search matching symbols across the solution"), ("file_outline", "See all symbols in the file")])),
            "quick-info",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))));
    }

    private static IEnumerable<ToolEntry> ReferenceAndHierarchyTools()
    {
        yield return BridgeTool(
            ToolDefinitionCatalog.FindReferences(
                ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(SemanticReadFileTool, "Read a file containing a usage"), (GotoDefinitionTool, "Navigate to the definition")],
                    related:
                    [
                        ("count_references", "Get the exact reference count"),
                        ("call_hierarchy", "Explore the caller tree"),
                        (SearchSymbolsTool, "Search related symbol definitions"),
                    ])),
            "find-references",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))));

        yield return BridgeTool("count_references",
            "Run Find All References and return the exact count. This can take longer than direct read/search tools.",
            ObjectSchema(
                Req(FileArg, FileDesc),
                ReqInt(Line, LineDesc),
                ReqInt(Column, ColumnDesc),
                OptBool("activate_window", "Activate references window while counting (default true)."),
                OptInt("timeout_ms", "Optional window wait timeout in milliseconds.")),
            "count-references",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column)),
                BoolArg("activate-window", a, "activate_window", true, true),
                ("timeout-ms", OptionalText(a, "timeout_ms"))),
            Search,
            searchHints: BuildSearchHints(
                related: [(FindReferencesTool, "Get the full list of usages"), ("call_hierarchy", "Explore the caller tree"), (SearchSymbolsTool, "Search related symbol definitions")]));

        yield return BridgeTool("call_hierarchy",
            "Open Call Hierarchy for the symbol at a file/line/column and return a recursive caller tree — who calls this, and who calls those callers. Use this when you need to understand call chains, trace propagation paths, or find all entry points that can reach a symbol. For managed languages the tree is returned directly in the result. This can take longer than direct read/search tools.",
            ObjectSchema(
                Req(FileArg, FileDesc),
                ReqInt(Line, LineDesc),
                ReqInt(Column, ColumnDesc),
                OptInt("max_depth", "Optional caller tree depth for managed-language hierarchy capture (default 2)."),
                OptInt("max_children", "Optional max caller nodes per hierarchy level (default 20)."),
                OptInt("max_locations_per_caller", "Optional max call-site locations to include per caller (default 5).")),
            "call-hierarchy",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column)),
                ("max-depth", OptionalText(a, "max_depth")),
                ("max-children", OptionalText(a, "max_children")),
                ("max-locations-per-caller", OptionalText(a, "max_locations_per_caller"))),
            Search,
            searchHints: BuildSearchHints(
                workflow: [(FindReferencesTool, "Get all usages"), (SemanticReadFileTool, "Read a caller's implementation")],
                related: [(GotoDefinitionTool, "Navigate to the symbol's definition"), ("count_references", "Count total usages"), (SearchSymbolsTool, "Search related symbol definitions"), ("smart_context", "Broader open-ended exploration of how a symbol fits in the codebase")]));
    }

    private static IEnumerable<ToolEntry> DefinitionNavigationTools()
    {
        yield return BridgeTool("goto_definition",
            "Navigate to the definition of the symbol at a file/line/column.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)),
            "goto-definition",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))),
            Search,
            searchHints: BuildSearchHints(
                workflow: [(SemanticReadFileTool, "Read the definition"), ("file_outline", "See all symbols in the definition file"), (FindReferencesTool, "Find all usages")],
                related: [("goto_implementation", "Navigate to an implementation"), ("peek_definition", "Peek inline without navigating"), (SearchSymbolsTool, "Search matching symbol definitions"), ("call_hierarchy", "See the recursive caller tree - who calls this symbol")]));

        yield return BridgeTool("goto_implementation",
            "Navigate to an implementation of the symbol at a file/line/column.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)),
            "goto-implementation",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))),
            Search,
            searchHints: BuildSearchHints(
                workflow: [(SemanticReadFileTool, "Read the implementation"), (FindReferencesTool, "Find all usages of the implementation")],
                related: [(GotoDefinitionTool, "Navigate to the definition instead"), ("peek_definition", "Peek inline without navigating"), (SearchSymbolsTool, "Search matching symbol definitions")]));

        yield return BridgeTool(
            ToolDefinitionCatalog.PeekDefinition(
                ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc), ReqInt(Column, ColumnDesc)))
                .WithSearchHints(BuildSearchHints(
                    workflow: [(SemanticReadFileTool, "Read the full definition file"), (FindReferencesTool, "Find all usages")],
                    related: [(GotoDefinitionTool, "Navigate to the definition"), ("symbol_info", "Get type and documentation info"), (SearchSymbolsTool, "Search matching symbol definitions")])),
            "peek-definition",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))));
    }
}
