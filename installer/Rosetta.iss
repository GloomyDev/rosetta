; Build with installer\build.ps1 (publishes the app first).
#define AppName "Rosetta"
#define AppVersion "0.1.0"

[Setup]
AppId={{B7E4C1D2-3A5F-4E86-9C0D-52A1F7E8B364}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=GloomyDev
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
SetupIconFile=..\assets\rosetta.ico
UninstallDisplayIcon={app}\Rosetta.exe
OutputDir=Output
OutputBaseFilename=Rosetta-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
DisableProgramGroupPage=yes
InfoBeforeFile=translation-notice.txt

[Tasks]
Name: "autostart"; Description: "Start Rosetta when I sign in to Windows"; Flags: unchecked
Name: "translation"; Description: "Enable ONLINE translation of selected text (NOT local: sends the selected text to Google Translate)"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Rosetta"; Filename: "{app}\Rosetta.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Rosetta"; ValueData: """{app}\Rosetta.exe"""; Flags: uninsdeletevalue; Tasks: autostart

Root: HKCU; Subkey: "Software\Rosetta"; ValueType: dword; ValueName: "Translation"; ValueData: 1; Flags: uninsdeletekey; Tasks: translation

[Run]
Filename: "{app}\Rosetta.exe"; Description: "Launch Rosetta"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Rosetta.exe"; Flags: runhidden; RunOnceId: "KillRosetta"
