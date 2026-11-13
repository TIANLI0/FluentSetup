using System.Text;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Import;

/// <summary>
/// State shared by the Inno Setup and NSIS readers and the common mapping step. Paths and values found in a script are
/// first written with neutral tokens ("canonical" text), so both formats share the mapping rules:
/// <c>{InstallDir}</c>, <c>{ProgramFiles}</c>, <c>{LocalAppData}</c>, <c>{AppData}</c>, <c>{CommonAppData}</c>,
/// <c>{Programs}</c> (Start menu programs), <c>{Desktop}</c>, <c>{Startup}</c>, <c>{Temp}</c>, <c>{System}</c>,
/// <c>{Windows}</c>, <c>{SourceDir}</c> (where Setup.exe is), <c>{Uninstaller}</c>, <c>{OldData}</c>; anything else is
/// <c>{?original}</c> (not expressible). Script variables appear as <c>$(Name)</c>, as in the project file.
/// </summary>
internal sealed partial class ImportContext
{
    public ImportContext(string scriptPath, string outputDirectory)
    {
        ScriptPath = Path.GetFullPath(scriptPath);
        OutputDirectory = Path.GetFullPath(outputDirectory);
        Project = new SetupProject();
        // Start from "nothing found": the readers turn on what the script has.
        Project.Install.DesktopShortcut = "off";
        Project.Install.StartMenuShortcut = "off";
        Project.Install.LaunchAfterInstall = "off";
    }

    public string ScriptPath { get; }
    public string ScriptDirectory => Path.GetDirectoryName(ScriptPath)!;
    public string OutputDirectory { get; }
    public SetupProject Project { get; set; }
    public List<ImportNote> Notes { get; } = [];
    private readonly HashSet<string> _noteKeys = [];

    /// <summary>Script variables (Inno #define, NSIS !define): name → value (null = computed by the script, unknown here).</summary>
    public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where a variable's value comes from when that matters (computed by the script, read from a file); reported if the project uses it.</summary>
    public Dictionary<string, (SourceRef At, ImportSeverity Severity, string Message)> VariableNotes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FileEntry> Files { get; } = [];
    public List<RegistryWrite> Registry { get; } = [];
    public List<ShortcutEntry> Shortcuts { get; } = [];
    public List<OptionCandidate> Options { get; } = [];
    public List<(SourceRef At, string Path)> EmptyDirectories { get; } = [];

    /// <summary>Relative path ('/' separated) of the app's main program among the installed files; "" = unknown.</summary>
    public string MainExecutable { get; set; } = "";

    public void Info(SourceRef at, string message, string field = "") => Add(ImportSeverity.Info, at, message, field);
    public void Warn(SourceRef at, string message, string field = "") => Add(ImportSeverity.Warning, at, message, field);
    public void Manual(SourceRef at, string message, string field = "") => Add(ImportSeverity.Manual, at, message, field);
    public SourceRef Script => new(ScriptPath, 0);

    private void Add(ImportSeverity severity, SourceRef at, string message, string field)
    {
        if (_noteKeys.Add($"{severity}|{at.File}|{at.Line}|{message}")) Notes.Add(new ImportNote(severity, at.File, at.Line, message, field));
    }

    // ---- variables ---------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\$\$\(|\$\(([^)]*)\)")]
    private static partial Regex PlaceholderRegex();

    /// <summary>The text with every $(Name) replaced by the variable's value (unknown values become "").</summary>
    public string Resolve(string raw) =>
        raw.Contains('$') ? PlaceholderRegex().Replace(raw, m => m.Value == "$$(" ? "$(" : Variables.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "") : raw;

    /// <summary>True if the text refers to a variable whose value the script computes (e.g. from a file's version).</summary>
    public bool HasUnknownVariable(string raw) =>
        raw.Contains("$(", StringComparison.Ordinal) &&
        PlaceholderRegex().Matches(raw).Any(m => m.Value != "$$(" && (!Variables.TryGetValue(m.Groups[1].Value, out var v) || v is null));

