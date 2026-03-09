using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeCli;

internal static partial class CliApp
{
    private static readonly JsonSerializerOptions McpJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<int> RunMcpServerAsync(CliOptions options)
    {
        await McpServer.RunAsync(options).ConfigureAwait(false);
        return 0;
    }

    private static class McpServer
    {
        private static readonly string McpLog = Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "mcp-server.log");
        private const string ActivateWindowArgumentName = "activate_window";
        private const string CodeArgumentName = "code";
        private const string ColumnArgumentName = "column";
        private const string ConfigurationArgumentName = "configuration";
        private const int DefaultGitDiffContextLines = 3;
        private const int DefaultGitHubIssueSearchLimit = 20;
        private const int DefaultGitLogMaxCount = 20;
        private const string DotNetExecutableName = "dotnet";
        private const string FileArgumentName = "file";
        private const string GitExecutableName = "git";
        private const string GroupByArgumentName = "group_by";
        private const int JsonRpcInvalidRequestCode = -32600;
        private const int JsonRpcInvalidParamsCode = -32602;
        private const int HeaderTerminatorLength = 4;
        private const string LineArgumentName = "line";
        private const string MaxArgumentName = "max";
        private const string PathArgumentName = "path";
        private const string PlatformArgumentName = "platform";
        private const string ProjectArgumentName = "project";
        private const string QueryArgumentName = "query";
        private const string QuickArgumentName = "quick";
        private const int RawJsonInitialDepth = 1;
        private const string ApplyDiffToolName = "apply_diff";
        private const string CondaExecutableName = "conda";
        private const string DescriptionPropertyName = "description";
        private const string AbsoluteOrSolutionRelativeFilePathDescription = "Absolute or solution-relative file path.";
        private static readonly byte[] RawJsonTerminator = [(byte)'\n'];
        private const string ServiceControlPipeName = "VsIdeBridgeServiceControl";
        private const string HelpToolName = "help";
        private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
        private const string SolutionArgumentName = "solution";
        private const string StructuredContentPropertyName = "structuredContent";
        private static readonly string[] SupportedProtocolVersions = ["2025-03-26", "2024-11-05"];
        private const string SeverityArgumentName = "severity";
        private const string TextArgumentName = "text";
        private const string TimeoutMillisecondsArgumentName = "timeout_ms";
        private const string TimeoutMillisecondsSwitchName = "timeout-ms";
        private const string ToolHelpToolName = "tool_help";
        private const string UnknownMcpToolMessageFormat = "Unknown MCP tool: {toolName}";
        private const string WaitForIntellisenseArgumentName = "wait_for_intellisense";
        private const string WarningsToolName = "warnings";
        private static readonly string[] GitExecutableExtensions = [".exe", ".cmd", ".bat", string.Empty];
        private static readonly string[] GitRelativeCandidatePaths =
        [
            @"Git\cmd\git.exe",
            @"Git\bin\git.exe",
            @"Programs\Git\cmd\git.exe",
            @"Programs\Git\bin\git.exe",
        ];
        private static readonly string[] CondaExecutableExtensions = [".exe", ".cmd", ".bat", string.Empty];
        private static readonly string[] CondaRelativeCandidatePaths =
        [
            @"anaconda3\Scripts\conda.exe",
            @"miniconda3\Scripts\conda.exe",
            @"Miniconda3\Scripts\conda.exe",
        ];

        private enum McpWireFormat
        {
            HeaderFramed,
            RawJson,
        }

        private sealed class McpIncomingMessage
        {
            public required JsonObject Request { get; init; }

            public McpWireFormat WireFormat { get; init; }
        }

        private sealed class BridgeBinding(CliOptions options)
        {
            private readonly CliOptions _options = options;
            private readonly bool _verbose = options.GetFlag("verbose");
            private readonly object _stateGate = new();
            private BridgeInstanceSelector _selector = BridgeInstanceSelector.FromOptions(options);
            private string _lastKnownSolutionPath = string.Empty;
            private PipeDiscovery? _cachedDiscovery;

            public async Task<JsonObject> SendAsync(JsonNode? id, string command, string args)
            {
                try
                {
                    var response = await SendCoreAsync(command, args).ConfigureAwait(false);
                    RememberSolutionPath(TryGetSolutionPath(response));
                    return response;
                }
                catch (CliException ex)
                {
                    throw new McpRequestException(id, -32001, ex.Message);
                }
                catch (TimeoutException ex)
                {
                    throw new McpRequestException(id, -32002, $"Timed out waiting for Visual Studio bridge response: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    return await RetrySendAfterCommunicationFailureAsync(id, command, args, ex, accessDenied: true).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    return await RetrySendAfterCommunicationFailureAsync(id, command, args, ex, accessDenied: false).ConfigureAwait(false);
                }
            }

            public async Task<JsonObject> BindAsync(JsonNode? id, JsonObject? args)
            {
                UpdateSelector(CreateSelector(args));

                try
                {
                    var discovery = await GetDiscoveryAsync().ConfigureAwait(false);
                    RememberSolutionPath(discovery.SolutionPath);

                    return new JsonObject
                    {
                        ["success"] = true,
                        ["binding"] = DiscoveryToJson(discovery),
                        ["selector"] = SelectorToJson(CurrentSelector),
                    };
                }
                catch (CliException ex)
                {
                    throw new McpRequestException(id, -32001, ex.Message);
                }
            }

            public PipeDiscovery? CurrentDiscovery
            {
                get
                {
                    lock (_stateGate)
                    {
                        return _cachedDiscovery;
                    }
                }
            }

            public BridgeInstanceSelector CurrentSelector
            {
                get
                {
                    lock (_stateGate)
                    {
                        return CloneSelector(_selector);
                    }
                }
            }

            public string CurrentSolutionPath
            {
                get
                {
                    lock (_stateGate)
                    {
                        return _lastKnownSolutionPath;
                    }
                }
            }

            public DiscoveryMode DiscoveryMode => ResolveDiscoveryMode(_options);

            public void PreferSolution(string? solutionHint)
            {
                lock (_stateGate)
                {
                    _selector = new BridgeInstanceSelector
                    {
                        InstanceId = _selector.InstanceId,
                        ProcessId = _selector.ProcessId,
                        PipeName = _selector.PipeName,
                        SolutionHint = solutionHint,
                    };
                    _cachedDiscovery = null;
                }

                McpTrace($"selector updated to prefer solution hint '{solutionHint ?? string.Empty}'.");
            }

            private async Task<JsonObject> SendCoreAsync(string command, string args)
            {
                var discovery = await GetDiscoveryAsync().ConfigureAwait(false);
                await using var client = new PipeClient(discovery.PipeName, _options.GetInt32("timeout-ms", 130_000));
                var request = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("N")[..8],
                    ["command"] = command,
                    ["args"] = args,
                };

                return await client.SendAsync(request).ConfigureAwait(false);
            }

            private async Task<PipeDiscovery> GetDiscoveryAsync()
            {
                BridgeInstanceSelector selectorSnapshot;
                lock (_stateGate)
                {
                    if (_cachedDiscovery is not null)
                    {
                        return _cachedDiscovery;
                    }

                    selectorSnapshot = CloneSelector(_selector);
                }

                var discovery = await PipeDiscovery
                    .SelectAsync(selectorSnapshot, _verbose, ResolveDiscoveryMode(_options))
                    .ConfigureAwait(false);

                lock (_stateGate)
                {
                    if (_cachedDiscovery is null && SelectorEquals(_selector, selectorSnapshot))
                    {
                        _cachedDiscovery = discovery;
                    }

                    discovery = _cachedDiscovery ?? discovery;
                }

                McpTrace($"bound instance={discovery.InstanceId} pipe={discovery.PipeName} source={discovery.Source} solution={discovery.SolutionPath}");
                return discovery;
            }

            private async Task<JsonObject> RetrySendAfterCommunicationFailureAsync(JsonNode? id, string command, string args, Exception ex, bool accessDenied)
            {
                if (ClearCachedDiscovery() is not { } cachedDiscovery)
                {
                    throw new McpRequestException(
                        id,
                        -32003,
                        accessDenied
                            ? $"Access denied communicating with Visual Studio bridge pipe: {ex.Message}"
                            : $"Failed communicating with Visual Studio bridge pipe: {ex.Message}");
                }

                McpTrace($"cached pipe '{cachedDiscovery.PipeName}' {(accessDenied ? "access denied" : "failed")}, refreshing binding: {ex.Message}");

                try
                {
                    var response = await SendCoreAsync(command, args).ConfigureAwait(false);
                    RememberSolutionPath(TryGetSolutionPath(response));
                    return response;
                }
                catch (CliException retryEx)
                {
                    throw new McpRequestException(id, -32001, retryEx.Message);
                }
                catch (TimeoutException retryEx)
                {
                    throw new McpRequestException(id, -32002, $"Timed out waiting for Visual Studio bridge response: {retryEx.Message}");
                }
                catch (UnauthorizedAccessException retryEx)
                {
                    throw new McpRequestException(id, -32003, $"Access denied communicating with Visual Studio bridge pipe: {retryEx.Message}");
                }
                catch (IOException retryEx)
                {
                    throw new McpRequestException(id, -32003, $"Failed communicating with Visual Studio bridge pipe: {retryEx.Message}");
                }
            }

            private PipeDiscovery? ClearCachedDiscovery()
            {
                lock (_stateGate)
                {
                    var cachedDiscovery = _cachedDiscovery;
                    _cachedDiscovery = null;
                    return cachedDiscovery;
                }
            }

            private void UpdateSelector(BridgeInstanceSelector selector)
            {
                lock (_stateGate)
                {
                    _selector = CloneSelector(selector);
                    _cachedDiscovery = null;
                }
            }

            private void RememberSolutionPath(string? solutionPath)
            {
                if (string.IsNullOrWhiteSpace(solutionPath))
                {
                    return;
                }

                lock (_stateGate)
                {
                    _lastKnownSolutionPath = solutionPath;
                }
            }

            private static string? TryGetSolutionPath(JsonObject response)
            {
                if (response["Data"] is not JsonObject data)
                {
                    return null;
                }

                return data["solutionPath"]?.GetValue<string>();
            }

            private static BridgeInstanceSelector CloneSelector(BridgeInstanceSelector selector)
            {
                return new BridgeInstanceSelector
                {
                    InstanceId = selector.InstanceId,
                    ProcessId = selector.ProcessId,
                    PipeName = selector.PipeName,
                    SolutionHint = selector.SolutionHint,
                };
            }

            private static bool SelectorEquals(BridgeInstanceSelector left, BridgeInstanceSelector right)
            {
                return string.Equals(left.InstanceId, right.InstanceId, StringComparison.Ordinal)
                    && left.ProcessId == right.ProcessId
                    && string.Equals(left.PipeName, right.PipeName, StringComparison.Ordinal)
                    && string.Equals(left.SolutionHint, right.SolutionHint, StringComparison.Ordinal);
            }

            private static BridgeInstanceSelector CreateSelector(JsonObject? args)
            {
                return new BridgeInstanceSelector
                {
                    InstanceId = GetString(args, "instance_id") ?? GetString(args, "instance"),
                    ProcessId = GetInt32(args, "pid"),
                    PipeName = GetString(args, "pipe_name") ?? GetString(args, "pipe"),
                    SolutionHint = GetString(args, "solution_hint") ?? GetString(args, "sln"),
                };
            }

            private static string? GetString(JsonObject? args, string name)
            {
                var value = args?[name]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            private static int? GetInt32(JsonObject? args, string name)
            {
                return args?[name]?.GetValue<int?>();
            }
        }

