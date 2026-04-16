using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static class BridgeConnectionArgs
{
    public static BridgeInstanceSelector ParseSelector(JsonObject? args) => new()
    {
        InstanceId = GetString(args, "instance_id") ?? GetString(args, "instance"),
        ProcessId = args?["pid"]?.GetValue<int?>(),
        PipeName = GetString(args, "pipe_name") ?? GetString(args, "pipe"),
        SolutionHint = GetString(args, "solution") ?? GetString(args, "solution_hint") ?? GetString(args, "sln"),
    };

    public static DiscoveryMode ResolveMode(string[] args)
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

    public static int? GetOptionalPositiveInt(string[] args, string name)
    {
        string? raw = GetArgValue(args, name);
        return raw is not null && int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : null;
    }

    public static BridgeConnection.ToolTimeoutProfile SelectTimeoutProfile(string command)
    {
        return command switch
        {
            "ready" or
            "build" or
            "rebuild" or
            "build-solution" or
            "rebuild-solution" or
            "build-errors" or
            "run-code-analysis" or
            "debug-start" or
            "find-references" or
            "count-references" or
            "call-hierarchy" or
            "smart-context" or
            "open-solution" or
            "create-solution" => BridgeConnection.ToolTimeoutProfile.Heavy,

            "errors" or
            "warnings" or
            "diagnostics-snapshot" or
            "apply-diff" or
            "write-file" or
            "open-document" or
            "close-file" or
            "close-document" or
            "close-others" or
            "save-document" or
            "reload-document" or
            "activate-document" or
            "list-documents" or
            "list-tabs" or
            "list-windows" or
            "activate-window" or
            "execute-command" or
            "format-document" or
            "quick-info" or
            "peek-definition" or
            "goto-definition" or
            "goto-implementation" or
            "set-build-configuration" or
            "build-configurations" => BridgeConnection.ToolTimeoutProfile.Interactive,

            _ => BridgeConnection.ToolTimeoutProfile.Fast,
        };
    }

    private static string? GetString(JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], $"--{name}", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
