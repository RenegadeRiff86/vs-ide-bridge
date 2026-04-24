namespace VsIdeBridge.Shared;

public sealed partial class ToolRegistry
{
    private static readonly string[] DefaultRecommendedNavigationToolNames =
    [
        "find_files",
        "glob",
        "search_symbols",
        "call_hierarchy",
        "read_file",
        "peek_definition",
        "find_text",
        "find_references",
        "errors",
    ];

    private static readonly string[] DefaultRecommendedEditToolNames =
    [
        "apply_diff",
        "write_file",
    ];

    private static readonly string[] DefaultRecommendedBuildToolNames =
    [
        "build",
        "build_errors",
        "errors",
        "build_configurations",
        "set_build_configuration",
    ];

    private static readonly string[] DefaultRecommendedDiscoveryToolNames =
    [
        "recommend_tools",
        "list_tools",
        "list_tool_categories",
        "list_tools_by_category",
        "list_instances",
        "bind_instance",
        "bind_solution",
        "tool_help",
    ];

    public static IReadOnlyList<ToolCategoryDefinition> DefaultCategoryDefinitions { get; } =
    [
        new("core", "Session and binding", "Connection, binding, state, and always-load basics."),
        new("search", "Code navigation", "Find text, read code, inspect symbols, and trace references before apply_diff edits."),
        new("diagnostics", "Errors and build", "Errors, warnings, build output, and diagnostics snapshots."),
        new("documents", "Editor and files", "Open, close, save, patch with apply_diff first, and manage files and windows."),
        new("debug", "Debugger inspection", "Breakpoints, stacks, locals, watches, threads, and modules."),
        new("git", "Version control", "Bridge-managed Git and GitHub helpers, with fallback paths where needed."),
        new("python", "Python runtime", "Python environments, packages, and stateless scratchpad tools."),
        new("project", "Projects and solutions", "Projects, references, outputs, NuGet, and solution structure."),
        new("system", "Discovery and host", "Tool discovery, host control, and last-resort process execution."),
    ];

    public static IReadOnlyList<string> FeaturedToolNames { get; } =
    [
        "find_files",
        "glob",
        "find_text",
        "find_text_batch",
        "search_symbols",
        "call_hierarchy",
        "read_file",
        "apply_diff",
        "capture_vs_window",
        "read_file_batch",
        "file_outline",
        "symbol_info",
        "peek_definition",
        "find_references",
        "errors",
        "write_file",
        "recommend_tools",
        "list_instances",
    ];
}
