using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VsIdeBridge.Infrastructure;

internal static class ComRetryHelper
{
    private static readonly int[] RetryableHResults =
    {
        unchecked((int)0x80010001),
        unchecked((int)0x8001010A),
    };

    public static async Task<T> ExecuteAsync<T>(Func<T> func, CancellationToken cancellationToken, int attempts = 20, int delayMilliseconds = 250)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return func();
            }
            catch (COMException ex) when (Array.IndexOf(RetryableHResults, ex.HResult) >= 0 && attempt < attempts)
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static Task ExecuteAsync(Action action, CancellationToken cancellationToken, int attempts = 20, int delayMilliseconds = 250)
    {
        return ExecuteAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken,
            attempts,
            delayMilliseconds);
    }
}
