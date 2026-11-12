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
        if (p.Files.Variants.Count > 0)
        {
            // Variants bring their own source folders; files.source is not used.
            var variantIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (v, i) in p.Files.Variants.Select((v, i) => (v, i)))
            {
                var field = $"files.variants[{i}]";
                if (!System.Text.RegularExpressions.Regex.IsMatch(v.Id, "^[A-Za-z0-9_-]{1,32}$")) d.Error("FS1116", field + ".id", "Variant ids use letters, digits, - and _ (max 32).");
                else if (!variantIds.Add(v.Id)) d.Error("FS1111", field + ".id", $"Duplicate variant id '{v.Id}'.");
                if (v.Label.Count == 0 && string.IsNullOrEmpty(p.Ui.Localization)) d.Error("FS1112", field + ".label", "Give the variant a label (per language, or in the localization files).");
                if (string.IsNullOrWhiteSpace(v.Source)) d.Error("FS1101", field + ".source", "Choose the folder with this build.");
                else if (!Directory.Exists(Resolve(projectDirectory, v.Source))) d.Error("FS1102", field + ".source", $"Source folder not found: {Resolve(projectDirectory, v.Source)}");
            }
            if (p.Files.Variants.Count > 8) d.Error("FS1113", "files.variants", "At most 8 variants.");
            if (p.Files.Variants.Count(v => v.Default) > 1) d.Error("FS1114", "files.variants", "Only one variant can be the default.");
            if (!string.IsNullOrWhiteSpace(p.Files.Source)) d.Warning("FS1115", "files.source", "Ignored: with files.variants each variant has its own source.");
            foreach (var id in p.Output.Variants.Where(id => !variantIds.Contains(id))) d.Error("FS1662", "output.variants", $"No variant '{id}' in files.variants.");
        }
        else if (string.IsNullOrWhiteSpace(p.Files.Source)) d.Error("FS1101", "files.source", "Choose the folder that contains the app's files.");
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
        if (ui.DefaultLanguage != "auto" && !Localization.IsLanguageTag(ui.DefaultLanguage)) d.Error("FS1304", "ui.defaultLanguage", "Default language must be auto or a language tag such as zh-CN.");
        if (ui.LicenseMode is not ("firstInstall" or "always")) d.Error("FS1311", "ui.licenseMode", "Use firstInstall or always.");
        if (!string.IsNullOrEmpty(ui.License))
        {
            var lic = Resolve(projectDirectory, ui.License);
            if (!File.Exists(lic)) d.Error("FS1305", "ui.license", $"License file not found: {lic}");
            else if (new FileInfo(lic).Length > 1024 * 1024) d.Error("FS1306", "ui.license", "License file is larger than 1 MB.");
        }
        foreach (var lang in ui.Strings.Keys)
        {
            // Any language works: built-in ones get overrides, others bring their own strings.
            if (!Localization.IsLanguageTag(lang)) d.Warning("FS1307", $"ui.strings.{lang}", $"Strings for '{lang}' are ignored: '{lang}' is not a language tag such as en-US, zh-CN or fr-FR.");
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
            if (!Localization.IsLanguageTag(lang)) d.Warning("FS1309", $"ui.licenses.{lang}", $"'{lang}' is not a language tag; this license is ignored.");
            if (!File.Exists(Resolve(projectDirectory, file))) d.Error("FS1310", $"ui.licenses.{lang}", $"License file not found: {Resolve(projectDirectory, file)}");
            else if (new FileInfo(Resolve(projectDirectory, file)).Length > 1024 * 1024) d.Error("FS1306", $"ui.licenses.{lang}", "License file is larger than 1 MB.");
        }
        ExtensionValidator.Validate(p, d);
        Localization.Load(p, projectDirectory, d);  // reports problems in the localization folder

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
        ValidateUpdate(p, d);
        return d;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex GitHubRepositoryRegex();

    private static void ValidateUpdate(SetupProject p, DiagnosticBag d)
    {
        var u = p.Update;
        bool github = !string.IsNullOrWhiteSpace(u.GitHub), feed = !string.IsNullOrWhiteSpace(u.Feed);
        if (!github && !feed)
        {
            if (u.Asset.Length > 0 || u.Checksums.Length > 0 || u.Signer.Length > 0 || u.Prerelease)
                d.Warning("FS1709", "update", "Update settings have no effect until update.github or update.feed is set.");
            return;
        }
        if (github && feed) d.Error("FS1701", "update", "Choose GitHub releases (update.github) or a feed (update.feed), not both.");
        if (github && (!GitHubRepositoryRegex().IsMatch(u.GitHub) || u.GitHub.EndsWith(".git", StringComparison.OrdinalIgnoreCase)))
            d.Error("FS1702", "update.github", $"'{u.GitHub}' is not a GitHub repository; write owner/name, e.g. contoso/notes.");
        if (feed && !(Uri.TryCreate(u.Feed, UriKind.Absolute, out var feedUri) && feedUri.Scheme == Uri.UriSchemeHttps))
            d.Error("FS1703", "update.feed", "The update feed must be an https URL.");
        if (github)
        {
            var asset = UpdateAssetTemplate(p);
            var sample = asset.Replace("{Version}", "1.0.0", StringComparison.Ordinal);
            if (!PathRules.IsValidComponent(sample, out _) || sample.Contains('{') || sample.Contains('}') || !sample.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                d.Error("FS1704", "update.asset", $"'{asset}' is not a file name ending in .exe ({{Version}} is the only placeholder).");
            else if (!asset.Contains("{Version}", StringComparison.Ordinal) && IsValidVersion(p.App.Version) && asset.Contains(p.App.Version, StringComparison.Ordinal))
                d.Warning("FS1705", string.IsNullOrEmpty(u.Asset) ? "output.fileName" : "update.asset",
                          $"The Setup.exe name '{asset}' contains this version ({p.App.Version}) literally, so the next release is looked for under the same name. " +
                          "Write {Version} in update.asset (or in output.fileName) instead.");
            if (u.Checksums.Length > 0 && !PathRules.IsValidComponent(u.Checksums, out _))
                d.Error("FS1706", "update.checksums", "update.checksums must be the file name of a release asset, e.g. SHA256SUMS.txt.");
        }
        else if (u.Asset.Length > 0 || u.Checksums.Length > 0 || u.Prerelease)
        {
            d.Warning("FS1707", "update", "update.asset, update.checksums and update.prerelease are only used with update.github; the feed names the file and its SHA-256.");
        }
        if (u.Signer.Length > 0 && (u.Signer.Trim().Length != u.Signer.Length || u.Signer.Length > 256))
            d.Error("FS1708", "update.signer", "update.signer must be the certificate's subject name without surrounding spaces (max 256 characters).");
        else if (u.Signer.Length > 0 && !p.Signing.Enabled)
            d.Warning("FS1710", "update.signer", "update.signer is set but this project is not signed: updates without that signature will be refused.");
    }

    /// <summary>
    /// The name of Setup.exe in a GitHub release as the installer looks for it: update.asset, else output.fileName, with
    /// every placeholder but {Version} replaced (the installer puts in the version of the release).
    /// </summary>
    public static string UpdateAssetTemplate(SetupProject p)
    {
        const string marker = "\u0001";
        var template = string.IsNullOrWhiteSpace(p.Update.Asset) ? p.Output.FileName : p.Update.Asset;
        return ExpandFileName(template.Replace("{Version}", marker, StringComparison.Ordinal), p).Replace(marker, "{Version}", StringComparison.Ordinal);
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
