using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class SearchNavigationCommands
{
    internal sealed class IdeFindTextCommand : IdeCommandBase
    {
        public IdeFindTextCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0202)
        {
        }

        protected override string CanonicalName => "Tools.IdeFindText";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("query");
            var scope = args.GetEnum("scope", "solution", "solution", "project", "document");
            var project = args.GetString("project");
            var data = await context.Runtime.SearchService.FindTextAsync(
                context,
                query,
                scope,
                args.GetBoolean("match-case", false),
                args.GetBoolean("whole-word", false),
                args.GetBoolean("regex", false),
                args.GetInt32("results-window", 1),
                project).ConfigureAwait(true);

            return new CommandExecutionResult($"Found {data["count"]} match(es).", data);
        }
    }

    internal sealed class IdeFindFilesCommand : IdeCommandBase
    {
        public IdeFindFilesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0203)
        {
        }

        protected override string CanonicalName => "Tools.IdeFindFiles";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("query");
            var data = await context.Runtime.SearchService.FindFilesAsync(context, query).ConfigureAwait(true);
            return new CommandExecutionResult($"Found {data["count"]} file(s).", data);
        }
    }

    internal sealed class IdeOpenDocumentCommand : IdeCommandBase
    {
        public IdeOpenDocumentCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0204)
        {
        }

        protected override string CanonicalName => "Tools.IdeOpenDocument";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DocumentService.OpenDocumentAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1),
                args.GetInt32("column", 1)).ConfigureAwait(true);

            return new CommandExecutionResult("Document activated.", data);
        }
    }

    internal sealed class IdeActivateWindowCommand : IdeCommandBase
    {
        public IdeActivateWindowCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0205)
        {
        }

        protected override string CanonicalName => "Tools.IdeActivateWindow";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.WindowService.ActivateWindowAsync(context.Dte, args.GetRequiredString("window")).ConfigureAwait(true);
            return new CommandExecutionResult("Window activated.", data);
        }
    }
}
