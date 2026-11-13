using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Updates;

namespace FluentSetup.Builder.Core.Winget;

/// <summary>What the winget manifests need that a Setup.exe does not carry ("fluentsetup winget" options).</summary>
public sealed class WingetOptions
{
    /// <summary>https URL users download this Setup.exe from (InstallerUrl). Required unless <see cref="GitHubRepository"/> is set.</summary>
    public string? InstallerUrl { get; set; }
    /// <summary>"owner/repo": InstallerUrl is the release asset https://github.com/owner/repo/releases/download/v&lt;version&gt;/&lt;file&gt;.</summary>
    public string? GitHubRepository { get; set; }
    /// <summary>PackageIdentifier ("Publisher.App"); default: app.id when it has that form, else publisher + name.</summary>
    public string? Identifier { get; set; }
    /// <summary>License name, e.g. "MIT"; default: recognized from the package's license text, else "Proprietary".</summary>
    public string? License { get; set; }
    public string? LicenseUrl { get; set; }
    /// <summary>Default: the first sentence of app.description, else app.name.</summary>
    public string? ShortDescription { get; set; }
    public string? PublisherUrl { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? ReleaseNotesUrl { get; set; }
    /// <summary>PackageLocale of the default locale manifest.</summary>
    public string Locale { get; set; } = "en-US";
}

/// <summary>A generated multi-file manifest: file name → YAML, plus what was guessed (for the user to check).</summary>
public sealed class WingetManifestSet
{
    public string Identifier { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>Folder relative to the winget-pkgs root, with '/': manifests/c/Contoso/MyApp/1.0.0.</summary>
    public string RelativeFolder { get; init; } = "";
    public string InstallerUrl { get; init; } = "";
    public string InstallerSha256 { get; init; } = "";
    public string Architecture { get; init; } = "";
    /// <summary>One entry per installer: "user", "machine".</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string License { get; init; } = "";
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Writes the files into &lt;root&gt;/&lt;RelativeFolder&gt; (UTF-8, LF) and returns their full paths.</summary>
    public IReadOnlyList<string> Write(string root)
    {
        var folder = Path.Combine(Path.GetFullPath(root), RelativeFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (var (name, text) in Files)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            written.Add(path);
        }
        return written;
    }
}

public sealed class WingetManifestException(string message) : Exception(message);

/// <summary>
/// winget manifests (version, defaultLocale, installer) for a built Setup.exe, in the layout of the winget-pkgs
/// repository. Everything that the package header knows (id, name, publisher, version, description, website,
/// architecture, scope, minimum Windows build) is taken from there; see docs/winget.md.
/// </summary>
public static partial class WingetManifests
{
    /// <summary>The manifest schema written (the newest one winget-pkgs accepts).</summary>
    public const string ManifestVersion = "1.28.0";

    /// <summary>Setup.exe exit codes (docs/diagnostics.md) that winget can explain to the user.</summary>
    public static readonly IReadOnlyList<(int Code, string Response)> ExpectedReturnCodes =
    [
        (2, "cancelledByUser"),
        (3, "invalidParameter"),
        (5, "systemNotSupported"),
        (7, "diskFull"),
        (8, "fileInUse"),
        (10, "downgrade"),
        (16, "installInProgress"),
        (17, "missingDependency"),
        (3010, "rebootRequiredToFinish"),   // ERROR_SUCCESS_REBOOT_REQUIRED: installed, files are replaced at the next restart
    ];

    [GeneratedRegex(@"^[^\.\s\\/:\*\?""<>\|\x01-\x1f]{1,32}(\.[^\.\s\\/:\*\?""<>\|\x01-\x1f]{1,32}){1,7}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[^\\/:\*\?""<>\|\x01-\x1f]+$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^([a-zA-Z]{2,3}|[iI]-[a-zA-Z]+|[xX]-[a-zA-Z]{1,8})(-[a-zA-Z]{1,8})*$")]
    private static partial Regex LocalePattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex GitHubRepositoryPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^.+?[.!?。！？](?=\s|$)")]
    private static partial Regex Sentence();

