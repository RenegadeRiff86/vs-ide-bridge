using System;
using System.Collections.Generic;
using System.Linq;

namespace VsIdeBridge.Shared;

public sealed class BridgeCommandMetadata
{
    public string CanonicalName { get; set; } = string.Empty;

    public string PipeName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Example { get; set; } = string.Empty;
}

public static class BridgeCommandCatalog
{
    private static readonly BridgeCommandMetadata[] Commands = Build();
    private static readonly Dictionary<string, BridgeCommandMetadata> ByPipeName = Commands
        .ToDictionary(item => item.PipeName, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BridgeCommandMetadata> ByCanonicalName = Commands
        .ToDictionary(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase);

    static BridgeCommandCatalog()
    {
        var errors = Validate().ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                $"Bridge command metadata is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }
    }

    public static IReadOnlyList<BridgeCommandMetadata> All => Commands;

    public static bool TryGetByPipeName(string? pipeName, out BridgeCommandMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            var key = pipeName!;
            if (ByPipeName.TryGetValue(key, out metadata!))
            {
                return true;
            }
        }

        metadata = null!;
        return false;
    }

    public static bool TryGetByCanonicalName(string? canonicalName, out BridgeCommandMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(canonicalName))
        {
            var key = canonicalName!;
            if (ByCanonicalName.TryGetValue(key, out metadata!))
            {
                return true;
            }
        }

