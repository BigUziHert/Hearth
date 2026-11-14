#ifndef AppVersion
  #define AppVersion "1.0.7"
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

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
begin
  if (CurStep = ssPostInstall) and HasHostService then begin
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\Enable-Hosting.ps1') + '" -InstallDir "' + ExpandConstant('{app}') + '" -UpdateOnly';
    if not ShellExec('runas', ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('Hearth is updated. The hosting service still needs its update. Open Hearth, go to Hosting, and enable/update hosting to finish. Existing pack data has been preserved.', mbInformation, MB_OK);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if HasHostService then
    MsgBox('The independent Hearth hosting service will keep running. To stop hosting, first open Hearth and disable Hosting, or run the installed scripts\Disable-Hosting.ps1 as administrator. Your packs and personal Minecraft files are preserved.', mbInformation, MB_OK);
end;
