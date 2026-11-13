using System.Text.Json.Serialization;

namespace FluentSetup.Builder.Core.Model;

/// <summary>A FluentSetup project (fluentsetup.json). See shared/schemas/fluentsetup.project.schema.json.</summary>
public sealed class SetupProject
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("$schema")]
    public string? Schema { get; set; } = "https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/shared/schemas/fluentsetup.project.schema.json";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>
    /// Values for $(Name) placeholders in any string of the project, e.g. { "Version": "1.2.0" } with
    /// "version": "$(Version)". <c>fluentsetup build -D Version=1.2.1</c> overrides them (CI release builds).
    /// </summary>
    public Dictionary<string, string> Variables { get; set; } = [];
    public AppInfo App { get; set; } = new();
    public FileSettings Files { get; set; } = new();
    public InstallSettings Install { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public SigningSettings Signing { get; set; } = new();
    /// <summary>Windows components the app needs (checked before anything is changed).</summary>
    public RequirementSettings Requirements { get; set; } = new();
    /// <summary>User-selectable switches shown on the options page; actions can depend on them.</summary>
    public List<UserOptionSettings> Options { get; set; } = [];
    /// <summary>Programs from the app's own files to run after install / before uninstall (no shell, no system tools).</summary>
    public List<ActionSettings> Actions { get; set; } = [];
    public UninstallSettings Uninstall { get; set; } = new();
    /// <summary>Where installed copies find a newer Setup.exe (<c>uninstall.exe /check-update</c> and <c>/install-update</c>).</summary>
    public UpdateSettings Update { get; set; } = new();
    /// <summary>More shortcuts (other programs of the app, arguments, the Startup folder) besides the main one.</summary>
    public List<ShortcutSettings> Shortcuts { get; set; } = [];
    /// <summary>File types the app opens (Explorer double-click, "Open with", Default apps).</summary>
    public List<FileAssociationSettings> Associations { get; set; } = [];
    /// <summary>URL schemes the app handles, e.g. "contoso-notes://open?id=1".</summary>
    public List<ProtocolSettings> Protocols { get; set; } = [];
    /// <summary>Registry values the installer writes below Software\&lt;Vendor&gt; (HKCU per-user, HKLM per-machine).</summary>
    public List<RegistryEntrySettings> Registry { get; set; } = [];
    /// <summary>Environment variables (per-user or machine-wide, following the install scope).</summary>
    public List<EnvironmentVariableSettings> Environment { get; set; } = [];
    /// <summary>Windows services of the app (per-machine installs only).</summary>
    public List<ServiceSettings> Services { get; set; } = [];
    /// <summary>Windows Defender Firewall allow rules for the app's programs (per-machine installs only).</summary>
    public List<FirewallRuleSettings> Firewall { get; set; } = [];
    /// <summary>Values the user enters during setup (a "Settings" page), used as {Input:id} in registry, config files, arguments.</summary>
    public List<InputSettings> Inputs { get; set; } = [];
    /// <summary>JSON or INI files the installer writes values into (e.g. a server address entered on the Settings page).</summary>
    public List<ConfigFileSettings> ConfigFiles { get; set; } = [];

    /// <summary>Unknown properties are kept so that newer files survive a round trip through an older tool.</summary>
    [JsonExtensionData]
    public Dictionary<string, object>? Extra { get; set; }
}

public sealed class AppInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Website { get; set; } = "";
    /// <summary>.ico or .png (up to 256×256), relative to the project file.</summary>
    public string Icon { get; set; } = "";
    /// <summary>Relative path of the program inside the installed files; used for shortcuts and "launch".</summary>
    public string MainExecutable { get; set; } = "";
    /// <summary>AppUserModelID set on the app's Start menu and desktop shortcuts (toast notifications of unpackaged apps need it), e.g. "Contoso.Notes".</summary>
    public string AppUserModelId { get; set; } = "";
    /// <summary>CLSID of the app's toast activator ("{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}"), set on those shortcuts together with the AppUserModelID.</summary>
    public string ToastActivatorClsid { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }
}