    public static WingetManifestSet Create(string setupPath, WingetOptions options)
    {
        setupPath = Path.GetFullPath(setupPath);
        if (!File.Exists(setupPath)) throw new WingetManifestException($"File not found: {setupPath}");
        PackageInfo info;
        try
        {
            info = PackageInspector.Inspect(setupPath, verifyStreams: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or JsonException)
        {
            throw new WingetManifestException($"{Path.GetFileName(setupPath)} is not a FluentSetup package: {ex.Message}");
        }
        using var header = info.Header;
        if (info.Problems.Count > 0 || header is null)
            throw new WingetManifestException($"{Path.GetFileName(setupPath)} is not a valid FluentSetup package: {string.Join("; ", info.Problems)}");
        var root = header.RootElement;
        if (Str(root, "kind") != "setup") throw new WingetManifestException($"{Path.GetFileName(setupPath)} is an uninstaller, not a Setup.exe.");
        var app = root.GetProperty("app");
        var install = root.GetProperty("install");
        var notes = new List<string>();

        string name = Str(app, "name"), publisher = Str(app, "publisher"), version = Str(app, "version"), appId = Str(app, "id");
        string description = Str(app, "description").Trim(), website = Str(app, "website").Trim();
        string arch = Str(app, "architecture");
        int minBuild = app.TryGetProperty("minWindowsBuild", out var mb) && mb.ValueKind == JsonValueKind.Number ? mb.GetInt32() : 0;
        string scope = Str(install, "scope");

        if (publisher.Trim().Length < 2) throw new WingetManifestException("winget needs a publisher name: set app.publisher in the project and build again.");
        if (name.Trim().Length < 2) throw new WingetManifestException("winget needs a package name of at least 2 characters (app.name).");
        if (!VersionPattern().IsMatch(version) || version.Length > 128) throw new WingetManifestException($"The version '{version}' cannot be a winget PackageVersion.");
        if (arch is not ("x64" or "arm64")) throw new WingetManifestException($"Unknown architecture '{arch}' in the package.");
        if (scope is not ("user" or "machine" or "userOrMachine")) throw new WingetManifestException($"Unknown install scope '{scope}' in the package.");
        if (!LocalePattern().IsMatch(options.Locale)) throw new WingetManifestException($"'{options.Locale}' is not a locale such as en-US.");

        // ---- identifier ------------------------------------------------------------------------------------------
        string identifier;
        if (!string.IsNullOrWhiteSpace(options.Identifier))
        {
            identifier = options.Identifier.Trim();
            if (!IsValidIdentifier(identifier))
                throw new WingetManifestException($"'{identifier}' is not a winget package identifier: 2 to 8 parts separated by dots (Publisher.App), each up to 32 characters without spaces or \\/:*?\"<>|.");
        }
        else if (IsValidIdentifier(appId))
        {
            identifier = appId;
        }
        else
        {
            identifier = IdentifierPart(publisher) + "." + IdentifierPart(name);
            if (!IsValidIdentifier(identifier))
                throw new WingetManifestException($"Cannot make a package identifier from '{publisher}' and '{name}'; give one with --identifier Publisher.App.");
            notes.Add($"PackageIdentifier {identifier} was made from the publisher and app name (app.id '{appId}' has no Publisher.App form); --identifier sets another.");
        }

        // ---- installer URL ---------------------------------------------------------------------------------------
        var fileName = Path.GetFileName(setupPath);
        string url;
        string? repo = null;
        if (!string.IsNullOrWhiteSpace(options.GitHubRepository))
        {
            repo = options.GitHubRepository.Trim().Trim('/');
            if (repo.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) repo = repo["https://github.com/".Length..];
            if (!GitHubRepositoryPattern().IsMatch(repo)) throw new WingetManifestException($"'{options.GitHubRepository}' is not a GitHub repository (owner/repo).");
            // GitHub renames uploaded assets: spaces become dots ("My App-1.0-Setup.exe" -> "My.App-1.0-Setup.exe").
            var asset = fileName.Replace(' ', '.');
            if (asset.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
                notes.Add($"The file name '{fileName}' has characters GitHub may change in release asset names; check that {asset} is the asset's name.");
            url = $"https://github.com/{repo}/releases/download/v{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(asset)}";
        }
        else
        {
            url = options.InstallerUrl?.Trim() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || url.Length > 2048)
                throw new WingetManifestException($"The installer URL must be an https:// address, got '{url}'.");
        }

        string? Link(string? value, string what)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) || value.Length > 2048)
                throw new WingetManifestException($"{what} must be an http(s) address, got '{value}'.");
            return value;
        }
        var packageUrl = website.Length > 0 && Uri.TryCreate(website, UriKind.Absolute, out var w) && (w.Scheme == Uri.UriSchemeHttps || w.Scheme == Uri.UriSchemeHttp)
            ? website : repo is not null ? $"https://github.com/{repo}" : null;
        var publisherUrl = Link(options.PublisherUrl, "--publisher-url") ?? (repo is not null ? $"https://github.com/{repo.Split('/')[0]}" : null);
        var licenseUrl = Link(options.LicenseUrl, "--license-url");
        var releaseNotesUrl = Link(options.ReleaseNotesUrl, "--release-notes-url") ?? (repo is not null ? $"https://github.com/{repo}/releases/tag/v{Uri.EscapeDataString(version)}" : null);

