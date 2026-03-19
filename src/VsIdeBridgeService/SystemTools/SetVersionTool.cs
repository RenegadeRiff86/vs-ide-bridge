using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VsIdeBridgeService.SystemTools;

internal static partial class SetVersionTool
{
    public static Task<JsonNode> ExecuteAsync(JsonNode? id, JsonObject? args, BridgeConnection bridge)
    {
        string version = args?["version"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new McpRequestException(id, McpErrorCodes.InvalidParams, "Missing required argument 'version'.");
        }

        string solutionDirectory = ServiceToolPaths.ResolveSolutionDirectory(bridge);
        JsonArray updatedFiles = new();

        UpdateFile(
            Path.Combine(solutionDirectory, "Directory.Build.props"),
            text => VersionTagRegex().Replace(text, $"<Version>{version}</Version>"),
            "Directory.Build.props",
            updatedFiles);

        UpdateFile(
            Path.Combine(solutionDirectory, "src", "VsIdeBridge", "source.extension.vsixmanifest"),
            text => VsixVersionRegex().Replace(text, version),
            "src/VsIdeBridge/source.extension.vsixmanifest",
            updatedFiles);

        UpdateFile(
            Path.Combine(solutionDirectory, "installer", "inno", "vs-ide-bridge.iss"),
            text => IssVersionRegex().Replace(text, version),
            "installer/inno/vs-ide-bridge.iss",
            updatedFiles);

        JsonObject payload = new()
        {
            ["success"] = true,
            ["version"] = version,
            ["solutionDirectory"] = solutionDirectory,
            ["updated_files"] = updatedFiles,
            ["file_count"] = updatedFiles.Count,
        };

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(),
                },
            },
            ["isError"] = false,
            ["structuredContent"] = payload,
        });
    }

    private static void UpdateFile(string filePath, Func<string, string> transform, string resultPath, JsonArray updatedFiles)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        string text = File.ReadAllText(filePath);
        string next = transform(text);
        if (!string.Equals(next, text, StringComparison.Ordinal))
        {
            File.WriteAllText(filePath, next);
        }

        updatedFiles.Add(JsonValue.Create(resultPath));
    }

    [GeneratedRegex("<Version>[^<]*</Version>")]
    private static partial Regex VersionTagRegex();

    [GeneratedRegex("""(?<=<Identity[^>]+Version=")[^"]*(?=")""")]
    private static partial Regex VsixVersionRegex();

    [GeneratedRegex("""(?<=#define MyAppVersion ")[^"]*(?=")""")]
    private static partial Regex IssVersionRegex();
}
