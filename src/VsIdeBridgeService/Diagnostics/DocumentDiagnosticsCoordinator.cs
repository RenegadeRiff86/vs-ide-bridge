using System.Text.Json.Nodes;

using static VsIdeBridgeService.ArgBuilder;

namespace VsIdeBridgeService.Diagnostics;

internal sealed class DocumentDiagnosticsCoordinator(BridgeConnection bridge)
{
    private static readonly TimeSpan RefreshDebounceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshCompletionTimeout = TimeSpan.FromSeconds(10);
    private const string DefaultDiagnosticCacheMax = "50";

    private readonly BridgeConnection _bridge = bridge;
    private readonly object _gate = new();
    private readonly CachedDiagnosticsState _cached = new();

    private Task? _refreshTask;
    private bool _refreshRequested;
    private readonly RefreshTimingState _timing = new();

    public JsonObject QueueRefreshAndGetSnapshot(string reason, bool clearCached = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (clearCached)
            {
                _cached.Errors = null;
                _cached.Warnings = null;
                _cached.Messages = null;
                _cached.LastError = null;
                _cached.Status = "idle";
            }

            if (_refreshTask is not null && !_refreshTask.IsCompleted)
            {
                if (clearCached)
                {
                    _refreshRequested = true;
                    _cached.Reason = reason;
                    _timing.LastQueuedUtc = now;
                }

                return CreateSnapshotLocked().ToJson();
            }

            if (!clearCached && _timing.LastCompletedUtc is not null && now - _timing.LastCompletedUtc < RefreshDebounceInterval)
            {
                return CreateSnapshotLocked().ToJson();
            }

            _refreshRequested = true;
            _cached.Reason = reason;
            _timing.LastQueuedUtc = now;

            if (_refreshTask is null || _refreshTask.IsCompleted)
            {
                _cached.Status = "queued";
                _refreshTask = Task.Run(RefreshLoopAsync);
            }

            return CreateSnapshotLocked().ToJson();
        }
    }

    public async Task<JsonObject> QueueRefreshAndWaitForSnapshotAsync(string reason, bool clearCached = false)
    {
        _ = QueueRefreshAndGetSnapshot(reason, clearCached);

        Task? refreshTask;
        lock (_gate)
        {
            refreshTask = _refreshTask;
        }

        if (refreshTask is not null)
        {
            _ = await Task.WhenAny(refreshTask, Task.Delay(RefreshCompletionTimeout)).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return CreateSnapshotLocked().ToJson();
        }
    }

    public void Invalidate(string reason)
    {
        lock (_gate)
        {
            _cached.Errors = null;
            _cached.Warnings = null;
            _cached.Messages = null;
            _cached.LastError = null;
            _cached.Status = "idle";
            _cached.Reason = reason;
            _timing.LastQueuedUtc = null;
            _timing.LastStartedUtc = null;
            _timing.LastCompletedUtc = null;
        }
    }

    public bool TryGetCachedErrors(JsonObject? args, out JsonObject response)
    {
        lock (_gate)
        {
            if (!CanServeCachedDiagnostics(args) || _cached.Errors is null || !HasUsableCachedDiagnosticsLocked())
            {
                response = [];
                return false;
            }

            response = CreateCachedResponseLocked(_cached.Errors, "errors");
            return true;
        }
    }

    public bool TryGetCachedWarnings(JsonObject? args, out JsonObject response)
    {
        lock (_gate)
        {
            if (!CanServeCachedDiagnostics(args) || _cached.Warnings is null || !HasUsableCachedDiagnosticsLocked())
            {
                response = [];
                return false;
            }

            response = CreateCachedResponseLocked(_cached.Warnings, "warnings");
            return true;
        }
    }

    public bool TryGetCachedMessages(JsonObject? args, out JsonObject response)
    {
        lock (_gate)
        {
            if (!CanServeCachedDiagnostics(args) || _cached.Messages is null || !HasUsableCachedDiagnosticsLocked())
            {
                response = [];
                return false;
            }

            response = CreateCachedResponseLocked(_cached.Messages, "messages");
            return true;
        }
    }

    private async Task RefreshLoopAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_refreshRequested)
                {
                    _refreshTask = null;
                    return;
                }

                _refreshRequested = false;
                _cached.Status = "running";
                _timing.LastStartedUtc = DateTimeOffset.UtcNow;
                _cached.LastError = null;
            }

            try
            {
                JsonObject errors = await _bridge.SendAsync(null, "errors", BuildCachedErrorArgs())
                    .ConfigureAwait(false);
                JsonObject warnings = await _bridge.SendAsync(null, "warnings", BuildCachedListArgs())
                    .ConfigureAwait(false);
                JsonObject messages = await _bridge.SendAsync(null, "messages", BuildCachedListArgs())
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _cached.Errors = errors;
                    _cached.Warnings = warnings;
                    _cached.Messages = messages;
                    _cached.Status = "completed";
                    _timing.LastCompletedUtc = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not null) // background diagnostics loop boundary
            {
                lock (_gate)
                {
                    _cached.Status = "failed";
                    _cached.LastError = ex.Message;
                    _timing.LastCompletedUtc = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    private static bool CanServeCachedDiagnostics(JsonObject? args)
    {
        if (args is null)
        {
            return true;
        }

        if (args["refresh"]?.GetValue<bool>() == true)
        {
            return false;
        }

        // quick and wait_for_intellisense are timing hints, not content filters.
        // Only bypass cache when content-filtering params are present.
        return args["severity"] is null
            && args["code"] is null
            && args["project"] is null
            && args["path"] is null
            && args["text"] is null
            && args["group_by"] is null;
    }

    private bool HasUsableCachedDiagnosticsLocked()
    {
        return string.Equals(_cached.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_cached.Reason, "startup", StringComparison.OrdinalIgnoreCase);
    }

    private DocumentDiagnosticsSnapshot CreateSnapshotLocked()
    {
        return new DocumentDiagnosticsSnapshot
        {
            Status = _cached.Status,
            Reason = _cached.Reason,
            LastError = _cached.LastError,
            Timing = new DocumentDiagnosticsTimingSnapshot
            {
                LastQueuedUtc = FormatUtc(_timing.LastQueuedUtc),
                LastStartedUtc = FormatUtc(_timing.LastStartedUtc),
                LastCompletedUtc = FormatUtc(_timing.LastCompletedUtc),
            },
            Results = new DocumentDiagnosticsResultSnapshot
            {
                Errors = _cached.Errors,
                Warnings = _cached.Warnings,
                Messages = _cached.Messages,
            },
        };
    }

    private JsonObject CreateCachedResponseLocked(JsonObject response, string kind)
    {
        JsonObject clone = response.DeepClone().AsObject();
        clone["Cache"] = new JsonObject
        {
            ["source"] = "service-memory",
            ["kind"] = kind,
            ["snapshot"] = CreateSnapshotLocked().ToJson(),
        };
        return clone;
    }

    private static JsonObject BuildCachedErrorArgs()
        => new()
        {
            ["quick"] = true,
            ["wait_for_intellisense"] = false,
            ["severity"] = "Error",
        };

    private static JsonObject BuildCachedListArgs()
        => new()
        {
            ["quick"] = true,
            ["wait_for_intellisense"] = false,
            ["max"] = DefaultDiagnosticCacheMax,
        };

    private static string? FormatUtc(DateTimeOffset? value)
    {
        return value?.UtcDateTime.ToString("O");
    }

    private sealed class RefreshTimingState
    {
        public DateTimeOffset? LastQueuedUtc { get; set; }
        public DateTimeOffset? LastStartedUtc { get; set; }
        public DateTimeOffset? LastCompletedUtc { get; set; }
    }

    private sealed class CachedDiagnosticsState
    {
        public string Status { get; set; } = "idle";
        public string? Reason { get; set; }
        public string? LastError { get; set; }
        public JsonObject? Errors { get; set; }
        public JsonObject? Warnings { get; set; }
        public JsonObject? Messages { get; set; }
    }
}
