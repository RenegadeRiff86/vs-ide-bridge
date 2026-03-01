using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Services;

internal sealed class IdeStateService
{
    public async Task<JObject> GetStateAsync(EnvDTE80.DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var data = new JObject
        {
            ["solutionPath"] = dte.Solution?.IsOpen == true ? dte.Solution.FullName : string.Empty,
            ["solutionName"] = dte.Solution?.IsOpen == true ? dte.Solution.FileName : string.Empty,
            ["debugMode"] = dte.Debugger.CurrentMode.ToString(),
            ["activeWindow"] = dte.ActiveWindow?.Caption ?? string.Empty,
            ["activeDocument"] = dte.ActiveDocument?.FullName ?? string.Empty,
            ["openDocuments"] = new JArray(dte.Documents.Cast<Document>().Select(document => document.FullName)),
        };

        if (dte.ActiveDocument?.Object("TextDocument") is TextDocument textDocument)
        {
            var selection = textDocument.Selection;
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
}
