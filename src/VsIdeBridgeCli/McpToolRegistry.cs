using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using VsIdeBridge.Shared;

namespace VsIdeBridgeCli;

internal static partial class CliApp
{
    private static partial class McpServer
    {
        private sealed class McpToolRegistry
        {
            private readonly McpToolEntry[] _all;
            private readonly Dictionary<string, McpToolEntry> _byLookupName;
            private readonly ToolRegistry _definitions;

            public McpToolRegistry(IEnumerable<McpToolEntry> entries)
            {
                _all = entries.ToArray();
                _byLookupName = BuildLookup(_all);
                _definitions = new ToolRegistry(_all.Select(entry => entry.Definition));
            }

            public IReadOnlyList<McpToolEntry> All => _all;

            public ToolRegistry Definitions => _definitions;

            public bool TryGet(string name, [NotNullWhen(true)] out McpToolEntry? entry)
                => _byLookupName.TryGetValue(name, out entry);

            public bool TryGetDefinition(string name, [NotNullWhen(true)] out ToolDefinition? definition)
                => _definitions.TryGet(name, out definition);

            public JsonArray BuildToolsList()
            {
                JsonArray result = new JsonArray();
                foreach (McpToolEntry entry in _all)
                    result.Add(entry.Definition.BuildToolObject());

                return result;
            }

            public Task<JsonNode> DispatchAsync(
                JsonNode? id,
                string name,
                JsonObject? args,
                BridgeBinding binding)
            {
                if (!_byLookupName.TryGetValue(name, out McpToolEntry? entry))
                {
                    throw new McpRequestException(
                        id,
                        JsonRpcInvalidParamsCode,
                        FormatUnknownMcpToolMessage(name));
                }

                return entry.Handler(id, args, binding);
            }

            private static Dictionary<string, McpToolEntry> BuildLookup(IEnumerable<McpToolEntry> entries)
            {
                Dictionary<string, McpToolEntry> lookup = new(StringComparer.Ordinal);
                foreach (McpToolEntry entry in entries)
                {
                    AddLookupEntry(lookup, entry.Name, entry);
                    foreach (string alias in entry.Definition.Aliases)
                        AddLookupEntry(lookup, alias, entry);
                }

                return lookup;
            }

            private static void AddLookupEntry(
                Dictionary<string, McpToolEntry> lookup,
                string key,
                McpToolEntry entry)
            {
                if (lookup.TryGetValue(key, out McpToolEntry? existing))
                {
                    throw new InvalidOperationException(
                        $"Duplicate MCP tool lookup key '{key}' for '{existing.Name}' and '{entry.Name}'.");
                }

                lookup[key] = entry;
            }
        }
    }
}
