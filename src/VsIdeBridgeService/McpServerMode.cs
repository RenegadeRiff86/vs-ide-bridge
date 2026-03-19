using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// Runs the MCP server JSON-RPC stdio loop.
// Launched when the service binary is invoked with the "mcp-server" argument.
internal static class McpServerMode
{
    private static readonly ToolExecutionRegistry Registry = ToolCatalog.CreateRegistry();

    public static async Task RunAsync(string[] args)
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        BridgeConnection bridge = new(args);

        McpServerLog.Write("stdio loop started");

        while (true)
        {
            McpProtocol.IncomingMessage? incoming;
            try
            {
                incoming = await McpProtocol.ReadAsync(input).ConfigureAwait(false);
            }
            catch (McpRequestException ex)
            {
                McpServerLog.Write($"read error code={ex.Code} message={ex.Message}");
                await McpProtocol.WriteAsync(output, McpProtocol.ErrorResponse(ex.Id, ex.Code, ex.Message),
                    McpProtocol.WireFormat.HeaderFramed).ConfigureAwait(false);
                continue;
            }

            if (incoming is null)
            {
                McpServerLog.Write("stdin closed; exiting stdio loop");
                break;
            }

            McpServerLog.WriteRequest(incoming.Request, incoming.Format);

            JsonObject? response = await HandleRequestAsync(incoming.Request, bridge)
                .ConfigureAwait(false);

            if (response is not null)
            {
                McpServerLog.WriteResponse(response);
                await McpProtocol.WriteAsync(output, response, incoming.Format).ConfigureAwait(false);
            }
        }
    }

    private static async Task<JsonObject?> HandleRequestAsync(JsonObject request, BridgeConnection bridge)
    {
        JsonNode? id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        JsonObject? @params = request["params"] as JsonObject;

        if (method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        try
        {
            JsonNode result = method switch
            {
                "initialize"  => InitializeResult(@params),
                "tools/list"  => new JsonObject { ["tools"] = Registry.BuildToolsList() },
                "tools/call"  => await DispatchToolAsync(id, @params, bridge).ConfigureAwait(false),
                "ping"        => new JsonObject(),
                _             => throw new McpRequestException(
                    id, McpErrorCodes.MethodNotFound, $"Unsupported method: {method}"),
            };

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpRequestException ex)
        {
            McpServerLog.Write($"request error method={method} code={ex.Code} message={ex.Message}");
            return McpProtocol.ErrorResponse(ex.Id ?? id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            McpServerLog.Write($"request fatal method={method} message={ex}");
            return McpProtocol.ErrorResponse(id, McpErrorCodes.MethodNotFound, ex.Message);
        }
    }

    private static JsonObject InitializeResult(JsonObject? @params)
    {
        string clientVersion = @params?["protocolVersion"]?.GetValue<string>() ?? string.Empty;
        string negotiated = McpProtocol.SelectProtocolVersion(clientVersion);

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"]    = "vs_ide_bridge",
                ["version"] = "0.1.0",
            },
        };
    }

    private static async Task<JsonNode> DispatchToolAsync(
        JsonNode? id, JsonObject? @params, BridgeConnection bridge)
    {
        string toolName = @params?["name"]?.GetValue<string>() ?? string.Empty;
        JsonObject? args = @params?["arguments"] as JsonObject;

        if (string.IsNullOrWhiteSpace(toolName))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing 'name' in tools/call params.");

        McpServerLog.Write($"dispatch tool={toolName}");

        return await Registry.DispatchAsync(id, toolName, args, bridge).ConfigureAwait(false);
    }
}
