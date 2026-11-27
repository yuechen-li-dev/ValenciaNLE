#ifndef StageDir
  #error StageDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef ValenciaVersion
  #define ValenciaVersion "0.3.0-rc.1"
#endif
#ifndef FileVersion
  #define FileVersion "0.3.0.1"
#endif

[Setup]
AppId={{E84B7F78-E0F1-457B-9870-74509182D17D}
AppName=Valencia
AppVersion={#ValenciaVersion}
AppPublisher=Valencia contributors
AppPublisherURL=https://github.com/yuechen-li-dev/ValenciaNLE
DefaultDirName={localappdata}\Programs\Valencia
DefaultGroupName=Valencia
UninstallDisplayIcon={app}\Valencia.App.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
VersionInfoVersion={#FileVersion}
OutputBaseFilename=Valencia-{#ValenciaVersion}-win-x64-setup
OutputDir={#OutputDir}
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
WizardStyle=modern

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Valencia"; Filename: "{app}\Valencia.App.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall Valencia"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Valencia.App.exe"; Description: "Launch Valencia"; Flags: nowait postinstall skipifsilent
