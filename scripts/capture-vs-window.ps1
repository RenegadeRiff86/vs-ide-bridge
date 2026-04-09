param(
    [string]$OutputPath,

    [string]$WindowTitleContains = "",

    [int]$ProcessId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$User32DllName = "user32.dll"
$DllImportAttribute = "[DllImport(`"$User32DllName`") ]".Replace(" )", ")")
$DllImportSetLastErrorAttribute = "[DllImport(`"$User32DllName`", SetLastError = true)]"

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NativeWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    $DllImportAttribute
    public static extern IntPtr GetForegroundWindow();

    $DllImportAttribute
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    $DllImportSetLastErrorAttribute
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    $DllImportAttribute
    public static extern bool BringWindowToTop(IntPtr hWnd);

    $DllImportAttribute
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    $DllImportAttribute
    public static extern bool IsIconic(IntPtr hWnd);

    $DllImportAttribute
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    $DllImportSetLastErrorAttribute
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
"@

function Get-VisualStudioCandidateProcesses {
    $processes = Get-Process -Name "devenv" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 }

    if ($ProcessId -gt 0) {
        $processes = $processes | Where-Object { $_.Id -eq $ProcessId }
    }

    if (-not [string]::IsNullOrWhiteSpace($WindowTitleContains)) {
        $processes = $processes | Where-Object { $_.MainWindowTitle -like "*$WindowTitleContains*" }
    }

    return @($processes)
}

function Select-VisualStudioWindow {
    $candidates = @(Get-VisualStudioCandidateProcesses)
    if ($candidates.Count -eq 0) {
        throw "No Visual Studio window matched the requested filters."
    }

    $foregroundHandle = [NativeWindowCapture]::GetForegroundWindow()
    if ($foregroundHandle -ne [IntPtr]::Zero) {
        [uint32]$foregroundProcessId = 0
        [void][NativeWindowCapture]::GetWindowThreadProcessId($foregroundHandle, [ref]$foregroundProcessId)
        $foregroundMatch = $candidates | Where-Object { $_.Id -eq [int]$foregroundProcessId } | Select-Object -First 1
        if ($null -ne $foregroundMatch) {
            return $foregroundMatch
        }
    }

    return $candidates | Sort-Object StartTime -Descending | Select-Object -First 1
}

function Resolve-OutputPath {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($OutputPath))
    }

    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $outputDirectory = Join-Path $repoRoot "artifacts\screenshots"
    $fileName = "vs-window-{0}.png" -f (Get-Date -Format "yyyyMMdd-HHmmss")
    return Join-Path $outputDirectory $fileName
}

function Raise-WindowToFront {
    param(
        [System.Diagnostics.Process]$Process
    )

    $handle = [IntPtr]$Process.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) {
        throw "The selected Visual Studio process does not have a main window handle."
    }

    $swRestore = 9
    $hwndTopMost = [IntPtr](-1)
    $hwndNoTopMost = [IntPtr](-2)
    $swpNoMove = 0x0002
    $swpNoSize = 0x0001
    $swpShowWindow = 0x0040
    $flags = $swpNoMove -bor $swpNoSize -bor $swpShowWindow

    if ([NativeWindowCapture]::IsIconic($handle)) {
        [void][NativeWindowCapture]::ShowWindow($handle, $swRestore)
    }

    [void][NativeWindowCapture]::SetWindowPos($handle, $hwndTopMost, 0, 0, 0, 0, $flags)
    [void][NativeWindowCapture]::BringWindowToTop($handle)
    [void][NativeWindowCapture]::SetForegroundWindow($handle)
    [void][NativeWindowCapture]::SetWindowPos($handle, $hwndNoTopMost, 0, 0, 0, 0, $flags)

    Start-Sleep -Milliseconds 150
}

$targetProcess = Select-VisualStudioWindow
$targetProcess.Refresh()
Raise-WindowToFront -Process $targetProcess
$handle = [IntPtr]$targetProcess.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) {
    throw "The selected Visual Studio process does not have a main window handle."
}

$rect = New-Object NativeWindowCapture+RECT
if (-not [NativeWindowCapture]::GetWindowRect($handle, [ref]$rect)) {
    throw "Failed to read the Visual Studio window bounds."
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "The Visual Studio window bounds are invalid: ${width}x${height}."
}

$resolvedOutputPath = Resolve-OutputPath
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($resolvedOutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output ("Captured Visual Studio window: {0}" -f $targetProcess.MainWindowTitle)
Write-Output ("Saved screenshot to: {0}" -f $resolvedOutputPath)
