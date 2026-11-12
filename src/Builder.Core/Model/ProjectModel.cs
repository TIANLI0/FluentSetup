using System.Text.Json.Serialization;

namespace FluentSetup.Builder.Core.Model;

/// <summary>A FluentSetup project (fluentsetup.json). See shared/schemas/fluentsetup.project.schema.json.</summary>
public sealed class SetupProject
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("$schema")]
    public string? Schema { get; set; } = "https://fluentsetup.dev/schemas/fluentsetup.project.schema.json";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
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
    /// <summary>.NET Framework assemblies (relative paths) to pre-compile with ngen (queued, best effort; per-machine installs).</summary>
    public List<string> Ngen { get; set; } = [];
    /// <summary>Leftovers of earlier versions removed during installation (before afterInstall actions run).</summary>
    public InstallCleanupSettings Cleanup { get; set; } = new();
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
    public string CertificateThumbprint { get; set; } = "";
    public string CertificateFile { get; set; } = "";
    /// <summary>Name of an environment variable holding the PFX password (never stored in the project).</summary>
    public string PasswordEnvironmentVariable { get; set; } = "";
    public string TimestampUrl { get; set; } = "http://timestamp.digicert.com";
    public string SignToolPath { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, object>? Extra { get; set; }
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
}

/// <summary>How setup obtains a missing prerequisite. Bundled installer first, then the download; neither = setup stops.</summary>
public class PrerequisiteSource
{
    /// <summary>Only needed for these variants (files.variants ids); empty = always.</summary>
    public List<string> Variants { get; set; } = [];
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
    /// <summary>A component that is always installed: shown checked and locked (e.g. a driver the app needs).</summary>
    public bool Required { get; set; }
    /// <summary>While selected, start the main executable with Windows: HKCU\...\Run\&lt;name&gt; = "&lt;exe&gt;" &lt;arguments&gt;. Removed on uninstall.</summary>
    public RunValueSettings? Startup { get; set; }
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
}

public sealed class RegistryVersionCondition
{
    /// <summary>HKLM | HKCU</summary>
    public string Hive { get; set; } = "HKLM";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "DisplayVersion";
    public string Version { get; set; } = "";
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
}