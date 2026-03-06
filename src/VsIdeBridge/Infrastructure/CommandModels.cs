using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Infrastructure;

internal sealed class CommandExecutionResult(string summary, JToken? data = null, JArray? warnings = null)
{
    public string Summary { get; } = summary;

    public JToken Data { get; } = data ?? new JObject();

    public JArray Warnings { get; } = warnings ?? [];
}

internal sealed class CommandEnvelope
{
    public int SchemaVersion { get; set; }

    public string Command { get; set; } = string.Empty;

    public string? RequestId { get; set; }

    public bool Success { get; set; }

    public string StartedAtUtc { get; set; } = string.Empty;

    public string FinishedAtUtc { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public JArray Warnings { get; set; } = [];

    public object? Error { get; set; }

    public JToken Data { get; set; } = new JObject();
}
