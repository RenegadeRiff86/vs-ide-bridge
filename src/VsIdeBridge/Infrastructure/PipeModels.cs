using Newtonsoft.Json;
using System.Collections.Generic;

namespace VsIdeBridge.Infrastructure;

internal sealed class PipeBatchRequest
{
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("command")] public string Command { get; set; } = "";
    [JsonProperty("args")] public string? Args { get; set; }
}

internal sealed class PipeRequest
{
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("command")] public string Command { get; set; } = "";
    [JsonProperty("args")] public string? Args { get; set; }
    [JsonProperty("batch")] public List<PipeBatchRequest>? Batch { get; set; }
    [JsonProperty("stopOnError")] public bool? StopOnError { get; set; }
}
