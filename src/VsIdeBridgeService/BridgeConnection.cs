using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// Manages a VS bridge connection for one MCP session.
// Caches the discovered instance to avoid repeated discovery on every tool call.
// Thread-safe: multiple concurrent tool calls may use the same connection.
internal sealed class BridgeConnection
{
    private const int DefaultTimeoutMs = 130_000;
    private const int BridgeError = -32001;
    private const int TimeoutError = -32002;
    private const int CommError = -32003;

    private readonly DiscoveryMode _discoveryMode;
    private readonly int _timeoutMs;
    private readonly object _gate = new();

    private BridgeInstanceSelector _selector = new();
    private BridgeInstance? _cached;
    private string _lastSolutionPath = string.Empty;

    public BridgeConnection(string[] args)
    {
        _discoveryMode = ResolveMode(args);
        _timeoutMs = GetIntArg(args, "timeout-ms", DefaultTimeoutMs);
    }

    // ── Public API used by tool handlers ──────────────────────────────────────

    public Task<JsonObject> SendAsync(JsonNode? id, string command, string args)
        => SendCoreAsync(id, command, args, ignoreSolutionHint: false);

    public Task<JsonObject> SendIgnoringSolutionHintAsync(JsonNode? id, string command, string args)
        => SendCoreAsync(id, command, args, ignoreSolutionHint: true);

    public string CurrentSolutionPath
    {
        get { lock (_gate) { return _lastSolutionPath; } }
    }

    public BridgeInstance? CurrentInstance
    {
        get { lock (_gate) { return _cached; } }
    }

    public BridgeInstanceSelector CurrentSelector
    {
        get { lock (_gate) { return _selector; } }
    }

    public DiscoveryMode Mode => _discoveryMode;

    // Bind to a specific instance and return binding info.
    public async Task<JsonObject> BindAsync(JsonNode? id, JsonObject? args)
    {
        BridgeInstanceSelector newSelector = ParseSelector(args);
        lock (_gate)
        {
            _selector = newSelector;
            _cached = null;
        }

        try
        {
            BridgeInstance discovered = await GetInstanceAsync(ignoreSolutionHint: false).ConfigureAwait(false);
            RememberSolutionPath(discovered.SolutionPath);
            return new JsonObject
            {
                ["success"] = true,
                ["binding"] = InstanceToJson(discovered),
                ["selector"] = SelectorToJson(CurrentSelector),
            };
        }
        catch (BridgeException ex)
        {
            throw new McpRequestException(id, BridgeError, ex.Message);
        }
    }

    // Prefer a solution for future discover without full rebind.
    public void PreferSolution(string? solutionHint)
    {
        lock (_gate)
        {
            _selector = new BridgeInstanceSelector
            {
                InstanceId = _selector.InstanceId,
                ProcessId = _selector.ProcessId,
                PipeName = _selector.PipeName,
                SolutionHint = solutionHint,
            };
            _cached = null;
        }
    }

    // ── Internal send logic ────────────────────────────────────────────────────

