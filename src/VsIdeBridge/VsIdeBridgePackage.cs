using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

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
        catch (Exception ex)
        {
            ActivityLog.LogError(nameof(VsIdeBridgePackage), $"Runtime initialization failed: {ex}");
            return;
        }

        try
        {
            await CommandRegistrar.InitializeAsync(this, runtime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ActivityLog.LogError(nameof(VsIdeBridgePackage), $"Command registration failed: {ex}");
            return;
        }

        try
        {
            runtime.BridgeWatchdogService.Start();
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Bridge watchdog failed to start: {ex.Message}");
        }

        // Start named pipe server (best-effort; failure does not break DTE commands)
        try
        {
            _pipeServer = new PipeServerService(this, runtime);
            _pipeServer.Start();
        }
        catch (Exception ex)
        {
            ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Pipe server failed to start: {ex.Message}");
        }

        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                if (await GetServiceAsync(typeof(DTE)).ConfigureAwait(true) is not DTE2 dte)
                {
                    return;
                }

                _dte = dte;
                HookBestPracticeRefreshEvents(dte);

                if (dte.Solution?.IsOpen != true)
                {
                    return;
                }

                var context = new IdeCommandContext(this, dte, runtime.Logger, runtime, CancellationToken.None);
                await runtime.ErrorListService.GetErrorListAsync(
                    context,
                    waitForIntellisense: true,
                    DiagnosticsWarmupTimeoutMilliseconds,
                    query: new ErrorListQuery { Max = 1 }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Diagnostics warmup failed: {ex.Message}");
            }
        });
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

        var events = dte.Events;
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
        QueueBestPracticeRefresh(waitForIntellisense: true);
    }

    private void OnDocumentSaved(Document document)
    {
        QueueBestPracticeRefresh(waitForIntellisense: false);
    }

    private void OnWindowActivated(Window gotFocus, Window lostFocus)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var documentPath = gotFocus?.Document?.FullName;
        if (string.IsNullOrWhiteSpace(documentPath) || string.Equals(documentPath, _lastActivatedDocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastActivatedDocumentPath = documentPath;
        QueueBestPracticeRefresh(waitForIntellisense: false);
    }

    private void QueueBestPracticeRefresh(bool waitForIntellisense)
    {
        var runtime = _runtime;
        var dte = _dte;
        if (runtime is null || dte is null)
        {
            return;
        }

        _bestPracticeRefreshCts?.Cancel();
        _bestPracticeRefreshCts?.Dispose();
        _bestPracticeRefreshCts = new CancellationTokenSource();
        var cancellationToken = _bestPracticeRefreshCts.Token;

        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await Task.Delay(BestPracticeRefreshDebounceMilliseconds, cancellationToken).ConfigureAwait(false);
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (dte.Solution?.IsOpen != true)
                {
                    return;
                }

                var context = new IdeCommandContext(this, dte, runtime.Logger, runtime, cancellationToken);
                if (waitForIntellisense)
                {
                    await runtime.ErrorListService.GetErrorListAsync(
                        context,
                        waitForIntellisense: true,
                        DiagnosticsWarmupTimeoutMilliseconds,
                        query: new ErrorListQuery { Max = 1 }).ConfigureAwait(true);
                    return;
                }

                await runtime.ErrorListService.RefreshBestPracticeDiagnosticsAsync(context).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning(nameof(VsIdeBridgePackage), $"Best practice diagnostics refresh failed: {ex.Message}");
            }
        });
    }
}
