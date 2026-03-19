using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VsIdeBridge.Services;

internal sealed class SolutionExplorerSyncService
{
    private const string ActivityLogSource = nameof(SolutionExplorerSyncService);
    private const string SyncWithActiveDocumentCommand = "SolutionExplorer.SyncWithActiveDocument";

    public async Task TrySyncToActiveDocumentAsync(DTE2 dte, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        Document? activeDocument = dte.ActiveDocument;
        if (activeDocument is null || string.IsNullOrWhiteSpace(activeDocument.FullName))
        {
            return;
        }

        Window? activeWindow = dte.ActiveWindow;

        try
        {
            dte.ExecuteCommand(SyncWithActiveDocumentCommand, string.Empty);
        }
        catch (COMException ex)
        {
            ActivityLog.LogWarning(
                ActivityLogSource,
                $"Solution Explorer sync failed for '{activeDocument.FullName}': {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(
                ActivityLogSource,
                $"Solution Explorer sync failed for '{activeDocument.FullName}': {ex.Message}");
            return;
        }

        try
        {
            if (activeWindow is not null)
            {
                activeWindow.Activate();
                return;
            }

            activeDocument.Activate();
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(
                ActivityLogSource,
                $"Solution Explorer sync restored selection but could not restore editor focus: {ex.Message}");
        }
    }
}
