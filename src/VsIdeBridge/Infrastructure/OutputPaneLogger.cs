using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VsIdeBridge.Infrastructure;

internal sealed class OutputPaneLogger(AsyncPackage package)
{
    private const string PaneName = "IDE Bridge";

    private readonly AsyncPackage _package = package;

    public async Task LogAsync(string message, CancellationToken cancellationToken, bool activatePane = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        _dte ??= await _package.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;

        if (_dte is not null)
        {
            _pane ??= GetOrCreatePane(_dte);
            _pane.OutputString($"{message}{Environment.NewLine}");
            if (activatePane)
            {
                _pane.Activate();
            }
        }

        _statusbar ??= await _package.GetServiceAsync(typeof(SVsStatusbar)).ConfigureAwait(true) as IVsStatusbar;

        _statusbar?.SetText(message);
    }

    // DTE, output pane, and status bar are cached after first use; all stable for the VS lifetime.
    // All reads/writes occur on the UI thread (guarded by SwitchToMainThreadAsync above).
    private DTE2? _dte;
    private EnvDTE.OutputWindowPane? _pane;
    private IVsStatusbar? _statusbar;

    private static EnvDTE.OutputWindowPane GetOrCreatePane(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        EnvDTE.OutputWindowPanes panes = dte.ToolWindows.OutputWindow.OutputWindowPanes;
        for (int i = 1; i <= panes.Count; i++)
        {
            EnvDTE.OutputWindowPane pane = panes.Item(i);
            if (string.Equals(pane.Name, PaneName, StringComparison.OrdinalIgnoreCase))
            {
                return pane;
            }
        }

        return panes.Add(PaneName);
    }
}