        // ---- license, descriptions, tags -------------------------------------------------------------------------
        string license;
        if (!string.IsNullOrWhiteSpace(options.License))
        {
            license = options.License.Trim();
        }
        else
        {
            var text = LicenseText(root);
            var guessed = text.Length > 0 ? GuessLicense(text) : null;
            license = guessed ?? "Proprietary";
            notes.Add(guessed is not null
                ? $"License: {license} (recognized from the package's license text; --license sets it)."
                : $"License: Proprietary ({(text.Length > 0 ? "the package's license text is not a well-known license" : "the package has no license text")}; --license sets it, e.g. --license MIT).");
        }
        if (license.Length is < 3 or > 512) throw new WingetManifestException("The license must be 3 to 512 characters long (e.g. MIT, Apache-2.0, Proprietary).");

        var shortDescription = !string.IsNullOrWhiteSpace(options.ShortDescription) ? options.ShortDescription.Trim() : ShortText(description, 256) ?? name.Trim();
        if (shortDescription.Length is < 3 or > 256) throw new WingetManifestException("The short description must be 3 to 256 characters long; give one with --short-description.");
        string? longDescription = description.Length >= 3 && description != shortDescription ? Truncate(description, 10000) : null;

        var tags = new List<string>();
        foreach (var raw in options.Tags)
        {
            var tag = Whitespace().Replace(raw.Trim().ToLowerInvariant(), "-");
            if (tag.Length == 0) continue;
            if (tag.Length > 40) throw new WingetManifestException($"The tag '{tag}' is longer than 40 characters.");
            if (!tags.Contains(tag)) tags.Add(tag);
        }
        if (tags.Count > 16) throw new WingetManifestException("winget allows at most 16 tags.");

        string sha256;
        using (var stream = File.OpenRead(setupPath)) sha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!info.Signed) notes.Add("Setup.exe is not signed. winget accepts it, but SmartScreen warns users and the Microsoft Store requires a signed installer (signing.enabled).");

        // ---- installers ------------------------------------------------------------------------------------------
        bool allowCustomDir = !install.TryGetProperty("allowCustomDir", out var acd) || acd.ValueKind != JsonValueKind.False;
        var scopes = scope == "userOrMachine" ? new[] { "user", "machine" } : [scope];
        var installers = new List<InstallerEntry>();
        foreach (var s in scopes)
        {
            var template = Str(install, s == "user" ? "userDir" : "machineDir");
            installers.Add(new InstallerEntry(s, scope == "userOrMachine", ExpandInstallDir(template, name, publisher, appId)));
        }

