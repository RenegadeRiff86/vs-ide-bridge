using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal sealed class McpRequestException : Exception
{
    public McpRequestException(JsonNode? id, int code, string message) : base(message)
    {
        Id = id;
        Code = code;
    }

    public JsonNode? Id { get; }
    public int Code { get; }
}
