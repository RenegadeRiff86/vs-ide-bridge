using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Text;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class PatchCommands
{
    private const int DiagnosticsRefreshTimeoutMilliseconds = 30_000;

    internal sealed class IdeApplyUnifiedDiffCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0221)
    {
        protected override string CanonicalName => "Tools.IdeApplyUnifiedDiff";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string? patchText = null;
            var patchTextBase64 = args.GetString("patch-text-base64");
            if (!string.IsNullOrWhiteSpace(patchTextBase64))
            {
                try
                {
                    patchText = Encoding.UTF8.GetString(System.Convert.FromBase64String(patchTextBase64));
                }
                catch (System.FormatException ex)
                {
                    throw new CommandErrorException(
                        "invalid_arguments",
                        "Value passed to --patch-text-base64 was not valid base64.",
                        new { exception = ex.Message });
                }
            }

            var patchFile = args.GetString("patch-file");
            var baseDirectory = args.GetString("base-directory");
            var approvalData = await context.Runtime.BridgeApprovalService
                .RequestApprovalAsync(
                    context,
                    BridgeApprovalKind.Edit,
                    BuildPatchApprovalSubject(patchFile, patchText),
                    BuildPatchApprovalDetails(baseDirectory, patchFile, patchText))
                .ConfigureAwait(true);

            var openChangedFiles = args.GetBoolean("open-changed-files", defaultValue: true);

            var data = await context.Runtime.PatchService.ApplyUnifiedDiffAsync(
                context.Dte,
                context.Runtime.DocumentService,
                patchFile,
                patchText,
                baseDirectory,
                openChangedFiles,
                args.GetBoolean("save-changed-files", false)).ConfigureAwait(true);

            await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                waitForIntellisense: true,
                DiagnosticsRefreshTimeoutMilliseconds,
                query: new ErrorListQuery { Max = 1 },
                afterEdit: true).ConfigureAwait(true);

            data["approval"] = approvalData["approval"]?.DeepClone();
            data["approvalOperation"] = approvalData["operation"]?.DeepClone();
            data["approvalPromptShown"] = approvalData["promptShown"]?.DeepClone();
            data["diagnosticsRefreshed"] = true;

            return new CommandExecutionResult($"Applied unified diff to {data["count"]} file(s).", data);
        }

        private static string BuildPatchApprovalSubject(string? patchFile, string? patchText)
        {
            var fileSummary = SummarizePatchFiles(patchText);
            if (!string.IsNullOrWhiteSpace(fileSummary))
            {
                return "Apply patch: " + fileSummary;
            }

            if (!string.IsNullOrWhiteSpace(patchFile))
            {
                return "Apply patch file: " + patchFile;
            }

            return "Apply patch to files in this solution";
        }

        private static string? BuildPatchApprovalDetails(string? baseDirectory, string? patchFile, string? patchText)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                builder.Append("baseDir=").Append(baseDirectory);
            }

            if (!string.IsNullOrWhiteSpace(patchFile))
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("patchFile=").Append(patchFile);
            }

            var patchSummary = SummarizePatchFiles(patchText);
            if (!string.IsNullOrWhiteSpace(patchSummary))
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("targets=").Append(patchSummary);
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static string? SummarizePatchFiles(string? patchText)
        {
            if (string.IsNullOrWhiteSpace(patchText))
            {
                return null;
            }

            var files = new System.Collections.Generic.List<string>();
            var lines = patchText!.Split(["\r\n", "\n"], System.StringSplitOptions.None);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("*** Update File: ", System.StringComparison.Ordinal) ||
                    line.StartsWith("*** Add File: ", System.StringComparison.Ordinal) ||
                    line.StartsWith("*** Delete File: ", System.StringComparison.Ordinal))
                {
                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex >= 0 && separatorIndex + 1 < line.Length)
                    {
                        files.Add(line.Substring(separatorIndex + 1).Trim());
                    }
                }
                else if (line.StartsWith("+++ b/", System.StringComparison.Ordinal) ||
                    line.StartsWith("--- a/", System.StringComparison.Ordinal))
                {
                    files.Add(line.Substring(6).Trim());
                }
            }

            if (files.Count == 0)
            {
                return null;
            }

            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var unique = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                if (seen.Add(file))
                {
                    unique.Add(file);
                }
            }

            if (unique.Count == 1)
            {
                return unique[0];
            }

            return unique.Count <= 3
                ? string.Join(", ", unique)
                : unique[0] + ", " + unique[1] + ", " + unique[2] + $" (+{unique.Count - 3} more)";
        }
    }

    internal sealed class IdeWriteFileCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x024D)
    {
        protected override string CanonicalName => "Tools.IdeWriteFile";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var file = args.GetString("file")
                ?? throw new CommandErrorException("invalid_arguments", "Missing required --file argument.");

            var contentBase64 = args.GetString("content-base64")
                ?? throw new CommandErrorException("invalid_arguments", "Missing required --content-base64 argument.");

            string content;
            try
            {
                content = Encoding.UTF8.GetString(System.Convert.FromBase64String(contentBase64));
            }
            catch (System.FormatException ex)
            {
                throw new CommandErrorException("invalid_arguments", "Value passed to --content-base64 was not valid base64.", new { exception = ex.Message });
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var resolvedPath = context.Runtime.PatchService.ResolveFilePath(context.Dte, file);

            var approvalData = await context.Runtime.BridgeApprovalService
                .RequestApprovalAsync(
                    context,
                    BridgeApprovalKind.Edit,
                    $"Write file: {System.IO.Path.GetFileName(resolvedPath)}",
                    $"path={resolvedPath}, bytes={Encoding.UTF8.GetByteCount(content)}")
                .ConfigureAwait(true);

            var writeResult = await context.Runtime.DocumentService.WriteDocumentTextAsync(
                context.Dte,
                resolvedPath,
                content,
                line: 1,
                column: 1,
                saveChanges: true).ConfigureAwait(true);

            var data = new Newtonsoft.Json.Linq.JObject
            {
                ["path"] = resolvedPath,
                ["byteCount"] = Encoding.UTF8.GetByteCount(content),
                ["lineCount"] = content.Split('\n').Length,
                ["editorBacked"] = writeResult["editorBacked"] ?? false,
                ["saved"] = writeResult["saved"] ?? true,
                ["approval"] = approvalData["approval"]?.DeepClone(),
                ["approvalOperation"] = approvalData["operation"]?.DeepClone(),
                ["approvalPromptShown"] = approvalData["promptShown"]?.DeepClone(),
            };

            await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                waitForIntellisense: true,
                DiagnosticsRefreshTimeoutMilliseconds,
                query: new ErrorListQuery { Max = 1 },
                afterEdit: true).ConfigureAwait(true);

            data["diagnosticsRefreshed"] = true;

            return new CommandExecutionResult($"Wrote {data["lineCount"]} lines to {System.IO.Path.GetFileName(resolvedPath)}.", data);
        }
    }
}
