using System.Threading;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VsIdeBridge.Services;

namespace VsIdeBridge.Infrastructure;

internal sealed class IdeCommandContext
{
    public IdeCommandContext(VsIdeBridgePackage package, DTE2 dte, OutputPaneLogger logger, IdeBridgeRuntime runtime, CancellationToken cancellationToken)
    {
        Package = package;
        Dte = dte;
        Logger = logger;
        Runtime = runtime;
        CancellationToken = cancellationToken;
    }

    public VsIdeBridgePackage Package { get; }

    public DTE2 Dte { get; }

    public OutputPaneLogger Logger { get; }

    public IdeBridgeRuntime Runtime { get; }

    public CancellationToken CancellationToken { get; }
}
