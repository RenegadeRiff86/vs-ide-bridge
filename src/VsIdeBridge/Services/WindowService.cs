using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class WindowService
{
    private const int WindowPollIntervalMilliseconds = 200;
    private const int WindowActivationDelayMilliseconds = 150;
    private const string User32Dll = "user32.dll";
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNotTopMost = new(-2);

    public async Task<JObject> ListWindowsAsync(DTE2 dte, string? query)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        JObject[] windows =
        [..
            dte.Windows
                .Cast<Window>()
                .Select(CreateWindowInfo)
                .Where(window => MatchesWindow(window, query))
        ];

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

        Window window = ResolveWindow(dte, windowName, allowContains: true);
        window.Activate();
        return CreateWindowInfo(window);
    }

    public async Task<JObject?> WaitForWindowAsync(DTE2 dte, string query, bool activate, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            Window? window = TryResolveWindow(dte, query, allowContains: true);
            if (window is null && DateTime.UtcNow >= deadline)
            {
                break;
            }

            if (window is null)
            {
                await Task.Delay(WindowPollIntervalMilliseconds).ConfigureAwait(true);
                continue;
            }

            if (activate)
            {
                window.Activate();
            }

            return CreateWindowInfo(window);

        }

        return null;
    }

    public async Task<JObject> CaptureVsWindowAsync(DTE2 dte, string? outputPath)
    {
        var (windowHandle, caption, resolvedPath) =
            await PrepareWindowCaptureAsync(dte, outputPath).ConfigureAwait(true);

        WindowCaptureResult capture = await Task.Run(
                () => CaptureWindowToFile(windowHandle, resolvedPath),
                CancellationToken.None)
            .ConfigureAwait(false);

        return new JObject
        {
            ["path"] = capture.Path,
            ["width"] = capture.Width,
            ["height"] = capture.Height,
            ["windowCaption"] = caption,
            ["activated"] = true,
            ["topMost"] = true,
        };
    }

    private static async Task<(IntPtr WindowHandle, string Caption, string ResolvedPath)> PrepareWindowCaptureAsync(DTE2 dte, string? outputPath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        IntPtr windowHandle = GetVsMainWindowHandle(dte);
        if (windowHandle == IntPtr.Zero)
        {
            throw new CommandErrorException("window_not_found", "Could not resolve the Visual Studio main window handle.");
        }

        string caption = GetWindowCaptionSafe(dte.MainWindow);
        string resolvedPath = ResolveCapturePath(outputPath);

        ActivateAndPromoteWindow(windowHandle, dte.MainWindow);
        await Task.Delay(WindowActivationDelayMilliseconds).ConfigureAwait(true);
        return (windowHandle, caption, resolvedPath);
    }

    private static bool MatchesWindow(JObject window, string? query)
    {
        string? text = query?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string queryText = text!;
        return Contains(window["caption"], queryText) ||
               Contains(window["kind"], queryText) ||
               Contains(window["objectKind"], queryText) ||
               Contains(window["documentPath"], queryText);
    }

    private static JObject CreateWindowInfo(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string caption = GetWindowCaptionSafe(window);
        string kind = GetWindowKindSafe(window);
        string objectKind = GetWindowObjectKindSafe(window);
        string windowType = GetWindowTypeSafe(window);
        bool visible = GetWindowVisibleSafe(window);
        string documentPath = GetWindowDocumentPathSafe(window);
        return new JObject
        {
            ["caption"] = caption,
            ["kind"] = kind,
            ["objectKind"] = objectKind,
            ["type"] = windowType,
            ["visible"] = visible,
            ["documentPath"] = string.IsNullOrWhiteSpace(documentPath)
                ? string.Empty
                : PathNormalization.NormalizeFilePath(documentPath),
        };
    }

    private static Window ResolveWindow(DTE2 dte, string query, bool allowContains)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Window? window = TryResolveWindow(dte, query, allowContains);
        if (window is not null)
        {
            return window;
        }

        throw new CommandErrorException("window_not_found", $"Window not found: {query}");
    }

    private static Window? TryResolveWindow(DTE2 dte, string query, bool allowContains)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string trimmed = query.Trim();
        Window[] windows = [.. dte.Windows.Cast<Window>()];

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

        Window[] partial = [.. windows.Where(window => MatchesWindowQuery(window, trimmed))];

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

        string caption = GetWindowCaptionSafe(window);
        string objectKind = GetWindowObjectKindSafe(window);
        string kind = GetWindowKindSafe(window);
        string documentPath = GetWindowDocumentPathSafe(window);
        return string.Equals(caption, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(objectKind, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(documentPath, query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesWindowQuery(Window window, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string caption = GetWindowCaptionSafe(window);
        string kind = GetWindowKindSafe(window);
        string objectKind = GetWindowObjectKindSafe(window);
        string documentPath = GetWindowDocumentPathSafe(window);
        return Contains(caption, query) ||
               Contains(kind, query) ||
               Contains(objectKind, query) ||
               Contains(documentPath, query);
    }

    private static bool Contains(string? value, string query)
    {
        string? candidate = value;
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

    private static IntPtr GetVsMainWindowHandle(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            IntPtr hWnd = dte.MainWindow.HWnd;
            if (hWnd != IntPtr.Zero)
            {
                return hWnd;
            }
        }
        catch (COMException ex)
        {
            Trace.TraceWarning($"Failed to read the Visual Studio main window handle from DTE: {ex.Message}");
        }

        return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    }

    private static string ResolveCapturePath(string? outputPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string baseDirectory = Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "screenshots");

        string resolvedPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(baseDirectory, $"vs-window-{DateTime.Now:yyyyMMdd-HHmmss}.png")
            : outputPath!;

        if (!Path.IsPathRooted(resolvedPath))
        {
            resolvedPath = Path.Combine(baseDirectory, resolvedPath);
        }

        string normalizedPath = Path.GetFullPath(resolvedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedPath) ?? baseDirectory);
        return normalizedPath;
    }

    private static void ActivateAndPromoteWindow(IntPtr windowHandle, Window mainWindow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            mainWindow.Activate();
        }
        catch (COMException ex)
        {
            Trace.TraceWarning($"Failed to activate Visual Studio before capture: {ex.Message}");
        }

        ShowWindow(windowHandle, 9);
        SetWindowPos(windowHandle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        SetForegroundWindow(windowHandle);
        BringWindowToTop(windowHandle);
        SetWindowPos(windowHandle, HwndNotTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static WindowCaptureResult CaptureWindowToFile(IntPtr windowHandle, string outputPath)
    {
        if (!GetWindowRect(windowHandle, out RECT rect))
        {
            throw new CommandErrorException("window_capture_failed", "Failed to read the Visual Studio window bounds.");
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new CommandErrorException("window_capture_failed", "The Visual Studio window bounds were empty.");
        }

        using Bitmap bitmap = new(width, height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
        }

        bitmap.Save(outputPath, ImageFormat.Png);
        return new WindowCaptureResult(outputPath, width, height);
    }

    private static string GetWindowCaption(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetWindowCaptionSafe(window);
    }

    private static string GetWindowCaptionSafe(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return window.Caption ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowKindSafe(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return window.Kind ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowObjectKindSafe(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return window.ObjectKind ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowDocumentPathSafe(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return window.Document?.FullName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowTypeSafe(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return window.Type.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool GetWindowVisibleSafe(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return window.Visible;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct WindowCaptureResult(string Path, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport(User32Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport(User32Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport(User32Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport(User32Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport(User32Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
