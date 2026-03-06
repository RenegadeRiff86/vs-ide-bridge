param(
    [string]$IsccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    [string]$ScriptPath = "installer\inno\vs-ide-bridge.iss"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

$iscc = if ([System.IO.Path]::IsPathRooted($IsccPath)) {
    $IsccPath
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $IsccPath))
}

if (-not (Test-Path $iscc)) {
    $fallback = "C:\Program Files\Inno Setup 6\ISCC.exe"
    if (Test-Path $fallback) {
        $iscc = $fallback
    } else {
        throw "ISCC.exe not found. Install Inno Setup 6 first: https://jrsoftware.org/isdl.php"
    }
}

$script = if ([System.IO.Path]::IsPathRooted($ScriptPath)) {
    $ScriptPath
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ScriptPath))
}

if (-not (Test-Path $script)) {
    throw "Inno Setup script not found: $script"
}

& $iscc $script
