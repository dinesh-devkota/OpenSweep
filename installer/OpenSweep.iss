; OpenSweep Setup – per-user installer (no administrator rights needed).
; Build: ISCC.exe /DAppVersion=1.5.1 installer\OpenSweep.iss   (expects dist\OpenSweep.exe)

#ifndef AppVersion
  #define AppVersion "1.5.1"
#endif

[Setup]
; Keep this AppId: SelfInstaller looks for "{AppId}_is1" to detect a Setup install.
AppId={{6E2B4F1A-3C7D-4E8B-9A15-0F5D2C8B7A31}
AppName=OpenSweep
AppVersion={#AppVersion}
AppVerName=OpenSweep {#AppVersion}
AppPublisher=OpenSweep contributors
AppPublisherURL=https://github.com/dinesh-devkota/OpenSweep
AppSupportURL=https://github.com/dinesh-devkota/OpenSweep/issues
AppUpdatesURL=https://github.com/dinesh-devkota/OpenSweep/releases
AppComments=Open-source Windows maintenance, cleanup previews, startup management and updates
VersionInfoVersion={#AppVersion}
VersionInfoProductName=OpenSweep
VersionInfoDescription=OpenSweep Setup
PrivilegesRequired=lowest
; Retain the installed target so existing schedules and taskbar pins continue to work.
DefaultDirName={autopf}\OpenSweep
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\src\OpenSweep\Assets\OpenSweep.ico
UninstallDisplayIcon={app}\OpenSweep.exe
UninstallDisplayName=OpenSweep
OutputDir=..\dist
OutputBaseFilename=OpenSweep-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\OpenSweep.exe"; DestDir: "{app}"; DestName: "OpenSweep.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\OpenSweep"; Filename: "{app}\OpenSweep.exe"; Comment: "Review cleanup, manage startup and keep Windows up to date"
Name: "{autodesktop}\OpenSweep"; Filename: "{app}\OpenSweep.exe"; Comment: "Review cleanup, manage startup and keep Windows up to date"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenSweep.exe"; Description: "Launch OpenSweep"; Flags: nowait postinstall skipifsilent

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
  // Remove the entry written by the app's built-in "Install" button so Installed apps lists OpenSweep only once.
  if CurStep = ssPostInstall then
  begin
    // New shortcuts are in place. Retire the previous release's Start menu and desktop names.
    RemoveLegacyShortcut(ExpandConstant('{autoprograms}\SqueakySift.lnk'));
    RemoveLegacyShortcut(ExpandConstant('{autodesktop}\SqueakySift.lnk'));
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
  if MsgBox('Also delete OpenSweep settings, logs and registry backups?' #13#10 #13#10 + DataDir,
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
