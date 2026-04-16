using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public static partial class ToolDefinitionCatalog
{
    private const string NavigationTag = "navigation";

    private static ToolDefinition CreateMutatingTool(
        string name,
        string category,
        string summary,
        string description,
        JsonObject parameterSchema,
        string? bridgeCommand = null,
        string? title = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        bool destructive = false)
    {
        return CreateTool(
            name,
            category,
            summary,
            description,
            parameterSchema,
            readOnly: false,
            mutating: true,
            destructive: destructive,
            bridgeCommand: bridgeCommand,
            title: title,
            aliases: aliases,
            tags: tags);
    }

    private static ToolDefinition CreateReadOnlyTool(
        string name,
        string category,
        string summary,
        string description,
        JsonObject parameterSchema,
        string? bridgeCommand = null,
        string? title = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null)
    {
        return CreateTool(
            name,
            category,
            summary,
            description,
            parameterSchema,
            readOnly: true,
            mutating: false,
            destructive: false,
            bridgeCommand: bridgeCommand,
            title: title,
            aliases: aliases,
            tags: tags);
    }

    private static ToolDefinition CreateTool(
        string name,
        string category,
        string summary,
        string description,
        JsonObject parameterSchema,
        bool readOnly,
        bool mutating,
        bool destructive,
        string? bridgeCommand = null,
        string? title = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null)
    {
        return new ToolDefinition(
            name,
            category,
            summary,
            description,
            parameterSchema,
            readOnly: readOnly,
            mutating: mutating,
            destructive: destructive,
            aliases: aliases,
            tags: tags,
            bridgeCommand: bridgeCommand,
            title: title);
    }
}
