param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$installer = Join-Path $repoRoot "src\VsIdeBridgeInstaller\bin\$Configuration\net8.0-windows\vs-ide-bridge-installer.exe"

if (-not (Test-Path $installer)) {
    throw "Installer EXE not found: $installer. Build it first."
}

& $installer uninstall
exit $LASTEXITCODE
