using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class IdeCoreCommands
{
    internal sealed class IdeCaptureVsWindowCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x024D)
    {
        protected override string CanonicalName => "Tools.IdeCaptureVsWindow";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject capture = await context.Runtime.WindowService
                .CaptureVsWindowAsync(context.Dte, args.GetString("out"))
                .ConfigureAwait(true);

            string path = capture["path"]?.ToString() ?? string.Empty;
            return new CommandExecutionResult($"Captured Visual Studio window to {path}", capture);
        }
    }
}
