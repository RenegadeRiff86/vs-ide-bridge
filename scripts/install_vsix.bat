@echo off
setlocal

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

set "ROOT=%~dp0.."
set "TARGET_FRAMEWORK=net472"
set "VSIX=%ROOT%\src\VsIdeBridge\bin\%CONFIG%\%TARGET_FRAMEWORK%\VsIdeBridge.vsix"
set "VSIXINSTALLER=%ProgramFiles%\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe"
set "LOGFILE=dd_VsIdeBridge_install.log"

if not exist "%VSIX%" (
    echo VSIX not found: "%VSIX%"
    echo Build it first with scripts\build_vsix.bat %CONFIG%
    exit /b 1
)

if not exist "%VSIXINSTALLER%" (
    echo VSIXInstaller not found: "%VSIXINSTALLER%"
    exit /b 1
)

echo Installing "%VSIX%"
echo VSIX installer log: %TEMP%\%LOGFILE%
"%VSIXINSTALLER%" /quiet /shutdownprocesses "%VSIX%" /logFile:"%LOGFILE%"
set "RC=%ERRORLEVEL%"
if %RC%==0 (
    echo Done - installed successfully.
) else (
    echo Failed - exit code %RC%. Check %TEMP%\%LOGFILE% for details.
)
exit /b %RC%
