; Stickmark installer (Inno Setup 6)
; Build with:
;   iscc /DAppVersion=2.4.0 packaging\Stickmark.iss

#define AppName "Stickmark"
#define AppExeName "Stickmark.exe"
#define AppPublisher "Stickmark"
#define AppUrl "https://github.com/dameng324/stickmark"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{E5F5B1A2-6E4E-47A7-8160-515F31DCE8FD}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=Stickmark-{#AppVersion}-win-x64-setup
SetupIconFile=..\Stickmark\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
LicenseFile=..\LICENSE

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
