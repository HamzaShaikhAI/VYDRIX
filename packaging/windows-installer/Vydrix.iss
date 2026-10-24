; Inno Setup script for the single-file VYDRIX installer (per-user; requests elevation only if installing WinFsp).
; Built by build-release.ps1:
;   ISCC.exe /DAppVersion=0.003.1 /DSourceDir=..\dist\VYDRIX-0.003.1-win-x64 /DWinFspMsi=..\tools\winfsp\winfsp-2.1.25156.msi /O..\dist Vydrix.iss
#ifndef AppVersion
  #define AppVersion "0.003.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\VYDRIX-" + AppVersion + "-win-x64"
#endif
#ifndef WinFspMsi
  #define WinFspMsi "..\tools\winfsp\winfsp-2.1.25156.msi"
#endif

[Setup]
AppId={{4A2F8D3B-9C1E-4E6B-8B5A-3C9F0A7E2D02}}
AppName=VYDRIX
AppVersion={#AppVersion}
AppVerName=VYDRIX {#AppVersion}
AppPublisher=VYDRIX
VersionInfoVersion={#AppVersion}
VersionInfoProductName=VYDRIX
VersionInfoDescription=VYDRIX - Cloud-to-SSD Game Storage Engine Setup
DefaultDirName={localappdata}\Programs\VYDRIX
DefaultGroupName=VYDRIX
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=VYDRIX-{#AppVersion}-setup
SetupIconFile=..\src\Vydrix.Desktop\Assets\vydrix.ico
UninstallDisplayIcon={app}\Vydrix.exe
UninstallDisplayName=VYDRIX - Cloud-to-SSD Engine
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The desktop app holds this mutex while running; Setup and uninstaller check it first.
AppMutex=Vydrix.Desktop.SingleInstance
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "Install.cmd,Install-Vydrix.ps1,Uninstall-Vydrix.ps1"
Source: "{#WinFspMsi}"; DestName: "winfsp.msi"; Flags: dontcopy; Condition: FileExists(WinFspMsi)

[Icons]
Name: "{autoprograms}\VYDRIX"; Filename: "{app}\Vydrix.exe"; Comment: "Cloud-to-SSD Game Storage Engine"
Name: "{autodesktop}\VYDRIX"; Filename: "{app}\Vydrix.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Vydrix.exe"; Description: "Launch VYDRIX"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User configuration and cache in %LOCALAPPDATA%\Vydrix are preserved by default.
Type: files; Name: "{app}\*.log"

[Code]
function WinFspInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll')) or
            FileExists(ExpandConstant('{commonpf64}\WinFsp\bin\winfsp-x64.dll')) or
            RegKeyExists(HKLM, 'SOFTWARE\WinFsp') or
            RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\WinFsp');
end;

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
    Procs := Service.ExecQuery('SELECT Name, ExecutablePath FROM Win32_Process WHERE Name = ''rclone.exe'' OR Name = ''vydrix-cli.exe'' OR Name = ''Vydrix.exe''');
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
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Proc: String;
begin
  Result := '';
  Proc := RunningFrom(ExpandConstant('{app}'));
  if Proc <> '' then
    Result := 'VYDRIX or its background drive service (' + Proc + ') is currently running from:' + #13#10 +
              ExpandConstant('{app}') + #13#10#13#10 +
              'Please exit VYDRIX before continuing with setup.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and not WinFspInstalled() then
  begin
    if MsgBox('WinFsp was not detected on this system.' + #13#10#13#10 +
              'WinFsp is required to mount cloud storage as a local Windows drive letter.' + #13#10#13#10 +
              'Would you like to open the WinFsp download website now?',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://winfsp.dev', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
