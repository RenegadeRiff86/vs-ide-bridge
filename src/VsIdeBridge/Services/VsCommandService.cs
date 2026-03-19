using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class VsCommandService
{
    private const string InternalBridgeCommandPrefix = "Tools.VsIdeBridge";

    public async Task<JObject> ExecuteCommandAsync(DTE2 dte, string commandName, string? commandArgs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        EnsureCommandAllowed(commandName);
        var command = ResolveCommand(dte, commandName);
        try
        {
            dte.ExecuteCommand(command.Name, commandArgs ?? string.Empty);
        }
        catch (COMException ex)
        {
            throw new CommandErrorException(
                "unsupported_operation",
                $"Visual Studio command failed: {command.Name}",
                new { command = command.Name, args = commandArgs ?? string.Empty, exception = ex.Message, hresult = ex.HResult });
        }

        return CreateCommandInfo(command, commandArgs);
    }

    public async Task<JObject> ExecuteSymbolCommandAsync(
        DTE2 dte,
        DocumentService documentService,
        WindowService windowService,
        string[] candidateCommands,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column,
        bool selectWord,
        string resultWindowQuery,
        bool activateResultWindow,
        int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var location = await documentService
            .PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord)
            .ConfigureAwait(true);

        Command? executed = null;
        string? commandError = null;
        foreach (var candidate in candidateCommands)
        {
            var command = TryResolveCommand(dte, candidate);
            if (command is null)
            {
                continue;
            }

            try
            {
                dte.ExecuteCommand(command.Name, string.Empty);
                executed = command;
                break;
            }
            catch (COMException ex)
            {
                commandError = ex.Message;
            }
        }

        if (executed is null)
        {
            throw new CommandErrorException(
                "unsupported_operation",
                $"None of the Visual Studio commands could be executed: {string.Join(", ", candidateCommands)}",
                new { candidates = candidateCommands, error = commandError ?? string.Empty });
        }

        var commandInfo = CreateCommandInfo(executed, string.Empty);
        commandInfo["location"] = location;
        commandInfo["candidateCommands"] = new JArray(candidateCommands);

        var window = await windowService.WaitForWindowAsync(
                dte,
                resultWindowQuery,
                activateResultWindow,
                Math.Max(0, timeoutMs))
            .ConfigureAwait(true);
        commandInfo["resultWindowQuery"] = resultWindowQuery;
        commandInfo["resultWindowActivated"] = window is not null;
        commandInfo["resultWindow"] = window;
        return commandInfo;
    }

    public async Task<JObject> ExecutePositionedCommandAsync(
        DTE2 dte,
        DocumentService documentService,
        string commandName,
        string? commandArgs,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column,
        bool selectWord)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var location = (filePath is not null || documentQuery is not null || line is not null || column is not null)
            ? await documentService
                .PositionTextSelectionAsync(dte, filePath, documentQuery, line, column, selectWord)
                .ConfigureAwait(true)
            : null;

        EnsureCommandAllowed(commandName);
        var command = ResolveCommand(dte, commandName);
        try
        {
            dte.ExecuteCommand(command.Name, commandArgs ?? string.Empty);
        }
        catch (COMException ex)
        {
            throw new CommandErrorException(
                "unsupported_operation",
                $"Visual Studio command failed: {commandName}",
                new { command = commandName, args = commandArgs, error = ex.Message, hresult = ex.HResult });
        }

        var commandInfo = CreateCommandInfo(command, commandArgs);
        if (location is not null)
        {
            commandInfo["location"] = location;
        }
        return commandInfo;
    }

    private static void EnsureCommandAllowed(string commandName)
    {
        if (commandName.StartsWith(InternalBridgeCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandErrorException(
                "command_not_allowed",
                $"Visual Studio bridge UI commands are not callable over automation: {commandName}");
        }
    }

    private static JObject CreateCommandInfo(Command command, string? commandArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return new JObject
        {
            ["command"] = command.Name,
            ["args"] = commandArgs ?? string.Empty,
            ["guid"] = command.Guid ?? string.Empty,
            ["id"] = command.ID,
            ["bindings"] = new JArray(ToStringArray(command.Bindings)),
        };
    }

    private static Command ResolveCommand(DTE2 dte, string commandName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var command = TryResolveCommand(dte, commandName);
        if (command is not null)
        {
            return command;
        }

        throw new CommandErrorException("unsupported_operation", $"Visual Studio command not found: {commandName}");
    }

    private static Command? TryResolveCommand(DTE2 dte, string commandName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return dte.Commands.Item(commandName, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return dte.Commands
            .Cast<Command>()
            .FirstOrDefault(command => MatchesCommandName(command, commandName));
    }

    private static bool MatchesCommandName(Command command, string commandName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ToStringArray(object bindings)
    {
        if (bindings is object[] items)
        {
            return [.. items.Select(item => item?.ToString() ?? string.Empty).Where(item => !string.IsNullOrWhiteSpace(item))];
        }

        return [];
    }
}
