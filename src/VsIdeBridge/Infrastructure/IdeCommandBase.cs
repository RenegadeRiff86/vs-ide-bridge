using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using VsIdeBridge.Services;

namespace VsIdeBridge.Infrastructure;

internal abstract class IdeCommandBase
{
    protected VsIdeBridgePackage Package { get; }
    protected IdeBridgeRuntime Runtime { get; }
    protected OleMenuCommand MenuCommand { get; }

    protected IdeCommandBase(
        VsIdeBridgePackage package,
        IdeBridgeRuntime runtime,
        OleMenuCommandService commandService,
        int commandId,
        bool acceptsParameters = true)
    {
        Package = package;
        Runtime = runtime;

        var menuCommandId = new CommandID(CommandRegistrar.CommandSet, commandId);
        var menuCommand = new OleMenuCommand(Execute, menuCommandId);
        if (acceptsParameters)
        {
            menuCommand.ParametersDescription = "$";
        }

        commandService.AddCommand(menuCommand);
        MenuCommand = menuCommand;
    }

    protected abstract string CanonicalName { get; }

    internal string Name => CanonicalName;

    internal virtual bool AllowAutomationInvocation => true;

    internal Task<CommandExecutionResult> ExecuteDirectAsync(IdeCommandContext ctx, CommandArguments args)
        => ExecuteAsync(ctx, args);

    protected abstract Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args);

    private void Execute(object sender, EventArgs e)
    {
        _ = Package.JoinableTaskFactory.RunAsync(() => ExecuteInternalAsync(e));
    }

    private async Task ExecuteInternalAsync(EventArgs e)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var rawArguments = (e as OleMenuCmdEventArgs)?.InValue as string;
        var outputPath = string.Empty;
        string? requestId = null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(Package.DisposalToken);
        var dte = await Package.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;
        Assumes.Present(dte);

        var context = new IdeCommandContext(Package, dte, Runtime.Logger, Runtime, Package.DisposalToken);

        try
        {
            var args = CommandArgumentParser.Parse(rawArguments);
            outputPath = ResolveOutputPath(args);
            requestId = args.GetString("request-id");
            var result = await ExecuteAsync(context, args).ConfigureAwait(true);
            var envelope = new CommandEnvelope
            {
                SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
                Command = CanonicalName,
                RequestId = requestId,
                Success = true,
                StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
                FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                Summary = result.Summary,
                Warnings = result.Warnings,
                Error = null,
                Data = result.Data,
            };

            await CommandResultWriter.WriteAsync(outputPath, envelope, Package.DisposalToken).ConfigureAwait(false);
            await context.Logger.LogAsync($"IDE Bridge: {CanonicalName} OK - {result.Summary} -> {outputPath}", Package.DisposalToken, activatePane: true).ConfigureAwait(true);
        }
        catch (CommandErrorException ex)
        {
            var failureData = await Runtime.FailureContextService.CaptureAsync(context).ConfigureAwait(true);
            var envelope = new CommandEnvelope
            {
                SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
                Command = CanonicalName,
                RequestId = requestId,
                Success = false,
                StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
                FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                Summary = ex.Message,
                Warnings = [],
                Error = new
                {
                    code = ex.Code,
                    message = ex.Message,
                    details = ex.Details,
                },
                Data = failureData,
            };

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                await CommandResultWriter.WriteAsync(outputPath, envelope, Package.DisposalToken).ConfigureAwait(false);
            }

            await context.Logger.LogAsync($"IDE Bridge: {CanonicalName} FAIL - {ex.Code}", Package.DisposalToken, activatePane: true).ConfigureAwait(true);
            ActivityLog.LogError(nameof(VsIdeBridgePackage), ex.ToString());
        }
        catch (Exception ex)
        {
            var failureData = await Runtime.FailureContextService.CaptureAsync(context).ConfigureAwait(true);
            var envelope = new CommandEnvelope
            {
                SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
                Command = CanonicalName,
                RequestId = requestId,
                Success = false,
                StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
                FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                Summary = ex.Message,
                Warnings = [],
                Error = new
                {
                    code = "internal_error",
                    message = ex.Message,
                    details = new { exception = ex.ToString() },
                },
                Data = failureData,
            };

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                await CommandResultWriter.WriteAsync(outputPath, envelope, Package.DisposalToken).ConfigureAwait(false);
            }

            await context.Logger.LogAsync($"IDE Bridge: {CanonicalName} FAIL - internal_error", Package.DisposalToken, activatePane: true).ConfigureAwait(true);
            ActivityLog.LogError(nameof(VsIdeBridgePackage), ex.ToString());
        }
    }

    private string ResolveOutputPath(CommandArguments args)
    {
        var explicitPath = args.GetString("out");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath!;
        }

        var fileName = CanonicalName.Replace("Tools.", string.Empty)
            .Replace('.', '-')
            .ToLowerInvariant() + ".json";
        return Path.Combine(Path.GetTempPath(), "vs-ide-bridge", fileName);
    }
}
