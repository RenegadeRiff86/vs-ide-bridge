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
            var approvalData = await context.Runtime.BridgeApprovalService
                .RequestApprovalAsync(context, BridgeApprovalKind.Edit, "Edit files in this solution.", null)
                .ConfigureAwait(true);

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

            var openChangedFiles = args.GetBoolean("open-changed-files", defaultValue: true);

            var data = await context.Runtime.PatchService.ApplyUnifiedDiffAsync(
                context.Dte,
                context.Runtime.DocumentService,
                args.GetString("patch-file"),
                patchText,
                args.GetString("base-directory"),
                openChangedFiles,
                args.GetBoolean("save-changed-files", false)).ConfigureAwait(true);

            await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                waitForIntellisense: true,
                DiagnosticsRefreshTimeoutMilliseconds,
                query: new ErrorListQuery { Max = 1 }).ConfigureAwait(true);

            data["approval"] = approvalData["approval"]?.DeepClone();
            data["approvalOperation"] = approvalData["operation"]?.DeepClone();
            data["approvalPromptShown"] = approvalData["promptShown"]?.DeepClone();
            data["diagnosticsRefreshed"] = true;

            return new CommandExecutionResult($"Applied unified diff to {data["count"]} file(s).", data);
        }
    }
}
