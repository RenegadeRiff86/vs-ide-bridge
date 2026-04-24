using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

/// <summary>
/// Manages the optional HTTP MCP server lifecycle.
/// The enabled state is persisted to a machine-shared flag file so it survives
/// process restarts and stays in sync with the Visual Studio extension toggle.
/// Call <see cref="RestoreState"/> from service startup and
/// <see cref="StopAndWait"/> from service shutdown.
/// </summary>
internal static class HttpServerController
{
    private static readonly string FlagFilePath = HttpServerStatePaths.GetHttpEnabledFlagPath();

    private static readonly string[] ServerArgs = ["--port", "8080"];

    internal const int DefaultPort = 8080;
    internal static string Url => $"http://localhost:{DefaultPort}/";

    private static readonly object Lock = new();
    private static CancellationTokenSource? _cts;
    private static Task? _serverTask;

    /// <summary>Whether the flag file exists (persisted enabled state).</summary>
    public static bool IsEnabled => System.IO.File.Exists(FlagFilePath);

    /// <summary>Whether the server task is currently running.</summary>
    public static bool IsRunning
    {
        get
        {
            lock (Lock)
            {
                return _serverTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Write the enabled flag without starting the server.
    /// Used during service construction so that <see cref="RestoreState"/> starts
    /// the server once OnStart is called.
    /// </summary>
    public static void MarkEnabled()
    {
        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(FlagFilePath)!);
        System.IO.File.WriteAllText(FlagFilePath, string.Empty);
    }

    /// <summary>
    /// Start the server if the flag file exists. Called from service OnStart.
    /// Does not modify the flag file.
    /// </summary>
    public static void RestoreState()
    {
        if (IsEnabled)
            StartCore();
    }

    /// <summary>
    /// Enable: write the flag file and start the server (idempotent).
    /// </summary>
    public static JsonObject Enable()
    {
        MarkEnabled();
        StartCore();
        return BuildStatus();
    }

    /// <summary>
    /// Disable: delete the flag file and stop the server.
    /// </summary>
    public static JsonObject Disable()
    {
        System.IO.File.Delete(FlagFilePath);
        StopCore();
        return BuildStatus();
    }

    /// <summary>Current status snapshot (enabled, running, port, url).</summary>
    public static JsonObject GetStatus() => BuildStatus();

    /// <summary>
    /// Stop the server and wait up to <paramref name="timeout"/> for it to finish.
    /// Does not modify the flag file. Called from service OnStop.
    /// </summary>
    public static void StopAndWait(TimeSpan timeout)
    {
        Task? task;
        lock (Lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            task = _serverTask;
        }
        task?.Wait(timeout);
    }

    private static void StartCore()
    {
        Task? oldTask;
        lock (Lock)
        {
            if (_cts != null && _serverTask is { IsCompleted: false })
                return; // Already running with a live token — do nothing.

            oldTask = _serverTask; // May still be winding down after StopCore.
        }

        // Wait for the previous server to release the port before binding again.
        // Runs outside the lock so we don't block concurrent status reads.
        oldTask?.Wait(TimeSpan.FromSeconds(5));

        lock (Lock)
        {
            if (_cts != null)
                return; // Another caller started the server while we waited.

            CancellationTokenSource cts = new();
            _cts = cts;
            _serverTask = Task.Run(() => McpServerMode.RunHttpAsync(ServerArgs, cts.Token));
        }
    }

    private static void StopCore()
    {
        lock (Lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static JsonObject BuildStatus() => new()
    {
        ["enabled"] = IsEnabled,
        ["running"] = IsRunning,
        ["port"] = DefaultPort,
        ["url"] = Url,
    };
}
