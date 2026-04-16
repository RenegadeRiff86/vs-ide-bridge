using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    public static ToolDefinition ApplyDiff(JsonObject parameterSchema)
        => CreateMutatingTool(
            "apply_diff",
            "documents",
            "Apply the default targeted edit patch for one or multiple files.",
            "Apply an editor patch to code files through the live editor. Use *** Begin Patch / *** Update File: path / @@ / -old / +new / *** End Patch. " +
            "Use this as the default in-solution edit tool after you inspect the target with read_file, search_symbols, find_text, or file_outline. " +
            "Supports *** Add File, *** Delete File, and *** Update File blocks. Multiple files apply atomically, and changed files open automatically. " +
            "Do not send unified diff headers like --- or +++.",
            parameterSchema,
            bridgeCommand: "apply-diff",
            title: "Apply Diff",
            aliases: ["apply_patch", "patch_file", "patch_code"],
            tags: ["edit", "patch", "diff", "code", "file"],
            destructive: true);

    public static ToolDefinition WriteFile(JsonObject parameterSchema)
        => CreateMutatingTool(
            "write_file",
            "documents",
            "Replace one file through the editor.",
            "Write or overwrite a file through the live editor. This REPLACES the entire file contents; it does not append or preserve omitted text. Prefer apply_diff for targeted edits, and use write_file only when creating a new file or intentionally replacing the whole file with complete content.",
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
            "Take one code slice from a file. Use search_symbols, find_text, file_outline, or peek_definition first to narrow what you need. For in-solution edits, use this before apply_diff so the patch targets the current code. Use start_line/end_line for a range, or line with context_before/context_after for an anchor. For multiple slices use read_file_batch.",
            parameterSchema,
            bridgeCommand: "document-slice",
            title: "Read File Slice",
            aliases: ["read_code", "read_source", "open_file_slice"],
            tags: ["code", NavigationTag, "read", "file", "slice"]);

    public static ToolDefinition ReadFileBatch(JsonObject parameterSchema)
        => CreateReadOnlyTool(
            "read_file_batch",
            "search",
            "Read several file slices.",
            "Take multiple code slices in one bridge request. Use search_symbols, find_text, file_outline, or peek_definition first, then use this instead of repeated read_file calls when you need several slices before apply_diff or a larger refactor.",
            parameterSchema,
            bridgeCommand: "document-slices",
            title: "Read File Slices",
            aliases: ["read_code_batch", "read_source_batch", "open_file_slices"],
            tags: ["code", NavigationTag, "read", "file", "slice", "batch"]);
}
