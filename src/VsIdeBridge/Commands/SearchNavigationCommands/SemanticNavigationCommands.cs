using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SearchNavigationCommands
{
    internal sealed class IdeFindAllReferencesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x021B)
    {
        private static readonly string[] CandidateCommands = ["Edit.FindAllReferences"];

        protected override string CanonicalName => "Tools.IdeFindAllReferences";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    context.Runtime.WindowService,
                    CandidateCommands,
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetBoolean("select-word", true),
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
            JObject referenceCommandResult = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
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

            string caption = referenceCommandResult["resultWindow"]?["caption"]?.ToString() ?? string.Empty;
            Match match = CountPattern.Match(caption);
            if (match.Success && int.TryParse(match.Groups["count"].Value, out int count))
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
            JObject managedHierarchy = await context.Runtime.SearchService.GetCallHierarchyAsync(
                    context,
                    args.GetString("file"),
                    args.GetString("document"),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"),
                    args.GetInt32("max-depth", 2),
                    args.GetInt32("max-children", 20),
                    args.GetInt32("max-locations-per-caller", 5))
                .ConfigureAwait(true);

            if ((bool?)managedHierarchy["available"] != true)
            {
                JObject unavailableResult = new()
                {
                    ["managedHierarchy"] = managedHierarchy,
                    ["nativeInvocationSkipped"] = true,
                };

                return new CommandExecutionResult(
                    "Call Hierarchy skipped native invocation because no navigable symbol was found; see Data.managedHierarchy for status.",
                    unavailableResult);
            }

            JObject nativeInvocationLocation = ResolveNativeCallHierarchyLocation(managedHierarchy, args);

            JObject callHierarchyResult = await context.Runtime.VsCommandService.ExecuteSymbolCommandAsync(
                    context.Dte,
                    context.Runtime.DocumentService,
                    context.Runtime.WindowService,
                    CandidateCommands,
                    (string?)nativeInvocationLocation["file"],
                    (string?)nativeInvocationLocation["document"],
                    (int?)nativeInvocationLocation["line"],
                    (int?)nativeInvocationLocation["column"],
                    args.GetBoolean("select-word", true),
                    "Call Hierarchy",
                    args.GetBoolean("activate-window", true),
                    args.GetInt32("timeout-ms", 5000))
                .ConfigureAwait(true);

            // CallHierarchy native SDK population disabled for VS 2026 compatibility
            JObject nativeSdkPopulation = new JObject { ["available"] = false, ["reason"] = "CallHierarchy not available in VS 2026 build" };

            callHierarchyResult["managedHierarchy"] = managedHierarchy;
            callHierarchyResult["nativeInvocationLocation"] = nativeInvocationLocation;
            callHierarchyResult["nativeSdkPopulation"] = nativeSdkPopulation;
            if ((bool?)managedHierarchy["available"] == true)
            {
                callHierarchyResult["hierarchy"] = managedHierarchy["root"];
            }

            int nodeCount = (int?)managedHierarchy["nodeCount"] ?? 0;
            string summary = (bool?)managedHierarchy["available"] == true
                ? $"Call Hierarchy executed. Captured {nodeCount} hierarchy node(s). See Data.hierarchy for details."
                : "Call Hierarchy executed. Managed hierarchy was unavailable; see Data.managedHierarchy for status.";
            return new CommandExecutionResult(summary, callHierarchyResult);
        }

        private static JObject ResolveNativeCallHierarchyLocation(JObject managedHierarchy, CommandArguments args)
        {
            string? file = args.GetString("file");
            string? document = args.GetString("document");
            int? line = args.GetNullableInt32("line");
            int? column = args.GetNullableInt32("column");

            if (managedHierarchy["sourceLocation"] is not JObject sourceLocation)
            {
                return CreateNativeInvocationLocation(file, document, line, column);
            }

            string resolvedSymbol = ((string?)sourceLocation["resolvedSymbol"] ?? string.Empty).Trim();
            string lineText = (string?)sourceLocation["lineText"] ?? string.Empty;
            int sourceLine = (int?)sourceLocation["line"] ?? line ?? 1;

            if (TryFindSymbolColumn(lineText, resolvedSymbol, out int symbolColumn))
            {
                return CreateNativeInvocationLocation(
                    (string?)sourceLocation["resolvedPath"] ?? file,
                    document,
                    sourceLine,
                    symbolColumn);
            }

            return CreateNativeInvocationLocation(
                (string?)sourceLocation["resolvedPath"] ?? file,
                document,
                sourceLine,
                (int?)sourceLocation["column"] ?? column);
        }

        private static JObject CreateNativeInvocationLocation(string? file, string? document, int? line, int? column)
        {
            return new JObject
            {
                ["file"] = file ?? string.Empty,
                ["document"] = document ?? string.Empty,
                ["line"] = line,
                ["column"] = column,
            };
        }

        private static bool TryFindSymbolColumn(string lineText, string resolvedSymbol, out int symbolColumn)
        {
            symbolColumn = 0;
            if (string.IsNullOrWhiteSpace(lineText) || string.IsNullOrWhiteSpace(resolvedSymbol))
            {
                return false;
            }

            int symbolIndex = lineText.IndexOf(resolvedSymbol, StringComparison.Ordinal);
            if (symbolIndex < 0)
            {
                return false;
            }

            symbolColumn = symbolIndex + 1;
            return true;
        }
    }

    internal sealed class IdeGoToDefinitionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0222)
    {
        protected override string CanonicalName => "Tools.IdeGoToDefinition";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject definitionResult = await context.Runtime.DocumentService.GoToDefinitionAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"))
                .ConfigureAwait(true);

            bool found = (bool?)definitionResult["definitionFound"] == true;
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
            JObject implementationResult = await context.Runtime.DocumentService.GoToImplementationAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetString(DocumentArgument),
                    args.GetNullableInt32("line"),
                    args.GetNullableInt32("column"))
                .ConfigureAwait(true);

            bool found = (bool?)implementationResult["implementationFound"] == true;
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
            JObject fileOutline = await context.Runtime.DocumentService.GetFileOutlineAsync(
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
            string? symbolQuery = args.GetString("query") ?? args.GetString("name");
            if (string.IsNullOrWhiteSpace(symbolQuery))
            {
                throw new CommandErrorException("invalid_arguments", "Missing required argument --query.");
            }

            JObject symbolSearchResult = await context.Runtime.SearchService.SearchSymbolsAsync(
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
            JObject quickInfo = await context.Runtime.DocumentService.GetQuickInfoAsync(
                context.Dte,
                args.GetString("file"),
                args.GetString(DocumentArgument),
                args.GetNullableInt32("line"),
                args.GetNullableInt32("column"),
                args.GetInt32("context-lines", 10)).ConfigureAwait(true);

            bool found = (bool?)quickInfo["definitionFound"] == true;
            return new CommandExecutionResult(
                found ? $"Quick info: definition found for '{quickInfo["word"]}'." : $"Quick info: no definition found for '{quickInfo["word"]}'.",
                quickInfo);
        }
    }

    internal sealed class IdePeekDefinitionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0265)
    {
        protected override string CanonicalName => "Tools.IdePeekDefinition";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject peekDefinition = await context.Runtime.DocumentService.PeekDefinitionAsync(
                context.Dte,
                args.GetString("file"),
                args.GetString(DocumentArgument),
                args.GetNullableInt32("line"),
                args.GetNullableInt32("column")).ConfigureAwait(true);

            bool found = (bool?)peekDefinition["definitionFound"] == true;
            string symbol = (string?)peekDefinition["word"] ?? string.Empty;
            return new CommandExecutionResult(
                found ? $"Peeked definition for '{symbol}'." : $"Peek definition: no definition found for '{symbol}'.",
                peekDefinition);
        }
    }

    internal sealed class IdeGetFileSymbolsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x022D)
    {
        protected override string CanonicalName => "Tools.IdeGetFileSymbols";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject fileSymbols = await context.Runtime.DocumentService.GetFileOutlineAsync(
                    context.Dte,
                    args.GetString("file"),
                    args.GetInt32("max-depth", 8),
                    args.GetString("kind"))
                .ConfigureAwait(true);

            return CreateFoundResult("symbol(s)", fileSymbols);
        }
    }
}