    /// <summary>Literal text for a project string: "$(" must be written "$$(".</summary>
    public static string Literal(string text) => text.Replace("$(", "$$(", StringComparison.Ordinal);

    /// <summary>The variables a project string refers to.</summary>
    public static IEnumerable<string> ReferencedVariables(string raw) =>
        PlaceholderRegex().Matches(raw).Where(m => m.Value != "$$(").Select(m => m.Groups[1].Value);

    // ---- options -----------------------------------------------------------------------------------------------

    public OptionCandidate? FindOption(string id) => Options.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Marks a candidate option as used (it goes into the project) and returns its id.</summary>
    public string UseOption(string id)
    {
        if (id.Length == 0) return "";
        var o = FindOption(id);
        if (o is null) return "";
        o.Used = true;
        return o.Id;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_-]+")]
    private static partial Regex NonIdRegex();

    /// <summary>A unique option id (letters, digits, - and _, max 32) for a task, component or section name.</summary>
    public string NewOptionId(string name)
    {
        var id = NonIdRegex().Replace(name.Trim(), "-").Trim('-');
        if (id.Length == 0) id = "option";
        if (id.Length > 32) id = id[..32].TrimEnd('-');
        var unique = id;
        for (var i = 2; FindOption(unique) is not null; i++) unique = $"{(id.Length > 28 ? id[..28] : id)}-{i}";
        return unique;
    }

    // ---- paths -------------------------------------------------------------------------------------------------

    /// <summary>A path from the script (relative to the script's folder, '\' or '/') as a full path.</summary>
    public string ScriptFile(string path)
    {
        var p = path.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(ScriptDirectory, p));
    }

    /// <summary>A full path as the project writes it: relative to the project folder, '/' separated.</summary>
    public string ProjectPath(string fullPath)
    {
        var rel = Path.GetRelativePath(OutputDirectory, fullPath);
        return Path.IsPathRooted(rel) ? rel : rel.Replace('\\', '/');
    }

    /// <summary>
    /// A path written in the script (with $(Name) for its variables) as the project writes it: relative to the project
    /// folder, with the variables put back, so "dist\App-$(Version)" keeps following -D Version=… .
    /// </summary>
    public string ProjectPathOf(string raw) => WithVariables(ProjectPath(ScriptFile(Resolve(raw))), ReferencedVariables(raw));

    /// <summary>Puts $(Name) back where a variable's value appears in a path (longest values first; values of 2+ characters).</summary>
    public string WithVariables(string path, IEnumerable<string> names)
    {
        foreach (var (name, value) in names.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Select(n => (Name: n, Value: Variables.TryGetValue(n, out var v) ? v : null))
                     // Values holding a path are relative to the script, not to the project folder: left as they are.
                     .Where(x => x.Value is { Length: >= 2 } && Model.ProjectVariables.IsValidName(x.Name) && x.Value.IndexOfAny(['$', '\\', '/']) < 0)
                     .OrderByDescending(x => x.Value!.Length))
        {
            var forward = value!.Replace('\\', '/');
            path = path.Replace(forward, $"$({name})", StringComparison.Ordinal);
        }
        return path;
    }

    /// <summary>
    /// Canonical text that names a path inside the install folder ("{InstallDir}" or "{InstallDir}\x") as a relative path
    /// ('/' separated, "" = the folder itself). Variables are resolved.
    /// </summary>
    public bool TryInstallRelative(string canonical, out string relative)
    {
        relative = "";
        var s = Resolve(canonical).Trim().Trim('"');
        const string token = "{InstallDir}";
        if (!s.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = s[token.Length..].Replace('/', '\\').Trim('\\');
        if (rest.Length == 0) return true;
        if (Canonical.IsUnsupported(rest) || !PathRules.TryNormalizeRelative(rest, out var norm, out _)) return false;
        relative = norm.Replace('\\', '/');
        return true;
    }

    /// <summary>Same file? (relative install paths, case-insensitive, either separator)</summary>
    public static bool SamePath(string a, string b) => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes Windows accelerator markers: "&amp;Desktop" → "Desktop", "&amp;&amp;" → "&amp;".</summary>
    public static string StripAccelerators(string label)
    {
        if (!label.Contains('&')) return label;
        var sb = new StringBuilder(label.Length);
        for (var i = 0; i < label.Length; i++)
        {
            if (label[i] != '&') { sb.Append(label[i]); continue; }
            if (i + 1 < label.Length && label[i + 1] == '&') { sb.Append('&'); i++; }
        }
        return sb.ToString();
    }
}

