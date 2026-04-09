using Microsoft;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class CommandRegistrar
{
    public static readonly System.Guid CommandSet = new("5C519A88-1F81-402D-BD2A-A0110F704494");

    public static async Task InitializeAsync(VsIdeBridgePackage package, IdeBridgeRuntime runtime)
    {
        OleMenuCommandService? commandService = await package.GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(false) as OleMenuCommandService;
        Assumes.Present(commandService);

        RegisterCoreCommands(package, runtime, commandService);
        RegisterSearchNavigationCommands(package, runtime, commandService);
        RegisterBreakpointCommands(package, runtime, commandService);
        RegisterDebugBuildCommands(package, runtime, commandService);
        RegisterSolutionProjectCommands(package, runtime, commandService);
    }

    private static void RegisterCoreCommands(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
    {
        RegisterCommandSafely(() => new IdeCoreCommands.IdeHelpMenuCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeToggleHttpServerMenuCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeRequestApprovalCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeHelpCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeSmokeTestCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeGetStateCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeGetUiSettingsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeCaptureVsWindowCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeWaitForReadyCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeOpenSolutionCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeLaunchVisualStudioCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeCreateSolutionCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeCloseIdeCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new IdeCoreCommands.IdeBatchCommandsCommand(package, runtime, commandService));

        void RegisterCommandSafely(Func<IdeCommandBase> factory)
        {
            TryRegisterCommand(runtime, factory);
        }
    }

    private static void RegisterSearchNavigationCommands(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
    {
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeFindTextCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeFindTextBatchCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeFindFilesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeOpenDocumentCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeListDocumentsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeListOpenTabsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeActivateDocumentCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeCloseDocumentCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeCloseFileCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeCloseAllExceptCurrentCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeSaveDocumentCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeReloadDocumentCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeActivateWindowCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeListWindowsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeExecuteVsCommandCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeFindAllReferencesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeCountReferencesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeShowCallHierarchyCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGetDocumentSliceCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGetSmartContextForQueryCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGoToDefinitionCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGoToImplementationCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGetFileOutlineCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeSearchSymbolsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGetQuickInfoCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGetDocumentSlicesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SearchNavigationCommands.IdeGetFileSymbolsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new PatchCommands.IdeApplyEditorPatchCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new PatchCommands.IdeWriteFileCommand(package, runtime, commandService));

        void RegisterCommandSafely(Func<IdeCommandBase> factory)
        {
            TryRegisterCommand(runtime, factory);
        }
    }

    private static void RegisterBreakpointCommands(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
    {
        RegisterCommandSafely(() => new BreakpointCommands.IdeSetBreakpointCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new BreakpointCommands.IdeListBreakpointsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new BreakpointCommands.IdeRemoveBreakpointCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new BreakpointCommands.IdeClearAllBreakpointsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new BreakpointCommands.IdeEnableBreakpointCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new BreakpointCommands.IdeDisableBreakpointCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new BreakpointCommands.IdeEnableAllBreakpointsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new BreakpointCommands.IdeDisableAllBreakpointsCommand(package, runtime, commandService));

        void RegisterCommandSafely(Func<IdeCommandBase> factory)
        {
            TryRegisterCommand(runtime, factory);
        }
    }

    private static void RegisterDebugBuildCommands(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
    {
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugGetStateCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugStartCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugStopCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugBreakCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugContinueCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugStepOverCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugStepIntoCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugStepOutCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugThreadsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugStackCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugLocalsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugModulesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugWatchCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDebugExceptionsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeDiagnosticsSnapshotCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeBuildConfigurationsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeSetBuildConfigurationCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeBuildSolutionCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeRebuildSolutionCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeGetErrorListCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeGetWarningsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeGetMessagesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeBuildAndCaptureErrorsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new DebugBuildCommands.IdeRunCodeAnalysisCommand(package, runtime, commandService));

        void RegisterCommandSafely(Func<IdeCommandBase> factory)
        {
            TryRegisterCommand(runtime, factory);
        }
    }

    private static void RegisterSolutionProjectCommands(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
    {
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeListProjectsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeAddProjectCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeCreateProjectCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeRemoveProjectCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeSetStartupProjectCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeRenameProjectCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeAddFileToProjectCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeRemoveFileFromProjectCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeSearchSolutionsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeQueryProjectItemsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeQueryProjectPropertiesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeQueryProjectConfigurationsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeQueryProjectReferencesCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeQueryProjectOutputsCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeSetPythonProjectEnvCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeSetPythonStartupFileCommand(package, runtime, commandService));
        RegisterCommandSafely(() => new SolutionProjectCommands.IdeGetPythonStartupFileCommand(package, runtime, commandService));

        void RegisterCommandSafely(Func<IdeCommandBase> factory)
        {
            TryRegisterCommand(runtime, factory);
        }
    }

    private static void TryRegisterCommand(IdeBridgeRuntime runtime, Func<IdeCommandBase> factory)
    {
        try
        {
            runtime.RegisterCommand(factory());
        }
        catch (ArgumentException ex)
        {
            string commandName = factory.Method.ReturnType.FullName ?? factory.Method.ReturnType.Name;
            ActivityLog.LogError(nameof(CommandRegistrar), $"Failed to register command '{commandName}'. {ex}");
        }
    }
}