        private static void McpTrace(string msg)
        {
            try { File.AppendAllText(McpLog, $"{DateTime.UtcNow:O} {msg}\n"); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        public static async Task RunAsync(CliOptions options)
        {
            try
            {
                File.WriteAllText(McpLog, $"{DateTime.Now:O} mcp-server started\n");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            var bridgeBinding = new BridgeBinding(options);
            var advertiseExtraCapabilities = !options.GetFlag("tools-only");
            var wireFormat = McpWireFormat.HeaderFramed;
            var outputGate = new SemaphoreSlim(1, 1);
            var pendingRequests = new List<Task>();
            McpTrace("stdin/stdout opened");
            NotifyService("CLIENT_CONNECTED");
            while (true)
            {
                try
                {
                    McpTrace("waiting for next message...");
                    var incoming = await ReadMessageAsync(input).ConfigureAwait(false);
                    if (incoming is null)
                    {
                        McpTrace("null request (EOF) — exiting");
                        NotifyService("CLIENT_DISCONNECTED");
                        break;
                    }

                    wireFormat = incoming.WireFormat;
                    pendingRequests.RemoveAll(static pendingRequest => pendingRequest.IsCompleted);
                    pendingRequests.Add(ProcessRequestAsync(incoming, output, outputGate, bridgeBinding, advertiseExtraCapabilities));
                }
                catch (McpRequestException ex)
                {
                    McpTrace($"McpRequestException: {ex.Message}");
                    await WriteResponseAsync(output, CreateErrorResponse(ex.Id, ex.Code, ex.Message), wireFormat, outputGate).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    McpTrace($"JsonException: {ex.Message}");
                    await WriteResponseAsync(output, CreateErrorResponse(null, -32700, $"Parse error: {ex.Message}"), wireFormat, outputGate).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    McpTrace($"Exception: {ex}");
                    await WriteResponseAsync(output, CreateErrorResponse(null, -32603, $"Internal error: {ex.Message}"), wireFormat, outputGate).ConfigureAwait(false);
                }
            }

            if (pendingRequests.Count > 0)
            {
                await Task.WhenAll(pendingRequests).ConfigureAwait(false);
            }
        }

        private static async Task ProcessRequestAsync(McpIncomingMessage incoming, Stream output, SemaphoreSlim outputGate, BridgeBinding bridgeBinding, bool advertiseExtraCapabilities)
        {
            JsonObject? response;
            var request = incoming.Request;
            var method = request["method"]?.GetValue<string>() ?? "(null)";
            NotifyService("MCP_REQUEST");
            McpTrace($"got request method={method}");

            var trackInFlight = string.Equals(method, "tools/call", StringComparison.Ordinal);
            if (trackInFlight)
            {
                NotifyService("COMMAND_START");
            }

            try
            {
                response = await HandleRequestAsync(request, bridgeBinding, advertiseExtraCapabilities).ConfigureAwait(false);
                McpTrace($"handled method={method} response={(response is null ? "null" : "ok")}");
            }
            catch (McpRequestException ex)
            {
                McpTrace($"McpRequestException: {ex.Message}");
                response = CreateErrorResponse(ex.Id, ex.Code, ex.Message);
            }
            catch (JsonException ex)
            {
                McpTrace($"JsonException: {ex.Message}");
                response = CreateErrorResponse(null, -32700, $"Parse error: {ex.Message}");
            }
            catch (Exception ex)
            {
                McpTrace($"Exception: {ex}");
                response = CreateErrorResponse(null, -32603, $"Internal error: {ex.Message}");
            }
            finally
            {
                if (trackInFlight)
                {
                    NotifyService("COMMAND_END");
                }
            }

            if (response is not null)
            {
                await WriteResponseAsync(output, response, incoming.WireFormat, outputGate).ConfigureAwait(false);
            }
        }

        private static async Task WriteResponseAsync(Stream output, JsonObject response, McpWireFormat wireFormat, SemaphoreSlim outputGate)
        {
            await outputGate.WaitAsync().ConfigureAwait(false);
            try
            {
                McpTrace("writing response...");
                await WriteMessageAsync(output, response, wireFormat).ConfigureAwait(false);
                McpTrace("response written");
            }
            finally
            {
                outputGate.Release();
            }
        }

        private static long _serviceUnavailableUntilTicks;

        private static void NotifyService(string evt)
        {
            if (string.IsNullOrWhiteSpace(evt))
            {
                return;
            }

            // Skip if service was recently unavailable to avoid repeated 50ms connect timeouts.
            if (Environment.TickCount64 < Interlocked.Read(ref _serviceUnavailableUntilTicks))
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".",
                        ServiceControlPipeName,
                        PipeDirection.Out,
                        PipeOptions.Asynchronous);

                    pipe.Connect(50);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
                    {
                        AutoFlush = true,
                    };

                    writer.WriteLine(evt);
                }
                catch
                {
                    // Back off for 30s when service is unavailable.
                    Interlocked.Exchange(ref _serviceUnavailableUntilTicks, Environment.TickCount64 + 30_000);
                }
            });
        }

        private static async Task<JsonObject?> HandleRequestAsync(JsonObject request, BridgeBinding bridgeBinding, bool advertiseExtraCapabilities)
        {
            var id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? string.Empty;
            var @params = request["params"] as JsonObject;

            if (method.StartsWith("notifications/", StringComparison.Ordinal))
                return null;

            JsonNode result = method switch
            {
                "initialize" => InitializeResult(@params, advertiseExtraCapabilities),
                "tools/list" => new JsonObject { ["tools"] = ListTools() },
                "tools/call" => await CallToolAsync(id, @params, bridgeBinding).ConfigureAwait(false),
                "resources/list" => new JsonObject { ["resources"] = ListResources() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = ListResourceTemplates() },
                "resources/read" => await ReadResourceAsync(id, @params, bridgeBinding).ConfigureAwait(false),
                "prompts/list" => new JsonObject { ["prompts"] = ListPrompts() },
                "prompts/get" => GetPrompt(id, @params),
                "ping" => new JsonObject(),
                _ => throw new McpRequestException(id, -32601, $"Unsupported MCP method: {method}"),
            };

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JsonObject InitializeResult(JsonObject? @params, bool advertiseExtraCapabilities)
        {
            var capabilities = new JsonObject
            {
                ["tools"] = new JsonObject(),
            };

            if (advertiseExtraCapabilities)
            {
                capabilities["resources"] = new JsonObject { ["subscribe"] = false };
                capabilities["prompts"] = new JsonObject();
            }

            return new JsonObject
            {
                ["protocolVersion"] = SelectProtocolVersion(@params?["protocolVersion"]?.GetValue<string>()),
                ["capabilities"] = capabilities,
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "vs-ide-bridge-mcp",
                    ["version"] = "0.1.0",
                },
            };
        }

        private static string SelectProtocolVersion(string? clientProtocolVersion)
        {
            if (string.IsNullOrWhiteSpace(clientProtocolVersion))
            {
                return SupportedProtocolVersions[0];
            }

            if (SupportedProtocolVersions.Contains(clientProtocolVersion, StringComparer.Ordinal))
            {
                return clientProtocolVersion;
            }

            // Compatibility fallback for clients that require an exact echo.
            McpTrace($"initialize: client requested unsupported protocolVersion={clientProtocolVersion}; echoing for compatibility.");
            return clientProtocolVersion;
        }