/// <summary>Helpers for canonical text (see <see cref="ImportContext"/>).</summary>
internal static partial class Canonical
{
    [GeneratedRegex(@"\{\?([^}]*)\}")]
    private static partial Regex UnsupportedRegex();

    public static bool IsUnsupported(string s) => s.Contains("{?", StringComparison.Ordinal);

    /// <summary>The text without the constants FluentSetup cannot fill in.</summary>
    public static string Strip(string s) => IsUnsupported(s) ? UnsupportedRegex().Replace(s, "") : s;

    /// <summary>The original spelling of the first constant or variable that has no FluentSetup equivalent.</summary>
    public static string FirstUnsupported(string s)
    {
        var m = UnsupportedRegex().Match(s);
        if (!m.Success) return "";
        return Original(m.Groups[1].Value);
    }

    /// <summary>"$R0" (NSIS variable), "def:NAME" (NSIS define) → "${NAME}", "lang:NAME" (NSIS LangString) → "$(NAME)", "code:X" (Inno constant) → "{code:X}".</summary>
    private static string Original(string inner) =>
        inner.StartsWith('$') ? inner :
        inner.StartsWith("def:", StringComparison.Ordinal) ? "${" + inner[4..] + "}" :
        inner.StartsWith("lang:", StringComparison.Ordinal) ? "$(" + inner[5..] + ")" : "{" + inner + "}";

    /// <summary>The text as the script wrote it, for notes ("{?code:X}" → "{code:X}").</summary>
    public static string Display(string s) => UnsupportedRegex().Replace(s, m => Original(m.Groups[1].Value));

