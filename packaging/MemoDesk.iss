#ifndef StageDir
  #error StageDir must point to a complete self-contained publish directory
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif

[Setup]
AppId={{C59995AB-33CD-4A46-B218-05E6C6631196}
AppName=备忘
AppVersion={#AppVersion}
AppPublisher=MemoDesk
DefaultDirName={localappdata}\Programs\MemoDesk
DefaultGroupName=备忘
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release
OutputBaseFilename=MemoDesk-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\MemoDesk.App\Assets\memo.ico
UninstallDisplayIcon={app}\MemoDesk.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=MemoDesk.exe,MemoDesk.App.exe

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\备忘"; Filename: "{app}\MemoDesk.exe"
Name: "{autodesktop}\备忘"; Filename: "{app}\MemoDesk.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MemoDesk.exe"; Description: "Launch 备忘"; Flags: nowait postinstall skipifsilent
