using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VsIdeBridge.Services;

internal sealed class PipeServerDiscoveryCoordinator : IDisposable
{
    private readonly VsIdeBridgePackage _package;
    private readonly IdeBridgeRuntime _runtime;
    private readonly string _discoveryFile;
    private readonly MemoryDiscoveryStore[] _memoryDiscoveryStores;
    private readonly bool _emitDiscoveryJson;
    private readonly bool _emitMemoryDiscovery;
    private readonly object _sync = new();
    private readonly PipeServerDiscoveryState _state = new();
    private readonly CancellationTokenSource _shutdown;
    private string? _cachedSolutionPath;

    public PipeServerDiscoveryCoordinator(VsIdeBridgePackage package, IdeBridgeRuntime runtime, CancellationTokenSource shutdown)
    {
        _package = package;
        _runtime = runtime;
        _shutdown = shutdown;
        string discoveryDir = Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "pipes");
        Directory.CreateDirectory(discoveryDir);
        _discoveryFile = Path.Combine(discoveryDir, $"bridge-{runtime.BridgeInstanceService.ProcessId}.json");
        _memoryDiscoveryStores =
        [
            new(),
            new(
                MemoryDiscoveryStore.GlobalMapName,
                MemoryDiscoveryStore.GlobalMutexName,
                MemoryDiscoveryStore.DefaultCapacityBytes,
                TimeSpan.FromMilliseconds(100),
                static name => new Mutex(false, name),
                static (name, capacity) => MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite)),
        ];
        _emitDiscoveryJson = PipeServerSupport.ReadBooleanEnvironmentVariable("VS_IDE_BRIDGE_EMIT_DISCOVERY_JSON", true);
        string? mode = Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_DISCOVERY_MODE");
        bool modeAllowsMemory = !string.Equals(mode, "json-only", StringComparison.OrdinalIgnoreCase);
        _emitMemoryDiscovery = modeAllowsMemory;
    }

    public void Start()
    {
        QueueUpdate(null, includePurge: true);
    }

    public void UpdateDiscovery(string? solutionPath)
    {
        string? normalizedSolutionPath = NormalizeSolutionPath(solutionPath);
        if (normalizedSolutionPath is not null)
        {
            _cachedSolutionPath = normalizedSolutionPath;
        }

        QueueUpdate(normalizedSolutionPath);
    }

    public async Task<string?> CaptureSolutionPathAsync(DTE2 dte, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            string? cached = NormalizeSolutionPath(_cachedSolutionPath);
            if (cached is not null)
            {
                return cached;
            }
        }

        await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? path = NormalizeSolutionPath(GetSolutionPath(dte));
        _cachedSolutionPath = path;
        return path;
    }

    public async Task RefreshAfterCommandAsync(DTE2 dte, string? initialSolutionPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(initialSolutionPath))
        {
            return;
        }

        string? refreshedSolutionPath = await CaptureSolutionPathAsync(dte, cancellationToken, forceRefresh: true).ConfigureAwait(false);
        QueueUpdate(refreshedSolutionPath);
    }

    public void Dispose()
    {
        foreach (MemoryDiscoveryStore store in _memoryDiscoveryStores)
        {
            store.Dispose();
        }

        try
        {
            if (_emitDiscoveryJson && File.Exists(_discoveryFile))
            {
                File.Delete(_discoveryFile);
            }
        }
        catch (Exception ex) when (ex is not null)
        {
            Debug.WriteLine(ex);
        }

        if (_emitMemoryDiscovery)
        {
            foreach (MemoryDiscoveryStore store in _memoryDiscoveryStores)
            {
                try
                {
                    store.Remove(_runtime.BridgeInstanceService.InstanceId);
                }
                catch (Exception ex) when (ex is not null)
                {
                    ActivityLog.LogWarning(nameof(PipeServerDiscoveryCoordinator), $"Failed to remove memory discovery entry: {ex.Message}");
                }
            }
        }
    }

    private void QueueUpdate(string? solutionPath, bool includePurge = false)
    {
        string? normalizedSolutionPath = NormalizeSolutionPath(solutionPath);
        lock (_sync)
        {
            _state.PendingSolutionPath = normalizedSolutionPath;
            if (includePurge)
            {
                _state.PurgePending = true;
            }

            if (_state.WorkerScheduled)
            {
                return;
            }

            _state.WorkerScheduled = true;
        }

        _ = Task.Run(FlushUpdatesAsync);
    }

    private async Task FlushUpdatesAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            string? solutionPath;
            bool purgePending;
            lock (_sync)
            {
                solutionPath = _state.PendingSolutionPath;
                _state.PendingSolutionPath = null;
                purgePending = _state.PurgePending;
                _state.PurgePending = false;
            }

            try
            {
                if (purgePending)
                {
                    PurgeStaleDiscoveryFiles();
                }

                WriteDiscoveryFile(solutionPath ?? _cachedSolutionPath ?? string.Empty);
            }
            catch (IOException ex)
            {
                ActivityLog.LogWarning(nameof(PipeServerDiscoveryCoordinator), $"Failed to flush discovery updates: {ex.Message}");
            }

            lock (_sync)
            {
                if (_state.PendingSolutionPath is null && !_state.PurgePending)
                {
                    _state.WorkerScheduled = false;
                    return;
                }
            }

            await Task.Yield();
        }

        lock (_sync)
        {
            _state.WorkerScheduled = false;
        }
    }

    private void PurgeStaleDiscoveryFiles()
    {
        string discoveryDir = Path.GetDirectoryName(_discoveryFile)!;
        try
        {
            foreach (string file in Directory.GetFiles(discoveryDir, "bridge-*.json"))
            {
                if (string.Equals(file, _discoveryFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string stem = Path.GetFileNameWithoutExtension(file);
                int dash = stem.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(stem.Substring(dash + 1), out int pid))
                {
                    try
                    {
                        Process.GetProcessById(pid);
                    }
                    catch (ArgumentException)
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        catch (IOException ex)
        {
            ActivityLog.LogWarning(nameof(PipeServerDiscoveryCoordinator), $"Failed to purge stale discovery files: {ex.Message}");
        }
    }

    private void WriteDiscoveryFile(string? solutionPath)
    {
        object record = _runtime.BridgeInstanceService.CreateDiscoveryRecord(solutionPath);

        if (_emitDiscoveryJson)
        {
            try
            {
                string discoveryJson = JsonConvert.SerializeObject(record);
                File.WriteAllText(_discoveryFile, discoveryJson, new UTF8Encoding(false));
            }
            catch (IOException ex)
            {
                ActivityLog.LogWarning(nameof(PipeServerDiscoveryCoordinator), $"Failed to update discovery file: {ex.Message}");
            }
        }

        if (_emitMemoryDiscovery)
        {
            foreach (MemoryDiscoveryStore store in _memoryDiscoveryStores)
            {
                try
                {
                    store.Upsert(record);
                }
                catch (Exception ex) when (ex is not null)
                {
                    ActivityLog.LogWarning(nameof(PipeServerDiscoveryCoordinator), $"Failed to update memory discovery store: {ex.Message}");
                }
            }
        }
    }

    private static string GetSolutionPath(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return dte.Solution?.FullName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? NormalizeSolutionPath(string? solutionPath)
    {
        return string.IsNullOrWhiteSpace(solutionPath) ? null : solutionPath;
    }
}

internal sealed class PipeServerDiscoveryState
{
    public bool WorkerScheduled;
    public bool PurgePending;
    public string? PendingSolutionPath;
}