    /// <summary>
    /// Canonical text as a registry, environment or config value: {InstallDir} is a FluentSetup placeholder; folders the
    /// installer does not expand there ({LocalAppData}, {Temp} …) and unknown constants make it unusable (null).
    /// </summary>
    public static string? AsValue(string s)
    {
        if (IsUnsupported(s)) return null;
        foreach (var token in new[] { "{ProgramFiles}", "{LocalAppData}", "{AppData}", "{CommonAppData}", "{Programs}", "{Desktop}", "{Startup}", "{Temp}", "{System}", "{Windows}", "{SourceDir}", "{Uninstaller}", "{OldData}" })
        {
            if (s.Contains(token, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return s;
    }

    /// <summary>"{LocalAppData}\X", "{AppData}\X", "{CommonAppData}\X" or "{InstallDir}\X" as a data template (uninstall.removeData).</summary>
    public static string? AsDataTemplate(string resolved)
    {
        var s = resolved.Replace('/', '\\').TrimEnd('\\');
        foreach (var root in new[] { "{LocalAppData}", "{AppData}", "{CommonAppData}", "{InstallDir}" })
        {
            if (!s.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = s[(root.Length + 1)..];
            if (IsUnsupported(rest) || rest.IndexOfAny(['*', '?']) >= 0 || !PathRules.TryNormalizeRelative(rest, out var norm, out _)) return null;
            if (root == "{InstallDir}" && norm.Split('\\')[0].Equals(".fsetup", StringComparison.OrdinalIgnoreCase)) return null;
            return root + "\\" + norm;
        }
        return null;
    }
}

internal enum RegistryUninstall { Keep, Values, Key, KeyIfEmpty }

/// <summary>A registry write of the script (Inno [Registry], NSIS WriteReg*).</summary>
internal sealed class RegistryWrite
{
    public SourceRef At { get; init; }
    /// <summary>HKA (the install scope's root: Inno HKA, NSIS SHCTX), HKCU, HKLM or another root name.</summary>
    public string Root { get; init; } = "HKA";
    /// <summary>Key below the root, '\' separated, without leading or trailing '\'. HKCR keys are written as Software\Classes\….</summary>
    public string Key { get; init; } = "";
    /// <summary>Value name ("" = default value); null = the key only (no value).</summary>
    public string? Name { get; init; }
    /// <summary>string | expandString | dword | qword | multiString | binary</summary>
    public string Type { get; init; } = "string";
    /// <summary>Canonical text.</summary>
    public string Data { get; init; } = "";
    public RegistryUninstall Uninstall { get; init; } = RegistryUninstall.Keep;
    /// <summary>Option candidate the write depends on ("" = always).</summary>
    public string IfOption { get; init; } = "";
    /// <summary>The script writes it only under a condition the import cannot express (Check:, ${If} …).</summary>
    public bool Conditional { get; init; }
    public bool Consumed { get; set; }
}

/// <summary>A shortcut the script creates (Inno [Icons], NSIS CreateShortCut).</summary>
internal sealed class ShortcutEntry
{
    public SourceRef At { get; init; }
    /// <summary>startMenu | desktop | startup | quickLaunch | other</summary>
    public string Location { get; init; } = "startMenu";
    /// <summary>Start menu sub-folder ('\' separated, "" = Programs itself).</summary>
    public string Folder { get; init; } = "";
    /// <summary>Name without .lnk (may hold $(Name)).</summary>
    public string Name { get; init; } = "";
    public string Target { get; set; } = "";
    /// <summary>Command-line text.</summary>
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Icon { get; init; } = "";
    public int IconIndex { get; init; }
    public string Description { get; init; } = "";
    public string IfOption { get; init; } = "";
    public string AppUserModelId { get; init; } = "";
    public string ToastActivatorClsid { get; init; } = "";
    /// <summary>Where it would go but cannot (e.g. "{?userfavorites}"), for the note.</summary>
    public string Unsupported { get; init; } = "";
}

/// <summary>A file or wildcard the script installs (Inno [Files], NSIS File).</summary>
internal sealed class FileEntry
{
    public SourceRef At { get; init; }
    /// <summary>Full path; the last segment may hold * and ?.</summary>
    public string Source { get; init; } = "";
    /// <summary>Relative to the install folder, '/' separated, "" = the install folder itself.</summary>
    public string DestDir { get; init; } = "";
    public bool Recurse { get; init; }
    /// <summary>Installed under another name ("" = same name).</summary>
    public string DestName { get; init; } = "";
    /// <summary>Installed only if missing and kept on uninstall (Inno onlyifdoesntexist/uninsneveruninstall, NSIS SetOverwrite off).</summary>
    public bool Preserve { get; init; }
    /// <summary>Patterns to leave out (Inno Excludes, NSIS /x), relative to the source folder.</summary>
    public List<string> Excludes { get; init; } = [];
    /// <summary>Variables the script used in the source path (put back into files.source / files.extra).</summary>
    public List<string> Variables { get; init; } = [];
}

/// <summary>An Inno task or component, or an NSIS section, that may become one of the project's options.</summary>
internal sealed class OptionCandidate
{
    public string Id { get; init; } = "";
    public Dictionary<string, string> Label { get; init; } = [];
    public bool Default { get; set; }
    public bool Required { get; set; }
    /// <summary>Value in quiet first installs (Inno skipifsilent → false); null = <see cref="Default"/>.</summary>
    public bool? QuietDefault { get; init; }
    public SourceRef At { get; init; }
    /// <summary>"task 'desktopicon'", "section 'Desktop shortcut'" …</summary>
    public string Origin { get; init; } = "";
    public bool Used { get; set; }
    /// <summary>Not an option of its own but the checkbox of one of the app's shortcuts (install.desktopShortcut …).</summary>
    public string AbsorbedInto { get; set; } = "";
    public RunValueSettings? Startup { get; set; }
    /// <summary>Things the script ties to it that an option cannot control (files, [Code] …).</summary>
    public List<string> Unexpressed { get; } = [];
}
