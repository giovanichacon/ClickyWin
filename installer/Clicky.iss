; Clicky for Windows installer (Inno Setup 6). Build with installer\build.ps1, which
; publishes the self-contained app to publish-dist\ first and passes the version in.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{B185ACA9-834F-4B3D-B533-0F172DBC6AA2}
AppName=Clicky
AppVersion={#AppVersion}
AppVerName=Clicky {#AppVersion}
AppPublisher=Giovani Chacon
DefaultDirName={localappdata}\Programs\Clicky
DefaultGroupName=Clicky
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Clicky-{#AppVersion}-Setup
SetupIconFile=..\Assets\Clicky.ico
UninstallDisplayIcon={app}\ClickyWindows.exe
UninstallDisplayName=Clicky
WizardStyle=modern dark
WizardSmallImageFile=..\Assets\Clicky-256.png
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; A running Clicky (tray app) is closed by [Code] below before files are replaced or removed
CloseApplications=force

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStartProgram,Clicky}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish-dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Clicky"; Filename: "{app}\ClickyWindows.exe"
Name: "{group}\Clicky Settings"; Filename: "{app}\ClickyWindows.exe"; Parameters: "--settings"
Name: "{autodesktop}\Clicky"; Filename: "{app}\ClickyWindows.exe"; Tasks: desktopicon

[Registry]
; Same value the app writes from Settings > General > Start with Windows (Helpers\Autostart.cs)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Clicky"; ValueData: """{app}\ClickyWindows.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ClickyWindows.exe"; Description: "{cm:LaunchProgram,Clicky}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app can also turn autostart on from Settings, so remove the value even without the task
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Clicky /f"; Flags: runhidden; RunOnceId: "NoAutostart"

[UninstallDelete]
; User settings and keys in %APPDATA%\ClickyWindows are kept so a reinstall keeps them.
Type: filesandordirs; Name: "{app}"

[Code]
// Clicky lives in the tray with no window to close, so stop it before installing or uninstalling.
procedure StopClicky();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ClickyWindows.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopClicky();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopClicky();
  Result := True;
end;
