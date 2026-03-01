param(
    [string]$SolutionPath,

    [Parameter(Mandatory = $true)]
    [string]$CommandName,

    [string]$CommandArgs = "",

    [string]$OutputPath,

    [int]$StartupTimeoutSeconds = 60,

    [int]$CommandTimeoutSeconds = 120,

    [bool]$ReuseVisualStudio = $true,

    [switch]$CloseVisualStudio
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $safeName = ($CommandName -replace "[^A-Za-z0-9._-]", "_")
    $OutputPath = Join-Path $env:TEMP "vs-ide-bridge\$safeName.json"
}

$solutionFullPath = $null
if (-not [string]::IsNullOrWhiteSpace($SolutionPath)) {
    $solutionFullPath = [System.IO.Path]::GetFullPath($SolutionPath)
    if (-not (Test-Path -LiteralPath $solutionFullPath)) {
        throw "Solution not found: $solutionFullPath"
    }
}

$outputFullPath = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($OutputPath))
$outputDirectory = [System.IO.Path]::GetDirectoryName($outputFullPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

if (Test-Path -LiteralPath $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Force
}

$devenvPath = Join-Path ${env:ProgramFiles} "Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
if (-not (Test-Path -LiteralPath $devenvPath)) {
    $devenvPath = Join-Path ${env:ProgramFiles} "Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
}
if (-not (Test-Path -LiteralPath $devenvPath)) {
    throw "devenv.exe not found."
}

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

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

public sealed class OleMessageFilter : IOleMessageFilter
{
    [DllImport("Ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

    public static void Register()
    {
        IOleMessageFilter oldFilter;
        CoRegisterMessageFilter(new OleMessageFilter(), out oldFilter);
    }

    public static void Revoke()
    {
        IOleMessageFilter oldFilter;
        CoRegisterMessageFilter(null, out oldFilter);
    }

    public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
    {
        return 0;
    }

    public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        if (dwRejectType == 2)
        {
            return 250;
        }

        return -1;
    }

    public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
    {
        return 2;
    }
}
"@

function Test-IsRetryableComException {
    param(
        [Parameter(Mandatory = $true)]
        [System.Exception]$Exception
    )

    if ($Exception -isnot [System.Runtime.InteropServices.COMException]) {
        return $false
    }

    return $Exception.HResult -eq -2147418111 -or $Exception.HResult -eq -2147417846
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [int]$TimeoutSeconds = 30,

        [int]$DelayMilliseconds = 500
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastException = $null

    while ($true) {
        try {
            return & $Action
        }
        catch {
            $lastException = $_.Exception
            if (Test-IsRetryableComException -Exception $lastException -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds $DelayMilliseconds
                continue
            }

            throw "Failed to $Description. $($lastException.Message)"
        }
    }
}

function Get-SolutionFullName {
    param(
        [Parameter(Mandatory = $true)]
        $Dte
    )

    return Invoke-WithRetry -Description "read the solution path" -TimeoutSeconds 10 -Action {
        if ($null -eq $Dte.Solution) {
            return ""
        }

        return $Dte.Solution.FullName
    }
}

function Get-RunningDteInstances {
    $prefix = "!VisualStudio.DTE.18.0"
    $displayNames = [RunningObjectTableHelper]::GetDisplayNames($prefix)
    $instances = @()

    foreach ($displayName in $displayNames) {
        try {
            $dte = [RunningObjectTableHelper]::GetByDisplayName($displayName)
            if ($null -eq $dte) {
                continue
            }

            $solutionName = Get-SolutionFullName -Dte $dte
            $instances += [PSCustomObject]@{
                DisplayName = $displayName
                Dte = $dte
                SolutionPath = if ([string]::IsNullOrWhiteSpace($solutionName)) { "" } else { [System.IO.Path]::GetFullPath($solutionName) }
            }
        }
        catch {
        }
    }

    return $instances
}

function Wait-ForTargetDte {
    param(
        [string]$DesiredSolutionPath,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $instances = Get-RunningDteInstances
        if ([string]::IsNullOrWhiteSpace($DesiredSolutionPath)) {
            if ($instances.Count -gt 0) {
                return $instances[0]
            }
        }
        else {
            $match = $instances | Where-Object { $_.SolutionPath -and $_.SolutionPath.Equals($DesiredSolutionPath, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            if ($null -ne $match) {
                return $match
            }
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for a Visual Studio 18 DTE instance."
}

function Start-VisualStudio {
    param(
        [string]$SolutionToOpen
    )

    $argumentList = @()
    if (-not [string]::IsNullOrWhiteSpace($SolutionToOpen)) {
        $argumentList += """$SolutionToOpen"""
    }

    Start-Process -FilePath $devenvPath -ArgumentList $argumentList | Out-Null
}

function Open-SolutionIfNeeded {
    param(
        [Parameter(Mandatory = $true)]
        $Dte,

        [string]$DesiredSolutionPath
    )

    if ([string]::IsNullOrWhiteSpace($DesiredSolutionPath)) {
        return
    }

    $currentSolution = Get-SolutionFullName -Dte $Dte
    if (-not [string]::IsNullOrWhiteSpace($currentSolution) -and
        [System.IO.Path]::GetFullPath($currentSolution).Equals($DesiredSolutionPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    Invoke-WithRetry -Description "open the target solution" -TimeoutSeconds $StartupTimeoutSeconds -Action {
        $Dte.UserControl = $true
        $Dte.Solution.Open($DesiredSolutionPath)
    } | Out-Null
}

function Get-ResolvedCommandNames {
    param(
        [Parameter(Mandatory = $true)]
        $Dte,

        [Parameter(Mandatory = $true)]
        [string]$RequestedName
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $ordered = [System.Collections.Generic.List[string]]::new()
    $candidates = [System.Collections.Generic.List[string]]::new()

    $candidates.Add($RequestedName) | Out-Null
    if ($RequestedName -like "Tools.*" -and $RequestedName -notlike "Tools.Tools.*") {
        $candidates.Add(($RequestedName -replace "^Tools\.", "Tools.Tools.")) | Out-Null
    }

    foreach ($candidate in $candidates) {
        try {
            $command = Invoke-WithRetry -Description "resolve command '$candidate'" -TimeoutSeconds 5 -Action {
                $Dte.Commands | Where-Object { $_.Name -eq $candidate } | Select-Object -First 1
            }

            if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Name) -and $seen.Add($command.Name)) {
                $ordered.Add($command.Name) | Out-Null
            }
        }
        catch {
        }
    }

    return $ordered
}

function Resolve-CommandName {
    param(
        [Parameter(Mandatory = $true)]
        $Dte,

        [Parameter(Mandatory = $true)]
        [string]$RequestedName,

        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $names = @(Get-ResolvedCommandNames -Dte $Dte -RequestedName $RequestedName)
        if ($names.Count -gt 0) {
            return $names[0]
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out resolving Visual Studio command '$RequestedName'."
}

function Invoke-DteCommand {
    param(
        [Parameter(Mandatory = $true)]
        $Dte,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Arguments,

        [int]$TimeoutSeconds
    )

    $resolvedName = Resolve-CommandName -Dte $Dte -RequestedName $Name -TimeoutSeconds $TimeoutSeconds
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastException = $null

    while ((Get-Date) -lt $deadline) {
        try {
            $Dte.ExecuteCommand($resolvedName, $Arguments)
            return
        }
        catch {
            $lastException = $_.Exception
            $message = $lastException.Message
            if ((Test-IsRetryableComException -Exception $lastException) -or
                $message -like "*not available*" -or
                $message -like "*cannot be executed*") {
                Start-Sleep -Milliseconds 500
                continue
            }

            throw
        }
    }

    if ($null -ne $lastException) {
        throw "Timed out invoking '$resolvedName'. Last error: $($lastException.Message)"
    }

    throw "Timed out invoking '$resolvedName'."
}

function Wait-ForOutputFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $previousLength = -1
    $stableSamples = 0

    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            try {
                $item = Get-Item -LiteralPath $Path -ErrorAction Stop
                if ($item.Length -gt 0) {
                    if ($item.Length -eq $previousLength) {
                        $stableSamples++
                    }
                    else {
                        $stableSamples = 0
                        $previousLength = $item.Length
                    }

                    if ($stableSamples -ge 2) {
                        return
                    }
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for output file: $Path"
}

$startedVisualStudio = $false

try {
    [OleMessageFilter]::Register()

    $target = $null
    $instances = Get-RunningDteInstances

    if (-not [string]::IsNullOrWhiteSpace($solutionFullPath)) {
        $target = $instances | Where-Object { $_.SolutionPath -and $_.SolutionPath.Equals($solutionFullPath, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ($null -eq $target -and $ReuseVisualStudio) {
            $target = $instances | Where-Object { [string]::IsNullOrWhiteSpace($_.SolutionPath) } | Select-Object -First 1
        }
    }
    elseif ($ReuseVisualStudio -and $instances.Count -gt 0) {
        $target = $instances[0]
    }

    if ($null -eq $target) {
        Start-VisualStudio -SolutionToOpen $solutionFullPath
        $startedVisualStudio = $true
        $target = Wait-ForTargetDte -DesiredSolutionPath $solutionFullPath -TimeoutSeconds $StartupTimeoutSeconds
    }

    Open-SolutionIfNeeded -Dte $target.Dte -DesiredSolutionPath $solutionFullPath

    if (-not [string]::IsNullOrWhiteSpace($solutionFullPath) -and [string]::IsNullOrWhiteSpace($target.SolutionPath)) {
        $target = Wait-ForTargetDte -DesiredSolutionPath $solutionFullPath -TimeoutSeconds $StartupTimeoutSeconds
    }

    $fullCommandArgs = if ($null -eq $CommandArgs) { "" } else { $CommandArgs.Trim() }
    if ($fullCommandArgs.Length -gt 0) {
        $fullCommandArgs += " "
    }
    $fullCommandArgs += "--out ""$outputFullPath"""

    Invoke-DteCommand -Dte $target.Dte -Name $CommandName -Arguments $fullCommandArgs -TimeoutSeconds $CommandTimeoutSeconds
    Wait-ForOutputFile -Path $outputFullPath -TimeoutSeconds $CommandTimeoutSeconds

    Write-Host "Wrote $outputFullPath"
}
finally {
    try {
        if ($CloseVisualStudio.IsPresent -and $null -ne $target -and $null -ne $target.Dte) {
            Invoke-WithRetry -Description "close Visual Studio" -TimeoutSeconds 15 -Action {
                $target.Dte.Quit()
            } | Out-Null
        }
    }
    finally {
        [OleMessageFilter]::Revoke()
    }
}
