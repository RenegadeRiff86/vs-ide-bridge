using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class IdeCoreCommands
{
    internal static async Task<CommandExecutionResult> ExecuteBatchAsync(IdeCommandContext context, JArray steps, bool stopOnError)
    {
        var results = new JArray();
        var successCount = 0;
        var failureCount = 0;
        var stoppedEarly = false;

        for (var i = 0; i < steps.Count; i++)
        {
            JObject stepResult;

            if (steps[i] is not JObject step)
            {
                failureCount++;
                stepResult = new JObject
                {
                    ["index"] = i,
                    ["id"] = JValue.CreateNull(),
                    ["command"] = string.Empty,
                    ["success"] = false,
                    ["summary"] = "Batch entry must be a JSON object.",
                    ["warnings"] = new JArray(),
                    ["data"] = new JObject(),
                    ["error"] = new JObject { ["code"] = "invalid_batch_entry", ["message"] = "Batch entry must be a JSON object." },
                };
            }
            else
            {
                var stepId = (string?)step["id"];
                var commandName = (string?)step["command"] ?? string.Empty;
                var commandArgs = (string?)step["args"] ?? string.Empty;

                if (!context.Runtime.TryGetCommand(commandName, out var cmd))
                {
                    failureCount++;
                    stepResult = new JObject
                    {
                        ["index"] = i,
                        ["id"] = stepId is null ? JValue.CreateNull() : stepId,
                        ["command"] = commandName,
                        ["success"] = false,
                        ["summary"] = $"Unknown command: {commandName}",
                        ["warnings"] = new JArray(),
                        ["data"] = new JObject(),
                        ["error"] = new JObject { ["code"] = "unknown_command", ["message"] = $"Command not registered: {commandName}" },
                    };
                }
                else
                {
                    var parsedArgs = CommandArgumentParser.Parse(commandArgs);
                    try
                    {
                        var result = await cmd.ExecuteDirectAsync(context, parsedArgs).ConfigureAwait(true);
                        successCount++;
                        stepResult = new JObject
                        {
                            ["index"] = i,
                            ["id"] = stepId is null ? JValue.CreateNull() : stepId,
                            ["command"] = commandName,
                            ["success"] = true,
                            ["summary"] = result.Summary,
                            ["warnings"] = result.Warnings,
                            ["data"] = result.Data,
                            ["error"] = JValue.CreateNull(),
                        };
                    }
                    catch (CommandErrorException ex)
                    {
                        failureCount++;
                        stepResult = new JObject
                        {
                            ["index"] = i,
                            ["id"] = stepId is null ? JValue.CreateNull() : stepId,
                            ["command"] = commandName,
                            ["success"] = false,
                            ["summary"] = ex.Message,
                            ["warnings"] = new JArray(),
                            ["data"] = new JObject(),
                            ["error"] = new JObject { ["code"] = ex.Code, ["message"] = ex.Message },
                        };
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        stepResult = new JObject
                        {
                            ["index"] = i,
                            ["id"] = stepId is null ? JValue.CreateNull() : stepId,
                            ["command"] = commandName,
                            ["success"] = false,
                            ["summary"] = ex.Message,
                            ["warnings"] = new JArray(),
                            ["data"] = new JObject(),
                            ["error"] = new JObject { ["code"] = "internal_error", ["message"] = ex.Message },
                        };
                    }
                }
            }

            results.Add(stepResult);

            if (stopOnError && !(stepResult.Value<bool?>("success") ?? false))
            {
                stoppedEarly = i < steps.Count - 1;
                break;
            }
        }

        var data = new JObject
        {
            ["batchCount"] = steps.Count,
            ["successCount"] = successCount,
            ["failureCount"] = failureCount,
            ["stoppedEarly"] = stoppedEarly,
            ["results"] = results,
        };

        return new CommandExecutionResult(
            $"Batch: {successCount}/{steps.Count} succeeded, {failureCount} failed.",
            data);
    }

    private static Task<CommandExecutionResult> GetHelpResultAsync()
    {
        var canonicalCommands = new[]
        {
            "Tools.IdeGetState",
            "Tools.IdeWaitForReady",
            "Tools.IdeFindText",
            "Tools.IdeFindFiles",
            "Tools.IdeOpenDocument",
            "Tools.IdeListDocuments",
            "Tools.IdeListOpenTabs",
            "Tools.IdeActivateDocument",
            "Tools.IdeCloseDocument",
            "Tools.IdeCloseFile",
            "Tools.IdeCloseAllExceptCurrent",
            "Tools.IdeActivateWindow",
            "Tools.IdeListWindows",
            "Tools.IdeExecuteVsCommand",
            "Tools.IdeFindAllReferences",
            "Tools.IdeCountReferences",
            "Tools.IdeShowCallHierarchy",
            "Tools.IdeGetDocumentSlice",
            "Tools.IdeGetSmartContextForQuery",
            "Tools.IdeApplyUnifiedDiff",
            "Tools.IdeSetBreakpoint",
            "Tools.IdeListBreakpoints",
            "Tools.IdeRemoveBreakpoint",
            "Tools.IdeClearAllBreakpoints",
            "Tools.IdeDebugGetState",
            "Tools.IdeDebugStart",
            "Tools.IdeDebugStop",
            "Tools.IdeDebugBreak",
            "Tools.IdeDebugContinue",
            "Tools.IdeDebugStepOver",
            "Tools.IdeDebugStepInto",
            "Tools.IdeDebugStepOut",
            "Tools.IdeDebugThreads",
            "Tools.IdeDebugStack",
            "Tools.IdeDebugLocals",
            "Tools.IdeDebugModules",
            "Tools.IdeDebugWatch",
            "Tools.IdeDebugExceptions",
            "Tools.IdeDiagnosticsSnapshot",
            "Tools.IdeBuildConfigurations",
            "Tools.IdeSetBuildConfiguration",
            "Tools.IdeBuildSolution",
            "Tools.IdeGetErrorList",
            "Tools.IdeGetWarnings",
            "Tools.IdeBuildAndCaptureErrors",
            "Tools.IdeOpenSolution",
            "Tools.IdeGoToDefinition",
            "Tools.IdeGoToImplementation",
            "Tools.IdeGetFileOutline",
            "Tools.IdeGetFileSymbols",
            "Tools.IdeSearchSymbols",
            "Tools.IdeGetQuickInfo",
            "Tools.IdeGetDocumentSlices",
            "Tools.IdeEnableBreakpoint",
            "Tools.IdeDisableBreakpoint",
            "Tools.IdeEnableAllBreakpoints",
            "Tools.IdeDisableAllBreakpoints",
            "Tools.IdeBatchCommands",
        };

        var commands = new JArray();
        var legacyCommands = new JArray();
        foreach (var canonicalCommand in canonicalCommands)
        {
            commands.Add(PipeCommandNames.GetPrimaryName(canonicalCommand));
            legacyCommands.Add(canonicalCommand);
        }

        return Task.FromResult(new CommandExecutionResult(
            "Command catalog written.",
            new JObject
            {
                ["commands"] = commands,
                ["legacyCommands"] = legacyCommands,
                ["note"] = "Pipe requests accept the simple command names in commands[]. The legacy Tools.Ide* names still work in Visual Studio and over the pipe.",
                ["commandDetails"] = BuildCommandDetails(canonicalCommands),
                ["recipes"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "find-symbol-definition",
                        ["summary"] = "Use symbol search before text search when you know the identifier name.",
                        ["command"] = @"search-symbols --query ""propose_export_file_name_and_path"" --kind function"
                    },
                    new JObject
                    {
                        ["name"] = "inspect-symbol-at-location",
                        ["summary"] = "Use quick info to get the destination location and nearby definition context.",
                        ["command"] = @"quick-info --file ""C:\repo\src\foo.cpp"" --line 42 --column 13"
                    },
                    new JObject
                    {
                        ["name"] = "group-current-warnings",
                        ["summary"] = "Filter the Error List down to warnings and group them by code.",
                        ["command"] = @"warnings --group-by code"
                    },
                    new JObject
                    {
                        ["name"] = "fetch-multiple-slices",
                        ["summary"] = "Use inline ranges JSON when you need several code windows in one round-trip.",
                        ["command"] = @"document-slices --ranges ""[{\""file\"":\""C:\\repo\\src\\foo.cpp\"",\""line\"":42,\""contextBefore\"":8,\""contextAfter\"":20}]"""
                    }
                },
                ["example"] = @"state --out ""C:\temp\ide-state.json""",
                ["documentSliceExample"] = @"document-slice --file ""C:\repo\src\foo.cpp"" --start-line 120 --end-line 180 --out ""C:\temp\slice.json""",
                ["documentSlicesExample"] = @"document-slices --ranges ""[{\""file\"":\""C:\\repo\\src\\foo.cpp\"",\""line\"":42,\""contextBefore\"":8,\""contextAfter\"":20}]""",
                ["searchSymbolsExample"] = @"search-symbols --query ""propose_export_file_name_and_path"" --kind function",
                ["quickInfoExample"] = @"quick-info --file ""C:\repo\src\foo.cpp"" --line 42 --column 13",
                ["findTextPathExample"] = @"find-text --query ""OnInit"" --path ""src\libslic3r""",
                ["fileSymbolsExample"] = @"file-symbols --file ""C:\repo\src\foo.cpp"" --kind function",
                ["smartContextExample"] = @"smart-context --query ""where is GUI_App::OnInit used"" --max-contexts 3 --out ""C:\temp\smart-context.json""",
                ["referencesExample"] = @"find-references --file ""C:\repo\src\foo.cpp"" --line 42 --column 13 --out ""C:\temp\references.json""",
                ["callHierarchyExample"] = @"call-hierarchy --file ""C:\repo\src\foo.cpp"" --line 42 --column 13 --out ""C:\temp\call-hierarchy.json""",
                ["applyDiffExample"] = @"apply-diff --patch-file ""C:\temp\change.diff"" --out ""C:\temp\apply-diff.json""",
                ["openSolutionExample"] = @"open-solution --solution ""C:\path\to\solution.sln"" --out ""C:\temp\open-solution.json"""
            }));
    }

    private static JArray BuildCommandDetails(IEnumerable<string> canonicalCommands)
    {
        var details = new JArray();
        foreach (var canonicalCommand in canonicalCommands)
        {
            var pipeName = PipeCommandNames.GetPrimaryName(canonicalCommand);
            var (description, example) = GetCommandDetail(pipeName);
            details.Add(new JObject
            {
                ["name"] = pipeName,
                ["legacyName"] = canonicalCommand,
                ["description"] = description,
                ["example"] = example,
            });
        }

        return details;
    }

    private static (string Description, string Example) GetCommandDetail(string commandName)
    {
        return commandName switch
        {
            "state" => ("Capture IDE state including solution, active document, and bridge identity.", @"state --out ""C:\temp\ide-state.json"""),
            "ready" => ("Wait for Visual Studio and IntelliSense to be ready for semantic commands.", "ready --timeout-ms 120000"),
            "find-text" => ("Find text across the solution, project, or current document, with optional subtree filtering.", @"find-text --query ""OnInit"" --path ""src\libslic3r"""),
            "find-files" => ("Search solution explorer files by name or path fragment and return ranked matches.", @"find-files --query ""CMakeLists.txt"""),
            "open-document" => ("Open a document by absolute path, solution-relative path, or solution item name.", @"open-document --file ""src\CMakeLists.txt"" --line 1 --column 1"),
            "list-documents" => ("List open documents.", "list-documents"),
            "list-tabs" => ("List open editor tabs and identify the active tab.", "list-tabs"),
            "activate-document" => ("Activate an open document tab by query.", @"activate-document --query ""Program.cs"""),
            "close-document" => ("Close one or more open tabs by query.", @"close-document --query "".json"" --all"),
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
            "apply-diff" => ("Apply a unified diff through the live editor so changes are visible in Visual Studio.", @"apply-diff --patch-file ""C:\temp\change.diff"""),
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
            "build" => ("Build the current solution.", @"build --configuration Debug --platform x64"),
            "errors" => ("Capture Error List rows with optional severity and text filters.", @"errors --severity error --max 50"),
            "warnings" => ("Capture warning rows with optional code/path/project filters.", @"warnings --group-by code"),
            "build-errors" => ("Build then capture Error List rows in one call.", @"build-errors --max 200"),
            "open-solution" => ("Open a solution in the current Visual Studio instance.", @"open-solution --solution ""C:\repo\VsIdeBridge.sln"""),
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
            "batch" => ("Run multiple commands in one request.", @"batch --steps ""[{\""id\"":\""state\"",\""command\"":\""state\""}]"""),
            _ => ($"Run bridge command '{commandName}'.", commandName),
        };
    }

    private static async Task<CommandExecutionResult> GetSmokeTestResultAsync(IdeCommandContext context)
    {
        var state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
        return new CommandExecutionResult(
            "Smoke test captured IDE state.",
            new JObject
            {
                ["success"] = true,
                ["state"] = state,
            });
    }

    private static JObject GetUiSettingsData(IdeCommandContext context)
    {
        return new JObject
        {
            ["allowBridgeEdits"] = context.Runtime.UiSettings.AllowBridgeEdits,
            ["goToEditedParts"] = context.Runtime.UiSettings.GoToEditedParts,
        };
    }

    private static Task<CommandExecutionResult> ToggleAllowBridgeEditsAsync(IdeCommandContext context)
    {
        var enabled = !context.Runtime.UiSettings.AllowBridgeEdits;
        context.Runtime.UiSettings.AllowBridgeEdits = enabled;
        return Task.FromResult(new CommandExecutionResult(
            enabled ? "Bridge edits enabled." : "Bridge edits disabled.",
            GetUiSettingsData(context)));
    }

    private static Task<CommandExecutionResult> ToggleGoToEditedPartsAsync(IdeCommandContext context)
    {
        var enabled = !context.Runtime.UiSettings.GoToEditedParts;
        context.Runtime.UiSettings.GoToEditedParts = enabled;
        return Task.FromResult(new CommandExecutionResult(
            enabled ? "Go To Edited Parts enabled." : "Go To Edited Parts disabled.",
            GetUiSettingsData(context)));
    }

    private static string? TryResolveReadmePath(IdeCommandContext context)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var solutionPath = context.Dte.Solution?.FullName;
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                return null;
            }

            var solutionDirectory = Path.GetDirectoryName(solutionPath);
            if (string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return null;
            }

            var readmePath = Path.Combine(solutionDirectory, "README.md");
            return File.Exists(readmePath) ? readmePath : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CommandExecutionResult> ShowHelpMenuAsync(IdeCommandContext context)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var readmePath = TryResolveReadmePath(context);
        if (!string.IsNullOrWhiteSpace(readmePath))
        {
            context.Dte.ItemOperations.OpenFile(readmePath);
        }

        var message = string.IsNullOrWhiteSpace(readmePath)
            ? "Use the Command Window with Tools.IdeHelp for the full command catalog. The README could not be resolved from the current solution."
            : $"Opened README: {readmePath}{Environment.NewLine}{Environment.NewLine}Use the Command Window with Tools.IdeHelp for the full command catalog.";

        VsShellUtilities.ShowMessageBox(
            context.Package,
            message,
            "VS IDE Bridge",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

        return new CommandExecutionResult(
            string.IsNullOrWhiteSpace(readmePath) ? "Displayed IDE Bridge help." : "Opened IDE Bridge help.",
            new JObject
            {
                ["readmePath"] = readmePath is null ? JValue.CreateNull() : readmePath,
                ["commandWindowHelp"] = "Tools.IdeHelp",
            });
    }

    internal sealed class IdeHelpMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0102, acceptsParameters: false)
    {
        protected override string CanonicalName => "Tools.VsIdeBridgeHelpMenu";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return ShowHelpMenuAsync(context);
        }
    }

    internal sealed class IdeToggleAllowBridgeEditsMenuCommand : IdeCommandBase
    {
        public IdeToggleAllowBridgeEditsMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0103, acceptsParameters: false)
        {
            MenuCommand.BeforeQueryStatus += (_, _) => MenuCommand.Checked = Runtime.UiSettings.AllowBridgeEdits;
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeToggleAllowBridgeEdits";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return ToggleAllowBridgeEditsAsync(context);
        }
    }

    internal sealed class IdeToggleGoToEditedPartsMenuCommand : IdeCommandBase
    {
        public IdeToggleGoToEditedPartsMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0104, acceptsParameters: false)
        {
            MenuCommand.BeforeQueryStatus += (_, _) => MenuCommand.Checked = Runtime.UiSettings.GoToEditedParts;
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeToggleGoToEditedParts";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return ToggleGoToEditedPartsAsync(context);
        }
    }

    internal sealed class IdeHelpCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0100)
    {
        protected override string CanonicalName => "Tools.IdeHelp";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return GetHelpResultAsync();
        }
    }

    internal sealed class IdeSmokeTestCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0101)
    {
        protected override string CanonicalName => "Tools.IdeSmokeTest";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return await GetSmokeTestResultAsync(context).ConfigureAwait(true);
        }
    }

    internal sealed class IdeGetStateCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0200)
    {
        protected override string CanonicalName => "Tools.IdeGetState";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("IDE state captured.", state);
        }
    }

    internal sealed class IdeWaitForReadyCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0201)
    {
        protected override string CanonicalName => "Tools.IdeWaitForReady";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var timeout = args.GetInt32("timeout-ms", 120000);
            var data = await context.Runtime.ReadinessService.WaitForReadyAsync(context, timeout).ConfigureAwait(true);
            return new CommandExecutionResult("Readiness wait completed.", data);
        }
    }

    internal sealed class IdeOpenSolutionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0224)
    {
        protected override string CanonicalName => "Tools.IdeOpenSolution";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var solutionPath = args.GetRequiredString("solution");
            if (!File.Exists(solutionPath))
            {
                throw new CommandErrorException("file_not_found", $"Solution file not found: {solutionPath}");
            }
            var ext = Path.GetExtension(solutionPath);
            if (!string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandErrorException("invalid_file_type", $"File is not a solution file: {solutionPath}");
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            context.Dte.Solution.Open(solutionPath);
            return new CommandExecutionResult("Solution opened.", new JObject { ["solutionPath"] = solutionPath });
        }
    }

    internal sealed class IdeCloseIdeCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0231)
    {
        private const int CloseIdeDelayMilliseconds = 300;

        protected override string CanonicalName => "Tools.IdeCloseIde";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            // Schedule quit after the response is written to the pipe
            _ = Task.Run(async () =>
            {
                await Task.Delay(CloseIdeDelayMilliseconds).ConfigureAwait(false);
                await context.Package.JoinableTaskFactory.SwitchToMainThreadAsync();
                context.Dte.Quit();
            });

            return Task.FromResult(new CommandExecutionResult(
                "Closing IDE.",
                new JObject { ["closing"] = true }));
        }
    }

    internal sealed class IdeBatchCommandsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0225)
    {
        protected override string CanonicalName => "Tools.IdeBatchCommands";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var batchFile = args.GetRequiredString("batch-file");
            if (!File.Exists(batchFile))
            {
                throw new CommandErrorException("file_not_found", $"Batch file not found: {batchFile}");
            }

            var json = File.ReadAllText(batchFile);
            JArray steps;
            try
            {
                steps = JArray.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse batch file: {ex.Message}");
            }

            var stopOnError = args.GetBoolean("stop-on-error", false);
            return await ExecuteBatchAsync(context, steps, stopOnError).ConfigureAwait(true);
        }
    }
}
