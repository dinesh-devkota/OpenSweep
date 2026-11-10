; SqueakySift Setup – per-user installer (no administrator rights needed).
; Build: ISCC.exe /DAppVersion=1.4.0 installer\OpenSweep.iss   (expects dist\SqueakySift.exe)

#ifndef AppVersion
  #define AppVersion "1.4.0"
#endif

[Setup]
; Keep this AppId: SelfInstaller looks for "{AppId}_is1" to detect a Setup install.
AppId={{6E2B4F1A-3C7D-4E8B-9A15-0F5D2C8B7A31}
AppName=SqueakySift
AppVersion={#AppVersion}
AppVerName=SqueakySift {#AppVersion}
AppPublisher=SqueakySift contributors
AppPublisherURL=https://github.com/dinesh-devkota/SqueakySift
AppSupportURL=https://github.com/dinesh-devkota/SqueakySift/issues
AppUpdatesURL=https://github.com/dinesh-devkota/SqueakySift/releases
AppComments=Open-source Windows maintenance, cleanup previews, startup management and updates
VersionInfoVersion={#AppVersion}
VersionInfoProductName=SqueakySift
VersionInfoDescription=SqueakySift Setup
PrivilegesRequired=lowest
; Retain the installed target so existing schedules and taskbar pins continue to work.
DefaultDirName={autopf}\OpenSweep
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\src\OpenSweep\Assets\SqueakySift.ico
UninstallDisplayIcon={app}\OpenSweep.exe
UninstallDisplayName=SqueakySift
OutputDir=..\dist
OutputBaseFilename=SqueakySift-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\SqueakySift.exe"; DestDir: "{app}"; DestName: "OpenSweep.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SqueakySift"; Filename: "{app}\OpenSweep.exe"; Comment: "Review cleanup, manage startup and keep Windows up to date"
Name: "{autodesktop}\SqueakySift"; Filename: "{app}\OpenSweep.exe"; Comment: "Review cleanup, manage startup and keep Windows up to date"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenSweep.exe"; Description: "Launch SqueakySift"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""OpenSweep Auto Clean"" /F"; Flags: runhidden; RunOnceId: "RemoveTask"

[Code]
procedure RemoveLegacyShortcut(Path: String);
var
  Shell, Shortcut: Variant;
begin
  if not FileExists(Path) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(Path);
    if CompareText(Shortcut.TargetPath, ExpandConstant('{app}\OpenSweep.exe')) = 0 then
      DeleteFile(Path);
  except
    Log('Could not inspect former shortcut: ' + Path);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Remove the entry written by the app's built-in "Install" button so Installed apps lists SqueakySift only once.
  if CurStep = ssPostInstall then
  begin
    // New shortcuts are in place. Retire the previous release's Start menu and desktop names.
    RemoveLegacyShortcut(ExpandConstant('{autoprograms}\OpenSweep.lnk'));
    RemoveLegacyShortcut(ExpandConstant('{autodesktop}\OpenSweep.lnk'));
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenSweep');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\OpenSweep');
  if UninstallSilent or not DirExists(DataDir) then Exit;
  if MsgBox('Also delete SqueakySift settings, logs and registry backups?' #13#10 #13#10 + DataDir,
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
