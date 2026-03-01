using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using VsIdeBridge.Commands;
using VsIdeBridge.Services;

namespace VsIdeBridge;

internal static class CommandRegistrar
{
    public static readonly System.Guid CommandSet = new("5C519A88-1F81-402D-BD2A-A0110F704494");

    public static async Task InitializeAsync(VsIdeBridgePackage package, IdeBridgeRuntime runtime)
    {
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(false) as OleMenuCommandService;
        Assumes.Present(commandService);

        _ = new IdeCoreCommands.IdeHelpCommand(package, runtime, commandService);
        _ = new IdeCoreCommands.IdeSmokeTestCommand(package, runtime, commandService);
        _ = new IdeCoreCommands.IdeGetStateCommand(package, runtime, commandService);
        _ = new IdeCoreCommands.IdeWaitForReadyCommand(package, runtime, commandService);

        _ = new SearchNavigationCommands.IdeFindTextCommand(package, runtime, commandService);
        _ = new SearchNavigationCommands.IdeFindFilesCommand(package, runtime, commandService);
        _ = new SearchNavigationCommands.IdeOpenDocumentCommand(package, runtime, commandService);
        _ = new SearchNavigationCommands.IdeActivateWindowCommand(package, runtime, commandService);

        _ = new BreakpointCommands.IdeSetBreakpointCommand(package, runtime, commandService);
        _ = new BreakpointCommands.IdeListBreakpointsCommand(package, runtime, commandService);
        _ = new BreakpointCommands.IdeRemoveBreakpointCommand(package, runtime, commandService);
        _ = new BreakpointCommands.IdeClearAllBreakpointsCommand(package, runtime, commandService);

        _ = new DebugBuildCommands.IdeDebugGetStateCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeDebugStartCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeDebugStopCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeDebugBreakCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeDebugContinueCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeDebugStepOverCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeDebugStepIntoCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeDebugStepOutCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeBuildSolutionCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeGetErrorListCommand(package, runtime, commandService);
        _ = new DebugBuildCommands.IdeBuildAndCaptureErrorsCommand(package, runtime, commandService);
    }
}
