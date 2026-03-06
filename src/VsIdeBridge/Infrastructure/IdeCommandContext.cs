using EnvDTE80;
using System.Threading;
using VsIdeBridge.Services;

namespace VsIdeBridge.Infrastructure;

internal sealed class IdeCommandContext(VsIdeBridgePackage package, DTE2 dte, OutputPaneLogger logger, IdeBridgeRuntime runtime, CancellationToken cancellationToken)
{
    public VsIdeBridgePackage Package { get; } = package;

    public DTE2 Dte { get; } = dte;

    public OutputPaneLogger Logger { get; } = logger;

    public IdeBridgeRuntime Runtime { get; } = runtime;

    public CancellationToken CancellationToken { get; } = cancellationToken;
}
