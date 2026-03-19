using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VsIdeBridgeCli.Git;
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

    private static partial class McpServer
    {
        private static readonly string McpLog = Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "mcp-server.log");
        private const string ActivateWindowArgumentName = "activate_window";
        private const string BasePathArgumentName = "base_path";
        private const string CodeArgumentName = "code";
        private const string ColumnArgumentName = "column";
        private const string ConfigurationArgumentName = "configuration";
        private const string ContextAfterArgumentName = "context_after";
        private const string ContextBeforeArgumentName = "context_before";
        private const int DefaultPythonToolTimeoutMilliseconds = ProcessRunner.DefaultTimeoutMilliseconds;
        private const string DefaultPythonToolTimeoutDescription = "Timeout in milliseconds (default 60000).";
        private const int DefaultContextLineCount = 3;
        private const string DefaultContextLineCountDescription = "Optional context line count.";
        private const int DefaultReadFileExampleContextAfter = 16;
        private const int DefaultGitDiffContextLines = DefaultContextLineCount;
        private const int DefaultGitHubIssueSearchLimit = 20;
        private const int DefaultGitLogMaxCount = 20;
        private const int DefaultLargeMaxCount = 200;
        private const int DefaultMaxQueriesPerChunk = 5;
        private const string CodeQualityRules =
            "Code rules: (1) Lines \u2264120 chars \u2014 never require horizontal scrolling. " +
            "(2) Read the file first \u2014 match existing naming conventions, patterns, and indentation exactly. " +
            "(3) Reuse existing constants and helpers \u2014 search before adding anything new. " +
            "(4) Extract repeated literals to named constants. " +
            "(5) Add comments only where the *why* is non-obvious \u2014 never restate what the code does. " +
            "(6) Prefer early returns and guard clauses over deep nesting.";
        private const string DotNetExecutableName = "dotnet";
        private const string DirectoryArgumentName = "directory";
        private const string FileArgumentName = "file";
        private const string GitExecutableName = "git";
        private const string GroupByArgumentName = "group_by";
        private const int JsonRpcInvalidRequestCode = -32600;
        private const int JsonRpcInvalidParamsCode = -32602;
        private const int BridgeErrorCode = -32001;
        private const string VisualStudioInstallDirName = "Microsoft Visual Studio";
        private const string DevenvExeFileName = "devenv.exe";
        private const string Vs2022Year = "2022";
        private const int HeaderTerminatorLength = 4;
        private const string LineArgumentName = "line";
        private const string MaxArgumentName = "max";
        private const string MatchCaseArgumentName = "match_case";
        private const string NameArgumentName = "name";
        private const string OptionalProjectFilterDescription = "Optional project filter.";
        private const string PathArgumentName = "path";
        private const string PackagesArgumentName = "packages";
        private const string PlatformArgumentName = "platform";
        private const string ProjectArgumentName = "project";
        private const string ProjectArgumentDescription = "Project name, unique name, or full path.";
        private const string SampleCliProjectName = "VsIdeBridgeCli";
        private const string SampleCliTestProjectName = "VsIdeBridgeCli.Tests";
        private const string SampleCliProgramPath = "src\\VsIdeBridgeCli\\Program.cs";
        private const string SampleCliProjectPath = "src\\VsIdeBridgeCli\\VsIdeBridgeCli.csproj";
        private const string QueryArgumentName = "query";
        private const string QuickArgumentName = "quick";
        private const string StartLineArgumentName = "start_line";
        private const string ReadOnlyHintPropertyName = "readOnlyHint";
        private const string TitlePropertyName = "title";
        private const string UiSettingsToolName = "ui_settings";
        private const int RawJsonInitialDepth = 1;
        private const string AnnotationsPropertyName = "annotations";
        private const string ApplyDiffToolName = "apply_diff";
        private const string WriteFileToolName = "write_file";
        private const string BridgeApprovalCommandName = "Tools.VsIdeBridgeRequestApproval";
        private const string PythonExecutionApprovalOperationName = "python_exec";
        private const string PythonEnvironmentMutationApprovalOperationName = "python_env_mutation";
        private const string PythonSetProjectEnvToolName = "python_set_project_env";
        private const string OptionalInterpreterPathDescription = "Optional interpreter path to use instead of the selected interpreter.";
        private const string CondaExecutableName = "conda";
        private const int DefaultReadFileBatchExampleEndLine = 17;
        private const string DescriptionPropertyName = "description";
        private const string DestructiveHintPropertyName = "destructiveHint";
        private const string EndLineArgumentName = "end_line";
        private const string IdempotentHintPropertyName = "idempotentHint";
        private const string InputSchemaPropertyName = "inputSchema";
        private const string OutputSchemaPropertyName = "outputSchema";
        private const string AbsoluteOrSolutionRelativeFilePathDescription = "Absolute or solution-relative file path.";
        private static readonly byte[] RawJsonTerminator = [(byte)'\n'];
        private const string ServiceControlPipeName = "VsIdeBridgeServiceControl";
        private const string HelpToolName = "help";
        private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
        private const string AddFileToProjectToolName = "add_file_to_project";
        private const string AddProjectToolName = "add_project";
        private const string CreateProjectToolName = "create_project";
        private const string CondaInstallToolName = "conda_install";
        private const string CondaRemoveToolName = "conda_remove";
        private const string CountReferencesToolName = "count_references";
        private const string DebugWatchToolName = "debug_watch";
        private const string FindFilesToolName = "find_files";
        private const string FindTextToolName = "find_text";
        private const string FindTextBatchToolName = "find_text_batch";
        private const string GitMergeToolName = "git_merge";
        private const string ListProjectsToolName = "list_projects";
        private const string QueryProjectConfigurationsToolName = "query_project_configurations";
        private const string QueryProjectItemsToolName = "query_project_items";
        private const string QueryProjectOutputsToolName = "query_project_outputs";
        private const string QueryProjectPropertiesToolName = "query_project_properties";
        private const string QueryProjectReferencesToolName = "query_project_references";
        private const string ReadFileToolName = "read_file";
        private const string ReadFileBatchToolName = "read_file_batch";
        private const string RemoveFileFromProjectToolName = "remove_file_from_project";
        private const string RemoveProjectToolName = "remove_project";
        private const string SolutionArgumentName = "solution";
        private const string StructuredContentPropertyName = "structuredContent";
        private static readonly string[] SupportedProtocolVersions = ["2025-03-26", "2024-11-05"];
        private const string SeverityArgumentName = "severity";
        private const int TwoValue = 2;
        private const string TextArgumentName = "text";
        private const string TimeoutMillisecondsArgumentName = "timeout_ms";
        private const string TimeoutMillisecondsSwitchName = "timeout-ms";
        private const string ToolHelpToolName = "tool_help";
        private const string OneBasedLineNumberDescription = "1-based line number.";
        private const string UnknownMethodName = "(null)";
        private const string UnknownMcpToolMessageFormat = "Unknown MCP tool: {toolName}. Tool names must not include the server name as a prefix. Call tool_help to list all available tools.";
        private const string WaitForReadyArgumentName = "wait_for_ready";
        private const string WaitForIntellisenseArgumentName = "wait_for_intellisense";
        private const string WarningsToolName = "warnings";
        private const int VsOpenDiscoveryTimeoutMilliseconds = 30000;
        private const int VsOpenDiscoveryPollMilliseconds = 250;
        private const int ShortOperationTimeoutMilliseconds = 5000;
        private const string BridgeStateToolName = "bridge_state";
        private const string BuildConfigurationsToolName = "build_configurations";
        private const string CallHierarchyToolName = "call_hierarchy";
        private const string ClearBreakpointsToolName = "clear_breakpoints";
        private const string CreateSolutionToolName = "create_solution";
        private const string DebugExceptionsToolName = "debug_exceptions";
        private const string DebugLocalsToolName = "debug_locals";
        private const string DebugModulesToolName = "debug_modules";
        private const string DebugStackToolName = "debug_stack";
        private const string DebugThreadsToolName = "debug_threads";
        private const string DiagnosticsSnapshotToolName = "diagnostics_snapshot";
        private const string FileOutlineToolName = "file_outline";
        private const string FindReferencesToolName = "find_references";
        private const string GitPullToolName = "git_pull";
        private const string GitPushToolName = "git_push";
        private const string GotoDefinitionToolName = "goto_definition";
        private const string GotoImplementationToolName = "goto_implementation";
        private const string ListBreakpointsToolName = "list_breakpoints";
        private const string ListDocumentsToolName = "list_documents";
        private const string ListTabsToolName = "list_tabs";
        private const string ListWindowsToolName = "list_windows";
        private const string NugetAddPackageToolName = "nuget_add_package";
        private const string NugetRemovePackageToolName = "nuget_remove_package";
        private const string OpenFileToolName = "open_file";
        private const string OpenSolutionToolName = "open_solution";
        private const string PeekDefinitionToolName = "peek_definition";
        private const string PythonCreateEnvToolName = "python_create_env";
        private const string PythonEnvInfoToolName = "python_env_info";
        private const string PythonInstallPackageToolName = "python_install_package";
        private const string PythonListEnvsToolName = "python_list_envs";
        private const string PythonListPackagesToolName = "python_list_packages";
        private const string PythonRemovePackageToolName = "python_remove_package";
        private const string SaveDocumentToolName = "save_document";
        private const string SearchSolutionsToolName = "search_solutions";
        private const string SearchSymbolsToolName = "search_symbols";
        private const string SetBuildConfigurationToolName = "set_build_configuration";
        private const string SetStartupProjectToolName = "set_startup_project";
        private const string ShellExecToolName = "shell_exec";
        private const string SymbolInfoToolName = "symbol_info";
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

            public Task<JsonObject> SendAsync(JsonNode? id, string command, string args)
            {
                return SendAsync(id, command, args, ignoreSolutionHint: false);
            }

            public Task<JsonObject> SendIgnoringSolutionHintAsync(JsonNode? id, string command, string args)
            {
                return SendAsync(id, command, args, ignoreSolutionHint: true);
            }

            private async Task<JsonObject> SendAsync(JsonNode? id, string command, string args, bool ignoreSolutionHint)
            {
                try
                {
                    var response = await SendCoreAsync(command, args, ignoreSolutionHint).ConfigureAwait(false);
                    RememberSolutionPath(TryGetSolutionPath(response));
                    return response;
                }
                catch (CliException ex)
                {
                    throw new McpRequestException(id, BridgeErrorCode, ex.Message);
                }
                catch (TimeoutException ex)
                {
                    throw new McpRequestException(id, -32002, $"Timed out waiting for Visual Studio bridge response: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    return await RetrySendAfterCommunicationFailureAsync(id, command, args, ex, accessDenied: true, ignoreSolutionHint).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    return await RetrySendAfterCommunicationFailureAsync(id, command, args, ex, accessDenied: false, ignoreSolutionHint).ConfigureAwait(false);
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
                    throw new McpRequestException(id, BridgeErrorCode, ex.Message);
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

            private async Task<JsonObject> SendCoreAsync(string command, string args, bool ignoreSolutionHint)
            {
                var discovery = ignoreSolutionHint
                    ? await GetDiscoveryIgnoringSolutionHintAsync().ConfigureAwait(false)
                    : await GetDiscoveryAsync().ConfigureAwait(false);
                await using var client = new PipeClient(discovery.PipeName, _options.GetInt32("timeout-ms", 130_000));
                var request = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("N")[..8],
                    ["command"] = command,
                    ["args"] = args,
                };

                return await client.SendAsync(request).ConfigureAwait(false);
            }

            private async Task<PipeDiscovery> GetDiscoveryIgnoringSolutionHintAsync()
            {
                BridgeInstanceSelector selectorSnapshot;
                PipeDiscovery? cachedDiscovery;
                lock (_stateGate)
                {
                    cachedDiscovery = _cachedDiscovery;
                    selectorSnapshot = CloneSelector(_selector);
                }

                if (cachedDiscovery is not null)
                {
                    return cachedDiscovery;
                }

                selectorSnapshot = new BridgeInstanceSelector
                {
                    InstanceId = selectorSnapshot.InstanceId,
                    ProcessId = selectorSnapshot.ProcessId,
                    PipeName = selectorSnapshot.PipeName,
                    SolutionHint = null,
                };

                var discovery = await PipeDiscovery
                    .SelectAsync(selectorSnapshot, _verbose, ResolveDiscoveryMode(_options))
                    .ConfigureAwait(false);

                McpTrace($"bound instance={discovery.InstanceId} pipe={discovery.PipeName} source={discovery.Source} solution={discovery.SolutionPath} without solution hint");
                return discovery;
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

            private async Task<JsonObject> RetrySendAfterCommunicationFailureAsync(JsonNode? id, string command, string args, Exception ex, bool accessDenied, bool ignoreSolutionHint)
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
                    var response = await SendCoreAsync(command, args, ignoreSolutionHint).ConfigureAwait(false);
                    RememberSolutionPath(TryGetSolutionPath(response));
                    return response;
                }
                catch (CliException retryEx)
                {
                    throw new McpRequestException(id, BridgeErrorCode, retryEx.Message);
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
                    SolutionHint = GetString(args, SolutionArgumentName) ?? GetString(args, "solution_hint") ?? GetString(args, "sln"),
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
            if (options.GetFlag("http"))
            {
                await RunHttpAsync(options).ConfigureAwait(false);
                return;
            }

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

        private static async Task RunHttpAsync(CliOptions options)
        {
            var port = options.GetInt32("port", 5010);
            var advertiseExtraCapabilities = !options.GetFlag("tools-only");
            var sessions = new ConcurrentDictionary<string, BridgeBinding>(StringComparer.Ordinal);

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/mcp/");
            listener.Start();

            McpTrace($"HTTP MCP server listening on http://localhost:{port}/mcp/");
            Console.Error.WriteLine($"VS IDE Bridge MCP HTTP server: http://localhost:{port}/mcp/");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); listener.Stop(); };

            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cts.Token.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleHttpRequestAsync(ctx, options, sessions, advertiseExtraCapabilities));
            }
        }

        private static async Task HandleHttpRequestAsync(
            HttpListenerContext ctx,
            CliOptions options,
            ConcurrentDictionary<string, BridgeBinding> sessions,
            bool advertiseExtraCapabilities)
        {
            try
            {
                var req = ctx.Request;
                var httpResponse = ctx.Response;

                httpResponse.Headers["Access-Control-Allow-Origin"] = "*";
                httpResponse.Headers["Access-Control-Allow-Methods"] = "POST, DELETE, OPTIONS";
                httpResponse.Headers["Access-Control-Allow-Headers"] = "Content-Type, Mcp-Session-Id";

                if (req.HttpMethod == "OPTIONS")
                {
                    httpResponse.StatusCode = 204;
                    httpResponse.Close();
                    return;
                }

                var path = req.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

                if (req.HttpMethod == "DELETE" && path == "/mcp")
                {
                    var sessionId = req.Headers["Mcp-Session-Id"];
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        sessions.TryRemove(sessionId, out _);
                    }

                    httpResponse.StatusCode = 204;
                    httpResponse.Close();
                    return;
                }

                if (req.HttpMethod != "POST" || path != "/mcp")
                {
                    httpResponse.StatusCode = 404;
                    httpResponse.Close();
                    return;
                }

                string body;
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                var sid = req.Headers["Mcp-Session-Id"];
                if (string.IsNullOrEmpty(sid))
                {
                    sid = Guid.NewGuid().ToString("N");
                }

                var binding = sessions.GetOrAdd(sid, _ => new BridgeBinding(options));

                var request = ParseJsonObject(body);
                var method = request["method"]?.GetValue<string>() ?? UnknownMethodName;
                NotifyService("MCP_REQUEST");
                McpTrace($"HTTP got request method={method}");

                var trackInFlight = string.Equals(method, "tools/call", StringComparison.Ordinal);
                if (trackInFlight)
                {
                    NotifyService("COMMAND_START");
                }

                JsonObject? response;
                try
                {
                    response = await HandleRequestAsync(request, binding, advertiseExtraCapabilities).ConfigureAwait(false);
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
                finally
                {
                    if (trackInFlight)
                    {
                        NotifyService("COMMAND_END");
                    }
                }

                httpResponse.StatusCode = 200;
                httpResponse.ContentType = "application/json";
                httpResponse.Headers["Mcp-Session-Id"] = sid;

                if (response is not null)
                {
                    var responseBytes = Encoding.UTF8.GetBytes(response.ToJsonString());
                    httpResponse.ContentLength64 = responseBytes.Length;
                    await httpResponse.OutputStream.WriteAsync(responseBytes).ConfigureAwait(false);
                }

                httpResponse.Close();
            }
            catch (Exception ex)
            {
                McpTrace($"HTTP request handler error: {ex.Message}");
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch
                {
                    // Ignore errors when closing an already-failed response.
                }
            }
        }

        private static async Task ProcessRequestAsync(McpIncomingMessage incoming, Stream output, SemaphoreSlim outputGate, BridgeBinding bridgeBinding, bool advertiseExtraCapabilities)
        {
            JsonObject? response;
            var request = incoming.Request;
            var method = request["method"]?.GetValue<string>() ?? UnknownMethodName;
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
                    ["name"] = "vs_ide_bridge",
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

        private static JsonArray ListTools() => Registry.BuildToolsList();

        private static JsonObject Tool(
            string name,
            string description,
            JsonObject inputSchema,
            string? title = null,
            JsonObject? annotations = null,
            JsonObject? outputSchema = null)
        {
            var resolvedTitle = !string.IsNullOrWhiteSpace(title)
                ? title
                : BuildDefaultToolTitle(name);
            var resolvedAnnotations = annotations ?? InferStandardToolAnnotations(name);
            var tool = new JsonObject
            {
                ["name"] = name,
                [DescriptionPropertyName] = ResolveToolDescription(name, description),
                [InputSchemaPropertyName] = inputSchema,
            };

            if (!string.IsNullOrWhiteSpace(resolvedTitle))
            {
                tool[TitlePropertyName] = resolvedTitle;
            }

            if (resolvedAnnotations is not null && resolvedAnnotations.Count > 0)
            {
                tool[AnnotationsPropertyName] = resolvedAnnotations.DeepClone();
            }

            if (outputSchema is not null)
            {
                tool[OutputSchemaPropertyName] = outputSchema.DeepClone();
            }

            return tool;
        }

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

        private static Task<JsonNode> CallToolAsync(JsonNode? id, JsonObject? p, BridgeBinding bridgeBinding)
        {
            string toolName = GetOptionalStringArgument(p, "name")
                ?? throw new McpRequestException(id, JsonRpcInvalidParamsCode, "tools/call missing name.");
            JsonObject? args = p?["arguments"] as JsonObject;
            return Registry.DispatchAsync(id, toolName, args, bridgeBinding);
        }

        private static JsonObject ToolHelp(JsonNode? id, string? toolName, string? category = null)
        {
            IReadOnlyList<VsIdeBridge.Shared.ToolDefinition> tools = Registry.Definitions.All;

            if (!string.IsNullOrWhiteSpace(toolName))
            {
                VsIdeBridge.Shared.ToolDefinition? match = tools.FirstOrDefault(tool =>
                    string.Equals(tool.Name, toolName, StringComparison.Ordinal))
                    ?? throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName));

                var toolHelpEntry = BuildToolHelpEntry(match);
                JsonObject helpResult = new JsonObject
                {
                    ["count"] = 1,
                    ["items"] = new JsonArray { toolHelpEntry },
                };
                return WrapToolResult(helpResult, isError: false);
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                VsIdeBridge.Shared.ToolCategoryDefinition? categoryDefinition = Registry.Definitions.Categories
                    .FirstOrDefault(item => string.Equals(item.Name, category, StringComparison.OrdinalIgnoreCase));
                if (categoryDefinition is null)
                {
                    string validNames = string.Join(", ", Registry.Definitions.Categories.Select(item => item.Name));
                    throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"Unknown category '{category}'. Valid: {validNames}");
                }

                IReadOnlyList<VsIdeBridge.Shared.ToolDefinition> categoryTools =
                    Registry.Definitions.GetByCategory(categoryDefinition.Name);
                JsonArray categoryEntries = new JsonArray();
                foreach (VsIdeBridge.Shared.ToolDefinition tool in categoryTools)
                {
                    categoryEntries.Add(BuildToolHelpEntry(tool));
                }

                return WrapToolResult(new JsonObject
                {
                    ["category"] = categoryDefinition.Name,
                    ["summary"] = categoryDefinition.Summary,
                    [DescriptionPropertyName] = categoryDefinition.Description,
                    ["count"] = categoryEntries.Count,
                    ["items"] = categoryEntries,
                }, isError: false);
            }

            JsonArray index = new JsonArray();
            foreach (VsIdeBridge.Shared.ToolCategoryDefinition categoryDefinition in Registry.Definitions.Categories)
            {
                JsonArray toolList = new JsonArray();
                IReadOnlyList<VsIdeBridge.Shared.ToolDefinition> categoryTools =
                    Registry.Definitions.GetByCategory(categoryDefinition.Name);
                foreach (VsIdeBridge.Shared.ToolDefinition tool in categoryTools)
                {
                    toolList.Add(JsonValue.Create(tool.Name));
                }

                index.Add(new JsonObject
                {
                    ["category"] = categoryDefinition.Name,
                    ["summary"] = categoryDefinition.Summary,
                    [DescriptionPropertyName] = categoryDefinition.Description,
                    ["count"] = categoryTools.Count,
                    ["tools"] = toolList,
                });
            }

            return WrapToolResult(new JsonObject
            {
                ["categoryCount"] = Registry.Definitions.Categories.Count,
                ["totalTools"] = Registry.Definitions.All.Count,
                ["usage"] = "Pass category: 'core' (or search/diagnostics/documents/debug/git/python/project/system) to get full tool schemas for that group.",
                ["categories"] = index,
            }, isError: false);
        }

        private static JsonObject BuildToolHelpEntry(VsIdeBridge.Shared.ToolDefinition tool)
        {
            BridgeCommandMetadata? bridgeMetadata = null;
            if (!string.IsNullOrWhiteSpace(tool.BridgeCommand))
            {
                if (BridgeCommandCatalog.TryGetByPipeName(tool.BridgeCommand, out BridgeCommandMetadata? commandMetadata))
                {
                    bridgeMetadata = commandMetadata;
                }
            }

            return tool.BuildHelpEntry(
                GetToolExample(tool.Name, tool.ParameterSchema),
                bridgeMetadata?.Example);
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

        private static async Task<JsonObject> RequestBridgeApprovalAsync(
            JsonNode? id,
            BridgeBinding bridgeBinding,
            string operation,
            string? subject,
            string? details)
        {
            var approvalArgs = BuildArgs(
                ("operation", operation),
                ("subject", subject),
                ("details", details));
            return await SendBridgeAsync(id, bridgeBinding, BridgeApprovalCommandName, approvalArgs).ConfigureAwait(false);
        }

        private static void AttachApprovalMetadata(JsonObject payload, JsonObject approvalResponse)
        {
            if (approvalResponse["Data"] is not JsonObject approvalData)
            {
                return;
            }

            payload["approval"] = approvalData["approval"]?.DeepClone();
            payload["approvalChoice"] = approvalData["approvalChoice"]?.DeepClone();
            payload["approvalOperation"] = approvalData["operation"]?.DeepClone();
            payload["approvalPromptShown"] = approvalData["promptShown"]?.DeepClone();
            payload["approvalPersistentSettingEnabled"] = approvalData["persistentSettingEnabled"]?.DeepClone();
            payload["approvalResultCode"] = approvalData["resultCode"]?.DeepClone();
        }

        private static JsonObject BuildApprovalFailurePayload(JsonObject approvalResponse)
        {
            var payload = new JsonObject
            {
                ["success"] = false,
                ["message"] = approvalResponse["Summary"]?.GetValue<string>() ?? "Bridge approval was denied.",
                ["bridgeResponse"] = approvalResponse.DeepClone(),
            };

            if (approvalResponse["Error"] is JsonObject error)
            {
                payload["error"] = error.DeepClone();
                payload["errorCode"] = error["code"]?.DeepClone();
                payload["errorDetails"] = error["details"]?.DeepClone();
                if (error["details"] is JsonObject errorDetails)
                {
                    payload["approvalChoice"] = errorDetails["approvalChoice"]?.DeepClone();
                    payload["approvalOperation"] = errorDetails["operation"]?.DeepClone();
                    payload["approvalPromptShown"] = errorDetails["promptShown"]?.DeepClone();
                    payload["approvalPersistentSettingEnabled"] = errorDetails["persistentSettingEnabled"]?.DeepClone();
                    payload["approvalResultCode"] = errorDetails["resultCode"]?.DeepClone();
                }
            }

            if (approvalResponse["Data"] is JsonNode data)
            {
                payload["context"] = data.DeepClone();
            }

            return payload;
        }

        private static string BuildShellExecApprovalSubject(string executable, string arguments)
        {
            var commandText = string.IsNullOrWhiteSpace(arguments)
                ? executable
                : executable + " " + arguments;

            return "Run command: " + commandText;
        }

        private static string BuildShellExecApprovalDetails(string workingDirectory, int timeoutMs)
        {
            var detailBuilder = new StringBuilder();
            detailBuilder.Append("cwd=").Append(workingDirectory);
            detailBuilder.Append(", timeoutMs=").Append(timeoutMs);

            return detailBuilder.ToString();
        }

        private static string BuildSetVersionApprovalDetails(string version)
        {
            return $"Update Directory.Build.props, src/VsIdeBridge/source.extension.vsixmanifest, and installer/inno/vs-ide-bridge.iss to version {version}.";
        }

        private static JsonObject GetPythonEnvironmentFromInfo(JsonNode? id, JsonObject environmentInfo)
        {
            if (environmentInfo["env"] is JsonObject environment)
            {
                return environment;
            }

            throw new McpRequestException(id, BridgeErrorCode, "Python environment metadata was missing from the bridge response.");
        }

        private static string BuildPythonInterpreterDisplayName(JsonObject environment)
        {
            var path = environment["path"]?.GetValue<string>() ?? "selected interpreter";
            var kind = environment["kind"]?.GetValue<string>();
            var version = environment["version"]?.GetValue<string>();

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(kind))
            {
                parts.Add(kind!);
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                parts.Add(version!);
            }

            return parts.Count == 0
                ? path
                : path + " (" + string.Join(", ", parts) + ")";
        }

        private static string BuildPythonExecutionApprovalSubject(string action, JsonObject environment, bool allowUnrestrictedExecution)
        {
            return action + " (" + (allowUnrestrictedExecution ? "unrestricted" : "restricted") + "): " + BuildPythonInterpreterDisplayName(environment);
        }

        private static string BuildPythonExecutionApprovalDetails(JsonObject environment, string workingDirectory, int timeoutMs, bool allowUnrestrictedExecution, string? extraDetails = null)
        {
            var detailBuilder = new StringBuilder();
            detailBuilder.Append("executionMode=")
                .Append(allowUnrestrictedExecution ? "unrestricted" : "restricted");
            detailBuilder.Append(", cwd=").Append(workingDirectory);
            detailBuilder.Append(", timeoutMs=").Append(timeoutMs);
            detailBuilder.Append(", userOwnedEnvironment=")
                .Append(!string.Equals(environment["kind"]?.GetValue<string>(), "managed", StringComparison.OrdinalIgnoreCase));

            if (!allowUnrestrictedExecution)
            {
                detailBuilder.Append(", restrictions=file writes, deletes, process launch, network, temp files, and native imports blocked");
            }

            if (!string.IsNullOrWhiteSpace(extraDetails))
            {
                detailBuilder.Append(", ").Append(extraDetails);
            }

            return detailBuilder.ToString();
        }

        private static async Task<bool> IsBridgePythonUnrestrictedExecutionAllowedAsync(JsonNode? id, BridgeBinding bridgeBinding)
        {
            var response = await SendBridgeAsync(id, bridgeBinding, "ui-settings", string.Empty).ConfigureAwait(false);
            if (!ResponseFormatter.IsSuccess(response) || response["Data"] is not JsonObject data)
            {
                return false;
            }

            return data["allowBridgePythonUnrestrictedExecution"] is JsonValue value &&
                value.TryGetValue<bool>(out var enabled) &&
                enabled;
        }

        private static string BuildPythonEnvironmentMutationApprovalSubject(string action, JsonObject environment, string? targetDescription = null)
        {
            return string.IsNullOrWhiteSpace(targetDescription)
                ? action + ": " + BuildPythonInterpreterDisplayName(environment)
                : action + ": " + targetDescription + " using " + BuildPythonInterpreterDisplayName(environment);
        }

        private static string BuildPythonEnvironmentMutationApprovalDetails(JsonObject environment, string? workingDirectory, int timeoutMs, string? extraDetails = null)
        {
            var detailBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                detailBuilder.Append("cwd=").Append(workingDirectory);
                detailBuilder.Append(", ");
            }

            detailBuilder.Append("timeoutMs=").Append(timeoutMs);
            detailBuilder.Append(", userOwnedEnvironment=")
                .Append(!string.Equals(environment["kind"]?.GetValue<string>(), "managed", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(extraDetails))
            {
                detailBuilder.Append(", ").Append(extraDetails);
            }

            return detailBuilder.ToString();
        }

        private static string GetToolExample(string name, JsonObject inputSchema)
        {
            var overrideExample = name switch
            {
                "bind_solution" => new JsonObject
                {
                    [SolutionArgumentName] = "VsIdeBridge.sln",
                }.ToJsonString(JsonOptions),
                "help" => "{ \"name\": \"open_file\" }",
                "tool_help" => "{ \"name\": \"open_file\" }",
                OpenSolutionToolName => new JsonObject
                {
                    [SolutionArgumentName] = "C:\\Users\\name\\source\\repos\\PinballBot\\PinballBot.sln",
                    [WaitForReadyArgumentName] = true,
                }.ToJsonString(JsonOptions),
                "wait_for_instance" => new JsonObject
                {
                    [SolutionArgumentName] = "C:\\Users\\name\\source\\repos\\PinballBot\\PinballBot.sln",
                    [TimeoutMillisecondsArgumentName] = 30000,
                }.ToJsonString(JsonOptions),
                CreateSolutionToolName => "{ \"directory\": \"C:\\\\repo\\\\Scratch\", \"name\": \"ScratchApp\", \"wait_for_ready\": true }",
                FindTextToolName => "{ \"query\": \"Tool(\", \"path\": \"src\\\\VsIdeBridgeCli\", \"scope\": \"solution\" }",
                FindTextBatchToolName => $"{{ \"queries\": [\"{ReadFileToolName}\", \"{ReadFileBatchToolName}\", \"{FindTextBatchToolName}\"], \"path\": \"src\\\\VsIdeBridgeCli\", \"scope\": \"solution\", \"max_queries_per_chunk\": {DefaultMaxQueriesPerChunk} }}",
                PythonListEnvsToolName => "{}",
                PythonEnvInfoToolName => "{ \"path\": \"C:\\\\Python313\\\\python.exe\" }",
                "python_set_active_env" => "{ \"path\": \"C:\\\\Python313\\\\python.exe\" }",
                PythonSetProjectEnvToolName => "{ \"path\": \"C:\\\\Python313\\\\python.exe\" }",
                PythonListPackagesToolName => "{ \"path\": \"C:\\\\Python313\\\\python.exe\" }",
                "python_repl" => new JsonObject
                {
                    [CodeArgumentName] = "print(sum([10, 20]))",
                }.ToJsonString(JsonOptions),
                "python_run_file" => new JsonObject
                {
                    [FileArgumentName] = "scripts\\demo.py",
                    ["args"] = new JsonArray("--flag"),
                }.ToJsonString(JsonOptions),
                PythonInstallPackageToolName => new JsonObject
                {
                    [PackagesArgumentName] = new JsonArray("requests"),
                }.ToJsonString(JsonOptions),
                PythonRemovePackageToolName => new JsonObject
                {
                    [PackagesArgumentName] = new JsonArray("requests"),
                }.ToJsonString(JsonOptions),
                PythonCreateEnvToolName => new JsonObject
                {
                    [PathArgumentName] = ".venv",
                }.ToJsonString(JsonOptions),
                GitMergeToolName => "{ \"source\": \"origin/main\", \"ff_only\": true }",
                ListProjectsToolName => "{}",
                QueryProjectItemsToolName => ProjectToolExample(SampleCliProjectName, "\"path\": \"src\\\\VsIdeBridgeCli\"", $"\"max\": {DefaultLargeMaxCount}"),
                QueryProjectPropertiesToolName => ProjectToolExample(SampleCliProjectName, "\"names\": [\"TargetFramework\", \"AssemblyName\"]"),
                QueryProjectConfigurationsToolName => ProjectToolExample(SampleCliProjectName),
                QueryProjectReferencesToolName => ProjectToolExample(SampleCliTestProjectName, "\"declared_only\": true"),
                QueryProjectOutputsToolName => ProjectToolExample(SampleCliProjectName, "\"configuration\": \"Release\"", "\"target_framework\": \"net8.0\""),
                AddProjectToolName => ProjectToolExample("C:\\\\repo\\\\MyLib\\\\MyLib.csproj", "\"solution_folder\": \"Libraries\""),
                CreateProjectToolName => "{ \"name\": \"MyLib\", \"template\": \"classlib\", \"solution_folder\": \"Libraries\" }",
                RemoveProjectToolName => ProjectToolExample("MyLib"),
                SetStartupProjectToolName => ProjectToolExample("VsIdeBridge"),
                AddFileToProjectToolName => ProjectToolExample(SampleCliProjectName, "\"file\": \"src\\\\VsIdeBridgeCli\\\\Program.cs\""),
                RemoveFileFromProjectToolName => ProjectToolExample(SampleCliProjectName, "\"file\": \"src\\\\VsIdeBridgeCli\\\\Program.cs\""),
                OpenFileToolName => new JsonObject
                {
                    [FileArgumentName] = SampleCliProgramPath,
                    [LineArgumentName] = 1,
                }.ToJsonString(JsonOptions),
                ReadFileToolName => new JsonObject
                {
                    [FileArgumentName] = SampleCliProgramPath,
                    [LineArgumentName] = 1,
                    [ContextBeforeArgumentName] = 0,
                    [ContextAfterArgumentName] = DefaultReadFileExampleContextAfter,
                }.ToJsonString(JsonOptions),
                ReadFileBatchToolName => new JsonObject
                {
                    ["ranges"] = new JsonArray(
                        new JsonObject
                        {
                            [FileArgumentName] = SampleCliProgramPath,
                            [LineArgumentName] = 1,
                            [ContextBeforeArgumentName] = 0,
                            [ContextAfterArgumentName] = 12,
                        },
                        new JsonObject
                        {
                            [FileArgumentName] = SampleCliProjectPath,
                            [StartLineArgumentName] = 1,
                            [EndLineArgumentName] = DefaultReadFileBatchExampleEndLine,
                        }),
                }.ToJsonString(JsonOptions),
                "find_files" => "{ \"query\": \"CMakeLists.txt\", \"include_non_project\": true }",
                "errors" => "{ \"wait_for_intellisense\": true, \"quick\": false }",
                WarningsToolName => "{ \"wait_for_intellisense\": true, \"quick\": false }",
                "apply_diff" => "{ \"patch\": \"*** Begin Patch\\n*** Update File: src/Example.cs\\n@@\\n context line before\\n-old line to replace\\n+new replacement line\\n context line after\\n*** End Patch\\n\", \"post_check\": true }",
                "debug_watch" => "{ \"expression\": \"count\", \"timeout_ms\": 1000 }",
                SetBuildConfigurationToolName => "{ \"configuration\": \"Debug\", \"platform\": \"x64\" }",
                "count_references" => "{ \"file\": \"src\\\\foo.cpp\", \"line\": 42, \"column\": 13 }",
                "nuget_restore" => "{ \"path\": \"VsIdeBridge.sln\" }",
                "nuget_add_package" => ProjectToolExample(SampleCliProjectPath, "\"package\": \"Newtonsoft.Json\"", "\"version\": \"13.0.3\""),
                "nuget_remove_package" => ProjectToolExample(SampleCliProjectPath, "\"package\": \"Newtonsoft.Json\""),
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

        private static string ProjectToolExample(string project, params string[] additionalProperties)
        {
            var example = new JsonObject
            {
                [ProjectArgumentName] = project,
            };

            foreach (var additionalProperty in additionalProperties)
            {
                if (JsonNode.Parse($"{{{additionalProperty}}}") is not JsonObject additionalObject)
                {
                    continue;
                }

                foreach (var property in additionalObject)
                {
                    example[property.Key] = property.Value?.DeepClone();
                }
            }

            return example.ToJsonString(JsonOptions);
        }

        private static string? ResolveBridgeCommandForTool(string toolName)
        {
            if (Registry.TryGetDefinition(toolName, out VsIdeBridge.Shared.ToolDefinition? definition))
                return definition.BridgeCommand;

            return null;
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
            var healthResult = new JsonObject
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

            return WrapToolResult(healthResult, isError: false);
        }

        private static async Task<JsonNode> OpenSolutionAsync(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var solution = args?["solution"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(solution))
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, "open_solution requires a non-empty solution path.");
            }

            var waitForReady = GetBoolean(args, WaitForReadyArgumentName, true);
            // Clear solution hint before sending so instance lookup succeeds even when VS has a different solution open.
            var open = await SendBridgeIgnoringSolutionHintAsync(id, bridgeBinding, "open-solution", BuildArgs(("solution", solution))).ConfigureAwait(false);

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

            var openSolutionResult = new JsonObject
            {
                ["open"] = open,
                ["ready"] = ready,
                ["state"] = state,
            };

            return WrapToolResult(openSolutionResult, isError: !ResponseFormatter.IsSuccess(open));
        }

        private static async Task<JsonNode> CreateSolutionAsync(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var directory = args?[DirectoryArgumentName]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, "create_solution requires a non-empty directory path.");
            }

            var name = args?[NameArgumentName]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, "create_solution requires a non-empty solution name.");
            }

            var waitForReady = GetBoolean(args, WaitForReadyArgumentName, true);
            var create = await SendBridgeIgnoringSolutionHintAsync(
                id,
                bridgeBinding,
                "create-solution",
                BuildArgs((DirectoryArgumentName, directory), (NameArgumentName, name))).ConfigureAwait(false);

            JsonObject? ready = null;
            JsonObject? state = null;
            if (ResponseFormatter.IsSuccess(create))
            {
                var createdPath = create["Data"]?["solutionPath"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(createdPath))
                {
                    bridgeBinding.PreferSolution(createdPath);
                }

                if (waitForReady)
                {
                    ready = await SendBridgeAsync(id, bridgeBinding, "ready", string.Empty).ConfigureAwait(false);
                }

                state = await SendBridgeAsync(id, bridgeBinding, "state", string.Empty).ConfigureAwait(false);
            }

            var createSolutionResult = new JsonObject
            {
                ["create"] = create,
                ["ready"] = ready,
                ["state"] = state,
            };

            return WrapToolResult(createSolutionResult, isError: !ResponseFormatter.IsSuccess(create));
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

            var instanceListResult = new JsonObject
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
                        ["text"] = instanceListResult.ToJsonString(JsonOptions),
                    },
                },
                ["isError"] = false,
                [StructuredContentPropertyName] = instanceListResult,
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
                "help" =>
                    "BRIDGE TOOL CATEGORIES — call tool_help(category:'<name>') for full schemas.\n" +
                    "CORE (always load first): bridge_health, list_instances, bind_instance, bind_solution, read_file, apply_diff, find_text, file_outline, errors, build\n" +
                    "SEARCH: find_files, search_symbols, find_references, goto_definition, goto_implementation, call_hierarchy, symbol_info, peek_definition, count_references\n" +
                    "DIAGNOSTICS: build_errors, warnings, diagnostics_snapshot, build_configurations, set_build_configuration\n" +
                    "DOCUMENTS: open_file, close_file, close_document, list_documents, list_tabs, activate_document, save_document, open_solution, format_document\n" +
                    "DEBUG: set_breakpoint, clear_breakpoints, debug_stack, debug_locals, debug_watch, debug_threads, debug_exceptions\n" +
                    "GIT: git_status, git_add, git_commit, git_diff_staged, git_log, git_checkout, git_push, git_pull, git_stash_push\n" +
                    "PYTHON: python_repl, python_list_envs, python_set_project_env, python_install_package, nuget_add_package, conda_install\n" +
                    "PROJECT: list_projects, add_file_to_project, query_project_properties, query_project_references, set_startup_project\n" +
                    "SYSTEM: execute_command, shell_exec, vs_open, vs_close, ui_settings, wait_for_instance",
                "fix_current_errors" => "Bind to the right solution first, call errors to list problems. Use read_file or find_text to inspect code, symbol_info and find_references for context, then apply_diff to fix.",
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

        private static Task<JsonObject> SendBridgeIgnoringSolutionHintAsync(JsonNode? id, BridgeBinding bridgeBinding, string command, string args)
        {
            return bridgeBinding.SendIgnoringSolutionHintAsync(id, command, args);
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

        private static (string Name, string? Value) OptionalTextArg(string switchName, JsonObject? args, string argumentName)
        {
            return (switchName, GetOptionalArgumentText(args, argumentName));
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

        private static string BuildBuildArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (ConfigurationArgumentName, GetOptionalStringArgument(args, ConfigurationArgumentName)),
                (PlatformArgumentName, GetOptionalStringArgument(args, PlatformArgumentName)),
                .. BuildBooleanArgs(args,
                    ("wait-for-intellisense", WaitForIntellisenseArgumentName, true, true)),
            ]);
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

        private static string BuildBuildErrorsArgs(JsonNode? id, JsonObject? args)
        {
            var max = GetOptionalArgumentText(args, "max");
            return BuildArgs(
            [
                (TimeoutMillisecondsSwitchName, GetBuildErrorsTimeoutArgument(id, args)),
                ("max", max),
                .. BuildBooleanArgs(args,
                    ("wait-for-intellisense", WaitForIntellisenseArgumentName, true, true)),
            ]);
        }

        private static string BuildDebugStackArgs(JsonObject? args)
        {
            var threadId = GetOptionalArgumentText(args, "thread_id");
            var maxFrames = GetOptionalArgumentText(args, "max_frames");
            return BuildArgs(("thread-id", threadId), ("max-frames", maxFrames));
        }

        private static string BuildDebugLocalsArgs(JsonObject? args)
        {
            var max = GetOptionalArgumentText(args, "max");
            return BuildArgs(("max", max));
        }

        private static string BuildDebugWatchArgs(JsonObject? args)
        {
            var expression = GetOptionalStringArgument(args, "expression");
            var timeout = GetOptionalArgumentText(args, TimeoutMillisecondsArgumentName);
            return BuildArgs(("expression", expression), (TimeoutMillisecondsSwitchName, timeout));
        }

        private static string BuildDiagnosticsSnapshotToolArgs(JsonObject? args)
        {
            var max = GetOptionalArgumentText(args, "max");
            return BuildArgs(
                ("wait-for-intellisense", GetOptionalBooleanArgument(args, WaitForIntellisenseArgumentName, true, true)),
                ("quick", GetOptionalBooleanArgument(args, "quick", false, true)),
                ("max", max));
        }

        private static string BuildSearchSolutionsToolArgs(JsonObject? args)
        {
            var path = GetOptionalStringArgument(args, PathArgumentName);
            var query = GetOptionalStringArgument(args, QueryArgumentName);
            var maxDepth = GetOptionalArgumentText(args, "max_depth");
            var max = GetOptionalArgumentText(args, "max");
            return BuildArgs((PathArgumentName, path), (QueryArgumentName, query), ("max-depth", maxDepth), ("max", max));
        }

        private static string BuildCreateProjectToolArgs(JsonObject? args)
        {
            var name = GetOptionalStringArgument(args, "name");
            var template = GetOptionalStringArgument(args, "template");
            var language = GetOptionalStringArgument(args, "language");
            var directory = GetOptionalStringArgument(args, "directory");
            var solutionFolder = GetOptionalStringArgument(args, "solution_folder");
            return BuildArgs(("name", name), ("template", template), ("language", language), ("directory", directory), ("solution-folder", solutionFolder));
        }

        private static string BuildQueryProjectItemsToolArgs(JsonObject? args)
        {
            var project = GetOptionalStringArgument(args, ProjectArgumentName);
            var path = GetOptionalStringArgument(args, PathArgumentName);
            var max = GetOptionalArgumentText(args, "max");
            return BuildArgs((ProjectArgumentName, project), (PathArgumentName, path), ("max", max));
        }

        private static string BuildQueryProjectPropertiesToolArgs(JsonObject? args)
        {
            var project = GetOptionalStringArgument(args, ProjectArgumentName);
            return BuildArgs((ProjectArgumentName, project), ("names", GetCsv(args?["names"] as JsonArray)));
        }

        private static string BuildQueryProjectConfigurationsToolArgs(JsonObject? args)
        {
            var project = GetOptionalStringArgument(args, ProjectArgumentName);
            return BuildArgs((ProjectArgumentName, project));
        }

        private static string BuildQueryProjectReferencesToolArgs(JsonObject? args)
        {
            var project = GetOptionalStringArgument(args, ProjectArgumentName);
            return BuildArgs(
            [
                (ProjectArgumentName, project),
                .. BuildBooleanArgs(args, ("include-framework", "include_framework", false, true)),
                .. BuildBooleanArgs(args, ("declared-only", "declared_only", false, true)),
            ]);
        }

        private static string BuildQueryProjectOutputsToolArgs(JsonObject? args)
        {
            var project = GetOptionalStringArgument(args, ProjectArgumentName);
            var configuration = GetOptionalStringArgument(args, ConfigurationArgumentName);
            var platform = GetOptionalStringArgument(args, PlatformArgumentName);
            var targetFramework = GetOptionalStringArgument(args, "target_framework");
            return BuildArgs(
                (ProjectArgumentName, project),
                (ConfigurationArgumentName, configuration),
                (PlatformArgumentName, platform),
                ("target-framework", targetFramework));
        }

        private static string BuildFindFilesArgs(JsonObject? args)
        {
            var query = GetOptionalStringArgument(args, QueryArgumentName);
            var path = GetOptionalStringArgument(args, PathArgumentName);
            var maxResults = GetOptionalArgumentText(args, "max_results");
            return BuildArgs(
            [
                (QueryArgumentName, query),
                (PathArgumentName, path),
                ("extensions", GetCsv(args?["extensions"] as JsonArray)),
                ("max-results", maxResults),
                .. BuildBooleanArgs(args, ("include-non-project", "include_non_project", true, true)),
            ]);
        }

        private static string BuildSearchSymbolsArgs(JsonObject? args)
        {
            var query = GetOptionalStringArgument(args, QueryArgumentName);
            var kind = GetOptionalStringArgument(args, "kind");
            var scope = GetOptionalStringArgument(args, "scope");
            var project = GetOptionalStringArgument(args, ProjectArgumentName);
            var path = GetOptionalStringArgument(args, PathArgumentName);
            var max = GetOptionalArgumentText(args, MaxArgumentName);
            return BuildArgs(
            [
                (QueryArgumentName, query),
                ("kind", kind),
                ("scope", scope),
                (ProjectArgumentName, project),
                (PathArgumentName, path),
                (MaxArgumentName, max),
                .. BuildBooleanArgs(args, ("match-case", MatchCaseArgumentName, false, false)),
            ]);
        }

        private static string BuildCountReferencesArgs(JsonObject? args)
        {
            var file = GetOptionalStringArgument(args, FileArgumentName);
            var line = GetOptionalArgumentText(args, LineArgumentName);
            var column = GetOptionalArgumentText(args, ColumnArgumentName);
            var timeout = GetOptionalArgumentText(args, TimeoutMillisecondsArgumentName);
            return BuildArgs(
            [
                (FileArgumentName, file),
                (LineArgumentName, line),
                (ColumnArgumentName, column),
                (TimeoutMillisecondsSwitchName, timeout),
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

        private static string BuildWriteFileArgs(JsonObject? args)
        {
            return BuildArgs(
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                ("content-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(GetOptionalStringArgument(args, "content") ?? string.Empty))));
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

        private static string BuildExecuteCommandArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                ("command", GetOptionalStringArgument(args, "command")),
                ("args", GetOptionalStringArgument(args, "args")),
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                ("document", GetOptionalStringArgument(args, "document")),
                (LineArgumentName, GetOptionalArgumentText(args, LineArgumentName)),
                (ColumnArgumentName, GetOptionalArgumentText(args, ColumnArgumentName)),
                .. BuildBooleanArgs(args, ("select-word", "select_word", false, false)),
            ]);
        }

        private static string BuildFormatDocumentArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                ("command", "Edit.FormatDocument"),
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                (LineArgumentName, GetOptionalArgumentText(args, LineArgumentName)),
                (ColumnArgumentName, GetOptionalArgumentText(args, ColumnArgumentName)),
            ]);
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
                    ("match-case", MatchCaseArgumentName, false, false),
                    ("whole-word", "whole_word", false, false),
                    ("regex", "regex", false, false)),
            ]);
        }

        private static string BuildFindTextBatchArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                ("queries", args?["queries"]?.ToJsonString(JsonOptions)),
                (PathArgumentName, GetOptionalStringArgument(args, PathArgumentName)),
                ("scope", GetOptionalStringArgument(args, "scope")),
                (ProjectArgumentName, GetOptionalStringArgument(args, ProjectArgumentName)),
                ("results-window", GetOptionalArgumentText(args, "results_window")),
                ("max-queries-per-chunk", GetOptionalArgumentText(args, "max_queries_per_chunk")),
                .. BuildBooleanArgs(args,
                    ("match-case", MatchCaseArgumentName, false, false),
                    ("whole-word", "whole_word", false, false),
                    ("regex", "regex", false, false)),
            ]);
        }

        private static string BuildReadFileArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                (FileArgumentName, GetOptionalStringArgument(args, FileArgumentName)),
                ("start-line", GetOptionalArgumentText(args, StartLineArgumentName)),
                ("end-line", GetOptionalArgumentText(args, "end_line")),
                (LineArgumentName, GetOptionalArgumentText(args, LineArgumentName)),
                ("context-before", GetOptionalArgumentText(args, ContextBeforeArgumentName)),
                ("context-after", GetOptionalArgumentText(args, ContextAfterArgumentName)),
                .. BuildBooleanArgs(args, ("reveal-in-editor", "reveal_in_editor", true, true)),
            ]);
        }

        private static string BuildReadFileBatchArgs(JsonObject? args)
        {
            return BuildArgs(
            [
                ("ranges", BuildReadRangesJson(args?["ranges"] as JsonArray)),
            ]);
        }

        private static string? BuildReadRangesJson(JsonArray? ranges)
        {
            if (ranges is null || ranges.Count == 0)
            {
                return null;
            }

            var normalized = new JsonArray();
            foreach (var token in ranges)
            {
                if (token is not JsonObject range)
                {
                    continue;
                }

                var normalizedRange = new JsonObject();
                AddOptionalProperty(normalizedRange, FileArgumentName, GetOptionalNodeClone(range, FileArgumentName));
                AddOptionalProperty(normalizedRange, LineArgumentName, GetOptionalNodeClone(range, LineArgumentName));
                AddOptionalProperty(normalizedRange, "startLine", GetOptionalNodeClone(range, StartLineArgumentName));
                AddOptionalProperty(normalizedRange, "endLine", GetOptionalNodeClone(range, "end_line"));
                AddOptionalProperty(normalizedRange, "contextBefore", GetOptionalNodeClone(range, ContextBeforeArgumentName));
                AddOptionalProperty(normalizedRange, "contextAfter", GetOptionalNodeClone(range, ContextAfterArgumentName));
                normalized.Add(normalizedRange);
            }

            return normalized.ToJsonString(JsonOptions);
        }

        private static JsonNode? GetOptionalNodeClone(JsonObject source, string name)
        {
            return source.TryGetPropertyValue(name, out var value) ? value?.DeepClone() : null;
        }

        private static void AddOptionalProperty(JsonObject target, string name, JsonNode? value)
        {
            if (value is not null)
            {
                target[name] = value;
            }
        }

        private static (string Name, string? Value)[] BuildBooleanArgs(JsonObject? args, params (string SwitchName, string ArgumentName, bool DefaultValue, bool EmitFalse)[] specs)
        {
            return [.. specs.Select(spec => (spec.SwitchName, GetOptionalBooleanArgument(args, spec.ArgumentName, spec.DefaultValue, spec.EmitFalse)))];
        }

        private static string? GetOptionalStringArgument(JsonObject? args, string name)
        {
            return args?[name]?.GetValue<string>();
        }

        private static (string Name, string? Value) OptionalStringArg(string switchName, JsonObject? args, string argumentName)
        {
            return (switchName, GetOptionalStringArgument(args, argumentName));
        }

        private static bool GetBoolean(JsonObject? args, string name, bool defaultValue)
        {
            return args?[name]?.GetValue<bool?>() ?? defaultValue;
        }

        private static (string Name, JsonObject Schema, bool Required) OptionalBooleanProperty(string name, string description) => (name, BooleanSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) OptionalIntegerProperty(string name, string description) => (name, IntegerSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) OptionalStringArrayProperty(string name, string description) => (name, ArrayOfStringsSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) OptionalStringProperty(string name, string description) => (name, StringSchema(description), false);

        private static (string Name, JsonObject Schema, bool Required) RequiredBooleanProperty(string name, string description) => (name, BooleanSchema(description), true);

        private static (string Name, JsonObject Schema, bool Required) RequiredIntegerProperty(string name, string description) => (name, IntegerSchema(description), true);

        private static (string Name, JsonObject Schema, bool Required) RequiredStringArrayProperty(string name, string description) => (name, ArrayOfStringsSchema(description), true);

        private static (string Name, JsonObject Schema, bool Required) RequiredStringProperty(string name, string description) => (name, StringSchema(description), true);

        private static JsonObject FileLineColumnSchema()
        {
            return ObjectSchema(
                RequiredStringProperty(FileArgumentName, AbsoluteOrSolutionRelativeFilePathDescription),
                RequiredIntegerProperty(LineArgumentName, OneBasedLineNumberDescription),
                RequiredIntegerProperty(ColumnArgumentName, "1-based column number."));
        }

        private static JsonObject ReadOnlyToolAnnotations(bool idempotentHint = true) =>
            ToolAnnotations(readOnlyHint: true, idempotentHint: idempotentHint);

        private static JsonObject DestructiveToolAnnotations(bool idempotentHint = false) =>
            ToolAnnotations(destructiveHint: true, idempotentHint: idempotentHint);

        private static JsonObject? InferStandardToolAnnotations(string toolName)
        {
            return toolName switch
            {
                ApplyDiffToolName or
                ShellExecToolName or
                "set_version" or
                "format_document" or
                SaveDocumentToolName or
                "git_add" or
                "git_commit" or
                "git_commit_amend" or
                "git_create_branch" or
                "git_merge" or
                "git_checkout" or
                GitPullToolName or
                GitPushToolName or
                "git_restore" or
                "git_reset" or
                "git_stash_pop" or
                "git_stash_push" or
                NugetAddPackageToolName or
                NugetRemovePackageToolName or
                CondaInstallToolName or
                CondaRemoveToolName or
                PythonInstallPackageToolName or
                PythonRemovePackageToolName or
                PythonCreateEnvToolName or
                AddProjectToolName or
                RemoveProjectToolName or
                AddFileToProjectToolName or
                RemoveFileFromProjectToolName or
                "github_issue_close" or
                "vs_close" or
                ClearBreakpointsToolName => DestructiveToolAnnotations(),
                BridgeStateToolName or
                UiSettingsToolName or
                WaitForReadyArgumentName or
                "bridge_health" or
                "list_instances" or
                "help" or
                ToolHelpToolName or
                ListProjectsToolName or
                ListDocumentsToolName or
                ListTabsToolName or
                ListWindowsToolName or
                ListBreakpointsToolName or
                "errors" or
                WarningsToolName or
                DiagnosticsSnapshotToolName or
                BuildConfigurationsToolName or
                "git_status" or
                "git_current_branch" or
                "git_remote_list" or
                "git_tag_list" or
                "git_stash_list" or
                "git_branch_list" or
                "git_log" or
                "git_show" or
                "git_diff_staged" or
                "git_diff_unstaged" or
                SearchSolutionsToolName or
                SearchSymbolsToolName or
                FindFilesToolName or
                FindTextToolName or
                FindTextBatchToolName or
                ReadFileToolName or
                ReadFileBatchToolName or
                CountReferencesToolName or
                FindReferencesToolName or
                SymbolInfoToolName or
                PeekDefinitionToolName or
                GotoDefinitionToolName or
                GotoImplementationToolName or
                CallHierarchyToolName or
                FileOutlineToolName or
                DebugThreadsToolName or
                DebugStackToolName or
                DebugLocalsToolName or
                DebugModulesToolName or
                DebugWatchToolName or
                DebugExceptionsToolName or
                QueryProjectItemsToolName or
                QueryProjectPropertiesToolName or
                QueryProjectConfigurationsToolName or
                QueryProjectReferencesToolName or
                QueryProjectOutputsToolName or
                PythonListEnvsToolName or
                PythonEnvInfoToolName or
                PythonListPackagesToolName => ReadOnlyToolAnnotations(),
                _ => null,
            };
        }

        private static string BuildDefaultToolTitle(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return string.Empty;
            }

            return string.Join(" ", toolName
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(HumanizeToolTitleToken));
        }

        private static string HumanizeToolTitleToken(string token)
        {
            return token switch
            {
                "ui" => "UI",
                "vs" => "VS",
                "vsix" => "VSIX",
                "mcp" => "MCP",
                "git" => "Git",
                "nuget" => "NuGet",
                "conda" => "Conda",
                "repl" => "REPL",
                "env" => "Environment",
                "envs" => "Environments",
                "id" => "ID",
                "ids" => "IDs",
                "uri" => "URI",
                "uris" => "URIs",
                _ when token.Length <= TwoValue => token.ToUpperInvariant(),
                _ => char.ToUpperInvariant(token[0]) + token[1..],
            };
        }

        private static JsonObject ToolAnnotations(bool? readOnlyHint = null, bool? destructiveHint = null, bool? idempotentHint = null)
        {
            var annotations = new JsonObject();

            if (readOnlyHint.HasValue)
            {
                annotations[ReadOnlyHintPropertyName] = readOnlyHint.Value;
            }

            if (destructiveHint.HasValue)
            {
                annotations[DestructiveHintPropertyName] = destructiveHint.Value;
            }

            if (idempotentHint.HasValue)
            {
                annotations[IdempotentHintPropertyName] = idempotentHint.Value;
            }

            return annotations;
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

        private static JsonObject ArraySchema(string description, JsonObject itemSchema, int? minItems = null)
        {
            var schema = new JsonObject
            {
                ["type"] = "array",
                [DescriptionPropertyName] = description,
                ["items"] = itemSchema,
            };

            if (minItems.HasValue)
            {
                schema["minItems"] = minItems.Value;
            }

            return schema;
        }

        private static async Task<JsonNode> CallGitToolAsync(JsonNode? id, string toolName, JsonObject? args, BridgeBinding bridgeBinding)
        {
            string workingDirectory = await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);

            JsonObject? managedGitResult = ManagedGitToolProvider.TryExecute(workingDirectory, toolName, args);
            if (managedGitResult is not null)
            {
                bool isError = !(managedGitResult["success"]?.GetValue<bool>() ?? false);
                return WrapToolResult(managedGitResult, isError);
            }

            string gitArgs = toolName switch
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
                GitMergeToolName => BuildGitMergeArgs(args, id),
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

            int timeoutMs = toolName is "git_push" or "git_pull" or "git_fetch" or GitMergeToolName or "git_clone" ? 120_000 : 30_000;
            JsonObject gitResult = await RunGitAsync(workingDirectory, gitArgs, timeoutMs).ConfigureAwait(false);
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
                CondaInstallToolName => BuildCondaInstallArgs(args, id),
                CondaRemoveToolName => BuildCondaRemoveArgs(args, id),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName)),
            };
            var condaResult = await RunProcessAsync(condaExecutable, condaArgs, workingDirectory).ConfigureAwait(false);

            return WrapToolResult(condaResult, !(condaResult["success"]?.GetValue<bool>() ?? false));
        }

        private static async Task<JsonNode> CallPythonToolAsync(JsonNode? id, string toolName, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var timeoutMs = GetIntOrDefault(args, TimeoutMillisecondsArgumentName, DefaultPythonToolTimeoutMilliseconds);
            JsonObject pythonResult;

            switch (toolName)
            {
                case PythonListEnvsToolName:
                    pythonResult = await PythonRuntimeService.ListEnvironmentsAsync().ConfigureAwait(false);
                    break;
                case PythonEnvInfoToolName:
                    pythonResult = await PythonRuntimeService.GetEnvironmentInfoAsync(GetOptionalStringArgument(args, PathArgumentName)).ConfigureAwait(false);
                    break;
                case "python_set_active_env":
                    pythonResult = await PythonRuntimeService.SetActiveEnvironmentAsync(GetOptionalStringArgument(args, PathArgumentName), GetOptionalStringArgument(args, "name")).ConfigureAwait(false);
                    break;
                case PythonListPackagesToolName:
                    pythonResult = await PythonRuntimeService.ListPackagesAsync(GetOptionalStringArgument(args, PathArgumentName)).ConfigureAwait(false);
                    break;
                case "python_repl":
                {
                    var interpreterPath = GetOptionalStringArgument(args, PathArgumentName);
                    var workingDirectory = await ResolvePythonWorkingDirectoryAsync(id, args, bridgeBinding).ConfigureAwait(false);
                    var environmentInfo = await PythonRuntimeService.GetEnvironmentInfoAsync(interpreterPath).ConfigureAwait(false);
                    var environment = GetPythonEnvironmentFromInfo(id, environmentInfo);
                    var allowUnrestrictedExecution = await IsBridgePythonUnrestrictedExecutionAllowedAsync(id, bridgeBinding).ConfigureAwait(false);
                    var approvalResponse = await RequestBridgeApprovalAsync(
                        id,
                        bridgeBinding,
                        operation: PythonExecutionApprovalOperationName,
                        subject: BuildPythonExecutionApprovalSubject("Run Python code", environment, allowUnrestrictedExecution),
                        details: BuildPythonExecutionApprovalDetails(environment, workingDirectory, timeoutMs, allowUnrestrictedExecution)).ConfigureAwait(false);
                    if (!ResponseFormatter.IsSuccess(approvalResponse))
                    {
                        return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
                    }

                    pythonResult = await PythonRuntimeService.ExecuteSnippetAsync(
                        GetRequiredString(args, id, CodeArgumentName),
                        interpreterPath,
                        workingDirectory,
                        timeoutMs,
                        approved: true,
                        allowUnrestrictedExecution).ConfigureAwait(false);
                    AttachApprovalMetadata(pythonResult, approvalResponse);
                    break;
                }
                case "python_run_file":
                {
                    var interpreterPath = GetOptionalStringArgument(args, PathArgumentName);
                    var workingDirectory = await ResolvePythonWorkingDirectoryAsync(id, args, bridgeBinding).ConfigureAwait(false);
                    var environmentInfo = await PythonRuntimeService.GetEnvironmentInfoAsync(interpreterPath).ConfigureAwait(false);
                    var environment = GetPythonEnvironmentFromInfo(id, environmentInfo);
                    var filePath = GetRequiredString(args, id, FileArgumentName);
                    var allowUnrestrictedExecution = await IsBridgePythonUnrestrictedExecutionAllowedAsync(id, bridgeBinding).ConfigureAwait(false);
                    var approvalResponse = await RequestBridgeApprovalAsync(
                        id,
                        bridgeBinding,
                        operation: PythonExecutionApprovalOperationName,
                        subject: BuildPythonExecutionApprovalSubject("Run Python file", environment, allowUnrestrictedExecution),
                        details: BuildPythonExecutionApprovalDetails(environment, workingDirectory, timeoutMs, allowUnrestrictedExecution, "file=" + filePath)).ConfigureAwait(false);
                    if (!ResponseFormatter.IsSuccess(approvalResponse))
                    {
                        return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
                    }

                    pythonResult = await PythonRuntimeService.RunFileAsync(
                        filePath,
                        GetOptionalStringArray(args, "args"),
                        interpreterPath,
                        workingDirectory,
                        timeoutMs,
                        approved: true,
                        allowUnrestrictedExecution).ConfigureAwait(false);
                    AttachApprovalMetadata(pythonResult, approvalResponse);
                    break;
                }
                case PythonInstallPackageToolName:
                {
                    var packages = GetRequiredStringArray(args, id, PackagesArgumentName);
                    var interpreterPath = GetOptionalStringArgument(args, PathArgumentName);
                    var environmentInfo = await PythonRuntimeService.GetEnvironmentInfoAsync(interpreterPath).ConfigureAwait(false);
                    var environment = GetPythonEnvironmentFromInfo(id, environmentInfo);
                    var approvalResponse = await RequestBridgeApprovalAsync(
                        id,
                        bridgeBinding,
                        operation: PythonEnvironmentMutationApprovalOperationName,
                        subject: BuildPythonEnvironmentMutationApprovalSubject("Install Python packages", environment, string.Join(", ", packages)),
                        details: BuildPythonEnvironmentMutationApprovalDetails(environment, null, timeoutMs, "packageCount=" + packages.Count)).ConfigureAwait(false);
                    if (!ResponseFormatter.IsSuccess(approvalResponse))
                    {
                        return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
                    }

                    pythonResult = await PythonRuntimeService.InstallPackagesAsync(
                        packages,
                        interpreterPath,
                        timeoutMs,
                        approved: true).ConfigureAwait(false);
                    AttachApprovalMetadata(pythonResult, approvalResponse);
                    break;
                }
                case "python_install_requirements":
                {
                    var requirementsFile = GetRequiredString(args, id, FileArgumentName);
                    var interpreterPath = GetOptionalStringArgument(args, PathArgumentName);
                    var environmentInfo = await PythonRuntimeService.GetEnvironmentInfoAsync(interpreterPath).ConfigureAwait(false);
                    var environment = GetPythonEnvironmentFromInfo(id, environmentInfo);
                    var approvalResponse = await RequestBridgeApprovalAsync(
                        id,
                        bridgeBinding,
                        operation: PythonEnvironmentMutationApprovalOperationName,
                        subject: BuildPythonEnvironmentMutationApprovalSubject("Install Python requirements", environment, requirementsFile),
                        details: BuildPythonEnvironmentMutationApprovalDetails(environment, null, timeoutMs, "requirementsFile=" + requirementsFile)).ConfigureAwait(false);
                    if (!ResponseFormatter.IsSuccess(approvalResponse))
                    {
                        return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
                    }

                    pythonResult = await PythonRuntimeService.InstallRequirementsAsync(
                        requirementsFile,
                        interpreterPath,
                        timeoutMs,
                        approved: true).ConfigureAwait(false);
                    AttachApprovalMetadata(pythonResult, approvalResponse);
                    break;
                }
                case PythonRemovePackageToolName:
                {
                    var packages = GetRequiredStringArray(args, id, PackagesArgumentName);
                    var interpreterPath = GetOptionalStringArgument(args, PathArgumentName);
                    var environmentInfo = await PythonRuntimeService.GetEnvironmentInfoAsync(interpreterPath).ConfigureAwait(false);
                    var environment = GetPythonEnvironmentFromInfo(id, environmentInfo);
                    var approvalResponse = await RequestBridgeApprovalAsync(
                        id,
                        bridgeBinding,
                        operation: PythonEnvironmentMutationApprovalOperationName,
                        subject: BuildPythonEnvironmentMutationApprovalSubject("Remove Python packages", environment, string.Join(", ", packages)),
                        details: BuildPythonEnvironmentMutationApprovalDetails(environment, null, timeoutMs, "packageCount=" + packages.Count)).ConfigureAwait(false);
                    if (!ResponseFormatter.IsSuccess(approvalResponse))
                    {
                        return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
                    }

                    pythonResult = await PythonRuntimeService.RemovePackagesAsync(
                        packages,
                        interpreterPath,
                        timeoutMs,
                        approved: true).ConfigureAwait(false);
                    AttachApprovalMetadata(pythonResult, approvalResponse);
                    break;
                }
                case PythonCreateEnvToolName:
                {
                    var targetPath = GetRequiredString(args, id, PathArgumentName);
                    var baseInterpreterPath = GetOptionalStringArgument(args, BasePathArgumentName);
                    var workingDirectory = await ResolvePythonWorkingDirectoryAsync(id, args, bridgeBinding).ConfigureAwait(false);
                    var environmentInfo = await PythonRuntimeService.GetEnvironmentInfoAsync(baseInterpreterPath).ConfigureAwait(false);
                    var environment = GetPythonEnvironmentFromInfo(id, environmentInfo);
                    var approvalResponse = await RequestBridgeApprovalAsync(
                        id,
                        bridgeBinding,
                        operation: PythonEnvironmentMutationApprovalOperationName,
                        subject: BuildPythonEnvironmentMutationApprovalSubject("Create Python environment", environment, targetPath),
                        details: BuildPythonEnvironmentMutationApprovalDetails(environment, workingDirectory, timeoutMs, "targetPath=" + targetPath)).ConfigureAwait(false);
                    if (!ResponseFormatter.IsSuccess(approvalResponse))
                    {
                        return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
                    }

                    pythonResult = await PythonRuntimeService.CreateEnvironmentAsync(
                        targetPath,
                        baseInterpreterPath,
                        workingDirectory,
                        timeoutMs,
                        approved: true).ConfigureAwait(false);
                    AttachApprovalMetadata(pythonResult, approvalResponse);
                    break;
                }
                default:
                    throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName));
            }

            return WrapToolResult(pythonResult, !(pythonResult["success"]?.GetValue<bool>() ?? false));
        }

        private static async Task<string> ResolvePythonWorkingDirectoryAsync(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var requestedWorkingDirectory = GetOptionalStringArgument(args, "cwd");
            if (!string.IsNullOrWhiteSpace(requestedWorkingDirectory))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedWorkingDirectory));
            }

            try
            {
                return await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);
            }
            catch (McpRequestException)
            {
                return Directory.GetCurrentDirectory();
            }
        }

        private static async Task<JsonNode> CallShellExecToolAsync(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var exe = GetRequiredString(args, id, "exe");
            var arguments = args?["args"]?.GetValue<string>() ?? string.Empty;
            var timeoutMs = GetIntOrDefault(args, TimeoutMillisecondsArgumentName, 60_000);
            var cwdArg = args?["cwd"]?.GetValue<string>();
            var workingDirectory = !string.IsNullOrWhiteSpace(cwdArg)
                ? cwdArg
                : await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);

            var approvalResponse = await RequestBridgeApprovalAsync(
                id,
                bridgeBinding,
                operation: ShellExecToolName,
                subject: BuildShellExecApprovalSubject(exe, arguments),
                details: BuildShellExecApprovalDetails(workingDirectory, timeoutMs)).ConfigureAwait(false);
            if (!ResponseFormatter.IsSuccess(approvalResponse))
            {
                return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
            }

            var processResult = await RunProcessAsync(exe, arguments, workingDirectory, timeoutMs).ConfigureAwait(false);
            var tailLines = GetIntOrDefault(args, "tail_lines", 0);
            if (tailLines > 0)
            {
                foreach (var key in new[] { "stdout", "stderr" })
                {
                    var text = processResult[key]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var lines = text.Split('\n');
                        if (lines.Length > tailLines)
                            processResult[key] = JsonValue.Create(string.Join('\n', lines[^tailLines..]));
                    }
                }
            }
            AttachApprovalMetadata(processResult, approvalResponse);
            return WrapToolResult(processResult, !(processResult["success"]?.GetValue<bool>() ?? false));
        }

        private static async Task<JsonNode> CallSetVersionToolAsync(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var version = GetRequiredString(args, id, "version");
            var solutionDir = await ResolveSolutionWorkingDirectoryAsync(id, bridgeBinding).ConfigureAwait(false);

            var approvalResponse = await RequestBridgeApprovalAsync(
                id,
                bridgeBinding,
                operation: "edit",
                subject: $"Set synced bridge version to {version}",
                details: BuildSetVersionApprovalDetails(version)).ConfigureAwait(false);
            if (!ResponseFormatter.IsSuccess(approvalResponse))
            {
                return WrapToolResult(BuildApprovalFailurePayload(approvalResponse), isError: true);
            }

            var updatedFiles = new JsonArray();

            // Directory.Build.props — update <Version>…</Version>
            var dbpPath = Path.Combine(solutionDir, "Directory.Build.props");
            if (File.Exists(dbpPath))
            {
                var text = File.ReadAllText(dbpPath);
                var next = VersionTagRegex().Replace(text, $"<Version>{version}</Version>");
                if (next != text)
                    File.WriteAllText(dbpPath, next);
                updatedFiles.Add(JsonValue.Create("Directory.Build.props"));
            }

            // source.extension.vsixmanifest — update Version="…" on <Identity …>
            var manifestPath = Path.Combine(solutionDir, "src", "VsIdeBridge", "source.extension.vsixmanifest");
            if (File.Exists(manifestPath))
            {
                var text = File.ReadAllText(manifestPath);
                var next = VsixVersionRegex().Replace(text, version);
                if (next != text)
                    File.WriteAllText(manifestPath, next);
                updatedFiles.Add(JsonValue.Create("src/VsIdeBridge/source.extension.vsixmanifest"));
            }

            // installer/inno/vs-ide-bridge.iss — update #define MyAppVersion "…"
            var issPath = Path.Combine(solutionDir, "installer", "inno", "vs-ide-bridge.iss");
            if (File.Exists(issPath))
            {
                var text = File.ReadAllText(issPath);
                var next = IssVersionRegex().Replace(text, version);
                if (next != text)
                    File.WriteAllText(issPath, next);
                updatedFiles.Add(JsonValue.Create("installer/inno/vs-ide-bridge.iss"));
            }

            var versionResult = new JsonObject
            {
                ["success"] = true,
                ["version"] = version,
                ["updated_files"] = updatedFiles,
                ["file_count"] = updatedFiles.Count,
            };
            AttachApprovalMetadata(versionResult, approvalResponse);
            return WrapToolResult(versionResult, isError: false);
        }

        private static JsonObject CallVsCloseTool(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var explicitPid = args?["process_id"]?.GetValue<int?>();
            var force = GetBoolean(args, "force", false);

            int pid;
            if (explicitPid.HasValue)
            {
                pid = explicitPid.Value;
            }
            else
            {
                var discovery = bridgeBinding.CurrentDiscovery
                    ?? throw new McpRequestException(id, BridgeErrorCode, "No bound VS instance. Pass 'process_id' explicitly or call bind_instance first.");
                pid = discovery.ProcessId;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                throw new McpRequestException(id, BridgeErrorCode, $"No running process found with ID {pid}.");
            }

            bool signalSent;
            if (force)
            {
                process.Kill();
                signalSent = true;
            }
            else
            {
                signalSent = process.CloseMainWindow();
            }

            var killProcessResult = new JsonObject
            {
                ["success"] = true,
                ["pid"] = pid,
                ["force"] = force,
                ["signal_sent"] = signalSent,
            };
            return WrapToolResult(killProcessResult, isError: false);
        }

        private static async Task<JsonObject> CallVsOpenToolAsync(JsonNode? id, JsonObject? args)
        {
            var solution = args?[SolutionArgumentName]?.GetValue<string>();
            var explicitDevenv = args?["devenv_path"]?.GetValue<string>();
            var devenvPath = !string.IsNullOrWhiteSpace(explicitDevenv)
                ? explicitDevenv
                : ResolveDevenvPathForMcp(id);

            var launched = await VisualStudioLauncher.LaunchAsync(devenvPath, solution).ConfigureAwait(false);
            if (!launched.Success)
            {
                var stderr = string.IsNullOrWhiteSpace(launched.Stderr)
                    ? string.Empty
                    : $" stderr: {launched.Stderr.Trim()}";
                throw new McpRequestException(id, BridgeErrorCode, $"Visual Studio launch request failed via {launched.Launcher}.{stderr}");
            }

            var vsOpenResult = new JsonObject
            {
                ["success"] = true,
                ["pid"] = launched.ProcessId,
                ["launch_requested"] = true,
                ["launcher"] = launched.Launcher,
                ["devenv_path"] = devenvPath,
                [SolutionArgumentName] = solution ?? string.Empty,
            };
            return WrapToolResult(vsOpenResult, isError: false);
        }

        private static async Task<JsonObject> CallWaitForInstanceToolAsync(JsonNode? id, JsonObject? args, BridgeBinding bridgeBinding)
        {
            var requestedSolutionPath = VsOpenInstanceSelector.NormalizePath(args?[SolutionArgumentName]?.GetValue<string>());
            var timeoutMilliseconds = GetOptionalPositiveInt(args, id, TimeoutMillisecondsArgumentName) ?? VsOpenDiscoveryTimeoutMilliseconds;
            var existingInstances = await PipeDiscovery.ListAsync(verbose: false, bridgeBinding.DiscoveryMode).ConfigureAwait(false);
            var existingInstanceIds = existingInstances.Select(item => item.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingProcessIds = existingInstances.Select(item => item.ProcessId).ToHashSet();

            var discovered = SelectWaitForInstanceMatch(existingInstances, requestedSolutionPath)
                ?? await WaitForVisualStudioInstanceAsync(
                    existingInstanceIds,
                    existingProcessIds,
                    requestedSolutionPath,
                    bridgeBinding.DiscoveryMode,
                    timeoutMilliseconds).ConfigureAwait(false)
                ?? throw new McpRequestException(id, BridgeErrorCode, $"No live VS IDE Bridge instance appeared within {timeoutMilliseconds} ms.");

            var discoveryResult = DiscoveryToJson(discovered);
            discoveryResult["success"] = true;
            return WrapToolResult(discoveryResult, isError: false);
        }

        private static async Task<PipeDiscovery?> WaitForVisualStudioInstanceAsync(
            IReadOnlyCollection<string> existingInstanceIds,
            IReadOnlyCollection<int> existingProcessIds,
            string? requestedSolutionPath,
            DiscoveryMode discoveryMode,
            int timeoutMilliseconds)
        {
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            while (!timeout.IsCancellationRequested)
            {
                var instances = await PipeDiscovery.ListAsync(verbose: false, discoveryMode).ConfigureAwait(false);
                var discovered = SelectWaitForInstanceMatch(instances, requestedSolutionPath)
                    ?? VsOpenInstanceSelector.SelectInstance(existingInstanceIds, existingProcessIds, instances, launchedProcessId: 0, requestedSolutionPath: requestedSolutionPath);
                if (discovered is not null)
                {
                    return discovered;
                }

                try
                {
                    await Task.Delay(VsOpenDiscoveryPollMilliseconds, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
            }

            return null;
        }

        private static PipeDiscovery? SelectWaitForInstanceMatch(IReadOnlyList<PipeDiscovery> instances, string? requestedSolutionPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedSolutionPath))
            {
                var bySolution = instances.FirstOrDefault(instance => VsOpenInstanceSelector.PathsEqual(instance.SolutionPath, requestedSolutionPath));
                if (bySolution is not null)
                {
                    return bySolution;
                }
            }

            return instances
                .OrderByDescending(instance => instance.LastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static string ResolveDevenvPathForMcp(JsonNode? id)
        {
            // Try vswhere first for reliable cross-edition detection
            var vswhereExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                VisualStudioInstallDirName, "Installer", "vswhere.exe");

            if (File.Exists(vswhereExe))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vswhereExe,
                        Arguments = "-latest -prerelease -requires Microsoft.Component.MSBuild -find Common7\\IDE\\devenv.exe",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi)!;
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(ShortOperationTimeoutMilliseconds);
                    if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                        return output;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"vswhere failed: {ex.Message}");
                }
            }

            // Fallback: hardcoded edition/year candidates
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] candidates =
            [
                Path.Combine(programFiles, VisualStudioInstallDirName, "18", "Community", "Common7", "IDE", DevenvExeFileName),
                Path.Combine(programFiles, VisualStudioInstallDirName, "18", "Professional", "Common7", "IDE", DevenvExeFileName),
                Path.Combine(programFiles, VisualStudioInstallDirName, "18", "Enterprise", "Common7", "IDE", DevenvExeFileName),
                Path.Combine(programFiles, VisualStudioInstallDirName, Vs2022Year, "Community", "Common7", "IDE", DevenvExeFileName),
                Path.Combine(programFiles, VisualStudioInstallDirName, Vs2022Year, "Professional", "Common7", "IDE", DevenvExeFileName),
                Path.Combine(programFiles, VisualStudioInstallDirName, Vs2022Year, "Enterprise", "Common7", "IDE", DevenvExeFileName),
            ];
            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;

            throw new McpRequestException(id, BridgeErrorCode, $"{DevenvExeFileName} not found. Install Visual Studio or pass 'devenv_path' explicitly.");
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
            var packages = GetRequiredStringArray(args, id, PackagesArgumentName);
            var channels = GetOptionalStringArray(args, "channels");
            var environmentName = args?["name"]?.GetValue<string>();
            var environmentPrefix = args?["prefix"]?.GetValue<string>();
            var dryRun = GetBoolean(args, "dry_run", false);
            var autoYes = GetBoolean(args, "yes", true);

            var segments = new List<string> { "install" };
            AppendCondaEnvironmentSelector(segments, environmentName, environmentPrefix, id, CondaInstallToolName);

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
            var packages = GetRequiredStringArray(args, id, PackagesArgumentName);
            var environmentName = args?["name"]?.GetValue<string>();
            var environmentPrefix = args?["prefix"]?.GetValue<string>();
            var dryRun = GetBoolean(args, "dry_run", false);
            var autoYes = GetBoolean(args, "yes", true);

            var segments = new List<string> { "remove" };
            AppendCondaEnvironmentSelector(segments, environmentName, environmentPrefix, id, CondaRemoveToolName);

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

        private static string BuildGitMergeArgs(JsonObject? args, JsonNode? id)
        {
            var source = GetRequiredString(args, id, "source");
            var ffOnly = args?["ff_only"]?.GetValue<bool>() == true;
            var noFastForward = args?["no_ff"]?.GetValue<bool>() == true;
            var squash = args?["squash"]?.GetValue<bool>() == true;
            var message = args?["message"]?.GetValue<string>();

            if (ffOnly && noFastForward)
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, "git_merge cannot set both ff_only and no_ff.");
            }

            var segments = new List<string> { "merge" };
            if (ffOnly)
            {
                segments.Add("--ff-only");
            }
            else if (noFastForward)
            {
                segments.Add("--no-ff");
            }

            if (squash)
            {
                segments.Add("--squash");
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                segments.Add("-m");
                segments.Add(QuoteForGit(message));
            }

            segments.Add(QuoteForGit(source));
            return string.Join(" ", segments);
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
                var stateData = state["Data"] as JsonObject;
                solutionPath = stateData?["solutionPath"]?.GetValue<string>() ?? string.Empty;
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

        private static int? GetOptionalPositiveInt(JsonObject? args, JsonNode? id, string name)
        {
            var value = args?[name]?.GetValue<int?>();
            if (value is null)
            {
                return null;
            }

            if (value.Value <= 0)
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"tools/call argument '{name}' must be greater than 0.");
            }

            return value.Value;
        }

        private static string? GetBuildErrorsTimeoutArgument(JsonNode? id, JsonObject? args)
        {
            var timeout = args?["timeout_ms"]?.GetValue<int?>();
            if (timeout is null)
            {
                return null;
            }

            if (timeout.Value < ShortOperationTimeoutMilliseconds)
            {
                throw new McpRequestException(id, JsonRpcInvalidParamsCode, $"tools/call argument 'timeout_ms' must be at least {ShortOperationTimeoutMilliseconds} for build_errors.");
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

            var githubResult = toolName switch
            {
                "github_issue_search" => await GitHubIssueSearchAsync(repo, args, token).ConfigureAwait(false),
                "github_issue_close" => await GitHubIssueCloseAsync(repo, args, token, id).ConfigureAwait(false),
                _ => throw new McpRequestException(id, JsonRpcInvalidParamsCode, FormatUnknownMcpToolMessage(toolName)),
            };

            return WrapToolResult(githubResult, !(githubResult["success"]?.GetValue<bool>() ?? false));
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
            var issueQuery = $"repo:{repo} is:issue";
            if (state != "all")
            {
                issueQuery += $" is:{state}";
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                issueQuery += $" {query}";
            }

            var uri = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(issueQuery)}&per_page={limit}";
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
            var gitOriginResult = await RunGitAsync(workingDirectory, "remote get-url origin").ConfigureAwait(false);
            if (!(gitOriginResult["success"]?.GetValue<bool>() ?? false))
            {
                return null;
            }

            var url = (gitOriginResult["stdout"]?.GetValue<string>() ?? string.Empty).Trim();
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
            if (!int.TryParse(lengthLine.Split(':', TwoValue)[1].Trim(), out var length) || length < 0)
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
                if (arr.Length >= TwoValue && arr[^1] == (byte)'\n' && arr[^TwoValue] == (byte)'\n'
                    && !(arr.Length >= HeaderTerminatorLength && arr[^HeaderTerminatorLength] == (byte)'\r'))
                {
                    McpTrace($"ReadHeaderAsync: got LF-only header after {bytes.Count} bytes");
                    return Encoding.ASCII.GetString([.. bytes]);
                }

                var singleByte = new byte[1];
                var read = await input.ReadAsync(singleByte).ConfigureAwait(false);
                if (read == 0)
                {
                    McpTrace($"ReadHeaderAsync: EOF after {bytes.Count} bytes. First bytes: {BitConverter.ToString([.. firstBytes])}");
                    return string.Empty;
                }

                bytes.Add(singleByte[0]);
                if (firstBytes.Count < 64) firstBytes.Add(singleByte[0]);
                lastFour.Enqueue(singleByte[0]);
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

        [GeneratedRegex("<Version>[^<]*</Version>")]
        private static partial Regex VersionTagRegex();

        [GeneratedRegex("""(?<=<Identity[^>]+Version=")[^"]*(?=")""")]
        private static partial Regex VsixVersionRegex();

        [GeneratedRegex("""(?<=#define MyAppVersion ")[^"]*(?=")""")]
        private static partial Regex IssVersionRegex();
    }
}


