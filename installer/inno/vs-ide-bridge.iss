#define MyAppName "VS IDE Bridge"
#define MyAppFolderName "VsIdeBridge"
#define MyAppPublisher "RenegadeRiff86"
#define MyAppURL "https://github.com/RenegadeRiff86/Visual-Studio-MCP"
#define MyAppVersion "2.2.10"
#define ServiceName "VsIdeBridgeService"
#define VsixId "RenegadeRiff86.VsIdeBridge"
#define LegacyVsixId "StanElston.VsIdeBridge"
#define Configuration "Release"

[Setup]
AppId={{F0B67A29-5A6A-4A0F-AD99-9F8A907A2A2E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppFolderName}
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\output
OutputBaseFilename=vs-ide-bridge-setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\cli\vs-ide-bridge.exe
SetupLogging=yes
CloseApplications=force
CloseApplicationsFilter=vs-ide-bridge.exe,VsIdeBridgeService.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "service"; Description: "Install Windows service (automatic start)"
Name: "startservice"; Description: "Start service after install"; Check: WizardIsTaskSelected('service')

[Files]
Source: "..\..\src\VsIdeBridgeService\bin\{#Configuration}\net8.0-windows\VsIdeBridgeService.exe"; DestDir: "{app}\cli"; DestName: "vs-ide-bridge.exe"; Flags: ignoreversion restartreplace uninsrestartdelete; BeforeInstall: KillCliProcesses
Source: "..\..\src\VsIdeBridgeService\bin\{#Configuration}\net8.0-windows\*"; DestDir: "{app}\cli"; Flags: recursesubdirs createallsubdirs ignoreversion restartreplace uninsrestartdelete; Excludes: "VsIdeBridgeService.exe"
Source: "..\..\src\VsIdeBridgeService\bin\{#Configuration}\net8.0-windows\*"; DestDir: "{app}\service"; Flags: recursesubdirs createallsubdirs ignoreversion restartreplace uninsrestartdelete
Source: "..\..\src\VsIdeBridgeLauncher\bin\{#Configuration}\*"; DestDir: "{app}\service"; Flags: recursesubdirs createallsubdirs ignoreversion restartreplace uninsrestartdelete
Source: "..\..\src\VsIdeBridge\bin\{#Configuration}\net472\VsIdeBridge.vsix"; DestDir: "{app}\vsix"; Flags: ignoreversion
Source: "..\..\src\VsIdeBridgeInstaller\bin\{#Configuration}\net8.0-windows\python-runtime\*"; DestDir: "{app}\python\managed-runtime"; Flags: recursesubdirs createallsubdirs ignoreversion uninsrestartdelete; Check: ShouldInstallManagedPython

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#ServiceName}"""; Flags: runhidden waituntilterminated; RunOnceId: "{#ServiceName}-stop"; StatusMsg: "Stopping VS IDE Bridge service..."
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated; RunOnceId: "{#ServiceName}-delete"; StatusMsg: "Removing VS IDE Bridge service..."
Filename: "{code:GetVsixInstallerPath}"; Parameters: "/quiet /shutdownprocesses /logFile:""{log}\vsix-uninstall.log"" /uninstall:{#VsixId}"; Flags: waituntilterminated; Check: HasVsixInstaller; RunOnceId: "{#VsixId}-uninstall"; StatusMsg: "Removing VS IDE Bridge extension from Visual Studio..."
Filename: "{code:GetVsixInstallerPath}"; Parameters: "/quiet /shutdownprocesses /logFile:""{log}\vsix-uninstall-legacy.log"" /uninstall:{#LegacyVsixId}"; Flags: waituntilterminated; Check: HasVsixInstaller; RunOnceId: "{#LegacyVsixId}-uninstall"; StatusMsg: "Removing previous VS IDE Bridge extension identity..."

[Code]
const
  VisualStudioMajorVersion = '18';
  InstallerLineBreak = #13#10;
  InstallerDoubleLineBreak = #13#10#13#10;
  PostInstallPageTitle = 'Configuring VS IDE Bridge';
  PostInstallPageDescription = 'Running Windows service and Visual Studio extension setup.';
  PythonSupportPageTitle = 'Python Runtime Support';
  PythonSupportPageDescription = 'Choose how VS IDE Bridge should provision Python support.';
  PythonProvisioningManaged = 'managed';
  PythonProvisioningSkip = 'skip';
  VsixInstallerLogArgumentPrefix = '/quiet /shutdownprocesses /logFile:';

var
  CachedVsixInstallerPath: string;
  PostInstallProgressPage: TOutputProgressWizardPage;
  PostInstallCompleted: Boolean;
  PythonSupportPage: TWizardPage;
  ManagedPythonRadioButton: TRadioButton;
  SkipManagedPythonRadioButton: TRadioButton;

function GetVsInstallBasePath(): string;
begin
  Result := ExpandConstant('{pf}\Microsoft Visual Studio') + '\' + VisualStudioMajorVersion;
end;

function EscapeJsonString(const Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function GetSelectedPythonProvisioningMode(): string;
begin
  if (ManagedPythonRadioButton = nil) or ManagedPythonRadioButton.Checked then
    Result := PythonProvisioningManaged
  else
    Result := PythonProvisioningSkip;
end;

function ShouldInstallManagedPython(): Boolean;
begin
  Result := GetSelectedPythonProvisioningMode() = PythonProvisioningManaged;
end;

function GetManagedPythonInterpreterPath(): string;
begin
  Result := ExpandConstant('{app}\python\managed-runtime\python.exe');
end;

function GetManagedPythonRuntimeVersion(): string;
begin
  Result := '';
  GetVersionNumbersString(GetManagedPythonInterpreterPath(), Result);
end;

function GetPythonRuntimeConfigPath(): string;
begin
  Result := ExpandConstant('{app}\config\python-runtime.json');
end;

procedure RemoveManagedPythonRuntimeIfPresent();
begin
  DelTree(ExpandConstant('{app}\python\managed-runtime'), True, True, True);
end;

procedure WritePythonRuntimeConfig();
var
  ConfigDirectory: string;
  ConfigPath: string;
  JsonText: string;
  ManagedPythonPath: string;
  ManagedPythonVersion: string;
begin
  ConfigPath := GetPythonRuntimeConfigPath();
  ConfigDirectory := ExtractFileDir(ConfigPath);
  if ConfigDirectory <> '' then
    ForceDirectories(ConfigDirectory);

  if ShouldInstallManagedPython() then
  begin
    ManagedPythonPath := EscapeJsonString(GetManagedPythonInterpreterPath());
    ManagedPythonVersion := EscapeJsonString(GetManagedPythonRuntimeVersion());
    JsonText :=
      '{' + InstallerLineBreak +
      '  "provisioningMode": "' + PythonProvisioningManaged + '",' + InstallerLineBreak +
      '  "managedRuntimeVersion": "' + ManagedPythonVersion + '",' + InstallerLineBreak +
      '  "managedEnvironmentPath": "' + ManagedPythonPath + '",' + InstallerLineBreak +
      '  "managedBaseInterpreterPath": "' + ManagedPythonPath + '"' + InstallerLineBreak +
      '}';
  end
  else
  begin
    RemoveManagedPythonRuntimeIfPresent();
    JsonText :=
      '{' + InstallerLineBreak +
      '  "provisioningMode": "' + PythonProvisioningSkip + '"' + InstallerLineBreak +
      '}';
  end;

  SaveStringToFile(ConfigPath, JsonText, False);
end;

procedure InitializePythonSupportPage();
var
  ManagedDescription: TNewStaticText;
  SkipDescription: TNewStaticText;
begin
  PythonSupportPage := CreateCustomPage(
    wpSelectTasks,
    PythonSupportPageTitle,
    PythonSupportPageDescription);

  ManagedPythonRadioButton := TRadioButton.Create(PythonSupportPage.Surface);
  ManagedPythonRadioButton.Parent := PythonSupportPage.Surface;
  ManagedPythonRadioButton.Left := ScaleX(0);
  ManagedPythonRadioButton.Top := ScaleY(8);
  ManagedPythonRadioButton.Width := PythonSupportPage.SurfaceWidth;
  ManagedPythonRadioButton.Caption := 'Bridge-managed CPython environment (Recommended)';
  ManagedPythonRadioButton.Checked := True;

  ManagedDescription := TNewStaticText.Create(PythonSupportPage.Surface);
  ManagedDescription.Parent := PythonSupportPage.Surface;
  ManagedDescription.Left := ScaleX(20);
  ManagedDescription.Top := ManagedPythonRadioButton.Top + ScaleY(20);
  ManagedDescription.Width := PythonSupportPage.SurfaceWidth - ScaleX(20);
  ManagedDescription.Height := ScaleY(32);
  ManagedDescription.AutoSize := False;
  ManagedDescription.WordWrap := True;
  ManagedDescription.Caption := 'Install a bridge-owned CPython runtime under the bridge install directory. This keeps bridge modules separate from your working Python environments.';

  SkipManagedPythonRadioButton := TRadioButton.Create(PythonSupportPage.Surface);
  SkipManagedPythonRadioButton.Parent := PythonSupportPage.Surface;
  SkipManagedPythonRadioButton.Left := ScaleX(0);
  SkipManagedPythonRadioButton.Top := ManagedDescription.Top + ManagedDescription.Height + ScaleY(16);
  SkipManagedPythonRadioButton.Width := PythonSupportPage.SurfaceWidth;
  SkipManagedPythonRadioButton.Caption := 'Skip bridge-managed Python';

  SkipDescription := TNewStaticText.Create(PythonSupportPage.Surface);
  SkipDescription.Parent := PythonSupportPage.Surface;
  SkipDescription.Left := ScaleX(20);
  SkipDescription.Top := SkipManagedPythonRadioButton.Top + ScaleY(20);
  SkipDescription.Width := PythonSupportPage.SurfaceWidth - ScaleX(20);
  SkipDescription.Height := ScaleY(32);
  SkipDescription.AutoSize := False;
  SkipDescription.WordWrap := True;
  SkipDescription.Caption := 'Install the bridge without a managed Python runtime. You can still attach an existing interpreter or environment later.';
end;

procedure InitializeWizard();
begin
  PostInstallProgressPage := CreateOutputProgressPage(
    PostInstallPageTitle,
    PostInstallPageDescription);
  InitializePythonSupportPage();
end;

function ResolveVsixInstallerPath(): string;
var
  BasePath: string;
  Candidate: string;
  I: Integer;
  Editions: array[0..3] of string;
  FindRec: TFindRec;
begin
  if CachedVsixInstallerPath <> '' then
  begin
    Result := CachedVsixInstallerPath;
    Exit;
  end;

  Editions[0] := 'Enterprise';
  Editions[1] := 'Professional';
  Editions[2] := 'Community';
  Editions[3] := 'Preview';

  BasePath := GetVsInstallBasePath();

  for I := 0 to 3 do
  begin
    Candidate := BasePath + '\' + Editions[I] + '\Common7\IDE\VSIXInstaller.exe';
    if FileExists(Candidate) then
    begin
      CachedVsixInstallerPath := Candidate;
      Result := Candidate;
      Exit;
    end;
  end;

  if FindFirst(BasePath + '\*\Common7\IDE\VSIXInstaller.exe', FindRec) then
  begin
    try
      repeat
        Candidate := BasePath + '\' + FindRec.Name + '\Common7\IDE\VSIXInstaller.exe';
        if FileExists(Candidate) then
        begin
          CachedVsixInstallerPath := Candidate;
          Result := Candidate;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  Result := '';
end;

function GetVsixInstallerPath(Param: string): string;
begin
  Result := ResolveVsixInstallerPath();
end;

function HasVsixInstaller(): Boolean;
begin
  Result := ResolveVsixInstallerPath() <> '';
end;

function GetServiceName(): string;
begin
  Result := '{#ServiceName}';
end;

function GetServiceBinaryPath(): string;
begin
  Result := ExpandConstant('{app}\service\VsIdeBridgeService.exe');
end;

function GetServiceCommandParameters(const Verb: string): string;
begin
  Result := Format('%s "%s"', [Verb, GetServiceName()]);
end;

function GetServiceCreateParameters(): string;
begin
  Result := Format('create "%s" binPath= "%s" start= auto DisplayName= "VS IDE Bridge Service"', [GetServiceName(), GetServiceBinaryPath()]);
end;

function GetServiceDescriptionParameters(): string;
begin
  Result := Format('description "%s" "VS IDE Bridge service host (automatic start, idle auto-stop)."', [GetServiceName()]);
end;

function GetServiceFailureParameters(): string;
begin
  // Restart after 3s, 10s, 30s — reset failure count after 0s (never).
  // This ensures the service restarts after an idle auto-stop.
  Result := Format('failure "%s" reset= 0 actions= restart/3000/restart/10000/restart/30000', [GetServiceName()]);
end;

function GetServiceFailureFlagParameters(): string;
begin
  // Trigger restart actions even on a clean exit (code 0), not just on crash.
  // Without this, the idle auto-stop (which calls Stop() with code 0) would
  // not trigger the recovery actions above.
  Result := Format('failureflag "%s" 1', [GetServiceName()]);
end;

function GetVsixLogFileArgument(const LogFileName: string): string;
begin
  Result := ExpandConstant(VsixInstallerLogArgumentPrefix + '"{log}\' + LogFileName + '"');
end;

function GetLegacyVsixUninstallParameters(): string;
begin
  Result := GetVsixLogFileArgument('vsix-uninstall-legacy.log') + ' /uninstall:{#LegacyVsixId}';
end;

function GetCurrentVsixUninstallParameters(): string;
begin
  Result := GetVsixLogFileArgument('vsix-uninstall.log') + ' /uninstall:{#VsixId}';
end;

function GetVsixInstallParameters(): string;
begin
  Result := GetVsixLogFileArgument('vsix-install.log') + ' "' + ExpandConstant('{app}\vsix\VsIdeBridge.vsix') + '"';
end;

function RemoveQuotes(const Value: string): string;
begin
  Result := Value;
  if (Length(Result) >= 2) and (Result[1] = '"') and (Result[Length(Result)] = '"') then
  begin
    Delete(Result, Length(Result), 1);
    Delete(Result, 1, 1);
  end;
end;

function GetInstalledUninstallString(): string;
var
  KeyPath: string;
  UninstallString: string;
begin
  KeyPath :=
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1';
  UninstallString := '';

  if not RegQueryStringValue(HKLM, KeyPath, 'UninstallString', UninstallString) then
    RegQueryStringValue(HKCU, KeyPath, 'UninstallString', UninstallString);

  Result := UninstallString;
end;

function UninstallOldVersionIfPresent(): Boolean;
var
  UninstallCommand: string;
  ExitCode: Integer;
begin
  Result := True;
  UninstallCommand := GetInstalledUninstallString();
  if UninstallCommand = '' then
    Exit;

  UninstallCommand := RemoveQuotes(UninstallCommand);
  Log('Previous version detected. Running uninstall command: ' + UninstallCommand);

  if not Exec(
    UninstallCommand,
    '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ExitCode) then
  begin
    Log('Failed to launch previous uninstaller.');
    Result := False;
    Exit;
  end;

  if ExitCode <> 0 then
  begin
    Log(Format('Previous uninstaller exited with code %d.', [ExitCode]));
    Result := False;
    Exit;
  end;

  Log('Previous version uninstall completed successfully.');
end;

function InitializeSetup(): Boolean;
begin
  Result := UninstallOldVersionIfPresent();
end;

procedure KillCliProcesses();
var
  ExitCode: Integer;
  I: Integer;
begin
  for I := 1 to 3 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM vs-ide-bridge.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    if ExitCode = 128 then
      Break;
    Sleep(300);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  // Stop the service gracefully via SCM first so it cannot be auto-restarted
  // while Inno Setup is copying the service binary.  Taskkill alone bypasses
  // the SCM and can lose the race against an immediate SCM restart.
  Log('PrepareToInstall: stopping VsIdeBridgeService via sc.exe...');
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop VsIdeBridgeService', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Log(Format('sc stop VsIdeBridgeService exited with code %d.', [ExitCode]));
  Sleep(2000);  { Give the service time to drain and release its file handles. }

  Log('PrepareToInstall: killing residual vs-ide-bridge.exe / VsIdeBridgeService.exe processes...');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM vs-ide-bridge.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Log(Format('taskkill vs-ide-bridge.exe exited with code %d (0=killed, 128=not running).', [ExitCode]));
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VsIdeBridgeService.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Log(Format('taskkill VsIdeBridgeService.exe exited with code %d.', [ExitCode]));
  Sleep(500);  { Brief pause before Inno Setup proceeds with file copies. }
end;


procedure ShowVsixInstallerMissingMessage();
begin
  Log('VSIXInstaller.exe not found; VSIX install skipped.');
  MsgBox(
    'Visual Studio VSIXInstaller.exe was not found. The VSIX step was skipped.' + InstallerLineBreak +
    'You can install {app}\vsix\VsIdeBridge.vsix manually later.',
    mbInformation,
    MB_OK);
end;

function GetPostInstallStepCount(const HasVsixInstallerPath: Boolean): Integer;
begin
  Result := 1;

  if WizardIsTaskSelected('service') then
    Result := Result + 6;  { stop, delete, create, description, failure, failureflag }

  if WizardIsTaskSelected('startservice') then
    Result := Result + 1;

  if HasVsixInstallerPath then
    Result := Result + 3;
end;

procedure UpdatePostInstallProgress(const Position, Max: Integer; const Status, SubStatus: string);
begin
  PostInstallProgressPage.SetText(Status, SubStatus);

  if Max > 0 then
    PostInstallProgressPage.SetProgress(Position, Max);
end;

function RunInstallerCommand(
  const Status,
  SubStatus,
  Filename,
  Parameters: string;
  const Required: Boolean): Boolean;
var
  ExitCode: Integer;
begin
  Result := True;
  Log(Status + ' [' + SubStatus + ']: ' + Filename + ' ' + Parameters);

  if not Exec(Filename, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Log(Format('%s failed to launch. Result code %d.', [Status, ExitCode]));
    Result := False;
    if Required then
      RaiseException(Format('%s failed to launch. Result code %d.', [Status, ExitCode]));
    Exit;
  end;

  if ExitCode <> 0 then
  begin
    Log(Format('%s exited with code %d.', [Status, ExitCode]));
    Result := False;
    if Required then
      RaiseException(Format('%s failed with exit code %d.', [Status, ExitCode]));
  end;
end;

procedure RunPostInstallStep(
  var StepIndex: Integer;
  const TotalSteps: Integer;
  const Status,
  SubStatus,
  Filename,
  Parameters: string;
  const Required: Boolean);
begin
  UpdatePostInstallProgress(StepIndex, TotalSteps, Status, SubStatus);
  RunInstallerCommand(Status, SubStatus, Filename, Parameters, Required);
  StepIndex := StepIndex + 1;
  UpdatePostInstallProgress(StepIndex, TotalSteps, Status, SubStatus);
end;

procedure RunPythonRuntimeSetupStep(var StepIndex: Integer; const TotalSteps: Integer);
begin
  UpdatePostInstallProgress(
    StepIndex,
    TotalSteps,
    'Configuring bridge Python runtime...',
    GetSelectedPythonProvisioningMode());
  WritePythonRuntimeConfig();
  StepIndex := StepIndex + 1;
  UpdatePostInstallProgress(
    StepIndex,
    TotalSteps,
    'Configuring bridge Python runtime...',
    GetSelectedPythonProvisioningMode());
end;

procedure RunPostInstallActions();
var
  ScPath: string;
  StepIndex: Integer;
  TotalSteps: Integer;
  VsixInstallerPath: string;
begin
  ScPath := ExpandConstant('{sys}\sc.exe');
  VsixInstallerPath := ResolveVsixInstallerPath();
  TotalSteps := GetPostInstallStepCount(VsixInstallerPath <> '');
  StepIndex := 0;

  if TotalSteps > 0 then
  begin
    UpdatePostInstallProgress(0, TotalSteps, PostInstallPageTitle, PostInstallPageDescription);
    PostInstallProgressPage.Show;
  end;

  try
    RunPythonRuntimeSetupStep(StepIndex, TotalSteps);

    if WizardIsTaskSelected('service') then
    begin
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Stopping previous VS IDE Bridge service...',
        'sc stop',
        ScPath,
        GetServiceCommandParameters('stop'),
        False);
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Removing previous VS IDE Bridge service registration...',
        'sc delete',
        ScPath,
        GetServiceCommandParameters('delete'),
        False);
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Registering VS IDE Bridge service...',
        'sc create',
        ScPath,
        GetServiceCreateParameters(),
        True);
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Configuring VS IDE Bridge service...',
        'sc description',
        ScPath,
        GetServiceDescriptionParameters(),
        True);
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Configuring VS IDE Bridge service recovery...',
        'sc failure',
        ScPath,
        GetServiceFailureParameters(),
        False);
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Configuring VS IDE Bridge service recovery flag...',
        'sc failureflag',
        ScPath,
        GetServiceFailureFlagParameters(),
        False);
    end;

    if WizardIsTaskSelected('startservice') then
    begin
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Starting VS IDE Bridge service...',
        'sc start',
        ScPath,
        GetServiceCommandParameters('start'),
        True);
    end;

    if VsixInstallerPath <> '' then
    begin
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Removing current VS IDE Bridge extension identity...',
        'VSIXInstaller /uninstall current',
        VsixInstallerPath,
        GetCurrentVsixUninstallParameters(),
        False);
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Removing previous VS IDE Bridge extension identity...',
        'VSIXInstaller /uninstall legacy',
        VsixInstallerPath,
        GetLegacyVsixUninstallParameters(),
        False);
      RunPostInstallStep(
        StepIndex,
        TotalSteps,
        'Installing VS IDE Bridge extension into Visual Studio...',
        'VSIXInstaller /install',
        VsixInstallerPath,
        GetVsixInstallParameters(),
        True);
    end
    else
    begin
      ShowVsixInstallerMissingMessage();
    end;

    if TotalSteps > 0 then
      UpdatePostInstallProgress(TotalSteps, TotalSteps, 'VS IDE Bridge setup completed.', '');
  finally
    if TotalSteps > 0 then
      PostInstallProgressPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not PostInstallCompleted) then
  begin
    RunPostInstallActions();
    PostInstallCompleted := True;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    if HasVsixInstaller() then
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + InstallerDoubleLineBreak +
        'Visual Studio extension installed: {#VsixId}.'
    else
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + InstallerDoubleLineBreak +
        'Visual Studio extension install was skipped (VSIXInstaller.exe not found).';
  end;
end;