        private static JsonArray ListTools() =>
        [
            Tool("state", "Capture current Visual Studio bridge state.", EmptySchema()),
            Tool("ready", "Wait for Visual Studio/IntelliSense readiness before semantic diagnostics.", EmptySchema()),
            Tool(
                ToolHelpToolName,
                "Return MCP tool help with descriptions, schemas, and examples. Pass name for one tool or omit for all.",
                ObjectSchema(OptionalStringProperty("name", "Optional tool name for focused help."))),
            Tool(
                HelpToolName,
                $"Alias for {ToolHelpToolName}. Return MCP tool help with descriptions, schemas, and examples.",
                ObjectSchema(OptionalStringProperty("name", "Optional tool name for focused help."))),
            Tool("bridge_health", "Get binding health, discovery source, and last round-trip metrics.", EmptySchema()),
            Tool("list_instances", "List live VS IDE Bridge instances visible to this MCP server.", EmptySchema()),
            Tool(
                "bind_instance",
                "Bind this MCP session to one Visual Studio bridge instance.",
                ObjectSchema(
                    OptionalStringProperty("instance_id", "Optional exact bridge instance id."),
                    OptionalIntegerProperty("pid", "Optional Visual Studio process id."),
                    OptionalStringProperty("pipe_name", "Optional exact bridge pipe name."),
                    OptionalStringProperty("solution_hint", "Optional solution path or name substring."))),
            Tool(
                "bind_solution",
                "Bind this MCP session to the Visual Studio bridge instance whose solution matches a name or path hint.",
                ObjectSchema(RequiredStringProperty(SolutionArgumentName, "Solution name or path substring to match."))),
            Tool(
                "errors",
                "Capture Error List rows with optional severity and text filters.",
                ObjectSchema(
                    OptionalStringProperty(SeverityArgumentName, "Optional severity filter."),
                    OptionalBooleanProperty(WaitForIntellisenseArgumentName, "Wait for IntelliSense readiness first (default true)."),
                    OptionalBooleanProperty("quick", "Read current snapshot immediately without stability polling (default false)."),
                    OptionalIntegerProperty("max", "Optional max rows."),
                    OptionalStringProperty("code", "Optional diagnostic code prefix filter."),
                    OptionalStringProperty("project", "Optional project filter."),
                    OptionalStringProperty("path", "Optional path filter."),
                    OptionalStringProperty("text", "Optional message text filter."),
                    OptionalStringProperty("group_by", "Optional grouping mode."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName, "Optional wait timeout in milliseconds."))),
            Tool(
                WarningsToolName,
                "Capture warning rows with optional code, path, and project filters.",
                ObjectSchema(
                    OptionalStringProperty(SeverityArgumentName, "Optional severity filter."),
                    OptionalBooleanProperty(WaitForIntellisenseArgumentName, "Wait for IntelliSense readiness first (default true)."),
                    OptionalBooleanProperty("quick", "Read current snapshot immediately without stability polling (default false)."),
                    OptionalIntegerProperty("max", "Optional max rows."),
                    OptionalStringProperty("code", "Optional diagnostic code prefix filter."),
                    OptionalStringProperty("project", "Optional project filter."),
                    OptionalStringProperty("path", "Optional path filter."),
                    OptionalStringProperty("text", "Optional message text filter."),
                    OptionalStringProperty("group_by", "Optional grouping mode."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName, "Optional wait timeout in milliseconds."))),
            Tool("list_tabs", "List open editor tabs.", EmptySchema()),
            Tool(
                "open_file",
                "Open an absolute path, solution-relative path, or solution item name and optional line/column.",
                ObjectSchema(
                    RequiredStringProperty("file", "Absolute path, solution-relative path, or solution item name."),
                    OptionalIntegerProperty("line", "Optional 1-based line number."),
                    OptionalIntegerProperty("column", "Optional 1-based column number."),
                    OptionalBooleanProperty("allow_disk_fallback", "Allow disk fallback under solution root when solution items do not match (default true)."))),
            Tool(
                "find_files",
                "Search solution explorer files by name or path fragment.",
                ObjectSchema(
                    RequiredStringProperty("query", "File name or path fragment."),
                    OptionalStringProperty("path", "Optional path fragment filter."),
                    OptionalStringArrayProperty("extensions", "Optional extension filters like ['.cmake','.txt']."),
                    OptionalIntegerProperty("max_results", "Optional max result count (default 200)."),
                    OptionalBooleanProperty("include_non_project", "Include disk files under solution root that are not in projects (default true)."))),
            Tool(
                "search_symbols",
                "Search symbol definitions by name across solution scope.",
                ObjectSchema(
                    RequiredStringProperty("query", "Symbol search text."),
                    OptionalStringProperty("kind", "Optional symbol kind filter."),
                    OptionalStringProperty("scope", "Optional scope: solution, project, document, or open."),
                    OptionalStringProperty("project", "Optional project filter."),
                    OptionalStringProperty("path", "Optional path or directory filter."),
                    OptionalIntegerProperty("max", "Optional max result count."),
                    OptionalBooleanProperty("match_case", "Case-sensitive match (default false)."))),
            Tool(
                "count_references",
                "Count symbol references at file/line/column with exact-or-explicit semantics.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName, AbsoluteOrSolutionRelativeFilePathDescription),
                    RequiredIntegerProperty("line", "1-based line number."),
                    RequiredIntegerProperty("column", "1-based column number."),
                    OptionalBooleanProperty(ActivateWindowArgumentName, "Activate references window while counting (default true)."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName, "Optional window wait timeout in milliseconds."))),
            Tool(
                "quick_info",
                "Get quick info at file/line/column.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName, AbsoluteOrSolutionRelativeFilePathDescription),
                    RequiredIntegerProperty("line", "1-based line number."),
                    RequiredIntegerProperty("column", "1-based column number."))),
            Tool(
                ApplyDiffToolName,
                "Apply unified diff text or editor patch text through the live editor so changes are visible in Visual Studio. Changed files open by default.",
                ObjectSchema(
                    RequiredStringProperty("patch", "Unified diff text or editor patch text."),
                    OptionalBooleanProperty("post_check", "If true, run ready and errors after applying diff."))),
            Tool("list_documents", "List open documents from the IDE document table.", EmptySchema()),
            Tool(
                "activate_document",
                "Activate one open document by path or name.",
                ObjectSchema(RequiredStringProperty("query", "Document path or name fragment."))),
            Tool(
                "close_document",
                "Close one matching document, or all open documents.",
                ObjectSchema(
                    OptionalStringProperty("query", "Document path or name fragment."),
                    OptionalBooleanProperty("all", "Close all open documents (default false)."),
                    OptionalBooleanProperty("save", "Save before closing (default false)."))),
            Tool(
                "save_document",
                "Save one document or all open documents.",
                ObjectSchema(
                    OptionalStringProperty("file", "File path to save, or omit for active document."),
                    OptionalBooleanProperty("all", "Save all open documents (default false)."))),
            Tool(
                "close_file",
                "Close one open file tab.",
                ObjectSchema(
                    OptionalStringProperty("file", "File path."),
                    OptionalStringProperty("query", "Name fragment."),
                    OptionalBooleanProperty("save", "Save before closing (default false)."))),
            Tool(
                "close_others",
                "Close all tabs except the active one.",
                ObjectSchema(OptionalBooleanProperty("save", "Save before closing (default false)."))),
            Tool(
                "list_windows",
                "List open tool/document windows.",
                ObjectSchema(OptionalStringProperty("query", "Optional caption filter."))),
            Tool(
                ActivateWindowArgumentName,
                "Activate one window by caption fragment.",
                ObjectSchema(RequiredStringProperty("window", "Window caption fragment."))),
            Tool(
                "goto_definition",
                "Jump to definition at file/line/column.",
                FileLineColumnSchema()),
            Tool(
                "goto_implementation",
                "Jump to one implementation at file/line/column.",
                FileLineColumnSchema()),
            Tool(
                "call_hierarchy",
                "Open Call Hierarchy at file/line/column.",
                FileLineColumnSchema()),
            Tool(
                "build_errors",
                "Build the solution and capture errors after completion.",
                ObjectSchema(
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName, "Optional build timeout in milliseconds."),
                    OptionalIntegerProperty(MaxArgumentName, "Optional max error rows."))),
            Tool("debug_threads", "Get debugger thread snapshot.", EmptySchema()),
            Tool(
                "debug_stack",
                "Get debugger stack frames for current or selected thread.",
                ObjectSchema(
                    OptionalIntegerProperty("thread_id", "Optional debugger thread id."),
                    OptionalIntegerProperty("max_frames", "Optional max frames (default 100)."))),
            Tool(
                "debug_locals",
                "Get local variables for the current stack frame.",
                ObjectSchema(OptionalIntegerProperty(MaxArgumentName, "Optional max locals (default 200)."))),
            Tool("debug_modules", "Get debugger modules snapshot (best effort).", EmptySchema()),
            Tool(
                "debug_watch",
                "Evaluate one watch expression in break mode.",
                ObjectSchema(
                    RequiredStringProperty("expression", "Debugger watch expression."),
                    OptionalIntegerProperty(TimeoutMillisecondsArgumentName, "Optional evaluation timeout milliseconds (default 1000)."))),
            Tool("debug_exceptions", "Get debugger exception settings snapshot (best effort).", EmptySchema()),
            Tool(
                "diagnostics_snapshot",
                "Capture IDE/debug/build state and errors/warnings in one response.",
                ObjectSchema(
                    OptionalBooleanProperty(WaitForIntellisenseArgumentName, "Wait for IntelliSense before diagnostics (default true)."),
                    OptionalBooleanProperty(QuickArgumentName, "Use quick diagnostics snapshot mode (default false)."),
                    OptionalIntegerProperty(MaxArgumentName, "Optional max diagnostics rows for errors/warnings."))),
            Tool("build_configurations", "List solution build configurations/platforms.", EmptySchema()),
            Tool(
                "set_build_configuration",
                "Activate one build configuration/platform pair.",
                ObjectSchema(
                    RequiredStringProperty(ConfigurationArgumentName, "Build configuration name (e.g. Debug)."),
                    OptionalStringProperty(PlatformArgumentName, "Optional platform (e.g. x64)."))),
            Tool("git_status", "Get repository status in porcelain mode.", EmptySchema()),
            Tool("git_current_branch", "Get current branch name (short).", EmptySchema()),
            Tool("git_remote_list", "List configured remotes with URLs.", EmptySchema()),
            Tool("git_tag_list", "List tags sorted by version-aware refname.", EmptySchema()),
            Tool("git_stash_list", "List stash entries.", EmptySchema()),
            Tool(
                "git_diff_unstaged",
                "Show unstaged diff with optional context lines.",
                ObjectSchema(OptionalIntegerProperty("context", "Optional context line count (default 3)."))),
            Tool(
                "git_diff_staged",
                "Show staged diff with optional context lines.",
                ObjectSchema(OptionalIntegerProperty("context", "Optional context line count (default 3)."))),
            Tool(
                "git_log",
                "Show recent commits in a compact machine-friendly format.",
                ObjectSchema(OptionalIntegerProperty("max_count", $"Optional max commit count (default {DefaultGitLogMaxCount})."))),
            Tool(
                "git_show",
                "Show metadata and patch for a specific commit.",
                ObjectSchema(RequiredStringProperty("revision", "Commit-ish (hash, HEAD~1, tag)."))),
            Tool("git_branch_list", "List local and remote branches.", EmptySchema()),
            Tool(
                "git_checkout",
                "Checkout an existing branch or revision.",
                ObjectSchema(RequiredStringProperty("target", "Branch name or revision to checkout."))),
            Tool(
                "git_create_branch",
                "Create and switch to a new branch.",
                ObjectSchema(
                    RequiredStringProperty("name", "New branch name."),
                    OptionalStringProperty("start_point", "Optional start point (default HEAD)."))),
            Tool(
                "git_add",
                "Stage files. Use ['.'] to stage all changes.",
                ObjectSchema(RequiredStringArrayProperty("paths", "File paths, globs, or '.' to stage all."))),
            Tool(
                "git_restore",
                "Restore paths from HEAD in the working tree.",
                ObjectSchema(RequiredStringArrayProperty("paths", "File paths or globs to restore."))),
            Tool(
                "git_commit",
                "Create a commit from staged changes.",
                ObjectSchema(RequiredStringProperty("message", "Commit message."))),
            Tool(
                "git_commit_amend",
                "Amend the previous commit. Optionally replace the message.",
                ObjectSchema(
                    OptionalStringProperty("message", "Optional replacement commit message."),
                    OptionalBooleanProperty("no_edit", "If true, keep the current commit message."))),
            Tool(
                "git_reset",
                "Unstage paths while keeping working tree changes.",
                ObjectSchema(OptionalStringArrayProperty("paths", "File paths or globs to unstage."))),
            Tool(
                "git_fetch",
                "Fetch updates from remotes.",
                ObjectSchema(
                    OptionalStringProperty("remote", "Optional remote name."),
                    OptionalBooleanProperty("all", "If true, fetch all remotes."),
                    OptionalBooleanProperty("prune", "If true, prune deleted remote refs (default true)."))),
            Tool(
                "git_stash_push",
                "Stash local changes. Optionally include untracked files and a message.",
                ObjectSchema(
                    OptionalStringProperty("message", "Optional stash message."),
                    OptionalBooleanProperty("include_untracked", "If true, include untracked files."))),
            Tool("git_stash_pop", "Apply and drop the latest stash entry.", EmptySchema()),
            Tool(
                "git_pull",
                "Pull updates from a remote branch.",
                ObjectSchema(
                    OptionalStringProperty("remote", "Optional remote name (default current tracking remote)."),
                    OptionalStringProperty("branch", "Optional branch name."))),
            Tool(
                "git_push",
                "Push current branch to remote.",
                ObjectSchema(
                    OptionalStringProperty("remote", "Optional remote name (default current tracking remote)."),
                    OptionalStringProperty("branch", "Optional branch name."),
                    OptionalBooleanProperty("set_upstream", "If true, pass --set-upstream."))),
            Tool(
                "github_issue_search",
                "Search open or closed GitHub issues.",
                ObjectSchema(
                    OptionalStringProperty(QueryArgumentName, "Free-text search query."),
                    OptionalStringProperty("state", "open, closed, or all."),
                    OptionalStringProperty("repo", "Optional owner/repo. Defaults to git origin repo."),
                    OptionalIntegerProperty("limit", $"Max results (default {DefaultGitHubIssueSearchLimit})."))),
            Tool(
                "github_issue_close",
                "Close a GitHub issue by number and optionally add a comment.",
                ObjectSchema(
                    RequiredIntegerProperty("issue_number", "Issue number to close."),
                    OptionalStringProperty("repo", "Optional owner/repo. Defaults to git origin repo."),
                    OptionalStringProperty("comment", "Optional closing comment."))),
            Tool(
                "nuget_restore",
                "Restore NuGet packages with dotnet restore for the active solution or a specific path.",
                ObjectSchema(OptionalStringProperty(PathArgumentName, "Optional solution/project path. Defaults to the active bridge solution."))),
            Tool(
                "nuget_add_package",
                "Add a NuGet package reference to a project via dotnet add package.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName, "Project path (.csproj/.fsproj/.vbproj), absolute or solution-relative."),
                    RequiredStringProperty("package", "NuGet package id to add."),
                    OptionalStringProperty("version", "Optional package version."),
                    OptionalStringProperty("source", "Optional package source (name or URL)."),
                    OptionalBooleanProperty("prerelease", "If true, include prerelease versions."),
                    OptionalBooleanProperty("no_restore", "If true, skip restore after adding the package."))),
            Tool(
                "nuget_remove_package",
                "Remove a NuGet package reference from a project via dotnet remove package.",
                ObjectSchema(
                    RequiredStringProperty(ProjectArgumentName, "Project path (.csproj/.fsproj/.vbproj), absolute or solution-relative."),
                    RequiredStringProperty("package", "NuGet package id to remove."))),
            Tool(
                "conda_install",
                "Install one or more packages into a conda environment.",
                ObjectSchema(
                    RequiredStringArrayProperty("packages", "One or more conda package specs (for example ['numpy','cmake>=3.29'])."),
                    OptionalStringProperty("name", "Optional environment name (-n/--name)."),
                    OptionalStringProperty("prefix", "Optional environment prefix path (--prefix)."),
                    OptionalStringArrayProperty("channels", "Optional channels to add with --channel."),
                    OptionalBooleanProperty("dry_run", "If true, run with --dry-run."),
                    OptionalBooleanProperty("yes", "Auto-confirm install (default true)."))),
            Tool(
                "conda_remove",
                "Remove one or more packages from a conda environment.",
                ObjectSchema(
                    RequiredStringArrayProperty("packages", "One or more package names to remove."),
                    OptionalStringProperty("name", "Optional environment name (-n/--name)."),
                    OptionalStringProperty("prefix", "Optional environment prefix path (--prefix)."),
                    OptionalBooleanProperty("dry_run", "If true, run with --dry-run."),
                    OptionalBooleanProperty("yes", "Auto-confirm remove (default true)."))),
            Tool(
                "find_text",
                "Full-text search across the solution or a path subtree. Returns file paths, line numbers and preview text.",
                ObjectSchema(
                    RequiredStringProperty(QueryArgumentName, "Search text or regex pattern."),
                    OptionalStringProperty(PathArgumentName, "Optional path or directory filter (solution-relative or absolute)."),
                    OptionalStringProperty("scope", "Scope: solution (default), project, or document."),
                    OptionalStringProperty(ProjectArgumentName, "Optional project filter."),
                    OptionalIntegerProperty("results_window", "Optional Find Results window number."),
                    OptionalBooleanProperty("match_case", "Case-sensitive match (default false)."),
                    OptionalBooleanProperty("whole_word", "Match whole word only (default false)."),
                    OptionalBooleanProperty("regex", "Treat query as a regular expression (default false)."))),
            Tool(
                "read_file",
                "Read lines from a file. Use start_line/end_line for a range, or line with context_before/context_after centered on an anchor.",
                ObjectSchema(
                    RequiredStringProperty(FileArgumentName, AbsoluteOrSolutionRelativeFilePathDescription),
                    OptionalIntegerProperty("start_line", "First 1-based line to read. Use with end_line for a range."),
                    OptionalIntegerProperty("end_line", "Last 1-based line to read (inclusive). Use with start_line."),
                    OptionalIntegerProperty(LineArgumentName, "Anchor 1-based line. Use with context_before/context_after."),
                    OptionalIntegerProperty("context_before", "Lines before anchor (default 10)."),
                    OptionalIntegerProperty("context_after", "Lines after anchor (default 30)."),
                    OptionalBooleanProperty("reveal_in_editor", "Whether to reveal the slice in the editor (default true)."))),
            Tool(
                "find_references",
                "Find all references to the symbol at file/line/column using VS IntelliSense.",
                FileLineColumnSchema()),
            Tool(
                "peek_definition",
                "Return the definition source and surrounding context of the symbol at file/line/column.",
                FileLineColumnSchema()),
            Tool(
                "file_outline",
                "Get the symbol outline (namespaces, classes, methods, fields, etc.) of a file.",
                ObjectSchema(RequiredStringProperty(FileArgumentName, AbsoluteOrSolutionRelativeFilePathDescription))),
            Tool(
                "build",
                "Trigger a solution build and return errors/warnings. Builds may take several minutes.",
                ObjectSchema(
                    OptionalStringProperty(ConfigurationArgumentName, "Optional build configuration (e.g. Debug, Release)."),
                    OptionalStringProperty(PlatformArgumentName, "Optional build platform (e.g. x64)."))),
            Tool(
                "open_solution",
                "Open a solution file in the current Visual Studio instance without opening a new window.",
                ObjectSchema(
                    RequiredStringProperty(SolutionArgumentName, "Absolute path to the .sln file to open."),
                    OptionalBooleanProperty("wait_for_ready", "Wait for readiness after opening the solution (default true).")))
        ];

        private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
        {
            ["name"] = name,
            [DescriptionPropertyName] = ResolveToolDescription(name, description),
            ["inputSchema"] = inputSchema,
        };

        private static JsonObject ToolResult(JsonObject result) => new()
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.ToJsonString(JsonOptions),
                },
            },
            ["isError"] = !(result["success"]?.GetValue<bool>() ?? false),
            [StructuredContentPropertyName] = result,
        };

        private static string FormatUnknownMcpToolMessage(string toolName)
        {
            return UnknownMcpToolMessageFormat.Replace("{toolName}", toolName, StringComparison.Ordinal);
        }

        private static string ResolveToolDescription(string toolName, string fallback)
        {
            var bridgeCommand = ResolveBridgeCommandForTool(toolName);
            if (string.IsNullOrWhiteSpace(bridgeCommand))
            {
                return fallback;
            }

            return BridgeCommandCatalog.TryGetByPipeName(bridgeCommand, out var metadata)
                ? metadata.Description
                : fallback;
        }

        private static async Task<JsonNode> CallToolAsync(JsonNode? id, JsonObject? p, BridgeBinding bridgeBinding)
        {
            var toolName = GetOptionalStringArgument(p, "name") ?? throw new McpRequestException(id, JsonRpcInvalidParamsCode, "tools/call missing name.");
            var args = p?["arguments"] as JsonObject;

            if (string.Equals(toolName, ToolHelpToolName, StringComparison.Ordinal) ||
                string.Equals(toolName, HelpToolName, StringComparison.Ordinal))
            {
                return ToolHelp(id, GetOptionalStringArgument(args, "name"));
            }

            if (string.Equals(toolName, "bridge_health", StringComparison.Ordinal))
            {
                return await BridgeHealthAsync(id, bridgeBinding).ConfigureAwait(false);
            }

            if (toolName.StartsWith("git_", StringComparison.Ordinal))
            {
                return await CallGitToolAsync(id, toolName, args, bridgeBinding).ConfigureAwait(false);
            }

            if (toolName.StartsWith("github_", StringComparison.Ordinal))
            {
                return await CallGitHubToolAsync(id, toolName, args, bridgeBinding).ConfigureAwait(false);
            }

            if (toolName.StartsWith("nuget_", StringComparison.Ordinal))
            {
                return await CallNuGetToolAsync(id, toolName, args, bridgeBinding).ConfigureAwait(false);
            }

            if (toolName.StartsWith("conda_", StringComparison.Ordinal))
            {
                return await CallCondaToolAsync(id, toolName, args, bridgeBinding).ConfigureAwait(false);
            }

            if (string.Equals(toolName, "list_instances", StringComparison.Ordinal))
            {
                return await ListInstancesAsync(bridgeBinding).ConfigureAwait(false);
            }

            if (string.Equals(toolName, "bind_instance", StringComparison.Ordinal))
            {
                var result = await bridgeBinding.BindAsync(id, args).ConfigureAwait(false);
                return ToolResult(result);
            }

            if (string.Equals(toolName, "bind_solution", StringComparison.Ordinal))
            {
                var bindArgs = new JsonObject
                {
                    ["solution_hint"] = args?[SolutionArgumentName]?.DeepClone() ?? args?["solution_hint"]?.DeepClone(),
                };

                var result = await bridgeBinding.BindAsync(id, bindArgs).ConfigureAwait(false);
                return ToolResult(result);
            }

            if (string.Equals(toolName, "open_solution", StringComparison.Ordinal))
            {
                return await OpenSolutionAsync(id, args, bridgeBinding).ConfigureAwait(false);
            }

            var (command, commandArgs) = toolName switch
            {
                "state" => ("state", string.Empty),
                "ready" => ("ready", string.Empty),
                "errors" => ("errors", BuildDiagnosticsArgs(args)),
                WarningsToolName => (WarningsToolName, BuildDiagnosticsArgs(args)),
                "list_tabs" => ("list-tabs", string.Empty),
                "open_file" => ("open-document", BuildOpenFileArgs(args)),
                "find_files" => ("find-files", BuildFindFilesArgs(args)),
                "search_symbols" => ("search-symbols", BuildSearchSymbolsArgs(args)),
                "count_references" => ("count-references", BuildCountReferencesArgs(args)),
                "quick_info" => ("quick-info", BuildFileLineColumnArgs(args)),
                ApplyDiffToolName => ("apply-diff", BuildApplyDiffArgs(args)),
                "list_documents" => ("list-documents", string.Empty),
                "activate_document" => ("activate-document", BuildSingleStringSwitchArg(args, QueryArgumentName, QueryArgumentName)),
                "close_document" => ("close-document", BuildCloseDocumentArgs(args)),
                "save_document" => ("save-document", BuildSaveDocumentArgs(args)),
                "close_file" => ("close-file", BuildCloseFileArgs(args)),
                "close_others" => ("close-others", BuildSaveOnlyArgs(args)),
                "list_windows" => ("list-windows", BuildSingleStringSwitchArg(args, QueryArgumentName, QueryArgumentName)),
                ActivateWindowArgumentName => ("activate-window", BuildSingleStringSwitchArg(args, "window", "window")),
                "goto_definition" => ("goto-definition", BuildFileLineColumnArgs(args)),
                "goto_implementation" => ("goto-implementation", BuildFileLineColumnArgs(args)),
                "call_hierarchy" => ("call-hierarchy", BuildFileLineColumnArgs(args)),
                "build_errors" => ("build-errors", BuildArgs((TimeoutMillisecondsSwitchName, GetBuildErrorsTimeoutArgument(id, args)), ("max", GetOptionalArgumentText(args, "max")))),
                "debug_threads" => ("debug-threads", string.Empty),
                "debug_stack" => ("debug-stack", BuildArgs(("thread-id", GetOptionalArgumentText(args, "thread_id")), ("max-frames", GetOptionalArgumentText(args, "max_frames")))),
                "debug_locals" => ("debug-locals", BuildArgs(("max", GetOptionalArgumentText(args, "max")))),
                "debug_modules" => ("debug-modules", string.Empty),
                "debug_watch" => ("debug-watch", BuildArgs(("expression", GetOptionalStringArgument(args, "expression")), (TimeoutMillisecondsSwitchName, GetOptionalArgumentText(args, TimeoutMillisecondsArgumentName)))),
                "debug_exceptions" => ("debug-exceptions", string.Empty),
                "diagnostics_snapshot" => ("diagnostics-snapshot", BuildArgs(
                    ("wait-for-intellisense", GetOptionalBooleanArgument(args, WaitForIntellisenseArgumentName, true, true)),
                    ("quick", GetOptionalBooleanArgument(args, "quick", false, true)),
                    ("max", GetOptionalArgumentText(args, "max")))),
                "build_configurations" => ("build-configurations", string.Empty),
                "set_build_configuration" => ("set-build-configuration", BuildConfigurationPlatformArgs(args)),
                "find_text" => ("find-text", BuildFindTextArgs(args)),
                "read_file" => ("document-slice", BuildReadFileArgs(args)),
                "find_references" => ("find-references", BuildFileLineColumnArgs(args)),
                "peek_definition" => ("peek-definition", BuildFileLineColumnArgs(args)),
                "file_outline" => ("file-outline", BuildSingleStringSwitchArg(args, FileArgumentName, FileArgumentName)),
                "build" => ("build", BuildConfigurationPlatformArgs(args)),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName)),
            };

            // Pre-build diagnostics: capture existing errors/warnings before building
            // so the LLM knows what was already broken.
            JsonNode? preBuildDiagnostics = null;
            if (string.Equals(toolName, "build", StringComparison.Ordinal))
            {
                try
                {
                    var preErrors = await SendBridgeAsync(id, bridgeBinding, "errors", "--quick").ConfigureAwait(false);
                    if (ResponseFormatter.IsSuccess(preErrors))
                    {
                        var errorCount = preErrors["Data"]?["errorCount"]?.GetValue<int>() ?? 0;
                        var warningCount = preErrors["Data"]?["warningCount"]?.GetValue<int>() ?? 0;
                        var messageCount = preErrors["Data"]?["messageCount"]?.GetValue<int>() ?? 0;
                        if (errorCount > 0 || warningCount > 0 || messageCount > 0)
                        {
                            preBuildDiagnostics = preErrors;
                        }
                    }
                }
                catch
                {
                    // Non-fatal: proceed with build even if pre-check fails.
                }
            }

            var response = await SendBridgeAsync(id, bridgeBinding, command, commandArgs).ConfigureAwait(false);

            if (preBuildDiagnostics is not null)
            {
                response["preBuildDiagnostics"] = preBuildDiagnostics;
            }

            if (string.Equals(toolName, "apply_diff", StringComparison.Ordinal) && GetBoolean(args, "post_check", false))
            {
                var ready = await SendBridgeAsync(id, bridgeBinding, "ready", string.Empty).ConfigureAwait(false);
                var errors = await SendBridgeAsync(id, bridgeBinding, "errors", "--wait-for-intellisense true").ConfigureAwait(false);
                response["postCheck"] = new JsonObject
                {
                    ["ready"] = ready,
                    ["errors"] = errors,
                };
            }

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
                [StructuredContentPropertyName] = response.DeepClone(),
            };
        }

        private static JsonObject ToolHelp(JsonNode? id, string? toolName)
        {
            var tools = ListTools()
                .OfType<JsonObject>()
                .ToArray();

            if (!string.IsNullOrWhiteSpace(toolName))
            {
                var match = tools.FirstOrDefault(tool =>
                    string.Equals(tool["name"]?.GetValue<string>(), toolName, StringComparison.Ordinal))
                    ?? throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName));

                var item = BuildToolHelpEntry(match);
                var result = new JsonObject
                {
                    ["count"] = 1,
                    ["items"] = new JsonArray { item },
                };
                return WrapToolResult(result, isError: false);
            }

            var entries = new JsonArray();
            foreach (var tool in tools.OrderBy(item => item["name"]?.GetValue<string>(), StringComparer.Ordinal))
            {
                entries.Add(BuildToolHelpEntry(tool));
            }

            var catalog = new JsonObject
            {
                ["count"] = entries.Count,
                ["items"] = entries,
            };

            return WrapToolResult(catalog, isError: false);
        }

        private static JsonObject BuildToolHelpEntry(JsonObject tool)
        {
            var name = tool["name"]?.GetValue<string>() ?? string.Empty;
            var inputSchema = tool["inputSchema"] as JsonObject ?? EmptySchema();
            var bridgeCommand = ResolveBridgeCommandForTool(name);
            BridgeCommandMetadata? bridgeMetadata = null;
            if (!string.IsNullOrWhiteSpace(bridgeCommand))
            {
                if (BridgeCommandCatalog.TryGetByPipeName(bridgeCommand, out var commandMetadata))
                {
                    bridgeMetadata = commandMetadata;
                }
            }

            var hasBridgeMetadata = bridgeMetadata is not null;
            var description = hasBridgeMetadata
                ? bridgeMetadata!.Description
                : tool["description"]?.GetValue<string>() ?? string.Empty;

            string? bridgeCommandValue = hasBridgeMetadata ? bridgeMetadata!.PipeName : null;
            string? bridgeExampleValue = hasBridgeMetadata ? bridgeMetadata!.Example : null;
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = inputSchema.DeepClone(),
                ["example"] = GetToolExample(name, inputSchema),
                ["bridgeCommand"] = bridgeCommandValue,
                ["bridgeExample"] = bridgeExampleValue,
            };
        }

        private static JsonObject WrapToolResult(JsonObject payload, bool isError)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = payload.ToJsonString(JsonOptions),
                    },
                },
                ["isError"] = isError,
                [StructuredContentPropertyName] = payload.DeepClone(),
            };
        }

        private static string GetToolExample(string name, JsonObject inputSchema)
        {
            var overrideExample = name switch
            {
                "bind_solution" => "{ \"solution\": \"VsIdeBridge.sln\" }",
                "help" => "{ \"name\": \"open_file\" }",
                "tool_help" => "{ \"name\": \"open_file\" }",
                "open_solution" => "{ \"solution\": \"C:\\\\repo\\\\VsIdeBridge.sln\", \"wait_for_ready\": true }",
                "open_file" => "{ \"file\": \"src\\\\VsIdeBridgeCli\\\\Program.cs\", \"line\": 1 }",
                "find_files" => "{ \"query\": \"CMakeLists.txt\", \"include_non_project\": true }",
                "errors" => "{ \"wait_for_intellisense\": true, \"quick\": false }",
                "warnings" => "{ \"wait_for_intellisense\": true, \"quick\": false }",
                "apply_diff" => "{ \"patch\": \"*** Begin Patch\\n*** Update File: src/Example.cs\\n@@\\n-oldValue\\n+newValue\\n*** End Patch\\n\", \"post_check\": true }",
                "debug_watch" => "{ \"expression\": \"count\", \"timeout_ms\": 1000 }",
                "set_build_configuration" => "{ \"configuration\": \"Debug\", \"platform\": \"x64\" }",
                "count_references" => "{ \"file\": \"src\\\\foo.cpp\", \"line\": 42, \"column\": 13 }",
                "nuget_restore" => "{ \"path\": \"VsIdeBridge.sln\" }",
                "nuget_add_package" => "{ \"project\": \"src\\\\VsIdeBridgeCli\\\\VsIdeBridgeCli.csproj\", \"package\": \"Newtonsoft.Json\", \"version\": \"13.0.3\" }",
                "nuget_remove_package" => "{ \"project\": \"src\\\\VsIdeBridgeCli\\\\VsIdeBridgeCli.csproj\", \"package\": \"Newtonsoft.Json\" }",
                "conda_install" => "{ \"packages\": [\"cmake\", \"ninja\"], \"name\": \"superslicer\", \"yes\": true }",
                "conda_remove" => "{ \"packages\": [\"ninja\"], \"name\": \"superslicer\", \"yes\": true }",
                _ => string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(overrideExample))
            {
                return overrideExample;
            }

            var example = new JsonObject();
            var requiredNames = inputSchema["required"] as JsonArray ?? [];
            var properties = inputSchema["properties"] as JsonObject ?? [];
            foreach (var required in requiredNames.OfType<JsonNode>())
            {
                var nameToken = required.GetValue<string>();
                var propertySchema = properties[nameToken] as JsonObject;
                var type = propertySchema?["type"]?.GetValue<string>() ?? "string";
                example[nameToken] = type switch
                {
                    "integer" => 1,
                    "boolean" => true,
                    "array" => new JsonArray("value"),
                    _ => "value",
                };
            }

            return example.ToJsonString(JsonOptions);
        }

        private static string? ResolveBridgeCommandForTool(string toolName)
        {
            return toolName switch
            {
                "help" => "help",
                "state" => "state",
                "ready" => "ready",
                "errors" => "errors",
                "warnings" => "warnings",
                "list_tabs" => "list-tabs",
                "open_file" => "open-document",
                "find_files" => "find-files",
                "search_symbols" => "search-symbols",
                "count_references" => "count-references",
                "quick_info" => "quick-info",
                "apply_diff" => "apply-diff",
                "debug_threads" => "debug-threads",
                "debug_stack" => "debug-stack",
                "debug_locals" => "debug-locals",
                "debug_modules" => "debug-modules",
                "debug_watch" => "debug-watch",
                "debug_exceptions" => "debug-exceptions",
                "diagnostics_snapshot" => "diagnostics-snapshot",
                "build_configurations" => "build-configurations",
                "set_build_configuration" => "set-build-configuration",
                "find_text" => "find-text",
                "read_file" => "document-slice",
                "find_references" => "find-references",
                "peek_definition" => "quick-info",
                "file_outline" => "file-outline",
                "build" => "build",
                "build_errors" => "build-errors",
                "open_solution" => "open-solution",
                "list_documents" => "list-documents",
                "activate_document" => "activate-document",
                "close_document" => "close-document",
                "save_document" => "save-document",
                "close_file" => "close-file",
                "close_others" => "close-others",
                "list_windows" => "list-windows",
                "activate_window" => "activate-window",
                "goto_definition" => "goto-definition",
                "goto_implementation" => "goto-implementation",
                "call_hierarchy" => "call-hierarchy",
                _ => null,
            };
        }

        private static async Task<JsonNode> BridgeHealthAsync(JsonNode? id, BridgeBinding bridgeBinding)
        {
            var sw = Stopwatch.StartNew();
            var state = await SendBridgeAsync(id, bridgeBinding, "state", string.Empty).ConfigureAwait(false);
            var ready = await SendBridgeAsync(id, bridgeBinding, "ready", string.Empty).ConfigureAwait(false);
            sw.Stop();

            var discovery = bridgeBinding.CurrentDiscovery;
            var stateData = state["Data"] as JsonObject;
            var watchdog = stateData?["watchdog"] as JsonObject;
            var isDegraded = GetBoolean(watchdog, "isDegraded", false);
            var readySuccess = ResponseFormatter.IsSuccess(ready);
            var stateSuccess = ResponseFormatter.IsSuccess(state);
            var result = new JsonObject
            {
                ["success"] = stateSuccess && readySuccess && !isDegraded,
                ["status"] = stateSuccess && readySuccess && !isDegraded ? "healthy" : "degraded",
                ["binding"] = discovery is null ? null : DiscoveryToJson(discovery),
                ["selector"] = SelectorToJson(bridgeBinding.CurrentSelector),
                ["roundTripMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                ["state"] = state,
                ["lastReady"] = ready,
                ["watchdog"] = watchdog?.DeepClone(),
            };

            return WrapToolResult(result, isError: false);
        }

        private static async Task<JsonNode> OpenSolutionAsync(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var solution = args?["solution"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(solution))
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, "open_solution requires a non-empty solution path.");
            }

            var waitForReady = GetBoolean(args, "wait_for_ready", true);
            // Clear solution hint before sending so instance lookup succeeds even when VS has a different solution open.
            bridgeBinding.PreferSolution(null);
            var open = await SendBridgeAsync(id, bridgeBinding, "open-solution", BuildArgs(("solution", solution))).ConfigureAwait(false);

            JsonObject? ready = null;
            JsonObject? state = null;
            if (ResponseFormatter.IsSuccess(open))
            {
                bridgeBinding.PreferSolution(solution);
                if (waitForReady)
                {
                    ready = await SendBridgeAsync(id, bridgeBinding, "ready", string.Empty).ConfigureAwait(false);
                }

                state = await SendBridgeAsync(id, bridgeBinding, "state", string.Empty).ConfigureAwait(false);
            }

            var result = new JsonObject
            {
                ["open"] = open,
                ["ready"] = ready,
                ["state"] = state,
            };

            return WrapToolResult(result, isError: !ResponseFormatter.IsSuccess(open));
        }

        private static async Task<JsonNode> ListInstancesAsync(BridgeBinding bridgeBinding)
        {
            var instances = await PipeDiscovery.ListAsync(verbose: false, bridgeBinding.DiscoveryMode).ConfigureAwait(false);
            var boundInstanceId = bridgeBinding.CurrentDiscovery?.InstanceId;
            var items = new JsonArray();
            foreach (var instance in instances.OrderByDescending(item => item.LastWriteTimeUtc))
            {
                var json = DiscoveryToJson(instance);
                json["is_bound"] = string.Equals(instance.InstanceId, boundInstanceId, StringComparison.OrdinalIgnoreCase);
                items.Add(json);
            }

            var result = new JsonObject
            {
                ["success"] = true,
                ["count"] = instances.Count,
                ["items"] = items,
            };

            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = result.ToJsonString(JsonOptions),
                    },
                },
                ["isError"] = false,
                [StructuredContentPropertyName] = result,
            };
        }

        private static JsonArray ListResources() =>
        [
            Resource("bridge://current-solution", "Current solution"),
            Resource("bridge://active-document", "Active document"),
            Resource("bridge://open-tabs", "Open tabs"),
            Resource("bridge://error-list-snapshot", "Error list snapshot"),
        ];

        private static JsonArray ListResourceTemplates() => [];

        private static JsonObject Resource(string uri, string name) => new()
        {
            ["uri"] = uri,
            ["name"] = name,
            ["mimeType"] = "application/json",
        };

        private static async Task<JsonNode> ReadResourceAsync(JsonNode? id, JsonObject? p, BridgeBinding bridgeBinding)
        {
            var uri = p?["uri"]?.GetValue<string>() ?? throw new McpRequestException(id, JsonRpcInvalidParamsCode, "resources/read missing uri.");
            JsonObject data = uri switch
            {
                "bridge://current-solution" => await SendBridgeAsync(id, bridgeBinding, "state", string.Empty).ConfigureAwait(false),
                "bridge://active-document" => await SendBridgeAsync(id, bridgeBinding, "state", string.Empty).ConfigureAwait(false),
                "bridge://open-tabs" => await SendBridgeAsync(id, bridgeBinding, "list-tabs", string.Empty).ConfigureAwait(false),
                "bridge://error-list-snapshot" => await SendBridgeAsync(id, bridgeBinding, "errors", "--quick --wait-for-intellisense false").ConfigureAwait(false),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"Unknown resource uri: {uri}"),
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

        private static JsonArray ListPrompts() =>
        [
            Prompt("help", "Show bridge and MCP usage guidance."),
            Prompt("fix_current_errors", "Gather errors and propose patch flow."),
            Prompt("open_solution_and_wait_ready", "Run ensure then ready flow."),
            Prompt("git_review_before_commit", "Review status, diff, and log before committing."),
            Prompt("git_sync_with_remote", "Fetch, inspect divergence, then pull or push safely."),
            Prompt("github_issue_triage", "Search open issues, inspect details, and close resolved items."),
        ];

        private static JsonObject Prompt(string name, string promptDescription) => new()
        {
            ["name"] = name,
            [DescriptionPropertyName] = promptDescription,
            ["arguments"] = new JsonArray(),
        };

        private static JsonObject GetPrompt(JsonNode? id, JsonObject? p)
        {
            var name = p?["name"]?.GetValue<string>() ?? throw new McpRequestException(id, JsonRpcInvalidParamsCode, "prompts/get missing name.");
            var text = name switch
            {
                "help" => "Key tools: bind_solution or bind_instance to connect, open_solution to load a .sln, state/ready/bridge_health for status. Navigation: find_files, find_text, open_file, search_symbols, count_references, quick_info, read_file, find_references, peek_definition, file_outline, goto_definition, goto_implementation, call_hierarchy. Editing: apply_diff (optionally post_check). Documents: list_documents, activate_document, close_document, save_document, close_file, close_others. Windows: list_windows, activate_window. Diagnostics: errors, warnings, diagnostics_snapshot, build (pre-build diagnostics auto-included), build_errors, build_configurations, set_build_configuration. Dependencies: nuget_restore, nuget_add_package, nuget_remove_package, conda_install, conda_remove. Debug: debug_threads, debug_stack, debug_locals, debug_modules, debug_watch, debug_exceptions. Use tool_help for per-tool schemas and examples.",
                "fix_current_errors" => "Bind to the right solution first, call errors to list problems. Use read_file or find_text to inspect code, quick_info and find_references for context, then apply_diff to fix.",
                "open_solution_and_wait_ready" => "Call open_solution with the absolute .sln path and wait_for_ready=true (default). Then call state or bridge_health.",
                "git_review_before_commit" => "Call git_status, git_diff_unstaged, git_diff_staged, git_log, then git_add and git_commit when ready.",
                "git_sync_with_remote" => "Call git_fetch, git_status, and git_log first. Then use git_pull when behind or git_push when ahead.",
                "github_issue_triage" => "Use github_issue_search with state=open, review candidates, then use github_issue_close with issue_number and optional comment.",
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"Unknown prompt: {name}"),
            };

            return new JsonObject
            {
                [DescriptionPropertyName] = $"Bridge prompt: {name}",
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

        private static Task<JsonObject> SendBridgeAsync(JsonNode? id, BridgeBinding bridgeBinding, string command, string args)
        {
            return bridgeBinding.SendAsync(id, command, args);
        }

        private static JsonObject DiscoveryToJson(PipeDiscovery discovery)
        {
            return new JsonObject
            {
                ["instanceId"] = discovery.InstanceId,
                ["pid"] = discovery.ProcessId,
                ["pipeName"] = discovery.PipeName,
                ["solutionPath"] = discovery.SolutionPath,
                ["solutionName"] = discovery.SolutionName,
                ["source"] = discovery.Source,
                ["startedAtUtc"] = discovery.StartedAtUtc,
                ["discoveryFile"] = discovery.DiscoveryFile,
                ["lastWriteTimeUtc"] = discovery.LastWriteTimeUtc.ToString("O"),
            };
        }

        private static JsonObject SelectorToJson(BridgeInstanceSelector selector)
        {
            return new JsonObject
            {
                ["instanceId"] = selector.InstanceId,
                ["pid"] = selector.ProcessId,
                ["pipeName"] = selector.PipeName,
                ["solutionHint"] = selector.SolutionHint,
            };
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

        private static string? GetOptionalArgumentText(JsonObject? args, string name)
        {
            return args?[name]?.ToString();
        }

        private static string? GetOptionalBooleanArgument(JsonObject? args, string name, bool defaultValue, bool emitFalse)
        {
            var value = GetBoolean(args, name, defaultValue);
            return value ? "true" : emitFalse ? "false" : null;
        }

        private static string BuildConfigurationPlatformArgs(JsonObject? args)
        {
            return BuildArgs(
                (ConfigurationArgumentName, GetOptionalStringArgument(args, ConfigurationArgumentName)),
                (PlatformArgumentName, GetOptionalStringArgument(args, PlatformArgumentName)));
        }

        private static string BuildOpenFileArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                (LineArgumentName, GetOptionalArgumentText(args, LineArgumentName)),
                (ColumnArgumentName, GetOptionalArgumentText(args, ColumnArgumentName)),
                .. BuildBooleanArgs(args, ("allow-disk-fallback", "allow_disk_fallback", true, true)),
            ]);
        }

        private static string BuildFindFilesArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (QueryArgumentName, GetOptionalStringArgument(args, QueryArgumentName)),
                (PathArgumentName, GetOptionalStringArgument(args, PathArgumentName)),
                ("extensions", GetCsv(args?["extensions"] as JsonArray)),
                ("max-results", GetOptionalArgumentText(args, "max_results")),
                .. BuildBooleanArgs(args, ("include-non-project", "include_non_project", true, true)),
            ]);
        }

        private static string BuildSearchSymbolsArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (QueryArgumentName, GetOptionalStringArgument(args, QueryArgumentName)),
                ("kind", GetOptionalStringArgument(args, "kind")),
                ("scope", GetOptionalStringArgument(args, "scope")),
                (ProjectArgumentName, GetOptionalStringArgument(args, ProjectArgumentName)),
                (PathArgumentName, GetOptionalStringArgument(args, PathArgumentName)),
                (MaxArgumentName, GetOptionalArgumentText(args, MaxArgumentName)),
                .. BuildBooleanArgs(args, ("match-case", "match_case", false, false)),
            ]);
        }

        private static string BuildCountReferencesArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                (LineArgumentName, GetOptionalArgumentText(args, LineArgumentName)),
                (ColumnArgumentName, GetOptionalArgumentText(args, ColumnArgumentName)),
                (TimeoutMillisecondsSwitchName, GetOptionalArgumentText(args, TimeoutMillisecondsArgumentName)),
                .. BuildBooleanArgs(args, ("activate-window", ActivateWindowArgumentName, true, true)),
            ]);
        }

        private static string BuildApplyDiffArgs(JsonObject? args)
        {
            return BuildArgs(
                ("patch-text-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(GetOptionalStringArgument(args, "patch") ?? string.Empty))),
                ("open-changed-files", "true"),
                ("save-changed-files", "true"));
        }

        private static string BuildSingleStringSwitchArg(JsonObject? args, string switchName, string argumentName)
        {
            return BuildArgs((switchName, GetOptionalStringArgument(args, argumentName)));
        }

        private static string BuildCloseDocumentArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (QueryArgumentName, GetOptionalStringArgument(args, QueryArgumentName)),
                .. BuildBooleanArgs(args,
                    ("all", "all", false, false),
                    ("save", "save", false, false)),
            ]);
        }

        private static string BuildSaveDocumentArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                .. BuildBooleanArgs(args, ("all", "all", false, false)),
            ]);
        }

        private static string BuildCloseFileArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                (QueryArgumentName, GetOptionalStringArgument(args, QueryArgumentName)),
                .. BuildBooleanArgs(args, ("save", "save", false, false)),
            ]);
        }

        private static string BuildSaveOnlyArgs(JsonObject? args)
        {
            return BuildArgs([.. BuildBooleanArgs(args, ("save", "save", false, false))]);
        }

        private static string BuildDiagnosticsArgs(JsonObject? args)
        {
            var quick = GetBoolean(args, QuickArgumentName, false);
            return BuildArgs(
            [
                (SeverityArgumentName, GetOptionalStringArgument(args, SeverityArgumentName)),
                (MaxArgumentName, GetOptionalArgumentText(args, MaxArgumentName)),
                (CodeArgumentName, GetOptionalStringArgument(args, CodeArgumentName)),
                (ProjectArgumentName, GetOptionalStringArgument(args, ProjectArgumentName)),
                (PathArgumentName, GetOptionalStringArgument(args, PathArgumentName)),
                (TextArgumentName, GetOptionalStringArgument(args, TextArgumentName)),
                ("group-by", GetOptionalStringArgument(args, GroupByArgumentName)),
                (TimeoutMillisecondsSwitchName, GetOptionalArgumentText(args, TimeoutMillisecondsArgumentName)),
                .. BuildBooleanArgs(args,
                    ("wait-for-intellisense", WaitForIntellisenseArgumentName, !quick, true),
                    (QuickArgumentName, QuickArgumentName, false, true)),
            ]);
        }

        private static string BuildFileLineColumnArgs(JsonObject? args)
        {
            return BuildArgs(
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                (LineArgumentName, GetOptionalArgumentText(args, LineArgumentName)),
                (ColumnArgumentName, GetOptionalArgumentText(args, ColumnArgumentName)));
        }

        private static string BuildFindTextArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (QueryArgumentName, GetOptionalStringArgument(args, QueryArgumentName)),
                (PathArgumentName, GetOptionalStringArgument(args, PathArgumentName)),
                ("scope", GetOptionalStringArgument(args, "scope")),
                (ProjectArgumentName, GetOptionalStringArgument(args, ProjectArgumentName)),
                ("results-window", GetOptionalArgumentText(args, "results_window")),
                .. BuildBooleanArgs(args,
                    ("match-case", "match_case", false, false),
                    ("whole-word", "whole_word", false, false),
                    ("regex", "regex", false, false)),
            ]);
        }

        private static string BuildReadFileArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                ("start-line", GetOptionalArgumentText(args, "start_line")),
                ("end-line", GetOptionalArgumentText(args, "end_line")),
                (LineArgumentName, GetOptionalArgumentText(args, LineArgumentName)),
                ("context-before", GetOptionalArgumentText(args, "context_before")),
                ("context-after", GetOptionalArgumentText(args, "context_after")),
                .. BuildBooleanArgs(args, ("reveal-in-editor", "reveal_in_editor", true, true)),
            ]);
        }

        private static (string Name, string? Value)[] BuildBooleanArgs(JsonObject? args, params (string SwitchName, string ArgumentName, bool DefaultValue, bool EmitFalse)[] specs)
        {
            return [.. specs.Select(spec => (spec.SwitchName, GetOptionalBooleanArgument(args, spec.ArgumentName, spec.DefaultValue, spec.EmitFalse)))];
        }

        private static string? GetOptionalStringArgument(JsonObject? args, string name)
        {
            return args?[name]?.GetValue<string>();
        }

        private static bool GetBoolean(JsonObject? args, string name, bool defaultValue)
        {
            return args?[name]?.GetValue<bool?>() ?? defaultValue;
        }

        private static (string Name, JsonObject Schema, bool Required) OptionalBooleanProperty(string name, string description) => (name, BooleanSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) OptionalIntegerProperty(string name, string description) => (name, IntegerSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) OptionalStringArrayProperty(string name, string description) => (name, ArrayOfStringsSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) OptionalStringProperty(string name, string description) => (name, StringSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) RequiredIntegerProperty(string name, string description) => (name, IntegerSchema(description), true);

        private static (string Name, JsonObject Schema, bool Required) RequiredStringArrayProperty(string name, string description) => (name, ArrayOfStringsSchema(description), true);

        private static (string Name, JsonObject Schema, bool Required) RequiredStringProperty(string name, string description) => (name, StringSchema(description), true);

        private static JsonObject FileLineColumnSchema()
        {
            return ObjectSchema(
                RequiredStringProperty(FileArgumentName, AbsoluteOrSolutionRelativeFilePathDescription),
                RequiredIntegerProperty(LineArgumentName, "1-based line number."),
                RequiredIntegerProperty(ColumnArgumentName, "1-based column number."));
        }

        private static string? GetCsv(JsonArray? values)
        {
            if (values is null || values.Count == 0)
            {
                return null;
            }

            var items = values
                .OfType<JsonNode>()
                .Select(item => item.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (items.Length == 0)
            {
                return null;
            }

            return string.Join(",", items);
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
            [DescriptionPropertyName] = description,
        };

        private static JsonObject IntegerSchema(string description) => new()
        {
            ["type"] = "integer",
            [DescriptionPropertyName] = description,
        };

        private static JsonObject BooleanSchema(string description) => new()
        {
            ["type"] = "boolean",
            [DescriptionPropertyName] = description,
        };

        private static JsonObject ArrayOfStringsSchema(string description) => new()
        {
            ["type"] = "array",
            [DescriptionPropertyName] = description,
            ["items"] = new JsonObject
            {
                ["type"] = "string",
            },
        };

        private static async Task<JsonNode> CallGitToolAsync(JsonNode? id, string toolName, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var workingDirectory = await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);

            var gitArgs = toolName switch
            {
                "git_status" => "status --porcelain=v1 --branch",
                "git_current_branch" => "branch --show-current",
                "git_remote_list" => "remote --verbose",
                "git_tag_list" => "tag --list --sort=version:refname",
                "git_stash_list" => "stash list",
                "git_diff_unstaged" => $"diff --no-color --unified={GetIntOrDefault(args, "context", DefaultGitDiffContextLines)}",
                "git_diff_staged" => $"diff --cached --no-color --unified={GetIntOrDefault(args, "context", DefaultGitDiffContextLines)}",
                "git_log" => $"log --max-count={GetIntOrDefault(args, "max_count", DefaultGitLogMaxCount)} --date=iso-strict --pretty=format:%H%x09%ad%x09%an%x09%s",
                "git_show" => $"show --no-color {QuoteForGit(GetRequiredString(args, id, "revision"))}",
                "git_branch_list" => "branch --all --verbose --no-abbrev",
                "git_checkout" => $"checkout {QuoteForGit(GetRequiredString(args, id, "target"))}",
                "git_create_branch" => BuildGitCreateBranchArgs(args, id),
                "git_add" => $"add -- {JoinGitPaths(GetRequiredPaths(args, id, "paths"))}",
                "git_restore" => $"restore --source=HEAD --worktree -- {JoinGitPaths(GetRequiredPaths(args, id, "paths"))}",
                "git_commit" => $"commit -m {QuoteForGit(GetRequiredString(args, id, "message"))}",
                "git_commit_amend" => BuildGitCommitAmendArgs(args),
                "git_reset" => BuildGitResetArgs(args),
                "git_fetch" => BuildGitFetchArgs(args),
                "git_stash_push" => BuildGitStashPushArgs(args),
                "git_stash_pop" => "stash pop",
                "git_pull" => BuildGitPullPushArgs("pull", args),
                "git_push" => BuildGitPullPushArgs("push", args),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName)),
            };

            var timeoutMs = toolName is "git_push" or "git_pull" or "git_fetch" or "git_clone" ? 120_000 : 30_000;
            var gitResult = await RunGitAsync(workingDirectory, gitArgs, timeoutMs).ConfigureAwait(false);
            return WrapToolResult(gitResult, !(gitResult["success"]?.GetValue<bool>() ?? false));
        }

        private static async Task<JsonNode> CallNuGetToolAsync(JsonNode? id, string toolName, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var workingDirectory = await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);
            var nugetArgs = toolName switch
            {
                "nuget_restore" => BuildNuGetRestoreArgs(args),
                "nuget_add_package" => BuildNuGetAddPackageArgs(args, id),
                "nuget_remove_package" => BuildNuGetRemovePackageArgs(args, id),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName)),
            };

            var nugetResult = await RunProcessAsync(DotNetExecutableName, nugetArgs, workingDirectory).ConfigureAwait(false);
            return WrapToolResult(nugetResult, !(nugetResult["success"]?.GetValue<bool>() ?? false));
        }

        private static async Task<JsonNode> CallCondaToolAsync(JsonNode? id, string toolName, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var condaExecutable = ResolveCondaExecutable(id);
            var workingDirectory = await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);
            var condaArgs = toolName switch
            {
                "conda_install" => BuildCondaInstallArgs(args, id),
                "conda_remove" => BuildCondaRemoveArgs(args, id),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName)),
            };
            var condaResult = await RunProcessAsync(condaExecutable, condaArgs, workingDirectory).ConfigureAwait(false);

            return WrapToolResult(condaResult, !(condaResult["success"]?.GetValue<bool>() ?? false));
        }

        private static string BuildNuGetRestoreArgs(JsonObject? args)
        {
            var path = args?["path"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(path)
                ? "restore"
                : $"restore {QuoteForProcess(path)}";
        }

        private static string BuildNuGetAddPackageArgs(JsonObject? args, JsonNode? id)
        {
            var project = GetRequiredString(args, id, "project");
            var package = GetRequiredString(args, id, "package");
            var version = args?["version"]?.GetValue<string>();
            var source = args?["source"]?.GetValue<string>();
            var prerelease = GetBoolean(args, "prerelease", false);
            var noRestore = GetBoolean(args, "no_restore", false);

            var segments = new List<string>
            {
                "add",
                QuoteForProcess(project),
                "package",
                QuoteForProcess(package),
            };

            if (!string.IsNullOrWhiteSpace(version))
            {
                segments.Add("--version");
                segments.Add(QuoteForProcess(version));
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                segments.Add("--source");
                segments.Add(QuoteForProcess(source));
            }

            if (prerelease)
            {
                segments.Add("--prerelease");
            }

            if (noRestore)
            {
                segments.Add("--no-restore");
            }

            return string.Join(" ", segments);
        }

        private static string BuildNuGetRemovePackageArgs(JsonObject? args, JsonNode? id)
        {
            var project = GetRequiredString(args, id, "project");
            var package = GetRequiredString(args, id, "package");

            return $"remove {QuoteForProcess(project)} package {QuoteForProcess(package)}";
        }

        private static string BuildCondaInstallArgs(JsonObject? args, JsonNode? id)
        {
            var packages = GetRequiredStringArray(args, id, "packages");
            var channels = GetOptionalStringArray(args, "channels");
            var environmentName = args?["name"]?.GetValue<string>();
            var environmentPrefix = args?["prefix"]?.GetValue<string>();
            var dryRun = GetBoolean(args, "dry_run", false);
            var autoYes = GetBoolean(args, "yes", true);

            var segments = new List<string> { "install" };
            AppendCondaEnvironmentSelector(segments, environmentName, environmentPrefix, id, "conda_install");

            foreach (var channel in channels)
            {
                segments.Add("--channel");
                segments.Add(QuoteForProcess(channel));
            }

            foreach (var package in packages)
            {
                segments.Add(QuoteForProcess(package));
            }

            if (autoYes)
            {
                segments.Add("--yes");
            }

            if (dryRun)
            {
                segments.Add("--dry-run");
            }

            return string.Join(" ", segments);
        }

        private static string BuildCondaRemoveArgs(JsonObject? args, JsonNode? id)
        {
            var packages = GetRequiredStringArray(args, id, "packages");
            var environmentName = args?["name"]?.GetValue<string>();
            var environmentPrefix = args?["prefix"]?.GetValue<string>();
            var dryRun = GetBoolean(args, "dry_run", false);
            var autoYes = GetBoolean(args, "yes", true);

            var segments = new List<string> { "remove" };
            AppendCondaEnvironmentSelector(segments, environmentName, environmentPrefix, id, "conda_remove");

            foreach (var package in packages)
            {
                segments.Add(QuoteForProcess(package));
            }

            if (autoYes)
            {
                segments.Add("--yes");
            }

            if (dryRun)
            {
                segments.Add("--dry-run");
            }

            return string.Join(" ", segments);
        }

        private static void AppendCondaEnvironmentSelector(
            List<string> segments,
            string? environmentName,
            string? environmentPrefix,
            JsonNode? id,
            string toolName)
        {
            if (!string.IsNullOrWhiteSpace(environmentName) && !string.IsNullOrWhiteSpace(environmentPrefix))
            {
                throw new McpRequestException(id, -32602, $"{toolName} accepts either name or prefix, not both.");
            }

            if (!string.IsNullOrWhiteSpace(environmentName))
            {
                segments.Add("--name");
                segments.Add(QuoteForProcess(environmentName));
            }

            if (!string.IsNullOrWhiteSpace(environmentPrefix))
            {
                segments.Add("--prefix");
                segments.Add(QuoteForProcess(environmentPrefix));
            }
        }

        private static string ResolveCondaExecutable(JsonNode? id)
        {
            var condaFromEnvironment = Environment.GetEnvironmentVariable("CONDA_EXE");
            if (!string.IsNullOrWhiteSpace(condaFromEnvironment))
            {
                if (File.Exists(condaFromEnvironment))
                {
                    return condaFromEnvironment;
                }

                throw new McpRequestException(id, -32007, $"CONDA_EXE points to '{condaFromEnvironment}', but the file does not exist.");
            }

            var condaFromPath = ResolveCondaFromPath();
            if (!string.IsNullOrWhiteSpace(condaFromPath))
            {
                return condaFromPath;
            }

            var condaFromKnownLocations = ResolveCondaFromKnownLocations();
            if (!string.IsNullOrWhiteSpace(condaFromKnownLocations))
            {
                return condaFromKnownLocations;
            }

            throw new McpRequestException(id, -32007, "Conda executable not found. Install Miniconda/Anaconda or set CONDA_EXE.");
        }

        private static string? ResolveCondaFromPath()
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return null;
            }

            var directories = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directory in directories)
            {
                foreach (var extension in CondaExecutableExtensions)
                {
                    var candidate = Path.Combine(directory, $"{CondaExecutableName}{extension}");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static string? ResolveCondaFromKnownLocations()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                roots.Add(userProfile);
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                roots.Add(localAppData);
            }

            foreach (var root in roots)
            {
                foreach (var relativePath in CondaRelativeCandidatePaths)
                {
                    var candidate = Path.Combine(root, relativePath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }


        private static string BuildGitCommitAmendArgs(JsonObject? args)
        {
            var message = args?["message"]?.GetValue<string>();
            var noEdit = args?["no_edit"]?.GetValue<bool>() == true;
            if (!string.IsNullOrWhiteSpace(message))
            {
                return $"commit --amend -m {QuoteForGit(message)}";
            }

            return noEdit ? "commit --amend --no-edit" : "commit --amend";
        }

        private static string BuildGitFetchArgs(JsonObject? args)
        {
            var remote = args?["remote"]?.GetValue<string>();
            var fetchAll = args?["all"]?.GetValue<bool>() == true;
            var prune = args?["prune"]?.GetValue<bool>();

            var segments = new List<string> { "fetch" };
            if (fetchAll)
            {
                segments.Add("--all");
            }

            if (prune != false)
            {
                segments.Add("--prune");
            }

            if (!string.IsNullOrWhiteSpace(remote))
            {
                segments.Add(QuoteForGit(remote));
            }

            return string.Join(" ", segments);
        }

        private static string BuildGitStashPushArgs(JsonObject? args)
        {
            var includeUntracked = args?["include_untracked"]?.GetValue<bool>() == true;
            var message = args?["message"]?.GetValue<string>();

            var segments = new List<string> { "stash", "push" };
            if (includeUntracked)
            {
                segments.Add("--include-untracked");
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                segments.Add("-m");
                segments.Add(QuoteForGit(message));
            }

            return string.Join(" ", segments);
        }
        private static string BuildGitResetArgs(JsonObject? args)
        {
            var paths = GetOptionalPaths(args, "paths");
            return paths.Count == 0
                ? "reset"
                : $"reset -- {JoinGitPaths(paths)}";
        }

        private static string BuildGitPullPushArgs(string verb, JsonObject? args)
        {
            var remote = args?["remote"]?.GetValue<string>();
            var branch = args?["branch"]?.GetValue<string>();
            var setUpstream = args?["set_upstream"]?.GetValue<bool>() == true;

            var segments = new List<string> { verb };
            if (setUpstream && string.Equals(verb, "push", StringComparison.Ordinal))
            {
                segments.Add("--set-upstream");
            }

            if (!string.IsNullOrWhiteSpace(remote))
            {
                segments.Add(QuoteForGit(remote));
            }

            if (!string.IsNullOrWhiteSpace(branch))
            {
                segments.Add(QuoteForGit(branch));
            }

            return string.Join(" ", segments);
        }

        private static string BuildGitCreateBranchArgs(JsonObject? args, JsonNode? id)
        {
            var name = GetRequiredString(args, id, "name");
            var startPoint = args?["start_point"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(startPoint)
                ? $"checkout -b {QuoteForGit(name)}"
                : $"checkout -b {QuoteForGit(name)} {QuoteForGit(startPoint)}";
        }

        private static async Task<string> ResolveSolutionWorkingDirectoryAsync(JsonNode? id, BridgeBinding bridgeBinding)
        {
            var solutionPath = bridgeBinding.CurrentSolutionPath;
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                solutionPath = bridgeBinding.CurrentDiscovery?.SolutionPath ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                var state = await SendBridgeAsync(id, bridgeBinding, "state", string.Empty).ConfigureAwait(false);
                var data = state["Data"] as JsonObject;
                solutionPath = data?["solutionPath"]?.GetValue<string>() ?? string.Empty;
            }

            var directory = Path.GetDirectoryName(solutionPath);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new McpRequestException(id, -32004, "Could not determine solution directory for local package/git operations. Ensure a solution is open.");
            }

            return directory;
        }

        private static async Task<JsonObject> RunGitAsync(string workingDirectory, string arguments, int timeoutMs = 30_000)
        {
            var gitExecutable = ResolveGitExecutable();
            var startInfo = new ProcessStartInfo
            {
                FileName = gitExecutable,
                Arguments = BuildGitArguments(workingDirectory, arguments),
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_NO_PAGER"] = "1";
            return await RunProcessAsync(startInfo, timeoutMs).ConfigureAwait(false);
        }

        private static async Task<JsonObject> RunProcessAsync(ProcessStartInfo startInfo, int timeoutMs = 60_000)
        {
            return await ProcessRunner.RunJsonAsync(startInfo, timeoutMs).ConfigureAwait(false);
        }

        private static Task<JsonObject> RunProcessAsync(string command, string arguments, string workingDirectory, int timeoutMs = 60_000)
        {
            return ProcessRunner.RunJsonAsync(command, arguments, workingDirectory, timeoutMs);
        }

        private static string GetRequiredString(JsonObject? args, JsonNode? id, string name)
        {
            var value = args?[name]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"tools/call arguments missing required field '{name}'.");
            }

            return value;
        }

        private static int GetIntOrDefault(JsonObject? args, string name, int defaultValue)
        {
            return args?[name]?.GetValue<int?>() ?? defaultValue;
        }

        private static string? GetBuildErrorsTimeoutArgument(JsonNode? id, JsonObject? args)
        {
            var timeout = args?["timeout_ms"]?.GetValue<int?>();
            if (timeout is null)
            {
                return null;
            }

            if (timeout.Value < 5000)
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, "tools/call argument 'timeout_ms' must be at least 5000 for build_errors.");
            }

            return timeout.Value.ToString();
        }

        private static List<string> GetRequiredPaths(JsonObject? args, JsonNode? id, string name)
        {
            var paths = GetOptionalPaths(args, name);
            if (paths.Count == 0)
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"tools/call arguments missing required array '{name}'.");
            }

            return paths;
        }

        private static List<string> GetOptionalPaths(JsonObject? args, string name)
        {
            return GetOptionalStringArray(args, name);
        }

        private static List<string> GetRequiredStringArray(JsonObject? args, JsonNode? id, string name)
        {
            var values = GetOptionalStringArray(args, name);
            if (values.Count == 0)
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"tools/call arguments missing required array '{name}'.");
            }

            return values;
        }

        private static List<string> GetOptionalStringArray(JsonObject? args, string name)
        {
            return args?[name] is JsonArray array
                ? [.. array.OfType<JsonNode>().Select(node => node.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value))]
                : [];
        }

        private static string JoinGitPaths(IEnumerable<string> paths)
        {
            return string.Join(" ", paths.Select(QuoteForGit));
        }

        private static string BuildGitArguments(string workingDirectory, string arguments)
        {
            return $"--no-pager -c safe.directory={QuoteForGit(Path.GetFullPath(workingDirectory))} {arguments}";
        }

        private static string ResolveGitExecutable()
        {
            var gitFromPath = ResolveGitFromPath();
            if (!string.IsNullOrWhiteSpace(gitFromPath))
            {
                return gitFromPath;
            }

            var gitFromKnownLocations = ResolveGitFromKnownLocations();
            if (!string.IsNullOrWhiteSpace(gitFromKnownLocations))
            {
                return gitFromKnownLocations;
            }

            return GitExecutableName;
        }

        private static string? ResolveGitFromPath()
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return null;
            }

            var directories = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directory in directories)
            {
                foreach (var extension in GitExecutableExtensions)
                {
                    var candidate = Path.Combine(directory, $"{GitExecutableName}{extension}");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static string? ResolveGitFromKnownLocations()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            foreach (var root in roots.Where(static root => !string.IsNullOrWhiteSpace(root)))
            {
                foreach (var relativePath in GitRelativeCandidatePaths)
                {
                    var candidate = Path.Combine(root, relativePath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static string QuoteForGit(string input)
        {
            return QuoteForProcess(input);
        }

        private static string QuoteForProcess(string input)
        {
            var escaped = input.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        }


        private static async Task<JsonNode> CallGitHubToolAsync(JsonNode? id, string toolName, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var workingDirectory = await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);
            var repo = args?["repo"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(repo))
            {
                repo = await ResolveGitHubRepoFromOriginAsync(workingDirectory).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(repo))
            {
                throw new McpRequestException(id, -32005, "Could not determine GitHub repository. Pass -- repo as owner/repo or set origin remote.");
            }

            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new McpRequestException(id, -32006, "Missing GITHUB_TOKEN (or GH_TOKEN) for GitHub issue operations.");
            }

            var result = toolName switch
            {
                "github_issue_search" => await GitHubIssueSearchAsync(repo, args, token).ConfigureAwait(false),
                "github_issue_close" => await GitHubIssueCloseAsync(repo, args, token, id).ConfigureAwait(false),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName)),
            };

            return WrapToolResult(result, !(result["success"]?.GetValue<bool>() ?? false));
        }

        private static async Task<JsonObject> GitHubIssueSearchAsync(string repo, JsonObject? args, string token)
        {
            var query = args?["query"]?.GetValue<string>()?.Trim();
            var state = (args?["state"]?.GetValue<string>() ?? "open").Trim().ToLowerInvariant();
            if (state is not ("open" or "closed" or "all"))
            {
                state = "open";
            }

            var limit = Math.Clamp(GetIntOrDefault(args, "limit", DefaultGitHubIssueSearchLimit), 1, 100);
            var q = $"repo:{repo} is:issue";
            if (state != "all")
            {
                q += $" is:{state}";
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                q += $" {query}";
            }

            var uri = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(q)}&per_page={limit}";
            return await SendGitHubRequestAsync(HttpMethod.Get, uri, token).ConfigureAwait(false);
        }

        private static async Task<JsonObject> GitHubIssueCloseAsync(string repo, JsonObject? args, string token, JsonNode? id)
        {
            var issueNumber = args?["issue_number"]?.GetValue<int?>();
            if (issueNumber is null || issueNumber <= 0)
            {
                throw new McpRequestException(id, -32602, "github_issue_close requires a positive issue_number.");
            }

            var comment = args?["comment"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(comment))
            {
                var commentUri = $"https://api.github.com/repos/{repo}/issues/{issueNumber}/comments";
                _ = await SendGitHubRequestAsync(HttpMethod.Post, commentUri, token, new JsonObject { ["body"] = comment }).ConfigureAwait(false);
            }

            var closeUri = $"https://api.github.com/repos/{repo}/issues/{issueNumber}";
            return await SendGitHubRequestAsync(HttpMethod.Patch, closeUri, token, new JsonObject { ["state"] = "closed" }).ConfigureAwait(false);
        }

        private static async Task<string?> ResolveGitHubRepoFromOriginAsync(string workingDirectory)
        {
            var result = await RunGitAsync(workingDirectory, "remote get-url origin").ConfigureAwait(false);
            if (!(result["success"]?.GetValue<bool>() ?? false))
            {
                return null;
            }

            var url = (result["stdout"]?.GetValue<string>() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var normalized = url.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
            const string sshPrefix = "git@github.com:";
            const string httpsPrefix = "https://github.com/";
            if (normalized.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized[sshPrefix.Length..];
            }

            if (normalized.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized[httpsPrefix.Length..];
            }

            return null;
        }

        private static async Task<JsonObject> SendGitHubRequestAsync(HttpMethod method, string uri, string token, JsonObject? body = null)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "vs-ide-bridge-mcp");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonNode? json = null;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try { json = JsonNode.Parse(payload); } catch (JsonException ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }

            return new JsonObject
            {
                ["success"] = response.IsSuccessStatusCode,
                ["statusCode"] = (int)response.StatusCode,
                ["uri"] = uri,
                ["method"] = method.Method,
                ["data"] = json,
                ["raw"] = json is null ? payload : null,
            };
        }

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

        private static async Task<McpIncomingMessage?> ReadMessageAsync(Stream input)
        {
            var firstByte = await ReadNextNonWhitespaceByteAsync(input).ConfigureAwait(false);
            if (firstByte is null)
            {
                return null;
            }

            if (LooksLikeRawJson(firstByte.Value))
            {
                McpTrace($"ReadMessageAsync: detected raw JSON transport starting with 0x{firstByte.Value:X2}");
                var rawJson = await ReadRawJsonMessageAsync(input, firstByte.Value).ConfigureAwait(false);
                return new McpIncomingMessage
                {
                    Request = ParseJsonObject(rawJson),
                    WireFormat = McpWireFormat.RawJson,
                };
            }

            var header = await ReadHeaderAsync(input, firstByte.Value).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(header))
            {
                return null;
            }

            var lengthLine = header.Split('\n').FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                ?? throw new McpRequestException(null, JsonRpcInvalidRequestCode, "MCP request missing Content-Length header.");
            if (!int.TryParse(lengthLine.Split(':', 2)[1].Trim(), out var length) || length < 0)
            {
                throw new McpRequestException(null, JsonRpcInvalidRequestCode, "MCP request has invalid Content-Length.");
            }

            var payloadBytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await input.ReadAsync(payloadBytes.AsMemory(offset, length - offset)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new McpRequestException(null, JsonRpcInvalidRequestCode, "Unexpected EOF while reading MCP payload.");
                }

                offset += read;
            }

            var json = Encoding.UTF8.GetString(payloadBytes);
            return new McpIncomingMessage
            {
                Request = ParseJsonObject(json),
                WireFormat = McpWireFormat.HeaderFramed,
            };
        }

        private static JsonObject ParseJsonObject(string json)
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new McpRequestException(null, JsonRpcInvalidRequestCode, "MCP request must be a JSON object.");
        }

        private static async Task<byte?> ReadNextNonWhitespaceByteAsync(Stream input)
        {
            while (true)
            {
                var buffer = new byte[1];
                var read = await input.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                if (!char.IsWhiteSpace((char)buffer[0]))
                {
                    return buffer[0];
                }
            }
        }

        private static bool LooksLikeRawJson(byte firstByte)
        {
            return firstByte == (byte)'{' || firstByte == (byte)'[';
        }

        private static async Task<string> ReadRawJsonMessageAsync(Stream input, byte firstByte)
        {
            List<byte> bytes = [firstByte];
            var depth = RawJsonInitialDepth;
            var inString = false;
            var isEscaped = false;

            while (depth > 0)
            {
                var buffer = new byte[1];
                var read = await input.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new McpRequestException(null, JsonRpcInvalidRequestCode, "Unexpected EOF while reading raw JSON MCP payload.");
                }

                var current = buffer[0];
                bytes.Add(current);

                if (isEscaped)
                {
                    isEscaped = false;
                    continue;
                }

                if (current == (byte)'\\')
                {
                    if (inString)
                    {
                        isEscaped = true;
                    }

                    continue;
                }

                if (current == (byte)'"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (current == (byte)'{' || current == (byte)'[')
                {
                    depth++;
                }
                else if (current == (byte)'}' || current == (byte)']')
                {
                    depth--;
                }
            }

            return Encoding.UTF8.GetString([.. bytes]);
        }

        private static async Task<string> ReadHeaderAsync(Stream input, byte firstByte)
        {
            List<byte> bytes = [firstByte];
            var lastFour = new Queue<byte>(HeaderTerminatorLength);
            List<byte> firstBytes = []; // log first 64 bytes for diagnostics
            firstBytes.Add(firstByte);
            lastFour.Enqueue(firstByte);
            while (true)
            {
                if (lastFour.Count == HeaderTerminatorLength && lastFour.SequenceEqual(HeaderTerminator))
                {
                    McpTrace($"ReadHeaderAsync: got CRLF header after {bytes.Count} bytes");
                    return Encoding.ASCII.GetString([.. bytes]);
                }

                var arr = lastFour.ToArray();
                if (arr.Length >= 2 && arr[^1] == (byte)'\n' && arr[^2] == (byte)'\n'
                    && !(arr.Length >= HeaderTerminatorLength && arr[^HeaderTerminatorLength] == (byte)'\r'))
                {
                    McpTrace($"ReadHeaderAsync: got LF-only header after {bytes.Count} bytes");
                    return Encoding.ASCII.GetString([.. bytes]);
                }

                var b = new byte[1];
                var read = await input.ReadAsync(b).ConfigureAwait(false);
                if (read == 0)
                {
                    McpTrace($"ReadHeaderAsync: EOF after {bytes.Count} bytes. First bytes: {BitConverter.ToString([.. firstBytes])}");
                    return string.Empty;
                }

                bytes.Add(b[0]);
                if (firstBytes.Count < 64) firstBytes.Add(b[0]);
                lastFour.Enqueue(b[0]);
                if (lastFour.Count > HeaderTerminatorLength)
                {
                    lastFour.Dequeue();
                }
            }
        }

        private static async Task WriteMessageAsync(Stream output, JsonObject response, McpWireFormat wireFormat)
        {
            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
            if (wireFormat == McpWireFormat.RawJson)
            {
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.WriteAsync(RawJsonTerminator).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                return;
            }

            var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(bytes).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }

        private sealed class McpRequestException : Exception
        {
            public McpRequestException()
            {
            }

            public McpRequestException(string message)
                : base(message)
            {
            }

            public McpRequestException(string message, Exception innerException)
                : base(message, innerException)
            {
            }

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
