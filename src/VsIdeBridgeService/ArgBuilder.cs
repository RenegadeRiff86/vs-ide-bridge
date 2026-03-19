using System.Text;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// Builds CLI-style argument strings sent over the VS bridge named pipe.
// The VS extension parses these strings as command-line arguments.
internal static class ArgBuilder
{
    // Build a --key "value" --key2 "value2" string from named pairs.
    // Null or whitespace values are omitted.
    public static string Build(params (string, string?)[] items)
    {
        PipeArgs builder = new();
        foreach ((string name, string? value) in items)
            builder.Add(name, value);
        return builder.Build();
    }

    // Convenience: no-arg command.
    public static string Empty() => string.Empty;

    // Read a required string from MCP tool args; throw McpRequestException if missing.
    public static string RequiredString(JsonNode? id, JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, $"Missing required argument '{name}'.");
        return value;
    }

    // Read an optional string from MCP tool args.
    public static string? OptionalString(JsonObject? args, string name)
        => args?[name]?.GetValue<string?>();

    // Read a string array from MCP tool args and join elements with a separator.
    // Returns null if the arg is absent or empty.
    public static string? OptionalStringArray(JsonObject? args, string name, string separator = ";")
    {
        JsonNode? node = args?[name];
        if (node is not JsonArray arr || arr.Count == 0)
            return null;
        return string.Join(separator,
            arr.Select(n => n?.GetValue<string>() ?? string.Empty)
               .Where(s => s.Length > 0));
    }

    // Read any JsonNode as a string (for integers, booleans — preserves raw text).
    public static string? OptionalText(JsonObject? args, string name)
        => args?[name]?.ToString();

    // Read an optional boolean, defaulting to defaultValue.
    public static bool OptionalBool(JsonObject? args, string name, bool defaultValue)
        => args?[name]?.GetValue<bool?>() ?? defaultValue;

    // Build a boolean arg pair: emit "true"/"false" as needed.
    // emitFalse: if false, suppress emission when value equals defaultValue.
    public static (string, string?) BoolArg(
        string switchName, JsonObject? args, string argName, bool defaultValue, bool emitFalse)
    {
        bool value = OptionalBool(args, argName, defaultValue);
        string? text = value ? "true" : emitFalse ? "false" : null;
        return (switchName, text);
    }
}

// Mutable builder for a single CLI arg string.
internal sealed class PipeArgs
{
    private readonly List<string> _tokens = [];

    public void AddRequired(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required option --{name}.");
        Add(name, value);
    }

    public void Add(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        _tokens.Add($"--{name}");
        _tokens.Add(Escape(value));
    }

    public string Build() => string.Join(" ", _tokens);

    private static string Escape(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        bool needsQuotes = value.Any(ch => char.IsWhiteSpace(ch) || ch == '"' || ch == '\\');
        if (!needsQuotes)
            return value;

        StringBuilder sb = new();
        sb.Append('"');
        foreach (char ch in value)
        {
            if (ch == '"' || ch == '\\')
                sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
