using System;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class WindowService
{
    public async Task<JObject> ActivateWindowAsync(DTE2 dte, string windowName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        foreach (Window window in dte.Windows)
        {
            if (string.Equals(window.Caption, windowName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(window.ObjectKind, windowName, StringComparison.OrdinalIgnoreCase))
            {
                window.Activate();
                return new JObject
                {
                    ["caption"] = window.Caption,
                    ["objectKind"] = window.ObjectKind ?? string.Empty,
                    ["kind"] = window.Kind ?? string.Empty,
                };
            }
        }

        throw new CommandErrorException("window_not_found", $"Window not found: {windowName}");
    }
}
