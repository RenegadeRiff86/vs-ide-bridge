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

internal static partial class IdeCoreCommands
{
    internal static async Task<CommandExecutionResult> ExecuteBatchAsync(IdeCommandContext context, JArray steps, bool stopOnError)
    {
        JArray results = [];
        int successCount = 0;
        int failureCount = 0;
        bool stoppedEarly = false;

        for (int i = 0; i < steps.Count; i++)
        {
            (JObject stepResult, bool succeeded) = await ExecuteBatchStepAsync(context, steps[i], i).ConfigureAwait(true);
            if (succeeded) successCount++; else failureCount++;
            results.Add(stepResult);

            if (stopOnError && !(stepResult.Value<bool?>("success") ?? false))
            {
                stoppedEarly = i < steps.Count - 1;
                break;
            }
        }

        JObject commandData = new()
        {
            ["batchCount"] = steps.Count,
            ["successCount"] = successCount,
            ["failureCount"] = failureCount,
            ["stoppedEarly"] = stoppedEarly,
            ["results"] = results,
        };

        return new CommandExecutionResult(
            $"Batch: {successCount}/{steps.Count} succeeded, {failureCount} failed.",
            commandData);
    }

    private static JArray ParseBatchSteps(string json, string sourceDescription)
    {
        try
        {
            return JArray.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CommandErrorException("invalid_json", $"Failed to parse {sourceDescription}: {ex.Message}");
        }
    }

    private static JArray LoadBatchSteps(CommandArguments args)
    {
        string? inlineSteps = args.GetString("steps");
        if (!string.IsNullOrWhiteSpace(inlineSteps))
        {
            return ParseBatchSteps(inlineSteps!, "batch steps");
        }

        string? batchFile = args.GetString("batch-file");
        if (string.IsNullOrWhiteSpace(batchFile))
        {
            throw new CommandErrorException("invalid_arguments", "Missing required argument --steps or --batch-file.");
        }

        if (!File.Exists(batchFile))
        {
            throw new CommandErrorException("file_not_found", $"Batch file not found: {batchFile}");
        }

        return ParseBatchSteps(File.ReadAllText(batchFile), "batch file");
    }

    private static async Task<(JObject result, bool succeeded)> ExecuteBatchStepAsync(
        IdeCommandContext context, JToken entry, int index)
    {
        if (entry is not JObject step)
        {
            JObject stepResult = new()
            {
                ["index"] = index,
                ["id"] = JValue.CreateNull(),
                ["command"] = string.Empty,
                ["success"] = false,
                ["summary"] = "Batch entry must be a JSON object.",
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = "invalid_batch_entry", ["message"] = "Batch entry must be a JSON object." },
            };
            return (stepResult, false);
        }

        string? stepId = (string?)step["id"];
        string commandName = (string?)step["command"] ?? string.Empty;
        JToken? commandArgs = step["args"];

