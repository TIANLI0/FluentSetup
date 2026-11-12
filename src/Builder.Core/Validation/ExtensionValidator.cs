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

    private static bool IsExeName(string n) => PathRules.IsValidComponent(n, out _) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
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
        if (!string.IsNullOrEmpty(p.Requirements.WebView2.Installer) && !PathRules.TryNormalizeRelative(p.Requirements.WebView2.Installer, out _, out var why))
        {
            d.Error("FS1604", "requirements.webView2.installer", $"Not a valid relative path: {why}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (o, i) in p.Options.Select((o, i) => (o, i)))
        {
            if (!OptionIdRegex().IsMatch(o.Id)) d.Error("FS1610", $"options[{i}].id", "Option ids use letters, digits, '-' and '_' (max 32).");
            else if (!ids.Add(o.Id)) d.Error("FS1611", $"options[{i}].id", $"Duplicate option id '{o.Id}'.");
            if (o.Label.Count == 0 || o.Label.Values.Any(string.IsNullOrWhiteSpace)) d.Error("FS1612", $"options[{i}].label", "Give the option a label (per language).");
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
            if (a.When == "afterInstall" && a.OnFailure == "fail")
            {
                d.Info("FS1627", field, "If this step fails the installation is rolled back; effects of the program itself (drivers, tasks) cannot be undone by setup.");
            }
        }
        if (p.Actions.Count > 32) d.Error("FS1628", "actions", "At most 32 actions.");

        foreach (var (t, i) in p.Uninstall.RemoveData.Select((t, i) => (t, i)))
        {
            var root = DataRoots.FirstOrDefault(r => t.StartsWith(r, StringComparison.Ordinal));
            if (root is null || !PathRules.TryNormalizeRelative(t[root.Length..], out _, out _))
            {
                d.Error("FS1630", $"uninstall.removeData[{i}]", @"Must be {CommonAppData}\Folder, {LocalAppData}\Folder or {AppData}\Folder.");
            }
        }
        foreach (var (t, i) in p.Uninstall.RemoveScheduledTasks.Select((t, i) => (t, i)))
        {
            if (!IsTaskOrValueName(t)) d.Error("FS1631", $"uninstall.removeScheduledTasks[{i}]", "Invalid scheduled task name.");
        }
        foreach (var (t, i) in p.Uninstall.RemoveRunValues.Select((t, i) => (t, i)))
        {
            if (!IsTaskOrValueName(t)) d.Error("FS1632", $"uninstall.removeRunValues[{i}]", "Invalid Run value name.");
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
        if (!string.IsNullOrEmpty(p.Requirements.WebView2.Installer) && PathRules.TryNormalizeRelative(p.Requirements.WebView2.Installer, out var w, out _) && !set.Contains(w))
        {
            d.Error("FS1641", "requirements.webView2.installer", $"'{w}' is not among the files to install.");
        }
    }
}
