@echo off
setlocal

set "VSIXINSTALLER=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe"
set "EXTENSION_ID=StanElston.VsIdeBridge"
set "VSIX_PATH="

for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "$candidates=@('C:\Users\elsto\source\repos\vs-ide-bridge\src\VsIdeBridge\bin\Debug\net472\VsIdeBridge.vsix','C:\Users\elsto\source\repos\vs-ide-bridge\src\VsIdeBridge\bin\Any CPU\Debug\net472\VsIdeBridge.vsix') | Where-Object { Test-Path $_ }; if ($candidates.Count -gt 0) { $latest = $candidates | Sort-Object { (Get-Item $_).LastWriteTimeUtc } -Descending | Select-Object -First 1; Write-Output $latest }"` ) do (
  set "VSIX_PATH=%%~I"
)

if not exist "%VSIXINSTALLER%" (
  echo ERROR: VSIXInstaller not found: %VSIXINSTALLER%
  exit /b 1
)

if not defined VSIX_PATH (
  echo ERROR: VSIX not found in expected output paths.
  exit /b 1
)

taskkill /F /IM devenv.exe >nul 2>nul
taskkill /F /IM VSIXInstaller.exe >nul 2>nul
taskkill /F /IM MSBuild.exe >nul 2>nul

echo Using VSIX: %VSIX_PATH%

echo Uninstalling %EXTENSION_ID%...
"%VSIXINSTALLER%" /q /shutdownprocesses /u:%EXTENSION_ID%
set "UNINSTALL_EXIT=%ERRORLEVEL%"
echo Uninstall exit code: %UNINSTALL_EXIT%

echo Installing %VSIX_PATH%...
"%VSIXINSTALLER%" /q /shutdownprocesses "%VSIX_PATH%"
set "INSTALL_EXIT=%ERRORLEVEL%"
echo Install exit code: %INSTALL_EXIT%

if not "%INSTALL_EXIT%"=="0" (
  exit /b %INSTALL_EXIT%
)

exit /b %UNINSTALL_EXIT%
