using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class WindowService
{
    private const int WindowPollIntervalMilliseconds = 200;

    public async Task<JObject> ListWindowsAsync(DTE2 dte, string? query)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var windows = dte.Windows
            .Cast<Window>()
            .Select(CreateWindowInfo)
            .Where(window => MatchesWindow(window, query))
            .ToArray();

        return new JObject
        {
            ["query"] = query ?? string.Empty,
            ["count"] = windows.Length,
            ["items"] = new JArray(windows),
        };
    }

    public async Task<JObject> ActivateWindowAsync(DTE2 dte, string windowName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var window = ResolveWindow(dte, windowName, allowContains: true);
        window.Activate();
        return CreateWindowInfo(window);
    }

    public async Task<JObject?> WaitForWindowAsync(DTE2 dte, string query, bool activate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var window = TryResolveWindow(dte, query, allowContains: true);
            if (window is not null)
            {
                if (activate)
                {
                    window.Activate();
                }

                return CreateWindowInfo(window);
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(WindowPollIntervalMilliseconds).ConfigureAwait(true);
        }

        return null;
    }

    private static bool MatchesWindow(JObject window, string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var queryText = text!;
        return Contains(window["caption"], queryText) ||
               Contains(window["kind"], queryText) ||
               Contains(window["objectKind"], queryText) ||
               Contains(window["documentPath"], queryText);
    }

    private static JObject CreateWindowInfo(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var documentPath = window.Document?.FullName;
        return new JObject
        {
            ["caption"] = window.Caption ?? string.Empty,
            ["kind"] = window.Kind ?? string.Empty,
            ["objectKind"] = window.ObjectKind ?? string.Empty,
            ["type"] = window.Type.ToString(),
            ["visible"] = window.Visible,
            ["documentPath"] = string.IsNullOrWhiteSpace(documentPath)
                ? string.Empty
                : PathNormalization.NormalizeFilePath(documentPath),
        };
    }

    private static Window ResolveWindow(DTE2 dte, string query, bool allowContains)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var window = TryResolveWindow(dte, query, allowContains);
        if (window is not null)
        {
            return window;
        }

        throw new CommandErrorException("window_not_found", $"Window not found: {query}");
    }

    private static Window? TryResolveWindow(DTE2 dte, string query, bool allowContains)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var trimmed = query.Trim();
        var windows = dte.Windows.Cast<Window>().ToArray();

        foreach (var window in windows)
        {
            if (EqualsWindow(window, trimmed))
            {
                return window;
            }
        }

        if (!allowContains)
        {
            return null;
        }

        var partial = windows.Where(window => MatchesWindowQuery(window, trimmed))
            .ToArray();

        if (partial.Length == 1)
        {
            return partial[0];
        }

        if (partial.Length > 1)
        {
            throw new CommandErrorException(
                "invalid_arguments",
                $"Window query '{query}' matched multiple windows.",
                new
                {
                    query,
                    matches = partial.Select(GetWindowCaption).ToArray(),
                });
        }

        return null;
    }

    private static bool EqualsWindow(Window window, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var caption = window.Caption;
        var objectKind = window.ObjectKind;
        var kind = window.Kind;
        var documentPath = window.Document?.FullName;
        return string.Equals(caption, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(objectKind, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(documentPath, query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesWindowQuery(Window window, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var caption = window.Caption;
        var kind = window.Kind;
        var objectKind = window.ObjectKind;
        var documentPath = window.Document?.FullName;
        return Contains(caption, query) ||
               Contains(kind, query) ||
               Contains(objectKind, query) ||
               Contains(documentPath, query);
    }

    private static bool Contains(string? value, string query)
    {
        var candidate = value;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return candidate!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Contains(JToken? token, string query)
    {
        return token is not null && Contains(token.ToString(), query);
    }

    private static string GetWindowCaption(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return window.Caption ?? string.Empty;
    }
}
