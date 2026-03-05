using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Commands;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

/// <summary>
/// Persistent named pipe server that eliminates per-call PowerShell overhead (~1500 ms → ~50 ms).
/// Discovery file: %TEMP%\vs-ide-bridge\pipes\bridge-{pid}.json
/// Protocol: newline-delimited JSON, one request per line, one response per line.
/// </summary>
internal sealed class PipeServerService : IDisposable
{
    private const int PipeStreamBufferSize = 4096;
    private const int DefaultCommandTimeoutMilliseconds = 120_000;
    private const int DefaultDiagnosticsTimeoutMilliseconds = 10_000;

    private readonly VsIdeBridgePackage _package;
    private readonly IdeBridgeRuntime _runtime;
    private readonly string _discoveryFile;
    private readonly MemoryDiscoveryStore _memoryDiscoveryStore = new();
    private readonly bool _emitDiscoveryJson;
    private readonly bool _emitMemoryDiscovery;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly object _executorSync = new();
    private Task? _listenTask;
    private Task? _activeCommandTask;

    public PipeServerService(VsIdeBridgePackage package, IdeBridgeRuntime runtime)
    {
        _package = package;
        _runtime = runtime;
        var discoveryDir = Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "pipes");
        Directory.CreateDirectory(discoveryDir);
        _discoveryFile = Path.Combine(discoveryDir, $"bridge-{runtime.BridgeInstanceService.ProcessId}.json");
        _emitDiscoveryJson = ReadBooleanEnvironmentVariable("VS_IDE_BRIDGE_EMIT_DISCOVERY_JSON", true);
        var mode = Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_DISCOVERY_MODE");
        _emitMemoryDiscovery = !string.Equals(mode, "json-only", StringComparison.OrdinalIgnoreCase);
    }

    public void Start()
    {
        PurgeStaleDiscoveryFiles();
        WriteDiscoveryFile(string.Empty);
        _ = _package.JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_cts.Token);
                var dte = await _package.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;
                if (dte != null)
                {
                    WriteDiscoveryFile(GetSolutionPath(dte));
                }
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning(nameof(PipeServerService), $"Initial discovery refresh failed: {ex.Message}");
            }
        });
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private void PurgeStaleDiscoveryFiles()
    {
        var discoveryDir = Path.GetDirectoryName(_discoveryFile)!;
        try
        {
            foreach (var file in Directory.GetFiles(discoveryDir, "bridge-*.json"))
            {
                if (string.Equals(file, _discoveryFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                var stem = Path.GetFileNameWithoutExtension(file); // "bridge-12345"
                var dash = stem.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(stem.Substring(dash + 1), out var pid))
                {
                    try { Process.GetProcessById(pid); }
                    catch (ArgumentException) { File.Delete(file); } // process gone
                }
            }
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(nameof(PipeServerService), $"Failed to purge stale discovery files: {ex.Message}");
        }
    }

    private void WriteDiscoveryFile(string? solutionPath)
    {
        var record = _runtime.BridgeInstanceService.CreateDiscoveryRecord(solutionPath);

        if (_emitDiscoveryJson)
        {
            try
            {
                var discoveryJson = JsonConvert.SerializeObject(record);
                File.WriteAllText(_discoveryFile, discoveryJson, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning(nameof(PipeServerService), $"Failed to update discovery file: {ex.Message}");
            }
        }

        if (_emitMemoryDiscovery)
        {
            try
            {
                _memoryDiscoveryStore.Upsert(record);
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning(nameof(PipeServerService), $"Failed to update memory discovery store: {ex.Message}");
            }
        }
    }

    private static bool ReadBooleanEnvironmentVariable(string name, bool defaultValue)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (bool.TryParse(raw, out var parsed))
            {
                return parsed;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                return numeric != 0;
            }

            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string GetSolutionPath(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return dte.Solution?.FullName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static JArray BuildBatchSteps(PipeRequest request)
    {
        var steps = new JArray();
        if (request.Batch == null)
        {
            return steps;
        }

        foreach (var batchRequest in request.Batch)
        {
            steps.Add(new JObject
            {
                ["id"] = batchRequest.Id is null ? JValue.CreateNull() : batchRequest.Id,
                ["command"] = batchRequest.Command ?? string.Empty,
                ["args"] = batchRequest.Args ?? string.Empty,
            });
        }

        return steps;
    }

    private static bool ShouldRevealActivity(string commandName)
    {
        return string.Equals(commandName, "Tools.IdeApplyUnifiedDiff", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "apply-diff", StringComparison.OrdinalIgnoreCase)
            || commandName.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0
            || commandName.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0
            || commandName.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0
            || commandName.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _runtime.BridgeInstanceService.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                ActivityLog.LogError(nameof(PipeServerService), $"Failed to create pipe server instance: {ex.Message}");
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                ActivityLog.LogError(nameof(PipeServerService), $"Pipe accept error: {ex.Message}");
                pipe.Dispose();
                continue;
            }

            // Fire-and-forget: handle each connection on the thread pool
            _ = HandleConnectionAsync(pipe, ct);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: PipeStreamBufferSize, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: PipeStreamBufferSize, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        break; // client disconnected mid-read
                    }

                    if (line == null) break; // clean EOF

                    var responseLine = await ExecuteRequestAsync(line, ct).ConfigureAwait(false);
                    await writer.WriteLineAsync(responseLine).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                ActivityLog.LogError(nameof(PipeServerService), $"Pipe connection error: {ex.Message}");
            }
        }
    }

    private async Task<string> ExecuteRequestAsync(string requestJson, CancellationToken ct)
    {
        PipeRequest request;
        try
        {
            request = JsonConvert.DeserializeObject<PipeRequest>(requestJson)
                ?? throw new CommandErrorException("invalid_request", "Could not parse request JSON.");
        }
        catch (JsonException ex)
        {
            var envelope = BuildEnvelope(
                string.Empty,
                null,
                false,
                "Could not parse request JSON.",
                new JObject(),
                new JArray(),
                new { code = "invalid_request", message = ex.Message },
                DateTimeOffset.UtcNow);
            return JsonConvert.SerializeObject(envelope);
        }

        var hasBatch = request.Batch is { Count: > 0 };
        var commandName = hasBatch
            ? (!string.IsNullOrWhiteSpace(request.Command) ? request.Command : "Tools.IdeBatchCommands")
            : (request.Command ?? string.Empty);
        var timeoutMilliseconds = ResolveTimeoutMilliseconds(commandName, request.Args, hasBatch);
        var isDiagnosticsCommand = IsDiagnosticsCommand(commandName);
        var startedAt = DateTimeOffset.UtcNow;

        Task<string>? executionTask;
        lock (_executorSync)
        {
            if (_activeCommandTask is { IsCompleted: false })
            {
                var busyEnvelope = BuildEnvelope(
                    commandName,
                    request.Id,
                    false,
                    "Another bridge command is still running.",
                    new JObject(),
                    new JArray(),
                    new { code = "command_in_progress", message = "Another bridge command is still running.", details = new { active = true } },
                    startedAt);
                return JsonConvert.SerializeObject(busyEnvelope);
            }

            executionTask = ExecuteRequestCoreAsync(request, commandName, hasBatch, timeoutMilliseconds, isDiagnosticsCommand, ct);
            _activeCommandTask = executionTask;
        }

        var response = await executionTask.ConfigureAwait(false);
        return response;
    }

    private async Task<string> ExecuteRequestCoreAsync(PipeRequest request, string commandName, bool hasBatch, int timeoutMilliseconds, bool isDiagnosticsCommand, CancellationToken serverCancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var commandStopwatch = Stopwatch.StartNew();
        string? requestId = request.Id;
        IdeCommandContext? failureContext = null;
        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        commandCts.CancelAfter(timeoutMilliseconds);
        var commandToken = commandCts.Token;

        try
        {
            await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} start (timeout={timeoutMilliseconds}ms)", commandToken).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            CommandExecutionResult result = null!;
            var executionTask = Task.Run(async () =>
            {
                var dte = await GetDteAsync(commandToken).ConfigureAwait(false);
                Assumes.Present(dte);

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(commandToken);
                WriteDiscoveryFile(GetSolutionPath(dte!));

                var ctx = new IdeCommandContext(_package, dte!, _runtime.Logger, _runtime, commandToken);
                failureContext = ctx;
                await _runtime.Logger.LogAsync($"IDE Bridge: {commandName} requested", commandToken).ConfigureAwait(false);

                if (hasBatch)
                {
                    var steps = BuildBatchSteps(request);
                    result = await IdeCoreCommands.ExecuteBatchAsync(ctx, steps, request.StopOnError ?? false).ConfigureAwait(false);
                    return;
                }

                if (!_runtime.TryGetCommand(commandName, out var cmd))
                    throw new CommandErrorException("command_not_found", $"Unknown command: '{commandName}'.");

                var args = CommandArgumentParser.Parse(request.Args);
                result = await cmd.ExecuteDirectAsync(ctx, args).ConfigureAwait(false);
            }, commandToken);

            await executionTask.ConfigureAwait(false);

            await _runtime.Logger.LogAsync(
                $"IDE Bridge: {commandName} OK - {result.Summary}",
                commandToken,
                activatePane: ShouldRevealActivity(commandName)).ConfigureAwait(false);
            await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} end (duration={commandStopwatch.ElapsedMilliseconds}ms)", commandToken).ConfigureAwait(false);
            var envelope = BuildEnvelope(commandName, requestId, true, result.Summary, result.Data, result.Warnings, null, startedAt);
            return JsonConvert.SerializeObject(envelope);
        }
        catch (OperationCanceledException) when (commandCts.IsCancellationRequested && !serverCancellationToken.IsCancellationRequested)
        {
            var errorCode = isDiagnosticsCommand ? "ide_blocked" : "timeout";
            var summary = isDiagnosticsCommand
                ? "IDE appears blocked (modal dialog or busy state) while processing the command."
                : $"Command timed out after {timeoutMilliseconds} ms.";
            var details = new
            {
                timeoutMs = timeoutMilliseconds,
                durationMs = commandStopwatch.ElapsedMilliseconds,
                reason = isDiagnosticsCommand ? "ui_not_responsive" : "command_timeout",
            };
            var errorObj = new { code = errorCode, message = summary, details };
            var failureData = await _runtime.FailureContextService.CaptureAsync(failureContext).ConfigureAwait(false);
            try
            {
                await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} timeout (duration={commandStopwatch.ElapsedMilliseconds}ms)", CancellationToken.None, activatePane: true).ConfigureAwait(false);
            }
            catch
            {
            }

            var envelope = BuildEnvelope(commandName, requestId, false, summary, failureData, new JArray(), errorObj, startedAt);
            return JsonConvert.SerializeObject(envelope);
        }
        catch (CommandErrorException ex)
        {
            await _runtime.Logger.LogAsync($"IDE Bridge: {commandName} FAIL - {ex.Code}", CancellationToken.None, activatePane: true).ConfigureAwait(false);
            var errorObj = new { code = ex.Code, message = ex.Message, details = ex.Details };
            var failureData = await _runtime.FailureContextService.CaptureAsync(failureContext).ConfigureAwait(false);
            await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} end (duration={commandStopwatch.ElapsedMilliseconds}ms, failed={ex.Code})", CancellationToken.None).ConfigureAwait(false);
            var envelope = BuildEnvelope(commandName, requestId, false, ex.Message, failureData, new JArray(), errorObj, startedAt);
            return JsonConvert.SerializeObject(envelope);
        }
        catch (Exception ex)
        {
            await _runtime.Logger.LogAsync($"IDE Bridge: {commandName} FAIL - internal_error", CancellationToken.None, activatePane: true).ConfigureAwait(false);
            var errorObj = new { code = "internal_error", message = ex.Message, details = new { exception = ex.ToString() } };
            var failureData = await _runtime.FailureContextService.CaptureAsync(failureContext).ConfigureAwait(false);
            await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} end (duration={commandStopwatch.ElapsedMilliseconds}ms, failed=internal_error)", CancellationToken.None).ConfigureAwait(false);
            var envelope = BuildEnvelope(commandName, requestId, false, ex.Message, failureData, new JArray(), errorObj, startedAt);
            return JsonConvert.SerializeObject(envelope);
        }
        finally
        {
            lock (_executorSync)
            {
                if (_activeCommandTask?.IsCompleted ?? false)
                {
                    _activeCommandTask = null;
                }
            }
        }
    }

    private async Task<DTE2?> GetDteAsync(CancellationToken cancellationToken)
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        return await _package.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;
    }

    private static bool IsDiagnosticsCommand(string commandName)
    {
        return string.Equals(commandName, "Tools.IdeWaitForReady", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeGetErrorList", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeGetWarnings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeBuildAndCaptureErrors", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeDiagnosticsSnapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "errors", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "warnings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "build-errors", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveTimeoutMilliseconds(string commandName, string? rawArgs, bool isBatch)
    {
        if (isBatch)
        {
            return DefaultCommandTimeoutMilliseconds;
        }

        var defaultTimeout = IsDiagnosticsCommand(commandName)
            ? DefaultDiagnosticsTimeoutMilliseconds
            : DefaultCommandTimeoutMilliseconds;

        if (string.IsNullOrWhiteSpace(rawArgs))
        {
            return defaultTimeout;
        }

        try
        {
            var args = CommandArgumentParser.Parse(rawArgs);
            var requested = args.GetInt32("timeout-ms", defaultTimeout);
            return requested > 0 ? requested : defaultTimeout;
        }
        catch (CommandErrorException)
        {
            return defaultTimeout;
        }
    }

    private static CommandEnvelope BuildEnvelope(
        string command,
        string? requestId,
        bool success,
        string summary,
        JToken data,
        JArray warnings,
        object? error,
        DateTimeOffset startedAt)
    {
        return new CommandEnvelope
        {
            SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
            Command = command,
            RequestId = requestId,
            Success = success,
            StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
            FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            Summary = summary,
            Warnings = warnings,
            Error = error,
            Data = data,
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (_emitDiscoveryJson && File.Exists(_discoveryFile))
                File.Delete(_discoveryFile);
        }
        catch { }

        if (_emitMemoryDiscovery)
        {
            try
            {
                _memoryDiscoveryStore.Remove(_runtime.BridgeInstanceService.InstanceId);
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning(nameof(PipeServerService), $"Failed to remove memory discovery entry: {ex.Message}");
            }
        }
    }
}
