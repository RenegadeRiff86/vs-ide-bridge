using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Commands;

internal static class IdeCoreCommands
{
    private const string WarningsCommandName = "warnings";
    private const string ExampleCppPath = @"C:\repo\src\foo.cpp";
    private const string WarningsCommandExample = WarningsCommandName + " --group-by code";
    private const string ExampleLineNumber = "42";
    private const string ExampleLineColumnSuffix = " --line " + ExampleLineNumber + " --column 13";

    private static string CreateExampleFileCommand(string commandName)
    {
        return commandName + " --file \"" + ExampleCppPath + "\"";
    }

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
                        ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
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
                            ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
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
                            ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
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
                            ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
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
        var commandMetadata = BridgeCommandCatalog.All
            .OrderBy(item => item.PipeName, StringComparer.Ordinal)
            .ToArray();
        var generatedAtUtc = DateTime.UtcNow.ToString("O");
        var commandDetails = BuildCommandDetails(commandMetadata);

        var commands = new JArray();
        var legacyCommands = new JArray();
        foreach (var command in commandMetadata)
        {
            commands.Add(command.PipeName);
            legacyCommands.Add(command.CanonicalName);
        }

        return Task.FromResult(new CommandExecutionResult(
            "Command catalog written.",
            new JObject
            {
                ["schemaVersion"] = "vs-ide-bridge.help.v1",
                ["generatedAtUtc"] = generatedAtUtc,
                ["catalog"] = new JObject
                {
                    ["schemaVersion"] = "vs-ide-bridge.command-catalog.v1",
                    ["generatedAtUtc"] = generatedAtUtc,
                    ["count"] = commandMetadata.Length,
                    ["commands"] = commandDetails.DeepClone(),
                    ["nameField"] = "name",
                    ["canonicalNameField"] = "canonicalName",
                    ["exampleField"] = "example",
                    ["aliasesField"] = "aliases",
                    ["notes"] = new JArray
                    {
                        "Use name for pipe/MCP command routing.",
                        "Use canonicalName only for compatibility mapping or VS Command Window fallbacks.",
                    },
                },
                ["commands"] = commands,
                ["legacyCommands"] = legacyCommands,
                ["note"] = "Pipe requests accept the simple command names in commands[]. The legacy Tools.Ide* names still work in Visual Studio and over the pipe.",
                ["commandDetails"] = commandDetails,
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
                        ["command"] = CreateExampleFileCommand("quick-info") + ExampleLineColumnSuffix
                    },
                    new JObject
                    {
                        ["name"] = "group-current-warnings",
                        ["summary"] = "Filter the Error List down to warnings and group them by code.",
                        ["command"] = WarningsCommandExample
                    },
                    new JObject
                    {
                        ["name"] = "fetch-multiple-slices",
                        ["summary"] = "Use inline ranges JSON when you need several code windows in one round-trip.",
                        ["command"] = @"document-slices --ranges ""[{\""file\"":\""C:\\repo\\src\\foo.cpp\"",\""line\"":42,\""contextBefore\"":8,\""contextAfter\"":20}]"""
                    }
                },
                ["example"] = @"state --out ""C:\temp\ide-state.json""",
                ["documentSliceExample"] = CreateExampleFileCommand("document-slice") + " --start-line 120 --end-line 180 --out \"C:\\temp\\slice.json\"",
                ["documentSlicesExample"] = @"document-slices --ranges ""[{\""file\"":\""C:\\repo\\src\\foo.cpp\"",\""line\"":42,\""contextBefore\"":8,\""contextAfter\"":20}]""",
                ["searchSymbolsExample"] = @"search-symbols --query ""propose_export_file_name_and_path"" --kind function",
                ["quickInfoExample"] = CreateExampleFileCommand("quick-info") + ExampleLineColumnSuffix,
                ["findTextPathExample"] = @"find-text --query ""OnInit"" --path ""src\libslic3r""",
                ["fileSymbolsExample"] = @"file-symbols --file ""C:\repo\src\foo.cpp"" --kind function",
                ["smartContextExample"] = @"smart-context --query ""where is GUI_App::OnInit used"" --max-contexts 3 --out ""C:\temp\smart-context.json""",
                ["referencesExample"] = CreateExampleFileCommand("find-references") + ExampleLineColumnSuffix + " --out \"C:\\temp\\references.json\"",
                ["callHierarchyExample"] = CreateExampleFileCommand("call-hierarchy") + ExampleLineColumnSuffix + " --out \"C:\\temp\\call-hierarchy.json\"",
                ["applyDiffFormat"] = "Use unified diff text with ---/+++ file headers and @@ hunks, or editor patch text with *** Begin Patch / *** End Patch and *** Update File / *** Add File / *** Delete File blocks.",
                ["applyDiffExample"] = @"apply-diff --patch-file ""C:\temp\change.diff"" --out ""C:\temp\apply-diff.json""",
                ["openSolutionExample"] = @"open-solution --solution ""C:\path\to\solution.sln"" --out ""C:\temp\open-solution.json"""
            }));
    }

    private static JArray BuildCommandDetails(IEnumerable<BridgeCommandMetadata> commandMetadata)
    {
        var details = new JArray();
        foreach (var command in commandMetadata)
        {
            var aliases = new JArray();
            foreach (var alias in PipeCommandNames.GetAliases(command.CanonicalName))
            {
                if (string.Equals(alias, command.PipeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                aliases.Add(alias);
            }

            details.Add(new JObject
            {
                ["name"] = command.PipeName,
                ["canonicalName"] = command.CanonicalName,
                ["legacyName"] = command.CanonicalName,
                ["description"] = command.Description,
                ["example"] = command.Example,
                ["aliases"] = aliases,
            });
        }

        return details;
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
            ["bestPracticeDiagnostics"] = context.Runtime.UiSettings.BestPracticeDiagnosticsEnabled,
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

    private static async Task<CommandExecutionResult> ToggleBestPracticeDiagnosticsAsync(IdeCommandContext context)
    {
        var enabled = !context.Runtime.UiSettings.BestPracticeDiagnosticsEnabled;
        context.Runtime.UiSettings.BestPracticeDiagnosticsEnabled = enabled;
        await context.Runtime.ErrorListService.RefreshBestPracticeDiagnosticsAsync(context).ConfigureAwait(true);
        return new CommandExecutionResult(
            enabled ? "Best practice diagnostics enabled." : "Best practice diagnostics disabled.",
            GetUiSettingsData(context));
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
                ["readmePath"] = (JToken?)readmePath ?? JValue.CreateNull(),
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

    internal sealed class IdeToggleBestPracticeDiagnosticsMenuCommand : IdeCommandBase
    {
        public IdeToggleBestPracticeDiagnosticsMenuCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0105, acceptsParameters: false)
        {
            MenuCommand.BeforeQueryStatus += (_, _) => MenuCommand.Checked = Runtime.UiSettings.BestPracticeDiagnosticsEnabled;
        }

        protected override string CanonicalName => "Tools.VsIdeBridgeToggleBestPracticeDiagnostics";

        internal override bool AllowAutomationInvocation => false;

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            return ToggleBestPracticeDiagnosticsAsync(context);
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



