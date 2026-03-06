using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VsIdeBridge.Services;

internal sealed class BridgeWatchdogService(AsyncPackage package, int probeIntervalMilliseconds = BridgeWatchdogService.DefaultProbeIntervalMilliseconds, int probeTimeoutMilliseconds = BridgeWatchdogService.DefaultProbeTimeoutMilliseconds) : IDisposable
{
    private const int DefaultProbeIntervalMilliseconds = 1_000;
    private const int DefaultProbeTimeoutMilliseconds = 2_000;
    private const int CommandTimeoutDegradedWindowMilliseconds = 15_000;

    private readonly AsyncPackage _package = package;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _probeIntervalMilliseconds = probeIntervalMilliseconds > 0 ? probeIntervalMilliseconds : DefaultProbeIntervalMilliseconds;
    private readonly int _probeTimeoutMilliseconds = probeTimeoutMilliseconds > 0 ? probeTimeoutMilliseconds : DefaultProbeTimeoutMilliseconds;

    private Task? _monitorTask;
    private Task<double>? _probeTask;
    private DateTimeOffset _startedAtUtc;
    private DateTimeOffset _probeStartedAtUtc;
    private DateTimeOffset? _lastProbeStartedAtUtc;
    private DateTimeOffset? _lastProbeCompletedAtUtc;
    private DateTimeOffset? _lastHealthyAtUtc;
    private DateTimeOffset? _degradedSinceUtc;
    private DateTimeOffset? _lastTimeoutCommandAtUtc;
    private bool _probeTimeoutRecorded;
    private bool _started;
    private bool _disposed;
    private bool _isDegraded;
    private string _degradedReason = string.Empty;
    private string _lastProbeError = string.Empty;
    private double _lastProbeDurationMs;
    private double _maxProbeDurationMs;
    private long _totalProbeTimeouts;
    private long _totalProbeFailures;
    private int _consecutiveUnhealthyProbes;

    private string _activeCommand = string.Empty;
    private string _activeRequestId = string.Empty;
    private DateTimeOffset? _activeCommandStartedAtUtc;
    private string _lastCommand = string.Empty;
    private string _lastCommandRequestId = string.Empty;
    private string _lastCommandErrorCode = string.Empty;
    private bool? _lastCommandSuccess;
    private double _lastCommandDurationMs;
    private DateTimeOffset? _lastCommandCompletedAtUtc;
    private long _totalCommands;
    private long _successfulCommands;
    private long _failedCommands;
    private long _timeoutCommands;
    private double _sumCommandDurationMs;
    private double _maxCommandDurationMs;
    private long _successfulProbeCount;

    public void Start()
    {
        lock (_sync)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _startedAtUtc = DateTimeOffset.UtcNow;
        }

