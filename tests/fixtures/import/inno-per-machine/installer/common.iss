; Values shared by the installer and the portable zip build.
#define AppId "Fabrikam.Studio"
#define AppName "Fabrikam Studio"
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\bin\Release\Studio.exe")
#define AppPublisher "Fabrikam, Inc."
#define AppExe "Studio.exe"
#define SetupName "FabrikamStudio-" + AppVersion + "-Setup"
