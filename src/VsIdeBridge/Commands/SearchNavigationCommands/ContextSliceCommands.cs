using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class SearchNavigationCommands
{
    internal sealed class IdeGetDocumentSliceCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0215)
    {
        protected override string CanonicalName => "Tools.IdeGetDocumentSlice";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            int? requestedLine = args.GetNullableInt32("line");
            int baseLine = requestedLine ?? 1;
            int contextBefore = args.GetInt32("context-before", 0);
            int? contextAfter = args.GetNullableInt32("context-after");
            int startLine = args.GetInt32("start-line", Math.Max(1, baseLine - contextBefore));
            int endLine = args.GetInt32("end-line", requestedLine.HasValue || contextAfter.HasValue
                ? Math.Max(startLine, baseLine + (contextAfter ?? 0))
                : startLine + 199);
            bool revealInEditor = args.GetBoolean("reveal-in-editor", true);
            int revealLine = requestedLine ?? (startLine + ((endLine - startLine) / 2));

            JObject documentSlice = await context.Runtime.DocumentService.GetDocumentSliceAsync(
                context.Dte,
                args.GetString("file"),
                startLine,
                endLine,
                args.GetBoolean("include-line-numbers", true),
                revealInEditor,
                revealLine)
                .ConfigureAwait(true);

            string documentText = (string?)documentSlice["text"] ?? string.Empty;
            string documentSummary = string.IsNullOrWhiteSpace(documentText)
                ? $"Captured lines {documentSlice["actualStartLine"]}-{documentSlice["actualEndLine"]}."
                : documentText;

            return new CommandExecutionResult(documentSummary, documentSlice);
        }
    }

    internal sealed class IdeGetSmartContextForQueryCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0220)
    {
        protected override string CanonicalName => "Tools.IdeGetSmartContextForQuery";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject smartContextResult = await context.Runtime.SearchService.GetSmartContextForQueryAsync(
                context,
                args.GetRequiredString("query"),
                args.GetEnum("scope", SolutionScope, SolutionScope, ProjectScope, DocumentScope),
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetString("project"),
                args.GetInt32("max-contexts", 3),
                args.GetInt32("context-before", 10),
                args.GetInt32("context-after", 10),
                args.GetBoolean("populate-results-window", false),
                args.GetInt32("results-window", 1)).ConfigureAwait(true);

            return new CommandExecutionResult(
                $"Captured {smartContextResult["contextCount"]} smart context(s) from {smartContextResult["totalMatchCount"]} match(es). See Data.contexts and Data.matches for details.",
                smartContextResult);
        }
    }

    internal sealed class IdeGetDocumentSlicesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0228)
    {
        protected override string CanonicalName => "Tools.IdeGetDocumentSlices";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string? rangesJson = args.GetString("ranges");
            string? rangesFile = args.GetString("ranges-file");
            JArray ranges = !string.IsNullOrWhiteSpace(rangesJson)
                ? ParseRangesFromJson(rangesJson!)
                : ParseRangesFromFile(rangesFile);

            JObject documentSlices = await context.Runtime.DocumentService.GetDocumentSlicesAsync(context.Dte, ranges).ConfigureAwait(true);
            return new CommandExecutionResult($"Captured {documentSlices["count"]} slice(s).", documentSlices);
        }

        private static JArray ParseRangesFromJson(string rangesJson)
        {
            try
            {
                return JArray.Parse(rangesJson);
            }
            catch (JsonException ex)
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
            catch (IOException ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse ranges file: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse ranges file: {ex.Message}");
            }
            catch (JsonException ex)
            {
                throw new CommandErrorException("invalid_json", $"Failed to parse ranges file: {ex.Message}");
            }
        }
    }
}
