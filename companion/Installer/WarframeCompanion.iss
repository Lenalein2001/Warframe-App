#ifndef PayloadDirectory
  #error PayloadDirectory must point to a tested self-contained publish directory.
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied.
#endif

[Setup]
AppId={{6D29E832-E0CD-4E76-BA73-A379438BFB5C}
AppName=Warframe Companion
AppVersion=0.1.0
AppPublisher=Lenalein2001
AppPublisherURL=https://github.com/Lenalein2001/Warframe-App
DefaultDirName={localappdata}\Programs\WarframeCompanion
DefaultGroupName=Warframe Companion
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDirectory}
OutputBaseFilename=WarframeCompanion-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\WarframeCompanion.Desktop.exe
CloseApplications=yes
RestartApplications=no
LicenseFile=..\..\LICENSE

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.sqlite,*.sqlite-wal,*.sqlite-shm,Portable.cmd"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Warframe Companion"; Filename: "{app}\WarframeCompanion.Desktop.exe"

[Run]
Filename: "{app}\WarframeCompanion.Desktop.exe"; Description: "Open Warframe Companion"; Flags: nowait postinstall skipifsilent

; Settings and account databases live outside the installation directory.
; Deliberately no UninstallDelete section: uninstall preserves user data.