public sealed class FileSettings
{
    /// <summary>Folder whose contents are installed (relative to the project file).</summary>
    public string Source { get; set; } = "";
    public List<string> Include { get; set; } = ["**/*"];
    public List<string> Exclude { get; set; } = ["**/*.pdb", "**/*.ilk", "**/*.obj", "**/.git/**", "**/Thumbs.db", "**/.DS_Store"];
    /// <summary>Patterns of user-data files: installed only if missing, never overwritten on update, kept on uninstall.</summary>
    public List<string> Preserve { get; set; } = [];
    /// <summary>Additional files or folders from elsewhere, mapped into a target sub-folder.</summary>
    public List<ExtraSource> Extra { get; set; } = [];
    /// <summary>Empty folders to create below the install directory.</summary>
    public List<string> EmptyDirectories { get; set; } = [];
    /// <summary>
    /// Alternative builds of the app, e.g. framework-dependent ("shared") and self-contained. Each has its own source
    /// folder; files that are identical in every variant are stored once. The user picks one at install time.
    /// </summary>
    public List<VariantSettings> Variants { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, object>? ExtraProperties { get; set; }
}

public sealed class ExtraSource
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class VariantSettings
{
    /// <summary>Letters, digits, '-' and '_' (max 32); used by /variant and requirements[].variants.</summary>
    public string Id { get; set; } = "";
    public Dictionary<string, string> Label { get; set; } = [];
    public Dictionary<string, string> Description { get; set; } = [];
    public bool Default { get; set; }
    /// <summary>Folder with this build (e.g. a dotnet publish output). files.include/exclude/preserve apply; files.extra is added to every variant.</summary>
    public string Source { get; set; } = "";
}

public sealed class InstallSettings
{
    /// <summary>user | machine | userOrMachine</summary>
    public string Scope { get; set; } = "user";
    public string DefaultScope { get; set; } = "user";
    public string UserDir { get; set; } = @"{LocalAppData}\Programs\{AppName}";
    public string MachineDir { get; set; } = @"{ProgramFiles}\{AppName}";
    public bool AllowCustomDir { get; set; } = true;
    /// <summary>off | on | optionalOff | optionalOn</summary>
    public string DesktopShortcut { get; set; } = "optionalOff";
    public string StartMenuShortcut { get; set; } = "on";
    public string LaunchAfterInstall { get; set; } = "optionalOn";
    public bool AllowDowngrade { get; set; }
    public int MinWindowsBuild { get; set; } = 17763;
    /// <summary>Image names (e.g. "MyApp.exe") closed - politely, then forcibly - before installing or uninstalling.</summary>
    public List<string> CloseProcesses { get; set; } = [];
    /// <summary>Take over an installation of this app made by a previous installer (same uninstall key = app id).</summary>
    public bool AdoptExistingInstall { get; set; }
    /// <summary>Uninstall keys of older product names: adopted (their folder is upgraded) and removed after installing.</summary>
    public List<string> LegacyUninstallKeys { get; set; } = [];
    /// <summary>Asks a running copy to exit cleanly before files are replaced or removed, e.g. { "run": "App.exe", "arguments": ["--exit"] }; closeProcesses then handles the rest.</summary>
    public CommandSettings? CloseCommand { get; set; }
    /// <summary>Arguments for the app when an in-app updater ran Setup.exe with /relaunch (Inno: /RELAUNCH=1), e.g. ["--updated"].</summary>
    public List<string> RelaunchArguments { get; set; } = [];
    /// <summary>Start menu sub-folder for the app's shortcut (e.g. "Contoso"); empty = directly in Programs.</summary>
    public string StartMenuFolder { get; set; } = "";
    /// <summary>Folders of the app to put on PATH (user PATH for per-user installs, machine PATH for per-machine), e.g. ["cli"] or [""] for the install folder. Removed on uninstall.</summary>
    public List<string> AddToPath { get; set; } = [];
    /// <summary>.NET Framework assemblies (relative paths) to pre-compile with ngen (queued, best effort; per-machine installs).</summary>
    public List<string> Ngen { get; set; } = [];
    /// <summary>Leftovers of earlier versions removed during installation (before afterInstall actions run).</summary>
    public InstallCleanupSettings Cleanup { get; set; } = new();
    /// <summary>Registers app.mainExecutable under App Paths, so Win+R and other programs find it by its file name.</summary>
    public bool AppPaths { get; set; }
    /// <summary>auto: close (and afterwards restart) other programs that use files being replaced or removed (Windows Restart Manager) | off: only closeProcesses.</summary>
    public string RestartManager { get; set; } = "auto";
    /// <summary>fail: a file that is still in use stops the installation (rolled back) | reboot: per-machine installs replace it at the next restart (exit code 3010).</summary>
    public string LockedFiles { get; set; } = "fail";
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }
}

