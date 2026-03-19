using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal sealed class ToolEntry
{
    public ToolEntry(
        ToolDefinition definition,
        Func<JsonNode?, JsonObject?, BridgeConnection, Task<JsonNode>> handler)
    {
        Definition = definition;
        Handler = handler;
    }

    public ToolEntry(
        string name,
        string description,
        JsonObject inputSchema,
        string category,
        Func<JsonNode?, JsonObject?, BridgeConnection, Task<JsonNode>> handler,
        string? title = null,
        JsonObject? annotations = null,
        JsonObject? outputSchema = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        string? bridgeCommand = null,
        string? summary = null,
        bool? readOnly = null,
        bool? mutating = null,
        bool? destructive = null)
        : this(
            ToolDefinition.CreateLegacy(
                name,
                category,
                description,
                inputSchema,
                title,
                annotations,
                outputSchema,
                aliases,
                tags,
                bridgeCommand,
                summary,
                readOnly,
                mutating,
                destructive),
            handler)
    {
    }

    public ToolDefinition Definition { get; }

    public Func<JsonNode?, JsonObject?, BridgeConnection, Task<JsonNode>> Handler { get; }

    public string Name => Definition.Name;

    public string Category => Definition.Category;
}