        _monitorTask = Task.Factory.StartNew(
            () => MonitorLoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public void RecordCommandStarted(string commandName, string? requestId)
    {
        lock (_sync)
        {
            _activeCommand = commandName ?? string.Empty;
            _activeRequestId = requestId ?? string.Empty;
            _activeCommandStartedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordCommandCompleted(string commandName, string? requestId, bool success, double durationMs, string? errorCode)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _totalCommands++;
            _sumCommandDurationMs += durationMs;
            _maxCommandDurationMs = Math.Max(_maxCommandDurationMs, durationMs);

            if (success)
            {
                _successfulCommands++;
            }
            else
            {
                _failedCommands++;
                if (IsTimeoutError(errorCode))
                {
                    _timeoutCommands++;
                    _lastTimeoutCommandAtUtc = now;
                    if (!_isDegraded)
                    {
                        _degradedSinceUtc = now;
                    }

                    _isDegraded = true;
                    _degradedReason = "command_timeout";
                }
            }

            _lastCommand = commandName ?? string.Empty;
            _lastCommandRequestId = requestId ?? string.Empty;
            _lastCommandSuccess = success;
            _lastCommandErrorCode = errorCode ?? string.Empty;
            _lastCommandDurationMs = durationMs;
            _lastCommandCompletedAtUtc = now;

            _activeCommand = string.Empty;
            _activeRequestId = string.Empty;
            _activeCommandStartedAtUtc = null;
        }
    }

    public JObject GetSnapshot()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var probeStalled = _probeTask is { IsCompleted: false } &&
                _probeTimeoutRecorded &&
                _probeStartedAtUtc != default;
            var probeStalledForMs = probeStalled
                ? Math.Max(0, (now - _probeStartedAtUtc).TotalMilliseconds)
                : 0;
            var timeoutWindowOpen = _lastTimeoutCommandAtUtc is not null &&
                (now - _lastTimeoutCommandAtUtc.Value).TotalMilliseconds <= CommandTimeoutDegradedWindowMilliseconds;
            var degraded = _isDegraded || probeStalled || timeoutWindowOpen;
            var degradedReason = _degradedReason;
            if (probeStalled)
            {
                degradedReason = "watchdog_probe_timeout";
            }
            else if (timeoutWindowOpen && string.IsNullOrWhiteSpace(degradedReason))
            {
                degradedReason = "command_timeout_recent";
            }

            var totalCompletedCommands = _successfulCommands + _failedCommands;
            var averageCommandDurationMs = totalCompletedCommands > 0
                ? _sumCommandDurationMs / totalCompletedCommands
                : 0.0;

            return new JObject
            {
                ["startedAtUtc"] = _startedAtUtc == default ? JValue.CreateNull() : _startedAtUtc.ToString("O"),
                ["probeIntervalMs"] = _probeIntervalMilliseconds,
                ["probeTimeoutMs"] = _probeTimeoutMilliseconds,
                ["isDegraded"] = degraded,
                ["degradedReason"] = degradedReason,
                ["degradedSinceUtc"] = ToNullableTimestamp(_degradedSinceUtc),
                ["lastProbeStartedAtUtc"] = ToNullableTimestamp(_lastProbeStartedAtUtc),
                ["lastProbeCompletedAtUtc"] = ToNullableTimestamp(_lastProbeCompletedAtUtc),
                ["lastHealthyAtUtc"] = ToNullableTimestamp(_lastHealthyAtUtc),
                ["lastProbeDurationMs"] = Math.Round(_lastProbeDurationMs, 1),
                ["maxProbeDurationMs"] = Math.Round(_maxProbeDurationMs, 1),
                ["totalProbeTimeouts"] = _totalProbeTimeouts,
                ["totalProbeFailures"] = _totalProbeFailures,
                ["consecutiveUnhealthyProbes"] = _consecutiveUnhealthyProbes,
                ["probeStalled"] = probeStalled,
                ["probeStalledForMs"] = Math.Round(probeStalledForMs, 1),
                ["lastProbeError"] = _lastProbeError,
                ["activeCommand"] = _activeCommandStartedAtUtc is null
                    ? JValue.CreateNull()
                    : new JObject
                    {
                        ["name"] = _activeCommand,
                        ["requestId"] = _activeRequestId,
                        ["startedAtUtc"] = _activeCommandStartedAtUtc.Value.ToString("O"),
                        ["elapsedMs"] = Math.Round((now - _activeCommandStartedAtUtc.Value).TotalMilliseconds, 1),
                    },
                ["lastCommand"] = _lastCommandCompletedAtUtc is null
                    ? JValue.CreateNull()
                    : new JObject
                    {
                        ["name"] = _lastCommand,
                        ["requestId"] = _lastCommandRequestId,
                        ["success"] = _lastCommandSuccess,
                        ["errorCode"] = _lastCommandErrorCode,
                        ["durationMs"] = Math.Round(_lastCommandDurationMs, 1),
                        ["completedAtUtc"] = _lastCommandCompletedAtUtc.Value.ToString("O"),
                    },
                ["totals"] = new JObject
                {
                    ["commands"] = _totalCommands,
                    ["successfulCommands"] = _successfulCommands,
                    ["failedCommands"] = _failedCommands,
                    ["timeoutCommands"] = _timeoutCommands,
                    ["averageCommandDurationMs"] = Math.Round(averageCommandDurationMs, 1),
                    ["maxCommandDurationMs"] = Math.Round(_maxCommandDurationMs, 1),
                },
            };
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Cancel();
        try
        {
            _monitorTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordProbeFailure(ex.Message);
            }

            try
            {
                await Task.Delay(_probeIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        Task<double>? completedProbe = null;
        var completedProbeWasTimedOut = false;
        var shouldStartProbe = false;
        var shouldRecordTimeout = false;
        var timeoutElapsedMs = 0.0;
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            if (_probeTask is null)
            {
                shouldStartProbe = true;
            }
            else if (_probeTask.IsCompleted)
            {
                completedProbe = _probeTask;
                completedProbeWasTimedOut = _probeTimeoutRecorded;
                _probeTask = null;
                _probeTimeoutRecorded = false;
            }
            else
            {
                timeoutElapsedMs = Math.Max(0, (now - _probeStartedAtUtc).TotalMilliseconds);
                if (!_probeTimeoutRecorded && timeoutElapsedMs >= _probeTimeoutMilliseconds)
                {
                    _probeTimeoutRecorded = true;
                    shouldRecordTimeout = true;
                }
            }
        }

        if (shouldRecordTimeout)
        {
            RecordProbeTimeout(timeoutElapsedMs);
        }

        if (completedProbe is not null)
        {
            try
            {
                var durationMs = await completedProbe.ConfigureAwait(false);
                RecordProbeSuccess(durationMs, completedProbeWasTimedOut);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                RecordProbeFailure(ex.Message);
            }

            shouldStartProbe = true;
        }

        if (shouldStartProbe)
        {
            StartProbe(cancellationToken);
        }
    }

    private void StartProbe(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var task = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }).Task;

