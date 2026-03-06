using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class IdeStateService(BridgeInstanceService bridgeInstanceService, BridgeWatchdogService bridgeWatchdogService)
{
    private readonly BridgeInstanceService _bridgeInstanceService = bridgeInstanceService;
    private readonly BridgeWatchdogService _bridgeWatchdogService = bridgeWatchdogService;

    public async Task<JObject> GetStateAsync(EnvDTE80.DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var solutionPath = dte.Solution?.IsOpen == true ? PathNormalization.NormalizeFilePath(dte.Solution.FullName) : string.Empty;
        var activeDocument = dte.ActiveDocument;
        var activeDocumentPath = activeDocument?.FullName;
        var data = new JObject
        {
            ["solutionPath"] = solutionPath,
            ["solutionName"] = dte.Solution?.IsOpen == true ? Path.GetFileName(dte.Solution.FullName) : string.Empty,
            ["solutionDirectory"] = dte.Solution?.IsOpen == true ? Path.GetDirectoryName(dte.Solution.FullName) ?? string.Empty : string.Empty,
            ["debugMode"] = dte.Debugger.CurrentMode.ToString(),
            ["activeWindow"] = dte.ActiveWindow?.Caption ?? string.Empty,
            ["activeWindowKind"] = dte.ActiveWindow?.Kind ?? string.Empty,
            ["activeDocument"] = string.IsNullOrWhiteSpace(activeDocumentPath) ? string.Empty : PathNormalization.NormalizeFilePath(activeDocumentPath),
            ["openDocuments"] = GetOpenDocumentPaths(dte),
            ["startupProjects"] = GetStartupProjects(dte),
            ["bridge"] = _bridgeInstanceService.CreateStateData(solutionPath),
            ["watchdog"] = _bridgeWatchdogService.GetSnapshot(),
        };

        if (TryGetActiveTextSelection(activeDocument, out var selection))
        {
            data["caretLine"] = selection.ActivePoint.Line;
            data["caretColumn"] = selection.ActivePoint.DisplayColumn;
            data["selectionStartLine"] = selection.TopPoint.Line;
            data["selectionStartColumn"] = selection.TopPoint.DisplayColumn;
            data["selectionEndLine"] = selection.BottomPoint.Line;
            data["selectionEndColumn"] = selection.BottomPoint.DisplayColumn;
        }

        var activeConfig = dte.Solution?.SolutionBuild?.ActiveConfiguration;
        if (activeConfig is not null)
        {
            data["activeConfiguration"] = activeConfig.Name ?? string.Empty;
            data["activePlatform"] = (activeConfig as SolutionConfiguration2)?.PlatformName ?? string.Empty;
        }

        return data;
    }

    private static JArray GetOpenDocumentPaths(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var items = new JArray();
        foreach (Document document in dte.Documents)
        {
            var fullName = document.FullName;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            items.Add(PathNormalization.NormalizeFilePath(fullName));
        }

        return items;
    }

    private static bool TryGetActiveTextSelection(Document? document, out TextSelection selection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        selection = null!;
        if (document is null)
        {
            return false;
        }

        try
        {
            if (document.Object("TextDocument") is not TextDocument textDocument)
            {
                return false;
            }

            selection = textDocument.Selection;
            return selection is not null;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static JArray GetStartupProjects(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var startupProjects = dte.Solution?.SolutionBuild?.StartupProjects;
        return startupProjects switch
        {
            string singleProject when !string.IsNullOrWhiteSpace(singleProject) => new JArray(singleProject),
            object[] projects => new JArray(projects
                .OfType<string>()
                .Where(project => !string.IsNullOrWhiteSpace(project))),
            _ => [],
        };
    }
}
