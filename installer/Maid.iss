; Maid's Windows installer (Inno Setup 6). Built by tools\build-installer.ps1, which publishes a self-contained Maid
; (no .NET needed on the PC it goes to) into a staging folder and passes it in:
;   ISCC /DAppVersion=1.6 /DSourceDir=<staging folder> /DOutputDir=<dist> installer\Maid.iss

#ifndef AppVersion
  #error Pass /DAppVersion=<version> (tools\build-installer.ps1 does)
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<self-contained publish of Maid> (tools\build-installer.ps1 does)
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Never change the AppId: it is how a newer setup finds the installed Maid to update it.
AppId={{A2B84FFD-01AA-4358-9126-E59BD4987D12}
AppName=Maid
AppVersion={#AppVersion}
AppVerName=Maid {#AppVersion}
AppPublisher=Petr Nemec
AppComments=All your Claude Code sessions in one window
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Maid Setup
DefaultDirName={autopf}\Maid
DisableDirPage=auto
DisableProgramGroupPage=yes
; Per machine, in Program Files: Maid runs as administrator, so its files must not be writable without admin rights.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809: the first with the pseudo console (ConPTY) the terminals run in.
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Maid-Setup-{#AppVersion}
SetupIconFile=..\src\Maid\Assets\maid.ico
UninstallDisplayIcon={app}\Maid.exe
UninstallDisplayName=Maid
InfoBeforeFile=before-install.txt
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; A running Maid holds this mutex (App.RunningMutexName): Setup and the uninstaller ask for it to be closed first, as it
; loads parts of the runtime only when it needs them (removing them under it made it fail on closing).
AppMutex=Maid.Running
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; The page and maidctl are replaced as a whole, so files an older version had there do not linger.
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\cli"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Maid"; Filename: "{app}\Maid.exe"; Comment: "All your Claude Code sessions in one window"
Name: "{autodesktop}\Maid"; Filename: "{app}\Maid.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 Runtime..."; Check: WebView2Downloaded; Flags: waituntilterminated
; Started as the user who ran Setup (not as Setup's administrator): Maid asks for administrator rights itself.
Filename: "{app}\Maid.exe"; Description: "{cm:LaunchProgram,Maid}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  // The WebView2 Runtime's entry in EdgeUpdate (per machine or per user), and Microsoft's small online installer.
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2Setup = 'MicrosoftEdgeWebview2Setup.exe';
  ClaudeCodeUrl = 'https://claude.com/claude-code';

var
  DownloadPage: TDownloadWizardPage;

function HasRuntimeVersion(Root: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, WebView2ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function WebView2Installed: Boolean;
begin
  Result := HasRuntimeVersion(HKLM32) or HasRuntimeVersion(HKLM64) or HasRuntimeVersion(HKCU);
end;

function WebView2Downloaded: Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\' + WebView2Setup));
end;

// Whether `claude` (Claude Code) is on the PATH, or where its own installer puts it. Only for a hint at the end.
function InDirectories(Directories: String): Boolean;
var
  Position: Integer;
  Directory: String;
begin
  Result := False;
  Directories := Directories + ';';
  while (not Result) and (Directories <> '') do
  begin
    Position := Pos(';', Directories);
    Directory := RemoveQuotes(Trim(Copy(Directories, 1, Position - 1)));
    Delete(Directories, 1, Position);
    if Directory <> '' then
      Result := FileExists(AddBackslash(Directory) + 'claude.exe') or FileExists(AddBackslash(Directory) + 'claude.cmd');
  end;
end;

function ClaudeCodeFound: Boolean;
var
  UserPath: String;
begin
  Result := InDirectories(GetEnv('PATH')) or FileExists(ExpandConstant('{%USERPROFILE}\.local\bin\claude.exe'));
  if (not Result) and RegQueryStringValue(HKCU, 'Environment', 'Path', UserPath) then
    Result := InDirectories(ExpandConstant(UserPath));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or WebView2Installed then
    Exit;
  // Maid's window is a WebView2 page: get the runtime first if this PC lacks it (Windows 11 has it).
  DownloadPage.Clear;
  DownloadPage.Add(WebView2Url, WebView2Setup, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox('The Microsoft Edge WebView2 Runtime, which Maid needs, could not be downloaded:' + #13#10 +
          GetExceptionMessage + #13#10#13#10 + 'Setup goes on; install the runtime from' + #13#10 +
          'https://developer.microsoft.com/microsoft-edge/webview2/' + #13#10 + 'before you start Maid.',
          mbError, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and not ClaudeCodeFound then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Claude Code was not found on this PC. Maid runs it in every session: install it from ' + ClaudeCodeUrl +
      ' and sign in once (run "claude" in a terminal) before you start sessions in Maid.';
end;
