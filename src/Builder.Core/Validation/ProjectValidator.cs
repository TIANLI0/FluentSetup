using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Validation;

/// <summary>Static checks of a project. File-system checks that need the payload are done by <see cref="FileCollector"/>.</summary>
public static partial class ProjectValidator
{
    private static readonly string[] Offers = ["off", "on", "optionalOff", "optionalOn"];
    private static readonly string[] DirTokens = ["{LocalAppData}", "{UserProgramFiles}", "{AppData}", "{ProgramFiles}", "{ProgramData}", "{AppName}", "{Publisher}", "{AppId}"];

    [GeneratedRegex(@"^\d{1,10}(\.\d{1,10}){0,3}(-[0-9A-Za-z.\-]+)?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorRegex();

    /// <summary>Same rules as the installer (src/Common/version.cpp): 1-4 numbers, each fitting in 32 bits, optional -tag.</summary>
    public static bool IsValidVersion(string v) =>
        v.Length <= 64 && VersionRegex().IsMatch(v) && v.Split('-')[0].Split('.').All(n => ulong.Parse(n, System.Globalization.CultureInfo.InvariantCulture) <= uint.MaxValue);

    public static DiagnosticBag Validate(SetupProject p, string projectDirectory)
    {
        var d = new DiagnosticBag();
        var app = p.App;

        if (string.IsNullOrWhiteSpace(app.Id)) d.Error("FS1001", "app.id", "App ID is required (for example \"Contoso.Notes\").");
        else if (!PathRules.IsValidAppId(app.Id)) d.Error("FS1002", "app.id", "App ID may only contain letters, digits, '.', '-' and '_' (max 64) and must not start or end with '.'.");
        if (string.IsNullOrWhiteSpace(app.Name)) d.Error("FS1003", "app.name", "App name is required.");
        else if (app.Name.Length > 128) d.Error("FS1004", "app.name", "App name is longer than 128 characters.");
        if (app.Publisher.Length > 128) d.Error("FS1005", "app.publisher", "Publisher is longer than 128 characters.");
        if (string.IsNullOrWhiteSpace(app.Publisher)) d.Warning("FS1006", "app.publisher", "No publisher: Windows Settings will show the app without a publisher.");
        if (!IsValidVersion(app.Version)) d.Error("FS1007", "app.version", $"'{app.Version}' is not a valid version. Use 1-4 numbers separated by dots, optionally followed by -tag (for example 1.2.0 or 2.0.0-beta.1).");
        if (app.Description.Length > 1024) d.Error("FS1008", "app.description", "Description is longer than 1024 characters.");
        if (!string.IsNullOrEmpty(app.Website) &&
            (!Uri.TryCreate(app.Website, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            d.Error("FS1009", "app.website", "Website must be an absolute http:// or https:// URL.");
        }
        if (!string.IsNullOrEmpty(app.Icon))
        {
            var icon = Resolve(projectDirectory, app.Icon);
            if (!File.Exists(icon)) d.Error("FS1010", "app.icon", $"Icon file not found: {icon}");
            else if (!icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                d.Error("FS1011", "app.icon", "Icon must be an .ico or .png file.");
        }
        else
        {
            d.Info("FS1012", "app.icon", "No icon set: the default FluentSetup icon is used.");
        }
        if (!string.IsNullOrEmpty(app.MainExecutable))
        {
            if (!PathRules.TryNormalizeRelative(app.MainExecutable, out var norm, out var why)) d.Error("FS1013", "app.mainExecutable", $"Main executable path is not valid: {why}.");
            else if (!norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) d.Error("FS1014", "app.mainExecutable", "Main executable must be an .exe file.");
        }
        else
        {
            d.Warning("FS1015", "app.mainExecutable", "No main executable: no shortcuts are created and the app cannot be launched after installation.");
        }

        // files
        if (string.IsNullOrWhiteSpace(p.Files.Source)) d.Error("FS1101", "files.source", "Choose the folder that contains the app's files.");
        else if (!Directory.Exists(Resolve(projectDirectory, p.Files.Source))) d.Error("FS1102", "files.source", $"Source folder not found: {Resolve(projectDirectory, p.Files.Source)}");
        foreach (var (extra, i) in p.Files.Extra.Select((e, i) => (e, i)))
        {
            var src = Resolve(projectDirectory, extra.Source);
            if (!File.Exists(src) && !Directory.Exists(src)) d.Error("FS1103", $"files.extra[{i}].source", $"Extra source not found: {src}");
            if (!string.IsNullOrEmpty(extra.Target) && !PathRules.TryNormalizeRelative(extra.Target, out _, out var why))
                d.Error("FS1104", $"files.extra[{i}].target", $"Target folder is not a valid relative path: {why}.");
        }
        foreach (var (dir, i) in p.Files.EmptyDirectories.Select((e, i) => (e, i)))
        {
            if (!PathRules.TryNormalizeRelative(dir, out _, out var why)) d.Error("FS1105", $"files.emptyDirectories[{i}]", $"Not a valid relative folder: {why}.");
        }
        foreach (var (pattern, i) in p.Files.Include.Concat(p.Files.Exclude).Concat(p.Files.Preserve).Select((e, i) => (e, i)))
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(pattern))
                d.Error("FS1106", "files", $"Pattern '{pattern}' is not a relative glob.");
        }

        // install
        var inst = p.Install;
        if (inst.Scope is not ("user" or "machine" or "userOrMachine")) d.Error("FS1201", "install.scope", "Scope must be user, machine or userOrMachine.");
        if (inst.DefaultScope is not ("user" or "machine")) d.Error("FS1202", "install.defaultScope", "Default scope must be user or machine.");
        CheckDirTemplate(d, "install.userDir", inst.UserDir);
        CheckDirTemplate(d, "install.machineDir", inst.MachineDir);
        if (inst.Scope is "user" or "userOrMachine" && inst.UserDir.Contains("{ProgramFiles}", StringComparison.OrdinalIgnoreCase))
            d.Error("FS1203", "install.userDir", "A per-user install cannot go to Program Files (it requires administrator rights). Use {LocalAppData}\\Programs.");
        if (inst.Scope is "machine" or "userOrMachine" &&
            (inst.MachineDir.Contains("{LocalAppData}", StringComparison.OrdinalIgnoreCase) || inst.MachineDir.Contains("{AppData}", StringComparison.OrdinalIgnoreCase)))
            d.Error("FS1204", "install.machineDir", "A per-machine install must not go into one user's profile.");
        foreach (var (name, value) in new[] { ("install.desktopShortcut", inst.DesktopShortcut), ("install.startMenuShortcut", inst.StartMenuShortcut), ("install.launchAfterInstall", inst.LaunchAfterInstall) })
        {
            if (!Offers.Contains(value)) d.Error("FS1205", name, $"'{value}' is not one of off, on, optionalOff, optionalOn.");
        }
        if (inst.LaunchAfterInstall == "on") d.Error("FS1206", "install.launchAfterInstall", "Launching without asking is not supported; use optionalOn.");
        if (inst.MinWindowsBuild is < 0 or > 100000) d.Error("FS1207", "install.minWindowsBuild", "Minimum Windows build is out of range.");
        if (inst.MinWindowsBuild is > 0 and < 17763) d.Warning("FS1208", "install.minWindowsBuild", "FluentSetup itself needs Windows 10 version 1809 (build 17763) or later.");

        // ui
        var ui = p.Ui;
        if (ui.Theme is not ("system" or "light" or "dark")) d.Error("FS1301", "ui.theme", "Theme must be system, light or dark.");
        if (ui.Backdrop is not ("mica" or "micaAlt" or "none")) d.Error("FS1302", "ui.backdrop", "Backdrop must be mica, micaAlt or none.");
        if (!ui.UseSystemAccent && !ColorRegex().IsMatch(ui.AccentColor)) d.Error("FS1303", "ui.accentColor", "Accent colour must look like #0067C0 when the system accent is not used.");
        if (ui.DefaultLanguage is not ("auto" or "en-US" or "zh-CN")) d.Error("FS1304", "ui.defaultLanguage", "Default language must be auto, en-US or zh-CN.");
        if (!string.IsNullOrEmpty(ui.License))
        {
            var lic = Resolve(projectDirectory, ui.License);
            if (!File.Exists(lic)) d.Error("FS1305", "ui.license", $"License file not found: {lic}");
            else if (new FileInfo(lic).Length > 1024 * 1024) d.Error("FS1306", "ui.license", "License file is larger than 1 MB.");
        }
        foreach (var lang in ui.Strings.Keys)
        {
            if (lang is not ("en-US" or "zh-CN")) d.Warning("FS1307", $"ui.strings.{lang}", $"Strings for '{lang}' are ignored: the installer supports en-US and zh-CN.");
        }
        foreach (var (field, text, max) in new[] { ("ui.title", ui.Title, 128), ("ui.welcomeText", ui.WelcomeText, 2048), ("ui.finishText", ui.FinishText, 2048) })
        {
            if (text.Length > max) d.Error("FS1308", field, $"Text is longer than {max} characters.");
        }

        // output
        var o = p.Output;
        if (o.Architecture is not ("x64" or "arm64")) d.Error("FS1401", "output.architecture", "Architecture must be x64 or arm64.");
        if (o.Compression is not ("fast" or "normal" or "max")) d.Error("FS1402", "output.compression", "Compression must be fast, normal or max.");
        var fileName = ExpandFileName(o.FileName, p);
        if (string.IsNullOrWhiteSpace(fileName) || !PathRules.IsValidComponent(fileName, out _) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            d.Error("FS1403", "output.fileName", "Output file name must be a valid file name ending in .exe.");
        if (string.IsNullOrWhiteSpace(o.Directory)) d.Error("FS1404", "output.directory", "Choose an output folder.");

        foreach (var (lang, file) in ui.Licenses)
        {
            if (lang is not ("en-US" or "zh-CN")) d.Warning("FS1309", $"ui.licenses.{lang}", $"License for '{lang}' is ignored: the installer supports en-US and zh-CN.");
            if (!File.Exists(Resolve(projectDirectory, file))) d.Error("FS1310", $"ui.licenses.{lang}", $"License file not found: {Resolve(projectDirectory, file)}");
            else if (new FileInfo(Resolve(projectDirectory, file)).Length > 1024 * 1024) d.Error("FS1306", $"ui.licenses.{lang}", "License file is larger than 1 MB.");
        }
        ExtensionValidator.Validate(p, d);

        // signing
        var s = p.Signing;
        if (s.Enabled)
        {
            if (string.IsNullOrEmpty(s.CertificateThumbprint) && string.IsNullOrEmpty(s.CertificateFile))
                d.Error("FS1501", "signing", "Signing needs a certificate thumbprint (from the certificate store) or a .pfx file.");
            if (!string.IsNullOrEmpty(s.CertificateFile) && !File.Exists(Resolve(projectDirectory, s.CertificateFile)))
                d.Error("FS1502", "signing.certificateFile", "Certificate file not found.");
            if (!string.IsNullOrEmpty(s.TimestampUrl) && !Uri.TryCreate(s.TimestampUrl, UriKind.Absolute, out _))
                d.Error("FS1503", "signing.timestampUrl", "Timestamp URL is not valid.");
        }
        return d;
    }

    private static void CheckDirTemplate(DiagnosticBag d, string field, string template)
    {
        if (string.IsNullOrWhiteSpace(template)) { d.Error("FS1210", field, "Install folder is required."); return; }
        var rest = template;
        foreach (var t in DirTokens) rest = rest.Replace(t, "X", StringComparison.Ordinal);
        if (rest.Contains('{') || rest.Contains('}')) d.Error("FS1211", field, $"Unknown placeholder in '{template}'. Known: {string.Join(", ", DirTokens)}.");
        if (!template.StartsWith('{') && !(template.Length > 2 && template[1] == ':'))
            d.Error("FS1212", field, "Install folder must start with a known folder placeholder such as {ProgramFiles} or an absolute path.");
    }

    public static string Resolve(string projectDirectory, string path) =>
        string.IsNullOrEmpty(path) ? path : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectDirectory, path));

    public static string ExpandFileName(string template, SetupProject p)
    {
        static string Clean(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars())).Trim();
        return template.Replace("{AppName}", Clean(p.App.Name), StringComparison.Ordinal)
                       .Replace("{AppId}", Clean(p.App.Id), StringComparison.Ordinal)
                       .Replace("{Version}", Clean(p.App.Version), StringComparison.Ordinal)
                       .Replace("{Architecture}", p.Output.Architecture, StringComparison.Ordinal);
    }
}
