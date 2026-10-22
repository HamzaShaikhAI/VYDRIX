; Inno Setup script for the single-file CLOUDPLAY X installer (per-user; asks for administrator rights only to install WinFsp).
; Built by build-release.ps1:
;   ISCC.exe /DAppVersion=0.7.0 /DSourceDir=..\dist\CloudPlayX-0.7.0-win-x64 /DWinFspMsi=..\tools\winfsp\winfsp-2.1.25156.msi /O..\dist CloudPlayX.iss
#ifndef AppVersion
  #define AppVersion "0.7.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\CloudPlayX-" + AppVersion + "-win-x64"
#endif
#ifndef WinFspMsi
  #define WinFspMsi "..\tools\winfsp\winfsp-2.1.25156.msi"
#endif

[Setup]
AppId={{6A0C9C2E-6E7B-4E5E-9D2B-4C1F0A7E1D01}
AppName=CLOUDPLAY X
AppVersion={#AppVersion}
AppVerName=CLOUDPLAY X {#AppVersion}
AppPublisher=CLOUDPLAY X
VersionInfoVersion={#AppVersion}
VersionInfoProductName=CLOUDPLAY X
VersionInfoDescription=CLOUDPLAY X Setup
DefaultDirName={localappdata}\Programs\CloudPlayX
DefaultGroupName=CLOUDPLAY X
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=CloudPlayX-{#AppVersion}-setup
SetupIconFile=..\src\CloudPlayX.Desktop\Assets\cloudplayx.ico
UninstallDisplayIcon={app}\CloudPlayX.exe
UninstallDisplayName=CLOUDPLAY X
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The desktop app holds this mutex while running; Setup and the uninstaller ask the user to close it first.
AppMutex=CloudPlayX.Desktop.SingleInstance
; Never let Windows force-close rclone: a mounted drive may still be uploading. PrepareToInstall checks instead.
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "Install.cmd,Install-CloudPlayX.ps1,Uninstall-CloudPlayX.ps1"
; Official, signed WinFsp installer (Navimatics LLC, GPLv3 with FLOSS exception). Only extracted when WinFsp is missing.
Source: "{#WinFspMsi}"; DestName: "winfsp.msi"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\CLOUDPLAY X"; Filename: "{app}\CloudPlayX.exe"; Comment: "Cloud-to-SSD game storage engine"
Name: "{autodesktop}\CLOUDPLAY X"; Filename: "{app}\CloudPlayX.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CloudPlayX.exe"; Description: "Start CLOUDPLAY X"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, saved logins and caches (%LOCALAPPDATA%\CloudPlayX and the chosen cache folder) are kept on purpose:
; the cache can hold game files that have not finished uploading yet. Files in Google Drive are never touched.
Type: files; Name: "{app}\*.log"

[Code]
function WinFspInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll')) or
            FileExists(ExpandConstant('{commonpf64}\WinFsp\bin\winfsp-x64.dll'));
end;

{ Name of a CLOUDPLAY X process (app, cpx CLI or a mounted drive's rclone) running from Dir, or '' if none. }
function RunningFrom(const Dir: String): String;
var
  Locator, Service, Procs, P: Variant;
  I: Integer;
  Path, Prefix: String;
begin
  Result := '';
  Prefix := Lowercase(AddBackslash(Dir));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT Name, ExecutablePath FROM Win32_Process WHERE Name = ''rclone.exe'' OR Name = ''cpx.exe'' OR Name = ''CloudPlayX.exe''');
    for I := 0 to Procs.Count - 1 do
    begin
      P := Procs.ItemIndex(I);
      if VarIsNull(P.ExecutablePath) then Path := '' else Path := P.ExecutablePath;
      Path := Lowercase(Path);
      if (Path <> '') and (Pos(Prefix, Path) = 1) then
      begin
        Result := P.Name;
        Exit;
      end;
    end;
  except
    { WMI unavailable: fall back to the AppMutex check only. }
  end;
end;

function BusyMessage(const Name: String): String;
begin
  if Lowercase(Name) = 'rclone.exe' then
    Result := 'A CLOUDPLAY X cloud drive is still mounted.' + #13#10#13#10 +
              'Open CLOUDPLAY X, close its window and choose "No" so the drives are unmounted safely ' +
              '(pending uploads finish first). Then try again.'
  else
    Result := 'CLOUDPLAY X (' + Name + ') is running. Close it and try again.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Busy: String;
begin
  Result := '';
  Busy := RunningFrom(ExpandConstant('{app}'));
  if Busy <> '' then Result := BusyMessage(Busy);
end;

procedure InstallWinFsp();
var
  Code: Integer;
  Msi: String;
begin
  WizardForm.StatusLabel.Caption := 'Installing WinFsp (needed for the virtual drive) - please approve the Windows prompt...';
  ExtractTemporaryFile('winfsp.msi');
  Msi := ExpandConstant('{tmp}\winfsp.msi');
  if not ShellExec('runas', 'msiexec.exe', '/i "' + Msi + '" /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
    Code := -1;
  if (Code <> 0) and (Code <> 3010) and not WinFspInstalled() then
    MsgBox('WinFsp was not installed (it needs administrator approval).' + #13#10#13#10 +
           'CLOUDPLAY X is installed, but cloud drives cannot be mounted until WinFsp is installed. ' +
           'Run this setup again, or install WinFsp from https://winfsp.dev/rel/', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WinFspInstalled() then
    InstallWinFsp();
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine + NewLine;
  if WinFspInstalled() then
    Result := Result + 'WinFsp (virtual drive driver): already installed'
  else
    Result := Result + 'WinFsp (virtual drive driver): will be installed - Windows asks for permission once';
end;

function InitializeUninstall(): Boolean;
var
  Busy: String;
begin
  Result := True;
  Busy := RunningFrom(ExpandConstant('{app}'));
  if Busy <> '' then
  begin
    MsgBox(BusyMessage(Busy), mbInformation, MB_OK);
    Result := False;
  end;
end;
