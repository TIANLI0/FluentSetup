; Contoso Notes - per-user installer (Inno Setup 6)
; Built by CI with: iscc /DMyAppVersion=2.4.1 installer\setup.iss

#define MyAppName "Contoso Notes"
#ifndef MyAppVersion
  #define MyAppVersion "2.4.1"
#endif
#define MyAppPublisher "Contoso Ltd"
#define MyAppURL "https://notes.contoso.example"
#define MyAppExeName "ContosoNotes.exe"

[Setup]
AppId={{8C4E5D1A-3B2F-4F6A-9C7D-1E2F3A4B5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/support
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ContosoNotes-{#MyAppVersion}-Setup
SetupIconFile=assets\notes.ico
LicenseFile=LICENSE.txt
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.StartWithWindows=Start Contoso Notes when I sign in
chinesesimplified.StartWithWindows=登录时启动 Contoso Notes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:StartWithWindows}"; Flags: unchecked

[Files]
Source: "..\build\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\publish\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "defaults\settings.json"; DestDir: "{app}\config"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
Name: "{app}\logs"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ContosoNotes"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "Software\Contoso\Notes"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Contoso\Notes"; ValueType: dword; ValueName: "FirstRun"; ValueData: "$1"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{localappdata}\Contoso\Notes"
