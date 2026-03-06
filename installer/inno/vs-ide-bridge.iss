#define MyAppName "VS IDE Bridge"
#define MyAppPublisher "Stan Elston"
#define MyAppURL "https://github.com/RenegadeRiff86/vs-ide-bridge"
#define MyAppVersion "2.0.3"
#define ServiceName "VsIdeBridgeService"
#define VsixId "StanElston.VsIdeBridge"

[Setup]
AppId={{F0B67A29-5A6A-4A0F-AD99-9F8A907A2A2E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\VsIdeBridge
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "service"; Description: "Install Windows service (manual start)"; Flags: checkedonce
Name: "startservice"; Description: "Start service after install"; Flags: unchecked; Check: WizardIsTaskSelected('service')

[Files]
Source: "..\..\src\VsIdeBridgeCli\bin\Release\net8.0\*"; DestDir: "{app}\cli"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\..\src\VsIdeBridgeService\bin\Release\net8.0-windows\*"; DestDir: "{app}\service"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\..\src\VsIdeBridge\bin\Release\net472\VsIdeBridge.vsix"; DestDir: "{app}\vsix"; Flags: ignoreversion

[Run]
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#ServiceName}"""; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{sys}\sc.exe"; Parameters: "create ""{#ServiceName}"" binPath= """"{app}\service\VsIdeBridgeService.exe"" --idle-soft-seconds 900 --idle-hard-seconds 1200"" start= demand DisplayName= ""VS IDE Bridge Service"""; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{sys}\sc.exe"; Parameters: "description ""{#ServiceName}"" ""VS IDE Bridge service host (manual start, idle auto-stop)."""; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{sys}\sc.exe"; Parameters: "start ""{#ServiceName}"""; Flags: runhidden waituntilterminated; Tasks: startservice
Filename: "{code:GetVsixInstallerPath}"; Parameters: "/quiet ""{app}\vsix\VsIdeBridge.vsix"""; Flags: waituntilterminated; Check: HasVsixInstaller

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop ""{#ServiceName}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete ""{#ServiceName}"""; Flags: runhidden waituntilterminated
Filename: "{code:GetVsixInstallerPath}"; Parameters: "/quiet /uninstall:{#VsixId}"; Flags: waituntilterminated; Check: HasVsixInstaller

[Code]
var
  CachedVsixInstallerPath: string;

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

  BasePath := ExpandConstant('{pf}\Microsoft Visual Studio\18');

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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not HasVsixInstaller()) then
  begin
    Log('VSIXInstaller.exe not found; VSIX install skipped.');
    MsgBox('Visual Studio VSIXInstaller.exe was not found. The VSIX step was skipped.'#13#10 +
      'You can install {app}\vsix\VsIdeBridge.vsix manually later.', mbInformation, MB_OK);
  end;
end;