    private async Task<JsonObject> SendCoreAsync(JsonNode? id, string command, string args, bool ignoreSolutionHint)
    {
        try
        {
            JsonObject response = await SendPipeAsync(command, args, ignoreSolutionHint).ConfigureAwait(false);
            RememberSolutionPath(response["Data"]?["solutionPath"]?.GetValue<string>());
            return response;
        }
        catch (BridgeException ex) { throw new McpRequestException(id, BridgeError, ex.Message); }
        catch (TimeoutException ex) { throw new McpRequestException(id, TimeoutError, $"Timed out: {ex.Message}"); }
        catch (UnauthorizedAccessException ex)
        {
            return await RetryAfterFailureAsync(id, command, args, ex, ignoreSolutionHint).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return await RetryAfterFailureAsync(id, command, args, ex, ignoreSolutionHint).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject> SendPipeAsync(string command, string args, bool ignoreSolutionHint)
    {
        BridgeInstance instance = await GetInstanceAsync(ignoreSolutionHint).ConfigureAwait(false);
        await using VsPipeClient client = new(instance.PipeName, _timeoutMs);
        JsonObject request = new()
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["command"] = command,
            ["args"] = args,
        };
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private async Task<JsonObject> RetryAfterFailureAsync(
        JsonNode? id, string command, string args, Exception ex, bool ignoreSolutionHint)
    {
        BridgeInstance? evicted = ClearCached();
        if (evicted is null)
            throw new McpRequestException(id, CommError, $"VS bridge communication failed: {ex.Message}");

        try
        {
            JsonObject response = await SendPipeAsync(command, args, ignoreSolutionHint).ConfigureAwait(false);
            RememberSolutionPath(response["Data"]?["solutionPath"]?.GetValue<string>());
            return response;
        }
        catch (BridgeException retryEx) { throw new McpRequestException(id, BridgeError, retryEx.Message); }
        catch (TimeoutException retryEx) { throw new McpRequestException(id, TimeoutError, $"Timed out: {retryEx.Message}"); }
        catch (Exception retryEx) { throw new McpRequestException(id, CommError, $"VS bridge retry failed: {retryEx.Message}"); }
    }

    private async Task<BridgeInstance> GetInstanceAsync(bool ignoreSolutionHint)
    {
        BridgeInstanceSelector selectorSnapshot;
        lock (_gate)
        {
            if (_cached is not null) return _cached;
            selectorSnapshot = _selector;
        }

        BridgeInstanceSelector effectiveSelector = ignoreSolutionHint
            ? new BridgeInstanceSelector
            {
                InstanceId = selectorSnapshot.InstanceId,
                ProcessId = selectorSnapshot.ProcessId,
                PipeName = selectorSnapshot.PipeName,
                SolutionHint = null,
            }
            : selectorSnapshot;

        BridgeInstance discovered = await VsDiscovery.SelectAsync(effectiveSelector, _discoveryMode).ConfigureAwait(false);

        lock (_gate)
        {
            if (_cached is null && ReferenceEquals(_selector, selectorSnapshot))
                _cached = discovered;
        }

        return discovered;
    }

    private BridgeInstance? ClearCached()
    {
        lock (_gate)
        {
            BridgeInstance? prev = _cached;
            _cached = null;
            return prev;
        }
    }

    private void RememberSolutionPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            lock (_gate) { _lastSolutionPath = path; }
    }

    // ── JSON helpers ───────────────────────────────────────────────────────────

    private static JsonObject InstanceToJson(BridgeInstance inst) => new()
    {
        ["instanceId"] = inst.InstanceId,
        ["pipeName"] = inst.PipeName,
        ["pid"] = inst.ProcessId,
        ["solutionPath"] = inst.SolutionPath,
        ["solutionName"] = inst.SolutionName,
        ["source"] = inst.Source,
    };

    private static JsonObject SelectorToJson(BridgeInstanceSelector sel) => new()
    {
        ["instanceId"] = sel.InstanceId,
        ["pid"] = sel.ProcessId,
        ["pipeName"] = sel.PipeName,
        ["solutionHint"] = sel.SolutionHint,
    };

    // ── Arg parsing ────────────────────────────────────────────────────────────

    private static BridgeInstanceSelector ParseSelector(JsonObject? args) => new()
    {
        InstanceId = GetStr(args, "instance_id") ?? GetStr(args, "instance"),
        ProcessId = args?["pid"]?.GetValue<int?>(),
        PipeName = GetStr(args, "pipe_name") ?? GetStr(args, "pipe"),
        SolutionHint = GetStr(args, "solution") ?? GetStr(args, "solution_hint") ?? GetStr(args, "sln"),
    };

    private static string? GetStr(JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DiscoveryMode ResolveMode(string[] args)
    {
        string? raw = GetArgValue(args, "discovery-mode");
        return raw?.ToLowerInvariant() switch
        {
            "memory-first" => DiscoveryMode.MemoryFirst,
            "json-only" => DiscoveryMode.JsonOnly,
            "hybrid" => DiscoveryMode.Hybrid,
            _ => DiscoveryMode.MemoryFirst,
        };
    }

    private static int GetIntArg(string[] args, string name, int defaultValue)
    {
        string? raw = GetArgValue(args, name);
        return raw is not null && int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], $"--{name}", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
