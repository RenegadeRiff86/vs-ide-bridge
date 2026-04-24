using System;
using System.IO;
using VsIdeBridge.Shared;

namespace VsIdeBridge.Services;

/// <summary>
/// Manages HTTP server state for VS IDE Bridge by reading/writing the shared
/// machine-wide flag file used by VsIdeBridgeService.HttpServerController.
/// </summary>
internal static class HttpServerStateManager
{
    private static readonly string FlagFilePath = HttpServerStatePaths.GetHttpEnabledFlagPath();

    public const int DefaultPort = 8080;
    public static string Url => $"http://localhost:{DefaultPort}/";

    /// <summary>Whether the HTTP server is marked as enabled (flag file exists).</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                return File.Exists(FlagFilePath);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
            catch (NotSupportedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpServerStateManager] Could not read flag file: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Enable the HTTP server by writing the flag file.</summary>
    public static void Enable()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FlagFilePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(FlagFilePath, string.Empty);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to enable HTTP server: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to enable HTTP server: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to enable HTTP server: {ex.Message}", ex);
        }
    }

    /// <summary>Disable the HTTP server by removing the flag file.</summary>
    public static void Disable()
    {
        try
        {
            if (File.Exists(FlagFilePath))
            {
                File.Delete(FlagFilePath);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to disable HTTP server: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to disable HTTP server: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException($"Failed to disable HTTP server: {ex.Message}", ex);
        }
    }
}
