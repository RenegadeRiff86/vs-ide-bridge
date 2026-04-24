using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VsIdeBridgeService;

internal sealed class StdioHostLease : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly int _currentPid;
    private readonly int _parentPid;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _monitorTask;

    private StdioHostLease(int currentPid, int parentPid)
    {
        _currentPid = currentPid;
        _parentPid = parentPid;
        _monitorTask = Task.Run(MonitorLoopAsync);
    }

    public static StdioHostLease? TryCreate()
    {
        int currentPid = Environment.ProcessId;
        int parentPid = TryGetParentProcessId(currentPid);
        if (parentPid <= 0)
        {
            return null;
        }

        return new StdioHostLease(currentPid, parentPid);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _monitorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            McpServerLog.WriteException("failed to wait for stdio host monitor shutdown", ex);
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.WriteException("failed to wait for stdio host monitor shutdown", ex);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task MonitorLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!IsProcessAlive(_parentPid))
            {
                McpServerLog.Write($"stdio host parent {_parentPid} exited; terminating pid {_currentPid}");
                Environment.Exit(0);
                return;
            }

            try
            {
                await Task.Delay(PollInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int TryGetParentProcessId(int pid)
    {
        IntPtr snapshot = StdioProcessSnapshotInterop.CreateToolhelp32Snapshot(StdioProcessSnapshotInterop.Th32csSnapprocess, 0);
        if (snapshot == StdioProcessSnapshotInterop.InvalidHandleValue)
        {
            return 0;
        }

        try
        {
            StdioProcessSnapshotInterop.PROCESSENTRY32 entry = new()
            {
                dwSize = (uint)Marshal.SizeOf<StdioProcessSnapshotInterop.PROCESSENTRY32>(),
            };

            if (!StdioProcessSnapshotInterop.Process32First(snapshot, ref entry))
            {
                return 0;
            }

            do
            {
                if ((int)entry.th32ProcessID == pid)
                {
                    return (int)entry.th32ParentProcessID;
                }
            }
            while (StdioProcessSnapshotInterop.Process32Next(snapshot, ref entry));

            return 0;
        }
        finally
        {
            StdioProcessSnapshotInterop.CloseHandle(snapshot);
        }
    }
}
