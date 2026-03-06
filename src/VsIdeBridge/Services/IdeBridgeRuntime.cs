using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class IdeBridgeRuntime
{
    private IdeBridgeRuntime(
        OutputPaneLogger logger,
        BridgeInstanceService bridgeInstanceService,
        BridgeUiSettingsService uiSettings,
        BridgeWatchdogService bridgeWatchdogService,
        IdeStateService ideStateService,
        FailureContextService failureContextService,
        ReadinessService readinessService,
        SearchService searchService,
        DocumentService documentService,
        WindowService windowService,
        VsCommandService vsCommandService,
        PatchService patchService,
        BreakpointService breakpointService,
        DebuggerService debuggerService,
        BuildService buildService,
        ErrorListService errorListService)
    {
        Logger = logger;
        BridgeInstanceService = bridgeInstanceService;
        UiSettings = uiSettings;
        BridgeWatchdogService = bridgeWatchdogService;
        IdeStateService = ideStateService;
        FailureContextService = failureContextService;
        ReadinessService = readinessService;
        SearchService = searchService;
        DocumentService = documentService;
        WindowService = windowService;
        VsCommandService = vsCommandService;
        PatchService = patchService;
        BreakpointService = breakpointService;
        DebuggerService = debuggerService;
        BuildService = buildService;
        ErrorListService = errorListService;
    }

    public OutputPaneLogger Logger { get; }

    public BridgeInstanceService BridgeInstanceService { get; }

    public BridgeUiSettingsService UiSettings { get; }

    public BridgeWatchdogService BridgeWatchdogService { get; }

    public IdeStateService IdeStateService { get; }

    public FailureContextService FailureContextService { get; }

    public ReadinessService ReadinessService { get; }

    public SearchService SearchService { get; }

    public DocumentService DocumentService { get; }

    public WindowService WindowService { get; }

    public VsCommandService VsCommandService { get; }

    public PatchService PatchService { get; }

    public BreakpointService BreakpointService { get; }

    public DebuggerService DebuggerService { get; }

    public BuildService BuildService { get; }

    public ErrorListService ErrorListService { get; }

    private readonly Dictionary<string, IdeCommandBase> _dispatcher =
        new(StringComparer.OrdinalIgnoreCase);

    internal void RegisterCommand(IdeCommandBase cmd)
    {
        if (!cmd.AllowAutomationInvocation)
        {
            return;
        }

        _dispatcher[cmd.Name] = cmd;
        foreach (var alias in PipeCommandNames.GetAliases(cmd.Name))
        {
            _dispatcher[alias] = cmd;
        }
    }

    internal bool TryGetCommand(string name, out IdeCommandBase cmd)
        => _dispatcher.TryGetValue(name, out cmd!);

    public static Task<IdeBridgeRuntime> CreateAsync(VsIdeBridgePackage package)
    {
        var logger = new OutputPaneLogger(package);
        var bridgeInstanceService = new BridgeInstanceService();
        var uiSettings = new BridgeUiSettingsService(package);
        var documentService = new DocumentService(package);
        var failureContextService = new FailureContextService();
        var readinessService = new ReadinessService();
        var searchService = new SearchService();
        var errorListService = new ErrorListService(readinessService);
        var buildService = new BuildService(readinessService);
        var bridgeWatchdogService = new BridgeWatchdogService(package);

        var runtime = new IdeBridgeRuntime(
            logger,
            bridgeInstanceService,
            uiSettings,
            bridgeWatchdogService,
            new IdeStateService(bridgeInstanceService, bridgeWatchdogService),
            failureContextService,
            readinessService,
            searchService,
            documentService,
            new WindowService(),
            new VsCommandService(),
            new PatchService(),
            new BreakpointService(),
            new DebuggerService(),
            buildService,
            errorListService);
        return Task.FromResult(runtime);
    }
}
