using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Text;
using System.IO;

namespace VsIdeBridgeService;

// Runs the MCP server JSON-RPC stdio loop.
// Launched when the service binary is invoked with the "mcp-server" argument.
internal static class McpServerMode
{
    private static readonly ToolExecutionRegistry Registry = ToolCatalog.Registry;
    private const int BadRequestStatusCode = 400;

    public static async Task RunAsync(string[] args)
    {
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        BridgeConnection bridge = new(args);
        using StdioHostLease? hostLease = StdioHostLease.TryCreate();
        ServiceControlClient? controlClient = null;

        controlClient = await TryConnectControlPipeAsync().ConfigureAwait(false);

        McpServerLog.Write("stdio loop started");

        try
        {
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

                if (controlClient is not null)
                {
                    try
                    {
                        await controlClient.NotifyRequestAsync().ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        McpServerLog.Write($"control pipe request notify failed: {ex.Message}");
                        await controlClient.DisposeAsync().ConfigureAwait(false);
                        controlClient = null;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        McpServerLog.Write($"control pipe request notify failed: {ex.Message}");
                        await controlClient.DisposeAsync().ConfigureAwait(false);
                        controlClient = null;
                    }
                    catch (InvalidOperationException ex)
                    {
                        McpServerLog.Write($"control pipe request notify failed: {ex.Message}");
                        await controlClient.DisposeAsync().ConfigureAwait(false);
                        controlClient = null;
                    }
                }

                McpServerLog.WriteRequest(incoming.Request, incoming.Format);

                JsonObject? response = await HandleRequestAsync(incoming.Request, bridge, controlClient)
                    .ConfigureAwait(false);

                if (response is not null)
                {
                    McpServerLog.WriteResponse(response);
                    await McpProtocol.WriteAsync(output, response, incoming.Format).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (controlClient is not null)
            {
                await controlClient.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<JsonObject?> HandleRequestAsync(
        JsonObject request,
        BridgeConnection bridge,
        ServiceControlClient? controlClient)
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
                "initialize"                => InitializeResult(@params),
                "tools/list"                => new JsonObject { ["tools"] = Registry.BuildToolsList() },
                "tools/call"                => await DispatchToolAsync(id, @params, bridge, controlClient).ConfigureAwait(false),
                "resources/list"            => EmptyResourcesList(),
                "resources/templates/list"  => EmptyResourceTemplatesList(),
                "ping"                      => new JsonObject(),
                _                           => throw new McpRequestException(
                    id, McpErrorCodes.MethodNotFound, $"Unsupported method: {method}"),
            };

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpRequestException ex)
        {
            McpServerLog.Write($"request error method={method} code={ex.Code} message={ex.Message}");
            return McpProtocol.ErrorResponse(ex.Id ?? id, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not null) // top-level MCP request boundary
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
                ["resources"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"]    = "vs_ide_bridge",
                ["version"] = "0.1.0",
            },
        };
    }

    private static JsonObject EmptyResourcesList()
    {
        return new JsonObject
        {
            ["resources"] = new JsonArray(),
        };
    }

    private static JsonObject EmptyResourceTemplatesList()
    {
        return new JsonObject
        {
            ["resourceTemplates"] = new JsonArray(),
        };
    }

    private static async Task<JsonNode> DispatchToolAsync(
        JsonNode? id, JsonObject? @params, BridgeConnection bridge, ServiceControlClient? controlClient)
    {
        string toolName = @params?["name"]?.GetValue<string>() ?? string.Empty;
        JsonObject? args = @params?["arguments"] as JsonObject;

        if (string.IsNullOrWhiteSpace(toolName))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing 'name' in tools/call params.");

        McpServerLog.Write($"dispatch tool={toolName}");

        if (controlClient is null)
        {
            return await Registry.DispatchAsync(id, toolName, args, bridge).ConfigureAwait(false);
        }

        await controlClient.NotifyCommandStartAsync().ConfigureAwait(false);
        try
        {
            return await Registry.DispatchAsync(id, toolName, args, bridge).ConfigureAwait(false);
        }
        finally
        {
            await controlClient.NotifyCommandEndAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ServiceControlClient?> TryConnectControlPipeAsync()
    {
        try
        {
            return await ServiceControlClient.ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public static async Task RunHttpAsync(string[] args, CancellationToken cancellationToken = default)
    {
        int port = 8080;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(args[i + 1], out int parsedPort) && parsedPort > 0 && parsedPort < 65536)
                    port = parsedPort;
                break;
            }
        }

        string prefix = $"http://localhost:{port}/";
        BridgeConnection bridge = new(args);

        McpServerLog.Write($"MCP HTTP server starting on {prefix}");

        using HttpListener listener = new();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            McpServerLog.Write("HTTP listener started successfully");
        }
        catch (HttpListenerException ex)
        {
            McpServerLog.Write($"Failed to start listener: {ex}");
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.Write($"Failed to start listener: {ex}");
            throw;
        }

        // When the cancellationToken fires, stop the listener so GetContextAsync throws
        // HttpListenerException with IsListening == false and the accept loop exits cleanly.
        using CancellationTokenRegistration stopReg = cancellationToken.Register(
            static state => ((HttpListener)state!).Stop(), listener);

        while (true)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
                await HandleHttpRequestAsync(context, bridge).ConfigureAwait(false);
            }
            catch (HttpListenerException) when (listener.IsListening == false)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                McpServerLog.Write($"HTTP accept error: {ex}");
            }
            catch (IOException ex)
            {
                McpServerLog.Write($"HTTP accept error: {ex}");
            }
            catch (ObjectDisposedException ex)
            {
                McpServerLog.Write($"HTTP accept error: {ex}");
            }
        }
    }

    private static async Task HandleHttpRequestAsync(HttpListenerContext context, BridgeConnection bridge)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            McpServerLog.Write($"HTTP {method} {path}");

            if (method == "GET")
            {
                await WriteHealthResponseAsync(context).ConfigureAwait(false);
                return;
            }

            if (method != "POST")
            {
                context.Response.StatusCode = 405;
                context.Response.ContentType = "text/plain";
                await context.Response.OutputStream.WriteAsync("Method not allowed"u8.ToArray()).ConfigureAwait(false);
                return;
            }

            JsonObject? request = await ReadHttpRequestAsync(context).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            McpServerLog.WriteRequest(request, McpProtocol.WireFormat.RawJson);

            JsonObject? response = await HandleRequestAsync(request, bridge, controlClient: null).ConfigureAwait(false);

            if (response is not null)
            {
                McpServerLog.WriteResponse(response);
                string jsonResponse = response.ToJsonString();
                byte[] bytes = Encoding.UTF8.GetBytes(jsonResponse);
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 204; // no content for notifications
            }
        }
        catch (McpRequestException ex)
        {
            McpServerLog.Write($"HTTP MCP error code={ex.Code}: {ex.Message}");
            await WriteErrorResponse(context, ex.Message, ex.Code);
        }
        catch (Exception ex) when (ex is not null) // top-level HTTP request boundary
        {
            McpServerLog.Write($"HTTP request fatal: {ex}");
            await WriteErrorResponse(context, ex.Message);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (ObjectDisposedException ex) { McpServerLog.WriteException("HTTP response close skipped because the response was already disposed", ex); }
            catch (HttpListenerException ex) { McpServerLog.WriteException("HTTP response close skipped because the listener was already stopping", ex); }
        }
    }

    private static async Task WriteHealthResponseAsync(HttpListenerContext context)
    {
        JsonObject info = new()
        {
            ["name"] = "vs-ide-bridge",
            ["version"] = "0.1.0",
            ["protocolVersions"] = new JsonArray { "2025-03-26", "2024-11-05" },
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["status"] = "ok"
        };

        await WriteJsonResponseAsync(context, info).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReadHttpRequestAsync(HttpListenerContext context)
    {
        string body = await ReadRequestBodyAsync(context.Request).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            context.Response.StatusCode = BadRequestStatusCode;
            return null;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(body);
            return node as JsonObject ?? throw new InvalidOperationException("Not an object");
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = BadRequestStatusCode;
            await WriteErrorResponse(context, "Invalid JSON").ConfigureAwait(false);
            return null;
        }
        catch (FormatException)
        {
            context.Response.StatusCode = BadRequestStatusCode;
            await WriteErrorResponse(context, "Invalid JSON").ConfigureAwait(false);
            return null;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = BadRequestStatusCode;
            await WriteErrorResponse(context, "Invalid JSON").ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task WriteJsonResponseAsync(HttpListenerContext context, JsonObject payload)
    {
        string json = payload.ToJsonString();
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteErrorResponse(HttpListenerContext context, string message, int code = -32603)
    {
        JsonObject errorResponse = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        context.Response.StatusCode = 200;
        await WriteJsonResponseAsync(context, errorResponse).ConfigureAwait(false);
    }
}