public sealed class UiSettings
{
    public string Title { get; set; } = "";
    public string WelcomeText { get; set; } = "";
    public string FinishText { get; set; } = "";
    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";
    /// <summary>mica | micaAlt | none</summary>
    public string Backdrop { get; set; } = "mica";
    public bool UseSystemAccent { get; set; } = true;
    public string AccentColor { get; set; } = "";
    /// <summary>Plain-text license file; when set the user must accept it before installing.</summary>
    public string License { get; set; } = "";
    /// <summary>Per-language license files (e.g. { "zh-CN": "license_zh.txt" }); falls back to <see cref="License"/>.</summary>
    public Dictionary<string, string> Licenses { get; set; } = [];
    /// <summary>firstInstall (default) | always: also before upgrades and reinstalls.</summary>
    public string LicenseMode { get; set; } = "firstInstall";
    /// <summary>Folder with one &lt;language&gt;.json per language (option labels, action names, license, strings).</summary>
    public string Localization { get; set; } = "";
    /// <summary>auto | en-US | zh-CN | ja-JP | ko-KR</summary>
    public string DefaultLanguage { get; set; } = "auto";
    /// <summary>Per-language overrides of installer strings, e.g. { "zh-CN": { "welcome.body": "…" } }.</summary>
    public Dictionary<string, Dictionary<string, string>> Strings { get; set; } = [];
    /// <summary>Image (.png, .jpg or .bmp, up to 1 MB; about 4:1, e.g. 960×240) shown at the top of the installer's first page.</summary>
    public string Banner { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }
}

public sealed class OutputSettings
{
    public string Directory { get; set; } = "dist";
    public string FileName { get; set; } = "{AppName}-{Version}-Setup.exe";
    /// <summary>x64 | arm64</summary>
    public string Architecture { get; set; } = "x64";
    /// <summary>fast | normal | max</summary>
    public string Compression { get; set; } = "normal";
    /// <summary>Variants to put into the package (files.variants ids); empty = all. One variant = no choice at install time.</summary>
    public List<string> Variants { get; set; } = [];
    /// <summary>Embed the WinUI 3 front end (falls back to Win32 automatically where it cannot run).</summary>
    public bool IncludeWinUI { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }
}

public sealed class SigningSettings
{
    public bool Enabled { get; set; }
    /// <summary>
    /// signtool (certificate store or .pfx, Windows) | trustedSigning (Azure Artifact Signing through signtool, Windows) |
    /// command (any signing tool, e.g. osslsigncode or jsign on Linux and macOS). "" = signtool.
    /// </summary>
    public string Provider { get; set; } = "signtool";
    public string CertificateThumbprint { get; set; } = "";
    public string CertificateFile { get; set; } = "";
    /// <summary>Name of an environment variable holding the PFX password (never stored in the project).</summary>
    public string PasswordEnvironmentVariable { get; set; } = "";
    public string TimestampUrl { get; set; } = "http://timestamp.digicert.com";
    public string SignToolPath { get; set; } = "";
    /// <summary>Also sign the uninstall.exe that setup writes into the install folder (UAC and SmartScreen then show the publisher when uninstalling).</summary>
    public bool SignUninstaller { get; set; } = true;
    /// <summary>Settings of the trustedSigning provider.</summary>
    public TrustedSigningSettings? TrustedSigning { get; set; }
    /// <summary>Settings of the command provider.</summary>
    public SigningCommand? Command { get; set; }
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }
}

/// <summary>Azure Artifact Signing (formerly Trusted Signing): signtool with Microsoft's Azure.CodeSigning.Dlib.dll.</summary>
public sealed class TrustedSigningSettings
{
    /// <summary>Regional endpoint of the account, e.g. https://eus.codesigning.azure.net.</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>Artifact Signing account name.</summary>
    public string AccountName { get; set; } = "";
    /// <summary>Certificate profile name.</summary>
    public string CertificateProfile { get; set; } = "";
    /// <summary>Path of Azure.CodeSigning.Dlib.dll (same architecture as signtool.exe).</summary>
    public string DlibPath { get; set; } = "";
    /// <summary>Optional opaque value that tags the signing requests (e.g. a build id).</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>DefaultAzureCredential sources to skip, e.g. ["ManagedIdentityCredential"].</summary>
    public List<string> ExcludeCredentials { get; set; } = [];
}

