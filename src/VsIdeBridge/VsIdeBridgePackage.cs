using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;
using VsIdeBridge.Commands;

namespace VsIdeBridge;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("VS IDE Bridge", "Scriptable IDE control commands for Visual Studio.", "2.0.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid(PackageGuidString)]
public sealed class VsIdeBridgePackage : AsyncPackage, IAsyncDisposable
{
    public const string PackageGuidString = "D8F750B1-5FB7-4A52-8D75-ED5A7F576088";
    private const int DiagnosticsWarmupTimeoutMilliseconds = 30_000;
    private const int BestPracticeRefreshDebounceMilliseconds = 750;

    private IdeBridgeRuntime? _runtime;
    private PipeServerService? _pipeServer;
    private DTE2? _dte;
    private SolutionEvents? _solutionEvents;
    private DocumentEvents? _documentEvents;
    private WindowEvents? _windowEvents;
    private CancellationTokenSource? _bestPracticeRefreshCts;
    private string? _lastActivatedDocumentPath;
    private bool _disposed;
    private bool _noSolutionRequested;

    internal IdeBridgeRuntime Runtime => _runtime ?? throw new InvalidOperationException("Runtime is not initialized.");

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        IdeBridgeRuntime runtime;
        try
        {
            runtime = await IdeBridgeRuntime.CreateAsync(this).ConfigureAwait(false);
            _runtime = runtime;
        }
        catch (Exception ex) when (ex is not null) // top-level package init boundary
        {
            ActivityLog.LogError(nameof(VsIdeBridgePackage), $"Runtime initialization failed: {ex}");
            return;
        }

        try
        {
            runtime.BridgeWatchdogService.Start();
        }
        catch (Exception ex) when (ex is not null) // best-effort watchdog startup
        {
            ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Bridge watchdog failed to start: {ex.Message}");
        }

        // Start named pipe server (best-effort; failure does not break DTE commands)
        try
        {
            _pipeServer = new PipeServerService(this, runtime);
            _pipeServer.Start();
        }
        catch (Exception ex) when (ex is not null) // best-effort pipe server startup
        {
            ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Pipe server failed to start: {ex.Message}");
        }

        try
        {
            await CommandRegistrar.InitializeAsync(this, runtime).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null) // top-level package init boundary
        {
            ActivityLog.LogError(nameof(VsIdeBridgePackage), $"Command registration failed: {ex}");
            return;
        }

        _ = JoinableTaskFactory.RunAsync(() => InitializeDteAsync(runtime));
    }

    protected override void Dispose(bool disposing)
    {
        JoinableTaskFactory.Run(async delegate
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            await DisposeAsync().ConfigureAwait(true);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await JoinableTaskFactory.SwitchToMainThreadAsync();
        _solutionEvents?.Opened -= OnSolutionOpened;
        _documentEvents?.DocumentSaved -= OnDocumentSaved;
        _windowEvents?.WindowActivated -= OnWindowActivated;
        _bestPracticeRefreshCts?.Cancel();
        _bestPracticeRefreshCts?.Dispose();
        _pipeServer?.Dispose();
        _runtime?.BridgeWatchdogService.Dispose();
        base.Dispose(true);
    }

    private void HookBestPracticeRefreshEvents(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        EnvDTE.Events events = dte.Events;
        _solutionEvents ??= events.SolutionEvents;
        _documentEvents ??= events.DocumentEvents;
        _windowEvents ??= events.WindowEvents;

        _solutionEvents.Opened -= OnSolutionOpened;
        _solutionEvents.Opened += OnSolutionOpened;

        _documentEvents.DocumentSaved -= OnDocumentSaved;
        _documentEvents.DocumentSaved += OnDocumentSaved;

        _windowEvents.WindowActivated -= OnWindowActivated;
        _windowEvents.WindowActivated += OnWindowActivated;
    }

    private void OnSolutionOpened()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_noSolutionRequested)
        {
            _noSolutionRequested = false;
            _dte?.Solution?.Close(SaveFirst: false);
            return;
        }
        _pipeServer?.UpdateDiscovery(_dte?.Solution?.FullName ?? string.Empty);
        QueueBestPracticeRefresh(waitForIntellisense: true);
    }

    private void OnDocumentSaved(Document document)
    {
        QueueBestPracticeRefresh(waitForIntellisense: false);
    }

    private void OnWindowActivated(Window gotFocus, Window lostFocus)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string? documentPath = gotFocus?.Document?.FullName;
        if (string.IsNullOrWhiteSpace(documentPath) || string.Equals(documentPath, _lastActivatedDocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastActivatedDocumentPath = documentPath;
        QueueSolutionExplorerSync();
        QueueBestPracticeRefresh(waitForIntellisense: false);
    }

    private void QueueSolutionExplorerSync()
    {
        IdeBridgeRuntime? runtime = _runtime;
        DTE2? dte = _dte;
        if (runtime is null || dte is null)
        {
            return;
        }

        _ = JoinableTaskFactory.RunAsync(
            () => runtime.SolutionExplorerSyncService.TrySyncToActiveDocumentAsync(dte, CancellationToken.None));
    }

    private void QueueBestPracticeRefresh(bool waitForIntellisense)
    {
        IdeBridgeRuntime? runtime = _runtime;
        DTE2? dte = _dte;
        if (runtime is null || dte is null)
        {
            return;
        }

        _bestPracticeRefreshCts?.Cancel();
        _bestPracticeRefreshCts?.Dispose();
        _bestPracticeRefreshCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _bestPracticeRefreshCts.Token;

        _ = JoinableTaskFactory.RunAsync(() => RunBestPracticeRefreshAsync(dte, runtime, waitForIntellisense, cancellationToken));
    }

    private static bool IsBridgeNoSolutionRequested()
    {
        string flagFile = Path.Combine(
            Path.GetTempPath(), "vs-ide-bridge",
            $"bridge-nosolution-{System.Diagnostics.Process.GetCurrentProcess().Id}.flag");
        if (!File.Exists(flagFile))
            return false;
        try
        {
            File.Delete(flagFile);
        }
        catch (IOException ex) { BridgeActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Failed to delete startup flag file '{flagFile}'", ex); }
        catch (UnauthorizedAccessException ex) { BridgeActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Failed to delete startup flag file '{flagFile}'", ex); }
        return true;
    }

    private static string? GetBridgeStartupSolutionPath()
    {
        string flagFile = Path.Combine(
            Path.GetTempPath(), "vs-ide-bridge",
            $"bridge-opensolution-{System.Diagnostics.Process.GetCurrentProcess().Id}.flag");
        if (!File.Exists(flagFile))
            return null;
        try
        {
            string solutionPath = File.ReadAllText(flagFile).Trim();
            File.Delete(flagFile);
            return string.IsNullOrWhiteSpace(solutionPath) ? null : solutionPath;
        }
        catch (IOException ex) { BridgeActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Failed to read startup solution flag '{flagFile}'", ex); return null; }
        catch (UnauthorizedAccessException ex) { BridgeActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Failed to read startup solution flag '{flagFile}'", ex); return null; }
    }

    private async Task InitializeDteAsync(IdeBridgeRuntime runtime)
    {
        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
            if (await GetServiceAsync(typeof(DTE)).ConfigureAwait(false) is not DTE2 dte)
                return;

            if (!InitializeDteCore(dte))
                return;

            IdeCommandContext context = new(this, dte, runtime.Logger, runtime, CancellationToken.None);
            await runtime.ErrorListService.GetErrorListAsync(
                context,
                waitForIntellisense: true,
                DiagnosticsWarmupTimeoutMilliseconds).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null) // best-effort diagnostics warmup
        {
            ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Diagnostics warmup failed: {ex.Message}");
        }
    }

    private bool InitializeDteCore(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _dte = dte;
        Solution? solution = dte.Solution;
        _pipeServer?.UpdateDiscovery(solution?.FullName ?? string.Empty);
        HookBestPracticeRefreshEvents(dte);

        _noSolutionRequested = IsBridgeNoSolutionRequested();
        if (_noSolutionRequested && solution?.IsOpen == true)
            solution.Close(SaveFirst: false);

        string? startupSolutionPath = GetBridgeStartupSolutionPath();
        if (!_noSolutionRequested &&
            solution is { IsOpen: false } &&
            startupSolutionPath is string startupSolutionToOpen &&
            File.Exists(startupSolutionToOpen))
        {
            solution.Open(startupSolutionToOpen);
            _pipeServer?.UpdateDiscovery(solution.FullName ?? startupSolutionToOpen);
        }

        return solution?.IsOpen == true;
    }

    private async Task RunBestPracticeRefreshAsync(DTE2 dte, IdeBridgeRuntime runtime, bool waitForIntellisense, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(BestPracticeRefreshDebounceMilliseconds, cancellationToken).ConfigureAwait(false);
            bool solutionIsOpen;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            solutionIsOpen = dte.Solution?.IsOpen == true;
            await Task.Run(static () => { }, cancellationToken).ConfigureAwait(false);
            if (!solutionIsOpen)
                return;

            IdeCommandContext context = new(this, dte, runtime.Logger, runtime, cancellationToken);
            if (waitForIntellisense)
            {
                await runtime.ErrorListService.GetErrorListAsync(
                    context,
                    waitForIntellisense: true,
                    DiagnosticsWarmupTimeoutMilliseconds).ConfigureAwait(false);
                return;
            }

            await runtime.ErrorListService.RefreshBestPracticeDiagnosticsAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not null) // best-effort diagnostics refresh boundary
        {
            ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Best practice diagnostics refresh failed: {ex.Message}");
        }
    }
}
