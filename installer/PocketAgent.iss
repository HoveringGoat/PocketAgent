#ifndef AppVersion
#define AppVersion "0.2.2"
#endif
#ifndef SourceDir
#define SourceDir "..\artifacts\releases\0.2.2\desktop"
#endif
#ifndef OutputDir
#define OutputDir "..\artifacts\releases\0.2.2"
#endif
#ifndef OutputBaseFilename
#define OutputBaseFilename "PocketAgent-0.2.2-Setup"
#endif
#ifndef ProjectRoot
#define ProjectRoot ".."
#endif

[Setup]
AppId={{B8AA1AB3-0C6E-4E3E-BF54-6A6E4D8FC7BB}
AppName=PocketAgent
AppVersion={#AppVersion}
AppPublisher=PocketAgent
DefaultDirName={localappdata}\Programs\PocketAgent
DefaultGroupName=PocketAgent
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#ProjectRoot}\src\PocketAgent.Desktop\Assets\PocketAgent.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{autoprograms}\PocketAgent"; Filename: "{app}\PocketAgent.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\PocketAgent"; Filename: "{app}\PocketAgent.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PocketAgent.Desktop.exe"; Description: "Launch PocketAgent"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v PocketAgent /f"; Flags: runhidden; RunOnceId: "RemovePocketAgentStartup"
