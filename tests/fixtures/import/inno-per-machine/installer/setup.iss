; Fabrikam Studio - installs for all users (default) or the current user
#include "common.iss"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://fabrikam.example/studio
DefaultDirName={autopf}\Fabrikam\{#AppName}
DefaultGroupName=Fabrikam
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ChangesAssociations=yes
ChangesEnvironment=yes
OutputBaseFilename=FabrikamStudio-Setup
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
AppMutex=FabrikamStudioMutex
SignTool=fabrikam

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fileassoc"; Description: "{cm:AssocFileExtension,{#AppName},.fstudio}"
Name: "addtopath"; Description: "Add the command-line tools to PATH"; Flags: unchecked

[Files]
Source: "..\bin\Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\docs\manual.pdf"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs; Tasks: addtopath

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Fabrikam.Studio"
Name: "{group}\{#AppName} (Safe Mode)"; Filename: "{app}\{#AppExe}"; Parameters: "--safe-mode"; IconFilename: "{app}\safe.ico"
Name: "{group}\Fabrikam on the Web"; Filename: "https://fabrikam.example/"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; File type .fstudio
Root: HKA; Subkey: "Software\Classes\.fstudio\OpenWithProgids"; ValueType: string; ValueName: "Fabrikam.Studio.Project"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.fstudio"; ValueType: string; ValueName: ""; ValueData: "Fabrikam.Studio.Project"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Fabrikam.Studio.Project"; ValueType: string; ValueName: ""; ValueData: "Fabrikam Studio Project"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Fabrikam.Studio.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},1"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Fabrikam.Studio.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".fstudio"; ValueData: ""
; URL protocol fabrikam-studio://
Root: HKA; Subkey: "Software\Classes\fabrikam-studio"; ValueType: string; ValueName: ""; ValueData: "URL:Fabrikam Studio Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\fabrikam-studio"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\fabrikam-studio\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\fabrikam-studio\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --open-url ""%1"""
; Win+R
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
; Command-line tools on PATH
Root: HKA; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\tools"; Tasks: addtopath; Check: NeedsAddPath('{app}\tools')
Root: HKA; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: string; ValueName: "FABRIKAM_STUDIO_HOME"; ValueData: "{app}"; Flags: uninsdeletevalue
; Settings
Root: HKA; Subkey: "Software\Fabrikam"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Fabrikam\Studio"; ValueType: string; ValueName: "Edition"; ValueData: "Community"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Fabrikam\Studio"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"
Root: HKLM; Subkey: "Software\Policies\Fabrikam"; ValueType: dword; ValueName: "Telemetry"; ValueData: "0"

[INI]
Filename: "{app}\studio.ini"; Section: "General"; Key: "InstallDir"; String: "{app}"
Filename: "{app}\studio.ini"; Section: "General"; Key: "Channel"; String: "stable"

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the Visual C++ runtime..."; Flags: waituntilterminated
Filename: "{app}\StudioHelper.exe"; Parameters: "--register-fonts ""{app}\fonts"""; StatusMsg: "Registering fonts..."; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Fabrikam Studio"" dir=in action=allow program=""{app}\{#AppExe}"""; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
Filename: "{app}\StudioHelper.exe"; Parameters: "--unregister-fonts"; Flags: runhidden; RunOnceId: "UnregFonts"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im StudioAgent.exe"; Flags: runhidden; RunOnceId: "KillAgent"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\Fabrikam\Studio\cache"

[Code]
function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Param + ';', ';' + OrigPath + ';') = 0;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not RegKeyExists(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full') then
  begin
    MsgBox('.NET Framework 4.8 is required.', mbError, MB_OK);
    Result := False;
  end;
end;
