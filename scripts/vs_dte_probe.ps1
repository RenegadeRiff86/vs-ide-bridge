$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RunningObjectTableHelper
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    public static string[] GetDisplayNames(string prefix)
    {
        var names = new List<string>();
        IRunningObjectTable rot;
        IEnumMoniker enumMoniker;

        GetRunningObjectTable(0, out rot);
        rot.EnumRunning(out enumMoniker);
        enumMoniker.Reset();

        IMoniker[] monikers = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;

        while (enumMoniker.Next(1, monikers, fetched) == 0)
        {
            IBindCtx bindContext;
            string displayName;
            CreateBindCtx(0, out bindContext);
            monikers[0].GetDisplayName(bindContext, null, out displayName);
            if (prefix == null || displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(displayName);
            }
        }

        return names.ToArray();
    }

    public static object GetByDisplayName(string displayName)
    {
        IRunningObjectTable rot;
        IEnumMoniker enumMoniker;

        GetRunningObjectTable(0, out rot);
        rot.EnumRunning(out enumMoniker);
        enumMoniker.Reset();

        IMoniker[] monikers = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;

        while (enumMoniker.Next(1, monikers, fetched) == 0)
        {
            IBindCtx bindContext;
            string currentDisplayName;
            object runningObject;

            CreateBindCtx(0, out bindContext);
            monikers[0].GetDisplayName(bindContext, null, out currentDisplayName);
            if (string.Equals(currentDisplayName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                rot.GetObject(monikers[0], out runningObject);
                return runningObject;
            }
        }

        return null;
    }
}
"@

$instances = @()
foreach ($displayName in [RunningObjectTableHelper]::GetDisplayNames("!VisualStudio.DTE.18.0")) {
    try {
        $dte = [RunningObjectTableHelper]::GetByDisplayName($displayName)
        if ($null -eq $dte) {
            continue
        }

        $instances += [PSCustomObject]@{
            DisplayName = $displayName
            SolutionPath = $dte.Solution.FullName
            ActiveWindow = $dte.ActiveWindow.Caption
            ActiveDocument = $dte.ActiveDocument.FullName
            DebugMode = $dte.Debugger.CurrentMode.ToString()
        }
    }
    catch {
        $instances += [PSCustomObject]@{
            DisplayName = $displayName
            SolutionPath = ""
            ActiveWindow = ""
            ActiveDocument = ""
            DebugMode = "ERROR: $($_.Exception.Message)"
        }
    }
}

if ($instances.Count -eq 0) {
    Write-Host "No Visual Studio 18 DTE instances found."
    exit 0
}

$instances | Format-Table -AutoSize