        var minOs = minBuild is > 0 and <= 65535 ? $"10.0.{minBuild}.0" : null;
        var y = new YamlWriter();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        // Version manifest.
        y.Header("version");
        y.Value("PackageIdentifier", identifier);
        y.Value("PackageVersion", version);
        y.Value("DefaultLocale", options.Locale);
        y.Value("ManifestType", "version");
        y.Value("ManifestVersion", ManifestVersion);
        files[identifier + ".yaml"] = y.ToStringAndReset();

        // Default locale manifest.
        y.Header("defaultLocale");
        y.Value("PackageIdentifier", identifier);
        y.Value("PackageVersion", version);
        y.Value("PackageLocale", options.Locale);
        y.Value("Publisher", publisher.Trim());
        y.Value("PublisherUrl", publisherUrl);
        y.Value("PackageName", name.Trim());
        y.Value("PackageUrl", packageUrl);
        y.Value("License", license);
        y.Value("LicenseUrl", licenseUrl);
        y.Value("ShortDescription", shortDescription);
        y.Value("Description", longDescription);
        y.List("Tags", tags);
        y.Value("ReleaseNotesUrl", releaseNotesUrl);
        y.Value("ManifestType", "defaultLocale");
        y.Value("ManifestVersion", ManifestVersion);
        files[identifier + ".locale." + options.Locale + ".yaml"] = y.ToStringAndReset();

        // Installer manifest.
        y.Header("installer");
        y.Value("PackageIdentifier", identifier);
        y.Value("PackageVersion", version);
        y.List("Platform", ["Windows.Desktop"]);
        y.Value("MinimumOSVersion", minOs);
        y.Value("InstallerType", "exe");
        y.List("InstallModes", ["interactive", "silent", "silentWithProgress"]);
        y.Value("UpgradeBehavior", "install");
        y.Value("ProductCode", appId);
        y.Line("AppsAndFeaturesEntries:");
        y.Line("- DisplayName: " + YamlWriter.Scalar(name));
        y.Line("  Publisher: " + YamlWriter.Scalar(publisher));
        y.Line("  DisplayVersion: " + YamlWriter.Scalar(version));
        y.Line("  ProductCode: " + YamlWriter.Scalar(appId));
        y.Line("ExpectedReturnCodes:");
        foreach (var (code, response) in ExpectedReturnCodes)
        {
            y.Line("- InstallerReturnCode: " + code.ToString(CultureInfo.InvariantCulture));
            y.Line("  ReturnResponse: " + response);
        }
        y.Line("Installers:");
        foreach (var i in installers)
        {
            y.Line("- Architecture: " + arch);
            y.Line("  Scope: " + i.Scope);
            y.Line("  InstallerUrl: " + YamlWriter.Scalar(url));
            y.Line("  InstallerSha256: " + sha256);
            y.Line("  InstallerSwitches:");
            y.Line("    Silent: /quiet");
            y.Line("    SilentWithProgress: /passive");
            if (allowCustomDir) y.Line("    InstallLocation: " + YamlWriter.Scalar("/dir \"<INSTALLPATH>\""));
            y.Line("    Log: " + YamlWriter.Scalar("/log \"<LOGPATH>\""));
            if (i.PassScope) y.Line("    Custom: /scope " + i.Scope);
            // Setup.exe asks for administrator rights itself (UAC), also in quiet mode.
            if (i.Scope == "machine") y.Line("  ElevationRequirement: elevatesSelf");
            if (i.DefaultInstallLocation is { } dir)
            {
                y.Line("  InstallationMetadata:");
                y.Line("    DefaultInstallLocation: " + YamlWriter.Scalar(dir));
            }
        }
        y.Value("ManifestType", "installer");
        y.Value("ManifestVersion", ManifestVersion);
        files[identifier + ".installer.yaml"] = y.ToStringAndReset();

