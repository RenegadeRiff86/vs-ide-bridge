using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
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

    private sealed class IdeWorkspaceSnapshot
    {
        public string SolutionPath { get; set; } = string.Empty;
        public string SolutionName { get; set; } = string.Empty;
        public string SolutionDirectory { get; set; } = string.Empty;
        public string[] StartupProjects { get; set; } = [];
    }

    private sealed class IdeWindowSnapshot
    {
        public string DebugMode { get; set; } = string.Empty;
        public string ActiveWindow { get; set; } = string.Empty;
        public string ActiveWindowKind { get; set; } = string.Empty;
        public string ActiveDocument { get; set; } = string.Empty;
        public List<string> OpenDocuments { get; set; } = [];
    }

    private sealed class IdeTextSelectionSnapshot
    {
        public int? CaretLine { get; set; }
        public int? CaretColumn { get; set; }
        public int? SelectionStartLine { get; set; }
        public int? SelectionStartColumn { get; set; }
        public int? SelectionEndLine { get; set; }
        public int? SelectionEndColumn { get; set; }
    }

    private sealed class IdeBuildConfigurationSnapshot
    {
        public string ActiveConfiguration { get; set; } = string.Empty;
        public string ActivePlatform { get; set; } = string.Empty;
    }

    private sealed class IdeStateSnapshot
    {
        public IdeWorkspaceSnapshot Workspace { get; set; } = new();
        public IdeWindowSnapshot Window { get; set; } = new();
        public IdeTextSelectionSnapshot Selection { get; set; } = new();
        public IdeBuildConfigurationSnapshot Build { get; set; } = new();
    }

    public async Task<JObject> GetStateAsync(EnvDTE80.DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        IdeStateSnapshot snapshot = CaptureStateSnapshot(dte);
        JObject bridgeState = _bridgeInstanceService.CreateStateData(snapshot.Workspace.SolutionPath);
        JObject watchdogState = _bridgeWatchdogService.GetSnapshot();

        JObject ideState = await Task.Run(() => BuildStatePayload(snapshot, bridgeState, watchdogState)).ConfigureAwait(false);
        return ideState;
    }

    private static IdeStateSnapshot CaptureStateSnapshot(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string solutionPath = dte.Solution?.IsOpen == true ? PathNormalization.NormalizeFilePath(dte.Solution.FullName) : string.Empty;
        string solutionName = dte.Solution?.IsOpen == true ? Path.GetFileName(dte.Solution.FullName) : string.Empty;
        string solutionDirectory = dte.Solution?.IsOpen == true ? Path.GetDirectoryName(dte.Solution.FullName) ?? string.Empty : string.Empty;
        Document? activeDocument = dte.ActiveDocument;
        string? activeDocumentPath = TryGetDocumentFullName(activeDocument);
        TextSelection? selection = TryGetActiveTextSelection(activeDocument, out TextSelection activeSelection) ? activeSelection : null;
        SolutionConfiguration? activeConfig = dte.Solution?.SolutionBuild?.ActiveConfiguration;

        return new IdeStateSnapshot
        {
            Workspace = new IdeWorkspaceSnapshot
            {
                SolutionPath = solutionPath,
                SolutionName = solutionName,
                SolutionDirectory = solutionDirectory,
                StartupProjects = GetStartupProjects(dte),
            },
            Window = new IdeWindowSnapshot
            {
                DebugMode = dte.Debugger.CurrentMode.ToString(),
                ActiveWindow = dte.ActiveWindow?.Caption ?? string.Empty,
                ActiveWindowKind = dte.ActiveWindow?.Kind ?? string.Empty,
                ActiveDocument = string.IsNullOrWhiteSpace(activeDocumentPath) ? string.Empty : PathNormalization.NormalizeFilePath(activeDocumentPath),
                OpenDocuments = GetOpenDocumentPaths(dte),
            },
            Selection = new IdeTextSelectionSnapshot
            {
                CaretLine = selection?.ActivePoint.Line,
                CaretColumn = selection?.ActivePoint.DisplayColumn,
                SelectionStartLine = selection?.TopPoint.Line,
                SelectionStartColumn = selection?.TopPoint.DisplayColumn,
                SelectionEndLine = selection?.BottomPoint.Line,
                SelectionEndColumn = selection?.BottomPoint.DisplayColumn,
            },
            Build = new IdeBuildConfigurationSnapshot
            {
                ActiveConfiguration = activeConfig?.Name ?? string.Empty,
                ActivePlatform = (activeConfig as SolutionConfiguration2)?.PlatformName ?? string.Empty,
            },
        };
    }

    private static JObject BuildStatePayload(IdeStateSnapshot snapshot, JObject bridgeState, JObject watchdogState)
    {
        JObject ideState = new()
        {
            ["solutionPath"] = snapshot.Workspace.SolutionPath,
            ["solutionName"] = snapshot.Workspace.SolutionName,
            ["solutionDirectory"] = snapshot.Workspace.SolutionDirectory,
            ["debugMode"] = snapshot.Window.DebugMode,
            ["activeWindow"] = snapshot.Window.ActiveWindow,
            ["activeWindowKind"] = snapshot.Window.ActiveWindowKind,
            ["activeDocument"] = snapshot.Window.ActiveDocument,
            ["openDocuments"] = new JArray(snapshot.Window.OpenDocuments),
            ["startupProjects"] = new JArray(snapshot.Workspace.StartupProjects),
            ["bridge"] = bridgeState,
            ["watchdog"] = watchdogState,
        };

        ApplyTextSelectionInfo(ideState, snapshot);
        ApplyActiveConfiguration(ideState, snapshot);

        return ideState;
    }

    private static void ApplyTextSelectionInfo(JObject ideState, IdeStateSnapshot snapshot)
    {
        if (!snapshot.Selection.CaretLine.HasValue || !snapshot.Selection.CaretColumn.HasValue)
            return;

        ideState["caretLine"] = snapshot.Selection.CaretLine.Value;
        ideState["caretColumn"] = snapshot.Selection.CaretColumn.Value;
        ideState["selectionStartLine"] = snapshot.Selection.SelectionStartLine ?? snapshot.Selection.CaretLine.Value;
        ideState["selectionStartColumn"] = snapshot.Selection.SelectionStartColumn ?? snapshot.Selection.CaretColumn.Value;
        ideState["selectionEndLine"] = snapshot.Selection.SelectionEndLine ?? snapshot.Selection.CaretLine.Value;
        ideState["selectionEndColumn"] = snapshot.Selection.SelectionEndColumn ?? snapshot.Selection.CaretColumn.Value;
    }

    private static void ApplyActiveConfiguration(JObject ideState, IdeStateSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Build.ActiveConfiguration) && string.IsNullOrWhiteSpace(snapshot.Build.ActivePlatform))
            return;

        ideState["activeConfiguration"] = snapshot.Build.ActiveConfiguration;
        ideState["activePlatform"] = snapshot.Build.ActivePlatform;
    }

    private static List<string> GetOpenDocumentPaths(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        List<string> items = [];
        foreach (Document document in dte.Documents)
        {
            string? fullName = TryGetDocumentFullName(document);
            if (!string.IsNullOrWhiteSpace(fullName))
                items.Add(PathNormalization.NormalizeFilePath(fullName));
        }

        return items;
    }

    private static bool TryGetActiveTextSelection(Document? document, out TextSelection selection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        selection = null!;
        if (document is null) return false;
        try
        {
            if (document.Object("TextDocument") is not TextDocument textDocument) return false;
            selection = textDocument.Selection;
            return selection is not null;
        }
        catch (Exception ex) when (IsDeferredDocumentLoadFailure(ex)) { return false; }
        catch (COMException) { return false; }
    }

    private static string? TryGetDocumentFullName(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document is null)
            return null;

        try
        {
            return document.FullName;
        }
        catch (Exception ex) when (IsDeferredDocumentLoadFailure(ex))
        {
            return null;
        }
    }

    private static bool IsDeferredDocumentLoadFailure(Exception ex)
    {
        return string.Equals(ex.GetType().FullName, "Microsoft.Assumes+InternalErrorException", StringComparison.Ordinal)
            || string.Equals(ex.GetType().Name, "InternalErrorException", StringComparison.Ordinal);
    }

    private static string[] GetStartupProjects(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        object? startupProjects = dte.Solution?.SolutionBuild?.StartupProjects;
        return startupProjects switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => [s],
            object[] arr => [.. arr.OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p))],
            _ => [],
        };
    }
}
