using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Validation;

/// <summary>
/// Validates the 1.2 sections: shortcuts, file associations, protocols, registry, environment, services, firewall,
/// inputs, config files and the install/app/ui settings that go with them. Mirrors the native checks in
/// src/Install.Core/extensions.cpp (the installer validates the package header again).
/// </summary>
public static partial class IntegrationValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}$")] private static partial Regex IdRegex();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$")] private static partial Regex AumidRegex();
    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")] private static partial Regex ClsidRegex();
    [GeneratedRegex(@"^\.[A-Za-z0-9_-]{1,32}$")] private static partial Regex ExtensionRegex();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$")] private static partial Regex ProgIdRegex();
    [GeneratedRegex(@"^[a-z][a-z0-9+.-]{1,31}$")] private static partial Regex SchemeRegex();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.()-]{0,127}$")] private static partial Regex EnvNameRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,80}$")] private static partial Regex ServiceNameRegex();
    [GeneratedRegex(@"^[!#$&^_.+\-/A-Za-z0-9]{0,128}$")] private static partial Regex ContentTypeRegex();
    [GeneratedRegex(@"\{Input:([^}]*)\}")] private static partial Regex InputRefRegex();

    private static readonly HashSet<string> ReservedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".lnk", ".dll", ".sys", ".msi", ".msp", ".msu", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js",
        ".jse", ".wsf", ".wsh", ".scr", ".pif", ".cpl", ".reg", ".url", ".hta", ".inf", ".msc", ".ocx", ".drv", ".appx", ".msix",
        ".appxbundle", ".msixbundle", ".appinstaller", ".library-ms", ".search-ms", ".settingcontent-ms", ".application", ".jar", ".scf",
        ".theme", ".themepack", ".diagcab",
    };
    private static readonly HashSet<string> ReservedSchemes = new(StringComparer.Ordinal)
    {
        "http", "https", "file", "ftp", "ftps", "mailto", "news", "nntp", "telnet", "ldap", "tel", "sms", "callto", "javascript", "vbscript",
        "data", "about", "res", "shell", "search", "its", "mk", "hcp", "read", "onenote", "skype", "zoommtg", "steam",
    };
    private static readonly HashSet<string> ReservedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path", "PATHEXT", "SystemRoot", "windir", "TEMP", "TMP", "ComSpec", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "OS", "NUMBER_OF_PROCESSORS", "PSModulePath", "USERNAME", "USERDOMAIN",
        "HOMEDRIVE", "HOMEPATH", "PUBLIC", "ALLUSERSPROFILE", "SystemDrive", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "CommonProgramW6432", "DriverData", "COMPUTERNAME", "LOGONSERVER", "SESSIONNAME",
    };
    private static readonly string[] DataRoots = [@"{CommonAppData}\", @"{LocalAppData}\", @"{AppData}\"];
    private static readonly string[] SystemRoots = [@"{ProgramFiles}\", @"{ProgramFilesX86}\", @"{System}\", @"{CommonAppData}\", @"{LocalAppData}\", @"{AppData}\"];

    public static bool IsAssociableExtension(string ext) => ExtensionRegex().IsMatch(ext) && !ReservedExtensions.Contains(ext);
    public static bool IsProgId(string id) =>
        id.Length <= 39 && ProgIdRegex().IsMatch(id) && !id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
        !id.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) && !id.StartsWith("AppX", StringComparison.OrdinalIgnoreCase);
    public static bool IsCustomScheme(string s) =>
        SchemeRegex().IsMatch(s) && !ReservedSchemes.Contains(s) && !s.StartsWith("ms-", StringComparison.Ordinal) &&
        !s.StartsWith("windows", StringComparison.Ordinal) && !s.StartsWith("microsoft", StringComparison.Ordinal);
    public static bool IsEnvironmentName(string n) => EnvNameRegex().IsMatch(n) && !ReservedEnvironment.Contains(n) && !n.StartsWith("PROCESSOR_", StringComparison.OrdinalIgnoreCase);
    public static bool IsServiceName(string n) => ServiceNameRegex().IsMatch(n);

    /// <summary>Software\Vendor[\...] below the scope's hive (not a shared tree, not FluentSetup's own keys).</summary>
    public static bool IsVendorKey(string key) =>
        ExtensionValidator.IsRemovableRegistryKey(@"HKCU\" + key) &&
        !key.Split('\\')[1].Equals("FluentSetup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decimal or 0x hex; dword values must fit in 32 bits.</summary>
    public static bool IsRegistryNumber(string text, bool qword)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = hex ? text[2..] : text;
        if (digits.Length == 0) return false;
        if (!ulong.TryParse(digits, hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        return qword || v <= uint.MaxValue;
    }

    public static bool IsPortList(string list) =>
        list.Length <= 256 && list.Split(',').All(part =>
        {
            var range = part.Split('-');
            return range.Length is 1 or 2 && range.All(n => n.Length is > 0 and <= 5 && n.All(char.IsAsciiDigit) && int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 65535);
        });

    private static bool IsRelative(string path) => PathRules.TryNormalizeRelative(path, out _, out _);
    private static bool IsExe(string path) => PathRules.TryNormalizeRelative(path, out var n, out _) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    private static bool BadArguments(List<string> args) => args.Count > 32 || args.Any(a => a.Length > 1024 || a.Contains('\0'));
    private static bool HasControl(string s) => s.Any(c => c < 0x20);

    public static void Validate(SetupProject p, string projectDirectory, DiagnosticBag d)
    {
        var options = new HashSet<string>(p.Options.Select(o => o.Id), StringComparer.Ordinal);
        void CheckOption(string ifOption, string field, string code)
        {
            if (!string.IsNullOrEmpty(ifOption) && !options.Contains(ifOption)) d.Error(code, field + ".ifOption", $"No option '{ifOption}'.");
        }
        var hasMain = !string.IsNullOrEmpty(p.App.MainExecutable);
        var machineOnly = p.Install.Scope == "machine";
        var userOnly = p.Install.Scope == "user";

        // ---- app / install / ui ------------------------------------------------------------------------------------
        if (p.App.AppUserModelId.Length > 0 && !AumidRegex().IsMatch(p.App.AppUserModelId))
            d.Error("FS1220", "app.appUserModelId", "An AppUserModelID has up to 128 letters, digits, '.', '-' or '_' (no spaces), e.g. Contoso.Notes.");
        if (p.App.ToastActivatorClsid.Length > 0 && !ClsidRegex().IsMatch(p.App.ToastActivatorClsid))
            d.Error("FS1221", "app.toastActivatorClsid", "Write the CLSID as {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}.");
        if (p.App.ToastActivatorClsid.Length > 0 && p.App.AppUserModelId.Length == 0)
            d.Warning("FS1222", "app.toastActivatorClsid", "A toast activator only works together with app.appUserModelId.");
        if (p.Install.RestartManager is not ("auto" or "off")) d.Error("FS1223", "install.restartManager", "Use auto or off.");
        if (p.Install.LockedFiles is not ("fail" or "reboot")) d.Error("FS1224", "install.lockedFiles", "Use fail or reboot.");
        if (p.Install.LockedFiles == "reboot" && userOnly)
            d.Warning("FS1225", "install.lockedFiles", "Replacing files at the next restart needs administrator rights; per-user installs fail instead.");
        if (p.Install.AppPaths && !hasMain) d.Error("FS1226", "install.appPaths", "App Paths registers app.mainExecutable; set it first.");

        if (!string.IsNullOrEmpty(p.Ui.Banner))
        {
            var banner = ProjectValidator.Resolve(projectDirectory, p.Ui.Banner);
            if (!File.Exists(banner)) d.Error("FS1312", "ui.banner", $"Banner image not found: {banner}");
            else if (Path.GetExtension(banner).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp")) d.Error("FS1313", "ui.banner", "The banner must be a .png, .jpg or .bmp image.");
            else if (new FileInfo(banner).Length > 1024 * 1024) d.Error("FS1314", "ui.banner", "The banner image is larger than 1 MB.");
        }

        // ---- shortcuts ------------------------------------------------------------------------------------------
        if (p.Shortcuts.Count > 16) d.Error("FS1901", "shortcuts", "At most 16 shortcuts.");
        var shortcutNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s, i) in p.Shortcuts.Select((s, i) => (s, i)))
        {
            var f = $"shortcuts[{i}]";
            if (s.Name.Length is 0 or > 128 || !PathRules.IsValidComponent(s.Name + ".lnk", out _)) d.Error("FS1902", f + ".name", "A shortcut name (without .lnk, max 128 characters) that is a valid file name.");
            if (s.Target.Length == 0 && !hasMain) d.Error("FS1903", f + ".target", "Set the target (or app.mainExecutable).");
            if (s.Target.Length > 0 && !IsRelative(s.Target)) d.Error("FS1904", f + ".target", "The target is a relative path among the installed files.");
            if (s.Icon.Length > 0 && !IsRelative(s.Icon)) d.Error("FS1905", f + ".icon", "The icon is a relative path among the installed files.");
            if (s.WorkingDirectory.Length > 0 && !IsRelative(s.WorkingDirectory)) d.Error("FS1906", f + ".workingDirectory", "A folder inside the install folder (relative).");
            if (s.Location is not ("startMenu" or "desktop" or "startup")) d.Error("FS1907", f + ".location", "Use startMenu, desktop or startup.");
            if (s.Folder.Length > 0 && (!PathRules.IsValidComponent(s.Folder, out _) || s.Folder.Length > 64)) d.Error("FS1908", f + ".folder", "A single folder name (max 64 characters).");
            CheckOption(s.IfOption, f, "FS1909");
            if (s.AppUserModelId.Length > 0 && !AumidRegex().IsMatch(s.AppUserModelId)) d.Error("FS1910", f + ".appUserModelId", "Up to 128 letters, digits, '.', '-' or '_'.");
            if (BadArguments(s.Arguments)) d.Error("FS1911", f + ".arguments", "At most 32 arguments of up to 1024 characters.");
            if (s.Description.Length > 259 || HasControl(s.Description)) d.Error("FS1911", f + ".description", "At most 259 characters on one line.");
            var where = s.Location + "|" + (s.Location == "startMenu" ? (s.Folder.Length > 0 ? s.Folder : p.Install.StartMenuFolder) : "") + "|" + s.Name;
            if (!shortcutNames.Add(where)) d.Error("FS1912", f + ".name", $"Another shortcut has the name '{s.Name}' in the same place.");
            if (s.Location != "desktop" && s.Name.Equals(p.App.Name, StringComparison.OrdinalIgnoreCase) && s.Location == "startMenu" &&
                (s.Folder.Length == 0 ? p.Install.StartMenuFolder : s.Folder).Equals(p.Install.StartMenuFolder, StringComparison.OrdinalIgnoreCase))
                d.Warning("FS1912", f + ".name", "This name is the app's own Start menu shortcut; it would replace it.");
        }

        // ---- file associations / protocols -------------------------------------------------------------------------
        if (p.Associations.Count > 16) d.Error("FS1920", "associations", "At most 16 file types.");
        var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (a, i) in p.Associations.Select((a, i) => (a, i)))
        {
            var f = $"associations[{i}]";
            if (a.Extensions.Count is 0 or > 16) d.Error("FS1921", f + ".extensions", "List 1-16 extensions, e.g. [\".cnote\"].");
            foreach (var (x, j) in a.Extensions.Select((x, j) => (x, j)))
            {
                if (!IsAssociableExtension(x)) d.Error("FS1922", $"{f}.extensions[{j}]", $"'{x}' is not an extension the installer may register ('.' + 1-32 letters, digits, '-', '_'; not a program or shell type such as .exe or .lnk).");
                else if (!extensions.Add(x)) d.Error("FS1930", $"{f}.extensions[{j}]", $"'{x}' is associated twice.");
            }
            if (!IsProgId(a.ProgId)) d.Error("FS1923", f + ".progId", "A ProgID such as Contoso.Notes.Document (letters and digits in dot-separated parts, max 39 characters, not Microsoft.* or Windows.*).");
            else if (!progIds.Add(a.ProgId)) d.Error("FS1930", f + ".progId", $"ProgID '{a.ProgId}' is used twice.");
            if (a.Description.Length > 256 || HasControl(a.Description)) d.Error("FS1924", f + ".description", "At most 256 characters on one line.");
            if (a.Target.Length == 0 && !hasMain) d.Error("FS1925", f + ".target", "Set the target (or app.mainExecutable).");
            if (a.Target.Length > 0 && !IsExe(a.Target)) d.Error("FS1925", f + ".target", "The program is a relative path of an .exe among the installed files.");
            if (a.Icon.Length > 0 && !IsRelative(a.Icon)) d.Error("FS1926", f + ".icon", "The icon is a relative path among the installed files.");
            if (BadArguments(a.Arguments)) d.Error("FS1927", f + ".arguments", "At most 32 arguments of up to 1024 characters.");
            else if (!a.Arguments.Any(x => x.Contains("%1", StringComparison.Ordinal))) d.Warning("FS1927", f + ".arguments", "No argument contains %1, so the program is not told which file to open.");
            if (!ContentTypeRegex().IsMatch(a.ContentType)) d.Error("FS1928", f + ".contentType", "A MIME type such as application/x-contoso-note.");
            CheckOption(a.IfOption, f, "FS1929");
        }
        if (p.Protocols.Count > 16) d.Error("FS1932", "protocols", "At most 16 protocols.");
        var schemes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pr, i) in p.Protocols.Select((x, i) => (x, i)))
        {
            var f = $"protocols[{i}]";
            if (!IsCustomScheme(pr.Scheme)) d.Error("FS1933", f + ".scheme", $"'{pr.Scheme}' is not a scheme the installer may register (lowercase, 2-32 characters; not http, https, file, mailto, ms-*, …).");
            else if (!schemes.Add(pr.Scheme)) d.Error("FS1936", f + ".scheme", $"'{pr.Scheme}' is registered twice.");
            if (pr.Description.Length > 256 || HasControl(pr.Description)) d.Error("FS1933", f + ".description", "At most 256 characters on one line.");
            if (pr.Target.Length == 0 && !hasMain) d.Error("FS1934", f + ".target", "Set the target (or app.mainExecutable).");
            if (pr.Target.Length > 0 && !IsExe(pr.Target)) d.Error("FS1934", f + ".target", "The program is a relative path of an .exe among the installed files.");
            if (pr.Icon.Length > 0 && !IsRelative(pr.Icon)) d.Error("FS1934", f + ".icon", "The icon is a relative path among the installed files.");
            if (BadArguments(pr.Arguments)) d.Error("FS1934", f + ".arguments", "At most 32 arguments of up to 1024 characters.");
            CheckOption(pr.IfOption, f, "FS1935");
        }

        // ---- registry / environment ------------------------------------------------------------------------------
        if (p.Registry.Count > 32) d.Error("FS1940", "registry", "At most 32 registry keys.");
        foreach (var (r, i) in p.Registry.Select((r, i) => (r, i)))
        {
            var f = $"registry[{i}]";
            if (!IsVendorKey(r.Key)) d.Error("FS1941", f + ".key", @"A key below the scope's hive such as Software\Contoso\Notes (Software\<Vendor> or deeper; not Microsoft, Classes, Policies, Wow6432Node, RegisteredApplications, Clients or FluentSetup). Use associations and protocols for Software\Classes.");
            if (r.Values.Count > 32) d.Error("FS1942", f + ".values", "At most 32 values per key.");
            if (r.Uninstall is not ("values" or "key" or "keep")) d.Error("FS1946", f + ".uninstall", "Use values, key or keep.");
            CheckOption(r.IfOption, f, "FS1947");
            foreach (var (v, j) in r.Values.Select((v, j) => (v, j)))
            {
                var vf = $"{f}.values[{j}]";
                if (v.Name.Length > 255 || HasControl(v.Name)) d.Error("FS1943", vf + ".name", "At most 255 characters, no control characters.");
                if (v.Type is not ("string" or "expandString" or "dword" or "qword" or "multiString")) d.Error("FS1944", vf + ".type", "Use string, expandString, dword, qword or multiString.");
                else if (v.Type is "dword" or "qword" && !v.Data.Contains('{') && !IsRegistryNumber(v.Data, v.Type == "qword"))
                    d.Error("FS1945", vf + ".data", $"'{v.Data}' is not a {(v.Type == "dword" ? "32" : "64")}-bit number (decimal or 0x hex).");
                if (v.Data.Length > 16384) d.Error("FS1948", vf + ".data", "At most 16384 characters.");
            }
            if (r.Values.Count == 0 && r.Uninstall == "values") d.Info("FS1948", f, "A key without values is created and removed again on uninstall once it is empty.");
        }
        if (p.Environment.Count > 16) d.Error("FS1950", "environment", "At most 16 environment variables.");
        var envNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (e, i) in p.Environment.Select((e, i) => (e, i)))
        {
            var f = $"environment[{i}]";
            if (e.Name.Equals("Path", StringComparison.OrdinalIgnoreCase)) d.Error("FS1951", f + ".name", "Use install.addToPath to put folders on PATH.");
            else if (!IsEnvironmentName(e.Name)) d.Error("FS1951", f + ".name", $"'{e.Name}' is not a name the installer may set (letters, digits, _ . ( ) -; not a Windows variable such as TEMP or SystemRoot).");
            else if (!envNames.Add(e.Name)) d.Error("FS1954", f + ".name", $"'{e.Name}' is set twice.");
            if (e.Value.Length > 8192 || e.Value.Contains('\0')) d.Error("FS1952", f + ".value", "At most 8192 characters.");
            CheckOption(e.IfOption, f, "FS1953");
        }

        // ---- services / firewall (per-machine) ----------------------------------------------------------------
        if (p.Services.Count > 8) d.Error("FS1960", "services", "At most 8 services.");
        if (p.Services.Count > 0 && userOnly) d.Error("FS1966", "services", "Services need a per-machine install (install.scope machine or userOrMachine).");
        else if (p.Services.Count > 0 && !machineOnly) d.Warning("FS1967", "services", "Services are only installed when the user chooses \"All users\".");
        var serviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s, i) in p.Services.Select((s, i) => (s, i)))
        {
            var f = $"services[{i}]";
            if (!IsServiceName(s.Name)) d.Error("FS1961", f + ".name", "Letters, digits, '.', '-' and '_' (max 80).");
            else if (!serviceNames.Add(s.Name)) d.Error("FS1968", f + ".name", $"Service '{s.Name}' is declared twice.");
            if (!IsExe(s.Run)) d.Error("FS1962", f + ".run", "The service program is a relative path of an .exe among the installed files.");
            if (s.StartType is not ("auto" or "delayedAuto" or "manual" or "disabled")) d.Error("FS1963", f + ".startType", "Use auto, delayedAuto, manual or disabled.");
            if (s.Account is not ("localSystem" or "localService" or "networkService")) d.Error("FS1964", f + ".account", "Use localSystem, localService or networkService.");
            if (s.Dependencies.Count > 16 || s.Dependencies.Any(x => !IsServiceName(x))) d.Error("FS1965", f + ".dependencies", "At most 16 service names.");
            if (BadArguments(s.Arguments)) d.Error("FS1970", f + ".arguments", "At most 32 arguments of up to 1024 characters.");
            if (s.DisplayName.Length > 256 || s.Description.Length > 1024 || HasControl(s.DisplayName) || HasControl(s.Description))
                d.Error("FS1970", f, "Display name up to 256 and description up to 1024 characters, on one line.");
        }
        if (p.Firewall.Count > 16) d.Error("FS1971", "firewall", "At most 16 firewall rules.");
        if (p.Firewall.Count > 0 && userOnly) d.Error("FS1978", "firewall", "Firewall rules need a per-machine install (install.scope machine or userOrMachine).");
        else if (p.Firewall.Count > 0 && !machineOnly) d.Warning("FS1978", "firewall", "Firewall rules are only added when the user chooses \"All users\".");
        foreach (var (r, i) in p.Firewall.Select((r, i) => (r, i)))
        {
            var f = $"firewall[{i}]";
            if (r.Name.Length is 0 or > 128 || r.Name.Contains('|') || HasControl(r.Name)) d.Error("FS1972", f + ".name", "A rule name of 1-128 characters (no '|').");
            if (r.Program.Length == 0 && !hasMain) d.Error("FS1973", f + ".program", "Set the program (or app.mainExecutable).");
            if (r.Program.Length > 0 && !IsExe(r.Program)) d.Error("FS1973", f + ".program", "A relative path of an .exe among the installed files.");
            if (r.Direction is not ("in" or "out")) d.Error("FS1974", f + ".direction", "Use in or out.");
            if (r.Protocol is not ("any" or "tcp" or "udp")) d.Error("FS1975", f + ".protocol", "Use any, tcp or udp.");
            if (r.LocalPorts.Length > 0 && (r.Protocol == "any" || !IsPortList(r.LocalPorts))) d.Error("FS1976", f + ".localPorts", "Ports (1-65535, ranges with '-', separated by ',') need protocol tcp or udp.");
            if (r.Profiles.Any(x => x is not ("domain" or "private" or "public"))) d.Error("FS1977", f + ".profiles", "Use domain, private and/or public.");
            CheckOption(r.IfOption, f, "FS1979");
        }

        // ---- inputs / config files -------------------------------------------------------------------------------
        if (p.Inputs.Count > 8) d.Error("FS1980", "inputs", "At most 8 inputs.");
        var inputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (inp, i) in p.Inputs.Select((x, i) => (x, i)))
        {
            var f = $"inputs[{i}]";
            if (!IdRegex().IsMatch(inp.Id)) d.Error("FS1981", f + ".id", "Input ids use letters, digits, '-' and '_' (max 32).");
            else if (!inputIds.Add(inp.Id)) d.Error("FS1982", f + ".id", $"Duplicate input id '{inp.Id}'.");
            if ((inp.Label.Count == 0 || inp.Label.Values.Any(string.IsNullOrWhiteSpace)) && string.IsNullOrEmpty(p.Ui.Localization))
                d.Error("FS1983", f + ".label", "Give the field a label (per language).");
            if (inp.Type is not ("text" or "password" or "choice")) d.Error("FS1984", f + ".type", "Use text, password or choice.");
            if (inp.Type == "choice" && inp.Choices.Count == 0) d.Error("FS1985", f + ".choices", "A choice needs at least one value.");
            if (inp.Choices.Count > 32) d.Error("FS1985", f + ".choices", "At most 32 choices.");
            Regex? pattern = null;
            if (inp.Pattern.Length > 512) d.Error("FS1986", f + ".pattern", "At most 512 characters.");
            else if (inp.Pattern.Length > 0)
            {
                try { pattern = new Regex(inp.Pattern, RegexOptions.ECMAScript, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex) { d.Error("FS1986", f + ".pattern", $"Not a valid regular expression: {ex.Message}"); }
            }
            if (inp.Default.Length > 4096) d.Error("FS1987", f + ".default", "At most 4096 characters.");
            if (inp.Default.Length > 0 && ((pattern is not null && !pattern.IsMatch(inp.Default)) || (inp.Type == "choice" && inp.Choices.All(c => c.Value != inp.Default))))
                d.Warning("FS1988", f + ".default", "The default value is not accepted by the field (pattern or choices).");
            if (inp.Type == "password" && inp.Remember) d.Info("FS1989", f + ".remember", "Passwords are never remembered for updates.");
        }
        if (p.ConfigFiles.Count > 16) d.Error("FS1990", "configFiles", "At most 16 config files.");
        foreach (var (c, i) in p.ConfigFiles.Select((c, i) => (c, i)))
        {
            var f = $"configFiles[{i}]";
            var root = DataRoots.FirstOrDefault(r => c.Path.StartsWith(r, StringComparison.Ordinal));
            var pathOk = root is not null
                ? PathRules.TryNormalizeRelative(c.Path[root.Length..], out _, out _)
                : !c.Path.StartsWith('{') && PathRules.TryNormalizeRelative(c.Path, out var rel, out _) && !PathRules.IsInstallerReserved(rel);
            if (!pathOk) d.Error("FS1991", f + ".path", @"A path relative to the install folder, or {LocalAppData}\X, {AppData}\X, {CommonAppData}\X.");
            if (c.Format is not ("json" or "ini")) d.Error("FS1992", f + ".format", "Use json or ini.");
            if (c.Values.Count is 0 or > 64) d.Error("FS1993", f + ".values", "List 1-64 values.");
            CheckOption(c.IfOption, f, "FS1997");
            foreach (var (v, j) in c.Values.Select((v, j) => (v, j)))
            {
                var vf = $"{f}.values[{j}]";
                var keyBad = v.Key.Length is 0 or > 256 || HasControl(v.Key) ||
                             (c.Format == "ini" ? v.Key.Contains('=') : v.Key.StartsWith('.') || v.Key.EndsWith('.') || v.Key.Contains("..", StringComparison.Ordinal));
                if (keyBad) d.Error("FS1994", vf + ".key", c.Format == "ini" ? "An INI key (no '=')." : "A dotted JSON path such as server.url.");
                if (c.Format == "ini" && (v.Section.Length is 0 or > 256 || HasControl(v.Section))) d.Error("FS1995", vf + ".section", "INI values need a section.");
                if (v.Type is not ("string" or "number" or "bool")) d.Error("FS1996", vf + ".type", "Use string, number or bool.");
                if (v.Value.Length > 4096) d.Error("FS1999", vf + ".value", "At most 4096 characters.");
            }
        }

        // {Input:id} references must name a declared input.
        void CheckRefs(string text, string field)
        {
            foreach (Match m in InputRefRegex().Matches(text))
                if (!inputIds.Contains(m.Groups[1].Value)) d.Error("FS1998", field, $"{{Input:{m.Groups[1].Value}}} refers to no input in inputs[].");
        }
        foreach (var (r, i) in p.Registry.Select((r, i) => (r, i)))
            foreach (var (v, j) in r.Values.Select((v, j) => (v, j))) CheckRefs(v.Data, $"registry[{i}].values[{j}].data");
        foreach (var (e, i) in p.Environment.Select((e, i) => (e, i))) CheckRefs(e.Value, $"environment[{i}].value");
        foreach (var (c, i) in p.ConfigFiles.Select((c, i) => (c, i)))
            foreach (var (v, j) in c.Values.Select((v, j) => (v, j))) CheckRefs(v.Value, $"configFiles[{i}].values[{j}].value");
        foreach (var (s, i) in p.Shortcuts.Select((s, i) => (s, i)))
            foreach (var a in s.Arguments) CheckRefs(a, $"shortcuts[{i}].arguments");
        foreach (var (a, i) in p.Actions.Select((a, i) => (a, i)))
            foreach (var x in a.Arguments) CheckRefs(x, $"actions[{i}].arguments");

        ValidatePrerequisites(p, d);
    }

    private static void ValidatePrerequisites(SetupProject p, DiagnosticBag d)
    {
        var vc = p.Requirements.VcRedist;
        if (vc.Required && vc.MinVersion.Length > 0 && !ProjectValidator.IsValidVersion(vc.MinVersion)) d.Error("FS1670", "requirements.vcRedist.minVersion", "Not a valid version, e.g. 14.40.");
        if (vc.Architecture is not ("" or "x64" or "x86" or "arm64")) d.Error("FS1671", "requirements.vcRedist.architecture", "Use x64, x86, arm64 or leave it empty (the package architecture).");
        if (p.Requirements.Custom.Count > 8) d.Error("FS1672", "requirements.custom", "At most 8 custom prerequisites.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (c, i) in p.Requirements.Custom.Select((c, i) => (c, i)))
        {
            var f = $"requirements.custom[{i}]";
            if (!IdRegex().IsMatch(c.Id)) d.Error("FS1673", f + ".id", "Letters, digits, '-' and '_' (max 32).");
            else if (!ids.Add(c.Id)) d.Error("FS1674", f + ".id", $"Duplicate id '{c.Id}'.");
            if (c.Name.Length is 0 or > 128 || HasControl(c.Name)) d.Error("FS1675", f + ".name", "A name of 1-128 characters shown to the user.");
            var reg = c.Detect.Registry;
            var file = c.Detect.File;
            if ((reg is null) == (file is null)) d.Error("FS1676", f + ".detect", "Detect the prerequisite either through a registry value (detect.registry) or a file (detect.file).");
            if (reg is not null && (reg.Hive is not ("HKLM" or "HKCU") || string.IsNullOrWhiteSpace(reg.Key) || reg.Key.Length > 255 || reg.Key.StartsWith('\\') ||
                                    (reg.Value?.Length ?? 0) > 128 || (reg.Version.Length > 0 && !ProjectValidator.IsValidVersion(reg.Version))))
                d.Error("FS1677", f + ".detect.registry", "Needs hive HKLM|HKCU, a key, a value name (empty = the key itself when no version is given) and optionally a minimum version.");
            if (file is not null)
            {
                var root = SystemRoots.FirstOrDefault(r => file.Path.StartsWith(r, StringComparison.Ordinal));
                if (root is null || !PathRules.TryNormalizeRelative(file.Path[root.Length..], out _, out _))
                    d.Error("FS1678", f + ".detect.file.path", @"{ProgramFiles}\X, {ProgramFilesX86}\X, {System}\X, {CommonAppData}\X, {LocalAppData}\X or {AppData}\X.");
                if (file.Version.Length > 0 && !ProjectValidator.IsValidVersion(file.Version)) d.Error("FS1679", f + ".detect.file.version", "Not a valid version.");
            }
            if (c.Auto.Length > 0) d.Error("FS1681", f + ".auto", "Official installers exist only for the built-in prerequisites; give an installer or a download.");
        }
        foreach (var (s, field, kind, version, _, _) in Prerequisites.OfficialInstallers.Sources(p))
        {
            if (s.Auto is not ("" or "bundle" or "download")) { d.Error("FS1680", field + ".auto", "Use bundle, download or leave it empty."); continue; }
            if (s.Auto.Length == 0) continue;
            if (kind is Prerequisites.PrerequisiteKind.DotNet or Prerequisites.PrerequisiteKind.WindowsAppRuntime && Prerequisites.OfficialInstallers.Channel(version) is null)
                d.Error("FS1680", field + ".auto", "The official installer is chosen by the version; set the version first.");
            if (!string.IsNullOrEmpty(s.Installer)) d.Info("FS1682", field, "Both an installer among the app's files and an official installer are set; the app's installer is used.");
            else if (s.Download is { Url.Length: > 0 } && s.Auto == "download") d.Info("FS1682", field, "The download URL is used instead of Microsoft's official installer.");
        }
    }

    /// <summary>Checks that every file the 1.2 sections name is among the files to install.</summary>
    public static void ValidateAgainstFiles(SetupProject p, HashSet<string> files, DiagnosticBag d)
    {
        void Check(string path, string field, string code)
        {
            if (path.Length > 0 && PathRules.TryNormalizeRelative(path, out var rel, out _) && !files.Contains(rel))
                d.Error(code, field, $"'{rel}' is not among the files to install.");
        }
        foreach (var (s, i) in p.Shortcuts.Select((s, i) => (s, i))) { Check(s.Target, $"shortcuts[{i}].target", "FS1913"); Check(s.Icon, $"shortcuts[{i}].icon", "FS1914"); }
        foreach (var (a, i) in p.Associations.Select((a, i) => (a, i))) { Check(a.Target, $"associations[{i}].target", "FS1931"); Check(a.Icon, $"associations[{i}].icon", "FS1931"); }
        foreach (var (pr, i) in p.Protocols.Select((x, i) => (x, i))) { Check(pr.Target, $"protocols[{i}].target", "FS1937"); Check(pr.Icon, $"protocols[{i}].icon", "FS1937"); }
        foreach (var (s, i) in p.Services.Select((s, i) => (s, i))) Check(s.Run, $"services[{i}].run", "FS1969");
        foreach (var (r, i) in p.Firewall.Select((r, i) => (r, i))) Check(r.Program, $"firewall[{i}].program", "FS1979");
    }
}
