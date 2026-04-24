using System;
using System.IO;

namespace VsIdeBridge.Shared;

public static class HttpServerStatePaths
{
    private const string ProductDirectoryName = "VsIdeBridge";
    private const string SharedStateDirectoryName = "state";
    private const string HttpEnabledFlagFileName = "http-enabled.flag";

    public static string GetSharedStateDirectory()
    {
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonAppData))
        {
            return Path.Combine(commonAppData, ProductDirectoryName, SharedStateDirectoryName);
        }

        return Path.Combine(Path.GetTempPath(), ProductDirectoryName, SharedStateDirectoryName);
    }

    public static string GetHttpEnabledFlagPath()
    {
        return Path.Combine(GetSharedStateDirectory(), HttpEnabledFlagFileName);
    }
}