        if (!context.Runtime.TryGetCommand(commandName, out var cmd))
        {
            JObject stepResult = new()
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = false,
                ["summary"] = $"Unknown command: {commandName}",
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = "unknown_command", ["message"] = $"Command not registered: {commandName}" },
            };
            return (stepResult, false);
        }

        CommandArguments parsedArgs = CommandArgumentParser.Parse(commandArgs);
        try
        {
            CommandExecutionResult commandResult = await cmd.ExecuteDirectAsync(context, parsedArgs).ConfigureAwait(true);
            JObject stepResult = new()
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = true,
                ["summary"] = commandResult.Summary,
                [WarningsPropertyName] = commandResult.Warnings,
                ["data"] = commandResult.Data,
                ["error"] = JValue.CreateNull(),
            };
            return (stepResult, true);
        }
        catch (CommandErrorException ex)
        {
            JObject stepResult = new()
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = false,
                ["summary"] = ex.Message,
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = ex.Code, ["message"] = ex.Message },
            };
            return (stepResult, false);
        }
        catch (Exception ex) when (ex is not null) // batch step dispatcher: all unexpected exceptions are captured per-step
        {
            JObject stepResult = new()
            {
                ["index"] = index,
                ["id"] = (JToken?)stepId ?? JValue.CreateNull(),
                ["command"] = commandName,
                ["success"] = false,
                ["summary"] = ex.Message,
                [WarningsPropertyName] = new JArray(),
                ["data"] = new JObject(),
                ["error"] = new JObject { ["code"] = "internal_error", ["message"] = ex.Message },
            };
            return (stepResult, false);
        }
    }

    private static Task<CommandExecutionResult> GetHelpResultAsync()
    {
        BridgeCommandMetadata[] commandMetadata = [..BridgeCommandCatalog.All
            .OrderBy(item => item.PipeName, StringComparer.Ordinal)
            ];
        string generatedAtUtc = DateTime.UtcNow.ToString("O");
        JArray commandDetails = BuildCommandDetails(commandMetadata);

        JArray commands = [];
        JArray legacyCommands = [];
        foreach (BridgeCommandMetadata command in commandMetadata)
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
                        ["command"] = CreateExampleCommand("search-symbols", ("query", new JValue("propose_export_file_name_and_path")), ("kind", new JValue("function")))
                    },
                    new JObject
                    {
                        ["name"] = "inspect-symbol-at-location",
                        ["summary"] = "Use quick info to get the destination location and nearby definition context.",
                        ["command"] = CreateExampleLocationCommand("quick-info")
                    },
                    new JObject
                    {
                        ["name"] = "group-current-warnings",
                        ["summary"] = "Filter the Error List down to warnings and group them by code.",
                        ["command"] = WarningsCommandExample
                    },
                    new JObject
                    {
                        ["name"] = "group-current-messages",
                        ["summary"] = "Filter the Error List down to messages and group them by code.",
                        ["command"] = MessagesCommandExample
                    },
                    new JObject
                    {
                        ["name"] = "fetch-multiple-slices",
                        ["summary"] = "Use inline ranges JSON when you need several code windows in one round-trip.",
                        ["command"] = CreateExampleCommand("document-slices", ("ranges", CreateExampleSlicesRanges()))
                    }
                },
                ["example"] = CreateExampleCommand("state", ("out", new JValue(@"C:\temp\ide-state.json"))),
                ["documentSliceExample"] = CreateExampleCommand("document-slice", ("file", new JValue(ExampleCppPath)), ("start_line", new JValue(120)), ("end_line", new JValue(180)), ("out", new JValue(@"C:\temp\slice.json"))),
                ["documentSlicesExample"] = CreateExampleCommand("document-slices", ("ranges", CreateExampleSlicesRanges())),
                ["searchSymbolsExample"] = CreateExampleCommand("search_symbols", ("query", new JValue("propose_export_file_name_and_path")), ("kind", new JValue("function"))),
                ["quickInfoExample"] = CreateExampleLocationCommand("quick_info"),
                ["findTextPathExample"] = CreateExampleCommand("find_text", ("query", new JValue("OnInit")), ("path", new JValue("src\\libslic3r"))),
                ["fileSymbolsExample"] = CreateExampleCommand("file_symbols", ("file", new JValue(ExampleCppPath)), ("kind", new JValue("function"))),
                ["smartContextExample"] = CreateExampleCommand("smart_context", ("query", new JValue("where is GUI_App::OnInit used")), ("max_contexts", new JValue(3)), ("out", new JValue(@"C:\temp\smart-context.json"))),
                ["referencesExample"] = CreateExampleCommand("find_references", [.. CreateExampleLocationArguments(), ("out", new JValue(@"C:\temp\references.json"))]),
                ["callHierarchyExample"] = CreateExampleCommand("call_hierarchy", [.. CreateExampleLocationArguments(), ("max_depth", new JValue(2)), ("max_children", new JValue(10)), ("out", new JValue(@"C:\temp\call_hierarchy.json"))]),
                ["applyDiffFormat"] = "PREFER editor patch format: *** Begin Patch / *** Update File: path/to/file / @@ / context line / -old line / +new line / context line / *** End of File / *** End Patch. Matches by content context — tolerates line shifts after prior edits. Use *** Add File: or *** Delete File: for whole-file operations.",
                ["applyDiffExample"] = CreateExampleCommand("apply-diff", ("patch_file", new JValue(@"C:\temp\change.diff")), ("out", new JValue(@"C:\temp\apply-diff.json"))),
                ["openSolutionExample"] = CreateExampleCommand("open-solution", ("solution", new JValue(@"C:\path\to\solution.sln")), ("out", new JValue(@"C:\temp\open-solution.json")))
            }));
    }

    private static JArray BuildCommandDetails(IEnumerable<BridgeCommandMetadata> commandMetadata)
    {
        JArray details = [];
        foreach (BridgeCommandMetadata command in commandMetadata)
        {
            JArray aliases = [];
            foreach (string alias in PipeCommandNames.GetAliases(command.CanonicalName))
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
        JObject state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
        return new CommandExecutionResult(
            "Smoke test captured IDE state.",
            new JObject
            {
                ["success"] = true,
                ["state"] = state,
            });
    }

    private static string? TryResolveSolutionDocPath(IdeCommandContext context, string fileName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            string? solutionPath = context.Dte.Solution?.FullName;
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                return null;
            }

            string? solutionDirectory = Path.GetDirectoryName(solutionPath);
            if (string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return null;
            }

            string documentPath = Path.Combine(solutionDirectory, fileName);
            return File.Exists(documentPath) ? documentPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CommandExecutionResult> ShowHelpMenuAsync(IdeCommandContext context)
    {
        (string? readmePath, string? bugsPath, bool openedReadme) = await ShowHelpMenuOnMainThreadAsync(context).ConfigureAwait(false);

        return new CommandExecutionResult(
            openedReadme ? "Opened IDE Bridge help." : "Displayed IDE Bridge help.",
            new JObject
            {
                ["readmePath"] = (JToken?)readmePath ?? JValue.CreateNull(),
                ["bugsPath"] = (JToken?)bugsPath ?? JValue.CreateNull(),
                ["commandWindowHelp"] = "Tools.IdeHelp",
            });
    }

    private static async Task<(string? ReadmePath, string? BugsPath, bool OpenedReadme)> ShowHelpMenuOnMainThreadAsync(IdeCommandContext context)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        string? readmePath = TryResolveSolutionDocPath(context, "README.md");
        string? bugsPath = TryResolveSolutionDocPath(context, "BUGS.md");
        bool openedReadme = !string.IsNullOrWhiteSpace(readmePath);
        if (openedReadme)
        {
            context.Dte.ItemOperations.OpenFile(readmePath);
        }

        string message = !openedReadme
            ? "README.md could not be resolved from the current solution. Start with the repo README for setup and usage, check BUGS.md for current runtime gaps, and use Tools.IdeHelp only when you need the raw command catalog."
            : $"Opened README.md for the main product guide.{Environment.NewLine}{Environment.NewLine}Check BUGS.md for current runtime gaps and use Tools.IdeHelp only when you need the raw command catalog.";

        VsShellUtilities.ShowMessageBox(
            context.Package,
            message,
            "VS IDE Bridge",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

        await Task.Yield();
        return (readmePath, bugsPath, openedReadme);
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

    internal sealed class IdeBatchCommandsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0225)
    {
        protected override string CanonicalName => "Tools.IdeBatchCommands";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JArray steps = LoadBatchSteps(args);
            bool stopOnError = args.GetBoolean("stop-on-error", false);
            return await ExecuteBatchAsync(context, steps, stopOnError).ConfigureAwait(true);
        }
    }
}
