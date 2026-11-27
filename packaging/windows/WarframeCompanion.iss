#ifndef PayloadDirectory
  #error PayloadDirectory must point to a tested runtime-dependent publish directory.
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied.
#endif

[Setup]
SetupIconFile=..\..\assets\icon.ico
#ifdef IsolatedTest
AppId={{6FB7A1A8-614C-464E-A5A3-7E0A767D8BB3}
UninstallDisplayName=Warframe Companion isolated installer test
#else
AppId={{6D29E832-E0CD-4E76-BA73-A379438BFB5C}
#endif
AppName=Warframe Companion
AppVersion={#ReleaseVersion}
VersionInfoVersion={#ReleaseVersion}.0
AppPublisher=Lenalein2001
AppPublisherURL=https://github.com/Lenalein2001/Warframe-App
#ifdef IsolatedTest
DefaultDirName={localappdata}\Programs\WarframeCompanion-InstallerTest
#else
DefaultDirName={localappdata}\Programs\WarframeCompanion
#endif
DefaultGroupName=Warframe Companion
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDirectory}
#ifdef IsolatedTest
OutputBaseFilename=WarframeCompanion-win-x64-TestSetup
#else
OutputBaseFilename=WarframeCompanion-win-x64-Setup
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\WarframeCompanion.Desktop.exe
CloseApplications=yes
RestartApplications=no
LicenseFile=..\..\LICENSE

; Retire obsolete program files before copying the new payload. Never target profile storage.
[InstallDelete]
#ifdef RuntimeCleanupInclude
#include RuntimeCleanupInclude
#endif
Type: files; Name: "{app}\tools\tesseract\*.dll"
Type: files; Name: "{app}\Portable.cmd"

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.sqlite,*.sqlite-wal,*.sqlite-shm,Portable.cmd"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Warframe Companion"; Filename: "{app}\WarframeCompanion.Desktop.exe"

[Run]
Filename: "{app}\WarframeCompanion.Desktop.exe"; Description: "Open Warframe Companion"; Flags: nowait postinstall skipifsilent

; Settings and account databases live outside the installation directory.
; Deliberately no UninstallDelete section: uninstall preserves user data.

[Code]
const
  DotNetRegistry = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64';
  WebViewRegistry = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  DotNetUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  WebViewUrl = 'https://developer.microsoft.com/microsoft-edge/webview2/#download';

var
  RuntimePage: TWizardPage;
  RuntimeStatus: TNewStaticText;
  DotNetButton, WebViewButton: TNewButton;

function DotNetRoot: String;
begin
  Result := GetEnv('DOTNET_ROOT_X64');
  if DirExists(Result) then exit;
  Result := GetEnv('DOTNET_ROOT');
  if DirExists(Result) then exit;
  if RegQueryStringValue(HKLM32, DotNetRegistry, 'InstallLocation', Result) and DirExists(Result) then exit;
  if IsArm64 then
    Result := ExpandConstant('{commonpf64}\dotnet\x64')
  else
    Result := ExpandConstant('{commonpf64}\dotnet');
end;

function HasFramework(const Root, Framework: String): Boolean;
var
  Directory: String;
  Version: TFindRec;
  Patch: Integer;
begin
  Result := False;
  Directory := AddBackslash(Root) + 'shared\' + Framework + '\';
  if not FindFirst(Directory + '10.0.*', Version) then exit;
  try
    repeat
      Patch := StrToIntDef(Copy(Version.Name, 6, Length(Version.Name)), -1);
      if (Patch >= 0) and DirExists(Directory + Version.Name) and
         FileExists(Directory + Version.Name + '\' + Framework + '.deps.json') then
      begin
        Result := True;
        exit;
      end;
    until not FindNext(Version);
  finally
    FindClose(Version);
  end;
end;

function HasWebView(RootKey: Integer): Boolean;
var
  Version: String;
  Packed: Int64;
begin
  Result := RegQueryStringValue(RootKey, WebViewRegistry, 'pv', Version) and
    StrToVersion(Version, Packed) and (Packed > 0);
end;

function CheckRuntimes: Boolean;
var
  Root, Missing: String;
begin
  Root := DotNetRoot;
  Missing := '';
  if not HasFramework(Root, 'Microsoft.NETCore.App') then
    Missing := '.NET Runtime 10 (x64)' + #13#10;
  if not HasFramework(Root, 'Microsoft.AspNetCore.App') then
    Missing := Missing + 'ASP.NET Core Runtime 10 (x64)' + #13#10;
  DotNetButton.Visible := Missing <> '';
  WebViewButton.Visible := not (HasWebView(HKLM32) or HasWebView(HKCU));
  if DotNetButton.Visible then WebViewButton.Top := ScaleY(128) else WebViewButton.Top := ScaleY(94);
  if WebViewButton.Visible then Missing := Missing + 'Microsoft Edge WebView2';
  Result := Missing = '';
  if Result then
    RuntimeStatus.Caption := 'All required runtimes are installed. Click Next to continue.'
  else
    RuntimeStatus.Caption := 'Install these components, then click Next to check again.' + #13#10 + #13#10 + Missing;
end;

procedure OpenRuntimeDownload(Sender: TObject);
var
  Url: String;
  ErrorCode: Integer;
begin
  if Sender = DotNetButton then Url := DotNetUrl else Url := WebViewUrl;
  if not ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox('Open this address in your browser:' + #13#10 + Url, mbInformation, MB_OK);
end;

function AddDownloadButton(Top: Integer; const Caption: String): TNewButton;
begin
  Result := TNewButton.Create(WizardForm);
  Result.Parent := RuntimePage.Surface;
  Result.Top := ScaleY(Top);
  Result.Width := ScaleX(190);
  Result.Height := ScaleY(25);
  Result.Caption := Caption;
  Result.OnClick := @OpenRuntimeDownload;
end;

procedure InitializeWizard;
begin
  RuntimePage := CreateCustomPage(wpLicense, 'Required components', 'Get the runtimes this PC is missing.');
  RuntimeStatus := TNewStaticText.Create(WizardForm);
  RuntimeStatus.Parent := RuntimePage.Surface;
  RuntimeStatus.AutoSize := False;
  RuntimeStatus.WordWrap := True;
  RuntimeStatus.Width := RuntimePage.SurfaceWidth;
  RuntimeStatus.Height := ScaleY(85);
  DotNetButton := AddDownloadButton(94, 'Open Microsoft .NET downloads');
  WebViewButton := AddDownloadButton(128, 'Open Microsoft WebView2 download');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = RuntimePage.ID then Result := WizardSilent or CheckRuntimes;
end;

function NextButtonClick(PageID: Integer): Boolean;
begin
  Result := True;
  if PageID = RuntimePage.ID then Result := CheckRuntimes;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CheckRuntimes then
  begin
    Result := 'Required runtimes are missing.' + #13#10 + RuntimeStatus.Caption;
    Log(Result);
  end;
end;
