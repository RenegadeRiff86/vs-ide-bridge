using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal sealed class ToolExecutionRegistry
{
    private readonly ToolEntry[] _all;
    private readonly Dictionary<string, ToolEntry> _byLookupName;
    private readonly ToolRegistry _definitions;

    public ToolExecutionRegistry(IEnumerable<ToolEntry> entries)
    {
        _all = [.. entries];
        _byLookupName = BuildLookup(_all);
        _definitions = new ToolRegistry(_all.Select(entry => entry.Definition));
    }

    public IReadOnlyList<ToolEntry> All => _all;

    public ToolRegistry Definitions => _definitions;

    public bool TryGet(string name, [NotNullWhen(true)] out ToolEntry? entry)
        => _byLookupName.TryGetValue(name, out entry);

    public bool TryGetDefinition(string name, [NotNullWhen(true)] out ToolDefinition? definition)
        => _definitions.TryGet(name, out definition);

    public JsonArray BuildToolsList()
    {
        JsonArray result = [];
        foreach (ToolEntry entry in _all)
            result.Add(entry.Definition.BuildToolObject());

        return result;
    }

    public async Task<JsonNode> DispatchAsync(JsonNode? id, string name, JsonObject? args, BridgeConnection bridge)
    {
        if (!_byLookupName.TryGetValue(name, out ToolEntry? entry))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Unknown tool '{name}'. " +
                "Call list_tools to browse all available tools, " +
                "recommend_tools with a description of your goal to get targeted suggestions, " +
                "or tool_help with a tool name to read its full documentation.");

        try
        {
            return await entry.Handler(id, args, bridge).ConfigureAwait(false);
        }
        catch (McpRequestException ex) when (ex.Code == McpErrorCodes.InvalidParams)
        {
            // Convert InvalidParams into a content-level isError result so the model can
            // self-correct using the embedded input schema rather than receiving a JSON-RPC error.
            McpServerLog.Write($"tool invalid-params tool={name} message={ex.Message}");
            return BuildInvalidParamsResult(ex.Message, entry);
        }
    }

    private static JsonObject BuildInvalidParamsResult(string message, ToolEntry entry)
    {
        string schema = entry.Definition.ParameterSchema?.ToJsonString() ?? "{}";
        string text =
            $"{message}\n\n" +
            $"Input schema for '{entry.Name}':\n{schema}\n\n" +
            $"Call tool_help with name=\"{entry.Name}\" for full documentation.";
        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = true,
        };
    }

    private static Dictionary<string, ToolEntry> BuildLookup(IEnumerable<ToolEntry> entries)
    {
        ToolEntry[] materialized = entries as ToolEntry[] ?? [.. entries];
        Dictionary<string, ToolEntry> lookup = new(StringComparer.Ordinal);

        // Pass 1: canonical names + explicit aliases must be unique. A
        // collision here is a real authoring bug in the registrars and
        // should fail loudly so it gets fixed before shipping.
        foreach (ToolEntry entry in materialized)
        {
            AddLookupEntry(lookup, entry.Name, entry);
            foreach (string alias in entry.Definition.Aliases)
                AddLookupEntry(lookup, alias, entry);
        }

        // Pass 2: BridgeCommand is a wire-level VS pipe address, not a
        // unique MCP dispatch key. Convenience wrapper tools may target the
        // same bridge command as a lower-level tool. Add the shortcut only
        // when it does not conflict with another tool's name, alias, or
        // earlier bridge-command shortcut. Silently dropping collisions keeps
        // the tool catalog usable instead of crashing the entire MCP server
        // and starving every client of every tool.
        foreach (ToolEntry entry in materialized)
        {
            string? bridgeCommand = entry.Definition.BridgeCommand;
            if (string.IsNullOrWhiteSpace(bridgeCommand))
                continue;
            if (lookup.ContainsKey(bridgeCommand))
                continue;
            lookup[bridgeCommand] = entry;
        }

        return lookup;
    }

    private static void AddLookupEntry(
        Dictionary<string, ToolEntry> lookup,
        string key,
        ToolEntry entry)
    {
        if (lookup.TryGetValue(key, out ToolEntry? existing))
        {
            if (ReferenceEquals(existing, entry))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Duplicate MCP tool lookup key '{key}' for '{existing.Name}' and '{entry.Name}'.");
        }

        lookup[key] = entry;
    }
}
