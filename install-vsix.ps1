$installer = "C:\Program Files\Microsoft Visual Studio\2026\preview\Common7\IDE\VSIXInstaller.exe"
$vsix = "Q:\src\Visual-Studio-MCP\src\VsIdeBridge\bin\Release\net472\VsIdeBridge.vsix"

Write-Host "Installing: $vsix"
Write-Host "Using: $installer"

$proc = Start-Process -FilePath $installer -ArgumentList $vsix -Wait -PassThru
Write-Host "Exit code: $($proc.ExitCode)"

# Verify
$found = Get-ChildItem "C:\Users\henrli\AppData\Local\Microsoft\VisualStudio" -Recurse -Filter "VsIdeBridge.dll" -Depth 5 -ErrorAction SilentlyContinue
if ($found) {
    foreach ($f in $found) { Write-Host "FOUND: $($f.FullName)" }
} else {
    Write-Host "NOT FOUND - install failed"
}
