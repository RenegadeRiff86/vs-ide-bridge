using System;
using System.IO;

namespace VsIdeBridgeService.SystemTools;

internal static class ServiceToolPaths
{
    public static string ResolveSolutionDirectory(BridgeConnection bridge)
    {
        string? solutionDirectory = TryGetSolutionDirectory(bridge.CurrentSolutionPath);
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return solutionDirectory;
        }

        BridgeInstance? currentInstance = bridge.CurrentInstance;
        if (currentInstance is not null)
        {
            solutionDirectory = TryGetSolutionDirectory(currentInstance.SolutionPath);
            if (!string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return solutionDirectory;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static string? TryGetSolutionDirectory(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return null;
        }

        string? solutionDirectory = Path.GetDirectoryName(solutionPath);
        if (string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return null;
        }

        return solutionDirectory;
    }
}
