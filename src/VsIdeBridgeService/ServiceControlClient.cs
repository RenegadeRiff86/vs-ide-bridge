using System.IO.Pipes;
using System.Text;

namespace VsIdeBridgeService;

internal sealed class ServiceControlClient : IAsyncDisposable
{
    private const string ServiceControlPipeName = "VsIdeBridgeServiceControl";

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private bool _connected;
    private bool _disposed;

    private ServiceControlClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };
    }

    public static async Task<ServiceControlClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream pipe = new(".", ServiceControlPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            ServiceControlClient client = new(pipe)
            {
                _connected = true,
            };
            await client.SendAsync("CLIENT_CONNECTED", cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool IsConnected => _connected && _pipe.IsConnected && !_disposed;

    public async Task NotifyRequestAsync(CancellationToken cancellationToken = default)
        => await SendAsync("MCP_REQUEST", cancellationToken).ConfigureAwait(false);

    public async Task NotifyCommandStartAsync(CancellationToken cancellationToken = default)
        => await SendAsync("COMMAND_START", cancellationToken).ConfigureAwait(false);

    public async Task NotifyCommandEndAsync(CancellationToken cancellationToken = default)
        => await SendAsync("COMMAND_END", cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (IsConnected)
            {
                await SendAsync("CLIENT_DISCONNECTED", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            McpServerLog.WriteException("failed to send CLIENT_DISCONNECTED notification", ex);
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.WriteException("failed to send CLIENT_DISCONNECTED notification", ex);
        }
        catch (InvalidOperationException ex)
        {
            McpServerLog.WriteException("failed to send CLIENT_DISCONNECTED notification", ex);
        }
        finally
        {
            _connected = false;
            _writer.Dispose();
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendAsync(string evt, CancellationToken cancellationToken)
    {
        if (_disposed || !_connected || !_pipe.IsConnected)
        {
            return;
        }

        await _writer.WriteLineAsync(evt.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
