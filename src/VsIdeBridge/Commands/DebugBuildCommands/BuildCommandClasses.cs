using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class DebugBuildCommands
{
    internal sealed class IdeDiagnosticsSnapshotCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0239)
    {
        protected override string CanonicalName => "Tools.IdeDiagnosticsSnapshot";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            bool quick = args.GetBoolean("quick", false);
            int timeout = args.GetInt32(TimeoutMillisecondsArgument, GetQuickDiagnosticsTimeout(quick));
            bool waitForIntellisense = args.GetBoolean("wait-for-intellisense", !quick);
            int? max = args.GetNullableInt32("max");

            JObject errors = await GetSeverityDiagnosticsAsync(
                context,
                waitForIntellisense,
                timeout,
                quick,
                "Error",
                max).ConfigureAwait(true);
            JObject warnings = await GetSeverityDiagnosticsAsync(
                context,
                waitForIntellisense,
                timeout,
                quick,
                "Warning",
                max).ConfigureAwait(true);
            JObject messages = await GetSeverityDiagnosticsAsync(
                context,
                waitForIntellisense,
                timeout,
                quick,
                "Message",
                max).ConfigureAwait(true);

            JObject diagnosticsSnapshot = new()
            {
                ["state"] = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true),
                ["debug"] = await context.Runtime.DebuggerService.GetStateAsync(context.Dte).ConfigureAwait(true),
                ["build"] = await context.Runtime.BuildService.GetBuildStateAsync(context.Dte).ConfigureAwait(true),
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["messages"] = messages,
            };

            return new CommandExecutionResult("Diagnostics snapshot captured.", diagnosticsSnapshot);
        }
    }

    internal sealed class IdeBuildConfigurationsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x023A)
    {
        protected override string CanonicalName => "Tools.IdeBuildConfigurations";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject buildConfigurations = await context.Runtime.BuildService.ListConfigurationsAsync(context.Dte).ConfigureAwait(true);
            return CreateCapturedResult("build configuration(s)", buildConfigurations);
        }
    }

    internal sealed class IdeSetBuildConfigurationCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x023B)
    {
        protected override string CanonicalName => "Tools.IdeSetBuildConfiguration";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            JObject buildConfigResult = await context.Runtime.BuildService.SetConfigurationAsync(
                context.Dte,
                args.GetRequiredString("configuration"),
                args.GetString("platform")).ConfigureAwait(true);
            return new CommandExecutionResult("Build configuration activated.", buildConfigResult);
        }
    }
}