        return new WingetManifestSet
        {
            Identifier = identifier,
            Version = version,
            RelativeFolder = $"manifests/{char.ToLowerInvariant(identifier[0])}/{identifier.Replace('.', '/')}/{version}",
            InstallerUrl = url,
            InstallerSha256 = sha256,
            Architecture = arch,
            Scopes = scopes,
            License = license,
            Files = files,
            Notes = notes,
        };
    }

    public static bool IsValidIdentifier(string id) => id.Length <= 128 && IdentifierPattern().IsMatch(id);

    /// <summary>A YAML scalar as written into the manifests (for tests).</summary>
    internal static string YamlScalar(string s) => YamlWriter.Scalar(s);

    private sealed record InstallerEntry(string Scope, bool PassScope, string? DefaultInstallLocation);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>One identifier part: no dots, spaces or characters winget forbids, at most 32 characters.</summary>
    private static string IdentifierPart(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c < 0x20 || "./\\:*?\"<>|".Contains(c)) continue;
            sb.Append(c);
        }
        return sb.Length > 32 ? sb.ToString(0, 32) : sb.ToString();
    }

    /// <summary>
    /// install.userDir / machineDir as winget writes install locations (%LocalAppData%\Programs\App), the way the
    /// engine expands them (src/Install.Core/package.cpp ExpandDirTemplate, path::SanitizeName).
    /// </summary>
    internal static string? ExpandInstallDir(string template, string name, string publisher, string appId)
    {
        if (template.Length == 0) return null;
        var result = template
            .Replace("{UserProgramFiles}", @"%LocalAppData%\Programs", StringComparison.Ordinal)
            .Replace("{LocalAppData}", "%LocalAppData%", StringComparison.Ordinal)
            .Replace("{AppData}", "%AppData%", StringComparison.Ordinal)
            .Replace("{ProgramFiles}", "%ProgramFiles%", StringComparison.Ordinal)
            .Replace("{ProgramData}", "%ProgramData%", StringComparison.Ordinal)
            .Replace("{AppName}", SanitizeName(name), StringComparison.Ordinal)
            .Replace("{Publisher}", SanitizeName(publisher.Length == 0 ? name : publisher), StringComparison.Ordinal)
            .Replace("{AppId}", appId, StringComparison.Ordinal);
        return result.Contains('{') || result.Length > 2048 ? null : result;
    }

    /// <summary>Mirror of fsetup::path::SanitizeName.</summary>
    internal static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(c < 0x20 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || PathRules.IsReservedDeviceName(result)) result = "App" + result;
        return result.Length > 64 ? result[..64] : result;
    }

    /// <summary>The license text shown by setup: ui.license, else the English or first per-language text.</summary>
    private static string LicenseText(JsonElement root)
    {
        if (!root.TryGetProperty("ui", out var ui)) return "";
        var text = Str(ui, "license");
        if (text.Length == 0 && ui.TryGetProperty("licenses", out var all) && all.ValueKind == JsonValueKind.Object)
        {
            text = Str(all, "en-US");
            if (text.Length == 0) text = all.EnumerateObject().Select(p => p.Value.GetString() ?? "").FirstOrDefault(t => t.Length > 0) ?? "";
        }
        return text;
    }

    /// <summary>SPDX identifier of a well-known license text, or null.</summary>
    internal static string? GuessLicense(string text)
    {
        var t = Whitespace().Replace(text.Length > 4000 ? text[..4000] : text, " ");
        bool Has(string s) => t.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("GNU AFFERO GENERAL PUBLIC LICENSE")) return "AGPL-3.0";
        if (Has("GNU LESSER GENERAL PUBLIC LICENSE")) return Has("Version 3") ? "LGPL-3.0" : Has("Version 2.1") ? "LGPL-2.1" : null;
        if (Has("GNU GENERAL PUBLIC LICENSE")) return Has("Version 3") ? "GPL-3.0" : Has("Version 2") ? "GPL-2.0" : null;
        if (Has("Apache License") && Has("Version 2.0")) return "Apache-2.0";
        if (Has("Mozilla Public License") && Has("2.0")) return "MPL-2.0";
        if (Has("Boost Software License")) return "BSL-1.0";
        if (Has("Permission is hereby granted, free of charge")) return "MIT";
        if (Has("This is free and unencumbered software released into the public domain")) return "Unlicense";
        if (Has("Redistribution and use in source and binary forms")) return Has("Neither the name") ? "BSD-3-Clause" : "BSD-2-Clause";
        return null;
    }

    /// <summary>The description if it is one line that fits, else its first sentence (shortened if needed); null if none.</summary>
    private static string? ShortText(string text, int max)
    {
        if (text.Length < 3) return null;
        var line = text.Split('\n')[0].Trim();
        if (line.Length == text.Length && line.Length <= max) return line;
        var m = Sentence().Match(line);
        var sentence = m.Success ? m.Value : line;
        if (sentence.Length > max) sentence = Truncate(sentence, max - 1).TrimEnd() + "…";
        return sentence.Length >= 3 ? sentence : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Just enough YAML for winget manifests: block mappings and sequences of scalars.</summary>
    private sealed partial class YamlWriter
    {
        private readonly StringBuilder _sb = new();

        public void Header(string manifestType)
        {
            _sb.Append("# Created with FluentSetup ").Append(FluentSetupUpdates.CurrentVersion).Append(" (fluentsetup winget)\n");
            _sb.Append("# yaml-language-server: $schema=https://aka.ms/winget-manifest.").Append(manifestType).Append('.').Append(ManifestVersion).Append(".schema.json\n\n");
        }

        public void Line(string text) => _sb.Append(text).Append('\n');

        public void Value(string key, string? value)
        {
            if (value is not null) Line(key + ": " + Scalar(value));
        }

        public void List(string key, IReadOnlyCollection<string> items)
        {
            if (items.Count == 0) return;
            Line(key + ":");
            foreach (var i in items) Line("- " + Scalar(i));
        }

        public string ToStringAndReset()
        {
            var s = _sb.ToString();
            _sb.Clear();
            return s;
        }

        [GeneratedRegex(@"^[\p{L}\p{N}/<(][\p{L}\p{N} ._\-/\\+()<>=~,;:'""&@!?*%#|\[\]{}$^`]*$")]
        private static partial Regex PlainCandidate();

        [GeneratedRegex(@"^(?:[-+]?(?:\.?[0-9][0-9_]*(?:\.[0-9_]*)?(?:[eE][-+]?[0-9]+)?|0x[0-9a-fA-F_]+|0o?[0-7_]+|\.inf|\.nan)|[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}.*|true|false|yes|no|y|n|on|off|null|~)$", RegexOptions.IgnoreCase)]
        private static partial Regex Ambiguous();

        /// <summary>A scalar as plain text when that reads back as the same string, else quoted.</summary>
        public static string Scalar(string s)
        {
            bool plain = s.Length > 0 && s == s.Trim() && PlainCandidate().IsMatch(s) && !Ambiguous().IsMatch(s) &&
                         !s.Contains(": ", StringComparison.Ordinal) && !s.Contains(" #", StringComparison.Ordinal) && !s.EndsWith(':') &&
                         s.IndexOfAny(['[', ']', '{', '}', ',']) < 0;
            if (plain) return s;
            if (!s.Any(Unsafe)) return "'" + s.Replace("'", "''") + "'";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (Unsafe(c)) sb.Append(@"\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Characters that only a double-quoted scalar can carry (escaped): controls, line/paragraph separators, BOM.</summary>
        private static bool Unsafe(char c) => c < 0x20 || c == 0x7F || c == 0x2028 || c == 0x2029 || c == 0xFEFF;
    }
}
