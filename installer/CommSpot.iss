; Per-user installer for The Comm-Spot. No admin rights needed.
; It installs to %LOCALAPPDATA%\Programs\CommSpot, which the user can write to,
; so the app's signed auto-updater keeps working after install.
; Build: iscc /DAppVersion=0.5.0 /DSourceDir=out\win /DOutputDir=dist installer\CommSpot.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\win"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6C2E8F4A-3B7D-4E19-9A52-C0D1E8F37B64}
AppName=The Comm-Spot
AppVersion={#AppVersion}
AppPublisher=The Comm-Spot
AppPublisherURL=https://commspot.net
DefaultDirName={localappdata}\Programs\CommSpot
DefaultGroupName=The Comm-Spot
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=CommSpot-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CommSpot.exe
UninstallDisplayName=The Comm-Spot
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\CommSpot.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\The Comm-Spot"; Filename: "{app}\CommSpot.exe"
Name: "{userdesktop}\The Comm-Spot"; Filename: "{app}\CommSpot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CommSpot.exe"; Description: "Open The Comm-Spot now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Files the auto-updater leaves next to the app.
Type: files; Name: "{app}\CommSpot.exe.old"
Type: filesandordirs; Name: "{app}\.commspot-update"
