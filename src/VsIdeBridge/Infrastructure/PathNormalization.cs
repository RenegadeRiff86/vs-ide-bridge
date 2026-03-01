using System;
using System.IO;

namespace VsIdeBridge.Infrastructure;

internal static class PathNormalization
{
    public static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool AreEquivalent(string left, string right)
    {
        return string.Equals(
            NormalizeFilePath(left),
            NormalizeFilePath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
