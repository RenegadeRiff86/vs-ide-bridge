using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsIdeBridge.Infrastructure;

internal sealed class OutputPaneLogger
{
    private const string PaneName = "IDE Bridge";

    private readonly AsyncPackage _package;

    public OutputPaneLogger(AsyncPackage package)
    {
        _package = package;
    }

    public async Task LogAsync(string message, CancellationToken cancellationToken, bool activatePane = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var dte = await _package.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;
        if (dte is not null)
        {
            var pane = GetOrCreatePane(dte);
            pane.OutputString($"{message}{Environment.NewLine}");
            if (activatePane)
            {
                pane.Activate();
            }
        }

        if (await _package.GetServiceAsync(typeof(SVsStatusbar)).ConfigureAwait(true) is IVsStatusbar statusbar)
        {
            statusbar.SetText(message);
        }
    }

    private static EnvDTE.OutputWindowPane GetOrCreatePane(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var panes = dte.ToolWindows.OutputWindow.OutputWindowPanes;
        for (var i = 1; i <= panes.Count; i++)
        {
            var pane = panes.Item(i);
            if (string.Equals(pane.Name, PaneName, StringComparison.OrdinalIgnoreCase))
            {
                return pane;
            }
        }

        return panes.Add(PaneName);
    }
}
