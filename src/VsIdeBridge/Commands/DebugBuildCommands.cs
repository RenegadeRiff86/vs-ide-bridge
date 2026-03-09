using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class DebugBuildCommands
{
    private const string CountKey = "count";
    private const string TimeoutMillisecondsArgument = "timeout-ms";
    private const int DefaultDebuggerTimeoutMilliseconds = 120000;
    private const int MinimumBuildErrorsTimeoutMilliseconds = 5000;

    private static CommandExecutionResult CreateCapturedResult(string itemLabel, JObject data)
    {
        return new CommandExecutionResult($"Captured {data[CountKey]} {itemLabel}.", data);
    }

    private static int GetQuickDiagnosticsTimeout(bool quick)
    {
        return quick ? MinimumBuildErrorsTimeoutMilliseconds : DefaultDebuggerTimeoutMilliseconds;
    }

    private static int GetBuildErrorsTimeout(CommandArguments args)
    {
        var timeout = args.GetInt32(TimeoutMillisecondsArgument, 600000);
        if (timeout < MinimumBuildErrorsTimeoutMilliseconds)
        {
            throw new CommandErrorException(
                "invalid_timeout",
                $"'{TimeoutMillisecondsArgument}' must be at least {MinimumBuildErrorsTimeoutMilliseconds}ms for build_errors.");
        }

        return timeout;
    }

    private static ErrorListQuery CreateErrorListQuery(CommandArguments args, string? defaultSeverity = null)
    {
        return new ErrorListQuery
        {
            Severity = args.GetString("severity") ?? defaultSeverity,
            Code = args.GetString("code"),
            Project = args.GetString("project"),
            Path = args.GetString("path"),
            Text = args.GetString("text"),
            GroupBy = args.GetString("group-by"),
            Max = args.GetNullableInt32("max"),
        };
    }

    private static JObject FilterRowsBySeverity(JArray allRows, string severity, int? max)
    {
        var filtered = allRows
            .Where(r => string.Equals((string?)r["severity"], severity, StringComparison.OrdinalIgnoreCase));
        if (max is > 0)
            filtered = filtered.Take(max.Value);

        var result = filtered.ToArray();
        return new JObject
        {
            ["count"] = result.Length,
            ["rows"] = new JArray(result.Select(r => r.DeepClone())),
        };
    }

    internal sealed class IdeDebugGetStateCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020A)
    {
        protected override string CanonicalName => "Tools.IdeDebugGetState";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.GetStateAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger state captured.", data);
        }
    }

    internal sealed class IdeDebugStartCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020B)
    {
        protected override string CanonicalName => "Tools.IdeDebugStart";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.StartAsync(
                context.Dte,
                args.GetBoolean("wait-for-break", false),
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger started.", data);
        }
    }

    internal sealed class IdeDebugStopCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020C)
    {
        protected override string CanonicalName => "Tools.IdeDebugStop";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.StopAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger stopped.", data);
        }
    }

    internal sealed class IdeDebugBreakCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020D)
    {
        protected override string CanonicalName => "Tools.IdeDebugBreak";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.BreakAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger break requested.", data);
        }
    }

    internal sealed class IdeDebugContinueCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020E)
    {
        protected override string CanonicalName => "Tools.IdeDebugContinue";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.ContinueAsync(
                context.Dte,
                args.GetBoolean("wait-for-break", false),
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger continued.", data);
        }
    }

    internal sealed class IdeDebugStepOverCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x020F)
    {
        protected override string CanonicalName => "Tools.IdeDebugStepOver";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.StepOverAsync(
                context.Dte,
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger step over completed.", data);
        }
    }

    internal sealed class IdeDebugStepIntoCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0210)
    {
        protected override string CanonicalName => "Tools.IdeDebugStepInto";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.StepIntoAsync(
                context.Dte,
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger step into completed.", data);
        }
    }

    internal sealed class IdeDebugStepOutCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0211)
    {
        protected override string CanonicalName => "Tools.IdeDebugStepOut";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.StepOutAsync(
                context.Dte,
                args.GetInt32(TimeoutMillisecondsArgument, DefaultDebuggerTimeoutMilliseconds)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger step out completed.", data);
        }
    }

    internal sealed class IdeDebugThreadsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0233)
    {
        protected override string CanonicalName => "Tools.IdeDebugThreads";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.GetThreadsAsync(context.Dte).ConfigureAwait(true);
            return CreateCapturedResult("debugger thread(s)", data);
        }
    }

    internal sealed class IdeDebugStackCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0234)
    {
        protected override string CanonicalName => "Tools.IdeDebugStack";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.GetStackAsync(
                context.Dte,
                args.GetNullableInt32("thread-id"),
                args.GetInt32("max-frames", 100)).ConfigureAwait(true);
            return CreateCapturedResult("stack frame(s)", data);
        }
    }

    internal sealed class IdeDebugLocalsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0235)
    {
        protected override string CanonicalName => "Tools.IdeDebugLocals";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.GetLocalsAsync(
                context.Dte,
                args.GetInt32("max", 200)).ConfigureAwait(true);
            return CreateCapturedResult("local variable(s)", data);
        }
    }

    internal sealed class IdeDebugModulesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0236)
    {
        protected override string CanonicalName => "Tools.IdeDebugModules";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.GetModulesAsync(context.Dte).ConfigureAwait(true);
            return CreateCapturedResult("process(es) in the module snapshot", data);
        }
    }

    internal sealed class IdeDebugWatchCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0237)
    {
        protected override string CanonicalName => "Tools.IdeDebugWatch";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.EvaluateWatchAsync(
                context.Dte,
                args.GetRequiredString("expression"),
                args.GetInt32("timeout-ms", 1000)).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger watch expression evaluated.", data);
        }
    }

    internal sealed class IdeDebugExceptionsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0238)
    {
        protected override string CanonicalName => "Tools.IdeDebugExceptions";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.DebuggerService.GetExceptionsAsync(context.Dte).ConfigureAwait(true);
            return new CommandExecutionResult("Debugger exception settings snapshot captured.", data);
        }
    }

    internal sealed class IdeDiagnosticsSnapshotCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0239)
    {
        protected override string CanonicalName => "Tools.IdeDiagnosticsSnapshot";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var quick = args.GetBoolean("quick", false);
            var timeout = args.GetInt32(TimeoutMillisecondsArgument, GetQuickDiagnosticsTimeout(quick));
            var waitForIntellisense = args.GetBoolean("wait-for-intellisense", !quick);
            var max = args.GetNullableInt32("max");

            var all = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                waitForIntellisense,
                timeout,
                quick,
                new ErrorListQuery { Max = max }).ConfigureAwait(true);

            var allRows = (JArray?)all["rows"] ?? [];
            var errors = FilterRowsBySeverity(allRows, "Error", max);
            var warnings = FilterRowsBySeverity(allRows, "Warning", max);
            var messages = FilterRowsBySeverity(allRows, "Message", max);

            var data = new JObject
            {
                ["state"] = await context.Runtime.IdeStateService.GetStateAsync(context.Dte).ConfigureAwait(true),
                ["debug"] = await context.Runtime.DebuggerService.GetStateAsync(context.Dte).ConfigureAwait(true),
                ["build"] = await context.Runtime.BuildService.GetBuildStateAsync(context.Dte).ConfigureAwait(true),
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["messages"] = messages,
            };

            return new CommandExecutionResult("Diagnostics snapshot captured.", data);
        }
    }

    internal sealed class IdeBuildConfigurationsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x023A)
    {
        protected override string CanonicalName => "Tools.IdeBuildConfigurations";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.BuildService.ListConfigurationsAsync(context.Dte).ConfigureAwait(true);
            return CreateCapturedResult("build configuration(s)", data);
        }
    }

    internal sealed class IdeSetBuildConfigurationCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x023B)
    {
        protected override string CanonicalName => "Tools.IdeSetBuildConfiguration";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.BuildService.SetConfigurationAsync(
                context.Dte,
                args.GetRequiredString("configuration"),
                args.GetString("platform")).ConfigureAwait(true);
            return new CommandExecutionResult("Build configuration activated.", data);
        }
    }

    internal sealed class IdeBuildSolutionCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0212)
    {
        protected override string CanonicalName => "Tools.IdeBuildSolution";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var data = await context.Runtime.BuildService.BuildSolutionAsync(
                context,
                args.GetInt32("timeout-ms", 600000),
                args.GetString("configuration"),
                args.GetString("platform")).ConfigureAwait(true);

            return new CommandExecutionResult($"Build completed with LastBuildInfo={data["lastBuildInfo"]}.", data);
        }
    }

    internal sealed class IdeGetErrorListCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0213)
    {
        protected override string CanonicalName => "Tools.IdeGetErrorList";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var quick = args.GetBoolean("quick", false);
            var data = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                args.GetBoolean("wait-for-intellisense", !quick),
                args.GetInt32(TimeoutMillisecondsArgument, GetQuickDiagnosticsTimeout(quick)),
                quick,
                CreateErrorListQuery(args)).ConfigureAwait(true);

            return new CommandExecutionResult($"Captured {data["count"]} Error List row(s).", data);
        }
    }

    internal sealed class IdeGetWarningsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0230)
    {
        protected override string CanonicalName => "Tools.IdeGetWarnings";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var quick = args.GetBoolean("quick", false);
            var data = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                args.GetBoolean("wait-for-intellisense", !quick),
                args.GetInt32(TimeoutMillisecondsArgument, GetQuickDiagnosticsTimeout(quick)),
                quick,
                CreateErrorListQuery(args, "warning")).ConfigureAwait(true);

            return new CommandExecutionResult($"Captured {data["count"]} warning row(s).", data);
        }
    }

    internal sealed class IdeBuildAndCaptureErrorsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService) : IdeCommandBase(package, runtime, commandService, 0x0214)
    {
        protected override string CanonicalName => "Tools.IdeBuildAndCaptureErrors";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var timeout = GetBuildErrorsTimeout(args);
            var build = await context.Runtime.BuildService.BuildAndCaptureErrorsAsync(
                context,
                timeout,
                args.GetBoolean("wait-for-intellisense", true)).ConfigureAwait(true);
            var errors = await context.Runtime.ErrorListService.GetErrorListAsync(
                context,
                false,
                timeout,
                query: CreateErrorListQuery(args)).ConfigureAwait(true);

            var data = new JObject
            {
                ["build"] = build,
                ["errors"] = errors,
            };

            return new CommandExecutionResult($"Build finished and captured {errors["count"]} Error List row(s).", data);
        }
    }
}
