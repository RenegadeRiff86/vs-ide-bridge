using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Commands;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

/// <summary>
/// Persistent named pipe server that eliminates per-call PowerShell overhead (~1500 ms → ~50 ms).
/// Discovery file: %TEMP%\vs-ide-bridge\pipes\bridge-{pid}.json
/// Protocol: newline-delimited JSON, one request per line, one response per line.
/// </summary>
internal sealed class PipeServerService : IDisposable
{
    private readonly VsIdeBridgePackage _package;
    private readonly IdeBridgeRuntime _runtime;
    private readonly PipeServerDiscoveryCoordinator _discovery;
    private readonly CancellationTokenSource _cts = new();
    private readonly PipeServerMutableState _state = new();
    private readonly SemaphoreSlim _commandQueue = new(1, 1);
    private Task? _listenTask;
    private DTE2? _dte; // cached after first use; stable for the lifetime of the VS instance

    public PipeServerService(VsIdeBridgePackage package, IdeBridgeRuntime runtime)
    {
        _package = package;
        _runtime = runtime;
        _discovery = new PipeServerDiscoveryCoordinator(package, runtime, _cts);
    }

    public void Start()
    {
        _discovery.Start();
        _listenTask = Task.Factory.StartNew(
            () => ListenLoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public void UpdateDiscovery(string? solutionPath)
    {
        _discovery.UpdateDiscovery(solutionPath);
    }

    private static JArray BuildBatchSteps(PipeRequest request)
    {
        JArray steps = [];
        if (request.Batch == null)
        {
            return steps;
        }

        foreach (var batchRequest in request.Batch)
        {
            steps.Add(new JObject
            {
                ["id"] = (JToken?)batchRequest.Id ?? JValue.CreateNull(),
                ["command"] = batchRequest.Command ?? string.Empty,
                ["args"] = batchRequest.Args?.DeepClone() ?? JValue.CreateNull(),
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
                    PipeOptions.Asynchronous,
                     PipeServerConstants.PipeStreamBufferSize,
                     PipeServerConstants.PipeStreamBufferSize,
                    PipeServerSupport.CreatePipeSecurity());
            }
            catch (Exception ex) when (ex is not null) // pipe creation can throw Win32Exception, UnauthorizedAccessException, or IOException
            {
                ActivityLog.LogWarning(nameof(PipeServerService), $"Failed to create pipe server instance: {ex.Message}");
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
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
            catch (Exception ex) when (ex is not null) // pipe accept boundary
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
                using StreamReader reader = new(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: PipeServerConstants.PipeStreamBufferSize, leaveOpen: true);
                using StreamWriter writer = new(pipe, new UTF8Encoding(false), bufferSize: PipeServerConstants.PipeStreamBufferSize, leaveOpen: true)
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

                    string responseLine;
                    try
                    {
                        responseLine = await ExecuteRequestAsync(line, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not null) // request execution boundary
                    {
                        // ExecuteRequestAsync should handle all exceptions internally, but if it
                        // escapes (e.g. OperationCanceledException from WaitAsync during VS shutdown),
                        // write an error response so the client never receives a raw EOF.
                        string msg = ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        responseLine = $"{{\"success\":false,\"summary\":\"{msg}\",\"data\":null,\"error\":{{\"code\":\"internal_error\",\"message\":\"Bridge server interrupted: {msg}\"}}}}";
                    }
                    try
                    {
                        await writer.WriteLineAsync(responseLine).ConfigureAwait(false);
                    }
                    catch
                    {
                        break; // pipe broke during write — client already disconnected
                    }
                }
            }
            catch (Exception ex) when (ex is not null) // pipe connection boundary
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
            CommandEnvelope envelope = new()
            {
                SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
                Command = string.Empty,
                RequestId = null,
                Success = false,
                StartedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                Summary = "Could not parse request JSON.",
                Warnings = [],
                Error = new { code = "invalid_request", message = ex.Message },
                Data = new JObject(),
            };
            return JsonConvert.SerializeObject(envelope);
        }

        bool hasBatch = request.Batch is { Count: > 0 };
        string commandName = hasBatch
            ? (!string.IsNullOrWhiteSpace(request.Command) ? request.Command : "Tools.IdeBatchCommands")
            : (request.Command ?? string.Empty);
        int timeoutMilliseconds = ResolveTimeoutMilliseconds(commandName, request.Args, hasBatch);
        bool isDiagnosticsCommand = IsDiagnosticsCommand(commandName);
        DateTimeOffset enqueuedAt = DateTimeOffset.UtcNow;
        int queueCount = Interlocked.Increment(ref _state.QueuedCommandCount);
        int positionAtEnqueue = Math.Max(0, queueCount - 1);
        bool acquired = false;
        try
        {
            await _commandQueue.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            double queueWaitMs = (startedAt - enqueuedAt).TotalMilliseconds;
            return await ExecuteRequestCoreAsync(
                request,
                commandName,
                hasBatch,
                timeoutMilliseconds,
                isDiagnosticsCommand,
                ct,
                enqueuedAt,
                startedAt,
                positionAtEnqueue,
                queueWaitMs).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _state.QueuedCommandCount);
            if (acquired)
            {
                _commandQueue.Release();
            }
        }
    }

    private async Task<string> ExecuteRequestCoreAsync(PipeRequest request, string commandName, bool hasBatch, int timeoutMilliseconds, bool isDiagnosticsCommand, CancellationToken serverCancellationToken, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs)
    {
        Stopwatch commandStopwatch = Stopwatch.StartNew();
        string? requestId = request.Id;
        bool completionRecorded = false;
        IdeCommandContext? failureContext = null;
        using CancellationTokenSource commandCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        commandCts.CancelAfter(timeoutMilliseconds);
        CancellationToken commandToken = commandCts.Token;
        _runtime.BridgeWatchdogService.RecordCommandStarted(commandName, requestId);

        try
        {
            await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} start (timeout={timeoutMilliseconds}ms)", commandToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null) // best-effort trace logging
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        try
        {
            JoinableTask<CommandExecutionResult> executionTask = _package.JoinableTaskFactory.RunAsync(() => ExecuteCommandAsync(
                request,
                commandName,
                hasBatch,
                commandToken,
                ctx => failureContext = ctx));
            CommandExecutionResult commandResult = await AwaitCommandExecutionAsync(
                executionTask,
                timeoutMilliseconds,
                commandCts,
                commandToken,
                serverCancellationToken).ConfigureAwait(false);
            return await CompleteSuccessfulRequestAsync(
                commandName,
                requestId,
                commandResult,
                commandStopwatch,
                commandToken,
                enqueuedAtUtc,
                startedAtUtc,
                queuePositionAtEnqueue,
                queueWaitMs,
                onCompleted: () => completionRecorded = true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (commandCts.IsCancellationRequested && !serverCancellationToken.IsCancellationRequested)
        {
            return await HandleTimedOutRequestAsync(
                commandName,
                requestId,
                isDiagnosticsCommand,
                timeoutMilliseconds,
                commandStopwatch,
                failureContext,
                enqueuedAtUtc,
                startedAtUtc,
                queuePositionAtEnqueue,
                queueWaitMs,
                onCompleted: () => completionRecorded = true).ConfigureAwait(false);
        }
        catch (CommandErrorException ex)
        {
            return await HandleCommandFailureAsync(
                commandName,
                requestId,
                ex,
                commandStopwatch,
                failureContext,
                enqueuedAtUtc,
                startedAtUtc,
                queuePositionAtEnqueue,
                queueWaitMs,
                onCompleted: () => completionRecorded = true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null) // top-level request envelope boundary
        {
            return await HandleInternalRequestFailureAsync(
                commandName,
                requestId,
                ex,
                commandStopwatch,
                failureContext,
                enqueuedAtUtc,
                startedAtUtc,
                queuePositionAtEnqueue,
                queueWaitMs,
                onCompleted: () => completionRecorded = true).ConfigureAwait(false);
        }
        finally
        {
            if (!completionRecorded)
            {
                _runtime.BridgeWatchdogService.RecordCommandCompleted(commandName, requestId, success: false, durationMs: commandStopwatch.Elapsed.TotalMilliseconds, "internal_error");
            }
        }
    }

    private async Task<string> CompleteSuccessfulRequestAsync(string commandName, string? requestId, CommandExecutionResult commandResult, Stopwatch commandStopwatch, CancellationToken commandToken, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs, Action onCompleted)
    {
        await _runtime.Logger.LogAsync(
            $"IDE Bridge: {commandName} OK - {commandResult.Summary}",
            commandToken,
            activatePane: ShouldRevealActivity(commandName)).ConfigureAwait(false);
        await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} end (duration={commandStopwatch.ElapsedMilliseconds}ms)", commandToken).ConfigureAwait(false);
        _runtime.BridgeWatchdogService.RecordCommandCompleted(commandName, requestId, success: true, durationMs: commandStopwatch.Elapsed.TotalMilliseconds, errorCode: null);
        onCompleted();
        return PipeServerSupport.SerializeSuccessEnvelope(
            commandName,
            requestId,
            commandResult,
            enqueuedAtUtc,
            startedAtUtc,
            queuePositionAtEnqueue,
            queueWaitMs);
    }

    private async Task<string> HandleTimedOutRequestAsync(string commandName, string? requestId, bool isDiagnosticsCommand, int timeoutMilliseconds, Stopwatch commandStopwatch, IdeCommandContext? failureContext, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs, Action onCompleted)
    {
        string errorCode = isDiagnosticsCommand ? "ide_blocked" : "timeout";
        string summary = isDiagnosticsCommand
            ? "IDE appears blocked (modal dialog or busy state) while processing the command."
            : $"Command timed out after {timeoutMilliseconds} ms.";
        CommandTimeoutDetails details = new(
            TimeoutMs: timeoutMilliseconds,
            DurationMs: commandStopwatch.ElapsedMilliseconds,
            Reason: isDiagnosticsCommand ? "ui_not_responsive" : "command_timeout");
        CommandTimeoutError errorObj = new(Code: errorCode, Message: summary, Details: details);
        JObject failureData = await _runtime.FailureContextService.CaptureAsync(failureContext).ConfigureAwait(false);
        try
        {
            await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} timeout (duration={commandStopwatch.ElapsedMilliseconds}ms)", CancellationToken.None, activatePane: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null) // best-effort timeout logging
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        _runtime.BridgeWatchdogService.RecordCommandCompleted(commandName, requestId, success: false, durationMs: commandStopwatch.Elapsed.TotalMilliseconds, errorCode);
        onCompleted();
        return PipeServerSupport.SerializeFailureEnvelope(
            commandName,
            requestId,
            summary,
            failureData,
            errorObj,
            enqueuedAtUtc,
            startedAtUtc,
            queuePositionAtEnqueue,
            queueWaitMs);
    }

    private async Task<string> HandleCommandFailureAsync(string commandName, string? requestId, CommandErrorException ex, Stopwatch commandStopwatch, IdeCommandContext? failureContext, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs, Action onCompleted)
    {
        await _runtime.Logger.LogAsync($"IDE Bridge: {commandName} FAIL - {ex.Code}", CancellationToken.None, activatePane: true).ConfigureAwait(false);
        JObject errorObj = new()
        {
            ["code"] = ex.Code,
            ["message"] = ex.Message,
            ["details"] = ex.Details is null ? null : JToken.FromObject(ex.Details),
        };
        JToken failureData = PipeServerSupport.IsCompactCommandError(ex.Code)
            ? PipeServerSupport.BuildCompactCommandErrorData(commandName, ex)
            : await _runtime.FailureContextService.CaptureAsync(failureContext).ConfigureAwait(false);
        await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} end (duration={commandStopwatch.ElapsedMilliseconds}ms, failed={ex.Code})", CancellationToken.None).ConfigureAwait(false);
        _runtime.BridgeWatchdogService.RecordCommandCompleted(commandName, requestId, success: false, durationMs: commandStopwatch.Elapsed.TotalMilliseconds, ex.Code);
        onCompleted();
        return PipeServerSupport.SerializeFailureEnvelope(
            commandName,
            requestId,
            ex.Message,
            failureData,
            errorObj,
            enqueuedAtUtc,
            startedAtUtc,
            queuePositionAtEnqueue,
            queueWaitMs);
    }

    private async Task<string> HandleInternalRequestFailureAsync(string commandName, string? requestId, Exception ex, Stopwatch commandStopwatch, IdeCommandContext? failureContext, DateTimeOffset enqueuedAtUtc, DateTimeOffset startedAtUtc, int queuePositionAtEnqueue, double queueWaitMs, Action onCompleted)
    {
        await _runtime.Logger.LogAsync($"IDE Bridge: {commandName} FAIL - internal_error", CancellationToken.None, activatePane: true).ConfigureAwait(false);
        JObject errorObj = new()
        {
            ["code"] = "internal_error",
            ["message"] = ex.Message,
            ["details"] = new JObject
            {
                ["exception"] = ex.ToString(),
            },
        };
        JObject failureData = await _runtime.FailureContextService.CaptureAsync(failureContext).ConfigureAwait(false);
        await _runtime.Logger.LogAsync($"IDE Bridge Trace: {commandName} end (duration={commandStopwatch.ElapsedMilliseconds}ms, failed=internal_error)", CancellationToken.None).ConfigureAwait(false);
        _runtime.BridgeWatchdogService.RecordCommandCompleted(commandName, requestId, success: false, durationMs: commandStopwatch.Elapsed.TotalMilliseconds, "internal_error");
        onCompleted();
        return PipeServerSupport.SerializeFailureEnvelope(
            commandName,
            requestId,
            ex.Message,
            failureData,
            errorObj,
            enqueuedAtUtc,
            startedAtUtc,
            queuePositionAtEnqueue,
            queueWaitMs);
    }

    private async Task<CommandExecutionResult> ExecuteCommandAsync(PipeRequest request, string commandName, bool hasBatch, CancellationToken commandToken, Action<IdeCommandContext> captureFailureContext)
    {
        DTE2? dte = await GetDteAsync(commandToken).ConfigureAwait(false);
        Assumes.Present(dte);

        string? solutionPath = await _discovery.CaptureSolutionPathAsync(dte!, commandToken).ConfigureAwait(false);
        _discovery.UpdateDiscovery(solutionPath);

        IdeCommandContext ctx = new(_package, dte!, _runtime.Logger, _runtime, commandToken);
        captureFailureContext(ctx);
        await _runtime.Logger.LogAsync($"IDE Bridge: {commandName} requested", commandToken).ConfigureAwait(false);

        if (hasBatch)
        {
            JArray steps = BuildBatchSteps(request);
            CommandExecutionResult batchResult = await IdeCoreCommands.ExecuteBatchAsync(ctx, steps, request.StopOnError ?? false).ConfigureAwait(false);
            await _discovery.RefreshAfterCommandAsync(dte!, solutionPath, commandToken).ConfigureAwait(false);
            return batchResult;
        }

        if (!_runtime.TryGetCommand(commandName, out IdeCommandBase cmd))
        {
            throw new CommandErrorException("command_not_found", $"Unknown command: '{commandName}'.");
        }

        CommandArguments args = CommandArgumentParser.Parse(request.Args);
        CommandExecutionResult result = await cmd.ExecuteDirectAsync(ctx, args).ConfigureAwait(false);
        await _discovery.RefreshAfterCommandAsync(dte!, solutionPath, commandToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<CommandExecutionResult> AwaitCommandExecutionAsync(JoinableTask<CommandExecutionResult> executionTask, int timeoutMilliseconds, CancellationTokenSource commandCts, CancellationToken commandToken, CancellationToken serverCancellationToken)
    {
        Task<CommandExecutionResult> joinedExecutionTask = executionTask.JoinAsync(serverCancellationToken);
        Task completed = await Task.WhenAny(
            joinedExecutionTask,
            Task.Delay(timeoutMilliseconds, serverCancellationToken)).ConfigureAwait(false);
        if (ReferenceEquals(completed, joinedExecutionTask))
        {
            return await joinedExecutionTask.ConfigureAwait(false);
        }

        if (serverCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(serverCancellationToken);
        }

        commandCts.Cancel();
        _ = joinedExecutionTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        throw new OperationCanceledException(commandToken);
    }

    private async Task<DTE2?> GetDteAsync(CancellationToken cancellationToken)
    {
        if (_dte is not null) return _dte;
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        _dte = await _package.GetServiceAsync(typeof(SDTE)).ConfigureAwait(true) as DTE2;
        return _dte;
    }

    private static bool IsDiagnosticsCommand(string commandName)
    {
        return string.Equals(commandName, "Tools.IdeWaitForReady", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeGetErrorList", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeGetWarnings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeGetMessages", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeBuildAndCaptureErrors", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "Tools.IdeDiagnosticsSnapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "ready", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "errors", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "warnings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "messages", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commandName, "build-errors", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveTimeoutMilliseconds(string commandName, JToken? rawArgs, bool isBatch)
    {
        if (isBatch)
        {
             return PipeServerConstants.DefaultCommandTimeoutMilliseconds;
        }

        int defaultTimeout = IsDiagnosticsCommand(commandName)
             ? PipeServerConstants.DefaultDiagnosticsTimeoutMilliseconds
             : PipeServerConstants.DefaultCommandTimeoutMilliseconds;

        if (rawArgs is null || rawArgs.Type == JTokenType.Null)
        {
            return defaultTimeout;
        }

        try
        {
            CommandArguments args = CommandArgumentParser.Parse(rawArgs);
            int requested = args.GetInt32("timeout-ms", defaultTimeout);
            return requested > 0 ? requested : defaultTimeout;
        }
        catch (CommandErrorException)
        {
            return defaultTimeout;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _commandQueue.Dispose();
        _discovery.Dispose();
    }
}

file static class PipeServerConstants
{
    internal const int PipeStreamBufferSize = 4096;
    internal const int DefaultCommandTimeoutMilliseconds = 120_000;
    internal const int DefaultDiagnosticsTimeoutMilliseconds = 120_000;
}

