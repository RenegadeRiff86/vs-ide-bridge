using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class CliApp
{
    private static readonly JsonSerializerOptions McpJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<int> RunMcpServerAsync(CliOptions options)
    {
        var selector = BridgeInstanceSelector.FromOptions(options);
        var discovery = await PipeDiscovery.SelectAsync(selector, options.GetFlag("verbose")).ConfigureAwait(false);
        await McpServer.RunAsync(discovery, options).ConfigureAwait(false);
        return 0;
    }

    private static class McpServer
    {
        public static async Task RunAsync(PipeDiscovery discovery, CliOptions options)
        {
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            while (true)
            {
                JsonObject? response;

                try
                {
                    var request = await ReadMessageAsync(input).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    response = await HandleRequestAsync(request, discovery, options).ConfigureAwait(false);
                }
                catch (McpRequestException ex)
                {
                    response = CreateErrorResponse(ex.Id, ex.Code, ex.Message);
                }
                catch (JsonException ex)
                {
                    response = CreateErrorResponse(null, -32700, $"Parse error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    response = CreateErrorResponse(null, -32603, $"Internal error: {ex.Message}");
                }

                if (response is not null)
                {
                    await WriteMessageAsync(output, response).ConfigureAwait(false);
                }
            }
        }

        private static async Task<JsonObject?> HandleRequestAsync(JsonObject request, PipeDiscovery discovery, CliOptions options)
        {
            var id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? string.Empty;
            var @params = request["params"] as JsonObject;

            JsonNode result = method switch
            {
                "initialize" => InitializeResult(),
                "tools/list" => new JsonObject { ["tools"] = ListTools() },
                "tools/call" => await CallToolAsync(id, @params, discovery, options).ConfigureAwait(false),
                "resources/list" => new JsonObject { ["resources"] = ListResources() },
                "resources/read" => await ReadResourceAsync(id, @params, discovery, options).ConfigureAwait(false),
                "prompts/list" => new JsonObject { ["prompts"] = ListPrompts() },
                "prompts/get" => GetPrompt(id, @params),
                "notifications/initialized" => null!,
                _ => throw new McpRequestException(id, -32601, $"Unsupported MCP method: {method}"),
            };

            if (method == "notifications/initialized")
            {
                return null;
            }

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JsonObject InitializeResult() => new()
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
                ["resources"] = new JsonObject(),
                ["prompts"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "vs-ide-bridge-mcp",
                ["version"] = "0.1.0",
            },
        };

        private static JsonArray ListTools() => new()
        {
            Tool("state", "Capture current Visual Studio bridge state.", EmptySchema()),
            Tool("errors", "Get current errors.", EmptySchema()),
            Tool("warnings", "Get current warnings.", EmptySchema()),
            Tool("list_tabs", "List open editor tabs.", EmptySchema()),
            Tool(
                "open_file",
                "Open a file path and optional line/column.",
                ObjectSchema(
                    ("file", StringSchema("Absolute or solution-relative file path."), true),
                    ("line", IntegerSchema("Optional 1-based line number."), false),
                    ("column", IntegerSchema("Optional 1-based column number."), false))),
            Tool(
                "search_symbols",
                "Search solution symbols by query.",
                ObjectSchema(
                    ("query", StringSchema("Symbol search text."), true),
                    ("kind", StringSchema("Optional symbol kind filter."), false))),
            Tool(
                "quick_info",
                "Get quick info at file/line/column.",
                ObjectSchema(
                    ("file", StringSchema("Absolute or solution-relative file path."), true),
                    ("line", IntegerSchema("1-based line number."), true),
                    ("column", IntegerSchema("1-based column number."), true))),
            Tool(
                "apply_diff",
                "Apply unified diff through Visual Studio editor buffer.",
                ObjectSchema(
                    ("patch", StringSchema("Unified diff text."), true))),
        };

        private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
        };

        private static async Task<JsonNode> CallToolAsync(JsonNode? id, JsonObject? p, PipeDiscovery discovery, CliOptions options)
        {
            var toolName = p?["name"]?.GetValue<string>() ?? throw new McpRequestException(id, -32602, "tools/call missing name.");
            var args = p?["arguments"] as JsonObject;
            var (command, commandArgs) = toolName switch
            {
                "state" => ("state", string.Empty),
                "errors" => ("errors", "--quick --wait-for-intellisense false"),
                "warnings" => ("warnings", "--quick --wait-for-intellisense false"),
                "list_tabs" => ("list-tabs", string.Empty),
                "open_file" => ("open-document", BuildArgs(("file", args?["file"]?.GetValue<string>()), ("line", args?["line"]?.ToString()), ("column", args?["column"]?.ToString()))),
                "search_symbols" => ("search-symbols", BuildArgs(("query", args?["query"]?.GetValue<string>()), ("kind", args?["kind"]?.GetValue<string>()))),
                "quick_info" => ("quick-info", BuildArgs(("file", args?["file"]?.GetValue<string>()), ("line", args?["line"]?.ToString()), ("column", args?["column"]?.ToString()))),
                "apply_diff" => ("apply-diff", BuildArgs(("patch-text-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(args?["patch"]?.GetValue<string>() ?? string.Empty))), ("open-changed-files", "true"))),
                _ => throw new McpRequestException(id, -32602, $"Unknown MCP tool: {toolName}"),
            };

            var response = await SendBridgeAsync(discovery, options, command, commandArgs).ConfigureAwait(false);
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = response.ToJsonString(JsonOptions),
                    },
                },
                ["isError"] = !ResponseFormatter.IsSuccess(response),
                ["structuredContent"] = response.DeepClone(),
            };
        }

        private static JsonArray ListResources() => new()
        {
            Resource("bridge://current-solution", "Current solution"),
            Resource("bridge://active-document", "Active document"),
            Resource("bridge://open-tabs", "Open tabs"),
            Resource("bridge://error-list-snapshot", "Error list snapshot"),
        };

        private static JsonObject Resource(string uri, string name) => new()
        {
            ["uri"] = uri,
            ["name"] = name,
            ["mimeType"] = "application/json",
        };

        private static async Task<JsonNode> ReadResourceAsync(JsonNode? id, JsonObject? p, PipeDiscovery discovery, CliOptions options)
        {
            var uri = p?["uri"]?.GetValue<string>() ?? throw new McpRequestException(id, -32602, "resources/read missing uri.");
            JsonObject data = uri switch
            {
                "bridge://current-solution" => await SendBridgeAsync(discovery, options, "state", string.Empty).ConfigureAwait(false),
                "bridge://active-document" => await SendBridgeAsync(discovery, options, "state", string.Empty).ConfigureAwait(false),
                "bridge://open-tabs" => await SendBridgeAsync(discovery, options, "list-tabs", string.Empty).ConfigureAwait(false),
                "bridge://error-list-snapshot" => await SendBridgeAsync(discovery, options, "errors", "--quick --wait-for-intellisense false").ConfigureAwait(false),
                _ => throw new McpRequestException(id, -32602, $"Unknown resource uri: {uri}"),
            };

            return new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = uri,
                        ["mimeType"] = "application/json",
                        ["text"] = data.ToJsonString(JsonOptions),
                    },
                },
            };
        }

        private static JsonArray ListPrompts() => new()
        {
            Prompt("help", "Show bridge and MCP usage guidance."),
            Prompt("fix_current_errors", "Gather errors and propose patch flow."),
            Prompt("open_solution_and_wait_ready", "Run ensure then ready flow."),
        };

        private static JsonObject Prompt(string name, string description) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["arguments"] = new JsonArray(),
        };

        private static JsonNode GetPrompt(JsonNode? id, JsonObject? p)
        {
            var name = p?["name"]?.GetValue<string>() ?? throw new McpRequestException(id, -32602, "prompts/get missing name.");
            var text = name switch
            {
                "help" => "Use tools state, errors, warnings, list_tabs, open_file, search_symbols, quick_info, and apply_diff.",
                "fix_current_errors" => "Call errors, inspect rows, then use open_file, quick_info, search_symbols, and apply_diff.",
                "open_solution_and_wait_ready" => "Outside MCP, run: vs-ide-bridge ensure --solution <path>; then call state until ready.",
                _ => throw new McpRequestException(id, -32602, $"Unknown prompt: {name}"),
            };

            return new JsonObject
            {
                ["description"] = $"Bridge prompt: {name}",
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
                    },
                },
            };
        }

        private static async Task<JsonObject> SendBridgeAsync(PipeDiscovery discovery, CliOptions options, string command, string args)
        {
            await using var client = new PipeClient(discovery.PipeName, options.GetInt32("timeout-ms", 10_000));
            var request = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N")[..8],
                ["command"] = command,
                ["args"] = args,
            };

            return await client.SendAsync(request).ConfigureAwait(false);
        }

        private static string BuildArgs(params (string Name, string? Value)[] items)
        {
            var builder = new PipeArgsBuilder();
            foreach (var (name, value) in items)
            {
                builder.Add(name, value);
            }

            return builder.Build();
        }

        private static JsonObject EmptySchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false,
        };

        private static JsonObject ObjectSchema(params (string Name, JsonObject Schema, bool Required)[] properties)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            };

            var propertyBag = (JsonObject)schema["properties"]!;
            var required = new JsonArray();
            foreach (var (name, propertySchema, isRequired) in properties)
            {
                propertyBag[name] = propertySchema;
                if (isRequired)
                {
                    required.Add(name);
                }
            }

            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            return schema;
        }

        private static JsonObject StringSchema(string description) => new()
        {
            ["type"] = "string",
            ["description"] = description,
        };

        private static JsonObject IntegerSchema(string description) => new()
        {
            ["type"] = "integer",
            ["description"] = description,
        };

        private static JsonObject CreateErrorResponse(JsonNode? id, int code, string message) => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        private static async Task<JsonObject?> ReadMessageAsync(Stream input)
        {
            var header = await ReadHeaderAsync(input).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(header))
            {
                return null;
            }

            var lengthLine = header.Split('\n').FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (lengthLine is null)
            {
                throw new McpRequestException(null, -32600, "MCP request missing Content-Length header.");
            }

            if (!int.TryParse(lengthLine.Split(':', 2)[1].Trim(), out var length) || length < 0)
            {
                throw new McpRequestException(null, -32600, "MCP request has invalid Content-Length.");
            }

            var payloadBytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await input.ReadAsync(payloadBytes.AsMemory(offset, length - offset)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new McpRequestException(null, -32600, "Unexpected EOF while reading MCP payload.");
                }

                offset += read;
            }

            var json = Encoding.UTF8.GetString(payloadBytes);
            return JsonNode.Parse(json) as JsonObject
                ?? throw new McpRequestException(null, -32600, "MCP request must be a JSON object.");
        }

        private static async Task<string> ReadHeaderAsync(Stream input)
        {
            var bytes = new List<byte>();
            var lastFour = new Queue<byte>(4);
            while (true)
            {
                var b = new byte[1];
                var read = await input.ReadAsync(b, 0, 1).ConfigureAwait(false);
                if (read == 0)
                {
                    return string.Empty;
                }

                bytes.Add(b[0]);
                lastFour.Enqueue(b[0]);
                if (lastFour.Count > 4)
                {
                    lastFour.Dequeue();
                }

                if (lastFour.Count == 4 && lastFour.SequenceEqual(new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' }))
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
            }
        }

        private static async Task WriteMessageAsync(Stream output, JsonObject response)
        {
            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString(McpJsonOptions));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
            await output.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await output.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }

        private sealed class McpRequestException : Exception
        {
            public McpRequestException(JsonNode? id, int code, string message)
                : base(message)
            {
                Id = id;
                Code = code;
            }

            public JsonNode? Id { get; }

            public int Code { get; }
        }
    }
}