        lock (_sync)
        {
            if (_probeTask is not null)
            {
                return;
            }

            _probeTask = task;
            _probeStartedAtUtc = startedAtUtc;
            _lastProbeStartedAtUtc = startedAtUtc;
            _probeTimeoutRecorded = false;
        }
    }

    private void RecordProbeTimeout(double elapsedMs)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _totalProbeTimeouts++;
            _consecutiveUnhealthyProbes++;
            _lastProbeError = $"UI responsiveness probe exceeded {_probeTimeoutMilliseconds} ms (elapsed={Math.Round(elapsedMs, 1)} ms).";
            if (!_isDegraded)
            {
                _degradedSinceUtc = now;
            }

            _isDegraded = true;
            _degradedReason = "watchdog_probe_timeout";
        }
    }

    private void RecordProbeFailure(string message)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _totalProbeFailures++;
            _consecutiveUnhealthyProbes++;
            _lastProbeCompletedAtUtc = now;
            _lastProbeError = message ?? string.Empty;
            if (!_isDegraded)
            {
                _degradedSinceUtc = now;
            }

            _isDegraded = true;
            _degradedReason = "watchdog_probe_failure";
        }
    }

    private void RecordProbeSuccess(double durationMs, bool recoveredFromTimeout)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _lastProbeCompletedAtUtc = now;
            _lastHealthyAtUtc = now;
            _lastProbeDurationMs = durationMs;
            _successfulProbeCount++;
            if (_successfulProbeCount % 120 == 0)
                _maxProbeDurationMs = durationMs;
            _maxProbeDurationMs = Math.Max(_maxProbeDurationMs, durationMs);
            _lastProbeError = string.Empty;
            _consecutiveUnhealthyProbes = 0;

            var timeoutWindowOpen = _lastTimeoutCommandAtUtc is not null &&
                (now - _lastTimeoutCommandAtUtc.Value).TotalMilliseconds <= CommandTimeoutDegradedWindowMilliseconds;
            if (!timeoutWindowOpen)
            {
                _isDegraded = false;
                _degradedReason = string.Empty;
                _degradedSinceUtc = null;
            }
            else if (recoveredFromTimeout && string.Equals(_degradedReason, "watchdog_probe_timeout", StringComparison.Ordinal))
            {
                _degradedReason = "command_timeout_recent";
            }
        }
    }

    private static bool IsTimeoutError(string? errorCode)
    {
        return string.Equals(errorCode, "timeout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(errorCode, "ide_blocked", StringComparison.OrdinalIgnoreCase);
    }

    private static JToken ToNullableTimestamp(DateTimeOffset? value)
    {
        return value is null ? JValue.CreateNull() : value.Value.ToString("O");
    }
}
