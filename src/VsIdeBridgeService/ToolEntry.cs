using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal sealed class ToolEntry(ToolDefinition definition, Func<JsonNode?, JsonObject?, BridgeConnection, Task<JsonNode>> handler)
{
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
        bool? destructive = null,
        JsonObject? searchHints = null)
        : this(ToolDefinition.CreateLegacy(
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
                destructive,
                searchHints: searchHints),
        handler)
    {
    }

    public ToolDefinition Definition { get; } = definition;

    public Func<JsonNode?, JsonObject?, BridgeConnection, Task<JsonNode>> Handler { get; } = handler;

    public string Name => Definition.Name;

    public string Category => Definition.Category;

    public ToolEntry WithDefinition(ToolDefinition definition)
    {
        return new ToolEntry(definition, Handler);
    }
}
