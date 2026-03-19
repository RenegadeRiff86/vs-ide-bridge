using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class BreakpointCommands
{
    private const string BreakpointCountSuffix = " breakpoint(s).";

    private static string FormatBreakpointCountMessage(string action, object? count)
    {
        return $"{action} {count}{BreakpointCountSuffix}";
    }

    internal sealed class IdeSetBreakpointCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0206)
    {
        protected override string CanonicalName => "Tools.IdeSetBreakpoint";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.SetBreakpointAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1),
                args.GetInt32("column", 1),
                args.GetString("condition"),
                args.GetEnum("condition-type", "when-true", "when-true", "changed"),
                args.GetInt32("hit-count", 0),
                args.GetEnum("hit-type", "none", "none", "equal", "multiple", "greater-or-equal"),
                args.GetString("trace-message"),
                args.GetBoolean("continue-execution", false)).ConfigureAwait(true);

            if (args.GetBoolean("reveal", true))
            {
                var reveal = await context.Runtime.DocumentService.PositionTextSelectionAsync(
                    context.Dte,
                    args.GetRequiredString("file"),
                    documentQuery: null,
                    args.GetInt32("line", 1),
                    args.GetInt32("column", 1),
                    selectWord: false).ConfigureAwait(true);

                breakpointInfo["revealedInEditor"] = true;
                breakpointInfo["reveal"] = reveal;
            }

            return new CommandExecutionResult("Breakpoint set.", breakpointInfo);
        }
    }

    internal sealed class IdeListBreakpointsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0207)
    {
        protected override string CanonicalName => "Tools.IdeListBreakpoints";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.ListBreakpointsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult(FormatBreakpointCountMessage("Enumerated", breakpointInfo["count"]), breakpointInfo);
        }
    }

    internal sealed class IdeRemoveBreakpointCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0208)
    {
        protected override string CanonicalName => "Tools.IdeRemoveBreakpoint";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.RemoveBreakpointAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1)).ConfigureAwait(true);

            return new CommandExecutionResult(FormatBreakpointCountMessage("Removed", breakpointInfo["removedCount"]), breakpointInfo);
        }
    }

    internal sealed class IdeClearAllBreakpointsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0209)
    {
        protected override string CanonicalName => "Tools.IdeClearAllBreakpoints";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.ClearAllBreakpointsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult(FormatBreakpointCountMessage("Removed", breakpointInfo["removedCount"]), breakpointInfo);
        }
    }

    internal sealed class IdeEnableBreakpointCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0229)
    {
        protected override string CanonicalName => "Tools.IdeEnableBreakpoint";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.EnableBreakpointAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1)).ConfigureAwait(true);

            return new CommandExecutionResult("Breakpoint enabled.", breakpointInfo);
        }
    }

    internal sealed class IdeDisableBreakpointCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x022A)
    {
        protected override string CanonicalName => "Tools.IdeDisableBreakpoint";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.DisableBreakpointAsync(
                context.Dte,
                args.GetRequiredString("file"),
                args.GetInt32("line", 1)).ConfigureAwait(true);

            return new CommandExecutionResult("Breakpoint disabled.", breakpointInfo);
        }
    }

    internal sealed class IdeEnableAllBreakpointsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x022B)
    {
        protected override string CanonicalName => "Tools.IdeEnableAllBreakpoints";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.EnableAllBreakpointsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult(FormatBreakpointCountMessage("Enabled", breakpointInfo["enabledCount"]), breakpointInfo);
        }
    }

    internal sealed class IdeDisableAllBreakpointsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x022C)
    {
        protected override string CanonicalName => "Tools.IdeDisableAllBreakpoints";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var breakpointInfo = await context.Runtime.BreakpointService.DisableAllBreakpointsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult(FormatBreakpointCountMessage("Disabled", breakpointInfo["disabledCount"]), breakpointInfo);
        }
    }
}
