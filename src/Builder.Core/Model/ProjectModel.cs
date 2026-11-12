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
    [JsonExtensionData] public Dictionary<string, object>? ExtraProperties { get; set; }
}

public sealed class ExtraSource
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
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
    /// <summary>auto | en-US | zh-CN</summary>
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
