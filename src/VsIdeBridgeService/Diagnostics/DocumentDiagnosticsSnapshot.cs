using System.Text.Json.Nodes;

namespace VsIdeBridgeService.Diagnostics;

internal sealed class DocumentDiagnosticsSnapshot
{
    public string Status { get; init; } = "idle";

    public string? Reason { get; init; }

    public string? LastError { get; init; }

    public DocumentDiagnosticsTimingSnapshot Timing { get; init; } = new();

    public DocumentDiagnosticsResultSnapshot Results { get; init; } = new();

    public JsonObject ToJson()
    {
        JsonObject json = new()
        {
            ["status"] = Status,
        };

        if (!string.IsNullOrWhiteSpace(Reason))
        {
            json["reason"] = Reason;
        }

        if (!string.IsNullOrWhiteSpace(LastError))
        {
            json["lastError"] = LastError;
        }

        if (!string.IsNullOrWhiteSpace(Timing.LastQueuedUtc))
        {
            json["lastQueuedUtc"] = Timing.LastQueuedUtc;
        }

        if (!string.IsNullOrWhiteSpace(Timing.LastStartedUtc))
        {
            json["lastStartedUtc"] = Timing.LastStartedUtc;
        }

        if (!string.IsNullOrWhiteSpace(Timing.LastCompletedUtc))
        {
            json["lastCompletedUtc"] = Timing.LastCompletedUtc;
        }

        if (Results.Errors is not null)
        {
            json["errors"] = Results.Errors.DeepClone();
        }

        if (Results.Warnings is not null)
        {
            json["warnings"] = Results.Warnings.DeepClone();
        }

        if (Results.Messages is not null)
        {
            json["messages"] = Results.Messages.DeepClone();
        }

        return json;
    }
}

internal sealed class DocumentDiagnosticsTimingSnapshot
{
    public string? LastQueuedUtc { get; init; }

    public string? LastStartedUtc { get; init; }

    public string? LastCompletedUtc { get; init; }
}

internal sealed class DocumentDiagnosticsResultSnapshot
{
    public JsonObject? Errors { get; init; }

    public JsonObject? Warnings { get; init; }

    public JsonObject? Messages { get; init; }
}
