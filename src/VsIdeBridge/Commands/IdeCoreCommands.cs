using System;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class IdeCoreCommands
{
    internal sealed class IdeHelpCommand : IdeCommandBase
    {
        public IdeHelpCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0100)
        {
        }

        protected override string CanonicalName => "Tools.IdeHelp";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var commands = new JArray(
                "Tools.IdeGetState",
                "Tools.IdeWaitForReady",
                "Tools.IdeFindText",
                "Tools.IdeFindFiles",
                "Tools.IdeOpenDocument",
                "Tools.IdeActivateWindow",
                "Tools.IdeSetBreakpoint",
                "Tools.IdeListBreakpoints",
                "Tools.IdeRemoveBreakpoint",
                "Tools.IdeClearAllBreakpoints",
                "Tools.IdeDebugGetState",
                "Tools.IdeDebugStart",
                "Tools.IdeDebugStop",
                "Tools.IdeDebugBreak",
                "Tools.IdeDebugContinue",
                "Tools.IdeDebugStepOver",
                "Tools.IdeDebugStepInto",
                "Tools.IdeDebugStepOut",
                "Tools.IdeBuildSolution",
                "Tools.IdeGetErrorList",
                "Tools.IdeBuildAndCaptureErrors");

            return Task.FromResult(new CommandExecutionResult(
                "Command catalog written.",
                new JObject
                {
                    ["commands"] = commands,
                    ["example"] = @"Tools.IdeGetState --out ""C:\temp\ide-state.json""",
                }));
        }
    }

    internal sealed class IdeSmokeTestCommand : IdeCommandBase
    {
        public IdeSmokeTestCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0101)
        {
        }

        protected override string CanonicalName => "Tools.IdeSmokeTest";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult(
                "Smoke test captured IDE state.",
                new JObject
                {
                    ["success"] = true,
                    ["state"] = state,
                });
        }
    }

    internal sealed class IdeGetStateCommand : IdeCommandBase
    {
        public IdeGetStateCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0200)
        {
        }

        protected override string CanonicalName => "Tools.IdeGetState";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var state = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("IDE state captured.", state);
        }
    }

    internal sealed class IdeWaitForReadyCommand : IdeCommandBase
    {
        public IdeWaitForReadyCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
            : base(package, runtime, commandService, 0x0201)
        {
        }

        protected override string CanonicalName => "Tools.IdeWaitForReady";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var timeout = args.GetInt32("timeout-ms", 120000);
            var data = await context.Runtime.ReadinessService.WaitForReadyAsync(context, timeout).ConfigureAwait(true);
            return new CommandExecutionResult("Readiness wait completed.", data);
        }
    }
}
