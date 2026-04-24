using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Diagnostics;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static class Program
{
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("VsIdeBridgeService is Windows-only.");
            Environment.ExitCode = 1;
            return;
        }

        if (args.Length > 0 && args[0].Equals("mcp-server", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                McpServerLog.Write("mcp-server starting");
                McpServerMode.RunAsync(args[1..]).GetAwaiter().GetResult();
                McpServerLog.Write("mcp-server stopped normally");
            }
            catch (Exception ex) when (ex is not null) // top-level MCP stdio host boundary
            {
                McpServerLog.Write($"mcp-server fatal error: {ex}");
                Environment.ExitCode = 1;
            }

            return;
        }

        if (args.Length > 0 && args[0].Equals("mcp-http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                McpServerLog.Write("mcp-http starting");
                McpServerMode.RunHttpAsync(args[1..]).GetAwaiter().GetResult();
                McpServerLog.Write("mcp-http stopped normally");
            }
            catch (Exception ex) when (ex is not null) // top-level MCP HTTP host boundary
            {
                McpServerLog.Write($"mcp-http fatal error: {ex}");
                Environment.ExitCode = 1;
            }

            return;
        }

        try
        {
            ServiceBase.Run(new BridgeService(args));
        }
        catch (Exception ex) when (ex is not null) // top-level Windows service startup boundary
        {
            BootstrapLog($"fatal startup error: {ex}");

            if (Environment.UserInteractive)
            {
                Console.Error.WriteLine($"VsIdeBridgeService failed to start: {ex.Message}");
            }
            else
            {
                try
                {
                    EventLog.WriteEntry("Application", $"VsIdeBridgeService failed to start: {ex}", EventLogEntryType.Error);
                }
                catch (Exception eventLogEx)
                {
                    BootstrapLog($"event log write failed: {eventLogEx}");
                }
            }

            Environment.ExitCode = 1;
        }
    }

    private static void BootstrapLog(string message)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "VsIdeBridgeService-bootstrap.log");
            File.AppendAllText(logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            McpServerLog.WriteException("bootstrap log write failed", ex);
        }
    }
}

internal sealed class BridgeService : ServiceBase
{
    private const string ServiceControlPipeName = "VsIdeBridgeServiceControl";
    private const int ControlPipeBufferSize = 4096;
    private const int ShutdownWaitTimeoutSeconds = 5;

    private readonly string _logPath;
    private readonly TimeSpan _idleSoftTimeout;
    private readonly TimeSpan _idleHardTimeout;
    private readonly object _stateGate = new();

    private CancellationTokenSource? _stopCts;
    private Task? _acceptLoop;
    private Task? _idleLoop;

    private DateTime _lastActivityUtc;
    private int _connectedClients;
    private int _inFlightCommands;
    private bool _draining;

    public BridgeService(string[] args)
    {
        ServiceName = "VsIdeBridgeService";
        CanStop = true;
        CanPauseAndContinue = false;
        AutoLog = false;

        _lastActivityUtc = DateTime.UtcNow;
        _idleSoftTimeout = TimeSpan.FromSeconds(GetIntArg(args, "idle-soft-seconds", 900));
        _idleHardTimeout = TimeSpan.FromSeconds(GetIntArg(args, "idle-hard-seconds", 1200));
        // --enable-http writes the flag file; RestoreState() in OnStart() starts the server.
        if (GetFlagArg(args, "enable-http"))
            HttpServerController.MarkEnabled();
        _logPath = ResolveLogPath();
    }

    protected override void OnStart(string[] args)
    {
        Log("service starting");

        _stopCts = new CancellationTokenSource();
        _lastActivityUtc = DateTime.UtcNow;

        _acceptLoop = AcceptLoopAsync(_stopCts.Token);
        _idleLoop = IdleLoopAsync(_stopCts.Token);
        HttpServerController.RestoreState();

        Log(HttpServerController.IsRunning
            ? $"service started (HTTP MCP on {HttpServerController.Url})"
            : "service started (HTTP MCP disabled)");
    }

    protected override void OnStop()
    {
        Log("service stopping");

        _stopCts?.Cancel();
        HttpServerController.StopAndWait(TimeSpan.FromSeconds(ShutdownWaitTimeoutSeconds));

        Task.WaitAll([.. new[] { _acceptLoop, _idleLoop }.OfType<Task>()], TimeSpan.FromSeconds(ShutdownWaitTimeoutSeconds));

        _stopCts?.Dispose();
        _stopCts = null;

        Log("service stopped");
    }

