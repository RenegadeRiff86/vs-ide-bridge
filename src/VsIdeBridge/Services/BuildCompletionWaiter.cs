using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Threading.Tasks;

namespace VsIdeBridge.Services;

internal sealed class BuildCompletionWaiter : IVsUpdateSolutionEvents
{
    private readonly IVsSolutionBuildManager2 _buildManager;
    private readonly TaskCompletionSource<bool> _completionSource = new();
    private uint _cookie;

    internal Task CompletionTask => _completionSource.Task;

    internal int LastBuildInfo { get; private set; }

    internal BuildCompletionWaiter(IVsSolutionBuildManager2 buildManager)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _buildManager = buildManager;
        _buildManager.AdviseUpdateSolutionEvents(this, out _cookie);
    }

    internal void Unsubscribe()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_cookie != 0)
        {
            _buildManager.UnadviseUpdateSolutionEvents(_cookie);
            _cookie = 0;
        }

        _completionSource.TrySetCanceled();
    }

    int IVsUpdateSolutionEvents.UpdateSolution_Begin(ref int pfCancelUpdate) => VSConstants.S_OK;

    int IVsUpdateSolutionEvents.UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
    {
        LastBuildInfo = fSucceeded != 0 ? 0 : 1;
        _completionSource.TrySetResult(true);
        return VSConstants.S_OK;
    }

    int IVsUpdateSolutionEvents.UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

    int IVsUpdateSolutionEvents.UpdateSolution_Cancel()
    {
        _completionSource.TrySetResult(true);
        return VSConstants.S_OK;
    }

    int IVsUpdateSolutionEvents.OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;
}
