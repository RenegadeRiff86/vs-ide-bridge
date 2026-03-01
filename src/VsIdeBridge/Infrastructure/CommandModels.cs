using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Infrastructure;

internal sealed class CommandExecutionResult
{
    public CommandExecutionResult(string summary, JToken? data = null, JArray? warnings = null)
    {
        Summary = summary;
        Data = data ?? new JObject();
        Warnings = warnings ?? new JArray();
    }

    public string Summary { get; }

    public JToken Data { get; }

    public JArray Warnings { get; }
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

    public JArray Warnings { get; set; } = new JArray();

    public object? Error { get; set; }

    public JToken Data { get; set; } = new JObject();
}
