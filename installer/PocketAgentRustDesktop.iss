#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#ifndef SourceDir
#define SourceDir "..\artifacts\rust-desktop-preview"
#endif
#ifndef OutputDir
#define OutputDir "..\artifacts"
#endif
#ifndef OutputBaseFilename
#define OutputBaseFilename "PocketAgent-RustDesktop-Setup"
#endif
#ifndef ProjectRoot
#define ProjectRoot ".."
#endif

[Setup]
AppId={{A7B70A4A-0A5B-4A3B-9C17-3A8D49C0A1F5}
AppName=PocketAgent Rust Desktop
AppVersion={#AppVersion}
AppPublisher=PocketAgent
DefaultDirName={localappdata}\Programs\PocketAgent-RustDesktop
DefaultGroupName=PocketAgent Rust Desktop
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
Name: "{autoprograms}\PocketAgent Rust Desktop"; Filename: "{app}\pocketagent-desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\PocketAgent Rust Desktop"; Filename: "{app}\pocketagent-desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\pocketagent-desktop.exe"; Description: "Launch PocketAgent Rust Desktop"; Flags: nowait postinstall skipifsilent
