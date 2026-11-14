#ifndef AppVersion
  #define AppVersion "1.0.9"
#endif
#define AppName "Hearth"
#define AppExe "Hearth.App.exe"

[Setup]
AppId={{BCE76A47-188D-4568-9332-317986C496C2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Hearth contributors
DefaultDirName={localappdata}\Programs\Hearth
DefaultGroupName=Hearth
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=Hearth-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Hearth.App\Assets\Hearth.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
DisableProgramGroupPage=yes
InfoAfterFile=..\docs\INSTALL-NOTES.txt

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\host\*"; DestDir: "{app}\host"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Enable-Hosting.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "Disable-Hosting.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "Retire-HostingService.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\docs\FIRST-RUN.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\Hearth"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Hearth"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Startup is opt-in inside Hearth. Record cleanup without changing it on install/update.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Hearth"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\hearth"; ValueType: string; ValueName: ""; ValueData: "URL:Hearth pack invite"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\hearth"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\hearth\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\hearth\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open Hearth"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsHearthUpdate

[Code]
function IsHearthUpdate: Boolean;
begin
  Result := ExpandConstant('{param:HEARTHUPDATE|0}') = '1';
end;

function HasHostService: Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Services\HearthHost');
end;

function RetireHostService(const ScriptPath: String): String;
var
  ResultCode: Integer;
  Params, SidPath, OwnerSid: String;
  SidText: AnsiString;
begin
  Result := '';
  SidPath := ExpandConstant('{tmp}\hearth-owner-sid.txt');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -GetCurrentUserSid -SidFile "' + SidPath + '"';
  if not ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    Result := 'Could not identify the Windows account that owns Hearth. The installation has not been changed.';
    Exit;
  end;
  if (ResultCode <> 0) or not LoadStringFromFile(SidPath, SidText) then begin
    Result := 'Could not identify the Windows account that owns Hearth. The installation has not been changed.';
    Exit;
  end;
  OwnerSid := Trim(String(SidText));
  DeleteFile(SidPath);
  if (Pos('S-1-5-21-', OwnerSid) <> 1) and (Pos('S-1-12-1-', OwnerSid) <> 1) then begin
    Result := 'The Windows account could not be verified. The installation has not been changed.';
    Exit;
  end;
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -OwnerSid "' + OwnerSid + '"';
  if not ShellExec('runas', ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    Result := 'The old background host needs a one-time Windows administrator approval to stop it and preserve its packs for Hearth. Approval was canceled or could not start. The installation has not been changed; the existing host may still be running.';
    Exit;
  end;
  if (ResultCode <> 0) or HasHostService then begin
    Result := 'Windows could not finish retiring the old background host. The installation has not been changed and your pack data was preserved. Close the Windows Services window and retry. If this continues, run scripts\Retire-HostingService.ps1 with your Windows user SID as administrator, then retry.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if HasHostService then begin
    ExtractTemporaryFile('Retire-HostingService.ps1');
    Result := RetireHostService(ExpandConstant('{tmp}\Retire-HostingService.ps1'));
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  Params: String;
begin
  Result := True;
  if HasHostService then begin
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\Disable-Hosting.ps1') + '"';
    if not ShellExec('runas', ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
      MsgBox('Windows approval to stop the old independent host was canceled or could not start. Uninstall was canceled; the existing host may still be running. Your data was preserved.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if (ResultCode <> 0) or HasHostService then begin
      MsgBox('Windows could not finish removing the old independent host. Close the Windows Services window and retry. Uninstall was canceled so a host is not left behind. Your data was preserved.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