/// <summary>A signing tool run without a shell; <c>{file}</c> in <see cref="Arguments"/> is the file to sign.</summary>
public sealed class SigningCommand
{
    /// <summary>Executable: a name found on PATH, or a path (relative to the project file).</summary>
    public string Run { get; set; } = "";
    /// <summary>Arguments; placeholders {file}, {output}, {description}, {timestampUrl}.</summary>
    public List<string> Arguments { get; set; } = [];
    /// <summary>Extra environment variables for the tool (it also inherits the build's environment).</summary>
    public Dictionary<string, string> Environment { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 600;
}

public sealed class RequirementSettings
{
    /// <summary>Minimum .NET Framework 4.x version, e.g. "4.7.2"; empty = not required.</summary>
    public string NetFramework { get; set; } = "";
    /// <summary>Where to get .NET Framework when it is missing (bundled installer or download).</summary>
    public PrerequisiteSource? NetFrameworkSetup { get; set; }
    public WebView2Settings WebView2 { get; set; } = new();
    /// <summary>.NET (Core) shared runtimes, e.g. the Windows Desktop Runtime 8.</summary>
    public List<DotNetRequirement> DotNet { get; set; } = [];
    /// <summary>The Windows App SDK runtime (framework package) a framework-dependent WinUI 3 app needs.</summary>
    public WindowsAppRuntimeRequirement WindowsAppRuntime { get; set; } = new();
    /// <summary>Microsoft Visual C++ 2015-2022 (v14) Redistributable.</summary>
    public VcRedistRequirement VcRedist { get; set; } = new();
    /// <summary>Other prerequisites, detected through a registry value or a file.</summary>
    public List<CustomRequirement> Custom { get; set; } = [];
}

/// <summary>How setup obtains a missing prerequisite. Bundled installer first, then the download; neither = setup stops.</summary>
public class PrerequisiteSource
{
    /// <summary>Only needed for these variants (files.variants ids); empty = always.</summary>
    public List<string> Variants { get; set; } = [];
    /// <summary>
    /// Microsoft's official installer, without adding it yourself: "bundle" = downloaded when the package is built and
    /// stored in Setup.exe (extracted only if needed, never installed into the app folder); "download" = downloaded by
    /// setup when needed (it must be signed by Microsoft). "" = use <see cref="Installer"/> / <see cref="Download"/>.
    /// </summary>
    public string Auto { get; set; } = "";
    /// <summary>Relative path of an installer inside the installed files (add it with files.extra).</summary>
    public string Installer { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    /// <summary>Download from the internet (https, verified with SHA-256) when nothing is bundled.</summary>
    public DownloadSettings? Download { get; set; }
    /// <summary>https link shown to the user when the prerequisite is missing and cannot be installed by setup.</summary>
    public string HelpUrl { get; set; } = "";
    /// <summary>warn (default): install the app anyway and report it | fail: if it is still missing or too old after its
    /// installer ran, stop and roll the installation back.</summary>
    public string OnFailure { get; set; } = "warn";
}

public sealed class DownloadSettings
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>Exact size in bytes (0 = unknown).</summary>
    public long Size { get; set; }
    /// <summary>Instead of (or with) the SHA-256: the file must carry a valid Authenticode signature issued to this name, e.g. "Microsoft Corporation" for a vendor bootstrapper that changes over time.</summary>
    public string Signer { get; set; } = "";
}

public sealed class VcRedistRequirement : PrerequisiteSource
{
    public bool Required { get; set; }
    /// <summary>Minimum version, e.g. "14.40" (empty = any v14 runtime).</summary>
    public string MinVersion { get; set; } = "";
    /// <summary>x64 | x86 | arm64; empty = the package architecture.</summary>
    public string Architecture { get; set; } = "";
    public VcRedistRequirement() => Arguments = ["/install", "/quiet", "/norestart"];
}

public sealed class CustomRequirement : PrerequisiteSource
{
    /// <summary>Letters, digits, '-' and '_' (max 32).</summary>
    public string Id { get; set; } = "";
    /// <summary>Shown to the user, e.g. "Contoso Driver 2".</summary>
    public string Name { get; set; } = "";
    public RequirementDetection Detect { get; set; } = new();
}

/// <summary>How setup finds out that a custom prerequisite is present: a registry value (with an optional minimum version) or a file.</summary>
public sealed class RequirementDetection
{
    public RegistryVersionCondition? Registry { get; set; }
    public FileDetection? File { get; set; }
}

public sealed class FileDetection
{
    /// <summary>{ProgramFiles}\X, {ProgramFilesX86}\X, {System}\X, {CommonAppData}\X, {LocalAppData}\X or {AppData}\X.</summary>
    public string Path { get; set; } = "";
    /// <summary>Minimum file version (from its version resource); empty = the file only has to exist.</summary>
    public string Version { get; set; } = "";
}

public sealed class WebView2Settings : PrerequisiteSource
{
    public bool Required { get; set; }
    /// <summary>Minimum runtime version, e.g. "120.0.2210.91"; empty = any.</summary>
    public string MinVersion { get; set; } = "";
    public WebView2Settings() => Arguments = ["/silent", "/install"];
}

public sealed class WindowsAppRuntimeRequirement : PrerequisiteSource
{
    public bool Required { get; set; }
    /// <summary>Minimum runtime version, e.g. "2.5.1" (Windows App SDK 2.x) or "1.6" (1.x: any 1.6 runtime).</summary>
    public string Version { get; set; } = "";
    public WindowsAppRuntimeRequirement() => Arguments = ["--quiet"];
}

public sealed class DotNetRequirement : PrerequisiteSource
{
    /// <summary>desktop (Microsoft.WindowsDesktop.App) | core (Microsoft.NETCore.App) | aspnetcore (Microsoft.AspNetCore.App)</summary>
    public string Runtime { get; set; } = "desktop";
    /// <summary>Minimum version, e.g. "8.0.0".</summary>
    public string Version { get; set; } = "";
    /// <summary>patch | minor (default, like .NET itself) | major: which newer installed versions count.</summary>
    public string RollForward { get; set; } = "minor";
    public DotNetRequirement() => Arguments = ["/install", "/quiet", "/norestart"];
}

public sealed class UserOptionSettings
{
    public string Id { get; set; } = "";
    /// <summary>Label per language, e.g. { "en-US": "Start when I sign in", "zh-CN": "登录时启动" }.</summary>
    public Dictionary<string, string> Label { get; set; } = [];
    public bool Default { get; set; }
    /// <summary>Value in quiet and passive installs that are not updates (Inno Setup "skipifsilent"); null = <see cref="Default"/>.</summary>
    public bool? QuietDefault { get; set; }
    /// <summary>A component that is always installed: shown checked and locked (e.g. a driver the app needs).</summary>
    public bool Required { get; set; }
    /// <summary>On updates, start from what is on the PC: selected if this Run value or scheduled task exists. A startup option detects its own Run value.</summary>
    public OptionDetectSettings? Detect { get; set; }
    /// <summary>While selected, start the main executable with Windows: HKCU\...\Run\&lt;name&gt; = "&lt;exe&gt;" &lt;arguments&gt;. Removed on uninstall.</summary>
    public RunValueSettings? Startup { get; set; }
}

public sealed class OptionDetectSettings
{
    /// <summary>Name of a HKCU/HKLM ...\CurrentVersion\Run value.</summary>
    public string RunValue { get; set; } = "";
    /// <summary>Scheduled task (folders allowed: "App\Task").</summary>
    public string ScheduledTask { get; set; } = "";
}

public sealed class CommandSettings
{
    /// <summary>Relative path of an .exe of the installed app.</summary>
    public string Run { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
}

public sealed class ActionSettings
{
    /// <summary>Optional id, used to translate <see cref="Name"/> in localization files.</summary>
    public string Id { get; set; } = "";
    /// <summary>afterInstall | beforeUninstall</summary>
    public string When { get; set; } = "afterInstall";
    /// <summary>Relative path of an .exe inside the installed files.</summary>
    public string Run { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 300;
    public List<int> SuccessExitCodes { get; set; } = [0];
    /// <summary>fail (roll the installation back) | warn (tell the user) | ignore</summary>
    public string OnFailure { get; set; } = "fail";
    /// <summary>Run only when this option is selected.</summary>
    public string IfOption { get; set; } = "";
    /// <summary>Skip when this registry value already holds at least this version (e.g. a driver already installed).</summary>
    public RegistryVersionCondition? SkipIfInstalled { get; set; }
    public Dictionary<string, string> Name { get; set; } = [];
    /// <summary>When the program does not succeed: run it again visibly with these arguments (e.g. an interactive installer).</summary>
    public List<string>? FallbackArguments { get; set; }
    /// <summary>When it still does not succeed: register it under HKCU\...\Run instead (e.g. autostart without a scheduled task).</summary>
    public RunValueSettings? FallbackRunValue { get; set; }
}

public sealed class RunValueSettings
{
    public string Name { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
}

public sealed class InstallCleanupSettings
{
    public List<string> RemoveScheduledTasks { get; set; } = [];
    public List<string> RemoveRunValues { get; set; } = [];
    /// <summary>Shortcut file names (e.g. "Old Name.lnk") removed from Start menu, Desktop and Startup folders.</summary>
    public List<string> RemoveShortcuts { get; set; } = [];
    /// <summary>Uninstall entries of other (older) product names to remove.</summary>
    public List<string> RemoveUninstallKeys { get; set; } = [];
    /// <summary>Files in the install folder left by an earlier installer, e.g. "unins*.exe" (wildcards in the file name only).</summary>
    public List<string> RemoveFiles { get; set; } = [];
}

public sealed class RegistryVersionCondition
{
    /// <summary>HKLM | HKCU</summary>
    public string Hive { get; set; } = "HKLM";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "DisplayVersion";
    public string Version { get; set; } = "";
}

/// <summary>
/// Built-in updates: the installed <c>uninstall.exe</c> looks for a newer Setup.exe of the same app, verifies it and runs
/// it. Off unless <see cref="GitHub"/> or <see cref="Feed"/> is set.
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>GitHub repository "owner/name" whose releases carry the new Setup.exe.</summary>
    [JsonPropertyName("github")]
    public string GitHub { get; set; } = "";
    /// <summary>
    /// Or an https URL of a JSON document describing the latest version:
    /// <c>{ "version": "1.2.0", "url": "https://…/Setup.exe", "sha256": "…", "size": 123, "notes": "https://…" }</c>.
    /// </summary>
    public string Feed { get; set; } = "";
    /// <summary>GitHub: file name of Setup.exe in a release; {Version} is the release's version. Empty = output.fileName.</summary>
    public string Asset { get; set; } = "";
    /// <summary>GitHub: also offer pre-releases.</summary>
    public bool Prerelease { get; set; }
    /// <summary>
    /// GitHub: a release file with "&lt;sha256&gt;  &lt;file name&gt;" lines (e.g. SHA256SUMS.txt). Empty = GitHub's own
    /// SHA-256 digest of the asset.
    /// </summary>
    public string Checksums { get; set; } = "";
    /// <summary>
    /// If set, a new Setup.exe is only run when it carries a valid Authenticode signature from a certificate issued to
    /// this name (the certificate's subject common name, e.g. "Contoso Ltd").
    /// </summary>
    public string Signer { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }

