using System.Text;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;
using VsIdeBridgeService.Diagnostics;
using VsIdeBridgeService.SystemTools;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string ApplyDiffTool = "apply_diff";
    private const string ReadFileTool = "read_file";
    private const string ListTabsTool = "list_tabs";

    private static string EncodeUtf8ToBase64(string? text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));
    }

    /// <summary>
    /// Resolves a relative path to an existing file by searching upward from
    /// <paramref name="solutionDir"/>. When multiple candidates exist (e.g., both
    /// <c>src/foo.cpp</c> and <c>build/src/foo.cpp</c>), source-tree paths are
    /// preferred over build-artifact paths, matching the DocumentService behavior.
    /// Falls back to <c>Path.Combine(solutionDir, fileArg)</c> when no candidate exists.
    /// </summary>
    private static string ResolveExistingFilePath(string fileArg, string solutionDir)
    {
        if (System.IO.Path.IsPathRooted(fileArg))
            return fileArg;

        string normalizedArg = fileArg.Replace('/', System.IO.Path.DirectorySeparatorChar);
        List<string> candidates = [];
        string current = solutionDir;
        for (int depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            string candidate = System.IO.Path.Combine(current, normalizedArg);
            if (File.Exists(candidate))
                candidates.Add(candidate);
            string? parent = System.IO.Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        if (candidates.Count == 0)
            return System.IO.Path.Combine(solutionDir, normalizedArg);

        return candidates
            .OrderByDescending(p => ContainsPathSegment(p, "src"))
            .ThenBy(p => ContainsPathSegment(p, "build") || ContainsPathSegment(p, "bin") || ContainsPathSegment(p, "obj"))
            .ThenBy(p => p.Length)
            .First();
    }

    private static bool ContainsPathSegment(string path, string segment)
    {
        string sep = System.IO.Path.DirectorySeparatorChar.ToString();
        string norm = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        return norm.Contains(sep + segment + sep, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildApplyDiffArgs(JsonObject? args)
    {
        return Build(
            ("patch-text-base64", EncodeUtf8ToBase64(OptionalString(args, "diff"))),
            ("open-changed-files", "true"),
            ("save-changed-files", "true"));
    }

    private static string BuildWriteFileArgs(JsonObject? args)
    {
        return Build(
            (FileArg, OptionalString(args, FileArg)),
            ("content-base64", EncodeUtf8ToBase64(OptionalString(args, "content"))));
    }

    private static IEnumerable<ToolEntry> DocumentTools()
        =>
        DocumentEditTools()
            .Concat(DocumentTabTools())
            .Concat(WindowCommandTools())
            .Concat(FileOperationTools())
            .Concat(SolutionSystemTools());

    private static IEnumerable<ToolEntry> DocumentEditTools()
    {
        yield return new(
            ToolDefinitionCatalog.ApplyDiff(
                ObjectSchema(
                    Req("diff", "Editor patch text. Required format:\n" +
                        "*** Begin Patch\\n" +
                        "*** Update File: path/to/file.cs\\n" +
                        "@@\\n" +
                        " context\\n" +
                        "-old line\\n" +
                        "+new line\\n" +
                        " context\\n" +
                        "*** End Patch\n" +
                        "Supports: *** Add File, *** Delete File, *** Update File.\n" +
                        "Multi-file: repeat file blocks before *** End Patch (all changes are atomic).\n" +
                        "Use this format only; do not send unified diff headers like --- / +++."),
                    OptBool(PostCheck,
                        "Queue a quick diagnostics refresh after applying (default false).")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("reload_document", "Reload the file so VS picks up the changes"), ("errors", "Check for diagnostics after applying")],
                    related: [("write_file", "Overwrite the full file instead"), (ReadFileTool, "Read the file first to understand its current state")])),
            async (id, args, bridge) =>
            {
                JsonObject result = await bridge.SendAsync(
                    id,
                    "apply-diff",
                    BuildApplyDiffArgs(args))
                    .ConfigureAwait(false);

                if (ArgBuilder.OptionalBool(args, PostCheck, false))
                {
                    result["postCheck"] = RunDocumentPostCheck(bridge, ApplyDiffTool);
                }

                return BridgeResult(result);
            });

        yield return new(
            ToolDefinitionCatalog.WriteFile(
                ObjectSchema(
                    Req(FileArg, FileDesc),
                    Req("content", "Full UTF-8 text content to write. This replaces the entire file; omitted text is removed."),
                    OptBool(PostCheck, "Queue a quick diagnostics refresh after writing (default false).")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("reload_document", "Reload the file so VS picks up the changes"), ("errors", "Check for diagnostics after writing")],
                    related: [(ApplyDiffTool, "Apply targeted changes instead of replacing the whole file"), (ReadFileTool, "Read the current file contents first")])),
            async (id, args, bridge) =>
            {
                JsonObject result = await bridge.SendAsync(id, "write-file", BuildWriteFileArgs(args))
                    .ConfigureAwait(false);

                if (ArgBuilder.OptionalBool(args, PostCheck, false))
                {
                    result["postCheck"] = RunDocumentPostCheck(bridge, "write_file");
                }

                return BridgeResult(result);
            });
    }

    private static IEnumerable<ToolEntry> DocumentTabTools()
    {
        yield return BridgeTool("open_file",
            "Open a document by unique filename, solution-relative path, absolute path, or solution item name.",
            ObjectSchema(
                Req(FileArg, FileDesc),
                OptInt(Line, "Optional 1-based line number to navigate to."),
                OptInt(Column, "Optional 1-based column number.")),
            "open-document",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column))),
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the file contents"), ("file_outline", "Get the file symbol structure")],
                related: [("activate_document", "Switch to an already-open tab"), ("find_files", "Find the file path first")]));

        yield return BridgeTool("close_file",
            "Close one editor tab by exact FileArg path (preferred) or caption query. Use when you have the FileArg path.",
            ObjectSchema(Opt(FileArg, "FileArg path to close."), Opt(Query, "Tab caption query.")),
            "close-file",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Query, OptionalString(a, Query))),
            searchHints: BuildSearchHints(
                related: [("close_document", "Close by caption query"), ("close_others", "Close all except active"), (ListTabsTool, "List open tabs first")]));

        yield return BridgeTool("close_document",
            "Close editor tabs matching a caption/name query. Use all: true to close all matching tabs (e.g. all .json files).",
            ObjectSchema(Req(Query, "Tab caption query."), OptBool("all", "Close all matching tabs.")),
            "close-document",
            a => Build(
                (Query, OptionalString(a, Query)),
                BoolArg("all", a, "all", false, true)),
            searchHints: BuildSearchHints(
                related: [("close_file", "Close by exact path"), ("close_others", "Close all except active"), (ListTabsTool, "List open tabs first")]));

        yield return BridgeTool("close_others",
            "Close all tabs except the active tab.",
            ObjectSchema(OptBool("save", "Save before closing (default false).")),
            "close-others",
            a => Build(BoolArg("save", a, "save", false, true)),
            searchHints: BuildSearchHints(
                related: [("close_file", "Close a specific tab"), (ListTabsTool, "See what is open")]));

        yield return BridgeTool("save_document",
            "Save one document by path or save all open documents.",
            ObjectSchema(Opt(FileArg, "FileArg to save. Omit to save all.")),
            "save-document",
            a => Build((FileArg, OptionalString(a, FileArg))),
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check diagnostics after saving")],
                related: [("reload_document", "Reload after external changes"), (ApplyDiffTool, "Apply changes before saving")]));

        yield return BridgeTool("reload_document",
            "Reload a document from disk — required after native Edit/Write tool changes. VS does not auto-detect external writes. Call after every .cs edit, then check errors.",
            ObjectSchema(Req(FileArg, FileDesc)),
            "reload-document",
            a => Build((FileArg, OptionalString(a, FileArg))),
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check for diagnostics after reloading")],
                related: [("save_document", "Save before reloading"), (ApplyDiffTool, "Apply changes that need reloading")]));

        yield return BridgeTool("list_documents",
            "List open documents.",
            EmptySchema(), "list-documents", _ => Empty(), Documents,
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read one of the listed documents"), ("activate_document", "Switch to a document")],
                related: [(ListTabsTool, "List open editor tabs")]));

        yield return BridgeTool(ListTabsTool,
            "List open editor tabs and identify the active tab.",
            EmptySchema(), "list-tabs", _ => Empty(), Documents,
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read one of the listed files"), ("activate_document", "Switch to a tab")],
                related: [("list_documents", "List open documents")]));

        yield return BridgeTool("activate_document",
            "Activate an open document tab by query.",
            ObjectSchema(Req(Query, "FileArg name or tab caption fragment.")),
            "activate-document",
            a => Build((Query, OptionalString(a, Query))),
            searchHints: BuildSearchHints(
                workflow: [(ReadFileTool, "Read the activated file"), ("file_outline", "Get the file structure")],
                related: [("open_file", "Open a file that is not yet open"), (ListTabsTool, "List available tabs")]));
    }

    private static IEnumerable<ToolEntry> WindowCommandTools()
    {
        yield return BridgeTool("list_windows",
            "List Visual Studio tool windows (Solution Explorer, Error List, Output, etc.).",
            ObjectSchema(Opt(Query, "Optional caption filter.")),
            "list-windows",
            a => Build((Query, OptionalString(a, Query))),
            searchHints: BuildSearchHints(
                workflow: [("activate_window", "Bring a window to the foreground")],
                related: [("list_tabs", "List editor tabs")]));

        yield return BridgeTool("activate_window",
            "Bring a Visual Studio tool window to the foreground by caption fragment.",
            ObjectSchema(Req("window", "Window caption fragment.")),
            "activate-window",
            a => Build(("window", OptionalString(a, "window"))),
            searchHints: BuildSearchHints(
                related: [("list_windows", "List available windows"), ("execute_command", "Run a VS command")]));

        yield return BridgeTool("execute_command",
            "Execute an arbitrary Visual Studio command with optional arguments.",
            ObjectSchema(
                Req("command", "Visual Studio command name (e.g. Edit.FormatDocument)."),
                Opt("args", "Optional command arguments string."),
                Opt(FileArg, FileDesc),
                Opt("document", "Optional open-document query to position before running."),
                OptInt(Line, "Optional 1-based line number."),
                OptInt(Column, "Optional 1-based column number."),
                OptBool("select_word", "If true, select the word at the caret before executing.")),
            "execute-command",
            a => Build(
                ("command", OptionalString(a, "command")),
                ("args", OptionalString(a, "args")),
                (FileArg, OptionalString(a, FileArg)),
                ("document", OptionalString(a, "document")),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column)),
                BoolArg("select-word", a, "select_word", false, true)),
            searchHints: BuildSearchHints(
                related: [("format_document", "Format a document"), ("shell_exec", "Run an external process")]));

        yield return BridgeTool("format_document",
            "Format the current document or a specific FileArg.",
            ObjectSchema(
                Opt(FileArg, FileDesc),
                OptInt(Line, "Optional 1-based line."),
                OptInt(Column, "Optional 1-based column.")),
            "execute-command",
            a =>
            {
                string? file = OptionalString(a, FileArg);
                if (string.IsNullOrWhiteSpace(file))
                {
                    return Build(("name", "Edit.FormatDocument"));
                }

                return Build(
                    ("name", "Edit.FormatDocument"),
                    (FileArg, file),
                    (Line, OptionalText(a, Line)),
                    (Column, OptionalText(a, Column)));
            },
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check diagnostics after formatting")],
                related: [("execute_command", "Run other VS commands"), ("apply_diff", "Make targeted edits")]));
    }

    private static IEnumerable<ToolEntry> FileOperationTools()
    {
        yield return new("delete_file",
            "Delete a FileArg from disk and close its editor tab. " +
            "SDK-style projects auto-update when a FileArg disappears from disk. " +
            "For legacy .csproj files use remove_file_from_project first.",
            ObjectSchema(
                Req(FileArg, "Absolute or solution-relative FileArg path to delete.")),
            Documents,
            async (id, args, bridge) =>
            {
                string fileArg = args?[FileArg]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fileArg))
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument 'FileArg'.");

                string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                string resolvedPath = ResolveExistingFilePath(fileArg, solutionDir);

                // Close in editor first (best-effort)
                try
                {
                    await bridge.SendAsync(id, "close-file", Build((FileArg, resolvedPath)))
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // best-effort: ignore close errors before delete
                }
                catch (InvalidOperationException)
                {
                    // best-effort: ignore close errors before delete
                }
                catch (McpRequestException)
                {
                    // best-effort: ignore close errors before delete
                }

                System.IO.File.Delete(resolvedPath);

                JsonObject delPayload = new()
                {
                    ["deleted"] = true,
                    ["path"] = resolvedPath,
                };
                return ToolResultFormatter.StructuredToolResult(delPayload, args, successText: $"Deleted file '{resolvedPath}'.");
            },
            destructive: true,
            searchHints: BuildSearchHints(
                related: [("remove_file_from_project", "Remove from project first for legacy .csproj"), ("copy_file", "Copy instead of delete")]));

        yield return new("copy_file",
            "Copy a file to a new location on disk, creating parent directories as needed.",
            ObjectSchema(
                Req("source", "Absolute or solution-relative source path."),
                Req("destination", "Absolute or solution-relative destination path."),
                OptBool("overwrite", "Overwrite destination if it already exists (default false).")),
            Documents,
            async (id, args, bridge) =>
            {
                string sourceArg = args?["source"]?.GetValue<string>() ?? string.Empty;
                string destArg = args?["destination"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sourceArg))
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument 'source'.");
                if (string.IsNullOrWhiteSpace(destArg))
                    throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument 'destination'.");

                bool overwrite = args?["overwrite"]?.GetValue<bool?>() ?? false;
                string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                string resolvedSource = ResolveExistingFilePath(sourceArg, solutionDir);
                string resolvedDest = System.IO.Path.IsPathRooted(destArg) ? destArg : System.IO.Path.Combine(solutionDir, destArg);

                string? destDir = System.IO.Path.GetDirectoryName(resolvedDest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                System.IO.File.Copy(resolvedSource, resolvedDest, overwrite);

                JsonObject copyPayload = new()
                {
                    ["copied"] = true,
                    ["source"] = resolvedSource,
                    ["destination"] = resolvedDest,
                };
                return ToolResultFormatter.StructuredToolResult(copyPayload, args,
                    successText: $"Copied '{resolvedSource}' to '{resolvedDest}'.");
            },
            mutating: true,
            searchHints: BuildSearchHints(
                related: [("delete_file", "Delete the original after copying"), ("add_file_to_project", "Add the copied file to a project")]));
    }

    private static IEnumerable<ToolEntry> SolutionSystemTools()
    {
        yield return new("open_solution",
            "Open a specific existing .sln or .slnx file in the current Visual Studio instance.",
            ObjectSchema(
                Req("solution", "Absolute path to the .sln or .slnx file."),
                OptBool("wait_for_ready", "Wait for readiness after opening (default true).")),
            "documents",
            (id, args, bridge) => OpenSolutionAsync(id, args, bridge),
            searchHints: BuildSearchHints(
                workflow: [("wait_for_ready", "Wait for IntelliSense to load"), ("list_projects", "Inspect the loaded projects")],
                related: [("vs_open", "Launch a new VS instance"), ("bind_solution", "Bind to an already-open solution"), ("search_solutions", "Find the solution path")]));

        yield return BridgeTool("create_solution",
            "Create and open a new solution in the current Visual Studio instance.",
            ObjectSchema(
                Req("directory", "Absolute directory where the solution should be created."),
                Req("name", "Solution name ('.sln' is optional.)")),
            "create-solution",
            a => Build(
                ("directory", OptionalString(a, "directory")),
                ("name", OptionalString(a, "name"))),
            searchHints: BuildSearchHints(
                workflow: [("create_project", "Add a project to the new solution"), ("list_projects", "Inspect the solution structure")],
                related: [("open_solution", "Open an existing solution")]));

        yield return new("vs_close",
            "Close a Visual Studio instance by process id, or the currently bound instance.",
            ObjectSchema(
                OptInt("process_id", "Process ID of the VS instance to close. Defaults to bound instance."),
                OptBool("force", "Kill the process instead of gracefully closing (default false).")),
            "system",
            (id, args, bridge) => VsCloseAsync(id, args, bridge),
            searchHints: BuildSearchHints(
                related: [("vs_open", "Launch a VS instance"), ("bridge_health", "Check binding health")]));

        yield return new("shell_exec",
            "Execute an arbitrary external process and capture stdout, stderr, and exit code. " +
            "Use for build scripts, test runners, package tools, and CLI utilities. " +
            "Prefer named tools for common operations: git_* for version control, " +
            "build / build_errors for compilation, delete_file / copy_file for FileArg operations. " +
            "Working directory defaults to the solution directory.",
            ObjectSchema(
                Req("exe", "Executable path or name (e.g. 'powershell', 'cmd', 'ISCC.exe')."),
                Opt("args", "Arguments string to pass to the executable."),
                Opt("cwd", "Working directory."),
                OptInt("timeout_ms", "Timeout in milliseconds (default 60000)."),
                OptInt("head_lines", "If set, include only the first N lines of stdout and stderr."),
                OptInt("tail_lines", "If set, include only the last N lines of stdout and stderr. Combine with head_lines to see both ends of long output."),
                OptInt("max_lines", "Max total lines per stream when head_lines/tail_lines are not set (default 200).")),
            "system",
            (id, args, bridge) => ShellExecTool.ExecuteAsync(id, args, bridge),
            searchHints: BuildSearchHints(
                related: [("execute_command", "Run a VS command instead"), ("build", "Use the build tool for compilation"), ("git_status", "Use git tools for version control")]));

        yield return new("set_version",
            "Update the version string across all version files in the solution.",
            ObjectSchema(
                Req("version", "New version string (e.g. 2.1.0).")),
            "system",
            (id, args, bridge) => SetVersionTool.ExecuteAsync(id, args, bridge),
            searchHints: BuildSearchHints(
                workflow: [("build", "Rebuild after changing the version"), ("errors", "Check for version-related errors")],
                related: [("shell_exec", "Run custom versioning scripts")]));
    }

    private static async Task<JsonNode> OpenSolutionAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string solutionPath = System.IO.Path.GetFullPath(OptionalString(args, "solution")
            ?? throw new McpRequestException(id, McpErrorCodes.InvalidParams, "'solution' is required."));

        if (!File.Exists(solutionPath))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, $"Solution file not found: {solutionPath}");
        }

        string extension = System.IO.Path.GetExtension(solutionPath);
        if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, $"File is not a solution file: {solutionPath}");
        }

        IReadOnlyList<BridgeInstance> instances = await VsDiscovery.ListAsync(bridge.Mode).ConfigureAwait(false);
        BridgeInstance[] matchingInstances = [.. instances.Where(instance =>
            !string.IsNullOrWhiteSpace(instance.SolutionPath)
            && string.Equals(System.IO.Path.GetFullPath(instance.SolutionPath), solutionPath, StringComparison.OrdinalIgnoreCase))];

        if (matchingInstances.Length == 1)
        {
            JsonObject bindResult = await bridge.BindAsync(id, new JsonObject { ["instance_id"] = matchingInstances[0].InstanceId }).ConfigureAwait(false);
            bindResult["alreadyOpen"] = true;
            bindResult["solutionPath"] = solutionPath;

            if (OptionalBool(args, "wait_for_ready", true))
            {
                JsonObject ready = await bridge.SendAsync(id, "ready", []).ConfigureAwait(false);
                bindResult["ready"] = ready["Data"]?.DeepClone();
            }

            return ToolResultFormatter.StructuredToolResult(bindResult, args, successText: "Bound to already-open solution.");
        }

        if (matchingInstances.Length > 1)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Multiple live VS IDE Bridge instances already have '{solutionPath}' open. Call bind_instance with a specific instance_id.");
        }

        if (bridge.CurrentInstance is null)
        {
            throw new McpRequestException(id, McpErrorCodes.BridgeError,
                "No live bound VS IDE Bridge instance is available to open the solution. Open Visual Studio with the VS IDE Bridge extension installed, then call bind_solution or bind_instance first.");
        }

        string openArgs = Build(
            ("solution", solutionPath),
            BoolArg("wait-for-ready", args, "wait_for_ready", true, true));
        JsonObject response = await bridge.SendIgnoringSolutionHintAsync(id, "open-solution", openArgs).ConfigureAwait(false);
        return BridgeResult(response);
    }

    private static JsonObject RunDocumentPostCheck(BridgeConnection bridge, string sourceTool)
    {
        JsonObject snapshot = bridge.DocumentDiagnostics.QueueRefreshAndGetSnapshot(sourceTool);
        JsonObject? errorsData = snapshot["errors"]?["Data"]?.AsObject();
        bool hasErrors = errorsData?["hasErrors"]?.GetValue<bool>() ?? false;
        int errorCount = errorsData?["severityCounts"]?["Error"]?.GetValue<int>() ?? 0;
        JsonObject result = new()
        {
            ["mode"] = "background-refresh",
            ["hasErrors"] = hasErrors,
            ["errorCount"] = errorCount,
        };
        if (hasErrors)
            result["errors"] = errorsData?["rows"]?.DeepClone();
        return result;
    }

}
