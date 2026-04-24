using Newtonsoft.Json.Linq;
using System;

namespace VsIdeBridge.Services;

internal sealed class BuildBackgroundOperationTracker
{
    private readonly object _gate = new();
    private BackgroundBuildOperationState? _state;

    public void MarkQueued(string operation, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            _state = new BackgroundBuildOperationState
            {
                Operation = operation,
                Status = "queued",
                StartedAtUtc = startedAt.ToString("O"),
            };
        }
    }

    public void MarkRunning(string operation, string startedAtUtc)
    {
        lock (_gate)
        {
            if (!MatchesLocked(operation, startedAtUtc))
            {
                return;
            }

            _state!.Status = "running";
        }
    }

    public void Complete(string operation, string startedAtUtc, JObject result)
    {
        lock (_gate)
        {
            if (!MatchesLocked(operation, startedAtUtc))
            {
                return;
            }

            _state!.Status = "completed";
            _state.FinishedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            _state.ErrorMessage = null;
            _state.Result = (JObject)result.DeepClone();
        }
    }

    public void Fail(string operation, string startedAtUtc, Exception ex)
    {
        lock (_gate)
        {
            if (!MatchesLocked(operation, startedAtUtc))
            {
                return;
            }

            _state!.Status = "failed";
            _state.FinishedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            _state.ErrorMessage = ex.Message;
            _state.Result = null;
        }
    }

    public JObject? GetSnapshot()
    {
        lock (_gate)
        {
            return _state?.ToJson();
        }
    }

    private bool MatchesLocked(string operation, string startedAtUtc)
        => _state is not null
            && string.Equals(_state.Operation, operation, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_state.StartedAtUtc, startedAtUtc, StringComparison.Ordinal);

    private sealed class BackgroundBuildOperationState
    {
        public string Operation { get; set; } = string.Empty;

        public string Status { get; set; } = "queued";

        public string StartedAtUtc { get; set; } = string.Empty;

        public string? FinishedAtUtc { get; set; }

        public string? ErrorMessage { get; set; }

        public JObject? Result { get; set; }

        public JObject ToJson()
        {
            JObject json = new()
            {
                ["operation"] = Operation,
                ["status"] = Status,
                ["startedAtUtc"] = StartedAtUtc,
            };

            if (!string.IsNullOrWhiteSpace(FinishedAtUtc))
            {
                json["finishedAtUtc"] = FinishedAtUtc;
            }

            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                json["errorMessage"] = ErrorMessage;
            }

            if (Result is not null)
            {
                json["result"] = Result.DeepClone();
            }

            return json;
        }
    }
}