    private static PipeSecurity CreateControlPipeSecurity()
    {
        PipeSecurity security = new();
        AddPipeAccessRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        AddPipeAccessRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);
        AddPipeAccessRule(security, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), NamedPipeAccessDefaults.ClientReadWriteRights);
        TryAddPipeAccessRule(security, "S-1-15-2-1", NamedPipeAccessDefaults.ClientReadWriteRights);
        return security;
    }

    private static void AddPipeAccessRule(PipeSecurity security, SecurityIdentifier sid, PipeAccessRights rights)
    {
        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
    }

    private static void TryAddPipeAccessRule(PipeSecurity security, string sidValue, PipeAccessRights rights)
    {
        try
        {
            AddPipeAccessRule(security, new SecurityIdentifier(sidValue), rights);
        }
        catch (ArgumentException ex)
        {
            McpServerLog.WriteException($"failed to add pipe access rule for SID '{sidValue}'", ex);
        }
    }
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(
                    ServiceControlPipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    ControlPipeBufferSize,
                    ControlPipeBufferSize,
                    CreateControlPipeSecurity());

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using StreamReader reader = new(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

                while (server.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    HandleEvent(line.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                Log($"control pipe error: {ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (ObjectDisposedException ex)
            {
                Log($"control pipe error: {ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (InvalidOperationException ex)
            {
                Log($"control pipe error: {ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task IdleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            bool shouldLogDraining = false;
            bool shouldStop = false;
            TimeSpan idle;
            int clients;
            int inFlight;

            lock (_stateGate)
            {
                idle = DateTime.UtcNow - _lastActivityUtc;
                clients = _connectedClients;
                inFlight = _inFlightCommands;

                if (inFlight > 0 || clients > 0)
                {
                    continue;
                }

                if (!_draining && idle >= _idleSoftTimeout)
                {
                    _draining = true;
                    shouldLogDraining = true;
                }

                if (_draining && idle >= _idleHardTimeout)
                {
                    shouldStop = true;
                }
            }

            if (shouldLogDraining)
            {
                Log($"service going idle: draining started after {idle.TotalSeconds:F0}s inactivity");
            }

            if (shouldStop)
            {
                Log($"service going idle: stopping after {idle.TotalSeconds:F0}s inactivity (clients={clients}, inFlight={inFlight})");
                Stop();
                return;
            }
        }
    }

    private void HandleEvent(string evt)
    {
        if (string.IsNullOrWhiteSpace(evt))
        {
            return;
        }

        lock (_stateGate)
        {
            switch (evt.ToUpperInvariant())
            {
                case "MCP_REQUEST":
                    _lastActivityUtc = DateTime.UtcNow;
                    _draining = false;
                    break;
                case "COMMAND_START":
                    _inFlightCommands++;
                    _lastActivityUtc = DateTime.UtcNow;
                    _draining = false;
                    break;
                case "COMMAND_END":
                    if (_inFlightCommands > 0)
                    {
                        _inFlightCommands--;
                    }

                    _lastActivityUtc = DateTime.UtcNow;
                    _draining = false;
                    break;
                case "CLIENT_CONNECTED":
                    _connectedClients++;
                    _lastActivityUtc = DateTime.UtcNow;
                    _draining = false;
                    break;
                case "CLIENT_DISCONNECTED":
                    if (_connectedClients > 0)
                    {
                        _connectedClients--;
                    }

                    _lastActivityUtc = DateTime.UtcNow;
                    break;
                case "PING":
                    _lastActivityUtc = DateTime.UtcNow;
                    break;
                default:
                    Log($"unknown control event '{evt}'");
                    break;
            }
        }
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"BridgeService.Log failed: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"BridgeService.Log failed: {ex}");
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"BridgeService.Log failed: {ex}");
        }
    }

    private static int GetIntArg(string[] args, string name, int defaultValue)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals($"--{name}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                return defaultValue;
            }

            return int.TryParse(args[i + 1], out var parsed) && parsed > 0 ? parsed : defaultValue;
        }

        return defaultValue;
    }

    private static bool GetFlagArg(string[] args, string name)
    {
        string flagName = $"--{name}";
        foreach (string arg in args)
        {
            if (arg.Equals(flagName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ResolveLogPath()
    {
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates =
        [
            Path.Combine(commonAppData, "VsIdeBridge"),
            Path.Combine(localAppData, "VsIdeBridge"),
            Path.GetTempPath()
        ];

        foreach (var directory in candidates)
        {
            try
            {
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "service.log");
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"BridgeService.ResolveLogPath failed for '{directory}': {ex}");
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"BridgeService.ResolveLogPath failed for '{directory}': {ex}");
            }
            catch (NotSupportedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"BridgeService.ResolveLogPath failed for '{directory}': {ex}");
            }
        }

        return Path.Combine(Path.GetTempPath(), "service.log");
    }
}
