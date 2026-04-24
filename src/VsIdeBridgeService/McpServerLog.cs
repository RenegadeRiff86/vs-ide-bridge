using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static class McpServerLog
{
    private static readonly object SyncRoot = new();
    private static readonly string LogPath = ResolveLogPath();

    public static void Write(string message)
    {
        try
        {
            lock (SyncRoot)
            {
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:O} [pid:{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.Write failed: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.Write failed: {ex}");
        }
        catch (NotSupportedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"McpServerLog.Write failed: {ex}");
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        Write($"{context}: {ex}");
    }

    public static void WriteRequest(JsonObject request, McpProtocol.WireFormat format)
    {
        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        JsonNode? id = request["id"];
        JsonObject? @params = request["params"] as JsonObject;
        string toolName = @params?["name"]?.GetValue<string>() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(toolName))
        {
            Write($"request format={format} id={FormatId(id)} method={method}");
            return;
        }

        Write($"request format={format} id={FormatId(id)} method={method} tool={toolName}");
    }

    public static void WriteResponse(JsonObject response)
    {
        JsonNode? id = response["id"];
        JsonObject? result = response["result"] is JsonObject resultObject ? resultObject : null;
        JsonObject? error = response["error"] as JsonObject;

        if (error is not null)
        {
            string code = error["code"]?.ToJsonString() ?? string.Empty;
            string message = error["message"]?.GetValue<string>() ?? string.Empty;
            Write($"response id={FormatId(id)} errorCode={code} errorMessage={message}");
            return;
        }

        if (result is null)
        {
            Write($"response id={FormatId(id)} result=<null>");
            return;
        }

        bool isError = result["isError"]?.GetValue<bool>() ?? false;
        bool hasStructuredContent = result["structuredContent"] is not null;
        bool hasContent = result["content"] is JsonArray;
        Write(
            $"response id={FormatId(id)} isError={isError} hasContent={hasContent} hasStructuredContent={hasStructuredContent}");
    }

    private static string ResolveLogPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string tempPath = Path.GetTempPath();
        string[] candidates =
        [
            Path.Combine(localAppData, "VsIdeBridge"),
            tempPath,
        ];

        foreach (string directory in candidates)
        {
            try
            {
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "mcp-server.log");
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"McpServerLog.ResolveLogPath failed for '{directory}': {ex}");
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"McpServerLog.ResolveLogPath failed for '{directory}': {ex}");
            }
            catch (NotSupportedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"McpServerLog.ResolveLogPath failed for '{directory}': {ex}");
            }
        }

        return Path.Combine(tempPath, "mcp-server.log");
    }

    private static string FormatId(JsonNode? id)
    {
        return id is null ? "<null>" : id.ToJsonString();
    }
}