    [JsonIgnore] public bool Enabled => !string.IsNullOrWhiteSpace(GitHub) || !string.IsNullOrWhiteSpace(Feed);
}

public sealed class UninstallSettings
{
    /// <summary>App-created data folders removed on uninstall: {CommonAppData}\X, {LocalAppData}\X or {AppData}\X only.</summary>
    public List<string> RemoveData { get; set; } = [];
    /// <summary>Names of scheduled tasks (root folder) the app registered.</summary>
    public List<string> RemoveScheduledTasks { get; set; } = [];
    /// <summary>Names of HKCU/HKLM ...\CurrentVersion\Run values the app registered.</summary>
    public List<string> RemoveRunValues { get; set; } = [];
    /// <summary>Shortcut file names removed from Start menu, Desktop and Startup folders (e.g. legacy names).</summary>
    public List<string> RemoveShortcuts { get; set; } = [];
    /// <summary>Per-user registry keys the app or an older installer created, e.g. "HKCU\Software\Contoso\App".</summary>
    public List<string> RemoveRegistryKeys { get; set; } = [];
    /// <summary>Settings and data the user decides about when uninstalling (checkbox; /remove-user-data in scripts).</summary>
    public UserDataSettings UserData { get; set; } = new();
    /// <summary>Changes the app made with administrator rights, undone on uninstall (one UAC prompt for per-user installs).</summary>
    public ElevatedCleanupSettings Elevated { get; set; } = new();
}

public sealed class UserDataSettings
{
    /// <summary>{LocalAppData}\X, {AppData}\X or {CommonAppData}\X files or folders.</summary>
    public List<string> Paths { get; set; } = [];
    /// <summary>Checkbox pre-selected (delete) in interactive uninstalls; silent uninstalls keep data unless /remove-user-data.</summary>
    public bool Default { get; set; }
}

public sealed class ElevatedCleanupSettings
{
    /// <summary>Only when one of these exists (files or folders; empty = always). Checked before user data is removed.</summary>
    public List<string> IfExists { get; set; } = [];
    /// <summary>ETW sessions to stop, including their boot-time autologger ("autosession\name" in logman).</summary>
    public List<string> TraceSessions { get; set; } = [];
    /// <summary>Scheduled tasks, folders allowed ("App\Task"); an emptied folder is removed.</summary>
    public List<string> ScheduledTasks { get; set; } = [];
    /// <summary>Remove allow-entries for user accounts (S-1-5-21-...) with exactly these SDDL rights from a service's DACL.</summary>
    public List<ServiceAccessSettings> ServiceAccess { get; set; } = [];
    public List<string> RemoveData { get; set; } = [];
    /// <summary>Registry keys removed with administrator rights, e.g. "HKLM\Software\Contoso\App".</summary>
    public List<string> RemoveRegistryKeys { get; set; } = [];
}

public sealed class ServiceAccessSettings
{
    public string Service { get; set; } = "";
    /// <summary>SDDL rights strings, e.g. "LCRPWP".</summary>
    public List<string> Rights { get; set; } = [];
}

/// <summary>An additional shortcut. Created and removed like the app's own shortcuts (recorded in the install record).</summary>
public sealed class ShortcutSettings
{
    /// <summary>Shortcut name without ".lnk" (max 128), e.g. "Contoso Notes (Safe mode)".</summary>
    public string Name { get; set; } = "";
    /// <summary>Relative path of a file among the installed files; empty = app.mainExecutable.</summary>
    public string Target { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string Description { get; set; } = "";
    /// <summary>Relative path of an .ico, .exe or .dll among the installed files; empty = the target.</summary>
    public string Icon { get; set; } = "";
    public int IconIndex { get; set; }
    /// <summary>Relative folder inside the install folder; empty = the target's folder.</summary>
    public string WorkingDirectory { get; set; } = "";
    /// <summary>startMenu | desktop | startup</summary>
    public string Location { get; set; } = "startMenu";
    /// <summary>Start menu sub-folder; empty = install.startMenuFolder.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Created only while this option is selected.</summary>
    public string IfOption { get; set; } = "";
    /// <summary>AppUserModelID of this shortcut (optional).</summary>
    public string AppUserModelId { get; set; } = "";
}

/// <summary>A file type the app opens. Registered per user (HKCU) or for the PC (HKLM), following the install scope.</summary>
public sealed class FileAssociationSettings
{
    /// <summary>File name extensions including the dot, e.g. [".cnote", ".cnotes"].</summary>
    public List<string> Extensions { get; set; } = [];
    /// <summary>Programmatic identifier, e.g. "Contoso.Notes.Document" (max 39 characters).</summary>
    public string ProgId { get; set; } = "";
    /// <summary>File type name shown in Explorer, e.g. "Contoso Notes document".</summary>
    public string Description { get; set; } = "";
    /// <summary>Relative path of an .ico, .exe or .dll among the installed files; empty = the target.</summary>
    public string Icon { get; set; } = "";
    public int IconIndex { get; set; }
    /// <summary>Relative path of the program that opens the files; empty = app.mainExecutable.</summary>
    public string Target { get; set; } = "";
    /// <summary>Command-line arguments; "%1" is the file.</summary>
    public List<string> Arguments { get; set; } = ["%1"];
    /// <summary>Optional MIME type, e.g. "application/x-contoso-note".</summary>
    public string ContentType { get; set; } = "";
    public string IfOption { get; set; } = "";
}

/// <summary>A URL scheme (protocol) the app handles, e.g. "contoso-notes".</summary>
public sealed class ProtocolSettings
{
    /// <summary>Lowercase scheme without "://", e.g. "contoso-notes".</summary>
    public string Scheme { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public int IconIndex { get; set; }
    /// <summary>Relative path of the program that handles the links; empty = app.mainExecutable.</summary>
    public string Target { get; set; } = "";
    /// <summary>Command-line arguments; "%1" is the URL.</summary>
    public List<string> Arguments { get; set; } = ["%1"];
    public string IfOption { get; set; } = "";
}

/// <summary>A registry key with values, below HKCU (per-user installs) or HKLM (per-machine installs).</summary>
public sealed class RegistryEntrySettings
{
    /// <summary>Key below the hive, e.g. "Software\Contoso\Notes" (Software\&lt;Vendor&gt; or deeper).</summary>
    public string Key { get; set; } = "";
    public List<RegistryValueSettings> Values { get; set; } = [];
    /// <summary>values: remove the values written (and the key once empty) | key: remove the whole key | keep</summary>
    public string Uninstall { get; set; } = "values";
    public string IfOption { get; set; } = "";
}

public sealed class RegistryValueSettings
{
    /// <summary>Value name; empty = the key's default value.</summary>
    public string Name { get; set; } = "";
    /// <summary>string | expandString | dword | qword | multiString</summary>
    public string Type { get; set; } = "string";
    /// <summary>Text; numbers in decimal or 0x hex; multiString lines separated by "\n". Placeholders: {InstallDir}, {MainExecutable}, {AppVersion}, {AppId}, {Input:id}.</summary>
    public string Data { get; set; } = "";
}

public sealed class EnvironmentVariableSettings
{
    public string Name { get; set; } = "";
    /// <summary>Placeholders: {InstallDir}, {MainExecutable}, {AppVersion}, {AppId}, {Input:id}.</summary>
    public string Value { get; set; } = "";
    /// <summary>Store as REG_EXPAND_SZ (the value refers to other variables such as %USERPROFILE%).</summary>
    public bool Expand { get; set; }
    public string IfOption { get; set; } = "";
}

/// <summary>A Windows service run from one of the app's programs (per-machine installs only).</summary>
public sealed class ServiceSettings
{
    /// <summary>Service (key) name: letters, digits, '.', '-', '_' (max 80).</summary>
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Relative path of the service .exe among the installed files.</summary>
    public string Run { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    /// <summary>auto | delayedAuto | manual | disabled</summary>
    public string StartType { get; set; } = "auto";
    /// <summary>localSystem | localService | networkService</summary>
    public string Account { get; set; } = "localSystem";
    /// <summary>Start the service after installing.</summary>
    public bool Start { get; set; } = true;
    /// <summary>Names of services this one needs.</summary>
    public List<string> Dependencies { get; set; } = [];
    /// <summary>Restart the service when it fails (after 5 s, 30 s, 60 s).</summary>
    public bool RestartOnFailure { get; set; } = true;
}

/// <summary>A Windows Defender Firewall allow rule for one of the app's programs (per-machine installs only).</summary>
public sealed class FirewallRuleSettings
{
    /// <summary>Rule name shown in the firewall settings.</summary>
    public string Name { get; set; } = "";
    /// <summary>Relative path of the program; empty = app.mainExecutable.</summary>
    public string Program { get; set; } = "";
    /// <summary>in | out</summary>
    public string Direction { get; set; } = "in";
    /// <summary>any | tcp | udp</summary>
    public string Protocol { get; set; } = "any";
    /// <summary>tcp/udp only: e.g. "8080" or "8000-8010,9000"; empty = any port.</summary>
    public string LocalPorts { get; set; } = "";
    /// <summary>domain, private, public; empty = all.</summary>
    public List<string> Profiles { get; set; } = [];
    public string IfOption { get; set; } = "";
}

/// <summary>A value the user enters on the installer's "Settings" page (or with /input id=value).</summary>
public sealed class InputSettings
{
    /// <summary>Letters, digits, '-' and '_' (max 32); referenced as {Input:id}.</summary>
    public string Id { get; set; } = "";
    public Dictionary<string, string> Label { get; set; } = [];
    /// <summary>Help text under the field.</summary>
    public Dictionary<string, string> Description { get; set; } = [];
    /// <summary>text | password | choice</summary>
    public string Type { get; set; } = "text";
    public string Default { get; set; } = "";
    public bool Required { get; set; }
    /// <summary>Regular expression the whole value must match (e.g. "^https://"); empty = anything.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>type choice: the values to choose from.</summary>
    public List<InputChoiceSettings> Choices { get; set; } = [];
    /// <summary>Keep the value for updates (never for passwords).</summary>
    public bool Remember { get; set; } = true;
}

public sealed class InputChoiceSettings
{
    public string Value { get; set; } = "";
    public Dictionary<string, string> Label { get; set; } = [];
}

/// <summary>A JSON or INI file the installer writes values into; existing files keep their other settings.</summary>
public sealed class ConfigFileSettings
{
    /// <summary>Relative to the install folder, or {LocalAppData}\X, {AppData}\X, {CommonAppData}\X.</summary>
    public string Path { get; set; } = "";
    /// <summary>json | ini</summary>
    public string Format { get; set; } = "json";
    public List<ConfigValueSettings> Values { get; set; } = [];
    public string IfOption { get; set; } = "";
}

public sealed class ConfigValueSettings
{
    /// <summary>json: dotted path such as "server.url"; ini: the key name.</summary>
    public string Key { get; set; } = "";
    /// <summary>ini only: the section.</summary>
    public string Section { get; set; } = "";
    /// <summary>Placeholders: {InstallDir}, {MainExecutable}, {AppVersion}, {AppId}, {Input:id}.</summary>
    public string Value { get; set; } = "";
    /// <summary>json: string | number | bool</summary>
    public string Type { get; set; } = "string";
}