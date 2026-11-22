; Inno Setup script for ArcadePcAgent-Setup-<version>.exe.
; Built by the release and CI workflows:
;   ISCC.exe /DAppVersion=0.5.0 /DPublishDir=..\artifacts\publish installer\ArcadePcAgent.iss
;
; Interactive: a wizard with an optional "MQTT broker" page (skip it and use
; the tray icon's Settings later).
;
; Scripted (run elevated, as the user who plays on the PC):
;   ArcadePcAgent-Setup-0.5.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART ^
;     /HOST=homeassistant.local [/PORT=1883] [/TLS=1] [/VERIFY=0] ^
;     [/USERNAME=arcade-pc-agent] [/PASSWORDFILE=C:\path\pw.txt | /PASSWORD=secret] ^
;     [/CAFILE=C:\path\ca.pem] [/NODEID=arcade_pc] [/DEVICENAME="Arcade PC"] [/AREA="Game Room"] ^
;     [/LAUNCHBOXDIR=D:\LaunchBox] [/NOWAIT=1] [/LOG=C:\path\setup.log]
; Without /HOST, existing settings are kept (an update). /PASSWORDFILE is
; deleted after its content is stored; prefer it to /PASSWORD, which other
; processes can read from the command line.
;
; The work after copying files (settings, DPAPI password, scheduled task,
; start, wait for "Connected") is scripts\install-arcade-pc-agent.ps1, the same
; script the SSH installer runs. Its output goes to the setup log (/LOG).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Arcade PC agent"
#define AppExe "ArcadePcAgent.exe"
#define TaskName "Arcade PC agent"
#define RepoUrl "https://github.com/dewab74/ha-arcade-pc-agent"

[Setup]
AppId={{6C1C3B9E-4F3A-4C8B-9E0B-6B8F2D7A51C4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ha-arcade-pc-agent contributors
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={autopf}\ArcadePcAgent
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=ArcadePcAgent-Setup-{#AppVersion}
SetupIconFile=..\src\ArcadePcAgent\Icons\agent.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
SetupLogging=yes

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\install-arcade-pc-agent.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/End /TN ""{#TaskName}"""; Flags: runhidden; RunOnceId: "EndTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden; RunOnceId: "DeleteTask"

[Code]
procedure ExitProcess(ExitCode: Integer);
  external 'ExitProcess@kernel32.dll stdcall';

var
  BrokerPage: TInputQueryWizardPage;
  TlsPage: TInputOptionWizardPage;
  InstallFailed: Boolean;

function Param(const Name, Default: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|' + Default + '}');
end;

function IsOn(const Value: String): Boolean;
begin
  Result := (Value = '1') or (CompareText(Value, 'true') = 0) or (CompareText(Value, 'yes') = 0);
end;

function JsonEscape(const S: String): String;
var
  I: Integer;
  C: Char;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '\' then Result := Result + '\\'
    else if C = '"' then Result := Result + '\"'
    else if Ord(C) < 32 then Result := Result + ' '
    else Result := Result + C;
  end;
end;

procedure AddString(var Json: String; const Key, Value: String);
begin
  if Value <> '' then
    Json := Json + ',' + #13#10 + '  "' + Key + '": "' + JsonEscape(Value) + '"';
end;

procedure InitializeWizard;
begin
  BrokerPage := CreateInputQueryPage(wpLicense, 'MQTT broker',
    'Where should the agent connect? (optional)',
    'Leave the host empty to keep the current settings, or to set them later from the tray icon''s Settings.');
  BrokerPage.Add('Host (e.g. homeassistant.local):', False);
  BrokerPage.Add('Port:', False);
  BrokerPage.Add('Username (empty if the broker has none):', False);
  BrokerPage.Add('Password:', True);
  BrokerPage.Values[0] := Param('HOST', '');
  BrokerPage.Values[1] := Param('PORT', '1883');
  BrokerPage.Values[2] := Param('USERNAME', '');
  BrokerPage.Values[3] := Param('PASSWORD', '');

  TlsPage := CreateInputOptionPage(BrokerPage.ID, 'Encryption',
    'TLS to the broker (optional)',
    'Turn on TLS if your broker listens with TLS (usually port 8883). Turn verification off only for a self-signed certificate.',
    False, False);
  TlsPage.Add('Use TLS');
  TlsPage.Add('Verify the broker''s certificate');
  TlsPage.Values[0] := IsOn(Param('TLS', '0'));
  TlsPage.Values[1] := IsOn(Param('VERIFY', '1'));
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = TlsPage.ID) and (Trim(BrokerPage.Values[0]) = '');
end;

{ Stop a running agent so its exe can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN "{#TaskName}"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1000);
  Result := '';
end;

procedure RunInstaller;
var
  Json, Args, SettingsPath, PasswordPath, OutPath, Host, Password, PasswordFile, CaFile: String;
  Code: Integer;
  Output: TArrayOfString;
  I: Integer;
begin
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\install-arcade-pc-agent.ps1') + '" -ExePath "' + ExpandConstant('{app}\{#AppExe}') + '"';

  Host := Trim(BrokerPage.Values[0]);
  if Host <> '' then
  begin
    Json := '{' + #13#10 + '  "host": "' + JsonEscape(Host) + '"';
    Json := Json + ',' + #13#10 + '  "port": ' + IntToStr(StrToIntDef(Trim(BrokerPage.Values[1]), 1883));
    if TlsPage.Values[0] then Json := Json + ',' + #13#10 + '  "useTls": true'
    else Json := Json + ',' + #13#10 + '  "useTls": false';
    if TlsPage.Values[1] then Json := Json + ',' + #13#10 + '  "verifyCertificate": true'
    else Json := Json + ',' + #13#10 + '  "verifyCertificate": false';
    AddString(Json, 'username', Trim(BrokerPage.Values[2]));
    AddString(Json, 'nodeId', Param('NODEID', ''));
    AddString(Json, 'deviceName', Param('DEVICENAME', ''));
    AddString(Json, 'suggestedArea', Param('AREA', ''));
    AddString(Json, 'launchBoxDir', Param('LAUNCHBOXDIR', ''));
    AddString(Json, 'inputWatchDir', Param('INPUTWATCHDIR', ''));
    AddString(Json, 'muteEntityId', Param('MUTEENTITYID', ''));
    AddString(Json, 'volumeEntityId', Param('VOLUMEENTITYID', ''));
    AddString(Json, 'nasShare', Param('NASSHARE', ''));
    Json := Json + #13#10 + '}' + #13#10;
    SettingsPath := ExpandConstant('{tmp}\agent.json');
    SaveStringToFile(SettingsPath, Json, False);
    Args := Args + ' -SettingsJson "' + SettingsPath + '"';

    CaFile := Param('CAFILE', '');
    if CaFile <> '' then Args := Args + ' -CaPem "' + CaFile + '"';
  end;

  PasswordFile := Param('PASSWORDFILE', '');
  Password := BrokerPage.Values[3];
  if PasswordFile <> '' then
    Args := Args + ' -PasswordFile "' + PasswordFile + '"'
  else if Password <> '' then
  begin
    PasswordPath := ExpandConstant('{tmp}\agent.password.txt');
    SaveStringToFile(PasswordPath, Password, False);
    Args := Args + ' -PasswordFile "' + PasswordPath + '"';
  end;

  if IsOn(Param('NOWAIT', '0')) or (Host = '') then Args := Args + ' -NoWait';

  OutPath := ExpandConstant('{tmp}\install-output.txt');
  Log('Running install-arcade-pc-agent.ps1');
  if not Exec(ExpandConstant('{cmd}'), '/C ""' + ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe') + '" ' + Args + ' > "' + OutPath + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  if LoadStringsFromFile(OutPath, Output) then
    for I := 0 to GetArrayLength(Output) - 1 do
      Log('  ' + Output[I]);
  if Code <> 0 then
  begin
    InstallFailed := True;
    SuppressibleMsgBox('The agent was copied, but setting it up failed (exit code ' + IntToStr(Code) + ').' + #13#10#13#10 +
      'Run setup again with /LOG=<file> for the details, or check %LOCALAPPDATA%\ArcadePcAgent\agent.log.',
      mbError, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RunInstaller;
end;

{ A failed setup step makes setup exit with code 1 even when silent, so scripts can tell. }
procedure DeinitializeSetup;
begin
  if InstallFailed then
    ExitProcess(1);
end;
