using System.Text;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// Low-level MCP wire protocol: read/write header-framed or raw-JSON messages.
internal static class McpProtocol
{
    private const int HeaderTerminatorLength = 4;
    private const int RawJsonInitialDepth = 1;
    private const int InvalidRequest = -32600;

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] RawJsonTerminator = [(byte)'\n'];

    public static readonly string[] SupportedProtocolVersions = ["2025-03-26", "2024-11-05"];

    public enum WireFormat { HeaderFramed, RawJson }

    public sealed class IncomingMessage
    {
        public required JsonObject Request { get; init; }
        public WireFormat Format { get; init; }
    }

    public static async Task<IncomingMessage?> ReadAsync(Stream input)
    {
        byte? firstByte = await ReadNextNonWhitespaceByteAsync(input).ConfigureAwait(false);
        if (firstByte is null) return null;

        if (firstByte.Value == (byte)'{' || firstByte.Value == (byte)'[')
        {
            string rawJson = await ReadRawJsonAsync(input, firstByte.Value).ConfigureAwait(false);
            return new IncomingMessage { Request = ParseJsonObject(rawJson), Format = WireFormat.RawJson };
        }

        string header = await ReadHeaderAsync(input, firstByte.Value).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(header)) return null;

        string? lengthLine = Array.Find(
            header.Split('\n'),
            line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (lengthLine is null)
            throw new McpRequestException(null, InvalidRequest, "MCP request missing Content-Length header.");

        if (!int.TryParse(lengthLine.Split(':', 2)[1].Trim(), out int length) || length < 0)
            throw new McpRequestException(null, InvalidRequest, "MCP request has invalid Content-Length.");

        byte[] payloadBytes = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await input.ReadAsync(payloadBytes.AsMemory(offset, length - offset)).ConfigureAwait(false);
            if (read == 0)
                throw new McpRequestException(null, InvalidRequest, "Unexpected EOF while reading MCP payload.");
            offset += read;
        }

        return new IncomingMessage
        {
            Request = ParseJsonObject(Encoding.UTF8.GetString(payloadBytes)),
            Format = WireFormat.HeaderFramed,
        };
    }

    public static async Task WriteAsync(Stream output, JsonObject response, WireFormat format)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        if (format == WireFormat.RawJson)
        {
            await output.WriteAsync(bytes).ConfigureAwait(false);
            await output.WriteAsync(RawJsonTerminator).ConfigureAwait(false);
        }
        else
        {
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(bytes).ConfigureAwait(false);
        }

        await output.FlushAsync().ConfigureAwait(false);
    }

    public static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    public static string SelectProtocolVersion(string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion)) return SupportedProtocolVersions[0];
        if (Array.Exists(SupportedProtocolVersions, v => string.Equals(v, clientVersion, StringComparison.Ordinal)))
            return clientVersion;
        return clientVersion; // echo unknown versions for compatibility
    }

    private static async Task<byte?> ReadNextNonWhitespaceByteAsync(Stream input)
    {
        byte[] buffer = new byte[1];
        while (true)
        {
            int read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return null;
            if (!char.IsWhiteSpace((char)buffer[0])) return buffer[0];
        }
    }

    private static async Task<string> ReadRawJsonAsync(Stream input, byte firstByte)
    {
        List<byte> bytes = [firstByte];
        int depth = RawJsonInitialDepth;
        bool inString = false;
        bool isEscaped = false;

        while (depth > 0)
        {
            byte[] buffer = new byte[1];
            int read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                throw new McpRequestException(null, InvalidRequest, "Unexpected EOF in raw JSON payload.");

            byte current = buffer[0];
            bytes.Add(current);

            if (isEscaped) { isEscaped = false; continue; }
            if (current == (byte)'\\') { if (inString) isEscaped = true; continue; }
            if (current == (byte)'"') { inString = !inString; continue; }
            if (inString) continue;

            if (current == (byte)'{' || current == (byte)'[') depth++;
            else if (current == (byte)'}' || current == (byte)']') depth--;
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static async Task<string> ReadHeaderAsync(Stream input, byte firstByte)
    {
        List<byte> bytes = [firstByte];
        Queue<byte> lastFour = new(HeaderTerminatorLength);
        lastFour.Enqueue(firstByte);

        while (true)
        {
            if (lastFour.Count == HeaderTerminatorLength && lastFour.SequenceEqual(HeaderTerminator))
                return Encoding.ASCII.GetString([.. bytes]);

            byte[] arr = lastFour.ToArray();
            if (arr.Length >= 2 && arr[^1] == (byte)'\n' && arr[^2] == (byte)'\n'
                && !(arr.Length >= 4 && arr[^4] == (byte)'\r'))
                return Encoding.ASCII.GetString([.. bytes]);

            byte[] single = new byte[1];
            int read = await input.ReadAsync(single).ConfigureAwait(false);
            if (read == 0) return string.Empty;

            bytes.Add(single[0]);
            lastFour.Enqueue(single[0]);
            if (lastFour.Count > HeaderTerminatorLength) lastFour.Dequeue();
        }
    }

    private static JsonObject ParseJsonObject(string json) =>
        JsonNode.Parse(json) as JsonObject
        ?? throw new McpRequestException(null, InvalidRequest, "MCP request must be a JSON object.");
}
