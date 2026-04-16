using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class DebugBuildCommands
{
    internal sealed class IdeDebugGetStateCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020A)
    {
        protected override string CanonicalName => "Tools.IdeDebugGetState";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger state captured.", commandData);
        }
    }

    internal sealed class IdeDebugStartCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020B)
    {
        protected override string CanonicalName => "Tools.IdeDebugStart";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.StartAsync(
                context.Dte,
                args.GetBoolean("wait-for-break", false),
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);

            if (commandData["likelyBuildOrLaunchFailure"]?.Value<bool>() == true)
            {
                JArray warnings =
                [
                    "Debugger returned to design mode without launching a process. The startup build or launch likely failed; read errors, warnings, messages, or diagnostics_snapshot for details.",
                ];
                return new CommandExecutionResult("Debugger did not start; the startup build or launch likely failed.", commandData, warnings);
            }

            return new CommandExecutionResult("Debugger started.", commandData);
        }
    }

    internal sealed class IdeDebugStopCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020C)
    {
        protected override string CanonicalName => "Tools.IdeDebugStop";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.StopAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger stopped.", commandData);
        }
    }

    internal sealed class IdeDebugBreakCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020D)
    {
        protected override string CanonicalName => "Tools.IdeDebugBreak";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.BreakAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger break requested.", commandData);
        }
    }

    internal sealed class IdeDebugContinueCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020E)
    {
        protected override string CanonicalName => "Tools.IdeDebugContinue";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.ContinueAsync(
                context.Dte,
                args.GetBoolean("wait-for-break", false),
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger continued.", commandData);
        }
    }

    internal sealed class IdeDebugStepOverCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020F)
    {
        protected override string CanonicalName => "Tools.IdeDebugStepOver";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.StepOverAsync(
                context.Dte,
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger step over completed.", commandData);
        }
    }

    internal sealed class IdeDebugStepIntoCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0210)
    {
        protected override string CanonicalName => "Tools.IdeDebugStepInto";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.StepIntoAsync(
                context.Dte,
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger step into completed.", commandData);
        }
    }

    internal sealed class IdeDebugStepOutCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0211)
    {
        protected override string CanonicalName => "Tools.IdeDebugStepOut";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject commandData = await context.Runtime.DebuggerService.StepOutAsync(
                context.Dte,
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger step out completed.", commandData);
        }
    }
}
