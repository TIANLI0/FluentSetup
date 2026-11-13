using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Validation;

/// <summary>
/// Validates requirements, options, actions, process closing and uninstall cleanup. Mirrors the native checks in
/// src/Install.Core/extensions.cpp (which re-validates everything at install time).
/// </summary>
public static partial class ExtensionValidator
{
    private static readonly Dictionary<string, int> NetFrameworkReleases = new(StringComparer.Ordinal)
    {
        ["4.5"] = 378389, ["4.5.1"] = 378675, ["4.5.2"] = 379893, ["4.6"] = 393295, ["4.6.1"] = 394254, ["4.6.2"] = 394802,
        ["4.7"] = 460798, ["4.7.1"] = 461308, ["4.7.2"] = 461808, ["4.8"] = 528040, ["4.8.1"] = 533320,
    };
    private static readonly string[] DataRoots = [@"{CommonAppData}\", @"{LocalAppData}\", @"{AppData}\"];

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex OptionIdRegex();

    public static int? NetFrameworkRelease(string version) => NetFrameworkReleases.TryGetValue(version, out var r) ? r : null;
    public static IReadOnlyCollection<string> KnownNetFrameworkVersions => NetFrameworkReleases.Keys;

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    /// <summary>Full shared-framework name and display name for a project's runtime value.</summary>
    public static (string Runtime, string Label) DotNetRuntime(string value) => value switch
    {
        "desktop" or "Microsoft.WindowsDesktop.App" => ("Microsoft.WindowsDesktop.App", ".NET Desktop Runtime"),
        "core" or "Microsoft.NETCore.App" => ("Microsoft.NETCore.App", ".NET Runtime"),
        "aspnetcore" or "Microsoft.AspNetCore.App" => ("Microsoft.AspNetCore.App", "ASP.NET Core Runtime"),
        _ => ("", ""),
    };

    /// <summary>All prerequisites that can carry an installer or download, with their field names.</summary>
    private static IEnumerable<(PrerequisiteSource Source, string Field)> Sources(SetupProject p)
    {
        if (p.Requirements.WindowsAppRuntime is { Required: true } war) yield return (war, "requirements.windowsAppRuntime");
        if (p.Requirements.NetFrameworkSetup is { } nf) yield return (nf, "requirements.netFrameworkSetup");
        if (p.Requirements.WebView2.Required) yield return (p.Requirements.WebView2, "requirements.webView2");
        foreach (var (r, i) in p.Requirements.DotNet.Select((r, i) => (r, i))) yield return (r, $"requirements.dotNet[{i}]");
        if (p.Requirements.VcRedist.Required) yield return (p.Requirements.VcRedist, "requirements.vcRedist");
        foreach (var (c, i) in p.Requirements.Custom.Select((c, i) => (c, i))) yield return (c, $"requirements.custom[{i}]");
    }

    private static void ValidateSource(PrerequisiteSource s, string field, DiagnosticBag d)
    {
        if (!string.IsNullOrEmpty(s.Installer) && !PathRules.TryNormalizeRelative(s.Installer, out _, out var why))
            d.Error("FS1604", field + ".installer", $"Not a valid relative path: {why}.");
        if (s.Arguments.Count > 32 || s.Arguments.Any(x => x.Length > 1024 || x.Contains('\0'))) d.Error("FS1605", field + ".arguments", "Too many or too long arguments.");
        if (s.Download is { } dl)
        {
            if (!Uri.TryCreate(dl.Url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || dl.Url.Length > 2048)
                d.Error("FS1606", field + ".download.url", "Downloads must use an https URL.");
            if (dl.Signer.Length == 0 ? !Sha256Regex().IsMatch(dl.Sha256) : dl.Sha256.Length > 0 && !Sha256Regex().IsMatch(dl.Sha256))
                d.Error("FS1607", field + ".download.sha256", "Give the SHA-256 of the file (64 hex characters) or a signer; setup refuses to run anything that does not match.");
            if (dl.Signer.Length > 256 || dl.Signer.Trim().Length != dl.Signer.Length)
                d.Error("FS1683", field + ".download.signer", "The certificate's subject name, e.g. Microsoft Corporation (max 256 characters, no surrounding spaces).");
            if (dl.Size < 0) d.Error("FS1608", field + ".download.size", "Size must be the file size in bytes, or 0 if unknown.");
            if (!string.IsNullOrEmpty(s.Installer)) d.Info("FS1609", field, "Both a bundled installer and a download are set; the bundled installer is used.");
        }
        if (s.OnFailure is not ("warn" or "fail")) d.Error("FS1660", field + ".onFailure", "Use warn or fail.");
        if (!string.IsNullOrEmpty(s.HelpUrl) && (!Uri.TryCreate(s.HelpUrl, UriKind.Absolute, out var h) || h.Scheme != Uri.UriSchemeHttps))
            d.Error("FS1615", field + ".helpUrl", "The help link must be an https URL.");
    }

    private static bool IsExeName(string n) => PathRules.IsValidComponent(n, out _) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    private static bool IsTaskPath(string n) => n.Length is > 0 and <= 255 && !n.StartsWith('\\') && !n.EndsWith('\\') && n.Split('\\').All(IsTaskOrValueName);
    private static bool IsDataTemplate(string t)
    {
        var root = DataRoots.FirstOrDefault(r => t.StartsWith(r, StringComparison.Ordinal));
        return root is not null && PathRules.TryNormalizeRelative(t[root.Length..], out _, out _);
    }
    /// <summary>A data template, or {InstallDir}\X for something the app writes into its own folder (not the installer's .fsetup).</summary>
    private static bool IsDataOrInstallDirTemplate(string t)
    {
        const string installDir = @"{InstallDir}\";
        if (!t.StartsWith(installDir, StringComparison.Ordinal)) return IsDataTemplate(t);
        return PathRules.TryNormalizeRelative(t[installDir.Length..], out var rel, out _) &&
               !rel.Split('\\')[0].Equals(".fsetup", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsLnkName(string n) => PathRules.IsValidComponent(n, out _) && n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
    /// <summary>"App.lnk", or "Folder\App.lnk" for a Start menu folder.</summary>
    private static bool IsShortcutName(string n)
    {
        var parts = n.Split('\\');
        return parts.Length switch { 1 => IsLnkName(n), 2 => PathRules.IsValidComponent(parts[0], out _) && IsLnkName(parts[1]), _ => false };
    }
    private static readonly string[] SharedRegistryTrees = ["Microsoft", "Classes", "Policies", "Wow6432Node", "RegisteredApplications", "Clients"];
    /// <summary>HKCU\Software\Vendor[\...] or HKLM\Software\Vendor[\...], never a tree other software shares (same rule as the installer).</summary>
    public static bool IsRemovableRegistryKey(string key, string? hive = null)
    {
        if (key.Length > 255 || !(key.StartsWith(@"HKCU\", StringComparison.Ordinal) || key.StartsWith(@"HKLM\", StringComparison.Ordinal))) return false;
        if (hive is not null && !key.StartsWith(hive + @"\", StringComparison.Ordinal)) return false;
        var parts = key[5..].Split('\\');
        return parts.Length >= 2 && parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase) &&
               parts.All(x => x.Length is > 0 and <= 128 && x.All(c => c >= 0x20)) &&
               !SharedRegistryTrees.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>A relative path whose file name may use * and ? (e.g. "unins*.exe"), with at least one literal character.</summary>
    public static bool IsFilePattern(string pattern)
    {
        var norm = pattern.Replace('/', '\\');
        if (norm.Length is 0 or > 260) return false;
        var slash = norm.LastIndexOf('\\');
        var folder = slash < 0 ? "" : norm[..slash];
        var name = slash < 0 ? norm : norm[(slash + 1)..];
        if (folder.Length > 0 && (folder.IndexOfAny(['*', '?']) >= 0 || !PathRules.TryNormalizeRelative(folder, out var rel, out _) ||
                                  rel.Split('\\')[0].Equals(".fsetup", StringComparison.OrdinalIgnoreCase))) return false;
        return PathRules.IsValidComponent(name.Replace('*', 'x').Replace('?', 'x'), out _) && name.Trim('*', '?', '.').Length > 0;
    }
    private static bool IsTaskOrValueName(string n) => n.Length is > 0 and <= 128 && n.All(c => c >= 0x20 && !"\\/:*?\"<>|".Contains(c));

    public static void Validate(SetupProject p, DiagnosticBag d)
    {
        foreach (var (name, i) in p.Install.CloseProcesses.Select((n, i) => (n, i)))
        {
            if (!IsExeName(name)) d.Error("FS1601", $"install.closeProcesses[{i}]", $"'{name}' must be a file name ending in .exe (no folder).");
        }
        if (p.Install.CloseProcesses.Count > 32) d.Error("FS1602", "install.closeProcesses", "At most 32 process names.");

        if (!string.IsNullOrEmpty(p.Requirements.NetFramework) && NetFrameworkRelease(p.Requirements.NetFramework) is null)
        {
            d.Error("FS1603", "requirements.netFramework", $"Unknown .NET Framework version. Use one of: {string.Join(", ", NetFrameworkReleases.Keys)}.");
        }
        if (!string.IsNullOrEmpty(p.Requirements.WebView2.MinVersion) && !ProjectValidator.IsValidVersion(p.Requirements.WebView2.MinVersion))
            d.Error("FS1614", "requirements.webView2.minVersion", "Not a valid version.");
        if (p.Requirements.NetFrameworkSetup is not null && string.IsNullOrEmpty(p.Requirements.NetFramework))
            d.Warning("FS1616", "requirements.netFrameworkSetup", "Ignored: requirements.netFramework is not set.");
        foreach (var (r, i) in p.Requirements.DotNet.Select((r, i) => (r, i)))
        {
            var field = $"requirements.dotNet[{i}]";
            if (DotNetRuntime(r.Runtime).Runtime.Length == 0) d.Error("FS1617", field + ".runtime", "Use desktop, core or aspnetcore.");
            if (!ProjectValidator.IsValidVersion(r.Version)) d.Error("FS1618", field + ".version", "Give the minimum runtime version, e.g. 8.0.0.");
            if (r.RollForward is not ("patch" or "minor" or "major")) d.Error("FS1619", field + ".rollForward", "Use patch, minor or major.");
        }
        foreach (var (s, field) in Sources(p))
        {
            ValidateSource(s, field, d);
            foreach (var id in s.Variants.Where(id => p.Files.Variants.All(v => v.Id != id)))
                d.Error("FS1661", field + ".variants", $"No variant '{id}' in files.variants.");
        }
        if (p.Requirements.WindowsAppRuntime is { Required: true } w2 &&
            !System.Text.RegularExpressions.Regex.IsMatch(w2.Version, @"^(1\.\d+|([2-9]|[1-9]\d+)\.\d+(\.\d+){0,2})$"))
        {
            d.Error("FS1663", "requirements.windowsAppRuntime.version", "Use the Windows App SDK version, e.g. 2.5.1 (or 1.6 for a 1.x runtime).");
        }
        if (p.Install.CloseCommand is { } close &&
            (!PathRules.TryNormalizeRelative(close.Run, out var closeRun, out _) || !closeRun.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || close.Arguments.Count > 32))
        {
            d.Error("FS1666", "install.closeCommand", "Needs run (relative path of an .exe of the app) and at most 32 arguments.");
        }
        foreach (var (n, i) in p.Install.LegacyUninstallKeys.Select((n, i) => (n, i)))
        {
            if (!IsTaskOrValueName(n) || n.Equals(p.App.Id, StringComparison.OrdinalIgnoreCase))
                d.Error("FS1650", $"install.legacyUninstallKeys[{i}]", "An uninstall key name of an older product (not this app's id).");
        }
        foreach (var (n, i) in p.Install.Ngen.Select((n, i) => (n, i)))
        {
            if (!PathRules.TryNormalizeRelative(n, out var rel, out _) || !(rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                d.Error("FS1651", $"install.ngen[{i}]", "A relative path of an .exe or .dll among the files to install.");
        }
        var c0 = p.Install.Cleanup;
        foreach (var (list, name, check) in new (List<string>, string, Func<string, bool>)[] {
                     (c0.RemoveScheduledTasks, "install.cleanup.removeScheduledTasks", IsTaskOrValueName),
                     (c0.RemoveRunValues, "install.cleanup.removeRunValues", IsTaskOrValueName),
                     (c0.RemoveShortcuts, "install.cleanup.removeShortcuts", IsShortcutName),
                     (c0.RemoveUninstallKeys, "install.cleanup.removeUninstallKeys", n => IsTaskOrValueName(n) && !n.Equals(p.App.Id, StringComparison.OrdinalIgnoreCase)),
                     (c0.RemoveFiles, "install.cleanup.removeFiles", IsFilePattern),
                     (p.Uninstall.RemoveShortcuts, "uninstall.removeShortcuts", IsShortcutName),
                     (p.Uninstall.RemoveRegistryKeys, "uninstall.removeRegistryKeys", k => IsRemovableRegistryKey(k, "HKCU")),
                     (p.Uninstall.Elevated.RemoveRegistryKeys, "uninstall.elevated.removeRegistryKeys", k => IsRemovableRegistryKey(k)) })
        {
            if (list.Count > 32) d.Error("FS1652", name, "At most 32 entries.");
            foreach (var (n, i) in list.Select((n, i) => (n, i)))
                if (!check(n)) d.Error("FS1653", $"{name}[{i}]", $"'{n}' is not a valid name here.");
        }

        if (!string.IsNullOrEmpty(p.Install.StartMenuFolder) && (!PathRules.IsValidComponent(p.Install.StartMenuFolder, out _) || p.Install.StartMenuFolder.Length > 64))
            d.Error("FS1667", "install.startMenuFolder", "A single folder name (max 64 characters), e.g. the publisher or app name.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (o, i) in p.Options.Select((o, i) => (o, i)))
        {
            if (!OptionIdRegex().IsMatch(o.Id)) d.Error("FS1610", $"options[{i}].id", "Option ids use letters, digits, '-' and '_' (max 32).");
            else if (!ids.Add(o.Id)) d.Error("FS1611", $"options[{i}].id", $"Duplicate option id '{o.Id}'.");
            if (o.Startup is { } st && (!IsTaskOrValueName(st.Name) || st.Arguments.Count > 32))
                d.Error("FS1664", $"options[{i}].startup", "Needs a Run value name and at most 32 arguments.");
            if (o.Startup is not null && string.IsNullOrEmpty(p.App.MainExecutable))
                d.Error("FS1665", $"options[{i}].startup", "Needs app.mainExecutable (the program that is started).");
            if (o.Label.Count == 0 || o.Label.Values.Any(string.IsNullOrWhiteSpace)) d.Error("FS1612", $"options[{i}].label", "Give the option a label (per language, or in the localization files plus one here).");
            if (o.Detect is { } det && ((det.RunValue.Length > 0 && !IsTaskOrValueName(det.RunValue)) || (det.ScheduledTask.Length > 0 && !IsTaskPath(det.ScheduledTask))))
                d.Error("FS1668", $"options[{i}].detect", @"runValue is a Run value name, scheduledTask a task name (folders: ""Folder\Task"").");
            if (o.Required && (o.Detect is not null || o.QuietDefault is not null))
                d.Warning("FS1669", $"options[{i}]", "A required option is always selected: detect and quietDefault are ignored.");
        }
        if (p.Options.Count > 16) d.Error("FS1613", "options", "At most 16 options.");

        foreach (var (a, i) in p.Actions.Select((a, i) => (a, i)))
        {
            var field = $"actions[{i}]";
            if (a.When is not ("afterInstall" or "beforeUninstall")) d.Error("FS1620", field + ".when", "Use afterInstall or beforeUninstall.");
            if (!PathRules.TryNormalizeRelative(a.Run, out var run, out var runWhy) || !run.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                d.Error("FS1621", field + ".run", $"Actions run an .exe from the installed files, given as a relative path ({runWhy ?? "not an .exe"}).");
            }
            if (a.Arguments.Count > 32 || a.Arguments.Any(x => x.Length > 1024 || x.Contains('\0'))) d.Error("FS1622", field + ".arguments", "Too many or too long arguments.");
            if (a.TimeoutSeconds is < 1 or > 3600) d.Error("FS1623", field + ".timeoutSeconds", "Timeout must be 1-3600 seconds.");
            if (a.OnFailure is not ("fail" or "warn" or "ignore")) d.Error("FS1624", field + ".onFailure", "Use fail, warn or ignore.");
            if (!string.IsNullOrEmpty(a.IfOption) && !p.Options.Any(o => o.Id == a.IfOption)) d.Error("FS1625", field + ".ifOption", $"No option '{a.IfOption}'.");
            if (a.SkipIfInstalled is { } c &&
                (c.Hive is not ("HKLM" or "HKCU") || string.IsNullOrWhiteSpace(c.Key) || c.Key.Length > 255 || c.Key.StartsWith('\\') ||
                 (c.Value?.Length ?? 0) > 128 || !ProjectValidator.IsValidVersion(c.Version)))
            {
                d.Error("FS1626", field + ".skipIfInstalled", "Needs hive HKLM|HKCU, a key below it (up to 255 characters), a value name (up to 128 characters; empty = the key's default value) and a version.");
            }
            if (!string.IsNullOrEmpty(a.Id) && !OptionIdRegex().IsMatch(a.Id)) d.Error("FS1629", field + ".id", "Action ids use letters, digits, '-' and '_' (max 32).");
            if (a.FallbackArguments is { } fb && (fb.Count > 32 || fb.Any(x => x.Length > 1024 || x.Contains('\0')))) d.Error("FS1622", field + ".fallbackArguments", "Too many or too long arguments.");
            if (a.FallbackRunValue is { } rv && (!IsTaskOrValueName(rv.Name) || rv.Arguments.Count > 32))
                d.Error("FS1633", field + ".fallbackRunValue", "Needs a Run value name and at most 32 arguments.");
            if (a.When == "afterInstall" && a.OnFailure == "fail")
            {
                d.Info("FS1627", field, "If this step fails the installation is rolled back; effects of the program itself (drivers, tasks) cannot be undone by setup.");
            }
        }
        if (p.Actions.Count > 32) d.Error("FS1628", "actions", "At most 32 actions.");

        foreach (var (t, i) in p.Uninstall.RemoveData.Select((t, i) => (t, i)))
        {
            if (!IsDataOrInstallDirTemplate(t))
            {
                d.Error("FS1630", $"uninstall.removeData[{i}]", @"Must be {CommonAppData}\Folder, {LocalAppData}\Folder, {AppData}\Folder or {InstallDir}\Folder.");
            }
        }
        foreach (var (t, i) in p.Uninstall.RemoveScheduledTasks.Select((t, i) => (t, i)))
        {
            if (!IsTaskPath(t)) d.Error("FS1631", $"uninstall.removeScheduledTasks[{i}]", "Invalid scheduled task name (folders: \"Folder\\Task\").");
        }
        foreach (var (t, i) in p.Uninstall.RemoveRunValues.Select((t, i) => (t, i)))
        {
            if (!IsTaskOrValueName(t)) d.Error("FS1632", $"uninstall.removeRunValues[{i}]", "Invalid Run value name.");
        }
        var ec = p.Uninstall.Elevated;
        foreach (var (list, name, check, rule) in new (List<string>, string, Func<string, bool>, string)[] {
                     (p.Uninstall.UserData.Paths, "uninstall.userData.paths", IsDataOrInstallDirTemplate, @"{LocalAppData}\X, {AppData}\X, {CommonAppData}\X or {InstallDir}\X"),
                     (ec.IfExists, "uninstall.elevated.ifExists", IsDataTemplate, @"{LocalAppData}\X, {AppData}\X or {CommonAppData}\X"),
                     (ec.RemoveData, "uninstall.elevated.removeData", IsDataTemplate, @"{LocalAppData}\X, {AppData}\X or {CommonAppData}\X"),
                     (ec.TraceSessions, "uninstall.elevated.traceSessions", IsTaskOrValueName, "a trace session name"),
                     (ec.ScheduledTasks, "uninstall.elevated.scheduledTasks", IsTaskPath, "a task name, folders allowed (Folder\\Task)") })
        {
            if (list.Count > 32) d.Error("FS1654", name, "At most 32 entries.");
            foreach (var (n, i) in list.Select((n, i) => (n, i)))
                if (!check(n)) d.Error("FS1655", $"{name}[{i}]", $"'{n}' must be {rule}.");
        }
        foreach (var (s, i) in ec.ServiceAccess.Select((s, i) => (s, i)))
        {
            if (!IsTaskOrValueName(s.Service) || s.Service.Contains(' ') || s.Rights.Count is 0 or > 8 ||
                s.Rights.Any(r => r.Length is 0 or > 32 || !r.All(char.IsAsciiLetterUpper)))
                d.Error("FS1656", $"uninstall.elevated.serviceAccess[{i}]", "Needs a service name and 1-8 SDDL rights strings such as LCRPWP.");
        }
        if (p.Install.RelaunchArguments.Count > 32) d.Error("FS1657", "install.relaunchArguments", "At most 32 arguments.");
        if (p.Install.AddToPath.Count > 8) d.Error("FS1658", "install.addToPath", "At most 8 folders.");
        foreach (var (dirName, i) in p.Install.AddToPath.Select((x, i) => (x, i)))
        {
            if (dirName is not ("" or ".") && !PathRules.TryNormalizeRelative(dirName, out _, out _))
                d.Error("FS1659", $"install.addToPath[{i}]", "A folder inside the install folder (relative), or \"\" for the install folder itself.");
        }
    }

    /// <summary>Checks that action targets exist among the files to install.</summary>
    public static void ValidateAgainstFiles(SetupProject p, IEnumerable<CollectedFile> files, DiagnosticBag d)
    {
        var set = new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var (a, i) in p.Actions.Select((a, i) => (a, i)))
        {
            if (PathRules.TryNormalizeRelative(a.Run, out var run, out _) && !set.Contains(run))
                d.Error("FS1640", $"actions[{i}].run", $"'{run}' is not among the files to install.");
        }
        foreach (var (s, field) in Sources(p))
        {
            if (!string.IsNullOrEmpty(s.Installer) && PathRules.TryNormalizeRelative(s.Installer, out var w, out _) && !set.Contains(w))
                d.Error("FS1641", field + ".installer", $"'{w}' is not among the files to install.");
        }
        foreach (var (n, i) in p.Install.Ngen.Select((n, i) => (n, i)))
        {
            if (PathRules.TryNormalizeRelative(n, out var rel, out _) && !set.Contains(rel))
                d.Error("FS1642", $"install.ngen[{i}]", $"'{rel}' is not among the files to install.");
        }
        IntegrationValidator.ValidateAgainstFiles(p, set, d);
    }
}
