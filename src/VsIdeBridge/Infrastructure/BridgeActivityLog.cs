using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.Shell;

namespace VsIdeBridge.Infrastructure;

internal static class BridgeActivityLog
{
    private const string InstallFolderName = "VsIdeBridge";
    private const string LogsFolderName = "logs";

    public static void LogWarning(string source, string context, Exception ex)
    {
        ActivityLog.LogWarning(source, $"{context}: {ex.Message}");
        Debug.WriteLine($"{source} warning: {context}: {ex}");
        WriteToFile("WARNING", source, context, ex.ToString());
    }

    private static void WriteToFile(string level, string source, string context, string detail)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                InstallFolderName,
                LogsFolderName);
            Directory.CreateDirectory(logDir);
            string fileName = $"vs-ide-bridge-{DateTime.Now:yyyy-MM-dd}.log";
            string logPath = Path.Combine(logDir, fileName);
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{source}] {context}{Environment.NewLine}{detail}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch (IOException ex)
        {
            // Logging must never crash the host process; fall back to the debugger output only.
            System.Diagnostics.Debug.WriteLine($"BridgeActivityLog.WriteToFile failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"BridgeActivityLog.WriteToFile failed: {ex.Message}");
        }
    }
}
