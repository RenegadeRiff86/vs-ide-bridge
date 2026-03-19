using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class SearchNavigationCommands
{
    private const string CountKey = "count";
    private const string DocumentArgument = "document";
    private const string DocumentScope = "document";
    private const string ProjectScope = "project";
    private const string SolutionScope = "solution";
    private const string OpenScope = "open";

    private static CommandExecutionResult CreateFoundResult(string itemLabel, JObject data)
    {
        return new CommandExecutionResult($"Found {data[CountKey]} {itemLabel}.", data);
    }

    internal sealed class IdeFindTextCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0202)
    {
        protected override string CanonicalName => "Tools.IdeFindText";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("query");
            var scope = args.GetEnum("scope", SolutionScope, SolutionScope, ProjectScope, DocumentScope, OpenScope);
            var project = args.GetString("project");
            var commandData = await context.Runtime.SearchService.FindTextAsync(
                context,
                query,
                scope,
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetInt32("results-window", 1),
                project,
                args.GetString("path")).ConfigureAwait(true);

            return CreateFoundResult("match(es)", commandData);
        }
    }

    internal sealed class IdeFindTextBatchCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x024A)
    {
        protected override string CanonicalName => "Tools.IdeFindTextBatch";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var queriesJson = args.GetRequiredString("queries");
            JArray queriesArray;
            try
            {
                queriesArray = JArray.Parse(queriesJson);
            }
            catch (Exception ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse --queries JSON: {ex.Message}");
            }

            var queries = queriesArray
                .Values<string>()
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .ToArray();
            if (queries.Length == 0)
            {
                throw new CommandErrorException("invalid_arguments", "Missing required argument --queries with at least one query string.");
            }

            var scope = args.GetEnum("scope", SolutionScope, SolutionScope, ProjectScope, DocumentScope, OpenScope);
            var project = args.GetString("project");
            var commandData = await context.Runtime.SearchService.FindTextBatchAsync(
                context,
                queries,
                scope,
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetInt32("results-window", 1),
                project,
                args.GetString("path"),
                args.GetInt32("max-queries-per-chunk", 5)).ConfigureAwait(true);

            return CreateFoundResult("match(es)", commandData);
        }
    }

    internal sealed class IdeFindFilesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0203)
    {
        protected override string CanonicalName => "Tools.IdeFindFiles";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("query");
            var rawExtensions = args.GetString("extensions", string.Empty) ?? string.Empty;
            var extensions = rawExtensions
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var commandData = await context.Runtime.SearchService.FindFilesAsync(
                context,
                query,
                args.GetString("path"),
                extensions,
                args.GetInt32("max-results", 200),
                args.GetBoolean("include-non-project", true)).ConfigureAwait(true);
            return CreateFoundResult("file(s)", commandData);
        }
    }

    internal sealed class IdeOpenDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0204)
    {
        protected override string CanonicalName => "Tools.IdeOpenDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.DocumentService.OpenDocumentAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1),
                args.GetInt32("column", 1),
                args.GetBoolean("allow-disk-fallback", true)).ConfigureAwait(true);

            return new CommandExecutionResult("Document activated.", commandData);
        }
    }

    internal sealed class IdeListDocumentsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0216)
    {
        protected override string CanonicalName => "Tools.IdeListDocuments";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.DocumentService.ListOpenDocumentsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {commandData["count"]} open document(s).", commandData);
        }
    }

    internal sealed class IdeActivateDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0217)
    {
        protected override string CanonicalName => "Tools.IdeActivateDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.DocumentService
                .ActivateOpenDocumentAsync(context.Dte, args.GetRequiredString("query"))
                .ConfigureAwait(true);
            return new CommandExecutionResult("Document tab activated.", commandData);
        }
    }

    internal sealed class IdeCloseDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0218)
    {
        protected override string CanonicalName => "Tools.IdeCloseDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.DocumentService
                .CloseOpenDocumentsAsync(
                    context.Dte,
                    args.GetString("query"),
                    args.GetBoolean("all", false),
                    args.GetBoolean("save", false))
                .ConfigureAwait(true);
            return new CommandExecutionResult($"Closed {commandData["count"]} document(s).", commandData);
        }
    }

    internal sealed class IdeListOpenTabsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021D)
    {
        protected override string CanonicalName => "Tools.IdeListOpenTabs";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.DocumentService.ListOpenTabsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {commandData["count"]} open tab(s).", commandData);
        }
    }

    internal sealed class IdeCloseFileCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021E)
    {
        protected override string CanonicalName => "Tools.IdeCloseFile";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.DocumentService
                .CloseFileAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetString("query"),
                    args.GetBoolean("save", false))
                .ConfigureAwait(true);

            return new CommandExecutionResult($"Closed {commandData["count"]} file(s).", commandData);
        }
    }

    internal sealed class IdeCloseAllExceptCurrentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021F)
    {
        protected override string CanonicalName => "Tools.IdeCloseAllExceptCurrent";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.DocumentService
                .CloseAllExceptCurrentAsync(context.Dte, args.GetBoolean("save", false))
                .ConfigureAwait(true);

            return new CommandExecutionResult($"Closed {commandData["count"]} file(s).", commandData);
        }
    }

    internal sealed class IdeSaveDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0232)
    {
        protected override string CanonicalName => "Tools.IdeSaveDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var saveAll = args.GetBoolean("all", false);
            var filePath = args.GetString("file");
            var commandData = await context.Runtime.DocumentService
                .SaveDocumentAsync(context.Dte, filePath, saveAll)
                .ConfigureAwait(true);
            var count = commandData["count"]?.Value<int>() ?? 0;
            return new CommandExecutionResult(saveAll ? $"Saved all {count} document(s)." : $"Saved {count} document(s).", commandData);
        }
    }

    internal sealed class IdeReloadDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x024E)
    {
        protected override string CanonicalName => "Tools.IdeReloadDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var filePath = args.GetRequiredString("file");
            var commandData = await context.Runtime.DocumentService
                .ReloadDocumentAsync(filePath)
                .ConfigureAwait(true);
            var reloaded = commandData["reloaded"]?.Value<bool>() ?? false;
            return new CommandExecutionResult(reloaded ? $"Reloaded {filePath}." : $"Skipped reload for {filePath} (not open).", commandData);
        }
    }

    internal sealed class IdeActivateWindowCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0205)
    {
        protected override string CanonicalName => "Tools.IdeActivateWindow";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.WindowService.ActivateWindowAsync(context.Dte, args.GetRequiredString("window")).ConfigureAwait(true);
            return new CommandExecutionResult("Window activated.", commandData);
        }
    }

    internal sealed class IdeListWindowsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0219)
    {
        protected override string CanonicalName => "Tools.IdeListWindows";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.WindowService.ListWindowsAsync(context.Dte, args.GetString("query")).ConfigureAwait(true);
            return new CommandExecutionResult($"Listed {commandData["count"]} window(s).", commandData);
        }
    }

    internal sealed class IdeExecuteVsCommandCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021A)
    {
        protected override string CanonicalName => "Tools.IdeExecuteVsCommand";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.VsCommandService
                .ExecutePositionedCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    args.GetRequiredString("command"),
                    args.GetString("args"),
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", false))
                .ConfigureAwait(true);
            return new CommandExecutionResult("Visual Studio command executed.", commandData);
        }
    }

    internal sealed class IdeFindAllReferencesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021B)
    {
        private static readonly string[] CandidateCommands = ["Edit.FindAllReferences"];

        protected override string CanonicalName => "Tools.IdeFindAllReferences";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commandData = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    context.Runtime.WindowService,
                    CandidateCommands,
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", false),
                    "references",
                    args.GetBoolean("activate-window", true),
                    args.GetInt32("timeout-ms", 5000))
                .ConfigureAwait(true);

            return new CommandExecutionResult("Find All References executed.", commandData);
        }
    }

    internal sealed class IdeCountReferencesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x023C)
    {
        private static readonly string[] CandidateCommands = ["Edit.FindAllReferences"];
        private static readonly Regex CountPattern = new(@"\b(?<count>\d+)\b", RegexOptions.Compiled);

        protected override string CanonicalName => "Tools.IdeCountReferences";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var referenceCommandResult = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    context.Runtime.WindowService,
                    CandidateCommands,
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", false),
                    "references",
                    args.GetBoolean("activate-window", true),
                    args.GetInt32("timeout-ms", 5000))
                .ConfigureAwait(true);

            var caption = referenceCommandResult["resultWindow"]?["caption"]?.ToString() ?? string.Empty;
            var match = CountPattern.Match(caption);
            if (match.Success && int.TryParse(match.Groups["count"].Value, out var count))
            {
                referenceCommandResult["countKnown"] = true;
                referenceCommandResult["count"] = count;
            }
            else
            {
                referenceCommandResult["countKnown"] = false;
                referenceCommandResult["count"] = null;
                referenceCommandResult["reason"] = string.IsNullOrWhiteSpace(caption)
                    ? "Could not determine reference count from the references tool window."
                    : $"Could not parse an exact count from window caption '{caption}'.";
            }

            return new CommandExecutionResult("Reference count request completed.", referenceCommandResult);
        }
    }

    internal sealed class IdeShowCallHierarchyCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021C)
    {
        private static readonly string[] CandidateCommands = ["View.CallHierarchy"];

        protected override string CanonicalName => "Tools.IdeShowCallHierarchy";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var callHierarchyResult = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    context.Runtime.WindowService,
                    CandidateCommands,
                    args.GetString("file"),
                    args.GetString("document"),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", false),
                    "Call Hierarchy",
                    args.GetBoolean("activate-window", true),
                    args.GetInt32("timeout-ms", 5000))
                .ConfigureAwait(true);

            return new CommandExecutionResult("Call Hierarchy executed.", callHierarchyResult);
        }
    }

    internal sealed class IdeGetDocumentSliceCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0215)
    {
        protected override string CanonicalName => "Tools.IdeGetDocumentSlice";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var requestedLine = args.GetNullableInt32("line");
            var baseLine = requestedLine ?? 1;
            var contextBefore = args.GetInt32("context-before", 0);
            var contextAfter = args.GetInt32("context-after", 0);
            var startLine = args.GetInt32("start-line", Math.Max(1, baseLine - contextBefore));
            var endLine = args.GetInt32("end-line", Math.Max(startLine, baseLine + contextAfter));
            var revealInEditor = args.GetBoolean("reveal-in-editor", true);
            var revealLine = requestedLine ?? (startLine + ((endLine - startLine) / 2));

            var documentSlice = await context.Runtime.DocumentService.GetDocumentSliceAsync(
                context.Dte,
                args.GetString("file"),
                startLine,
                endLine,
                args.GetBoolean("include-line-numbers", true),
                revealInEditor,
                revealLine)
                .ConfigureAwait(true);

            return new CommandExecutionResult(
                $"Captured lines {documentSlice["actualStartLine"]}-{documentSlice["actualEndLine"]}.",
                documentSlice);
        }
    }

    internal sealed class IdeGetSmartContextForQueryCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0220)
    {
        protected override string CanonicalName => "Tools.IdeGetSmartContextForQuery";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var smartContextResult = await context.Runtime.SearchService.GetSmartContextForQueryAsync(
                context,
                args.GetRequiredString("query"),
                args.GetEnum("scope", SolutionScope, SolutionScope, ProjectScope, DocumentScope),
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetString("project"),
                args.GetInt32("max-contexts", 5),
                args.GetInt32("context-before", 20),
                args.GetInt32("context-after", 20),
                args.GetBoolean("populate-results-window", true),
                args.GetInt32("results-window", 1)).ConfigureAwait(true);

            return new CommandExecutionResult(
                $"Captured {smartContextResult["contextCount"]} smart context(s) from {smartContextResult["totalMatchCount"]} match(es).",
                smartContextResult);
        }
    }

    internal sealed class IdeGoToDefinitionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0222)
    {
        protected override string CanonicalName => "Tools.IdeGoToDefinition";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var definitionResult = await context.Runtime.DocumentService.GoToDefinitionAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"))
                .ConfigureAwait(true);

            var found = (bool?)definitionResult["definitionFound"] == true;
            return new CommandExecutionResult(
                found ? "Navigated to definition." : "Go To Definition executed (location unchanged).",
                definitionResult);
        }
    }

    internal sealed class IdeGoToImplementationCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x022F)
    {
        protected override string CanonicalName => "Tools.IdeGoToImplementation";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var implementationResult = await context.Runtime.DocumentService.GoToImplementationAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"))
                .ConfigureAwait(true);

            var found = (bool?)implementationResult["implementationFound"] == true;
            return new CommandExecutionResult(
                found ? "Navigated to implementation." : "Go To Implementation executed (location unchanged).",
                implementationResult);
        }
    }

    internal sealed class IdeGetFileOutlineCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0223)
    {
        protected override string CanonicalName => "Tools.IdeGetFileOutline";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var fileOutline = await context.Runtime.DocumentService.GetFileOutlineAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetInt32("max-depth", 3),
                    args.GetString("kind"))
                .ConfigureAwait(true);

            return CreateFoundResult("symbol(s)", fileOutline);
        }
    }

    internal sealed class IdeSearchSymbolsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0226)
    {
        protected override string CanonicalName => "Tools.IdeSearchSymbols";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var symbolQuery = args.GetString("query") ?? args.GetString("name");
            if (string.IsNullOrWhiteSpace(symbolQuery))
            {
                throw new CommandErrorException("invalid_arguments", "Missing required argument --query.");
            }

            var symbolSearchResult = await context.Runtime.SearchService.SearchSymbolsAsync(
                context,
                symbolQuery!,
                args.GetEnum("kind", "all", "all", "function", "class", "struct", "enum", "namespace", "interface", "member", "type"),
                args.GetEnum("scope", SolutionScope, SolutionScope, ProjectScope, DocumentScope, OpenScope),
                args.GetBoolean("match-case", false),
                args.GetString("project"),
                args.GetString("path"),
                args.GetInt32("max", 50)).ConfigureAwait(true);

            return CreateFoundResult("symbol match(es)", symbolSearchResult);
        }
    }

    internal sealed class IdeGetQuickInfoCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0227)
    {
        protected override string CanonicalName => "Tools.IdeGetQuickInfo";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var quickInfo = await context.Runtime.DocumentService.GetQuickInfoAsync(
                context.Dte,
                args.GetString("file"),
                args.GetString(DocumentArgument),
                args.GetNullableInt32("line"),
                args.GetNullableInt32("column"),
                args.GetInt32("context-lines", 10)).ConfigureAwait(true);

            var found = (bool?)quickInfo["definitionFound"] == true;
            return new CommandExecutionResult(
                found ? $"Quick info: definition found for '{quickInfo["word"]}'." : $"Quick info: no definition found for '{quickInfo["word"]}'.",
                quickInfo);
        }
    }

    internal sealed class IdeGetDocumentSlicesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0228)
    {
        protected override string CanonicalName => "Tools.IdeGetDocumentSlices";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var rangesJson = args.GetString("ranges");
            var rangesFile = args.GetString("ranges-file");
            var ranges = !string.IsNullOrWhiteSpace(rangesJson)
                ? ParseRangesFromJson(rangesJson!)
                : ParseRangesFromFile(rangesFile);

            var documentSlices = await context.Runtime.DocumentService.GetDocumentSlicesAsync(context.Dte, ranges).ConfigureAwait(true);
            return new CommandExecutionResult($"Captured {documentSlices["count"]} slice(s).", documentSlices);
        }

        private static JArray ParseRangesFromJson(string rangesJson)
        {
            try
            {
                return JArray.Parse(rangesJson);
            }
            catch (Exception ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse --ranges JSON: {ex.Message}");
            }
        }

        private static JArray ParseRangesFromFile(string? rangesFile)
        {
            if (string.IsNullOrWhiteSpace(rangesFile))
                throw new CommandErrorException("invalid_arguments", "Specify either --ranges or --ranges-file.");
            if (!File.Exists(rangesFile))
                throw new CommandErrorException("file_not_found", $"Ranges file not found: {rangesFile}");
            try
            {
                return JArray.Parse(File.ReadAllText(rangesFile!));
            }
            catch (Exception ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse ranges file: {ex.Message}");
            }
        }
    }

    internal sealed class IdeGetFileSymbolsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x022D)
    {
        protected override string CanonicalName => "Tools.IdeGetFileSymbols";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var fileSymbols = await context.Runtime.DocumentService.GetFileOutlineAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetInt32("max-depth", 8),
                    args.GetString("kind"))
                .ConfigureAwait(true);

            return CreateFoundResult("symbol(s)", fileSymbols);
        }
    }
}