        metadata = null!;
        return false;
    }

    public static IEnumerable<string> Validate()
    {
        var errors = new List<string>();
        var duplicatePipe = Commands
            .GroupBy(item => item.PipeName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        foreach (var item in duplicatePipe)
        {
            errors.Add($"duplicate pipe command name: {item}");
        }

        var duplicateCanonical = Commands
            .GroupBy(item => item.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        foreach (var item in duplicateCanonical)
        {
            errors.Add($"duplicate canonical command name: {item}");
        }

        foreach (var command in Commands)
        {
            if (string.IsNullOrWhiteSpace(command.CanonicalName))
            {
                errors.Add("missing canonical command name");
            }

            if (string.IsNullOrWhiteSpace(command.PipeName))
            {
                errors.Add($"missing pipe command name for canonical '{command.CanonicalName}'");
            }

            if (string.IsNullOrWhiteSpace(command.Description))
            {
                errors.Add($"missing description for '{command.PipeName}'");
            }

            if (string.IsNullOrWhiteSpace(command.Example))
            {
                errors.Add($"missing example for '{command.PipeName}'");
            }
        }

        return errors;
    }

    private static BridgeCommandMetadata[] Build()
    {
        return
        [
            Create("Tools.IdeHelp", "help"),
            Create("Tools.IdeSmokeTest", "smoke-test"),
            Create("Tools.IdeGetState", "state"),
            Create("Tools.IdeWaitForReady", "ready"),
            Create("Tools.IdeOpenSolution", "open-solution"),
            Create("Tools.IdeCloseIde", "close-ide"),
            Create("Tools.IdeBatchCommands", "batch"),
            Create("Tools.IdeFindText", "find-text"),
            Create("Tools.IdeFindFiles", "find-files"),
            Create("Tools.IdeOpenDocument", "open-document"),
            Create("Tools.IdeListDocuments", "list-documents"),
            Create("Tools.IdeListOpenTabs", "list-tabs"),
            Create("Tools.IdeActivateDocument", "activate-document"),
            Create("Tools.IdeCloseDocument", "close-document"),
            Create("Tools.IdeSaveDocument", "save-document"),
            Create("Tools.IdeCloseFile", "close-file"),
            Create("Tools.IdeCloseAllExceptCurrent", "close-others"),
            Create("Tools.IdeActivateWindow", "activate-window"),
            Create("Tools.IdeListWindows", "list-windows"),
            Create("Tools.IdeExecuteVsCommand", "execute-command"),
            Create("Tools.IdeFindAllReferences", "find-references"),
            Create("Tools.IdeCountReferences", "count-references"),
            Create("Tools.IdeShowCallHierarchy", "call-hierarchy"),
            Create("Tools.IdeGetDocumentSlice", "document-slice"),
            Create("Tools.IdeGetSmartContextForQuery", "smart-context"),
            Create("Tools.IdeApplyUnifiedDiff", "apply-diff"),
            Create("Tools.IdeSetBreakpoint", "set-breakpoint"),
            Create("Tools.IdeListBreakpoints", "list-breakpoints"),
            Create("Tools.IdeRemoveBreakpoint", "remove-breakpoint"),
            Create("Tools.IdeClearAllBreakpoints", "clear-breakpoints"),
            Create("Tools.IdeDebugGetState", "debug-state"),
            Create("Tools.IdeDebugStart", "debug-start"),
            Create("Tools.IdeDebugStop", "debug-stop"),
            Create("Tools.IdeDebugBreak", "debug-break"),
            Create("Tools.IdeDebugContinue", "debug-continue"),
            Create("Tools.IdeDebugStepOver", "debug-step-over"),
            Create("Tools.IdeDebugStepInto", "debug-step-into"),
            Create("Tools.IdeDebugStepOut", "debug-step-out"),
            Create("Tools.IdeDebugThreads", "debug-threads"),
            Create("Tools.IdeDebugStack", "debug-stack"),
            Create("Tools.IdeDebugLocals", "debug-locals"),
            Create("Tools.IdeDebugModules", "debug-modules"),
            Create("Tools.IdeDebugWatch", "debug-watch"),
            Create("Tools.IdeDebugExceptions", "debug-exceptions"),
            Create("Tools.IdeDiagnosticsSnapshot", "diagnostics-snapshot"),
            Create("Tools.IdeBuildConfigurations", "build-configurations"),
            Create("Tools.IdeSetBuildConfiguration", "set-build-configuration"),
            Create("Tools.IdeBuildSolution", "build"),
            Create("Tools.IdeGetErrorList", "errors"),
            Create("Tools.IdeGetWarnings", "warnings"),
            Create("Tools.IdeBuildAndCaptureErrors", "build-errors"),
            Create("Tools.IdeGoToDefinition", "goto-definition"),
            Create("Tools.IdeGoToImplementation", "goto-implementation"),
            Create("Tools.IdeGetFileOutline", "file-outline"),
            Create("Tools.IdeGetFileSymbols", "file-symbols"),
            Create("Tools.IdeSearchSymbols", "search-symbols"),
            Create("Tools.IdeGetQuickInfo", "quick-info"),
            Create("Tools.IdeGetDocumentSlices", "document-slices"),
            Create("Tools.IdeEnableBreakpoint", "enable-breakpoint"),
            Create("Tools.IdeDisableBreakpoint", "disable-breakpoint"),
            Create("Tools.IdeEnableAllBreakpoints", "enable-all-breakpoints"),
            Create("Tools.IdeDisableAllBreakpoints", "disable-all-breakpoints"),
        ];
    }

    private static BridgeCommandMetadata Create(string canonicalName, string pipeName)
    {
        var (description, example) = GetCommandDetail(pipeName);
        return new BridgeCommandMetadata
        {
            CanonicalName = canonicalName,
            PipeName = pipeName,
            Description = description,
            Example = example,
        };
    }

    private static (string Description, string Example) GetCommandDetail(string commandName)
    {
        return commandName switch
        {
            "help" => ("Return bridge command catalog metadata and usage examples.", "help"),
            "smoke-test" => ("Capture smoke-test IDE state to verify bridge command execution.", "smoke-test"),
            "state" => ("Capture IDE state including solution, active document, and bridge identity.", @"state --out ""C:\temp\ide-state.json"""),
            "ready" => ("Wait for Visual Studio and IntelliSense to be ready for semantic commands.", "ready --timeout-ms 120000"),
            "open-solution" => ("Open a solution in the current Visual Studio instance.", @"open-solution --solution ""C:\repo\VsIdeBridge.sln"""),
            "close-ide" => ("Close the current Visual Studio instance through DTE Quit.", "close-ide"),
            "batch" => ("Run multiple commands in one request.", @"batch --steps ""[{\""id\"":\""state\"",\""command\"":\""state\""}]"""),
            "find-text" => ("Find text across the solution, project, or current document, with optional subtree filtering.", @"find-text --query ""OnInit"" --path ""src\libslic3r"""),
            "find-files" => ("Search solution explorer files by name or path fragment and return ranked matches.", @"find-files --query ""CMakeLists.txt"""),
            "open-document" => ("Open a document by absolute path, solution-relative path, or solution item name.", @"open-document --file ""src\CMakeLists.txt"" --line 1 --column 1"),
            "list-documents" => ("List open documents.", "list-documents"),
            "list-tabs" => ("List open editor tabs and identify the active tab.", "list-tabs"),
            "activate-document" => ("Activate an open document tab by query.", @"activate-document --query ""Program.cs"""),
            "close-document" => ("Close one or more open tabs by query.", @"close-document --query "".json"" --all"),
            "save-document" => ("Save one document by path or save all open documents.", @"save-document --file ""C:\repo\src\foo.cpp"""),
            "close-file" => ("Close one open file tab by path or query.", @"close-file --file ""C:\repo\src\foo.cpp"""),
            "close-others" => ("Close all tabs except the active tab.", "close-others"),
            "activate-window" => ("Activate a Visual Studio tool window by caption or kind.", @"activate-window --window ""Error List"""),
            "list-windows" => ("List Visual Studio tool windows, optionally filtered by query.", @"list-windows --query ""Error"""),
            "execute-command" => ("Execute an arbitrary Visual Studio command with optional arguments.", @"execute-command --name ""Edit.FormatDocument"""),
            "find-references" => ("Run Find All References for the symbol at a file/line/column.", @"find-references --file ""C:\repo\src\foo.cpp"" --line 42 --column 13"),
            "count-references" => ("Run Find All References and return exact count when Visual Studio exposes one, or explicit unknown otherwise.", @"count-references --file ""C:\repo\src\foo.cpp"" --line 42 --column 13"),
            "call-hierarchy" => ("Open Call Hierarchy for the symbol at a file/line/column.", @"call-hierarchy --file ""C:\repo\src\foo.cpp"" --line 42 --column 13"),
            "document-slice" => ("Fetch one code slice from a file.", @"document-slice --file ""C:\repo\src\foo.cpp"" --line 120 --context-before 8 --context-after 20"),
            "smart-context" => ("Collect focused code context for a natural-language query.", @"smart-context --query ""where is GUI_App::OnInit used"" --max-contexts 3"),
            "apply-diff" => ("Apply a unified diff through the live editor so changes are visible in Visual Studio. Changed files open by default.", @"apply-diff --patch-file ""C:\temp\change.diff"""),
            "set-breakpoint" => ("Set a breakpoint at file/line with optional condition and hit count.", @"set-breakpoint --file ""C:\repo\src\foo.cpp"" --line 42"),
            "list-breakpoints" => ("List current breakpoints.", "list-breakpoints"),
            "remove-breakpoint" => ("Remove breakpoints by file/line, id, or all.", @"remove-breakpoint --file ""C:\repo\src\foo.cpp"" --line 42"),
            "clear-breakpoints" => ("Clear all breakpoints.", "clear-breakpoints"),
            "debug-state" => ("Get debugger mode and active stack frame info.", "debug-state"),
            "debug-start" => ("Start debugging the current startup project.", "debug-start"),
            "debug-stop" => ("Stop the debugger.", "debug-stop"),
            "debug-break" => ("Break execution in the debugger.", "debug-break"),
            "debug-continue" => ("Continue execution in the debugger.", "debug-continue"),
            "debug-step-over" => ("Step over the current line in the debugger.", "debug-step-over"),
            "debug-step-into" => ("Step into the current call in the debugger.", "debug-step-into"),
            "debug-step-out" => ("Step out of the current function in the debugger.", "debug-step-out"),
            "debug-threads" => ("List debugger threads for the active debug session.", "debug-threads"),
            "debug-stack" => ("Capture stack frames for the current or selected debugger thread.", "debug-stack --thread-id 1 --max-frames 50"),
            "debug-locals" => ("Capture local variables for the active stack frame.", "debug-locals --max 200"),
            "debug-modules" => ("Capture debugger module snapshot (best effort by debugger engine).", "debug-modules"),
            "debug-watch" => ("Evaluate one debugger watch expression in break mode.", @"debug-watch --expression ""count"""),
            "debug-exceptions" => ("Capture debugger exception group/settings snapshot (best effort).", "debug-exceptions"),
            "diagnostics-snapshot" => ("Aggregate IDE state, debugger state, build state, and current errors/warnings.", "diagnostics-snapshot --wait-for-intellisense true"),
            "build-configurations" => ("List available solution build configurations and platforms.", "build-configurations"),
            "set-build-configuration" => ("Activate one build configuration/platform pair.", "set-build-configuration --configuration Debug --platform x64"),
            "build" => ("Build the current solution.", "build --configuration Debug --platform x64"),
            "errors" => ("Capture Error List rows with optional severity and text filters.", "errors --severity error --max 50"),
            "warnings" => ("Capture warning rows with optional code/path/project filters.", "warnings --group-by code"),
            "build-errors" => ("Build then capture Error List rows in one call.", "build-errors --max 200"),
            "goto-definition" => ("Navigate to the definition of the symbol at a file/line/column.", @"goto-definition --file ""C:\repo\src\foo.cpp"" --line 42 --column 13"),
            "goto-implementation" => ("Navigate to one implementation of the symbol at a file/line/column.", @"goto-implementation --file ""C:\repo\src\foo.cpp"" --line 42 --column 13"),
            "file-outline" => ("List a file outline from the code model.", @"file-outline --file ""C:\repo\src\foo.cpp"""),
            "file-symbols" => ("List symbols in one file with optional kind filtering.", @"file-symbols --file ""C:\repo\src\foo.cpp"" --kind function"),
            "search-symbols" => ("Search symbol definitions by name across solution scope.", @"search-symbols --query ""RunAsync"" --kind function --path ""src\VsIdeBridgeCli"""),
            "quick-info" => ("Resolve symbol information at file/line/column with surrounding context.", @"quick-info --file ""C:\repo\src\foo.cpp"" --line 42 --column 13"),
            "document-slices" => ("Fetch multiple code slices from --ranges-file or inline --ranges JSON.", @"document-slices --ranges ""[{\""file\"":\""C:\\repo\\src\\foo.cpp\"",\""line\"":42,\""contextBefore\"":8,\""contextAfter\"":20}]"""),
            "enable-breakpoint" => ("Enable a breakpoint by id or file/line.", @"enable-breakpoint --file ""C:\repo\src\foo.cpp"" --line 42"),
            "disable-breakpoint" => ("Disable a breakpoint by id or file/line.", @"disable-breakpoint --file ""C:\repo\src\foo.cpp"" --line 42"),
            "enable-all-breakpoints" => ("Enable all breakpoints.", "enable-all-breakpoints"),
            "disable-all-breakpoints" => ("Disable all breakpoints.", "disable-all-breakpoints"),
            _ => ($"Run bridge command '{commandName}'.", commandName),
        };
    }
}
