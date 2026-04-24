using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Language.CallHierarchy;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private static readonly Guid CallHierarchyServiceGuid = new("66B94087-4468-4938-9899-FA51E2F65FAB");
    private const string CallHierarchyWindowCaption = "Call Hierarchy";
    private const string CallHierarchyWindowObjectKind = "{3822E751-EB69-4B0E-B301-595A9E4C74D5}";

    public async Task<JObject> PopulateNativeCallHierarchyWindowAsync(IdeCommandContext context, JObject managedHierarchy, bool activateWindow)
    {
        if ((bool?)managedHierarchy["available"] != true || managedHierarchy["root"] is not JObject root)
        {
            return CreateNativeCallHierarchyStatus(
                available: false,
                stage: "managed_hierarchy_unavailable",
                detail: "Managed hierarchy did not produce a root item to show in the native Call Hierarchy window.");
        }

        BridgeCallHierarchyMemberItem rootItem = new(root, context.Dte);
        try
        {
            NativeCallHierarchyWindowResolution resolution = await ResolveNativeCallHierarchyWindowAsync(context.Dte, activateWindow);
            if (resolution.Status is not null)
            {
                return resolution.Status;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            resolution.ToolWindowUi!.ClearAllItems();
            resolution.ToolWindowUi.AddRootItem(rootItem);
            return CreateNativeCallHierarchyStatus(
                available: true,
                stage: "success",
                detail: string.Empty,
                resolution.WindowObject,
                resolution.ToolWindowUiSource);
        }
        catch (COMException ex)
        {
            return CreateNativeCallHierarchyStatus(
                available: false,
                stage: "populate_failed",
                detail: ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return CreateNativeCallHierarchyStatus(
                available: false,
                stage: "populate_failed",
                detail: ex.Message);
        }
        catch (InvalidCastException ex)
        {
            return CreateNativeCallHierarchyStatus(
                available: false,
                stage: "populate_failed",
                detail: ex.Message);
        }
    }

    private async Task<NativeCallHierarchyWindowResolution> ResolveNativeCallHierarchyWindowAsync(DTE2 dte, bool activateWindow)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return ResolveNativeCallHierarchyWindowOnUiThread(dte, activateWindow);
    }

    private NativeCallHierarchyWindowResolution ResolveNativeCallHierarchyWindowOnUiThread(DTE2 dte, bool activateWindow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Window? window = TryResolveCallHierarchyWindow(dte);
        if (window is null)
        {
            return NativeCallHierarchyWindowResolution.FromStatus(
                CreateNativeCallHierarchyStatus(
                    available: false,
                    stage: "window_not_found",
                    detail: "The Call Hierarchy tool window was not available after invoking the native command."));
        }

        if (activateWindow)
        {
            window.Activate();
        }

        object? windowObject = TryGetWindowObject(window);
        if (!TryResolveCallHierarchyToolWindowUi(windowObject, out ICallHierarchyToolWindowUI? toolWindowUi, out string toolWindowUiSource)
            || toolWindowUi is null)
        {
            return NativeCallHierarchyWindowResolution.FromStatus(
                CreateNativeCallHierarchyStatus(
                    available: false,
                    stage: "tool_window_ui_missing",
                    detail: "The native Call Hierarchy window did not expose an ICallHierarchyToolWindowUI instance.",
                    windowObject,
                    toolWindowUiSource));
        }

        return NativeCallHierarchyWindowResolution.FromResolved(toolWindowUi, windowObject, toolWindowUiSource);
    }

    private static JObject CreateNativeCallHierarchyStatus(bool available, string stage, string detail, object? windowObject = null, string toolWindowUiSource = "")
    {
        return new JObject
        {
            ["attempted"] = true,
            ["available"] = available,
            ["stage"] = stage,
            ["detail"] = detail,
            ["windowObjectType"] = windowObject?.GetType().FullName ?? string.Empty,
            ["toolWindowUiSource"] = toolWindowUiSource,
        };
    }

    private static Window? TryResolveCallHierarchyWindow(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (Window window in dte.Windows.Cast<Window>())
        {
            if (MatchesCallHierarchyWindow(window))
            {
                return window;
            }
        }

        Window? activeWindow = dte.ActiveWindow;
        return activeWindow is not null && MatchesCallHierarchyWindow(activeWindow) ? activeWindow : null;
    }

    private static bool MatchesCallHierarchyWindow(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string caption = TryGetWindowCaption(window);
        if (string.Equals(caption, CallHierarchyWindowCaption, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string objectKind = TryGetWindowObjectKind(window);
        return string.Equals(objectKind, CallHierarchyWindowObjectKind, StringComparison.OrdinalIgnoreCase);
    }

    private static object? TryGetWindowObject(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return window.Object;
        }
        catch
        {
            return null;
        }
    }

    private static object? TryGetVsShellWindowObject(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsShellUtilities.GetWindowObject(frame);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveCallHierarchyToolWindowUi(object? windowObject, out ICallHierarchyToolWindowUI? toolWindowUi, out string source)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryResolveCallHierarchyToolWindowUiFromService(out toolWindowUi, out source))
        {
            return true;
        }

        if (TryResolveInterface(windowObject, "window.Object", out toolWindowUi, out source))
        {
            return true;
        }

        string[] candidateProperties = ["ToolWindowUI", "Content", "UIElement", "DataContext"];
        foreach (string propertyName in candidateProperties)
        {
            if (!TryGetPropertyValue(windowObject, propertyName, out object? propertyValue) || propertyValue is null)
            {
                continue;
            }

            if (TryResolveInterface(propertyValue, $"window.Object.{propertyName}", out toolWindowUi, out source))
            {
                return true;
            }
        }

        if (TryResolveCallHierarchyToolWindowUiFromFrame(out toolWindowUi, out source))
        {
            return true;
        }

        toolWindowUi = null;
        source = string.Empty;
        return false;
    }

    private static bool TryResolveCallHierarchyToolWindowUiFromService(out ICallHierarchyToolWindowUI? toolWindowUi, out string source)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        toolWindowUi = null;
        source = string.Empty;

        if (TryQueryGlobalService(CallHierarchyServiceGuid, typeof(ICallHierarchyToolWindowUI).GUID, out object? directUiObject)
            && TryResolveInterface(directUiObject, "globalService.ICallHierarchyToolWindowUI", out toolWindowUi, out source))
        {
            return true;
        }

        if (!TryQueryGlobalService(CallHierarchyServiceGuid, typeof(ICallHierarchyUIFactory).GUID, out object? factoryObject)
            || factoryObject is not ICallHierarchyUIFactory factory)
        {
            return false;
        }

        try
        {
            toolWindowUi = factory.CreateToolWindowUI();
            if (toolWindowUi is null)
            {
                source = "globalService.ICallHierarchyUIFactory(null)";
                return false;
            }

            source = "globalService.ICallHierarchyUIFactory";
            return true;
        }
        catch (COMException ex)
        {
            source = "globalService.ICallHierarchyUIFactory(" + ex.GetType().Name + ")";
            toolWindowUi = null;
            return false;
        }
    }

    private static bool TryResolveCallHierarchyToolWindowUiFromFrame(out ICallHierarchyToolWindowUI? toolWindowUi, out string source)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        toolWindowUi = null;
        source = string.Empty;

        IVsWindowFrame? frame = TryFindCallHierarchyWindowFrame();
        if (frame is null)
        {
            return false;
        }

        object?[] frameCandidates =
        [
            TryGetVsShellWindowObject(frame),
            TryGetFrameProperty(frame, __VSFPROPID.VSFPROPID_DocView),
            TryGetFrameProperty(frame, __VSFPROPID.VSFPROPID_ViewHelper),
        ];

        string[] frameCandidateNames =
        [
            "windowFrame.WindowObject",
            "windowFrame.DocView",
            "windowFrame.ViewHelper",
        ];

        for (int index = 0; index < frameCandidates.Length; index++)
        {
            object? frameCandidate = frameCandidates[index];
            if (TryResolveInterface(frameCandidate, frameCandidateNames[index], out toolWindowUi, out source))
            {
                return true;
            }

            if (TryGetPropertyValue(frameCandidate, "ToolWindowUI", out object? propertyValue)
                && TryResolveInterface(propertyValue, frameCandidateNames[index] + ".ToolWindowUI", out toolWindowUi, out source))
            {
                return true;
            }
        }

        return false;
    }

    private static IVsWindowFrame? TryFindCallHierarchyWindowFrame()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell uiShell)
        {
            return null;
        }

        Guid persistenceSlot = new(CallHierarchyWindowObjectKind);
        int hr = uiShell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref persistenceSlot, out IVsWindowFrame frame);
        return hr == 0 ? frame : null;
    }

    private static object? TryGetFrameProperty(IVsWindowFrame frame, __VSFPROPID propertyId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int hr = frame.GetProperty((int)propertyId, out object value);
        return hr == 0 ? value : null;
    }

    private static bool TryQueryGlobalService(Guid serviceGuid, Guid interfaceGuid, out object? serviceObject)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        serviceObject = null;
        if (Package.GetGlobalService(typeof(SVsServiceProvider)) is not Microsoft.VisualStudio.OLE.Interop.IServiceProvider oleServiceProvider)
        {
            return false;
        }

        IntPtr servicePointer = IntPtr.Zero;
        try
        {
            int hr = oleServiceProvider.QueryService(ref serviceGuid, ref interfaceGuid, out servicePointer);
            if (hr != VSConstants.S_OK || servicePointer == IntPtr.Zero)
            {
                return false;
            }

            serviceObject = Marshal.GetObjectForIUnknown(servicePointer);
            return serviceObject is not null;
        }
        catch
        {
            serviceObject = null;
            return false;
        }
        finally
        {
            if (servicePointer != IntPtr.Zero)
            {
                Marshal.Release(servicePointer);
            }
        }
    }

    private static bool TryResolveInterface(object? candidate, string candidatePath, out ICallHierarchyToolWindowUI? toolWindowUi, out string source)
    {
        toolWindowUi = candidate as ICallHierarchyToolWindowUI;
        source = toolWindowUi is null ? string.Empty : candidatePath;
        return toolWindowUi is not null;
    }

    private static bool TryGetPropertyValue(object? instance, string propertyName, out object? value)
    {
        value = null;
        if (instance is null)
        {
            return false;
        }

        PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is null || property.GetIndexParameters().Length != 0)
        {
            return false;
        }

        try
        {
            value = property.GetValue(instance);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static string TryGetWindowCaption(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return window.Caption ?? string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (InvalidCastException)
        {
            return string.Empty;
        }
    }

    private static string TryGetWindowObjectKind(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return window.ObjectKind ?? string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (InvalidCastException)
        {
            return string.Empty;
        }
    }

    private sealed class BridgeCallHierarchyMemberItem(JObject node, DTE2 dte) : ICallHierarchyMemberItem
    {
        private readonly JObject _node = node;
        private readonly DTE2 _dte = dte;
        private readonly IReadOnlyList<CallHierarchySearchCategory> _categories = BuildSupportedCategories(node);

        public IEnumerable<CallHierarchySearchCategory> SupportedSearchCategories => _categories;

        public string NameSeparator => ".";

        public string ContainingNamespaceName => GetContainingNamespaceName(_node);

        public string ContainingTypeName => GetContainingTypeName(_node);

        public string MemberName => (string?)_node["name"] ?? string.Empty;

        public System.Windows.Media.ImageSource DisplayGlyph => null!;

        public string SortText => (string?)_node["fullName"] ?? MemberName;

        public IEnumerable<ICallHierarchyItemDetails> Details => CreateDetails(_node, _dte);

        public bool SupportsNavigateTo => HasLocation(_node);

        public bool SupportsFindReferences => false;

        public bool Valid => true;

        public void StartSearch(string categoryName, CallHierarchySearchScope searchScope, ICallHierarchySearchCallback callback)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.Equals(categoryName, CallHierarchyPredefinedSearchCategoryNames.Callers, StringComparison.Ordinal))
            {
                callback.SearchSucceeded();
                return;
            }

            List<JObject> callers =
            [..
                GetCallers(_node)
                    .Where(caller => MatchesSearchScope(caller, _node, searchScope))
            ];

            callback.ReportProgress(0, callers.Count);
            for (int index = 0; index < callers.Count; index++)
            {
                callback.AddResult(new BridgeCallHierarchyMemberItem(callers[index], _dte));
                callback.ReportProgress(index + 1, callers.Count);
            }

            callback.SearchSucceeded();
        }

        public void SuspendSearch(string categoryName)
        {
        }

        public void ResumeSearch(string categoryName)
        {
        }

        public void CancelSearch(string categoryName)
        {
        }

        public void ItemSelected()
        {
        }

        public void NavigateTo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigateToNode(_dte, _node);
        }

        public void FindReferences()
        {
        }

        private static IReadOnlyList<CallHierarchySearchCategory> BuildSupportedCategories(JObject node)
        {
            return GetCallers(node).Any()
                ? [new CallHierarchySearchCategory(CallHierarchyPredefinedSearchCategoryNames.Callers, "Callers")]
                : Array.Empty<CallHierarchySearchCategory>();
        }
    }

    private sealed class NativeCallHierarchyWindowResolution(ICallHierarchyToolWindowUI? toolWindowUi, object? windowObject, string toolWindowUiSource, JObject? status)
    {

        public ICallHierarchyToolWindowUI? ToolWindowUi { get; } = toolWindowUi;

        public object? WindowObject { get; } = windowObject;

        public string ToolWindowUiSource { get; } = toolWindowUiSource;

        public JObject? Status { get; } = status;

        public static NativeCallHierarchyWindowResolution FromResolved(ICallHierarchyToolWindowUI toolWindowUi, object? windowObject, string toolWindowUiSource)
        {
            return new NativeCallHierarchyWindowResolution(toolWindowUi, windowObject, toolWindowUiSource, null);
        }

        public static NativeCallHierarchyWindowResolution FromStatus(JObject status)
        {
            return new NativeCallHierarchyWindowResolution(null, null, string.Empty, status);
        }
    }

    private sealed class BridgeCallHierarchyItemDetails(string path, int startLine, int startColumn, int endLine, int endColumn, string text, DTE2 dte) : ICallHierarchyItemDetails
    {
        private readonly string _path = path;
        private readonly int _startLine = Math.Max(1, startLine);
        private readonly int _startColumn = Math.Max(1, startColumn);
        private readonly int _endLine = Math.Max(Math.Max(1, startLine), endLine);
        private readonly int _endColumn = Math.Max(1, endColumn);
        private readonly string _text = text;
        private readonly DTE2 _dte = dte;

        public string Text => _text;

        public string File => _path;

        public int StartLine => _startLine;

        public int StartColumn => _startColumn;

        public int EndLine => _endLine;

        public int EndColumn => _endColumn;

        public bool SupportsNavigateTo => !string.IsNullOrWhiteSpace(_path);

        public void NavigateTo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            try
            {
                Window window = _dte.ItemOperations.OpenFile(_path);
                window.Activate();
                if (window.Document?.Object("TextDocument") is TextDocument textDocument)
                {
                    textDocument.Selection.MoveToLineAndOffset(_startLine, _startColumn, false);
                }
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"CallHierarchy Navigate failed: {ex.Message}");
            }
        }
    }

    private static IEnumerable<ICallHierarchyItemDetails> CreateDetails(JObject node, DTE2 dte)
    {
        List<ICallHierarchyItemDetails> details = [];

        foreach (JObject callSite in (node["callSites"] as JArray)?.OfType<JObject>() ?? [])
        {
            if (TryCreateDetail(callSite, (string?)node["preview"] ?? (string?)node["signature"] ?? (string?)node["name"] ?? string.Empty, dte, out ICallHierarchyItemDetails? detail)
                && detail is not null)
            {
                details.Add(detail);
            }
        }

        if (details.Count == 0
            && TryCreateDetail(node, (string?)node["signature"] ?? (string?)node["name"] ?? string.Empty, dte, out ICallHierarchyItemDetails? declarationDetail)
            && declarationDetail is not null)
        {
            details.Add(declarationDetail);
        }

        return details;
    }

    private static bool TryCreateDetail(JObject location, string text, DTE2 dte, out ICallHierarchyItemDetails? detail)
    {
        detail = null;
        string path = PathNormalization.NormalizeFilePath((string?)location["path"] ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        detail = new BridgeCallHierarchyItemDetails(
            path,
            (int?)location["line"] ?? 1,
            (int?)location["column"] ?? 1,
            (int?)location["endLine"] ?? (int?)location["line"] ?? 1,
            (int?)location["endColumn"] ?? (int?)location["column"] ?? 1,
            text,
            dte);
        return true;
    }

    private static IEnumerable<JObject> GetCallers(JObject node)
    {
        return (node["callers"] as JArray)?.OfType<JObject>() ?? [];
    }

    private static bool MatchesSearchScope(JObject candidate, JObject root, CallHierarchySearchScope scope)
    {
        return scope switch
        {
            CallHierarchySearchScope.CurrentDocument => string.Equals((string?)candidate["path"], (string?)root["path"], StringComparison.OrdinalIgnoreCase),
            CallHierarchySearchScope.CurrentProject => string.Equals((string?)candidate["project"], (string?)root["project"], StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static bool HasLocation(JObject node)
    {
        return !string.IsNullOrWhiteSpace((string?)node["path"]);
    }

    private static void NavigateToNode(DTE2 dte, JObject node)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string path = PathNormalization.NormalizeFilePath((string?)node["path"] ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Window window = dte.ItemOperations.OpenFile(path);
            window.Activate();
            if (window.Document?.Object("TextDocument") is TextDocument textDocument)
            {
                textDocument.Selection.MoveToLineAndOffset((int?)node["line"] ?? 1, Math.Max(1, (int?)node["column"] ?? 1), false);
            }
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"CallHierarchy NavigateToNode failed: {ex.Message}");
        }
    }

    private static string GetContainingNamespaceName(JObject node)
    {
        ParseContainingNames(node, out string namespaceName, out _);
        return namespaceName;
    }

    private static string GetContainingTypeName(JObject node)
    {
        ParseContainingNames(node, out _, out string typeName);
        return typeName;
    }

    private static void ParseContainingNames(JObject node, out string namespaceName, out string typeName)
    {
        namespaceName = string.Empty;
        typeName = string.Empty;

        string fullName = (string?)node["fullName"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return;
        }

        int signatureIndex = fullName.IndexOf('(');
        string memberPath = signatureIndex >= 0 ? fullName.Substring(0, signatureIndex) : fullName;
        int memberSeparator = memberPath.LastIndexOf('.');
        if (memberSeparator <= 0)
        {
            return;
        }

        string containingPath = memberPath.Substring(0, memberSeparator);
        int typeSeparator = containingPath.LastIndexOf('.');
        if (typeSeparator < 0)
        {
            typeName = containingPath;
            return;
        }

        namespaceName = containingPath.Substring(0, typeSeparator);
        typeName = containingPath.Substring(typeSeparator + 1);
    }
}
