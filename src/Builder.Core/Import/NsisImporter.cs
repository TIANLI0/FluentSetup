using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Import;

/// <summary>
/// Reads an NSIS script (.nsi/.nsh) into the import context. NSIS scripts are programs; the importer follows the common
/// Modern UI 2 shape (attributes, sections with File/CreateShortCut/WriteReg*/ExecWait, an "Uninstall" section) and
/// reports everything else: functions, jumps, plug-ins and conditions are not evaluated.
/// </summary>
internal sealed partial class NsisImporter(ImportContext ctx)
{
    private sealed record Line(SourceRef At, List<string> Tokens)
    {
        public string Command => Tokens.Count > 0 ? Tokens[0] : "";
        public string Arg(int i) => i + 1 < Tokens.Count ? Tokens[i + 1] : "";
        public int ArgCount => Tokens.Count - 1;
    }

    private sealed class Block
    {
        public string Kind = "";      // section | function
        public string Name = "";
        public SourceRef At;
        public List<Line> Lines = [];
        public bool Unselected;       // Section /o
        public string Index = "";     // Section index output
        public string Group = "";     // enclosing SectionGroup
        public bool Uninstall => Kind == "section" ? Name.StartsWith("un.", StringComparison.OrdinalIgnoreCase) || Name.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)
                                                   : Name.StartsWith("un.", StringComparison.OrdinalIgnoreCase);
        public bool Hidden => Name.Length == 0 || Name.TrimStart('!').StartsWith('-') || Name.Equals("Uninstall", StringComparison.OrdinalIgnoreCase) || Name.Equals("un.", StringComparison.OrdinalIgnoreCase);
        public bool ReadOnly => Lines.Any(l => l.Command.Equals("SectionIn", StringComparison.OrdinalIgnoreCase) && l.Tokens.Skip(1).Any(t => t.Equals("RO", StringComparison.OrdinalIgnoreCase)));
        public string Candidate = "";  // option candidate id ("" = unconditional)
    }

    private readonly Dictionary<string, (List<string> Params, List<(SourceRef At, string Text)> Body)> _macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _langStrings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Tag, string Name, SourceRef At)> _languages = [];
    private readonly HashSet<string> _userVars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _varValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Line> _top = [];
    private readonly List<Block> _blocks = [];
    private readonly HashSet<string> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Value, SourceRef At)> _attrs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Value, SourceRef At)> _versionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pageFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, SourceRef At)> _uninstallKeys = [];
    private string _uninstallerName = "";
    private int _files;
    private int _lines;
    private bool _shellAll;
    private bool _resolvingName;
    private readonly Dictionary<string, string> _rawDefines = new(StringComparer.OrdinalIgnoreCase);
    private string _outDir = "{InstallDir}";

    private static readonly HashSet<string> StandardHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "MUI.nsh", "MUI2.nsh", "LogicLib.nsh", "x64.nsh", "FileFunc.nsh", "WinVer.nsh", "Sections.nsh", "nsDialogs.nsh", "StrFunc.nsh", "WordFunc.nsh",
        "Library.nsh", "WinMessages.nsh", "TextFunc.nsh", "MultiUser.nsh", "UpgradeDLL.nsh", "InstallOptions.nsh", "Memento.nsh", "Util.nsh", "VB6RunTime.nsh",
        "Integration.nsh", "VPatchLib.nsh", "Win\\COM.nsh", "Win\\WinDef.nsh", "Win\\WinError.nsh", "Win\\WinNT.nsh", "Win\\Propkey.nsh", "Win\\RestartManager.nsh",
        "nsProcess.nsh", "UAC.nsh", "ModernXL.nsh", "MUIEx.nsh",
    };

    private static readonly HashSet<string> CosmeticAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BrandingText", "Caption", "ShowInstDetails", "ShowUninstDetails", "XPStyle", "ManifestDPIAware", "ManifestDPIAwareness", "ManifestSupportedOS",
        "ManifestLongPathAware", "ManifestDisableWindowFiltering", "ManifestGdiScaling", "Unicode", "CRCCheck", "SetCompressor", "SetCompressorDictSize",
        "SetDatablockOptimize", "SetCompress", "FileBufSize", "AllowSkipFiles", "AutoCloseWindow", "InstProgressFlags", "WindowIcon", "BGGradient", "BGFont",
        "ChangeUI", "MiscButtonText", "InstallButtonText", "SpaceTexts", "DirText", "ComponentText", "LicenseText", "LicenseBkColor", "LicenseForceSelection",
        "CompletedText", "DetailsButtonText", "UninstallText", "UninstallCaption", "UninstallButtonText", "UninstallSubCaption", "SubCaption", "AddBrandingImage",
        "SetFont", "CheckBitmap", "InstallColors", "FileErrorText", "PEAddResource", "PERemoveResource", "PEDllCharacteristics", "PESubsysVer", "SetPluginUnload",
        "ReserveFile", "DirVar", "DirVerify", "PageCallbacks", "UninstallIcon", "VIFileVersion", "AllowRootDirInstall", "SetOverwrite", "Var", "InstType",
        "SilentUnInstall", "SetDateSave", "UninstPage", "Target", "CPU",
    };

    public void Run()
    {
        Process(ctx.ScriptPath, 0, new Stack<Frame>());
        Structure();
        ReadAttributes();
        DecideScope();
        CreateOptionCandidates();
        // Install sections first: the uninstall section's cleanup is matched against what they install and write.
        foreach (var b in _blocks.Where(b => b.Kind == "section" && !b.Uninstall)) ExecuteSection(b);
        foreach (var b in _blocks.Where(b => b.Kind == "section" && b.Uninstall)) ExecuteSection(b);
        // A shortcut to the uninstaller the script writes (WriteUninstaller) is FluentSetup's uninstall.exe.
        foreach (var s in ctx.Shortcuts.Where(s => _uninstallerName.Length > 0 && ctx.TryInstallRelative(s.Target, out var t) && t.Equals(_uninstallerName, StringComparison.OrdinalIgnoreCase)))
            s.Target = "{Uninstaller}";
        ApplyIdentity();
        ApplyFolders();
        FindMainExecutable();
        ApplyPrograms();
        ApplyFinishPage();
        ReportFunctions();
    }

    // ---- preprocessing ---------------------------------------------------------------------------------------

    private sealed class Frame
    {
        public bool Parent;
        public bool Active;
        public bool Taken;
    }

    private static bool Active(Stack<Frame> s) => s.Count == 0 || (s.Peek().Parent && s.Peek().Active);

    /// <summary>Reads a file: logical lines (continuations joined, comments removed), compile-time commands applied, macros expanded.</summary>
    private void Process(string file, int depth, Stack<Frame> stack)
    {
        if (++_files > ScriptText.MaxFiles) { ctx.Warn(new SourceRef(file, 0), $"More than {ScriptText.MaxFiles} files are included; the rest is not read."); return; }
        var lines = ScriptText.ReadLines(file, out var legacy);
        if (legacy) ctx.Info(new SourceRef(file, 0), $"{Path.GetFileName(file)} is not UTF-8; it was read as Windows-1252. Check non-English text in the project.");
        var logical = new List<(SourceRef At, string Text)>();
        var inComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var at = new SourceRef(file, i + 1);
            var text = lines[i];
            while (text.TrimEnd().EndsWith('\\') && i + 1 < lines.Length) text = text.TrimEnd()[..^1] + " " + lines[++i].TrimStart();
            text = StripBlockComments(text, ref inComment);
            if (text.Trim().Length > 0) logical.Add((at, text));
        }
        Run(logical, depth, stack, 0);
    }

    private static string StripBlockComments(string text, ref bool inComment)
    {
        var sb = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            if (inComment)
            {
                if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { inComment = false; i++; }
                continue;
            }
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; sb.Append(c); continue; }
            if (c is '"' or '\'' or '`') { quote = c; sb.Append(c); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { inComment = true; i++; continue; }
            if ((c is ';' or '#') && (i == 0 || char.IsWhiteSpace(text[i - 1]))) break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Splits a logical line into tokens; quotes (" ' `) group and are removed, $\" stays an escape for <see cref="Text"/>.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            var c = text[i];
            var sb = new StringBuilder();
            if (c is '"' or '\'' or '`')
            {
                i++;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '$' && i + 2 < text.Length && text[i + 1] == '\\' && text[i + 2] is '"' or '\'' or '`') { sb.Append("$\\").Append(text[i + 2]); i += 3; continue; }
                    sb.Append(text[i++]);
                }
                i++;
                // A quoted token can continue without a space ("abc"def is unusual; keep it simple).
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i])) sb.Append(text[i++]);
            }
            tokens.Add(sb.ToString());
        }
        return tokens;
    }

    private void Run(List<(SourceRef At, string Text)> lines, int depth, Stack<Frame> stack, int macroDepth)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var (at, text) = lines[i];
            if (++_lines > ScriptText.MaxLines) { ctx.Warn(at, $"The script has more than {ScriptText.MaxLines} lines; the rest is not read."); return; }
            var tokens = Tokenize(text);
            if (tokens.Count == 0) continue;
            var cmd = tokens[0];
            if (!cmd.StartsWith('!'))
            {
                if (Active(stack)) _rawLines.Add(new Line(at, tokens));
                continue;
            }
            var name = cmd[1..].ToLowerInvariant();
            switch (name)
            {
                case "ifdef" or "ifndef" or "if" or "ifmacrodef" or "ifmacrondef":
                {
                    var parent = Active(stack);
                    var value = parent && Condition(name, tokens.Skip(1).ToList(), at);
                    stack.Push(new Frame { Parent = parent, Active = value, Taken = value });
                    continue;
                }
                case "else":
                {
                    if (stack.Count == 0) { ctx.Warn(at, "!else without !if; ignored."); continue; }
                    var f = stack.Peek();
                    if (tokens.Count > 1)
                    {
                        var kind = tokens[1].TrimStart('!').ToLowerInvariant();
                        f.Active = !f.Taken && f.Parent && Condition(kind, tokens.Skip(2).ToList(), at);
                    }
                    else f.Active = !f.Taken;
                    f.Taken |= f.Active;
                    continue;
                }
                case "endif":
                    if (stack.Count == 0) ctx.Warn(at, "!endif without !if; ignored.");
                    else stack.Pop();
                    continue;
                case "macro":
                {
                    var body = new List<(SourceRef, string)>();
                    var nest = 0;
                    for (i++; i < lines.Count; i++)
                    {
                        var t = lines[i].Text.TrimStart();
                        if (t.StartsWith("!macroend", StringComparison.OrdinalIgnoreCase)) { if (nest-- == 0) break; }
                        else if (t.StartsWith("!macro ", StringComparison.OrdinalIgnoreCase)) nest++;
                        body.Add(lines[i]);
                    }
                    if (Active(stack) && tokens.Count > 1) _macros[tokens[1]] = (tokens.Skip(2).ToList(), body);
                    continue;
                }
            }
            if (!Active(stack)) continue;
            switch (name)
            {
                case "define": Define(tokens, at); break;
                case "undef": if (tokens.Count > 1) ctx.Variables.Remove(tokens[1]); break;
                case "include": Include(tokens, at, depth, stack); break;
                case "insertmacro": InsertMacro(tokens, at, depth, stack, macroDepth); break;
                case "addincludedir" or "addplugindir": ctx.Info(at, $"{cmd} is not needed by the import."); break;
                case "error": ctx.Warn(at, $"The script stops here when compiled: {string.Join(' ', tokens.Skip(1))}"); break;
                case "warning" or "echo" or "verbose" or "pragma" or "tempfile" or "delfile" or "appendfile" or "searchreplace" or "gettlbversion": break;
                case "getdllversion": GetDllVersion(tokens, at); break;
                case "searchparse":
                    foreach (var v in tokens.Skip(1).Where(t => !t.StartsWith('/')).Skip(2).Where((_, idx) => idx % 2 == 1)) ctx.Variables[v] = null;
                    ctx.Info(at, "!searchparse reads values when the script is compiled; variables it defines are reported where used.");
                    break;
                case "system" or "execute" or "makensis":
                    ctx.Manual(at, $"{cmd} {string.Join(' ', tokens.Skip(1))} runs a command when the script is compiled; do that in your build before 'fluentsetup build'.");
                    break;
                case "packhdr":
                    ctx.Info(at, "!packhdr (header compression) is not needed.");
                    break;
                case "finalize" or "uninstfinalize":
                    ctx.Manual(at, $"{cmd} signs or post-processes the installer; set up code signing in the project's signing section (signing.signUninstaller covers uninstall.exe).", "signing");
                    break;
                case "cd":
                    ctx.Warn(at, "!cd changes the folder relative paths start from; the importer keeps using the script's folder.");
                    break;
                default:
                    ctx.Info(at, $"Compile-time command {cmd} is ignored.");
                    break;
            }
        }
    }

    private readonly List<Line> _rawLines = [];

    private bool Condition(string kind, List<string> args, SourceRef at)
    {
        switch (kind)
        {
            case "ifdef" or "ifndef":
            {
                // NAME [&& | || NAME …]
                bool? result = null;
                var op = "";
                foreach (var t in args)
                {
                    if (t is "&&" or "||") { op = t; continue; }
                    var v = ctx.Variables.ContainsKey(t);
                    result = result is null ? v : op == "||" ? result.Value || v : result.Value && v;
                }
                var defined = result ?? false;
                return kind == "ifdef" ? defined : !defined;
            }
            case "ifmacrodef": return args.Count > 0 && _macros.ContainsKey(args[0]);
            case "ifmacrondef": return args.Count == 0 || !_macros.ContainsKey(args[0]);
            default:
            {
                if (args.Count == 2 && args[0].Equals("/FileExists", StringComparison.OrdinalIgnoreCase))
                {
                    if (Expand(args[1]) is not { } file) { ctx.Warn(at, $"!if /FileExists {args[1]}: the path uses values unknown here; the first branch is imported."); return true; }
                    return File.Exists(ctx.ScriptFile(file)) || Directory.Exists(ctx.ScriptFile(file));
                }
                var values = args.Select(a => Expand(a)).ToList();
                if (values.Any(v => v is null))
                {
                    ctx.Warn(at, $"!if {string.Join(' ', args)}: the condition uses values unknown here; the first branch is imported.");
                    return true;
                }
                var v2 = values.Select(v => v!).ToList();
                if (v2.Count == 1) return v2[0].Length > 0 && v2[0] != "0";
                if (v2.Count == 2 && v2[0] == "!") return v2[1].Length == 0 || v2[1] == "0";
                if (v2.Count == 3)
                {
                    var (a, op, b) = (v2[0], v2[1], v2[2]);
                    var numeric = long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var na) & long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nb);
                    switch (op)
                    {
                        case "==": return numeric ? na == nb : a == b;
                        case "!=": return numeric ? na != nb : a != b;
                        case "<": if (numeric) return na < nb; break;
                        case ">": if (numeric) return na > nb; break;
                        case "<=": if (numeric) return na <= nb; break;
                        case ">=": if (numeric) return na >= nb; break;
                        case "&&": return a is not ("" or "0") && b is not ("" or "0");
                        case "||": return a is not ("" or "0") || b is not ("" or "0");
                    }
                }
                ctx.Warn(at, $"!if {string.Join(' ', args)}: the condition cannot be evaluated here; the first branch is imported.");
                return true;
            }
        }
    }

    /// <summary>A token with ${defines} replaced (compile-time text); null if it uses an unknown define.</summary>
    private string? Expand(string token, int depth = 0)
    {
        if (!token.Contains("${", StringComparison.Ordinal) || depth > 16) return token;
        var sb = new StringBuilder();
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] == '$' && i + 1 < token.Length && token[i + 1] == '{')
            {
                var end = token.IndexOf('}', i + 2);
                if (end < 0) { sb.Append(token[i..]); break; }
                var name = token[(i + 2)..end];
                if (!ctx.Variables.TryGetValue(name, out var value) || value is null) return null;
                var inner = Expand(value, depth + 1);
                if (inner is null) return null;
                sb.Append(inner);
                i = end;
                continue;
            }
            sb.Append(token[i]);
        }
        return sb.ToString();
    }

    private void Define(List<string> tokens, SourceRef at)
    {
        var i = 1;
        bool ifndef = false, math = false, date = false, file = false;
        while (i < tokens.Count && tokens[i].StartsWith('/'))
        {
            var o = tokens[i].ToLowerInvariant();
            if (o == "/ifndef") ifndef = true;
            else if (o == "/math") math = true;
            else if (o is "/date" or "/utcdate") date = true;
            else if (o == "/file") file = true;
            i++;
        }
        if (i >= tokens.Count) return;
        var name = tokens[i++];
        if (ifndef && ctx.Variables.ContainsKey(name)) return;
        if (date || file)
        {
            ctx.Variables[name] = null;
            ctx.VariableNotes[name] = (at, ImportSeverity.Manual, $"!define {(date ? "/date" : "/file")} {name} is computed when the script is compiled.");
            return;
        }
        if (math && i + 2 < tokens.Count)
        {
            var a = Expand(tokens[i]);
            var b = Expand(tokens[i + 2]);
            if (long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                long? r = tokens[i + 1] switch { "+" => x + y, "-" => x - y, "*" => x * y, "/" when y != 0 => x / y, "%" when y != 0 => x % y, "&" => x & y, "|" => x | y, "^" => x ^ y, _ => null };
                ctx.Variables[name] = r?.ToString(CultureInfo.InvariantCulture);
            }
            else ctx.Variables[name] = null;
            return;
        }
        var value = i < tokens.Count ? Expand(tokens[i]) : "";
        ctx.Variables[name] = value;
        if (i < tokens.Count && tokens[i].Contains("${", StringComparison.Ordinal)) _rawDefines[name] = tokens[i];
        else _rawDefines.Remove(name);
        if (value is null) ctx.VariableNotes[name] = (at, ImportSeverity.Manual, $"!define {name} uses a value computed when the script is compiled.");
        else ctx.VariableNotes[name] = (at, ImportSeverity.Info, $"!define {name} → variables.{name} = \"{value}\"");
    }

    private void GetDllVersion(List<string> tokens, SourceRef at)
    {
        var args = tokens.Skip(1).Where(t => !t.StartsWith('/')).ToList();
        if (args.Count < 2) return;
        var file = Expand(args[0]);
        var prefix = args[1];
        System.Diagnostics.FileVersionInfo? info = null;
        if (file is not null && File.Exists(ctx.ScriptFile(file))) info = System.Diagnostics.FileVersionInfo.GetVersionInfo(ctx.ScriptFile(file));
        var parts = info is null ? null : new[] { info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart };
        for (var n = 1; n <= 4; n++)
        {
            var name = prefix + n.ToString(CultureInfo.InvariantCulture);
            ctx.Variables[name] = parts?[n - 1].ToString(CultureInfo.InvariantCulture);
            ctx.VariableNotes[name] = (at, parts is null ? ImportSeverity.Manual : ImportSeverity.Warning,
                $"!getdllversion reads the version of {file ?? args[0]} when the script is compiled{(parts is null ? "" : $" (it is {string.Join('.', parts)} now)")}; pass each release's value with -D.");
        }
    }

    private void Include(List<string> tokens, SourceRef at, int depth, Stack<Frame> stack)
    {
        var args = tokens.Skip(1).ToList();
        var nonFatal = args.RemoveAll(a => a.Equals("/NONFATAL", StringComparison.OrdinalIgnoreCase)) > 0;
        args.RemoveAll(a => a.StartsWith("/CHARSET=", StringComparison.OrdinalIgnoreCase));
        if (args.Count == 0) return;
        var name = Expand(args[0]);
        if (name is null) { ctx.Manual(at, $"!include {args[0]}: the file name uses an unknown define; not read."); return; }
        var path = ScriptText.ResolveInclude(name, at.File, ctx.ScriptDirectory);
        if (path is null)
        {
            if (StandardHeaders.Contains(name) || StandardHeaders.Contains(Path.GetFileName(name))) return;
            if (nonFatal) ctx.Info(at, $"!include /NONFATAL \"{name}\" was not found; skipped.");
            else ctx.Manual(at, $"!include \"{name}\" was not found next to the script; its contents are not imported.");
            return;
        }
        if (depth + 1 > ScriptText.MaxIncludeDepth) { ctx.Warn(at, $"!include {name}: includes nest deeper than {ScriptText.MaxIncludeDepth}; not read."); return; }
        ctx.Info(at, $"!include \"{name}\" read.");
        Process(path, depth + 1, stack);
    }

    private void InsertMacro(List<string> tokens, SourceRef at, int depth, Stack<Frame> stack, int macroDepth)
    {
        if (tokens.Count < 2) return;
        var name = tokens[1];
        var args = tokens.Skip(2).ToList();
        if (_macros.TryGetValue(name, out var macro))
        {
            if (macroDepth >= 8) { ctx.Warn(at, $"!insertmacro {name}: macros nest too deeply; not expanded."); return; }
            var body = macro.Body.Select(b =>
            {
                var text = b.Text;
                for (var p = 0; p < macro.Params.Count; p++)
                    text = text.Replace("${" + macro.Params[p] + "}", p < args.Count ? args[p] : "", StringComparison.Ordinal);
                return (at, text);
            }).ToList();
            Run(body, depth, stack, macroDepth + 1);
            return;
        }
        var upper = name.ToUpperInvariant();
        if (upper.StartsWith("MUI_PAGE_", StringComparison.Ordinal) || upper.StartsWith("MUI_UNPAGE_", StringComparison.Ordinal) || upper.StartsWith("MULTIUSER_PAGE_", StringComparison.Ordinal))
        {
            var un = upper.StartsWith("MUI_UNPAGE_", StringComparison.Ordinal);
            var page = upper[(upper.IndexOf("PAGE_", StringComparison.Ordinal) + 5)..];
            (un ? _unPages : _pages).Add(page);
            if (page == "LICENSE" && args.Count > 0 && !un) _attrs["LicenseData"] = (args[0], at);
            if (page == "STARTMENU" && args.Count > 1) _varValues[args[1].TrimStart('$')] = Expand("${MUI_STARTMENUPAGE_DEFAULTFOLDER}") ?? "";
            if (page == "STARTMENU") ctx.Info(at, "Start menu folder page: FluentSetup uses a fixed folder (install.startMenuFolder).", "install.startMenuFolder");
            foreach (var key in new[] { "MUI_PAGE_CUSTOMFUNCTION_PRE", "MUI_PAGE_CUSTOMFUNCTION_SHOW", "MUI_PAGE_CUSTOMFUNCTION_LEAVE" })
                if (ctx.Variables.TryGetValue(key, out var fn) && fn is { Length: > 0 }) { _pageFunctions.Add(fn); ctx.Variables.Remove(key); }
            return;
        }
        switch (upper)
        {
            case "MUI_LANGUAGE" or "MUI_LANGUAGEEX":
            {
                var lang = args.Count > 0 ? args[^1] : "English";
                var tag = ScriptLanguages.Tag(lang);
                _languages.Add((tag, lang, at));
                return;
            }
            case "MUI_RESERVEFILE_LANGDLL" or "MUI_LANGDLL_DISPLAY" or "MUI_UNGETLANGUAGE" or "MUI_STARTMENU_WRITE_BEGIN" or "MUI_STARTMENU_WRITE_END" or
                 "MUI_FUNCTION_DESCRIPTION_BEGIN" or "MUI_FUNCTION_DESCRIPTION_END" or "MUI_DESCRIPTION_TEXT" or "MUI_HEADER_TEXT" or "MUI_INSTALLOPTIONS_EXTRACT" or
                 "MUI_INSTALLOPTIONS_DISPLAY" or "MUI_UNFUNCTION_DESCRIPTION_BEGIN" or "MUI_UNFUNCTION_DESCRIPTION_END" or "MULTIUSER_INIT" or "MULTIUSER_UNINIT" or
                 "MUI_PAGE_FUNCTION_FULLWINDOW" or "MUI_RESERVEFILE_INSTALLOPTIONS":
                if (upper is "MUI_INSTALLOPTIONS_EXTRACT" or "MUI_INSTALLOPTIONS_DISPLAY") ctx.Manual(at, $"{name}: custom InstallOptions pages are not imported; values the user enters can be inputs[].", "inputs");
                return;
            default:
                if (Active(stack)) _rawLines.Add(new Line(at, ["!insertmacro", .. tokens.Skip(1)]));
                return;
        }
    }

    // ---- structure ------------------------------------------------------------------------------------------

    private void Structure()
    {
        Block? current = null;
        var group = "";
        foreach (var line in _rawLines)
        {
            var cmd = line.Command;
            if (cmd.Equals("Section", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) ctx.Warn(line.At, $"Section starts inside {current.Kind} \"{current.Name}\"; the previous one is closed.");
                var args = line.Tokens.Skip(1).ToList();
                var unselected = args.RemoveAll(a => a.Equals("/o", StringComparison.OrdinalIgnoreCase)) > 0;
                current = new Block { Kind = "section", At = line.At, Unselected = unselected, Name = args.Count > 0 ? args[0] : "", Index = args.Count > 1 ? args[1] : "", Group = group };
                _blocks.Add(current);
                continue;
            }
            if (cmd.Equals("Function", StringComparison.OrdinalIgnoreCase))
            {
                current = new Block { Kind = "function", At = line.At, Name = line.Arg(0) };
                _blocks.Add(current);
                continue;
            }
            if (cmd.Equals("SectionEnd", StringComparison.OrdinalIgnoreCase) || cmd.Equals("FunctionEnd", StringComparison.OrdinalIgnoreCase)) { current = null; continue; }
            if (cmd.Equals("SectionGroup", StringComparison.OrdinalIgnoreCase))
            {
                var args = line.Tokens.Skip(1).Where(a => !a.Equals("/e", StringComparison.OrdinalIgnoreCase)).ToList();
                group = args.Count > 0 ? args[0] : "";
                ctx.Info(line.At, $"Section group \"{Plain(group)}\": its sections become separate options.", "options");
                continue;
            }
            if (cmd.Equals("SectionGroupEnd", StringComparison.OrdinalIgnoreCase)) { group = ""; continue; }
            if (current is not null) current.Lines.Add(line);
            else _top.Add(line);
        }
    }

    // ---- text ---------------------------------------------------------------------------------------------------

    /// <summary>The text of a token as canonical text: ${define} → $(define) (or its value), $(LangString) → its text, $VARIABLES → tokens.</summary>
    private string Text(string token, string language = "", int depth = 0)
    {
        var sb = new StringBuilder();
        var literal = new StringBuilder();
        void Flush() { sb.Append(ImportContext.Literal(literal.ToString())); literal.Clear(); }
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (c != '$' || i + 1 >= token.Length) { literal.Append(c); continue; }
            var n = token[i + 1];
            if (n == '$') { literal.Append('$'); i++; continue; }
            if (n == '\\' && i + 2 < token.Length)
            {
                literal.Append(token[i + 2] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var q => q });
                i += 2;
                continue;
            }
            if (n == '{')
            {
                var end = token.IndexOf('}', i + 2);
                if (end < 0) { literal.Append(token[i..]); break; }
                var name = token[(i + 2)..end];
                Flush();
                if (ctx.Variables.TryGetValue(name, out var value))
                {
                    // A define built from others (or holding NSIS variables) is written out, keeping the inner defines:
                    // OUTFILE "dist\App-${VERSION}.exe" gives "dist\App-$(VERSION).exe".
                    if (_rawDefines.TryGetValue(name, out var raw) && raw.Contains('$') && depth < 16) sb.Append(Text(raw, language, depth + 1));
                    else if (value is not null && (value.Contains('$') || !ProjectVariables.IsValidName(name)) && depth < 16) sb.Append(Text(value, language, depth + 1));
                    else sb.Append("$(").Append(name).Append(')');
                }
                else sb.Append("{?def:").Append(name).Append('}');
                i = end;
                continue;
            }
            if (n == '(')
            {
                var end = token.IndexOf(')', i + 2);
                if (end < 0) { literal.Append(token[i..]); break; }
                var name = token[(i + 2)..end];
                Flush();
                if (name.Equals("^Name", StringComparison.OrdinalIgnoreCase) && !_resolvingName && Attr("Name") is { } appName)
                {
                    // NSIS's own LangString for the Name attribute.
                    _resolvingName = true;
                    sb.Append(Text(appName.Value, language, depth + 1));
                    _resolvingName = false;
                }
                else if (LangString(name, language) is { } text && depth < 16) sb.Append(Text(text, language, depth + 1));
                else sb.Append("{?lang:").Append(name).Append('}');
                i = end;
                continue;
            }
            if (n == '%')
            {
                // $%NAME%: an environment variable of the PC that compiles the script.
                var end = token.IndexOf('%', i + 2);
                if (end < 0) { literal.Append(token[i..]); break; }
                Flush();
                sb.Append("{?$%").Append(token[(i + 2)..end]).Append("%}");
                i = end;
                continue;
            }
            var j = i + 1;
            while (j < token.Length && (char.IsAsciiLetterOrDigit(token[j]) || token[j] == '_')) j++;
            var variable = token[(i + 1)..j];
            if (variable.Length == 0) { literal.Append(c); continue; }
            // NSIS matches the longest known name: "$INSTDIRx" is unusual, "$INSTDIR\x" common.
            var (replacement, length) = Variable(variable);
            Flush();
            sb.Append(replacement);
            i += length;
        }
        Flush();
        return sb.ToString();
    }

    private (string Replacement, int Length) Variable(string name)
    {
        string? Map(string v) => v.ToUpperInvariant() switch
        {
            "INSTDIR" => "{InstallDir}",
            "OUTDIR" => _outDir,
            "PROGRAMFILES" or "PROGRAMFILES32" or "PROGRAMFILES64" => "{ProgramFiles}",
            "LOCALAPPDATA" => _shellAll ? "{CommonAppData}" : "{LocalAppData}",
            "APPDATA" => _shellAll ? "{CommonAppData}" : "{AppData}",
            "SMPROGRAMS" => "{Programs}",
            "DESKTOP" => "{Desktop}",
            "SMSTARTUP" => "{Startup}",
            "QUICKLAUNCH" => @"{AppData}\Microsoft\Internet Explorer\Quick Launch",
            "TEMP" or "PLUGINSDIR" => "{Temp}",
            "EXEDIR" => "{SourceDir}",
            "WINDIR" => "{Windows}",
            "SYSDIR" => "{System}",
            _ => null,
        };
        if (Map(name) is { } direct) return (direct, name.Length);
        if (_varValues.TryGetValue(name, out var value)) return (Text(value), name.Length);
        if (_userVars.Contains(name)) return ("{?$" + name + "}", name.Length);
        foreach (var known in new[] { "PROGRAMFILES64", "PROGRAMFILES32", "PROGRAMFILES", "LOCALAPPDATA", "SMPROGRAMS", "SMSTARTUP", "QUICKLAUNCH", "PLUGINSDIR", "INSTDIR", "OUTDIR", "APPDATA", "DESKTOP", "EXEDIR", "WINDIR", "SYSDIR", "TEMP" })
            if (name.StartsWith(known, StringComparison.OrdinalIgnoreCase)) return (Map(known)!, known.Length);
        return ("{?$" + name + "}", name.Length);
    }

    private string? LangString(string name, string language)
    {
        if (!_langStrings.TryGetValue(name, out var byLang)) return null;
        if (language.Length > 0 && byLang.TryGetValue(language, out var t)) return t;
        if (byLang.TryGetValue("en-US", out var en)) return en;
        return byLang.Values.FirstOrDefault();
    }

    /// <summary>Text without tokens, for notes.</summary>
    private string Plain(string token) => Canonical.Display(ctx.Resolve(Text(token)));

    /// <summary>A section's name for notes (without the "!" bold marker and "un." prefix).</summary>
    private string SectionName(Block b)
    {
        var name = Plain(b.Name).TrimStart('!', '-');
        return name.StartsWith("un.", StringComparison.OrdinalIgnoreCase) && name.Length > 3 ? name[3..] : name;
    }

    /// <summary>Labels per language for a section name (LangStrings give one per language).</summary>
    private Dictionary<string, string> Labels(string token)
    {
        static string Clean(string s) => ImportContext.StripAccelerators(s.TrimStart('!', '-')).Trim();
        var result = new Dictionary<string, string>();
        if (token.Contains("$(", StringComparison.Ordinal))
            foreach (var tag in _languages.Select(l => l.Tag).Where(t => t.Length > 0).Distinct())
                if (Clean(Text(token, tag)) is { Length: > 0 } s && !Canonical.IsUnsupported(s)) result[tag] = s;
        if (result.Count == 0 && Clean(Text(token)) is { Length: > 0 } en && !Canonical.IsUnsupported(en)) result["en-US"] = en;
        return result;
    }

    // ---- attributes ----------------------------------------------------------------------------------------------

    private void ReadAttributes()
    {
        var cosmetic = new List<string>();
        foreach (var line in _top)
        {
            var cmd = line.Command;
            switch (cmd.ToLowerInvariant())
            {
                case "name" or "outfile" or "installdir" or "installdirregkey" or "requestexecutionlevel" or "icon" or "licensedata" or "viproductversion":
                    _attrs[cmd] = (line.Arg(0), line.At);
                    break;
                case "viaddversionkey":
                {
                    var args = line.Tokens.Skip(1).Where(t => !t.StartsWith("/LANG=", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (args.Count >= 2 && !_versionKeys.ContainsKey(args[0])) _versionKeys[args[0]] = (args[1], line.At);
                    break;
                }
                case "var":
                    foreach (var v in line.Tokens.Skip(1).Where(t => !t.StartsWith('/'))) _userVars.Add(v);
                    break;
                case "langstring":
                {
                    var args = line.Tokens.Skip(1).ToList();
                    if (args.Count < 3) break;
                    var tag = LanguageTag(args[1]);
                    if (!_langStrings.TryGetValue(args[0], out var byLang)) _langStrings[args[0]] = byLang = new(StringComparer.OrdinalIgnoreCase);
                    byLang[tag.Length > 0 ? tag : args[1]] = args[2];
                    break;
                }
                case "licenselangstring":
                {
                    var args = line.Tokens.Skip(1).ToList();
                    if (args.Count < 3) break;
                    var tag = LanguageTag(args[1]);
                    if (!_langStrings.TryGetValue(args[0], out var byLang)) _langStrings[args[0]] = byLang = new(StringComparer.OrdinalIgnoreCase);
                    byLang[tag.Length > 0 ? tag : args[1]] = args[2];
                    if (tag.Length > 0)
                    {
                        var path = Text(args[2]);
                        if (!Canonical.IsUnsupported(path)) { ctx.Project.Ui.Licenses[tag] = ctx.ProjectPathOf(path); ctx.Info(line.At, $"LicenseLangString ({tag}) → ui.licenses[\"{tag}\"]", "ui.licenses"); }
                    }
                    break;
                }
                case "page":
                {
                    var kind = line.Arg(0).ToLowerInvariant();
                    _pages.Add(kind == "license" ? "LICENSE" : kind == "components" ? "COMPONENTS" : kind == "directory" ? "DIRECTORY" : kind);
                    if (kind == "custom")
                    {
                        if (line.Arg(1).Length > 0) _pageFunctions.Add(line.Arg(1));
                        ctx.Manual(line.At, $"Custom page ({line.Arg(1)}): not imported. Values the user enters can be inputs[] (the Settings page).", "inputs");
                    }
                    break;
                }
                case "uninstpage":
                    _unPages.Add(line.Arg(0).ToUpperInvariant());
                    break;
                case "setshellvarcontext":
                    if (line.Arg(0).Equals("all", StringComparison.OrdinalIgnoreCase)) _shellAll = true;
                    break;
                case "silentinstall":
                    if (line.Arg(0).Equals("silent", StringComparison.OrdinalIgnoreCase)) ctx.Warn(line.At, "SilentInstall silent: FluentSetup shows its interface unless started with /quiet (or /S).");
                    break;
                case "!insertmacro":
                    ctx.Warn(line.At, $"Macro {line.Arg(0)} (from a header that was not found) is not expanded; check what it does.");
                    break;
                case "caption":
                    ctx.Info(line.At, "Caption: the installer's title is the app's name (ui.title for the first page).", "ui.title");
                    break;
                default:
                    if (CosmeticAttributes.Contains(cmd)) cosmetic.Add($"{cmd} (line {line.At.Line})");
                    else if (cmd.Contains("::", StringComparison.Ordinal)) ctx.Manual(line.At, $"Plug-in call {cmd} outside sections is not imported.");
                    else ctx.Info(line.At, $"{cmd} is not imported (no FluentSetup equivalent).");
                    break;
            }
        }
        if (cosmetic.Count > 0)
            ctx.Info(_top.First().At, $"Attributes about the installer's look, compression or NSIS's own behaviour are not needed: {string.Join(", ", cosmetic)}.");
        foreach (var (key, (value, at)) in _versionKeys)
            if (key is not ("ProductName" or "CompanyName" or "ProductVersion" or "FileVersion" or "FileDescription" or "LegalCopyright" or "Comments"))
                ctx.Info(at, $"VIAddVersionKey {key} is not imported (Setup.exe's version information comes from app.*).");
        ReadLanguages();
    }

    private static string LanguageTag(string token)
    {
        var t = token.Trim();
        if (t.StartsWith("${", StringComparison.Ordinal) && t.EndsWith('}')) t = t[2..^1];
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcid))
            return lcid switch { 1033 => "en-US", 2052 => "zh-CN", 1028 => "zh-TW", 1041 => "ja-JP", 1042 => "ko-KR", 1031 => "de-DE", 1036 => "fr-FR", 1034 or 3082 => "es-ES", 1049 => "ru-RU", 1040 => "it-IT", 1046 => "pt-BR", _ => "" };
        return ScriptLanguages.Tag(t);
    }

    private void ReadLanguages()
    {
        if (_languages.Count == 0) return;
        var builtIn = _languages.Where(l => Localization.SupportedLanguages.Contains(l.Tag)).Select(l => $"{l.Name} ({l.Tag})").ToList();
        ctx.Info(_languages[0].At, $"Languages: the installer shows Windows' display language when it has it{(builtIn.Count > 0 ? ": built in " + string.Join(", ", builtIn) : "")}.", "ui.defaultLanguage");
        foreach (var l in _languages.Where(l => !Localization.SupportedLanguages.Contains(l.Tag)))
            ctx.Manual(l.At, l.Tag.Length > 0
                ? $"Language {l.Name}: the installer's texts are built in for en-US, zh-CN, ja-JP and ko-KR. Add {l.Tag} with 'fluentsetup locale add {l.Tag} fluentsetup.json' (set ui.localization first)."
                : $"Language {l.Name} is not recognised; add it with 'fluentsetup locale add <tag> fluentsetup.json'.", "ui.localization");
        if (_languages.Count == 1 && _languages[0].Tag is { Length: > 0 } only && only != "en-US" && Localization.SupportedLanguages.Contains(only))
        {
            ctx.Project.Ui.DefaultLanguage = only;
            ctx.Info(_languages[0].At, $"The script has only {_languages[0].Name} → ui.defaultLanguage \"{only}\"", "ui.defaultLanguage");
        }
    }

    private (string Value, SourceRef At)? Attr(string name) => _attrs.TryGetValue(name, out var v) ? v : null;

    // ---- scope ------------------------------------------------------------------------------------------------

    private void DecideScope()
    {
        var p = ctx.Project;
        var level = Attr("RequestExecutionLevel");
        var multi = ctx.Variables.TryGetValue("MULTIUSER_EXECUTIONLEVEL", out var m) ? m?.ToLowerInvariant() : null;
        var dir = Attr("InstallDir") is { } d ? Text(d.Value) : "";
        var functionsSetAll = _blocks.Any(b => b.Lines.Any(l => l.Command.Equals("SetShellVarContext", StringComparison.OrdinalIgnoreCase) && l.Arg(0).Equals("all", StringComparison.OrdinalIgnoreCase)));
        string scope, why;
        if (multi is not null)
        {
            scope = multi switch { "highest" => "userOrMachine", "standard" => "user", _ => "machine" };
            p.Install.DefaultScope = ctx.Variables.ContainsKey("MULTIUSER_INSTALLMODE_DEFAULT_CURRENTUSER") || multi == "standard" ? "user" : "machine";
            why = $"MultiUser.nsh with MULTIUSER_EXECUTIONLEVEL {m}";
        }
        else
        {
            var l = level?.Value.ToLowerInvariant() ?? "";
            scope = l switch
            {
                "admin" => "machine",
                "user" => "user",
                "highest" => "userOrMachine",
                _ => dir.StartsWith("{LocalAppData}", StringComparison.OrdinalIgnoreCase) || dir.StartsWith("{AppData}", StringComparison.OrdinalIgnoreCase) ? "user" : "machine",
            };
            p.Install.DefaultScope = scope == "user" ? "user" : "machine";
            why = l.Length > 0 ? $"RequestExecutionLevel {l}" : $"no RequestExecutionLevel; InstallDir {Canonical.Display(dir)}";
        }
        p.Install.Scope = scope;
        if (scope == "machine" || functionsSetAll) _shellAll = _shellAll || functionsSetAll;
        ctx.Info(level?.At ?? ctx.Script, $"{why} → install.scope \"{scope}\"{(scope == "userOrMachine" ? $", defaultScope \"{p.Install.DefaultScope}\"" : "")}", "install.scope");
        if (scope == "user" && _shellAll) ctx.Warn(level?.At ?? ctx.Script, "SetShellVarContext all (shortcuts for all users) needs a per-machine install; the per-user install creates them for the current user.", "install.scope");
    }

    // ---- sections -----------------------------------------------------------------------------------------------

    private bool HasComponentsPage => _pages.Contains("COMPONENTS");

    private void CreateOptionCandidates()
    {
        foreach (var b in _blocks.Where(b => b.Kind == "section" && !b.Uninstall))
        {
            if (b.Hidden) continue;
            var label = Labels(b.Name);
            if (!HasComponentsPage)
            {
                if (b.Unselected) ctx.Manual(b.At, $"Section \"{Plain(b.Name)}\" is unselected (/o) and there is no components page, so NSIS never installed it. Its commands are not imported.");
                continue;
            }
            var id = ctx.NewOptionId(b.Index.Length > 0 ? b.Index : label.Values.FirstOrDefault() ?? "section");
            ctx.Options.Add(new OptionCandidate { Id = id, At = b.At, Origin = $"section \"{SectionName(b)}\"", Label = label, Default = !b.Unselected, Required = b.ReadOnly });
            b.Candidate = id;
        }
        foreach (var b in _blocks.Where(b => b.Kind == "section" && b.Uninstall && !b.Hidden && !_unPages.Contains("COMPONENTS")))
            if (b.Unselected) ctx.Info(b.At, $"Uninstall section \"{Plain(b.Name)}\" is unselected (/o) without an uninstall components page; not imported.");
    }

    private sealed class SectionState
    {
        public string Overwrite = "on";
        public bool Conditional;
        public int Depth;
        public bool Skipping;
        public int SkipDepth;
        public bool NotedJumps;
        public bool NotedElse;
        public List<string> Unknown = [];
        public List<string> Ignored = [];
    }

    private static readonly HashSet<string> NeutralCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "DetailPrint", "SetDetailsPrint", "SetDetailsView", "SectionIn", "AddSize", "StrCpy", "StrLen", "IntOp", "IntFmt", "Push", "Pop", "Exch", "ReadRegStr",
        "ReadRegDWORD", "ReadEnvStr", "ReadINIStr", "ExpandEnvStrings", "GetFullPathName", "GetTempFileName", "SearchPath", "SectionSetFlags", "SectionGetFlags",
        "SectionSetText", "SectionGetText", "SetAutoClose", "SetErrors", "ClearErrors", "SetRebootFlag", "GetDLLVersion", "GetFileTime", "GetDLLVersionLocal",
        "GetFileTimeLocal", "GetCurrentAddress", "GetFunctionAddress", "GetLabelAddress", "LogSet", "LogText", "Nop", "SetCtlColors", "EnableWindow", "ShowWindow",
        "FindWindow", "SendMessage", "LockWindow", "InitPluginsDir", "SetPluginUnload", "SetOutPath", "SetOverwrite", "SetShellVarContext", "SetRegView",
        "WriteUninstaller", "Sleep", "BringToFront", "HideWindow", "IfAbort", "SetCurInstType", "GetCurInstType", "InstTypeSetText", "InstTypeGetText", "FileOpen",
        "FileClose", "FileRead", "FileWrite", "FileSeek", "FileReadByte", "FileWriteByte", "FileReadUTF16LE", "FileWriteUTF16LE", "FindFirst", "FindNext", "FindClose",
        "ReadRegValue", "EnumRegKey", "EnumRegValue", "Return", "Abort", "Quit", "SetSilent", "IfSilent", "IfRebootFlag", "IfErrors", "IfFileExists", "StrCmp",
        "StrCmpS", "IntCmp", "IntCmpU", "Goto", "MessageBox", "Reboot", "IfShellVarContextAll", "IfRtlLanguage", "CreateFont", "LoadLanguageFile", "UnRegDLL", "RegDLL",
    };

    private void ExecuteSection(Block b)
    {
        if (!b.Uninstall && !b.Hidden && !HasComponentsPage && b.Unselected) return;
        if (b.Uninstall && !b.Hidden && b.Unselected && !_unPages.Contains("COMPONENTS")) return;
        var state = new SectionState();
        _outDir = "{InstallDir}";
        var label = SectionName(b);
        var what = b.Uninstall ? $"uninstall section \"{label}\"" : b.Hidden ? (label.Length > 0 ? $"section \"{label}\"" : "the unnamed section") : $"section \"{label}\"";
        var optional = b.Candidate.Length > 0 ? ctx.FindOption(b.Candidate) : null;
        foreach (var line in b.Lines)
        {
            var cmd = line.Command;
            if (LogicLib(line, state, what)) continue;
            if (state.Skipping) continue;
            if (cmd.EndsWith(':') && line.Tokens.Count == 1) continue;  // label
            if (cmd is "IfFileExists" or "StrCmp" or "StrCmpS" or "IntCmp" or "IntCmpU" or "Goto" or "IfErrors" or "IfSilent" or "MessageBox" or "IfRebootFlag" or "IfShellVarContextAll")
            {
                if (!state.NotedJumps) { state.NotedJumps = true; ctx.Warn(line.At, $"{Capitalize(what)} uses jumps or conditions ({cmd} …); they are not evaluated: its commands were imported in order."); }
                state.Conditional = true;
                continue;
            }
            switch (cmd.ToLowerInvariant())
            {
                case "setoutpath": _outDir = Text(line.Arg(0)); continue;
                case "setoverwrite": state.Overwrite = line.Arg(0).ToLowerInvariant(); continue;
                case "setshellvarcontext": _shellAll = line.Arg(0).Equals("all", StringComparison.OrdinalIgnoreCase); continue;
                case "setregview":
                    if (line.Arg(0) == "32") ctx.Warn(line.At, "SetRegView 32: the 32-bit registry view is not used; FluentSetup writes the native view.", "registry");
                    continue;
                case "file": FileCommand(line, b, state, optional); continue;
                case "createdirectory": CreateDirectory(line, b); continue;
                case "createshortcut": Shortcut(line, b, state); continue;
                case "writeregstr" or "writeregexpandstr" or "writeregdword" or "writeregbin" or "writeregmultistr" or "writeregnone" or "writeregqword":
                    RegistryWrite(line, b, state); continue;
                case "deleteregkey" or "deleteregvalue": RegistryDelete(line, b); continue;
                case "writeinistr": IniWrite(line, b, state); continue;
                case "writeuninstaller":
                {
                    var name = Path.GetFileName(ctx.Resolve(Text(line.Arg(0))).Replace('\\', '/').Split('/')[^1]);
                    _uninstallerName = name;
                    ctx.Info(line.At, "WriteUninstaller: FluentSetup writes its own uninstall.exe into the install folder.");
                    continue;
                }
                case "execwait" or "exec" or "execshell" or "execshellwait" or "nsexec::exec" or "nsexec::exectolog" or "nsexec::exectostack" or "execdos::exec" or "execcmd::exec":
                    Exec(line, b, state); continue;
                case "delete" or "rmdir": DeleteCommand(line, b, state); continue;
                case "call":
                {
                    var fn = line.Arg(0);
                    if (fn.StartsWith(':')) continue;
                    ctx.Manual(line.At, $"{Capitalize(what)} calls function {fn}; functions are not imported. Check what it does.");
                    continue;
                }
                case "copyfiles" or "rename":
                    ctx.Manual(line.At, $"{cmd} {string.Join(' ', line.Tokens.Skip(1).Select(Plain))}: FluentSetup installs files from the package only (no copying or renaming at install time).", "files");
                    continue;
                case "!insertmacro":
                    ctx.Warn(line.At, $"Macro {line.Arg(0)} (from a header that was not found) is not expanded in {what}.");
                    continue;
            }
            if (NeutralCommands.Contains(cmd)) continue;
            if (cmd.Contains("::", StringComparison.Ordinal))
            {
                ctx.Manual(line.At, $"Plug-in call {cmd} in {what} is not imported{PluginHint(cmd)}.");
                continue;
            }
            if (cmd.StartsWith("${", StringComparison.Ordinal))
            {
                ctx.Manual(line.At, $"{cmd} (a header function) in {what} is not imported.");
                continue;
            }
            state.Unknown.Add($"{cmd} (line {line.At.Line})");
        }
        if (state.Unknown.Count > 0) ctx.Warn(b.At, $"{Capitalize(what)}: commands not imported: {string.Join(", ", state.Unknown.Take(20))}.");
        if (state.Ignored.Count > 0) ctx.Info(b.At, $"{Capitalize(what)}: not needed, FluentSetup removes the app's files, shortcuts and folder itself: {string.Join(", ", state.Ignored.Take(20))}.");
    }

    private static string PluginHint(string cmd)
    {
        var plugin = cmd[..cmd.IndexOf("::", StringComparison.Ordinal)].ToLowerInvariant();
        return plugin switch
        {
            "simplesc" or "nsscm" or "servicelib" => " (declare Windows services in services[])",
            "simplefc" or "firewallapi" => " (declare firewall rules in firewall[])",
            "nsprocess" or "killproc" or "processwork" => " (list the programs in install.closeProcesses)",
            "inetc" or "nsisdl" or "inetload" => " (prerequisites can be downloaded by setup: requirements.*.download or auto \"download\")",
            "nsdialogs" or "installoptions" => " (values the user enters can be inputs[])",
            "userinfo" => " (the install scope decides about administrator rights)",
            "system" => " (Win32 API calls cannot be expressed)",
            _ => "",
        };
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>LogicLib (${If} … ${EndIf}): the first branch is imported; ${Else}/${ElseIf}/later ${Case} branches are skipped.</summary>
    private bool LogicLib(Line line, SectionState s, string what)
    {
        var cmd = line.Command;
        if (!cmd.StartsWith("${", StringComparison.Ordinal) || !cmd.EndsWith('}')) return false;
        var name = cmd[2..^1];
        switch (name)
        {
            case "If" or "IfNot" or "Unless" or "Switch" or "Select":
                s.Depth++;
                s.Conditional = true;
                return true;
            case "ElseIf" or "Else" or "ElseUnless" or "ElseIfNot" or "CaseElse" or "Default":
                if (!s.Skipping) { s.Skipping = true; s.SkipDepth = s.Depth; }
                if (!s.NotedElse) { s.NotedElse = true; ctx.Warn(line.At, $"{Capitalize(what)} has ${{If}}/${{Else}} branches; only the first branch of each was imported."); }
                return true;
            case "Case" or "Case2" or "Case3" or "Case4" or "Case5":
                return true;
            case "EndIf" or "EndUnless" or "EndSwitch" or "EndSelect":
                if (s.Skipping && s.SkipDepth == s.Depth) s.Skipping = false;
                s.Depth = Math.Max(0, s.Depth - 1);
                return true;
            case "Do" or "DoWhile" or "DoUntil" or "While" or "For" or "ForEach" or "Loop" or "LoopWhile" or "LoopUntil" or "EndWhile" or "Next" or "Break" or "Continue" or "ExitDo" or "ExitFor" or "ExitWhile":
                s.Conditional = true;
                return true;
            case "IfThen" or "IfNotThen" or "IfCmd":
                if (!s.NotedJumps) { s.NotedJumps = true; ctx.Warn(line.At, $"{Capitalize(what)}: one-line conditions (${{{name}}}) are not evaluated."); }
                return true;
            default:
                return false;
        }
    }

    private void FileCommand(Line line, Block b, SectionState state, OptionCandidate? optional)
    {
        var what = $"File {string.Join(' ', line.Tokens.Skip(1).Select(t => Plain(t)))}";
        if (b.Uninstall) { ctx.Warn(line.At, $"{what} in the uninstall section is not imported."); return; }
        if (!ctx.TryInstallRelative(_outDir, out var dest))
        {
            ctx.Manual(line.At, $"{what} goes to {Canonical.Display(_outDir)}: FluentSetup installs files into the install folder only.", "files");
            return;
        }
        bool recurse = false;
        var oname = "";
        var excludes = new List<string>();
        var sources = new List<string>();
        var args = line.Tokens.Skip(1).ToList();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Equals("/r", StringComparison.OrdinalIgnoreCase)) recurse = true;
            else if (a.Equals("/x", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) excludes.Add(ctx.Resolve(Text(args[++i])));
            else if (a.StartsWith("/oname=", StringComparison.OrdinalIgnoreCase)) oname = ctx.Resolve(Text(a[7..]));
            else if (a.Equals("/nonfatal", StringComparison.OrdinalIgnoreCase) || a.Equals("/a", StringComparison.OrdinalIgnoreCase)) { }
            else sources.Add(a);
        }
        if (optional is { Required: false })
        {
            optional.Unexpressed.Add("files");
            ctx.Warn(line.At, $"{what} is in optional section \"{SectionName(b)}\"; files cannot depend on an option, so they are always installed.", "files");
        }
        var preserve = state.Overwrite == "off";
        if (preserve) ctx.Info(line.At, $"{what} after SetOverwrite off → files.preserve (installed only if missing, never overwritten, kept on uninstall)", "files.preserve");
        else if (state.Overwrite is "ifnewer" or "ifdiff" or "try") ctx.Info(line.At, $"SetOverwrite {state.Overwrite}: FluentSetup replaces the app's files with the package's version.");
        foreach (var src in sources)
        {
            var text = Text(src);
            if (Canonical.IsUnsupported(text) || ctx.HasUnknownVariable(text))
            {
                ctx.Manual(line.At, $"{what}: the source path uses {(Canonical.FirstUnsupported(text) is { Length: > 0 } u ? u : "a value computed when the script is compiled")}; add these files by hand.", "files");
                continue;
            }
            var full = ctx.ScriptFile(ctx.Resolve(text));
            var name = Path.GetFileName(full);
            var wildcard = name.IndexOfAny(['*', '?']) >= 0;
            if (recurse && !wildcard && (Directory.Exists(full) || (!File.Exists(full) && !Path.HasExtension(name))))
            {
                // File /r folder: the folder itself goes into $OUTDIR.
                ctx.Files.Add(new FileEntry { At = line.At, Source = Path.Combine(full, "*"), DestDir = dest.Length == 0 ? name : dest + "/" + name, Recurse = true, Preserve = preserve, Excludes = excludes, Variables = [.. ImportContext.ReferencedVariables(text)] });
                continue;
            }
            ctx.Files.Add(new FileEntry { At = line.At, Source = full, DestDir = dest, Recurse = recurse, DestName = oname, Preserve = preserve, Excludes = excludes, Variables = [.. ImportContext.ReferencedVariables(text)] });
        }
    }

    private void CreateDirectory(Line line, Block b)
    {
        var path = Text(line.Arg(0));
        if (ctx.TryInstallRelative(path, out var rel))
        {
            if (rel.Length > 0 && !b.Uninstall) ctx.EmptyDirectories.Add((line.At, rel));
            return;
        }
        if (path.StartsWith("{Programs}", StringComparison.OrdinalIgnoreCase)) return;  // Start menu folder: created with the shortcuts
        if (Canonical.AsDataTemplate(ctx.Resolve(path)) is not null) { ctx.Info(line.At, $"CreateDirectory {Plain(line.Arg(0))}: the app creates its data folders itself; not needed."); return; }
        ctx.Manual(line.At, $"CreateDirectory {Plain(line.Arg(0))} is outside the install folder; FluentSetup creates folders inside it only.", "files.emptyDirectories");
    }

    private void Shortcut(Line line, Block b, SectionState state)
    {
        var args = line.Tokens.Skip(1).ToList();
        var noWorkingDir = args.RemoveAll(a => a.Equals("/NoWorkingDir", StringComparison.OrdinalIgnoreCase)) > 0;
        if (b.Uninstall || args.Count < 2) return;
        var link = Text(args[0]);
        if (ctx.Resolve(link).EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) link = link[..^4];
        var slash = link.LastIndexOf('\\');
        var dir = slash >= 0 ? link[..slash] : "";
        var leaf = slash >= 0 ? link[(slash + 1)..] : link;
        string location, folder = "", unsupported = "";
        if (dir.StartsWith("{Programs}", StringComparison.OrdinalIgnoreCase)) { location = "startMenu"; folder = dir["{Programs}".Length..].Trim('\\'); }
        else if (dir.Equals("{Desktop}", StringComparison.OrdinalIgnoreCase)) location = "desktop";
        else if (dir.Equals("{Startup}", StringComparison.OrdinalIgnoreCase)) location = "startup";
        else if (dir.Contains("Quick Launch", StringComparison.OrdinalIgnoreCase)) location = "quickLaunch";
        else { location = "other"; unsupported = dir; }
        if (args.Count > 5 && args[5].Length > 0) ctx.Info(line.At, $"CreateShortCut {Plain(args[0])}: the window state ({args[5]}) is not set.");
        if (args.Count > 6 && args[6].Length > 0) ctx.Info(line.At, $"CreateShortCut {Plain(args[0])}: hot key {args[6]} is not set.");
        if (state.Conditional) ctx.Warn(line.At, $"CreateShortCut {Plain(args[0])} depended on a script condition; it is always created{(b.Candidate.Length > 0 ? " with its option" : "")}.", "shortcuts");
        ctx.Shortcuts.Add(new ShortcutEntry
        {
            At = line.At, Location = location, Folder = folder, Name = leaf, Unsupported = unsupported,
            Target = Text(args[1]), Arguments = args.Count > 2 ? Text(args[2]) : "",
            Icon = args.Count > 3 ? Text(args[3]) : "",
            IconIndex = args.Count > 4 && int.TryParse(ctx.Resolve(Text(args[4])), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
            Description = args.Count > 7 ? Text(args[7]) : "",
            WorkingDirectory = noWorkingDir ? "" : _outDir,
            IfOption = b.Candidate,
        });
    }

    private static (string Root, string Prefix)? Root(string token) => token.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" or "HKLM64" or "HKLM32" => ("HKLM", ""),
        "HKCU" or "HKEY_CURRENT_USER" or "HKCU64" or "HKCU32" => ("HKCU", ""),
        "HKCR" or "HKEY_CLASSES_ROOT" or "HKCR64" or "HKCR32" => ("HKA", @"Software\Classes\"),
        "SHCTX" or "SHELL_CONTEXT" or "SHCTX64" or "SHCTX32" => ("HKA", ""),
        _ => null,
    };

    private void RegistryWrite(Line line, Block b, SectionState state)
    {
        var cmd = line.Command.ToLowerInvariant();
        var args = line.Tokens.Skip(1).Where(t => !t.StartsWith("/REGEDIT", StringComparison.OrdinalIgnoreCase)).ToList();
        var what = $"{line.Command} {string.Join(' ', args.Take(3).Select(Plain))}";
        if (b.Uninstall) { ctx.Warn(line.At, $"{what} in the uninstall section is not imported."); return; }
        if (args.Count < 3 || Root(args[0]) is not { } root)
        {
            ctx.Manual(line.At, $"{what}: only HKLM, HKCU, HKCR and SHCTX are supported.", "registry");
            return;
        }
        var key = root.Prefix + Text(args[1]).Trim('\\');
        if (Canonical.IsUnsupported(key)) { ctx.Manual(line.At, $"{what}: the key uses {Canonical.FirstUnsupported(key)}, which the importer cannot fill in.", "registry"); return; }
        var type = cmd switch { "writeregexpandstr" => "expandString", "writeregdword" => "dword", "writeregqword" => "qword", "writeregbin" => "binary", "writeregmultistr" => "multiString", "writeregnone" => "binary", _ => "string" };
        var data = args.Count > 3 ? Text(args[3]) : "";
        if (type == "multiString") { ctx.Manual(line.At, $"{what}: WriteRegMultiStr data is hex (REGEDIT5); write it as a multiString value in registry[] by hand.", "registry"); return; }
        ctx.Registry.Add(new RegistryWrite
        {
            At = line.At, Root = root.Root, Key = key, Name = Text(args[2]), Type = type, Data = data,
            IfOption = b.Candidate, Conditional = state.Conditional,
        });
    }

    private void RegistryDelete(Line line, Block b)
    {
        var args = line.Tokens.Skip(1).ToList();
        var ifEmpty = args.RemoveAll(a => a.Equals("/ifempty", StringComparison.OrdinalIgnoreCase) || a.Equals("/ifnosubkeys", StringComparison.OrdinalIgnoreCase) || a.Equals("/ifnovalues", StringComparison.OrdinalIgnoreCase)) > 0;
        var isValue = line.Command.Equals("DeleteRegValue", StringComparison.OrdinalIgnoreCase);
        var what = $"{line.Command} {string.Join(' ', args.Select(Plain))}";
        if (args.Count < 2 || Root(args[0]) is not { } root) { ctx.Manual(line.At, $"{what}: only HKLM, HKCU, HKCR and SHCTX are supported."); return; }
        var key = ctx.Resolve(root.Prefix + Text(args[1]).Trim('\\'));
        const string uninstallPrefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
        if (!b.Uninstall)
        {
            if (!isValue && key.StartsWith(uninstallPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _uninstallKeys.Add((key[uninstallPrefix.Length..], line.At));  // decided once app.id is known
                return;
            }
            ctx.Manual(line.At, $"{what}: the script deletes registry data while installing; FluentSetup does not (install.cleanup handles old Run values, tasks and uninstall entries).", "install.cleanup");
            return;
        }
        if (key.StartsWith(uninstallPrefix, StringComparison.OrdinalIgnoreCase)) { ctx.Info(line.At, $"{what}: FluentSetup removes the app's uninstall entry itself."); return; }
        if (key.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase)) { ctx.Info(line.At, $"{what}: FluentSetup removes the file types and protocols it registered."); return; }
        if (isValue && key.Equals(@"Software\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase))
        {
            var name = ctx.Resolve(Text(args.Count > 2 ? args[2] : ""));
            if (ctx.Registry.Any(w => w.Name is not null && ctx.Resolve(w.Key).Equals(key, StringComparison.OrdinalIgnoreCase) && ctx.Resolve(w.Name).Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Info(line.At, $"{what}: the startup option removes its Run value itself.");
                return;
            }
            if (name.Length > 0 && !ctx.Project.Uninstall.RemoveRunValues.Contains(name, StringComparer.OrdinalIgnoreCase)) ctx.Project.Uninstall.RemoveRunValues.Add(name);
            ctx.Info(line.At, $"{what} → uninstall.removeRunValues", "uninstall.removeRunValues");
            return;
        }
        ctx.Registry.Add(new RegistryWrite
        {
            At = line.At, Root = root.Root, Key = Text(args[1]).Trim('\\'), Name = null, Type = "string",
            Uninstall = isValue ? RegistryUninstall.Values : ifEmpty ? RegistryUninstall.KeyIfEmpty : RegistryUninstall.Key,
        });
    }

    private void IniWrite(Line line, Block b, SectionState state)
    {
        var args = line.Tokens.Skip(1).ToList();
        if (b.Uninstall || args.Count < 4) return;
        var file = Text(args[0]);
        var resolved = ctx.Resolve(file);
        var what = $"WriteINIStr {Plain(args[0])} [{Plain(args[1])}] {Plain(args[2])}";
        if (resolved.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) { ctx.Info(line.At, $"{what}: an internet shortcut file; FluentSetup has no web-link shortcuts."); return; }
        string path;
        if (ctx.TryInstallRelative(file, out var rel) && rel.Length > 0) path = rel;
        else if (Canonical.AsDataTemplate(resolved) is { } t && !t.StartsWith("{InstallDir}", StringComparison.Ordinal)) path = t;
        else { ctx.Manual(line.At, $"{what}: FluentSetup writes INI files in the install folder, {{LocalAppData}}, {{AppData}} or {{CommonAppData}} only.", "configFiles"); return; }
        if (Canonical.AsValue(Text(args[3])) is not { } value) { ctx.Manual(line.At, $"{what}: the value uses a folder or constant FluentSetup cannot fill in.", "configFiles"); return; }
        var p = ctx.Project;
        var ifOption = ctx.UseOption(b.Candidate);
        var cfg = p.ConfigFiles.FirstOrDefault(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && c.IfOption == ifOption);
        if (cfg is null)
        {
            if (p.ConfigFiles.Count >= 16) { ctx.Manual(line.At, $"{what} is not imported: at most 16 config files.", "configFiles"); return; }
            cfg = new ConfigFileSettings { Path = path, Format = "ini", IfOption = ifOption };
            p.ConfigFiles.Add(cfg);
        }
        cfg.Values.Add(new ConfigValueSettings { Section = Text(args[1]), Key = Text(args[2]), Value = value });
        ctx.Info(line.At, $"{what} → configFiles[{p.ConfigFiles.IndexOf(cfg)}] (ini)", "configFiles");
        if (state.Conditional) ctx.Warn(line.At, $"{what} depended on a script condition; it is always written.", "configFiles");
    }

    private void Exec(Line line, Block b, SectionState state)
    {
        var cmd = line.Command.ToLowerInvariant();
        var args = line.Tokens.Skip(1).Where(t => !t.StartsWith("/TIMEOUT", StringComparison.OrdinalIgnoreCase) && !t.Equals("/OEM", StringComparison.OrdinalIgnoreCase)).ToList();
        if (args.Count == 0) return;
        string program;
        List<string> arguments;
        if (cmd.StartsWith("execshell", StringComparison.Ordinal))
        {
            program = Text(args.Count > 1 ? args[1] : "");
            arguments = args.Count > 2 ? ScriptText.SplitCommandLine(Text(args[2])) : [];
        }
        else
        {
            var parts = ScriptText.SplitCommandLine(Text(args[0]));
            if (parts.Count == 0) return;
            program = parts[0];
            arguments = parts.Skip(1).ToList();
        }
        var what = $"{line.Command} {Canonical.Display(ctx.Resolve(program))}{(arguments.Count > 0 ? " " + string.Join(' ', arguments.Select(a => Canonical.Display(ctx.Resolve(a)))) : "")}";
        if (ImportHelpers.TryPrerequisite(ctx, program, line.At)) return;
        if (ImportHelpers.TrySystemCommand(ctx, program, arguments, b.Uninstall, line.At)) return;
        if (!ctx.TryInstallRelative(program, out var rel) || rel.Length == 0 || !rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Manual(line.At, $"{what}: FluentSetup runs only .exe files among the app's installed files (no shell, URLs or system tools). Not imported.", "actions");
            return;
        }
        var converted = new List<string>();
        foreach (var a in arguments)
        {
            if (Canonical.AsValue(a) is not { } v) { ctx.Manual(line.At, $"{what}: an argument uses a folder or constant FluentSetup cannot fill in. Not imported.", "actions"); return; }
            converted.Add(v);
        }
        // The main program is known only after all sections ran (it comes from the shortcuts): decided in ApplyPrograms.
        _programs.Add((line.At, what, rel, converted, b.Uninstall, cmd is "exec" or "execshell", state.Conditional, b.Candidate));
    }

    private readonly List<(SourceRef At, string What, string Run, List<string> Arguments, bool Uninstall, bool NoWait, bool Conditional, string Candidate)> _programs = [];

    /// <summary>Programs of the app the sections run: actions, or the app's relaunch / close command.</summary>
    private void ApplyPrograms()
    {
        var p = ctx.Project;
        foreach (var x in _programs)
        {
            var isMain = ctx.MainExecutable.Length > 0 && ImportContext.SamePath(x.Run, ctx.MainExecutable);
            if (ImportHelpers.TryAppLaunch(ctx, isMain, x.Uninstall, x.NoWait, x.Arguments, x.What, x.At)) continue;
            if (p.Actions.Count >= 32) { ctx.Manual(x.At, $"{x.What} is not imported: at most 32 actions.", "actions"); continue; }
            var action = new ActionSettings
            {
                When = x.Uninstall ? "beforeUninstall" : "afterInstall", Run = x.Run, Arguments = x.Arguments, OnFailure = "warn",
                IfOption = x.Uninstall ? "" : ctx.UseOption(x.Candidate),
            };
            p.Actions.Add(action);
            ctx.Warn(x.At, $"{x.What} → actions[{p.Actions.Count - 1}] ({action.When}, onFailure \"warn\"): check the exit codes it can return (successExitCodes) and whether a failure should roll the installation back (\"fail\").", "actions");
            if (x.Conditional) ctx.Warn(x.At, $"{x.What} depended on a script condition; it always runs{(action.IfOption.Length > 0 ? " with its option" : "")}.", "actions");
        }
    }

    private void DeleteCommand(Line line, Block b, SectionState state)
    {
        var args = line.Tokens.Skip(1).ToList();
        var recursive = args.RemoveAll(a => a.Equals("/r", StringComparison.OrdinalIgnoreCase)) > 0;
        args.RemoveAll(a => a.Equals("/REBOOTOK", StringComparison.OrdinalIgnoreCase));
        if (args.Count == 0) return;
        var isDir = line.Command.Equals("RMDir", StringComparison.OrdinalIgnoreCase);
        var path = Text(args[0]);
        var resolved = ctx.Resolve(path);
        var what = $"{line.Command}{(recursive ? " /r" : "")} {Plain(args[0])}";
        var p = ctx.Project;
        if (!b.Uninstall)
        {
            if (!isDir && ctx.TryInstallRelativePattern(path, out var pattern) && Validation.ExtensionValidator.IsFilePattern(pattern))
            {
                if (!p.Install.Cleanup.RemoveFiles.Contains(pattern, StringComparer.OrdinalIgnoreCase)) p.Install.Cleanup.RemoveFiles.Add(pattern);
                ctx.Info(line.At, $"{what} → install.cleanup.removeFiles (removed after the new files are placed)", "install.cleanup.removeFiles");
            }
            else ctx.Manual(line.At, $"{what}: deleting files or folders while installing is not imported (install.cleanup.removeFiles takes files in the install folder).", "install.cleanup.removeFiles");
            return;
        }
        if (resolved.StartsWith("{Programs}", StringComparison.OrdinalIgnoreCase) || resolved.StartsWith("{Desktop}", StringComparison.OrdinalIgnoreCase) ||
            resolved.StartsWith("{Startup}", StringComparison.OrdinalIgnoreCase))
        {
            state.Ignored.Add($"{what} (line {line.At.Line})");  // shortcuts: removed by FluentSetup
            return;
        }
        if (ctx.TryInstallRelative(path, out var rel) || ctx.TryInstallRelativePattern(path, out rel))
        {
            var top = rel.Split('/')[0];
            // A folder the script created empty (logs, cache) fills with the app's files: remove it. Anything that may
            // come from the package is removed by FluentSetup anyway.
            var created = ctx.EmptyDirectories.Any(d => d.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (!created && (rel.Length == 0 || rel.Contains('*') || rel.Equals(_uninstallerName, StringComparison.OrdinalIgnoreCase) ||
                ctx.Files.Any(f => f.DestDir.Length > 0 ? f.DestDir.Split('/')[0].Equals(top, StringComparison.OrdinalIgnoreCase) : Path.GetFileName(f.Source).Equals(top, StringComparison.OrdinalIgnoreCase) || IsWildcardFile(f))))
            {
                state.Ignored.Add($"{what} (line {line.At.Line})");
                return;
            }
            var template = @"{InstallDir}\" + rel.Replace('/', '\\');
            if (!p.Uninstall.RemoveData.Contains(template, StringComparer.OrdinalIgnoreCase)) p.Uninstall.RemoveData.Add(template);
            ctx.Info(line.At, $"{what} → uninstall.removeData \"{template}\" (something the app creates in its folder)", "uninstall.removeData");
            return;
        }
        if (Canonical.AsDataTemplate(resolved) is { } data)
        {
            var userData = b.Candidate.Length > 0 || (!b.Hidden && _unPages.Contains("COMPONENTS"));
            if (userData)
            {
                if (!p.Uninstall.UserData.Paths.Contains(data, StringComparer.OrdinalIgnoreCase)) p.Uninstall.UserData.Paths.Add(data);
                p.Uninstall.UserData.Default = !b.Unselected;
                ctx.Info(line.At, $"{what} in optional uninstall section \"{SectionName(b)}\" → uninstall.userData.paths (a checkbox on the uninstall page)", "uninstall.userData");
            }
            else
            {
                if (!p.Uninstall.RemoveData.Contains(data, StringComparer.OrdinalIgnoreCase)) p.Uninstall.RemoveData.Add(data);
                ctx.Info(line.At, $"{what} → uninstall.removeData \"{data}\"", "uninstall.removeData");
            }
            return;
        }
        ctx.Manual(line.At, $"{what}: FluentSetup removes {{InstallDir}}\\X, {{LocalAppData}}\\X, {{AppData}}\\X or {{CommonAppData}}\\X (no wildcards). Not imported.", "uninstall.removeData");
    }

    private static bool IsWildcardFile(FileEntry f) => f.DestDir.Length == 0 && Path.GetFileName(f.Source).IndexOfAny(['*', '?']) >= 0 && f.Recurse;

    // ---- identity, folders, main program -------------------------------------------------------------------------

    private const string UninstallKeyPrefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    private void ApplyIdentity()
    {
        var p = ctx.Project;
        var uninstall = ctx.Registry.Where(w => w.Name is not null && ctx.Resolve(w.Key).StartsWith(UninstallKeyPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        string Value(string name) => uninstall.FirstOrDefault(w => ctx.Resolve(w.Name!).Equals(name, StringComparison.OrdinalIgnoreCase))?.Data ?? "";
        SourceRef ValueAt(string name) => uninstall.FirstOrDefault(w => ctx.Resolve(w.Name!).Equals(name, StringComparison.OrdinalIgnoreCase))?.At ?? ctx.Script;

        if (Attr("Name") is { } name)
        {
            _resolvingName = true;  // $(^Name) inside Name itself refers to nothing
            p.App.Name = Text(name.Value);
            _resolvingName = false;
            ctx.Info(name.At, "Name → app.name", "app.name");
        }
        else if (Value("DisplayName") is { Length: > 0 } dn) { p.App.Name = dn; ctx.Info(ValueAt("DisplayName"), "DisplayName → app.name", "app.name"); }
        else ctx.Manual(ctx.Script, "The script has no Name; set app.name.", "app.name");

        var versionDefine = ctx.Variables.Keys.FirstOrDefault(k => k.Equals("VERSION", StringComparison.OrdinalIgnoreCase) || k.Equals("PRODUCT_VERSION", StringComparison.OrdinalIgnoreCase) || k.Equals("APP_VERSION", StringComparison.OrdinalIgnoreCase) || k.Equals("APPVERSION", StringComparison.OrdinalIgnoreCase))
                            ?? ctx.Variables.Keys.FirstOrDefault(k => k.EndsWith("VERSION", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("MUI", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("NSIS", StringComparison.OrdinalIgnoreCase));
        if (Value("DisplayVersion") is { Length: > 0 } dv) ImportHelpers.SetVersion(ctx, dv, ValueAt("DisplayVersion"), "DisplayVersion");
        else if (_versionKeys.TryGetValue("ProductVersion", out var pv)) ImportHelpers.SetVersion(ctx, Text(pv.Value), pv.At, "VIAddVersionKey ProductVersion");
        else if (Attr("VIProductVersion") is { } vi) ImportHelpers.SetVersion(ctx, Text(vi.Value), vi.At, "VIProductVersion");
        else if (versionDefine is not null && ProjectVariables.IsValidName(versionDefine)) ImportHelpers.SetVersion(ctx, $"$({versionDefine})", ctx.VariableNotes.TryGetValue(versionDefine, out var vn) ? vn.At : ctx.Script, $"!define {versionDefine}");
        else ctx.Manual(ctx.Script, "No version found (DisplayVersion, VIProductVersion or a VERSION define); app.version is 1.0.0.", "app.version");

        if (Value("Publisher") is { Length: > 0 } pub) { p.App.Publisher = pub; ctx.Info(ValueAt("Publisher"), "Publisher → app.publisher", "app.publisher"); }
        else if (_versionKeys.TryGetValue("CompanyName", out var cn)) { p.App.Publisher = Text(cn.Value); ctx.Info(cn.At, "VIAddVersionKey CompanyName → app.publisher", "app.publisher"); }
        else if (ctx.Variables.Keys.FirstOrDefault(k => (k.Contains("COMPANY", StringComparison.OrdinalIgnoreCase) || k.Contains("PUBLISHER", StringComparison.OrdinalIgnoreCase)) && ProjectVariables.IsValidName(k)) is { } pk)
        {
            p.App.Publisher = $"$({pk})";
            ctx.Info(ctx.VariableNotes.TryGetValue(pk, out var pn) ? pn.At : ctx.Script, $"!define {pk} → app.publisher", "app.publisher");
        }
        if (_versionKeys.TryGetValue("Comments", out var comments) || _versionKeys.TryGetValue("FileDescription", out comments))
        {
            p.App.Description = Text(comments.Value);
            ctx.Info(comments.At, "VIAddVersionKey Comments/FileDescription → app.description", "app.description");
        }
        var url = new[] { "URLInfoAbout", "HelpLink", "URLUpdateInfo" }.FirstOrDefault(n => Value(n).Length > 0);
        if (url is not null) ImportHelpers.SetWebsite(ctx, Value(url), ValueAt(url), url);
        else if (ctx.Variables.Keys.FirstOrDefault(k => (k.Contains("URL", StringComparison.OrdinalIgnoreCase) || k.Contains("WEBSITE", StringComparison.OrdinalIgnoreCase)) && !k.StartsWith("MUI", StringComparison.OrdinalIgnoreCase) && ProjectVariables.IsValidName(k)) is { } uk)
            ImportHelpers.SetWebsite(ctx, $"$({uk})", ctx.VariableNotes.TryGetValue(uk, out var un) ? un.At : ctx.Script, $"!define {uk}");

        var key = uninstall.Select(w => ctx.Resolve(w.Key)[UninstallKeyPrefix.Length..].Split('\\')[0]).FirstOrDefault(k => k.Length > 0);
        if (key is not null)
        {
            var at = uninstall[0].At;
            if (PathRules.IsValidAppId(key))
            {
                p.App.Id = key;
                p.Install.AdoptExistingInstall = true;
                ctx.Info(at, $"Uninstall key \"{key}\" → app.id \"{key}\" with install.adoptExistingInstall (existing installations are upgraded in place)", "app.id");
            }
            else
            {
                p.App.Id = ImportHelpers.DeriveAppId(ctx.Resolve(p.App.Publisher), ctx.Resolve(p.App.Name), ctx.ScriptPath);
                p.Install.LegacyUninstallKeys.Add(key);
                ctx.Info(at, $"Uninstall key \"{key}\" is not a valid app id → install.legacyUninstallKeys (adopted, then removed); app.id is \"{p.App.Id}\"", "install.legacyUninstallKeys");
            }
            foreach (var w in uninstall) w.Consumed = true;
            ctx.Info(at, $"The uninstall entry's values ({string.Join(", ", uninstall.Select(w => ctx.Resolve(w.Name!)).Distinct(StringComparer.OrdinalIgnoreCase))}) are written by FluentSetup from app.*.");
        }
        else
        {
            p.App.Id = ImportHelpers.DeriveAppId(ctx.Resolve(p.App.Publisher), ctx.Resolve(p.App.Name), ctx.ScriptPath);
            ctx.Warn(ctx.Script, $"The script writes no uninstall entry the importer could find; app.id is \"{p.App.Id}\". If older versions registered one, add its name to install.legacyUninstallKeys.", "app.id");
        }
        foreach (var (old, at) in _uninstallKeys)
        {
            if (old.Equals(p.App.Id, StringComparison.OrdinalIgnoreCase) || p.Install.LegacyUninstallKeys.Contains(old, StringComparer.OrdinalIgnoreCase) || old.Length == 0) continue;
            p.Install.Cleanup.RemoveUninstallKeys.Add(old);
            ctx.Info(at, $"DeleteRegKey of the uninstall entry \"{old}\" (an older product) → install.cleanup.removeUninstallKeys", "install.cleanup.removeUninstallKeys");
        }
        if (_uninstallerName.Length > 0 && !_uninstallerName.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase) && ExtensionValidatorFilePattern(_uninstallerName))
        {
            p.Install.Cleanup.RemoveFiles.Add(_uninstallerName);
            ctx.Info(ctx.Script, $"The old uninstaller {_uninstallerName} is removed from adopted installations → install.cleanup.removeFiles", "install.cleanup.removeFiles");
        }
    }

    private static bool ExtensionValidatorFilePattern(string name) => Validation.ExtensionValidator.IsFilePattern(name);

    private void ApplyFolders()
    {
        var p = ctx.Project;
        if (Attr("InstallDirRegKey") is { } reg)
            ctx.Info(reg.At, "InstallDirRegKey: FluentSetup finds an existing installation through its uninstall entry (and the adopted one).");
        if (Attr("InstallDir") is not { } dir) { ctx.Manual(ctx.Script, "The script has no InstallDir; the install folders are FluentSetup's defaults.", "install.userDir"); return; }
        var text = Text(dir.Value).Replace('/', '\\');
        string? Rest(string prefix)
        {
            var rest = text[prefix.Length..];
            if (Canonical.IsUnsupported(rest) || ctx.HasUnknownVariable(rest)) return null;
            var appName = ctx.Resolve(p.App.Name);
            var publisher = ctx.Resolve(p.App.Publisher);
            var segments = ctx.Resolve(rest).Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(s =>
                s.Equals(appName, StringComparison.Ordinal) && appName.Length > 0 ? "{AppName}" :
                s.Equals(publisher, StringComparison.Ordinal) && publisher.Length > 0 ? "{Publisher}" :
                s.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal));
            var joined = string.Join('\\', segments);
            return joined.Length == 0 ? "" : "\\" + joined;
        }
        string? user = null, machine = null;
        if (text.StartsWith(@"{LocalAppData}\Programs", StringComparison.OrdinalIgnoreCase) && Rest(@"{LocalAppData}\Programs") is { } r1) { user = @"{LocalAppData}\Programs" + r1; machine = "{ProgramFiles}" + r1; }
        else if (text.StartsWith("{ProgramFiles}", StringComparison.OrdinalIgnoreCase) && Rest("{ProgramFiles}") is { } r2) { machine = "{ProgramFiles}" + r2; user = @"{LocalAppData}\Programs" + r2; }
        else if (text.StartsWith("{LocalAppData}", StringComparison.OrdinalIgnoreCase) && Rest("{LocalAppData}") is { } r3) user = "{LocalAppData}" + r3;
        else if (text.StartsWith("{AppData}", StringComparison.OrdinalIgnoreCase) && Rest("{AppData}") is { } r4) user = "{AppData}" + r4;
        else if (text.StartsWith("{CommonAppData}", StringComparison.OrdinalIgnoreCase) && Rest("{CommonAppData}") is { } r5) machine = "{ProgramData}" + r5;
        else
        {
            ctx.Manual(dir.At, $"InstallDir \"{Canonical.Display(ctx.Resolve(text))}\" cannot be converted; the install folders are FluentSetup's defaults.", "install.userDir");
            return;
        }
        if (user is not null) p.Install.UserDir = user;
        if (machine is not null) p.Install.MachineDir = machine;
        if (p.Install.Scope != "user" && machine is null) ctx.Warn(dir.At, $"InstallDir is in the user's profile; a per-machine install goes to {p.Install.MachineDir} instead.", "install.machineDir");
        if (p.Install.Scope != "machine" && user is null) ctx.Warn(dir.At, $"InstallDir is not in the user's profile; a per-user install goes to {p.Install.UserDir} instead.", "install.userDir");
        ctx.Info(dir.At, $"InstallDir → install.userDir \"{p.Install.UserDir}\", install.machineDir \"{p.Install.MachineDir}\"", "install.userDir");

        if (Attr("OutFile") is { } outFile)
        {
            var raw = Text(outFile.Value).Replace('/', '\\');
            var slash = raw.LastIndexOf('\\');
            var fileName = slash >= 0 ? raw[(slash + 1)..] : raw;
            if (!Canonical.IsUnsupported(fileName))
            {
                p.Output.FileName = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";
                ctx.Info(outFile.At, "OutFile → output.fileName (in-app updaters that download a fixed name keep working)", "output.fileName");
            }
            if (slash >= 0 && !Canonical.IsUnsupported(raw[..slash]))
            {
                p.Output.Directory = ctx.ProjectPathOf(raw[..slash]);
                ctx.Info(outFile.At, "OutFile folder → output.directory", "output.directory");
            }
            else p.Output.Directory = ctx.ProjectPath(ctx.ScriptDirectory);
        }
        var icon = ctx.Variables.TryGetValue("MUI_ICON", out var muiIcon) && muiIcon is not null ? (muiIcon, ctx.VariableNotes.TryGetValue("MUI_ICON", out var mi) ? mi.At : ctx.Script, "MUI_ICON")
                 : Attr("Icon") is { } ic ? (ic.Value, ic.At, "Icon") : default;
        if (icon.Item1 is { Length: > 0 } iconPath)
        {
            var iconText = Text(iconPath);
            if (!Canonical.IsUnsupported(iconText))
            {
                p.App.Icon = ctx.ProjectPathOf(iconText);
                ctx.Info(icon.Item2, $"{icon.Item3} → app.icon", "app.icon");
            }
        }
        if (Attr("LicenseData") is { } lic)
        {
            var licText = Text(lic.Value);
            if (licText.StartsWith("{?lang:", StringComparison.Ordinal) || lic.Value.StartsWith("$(", StringComparison.Ordinal))
            {
                if (p.Ui.Licenses.Count > 0) p.Ui.License = p.Ui.Licenses.TryGetValue("en-US", out var en) ? en : p.Ui.Licenses.Values.First();
            }
            else if (!Canonical.IsUnsupported(licText))
            {
                p.Ui.License = ctx.ProjectPathOf(licText);
                ctx.Info(lic.At, "License page → ui.license (the user accepts it before installing)", "ui.license");
                if (p.Ui.License.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase)) ctx.Manual(lic.At, $"{p.Ui.License} is RTF; FluentSetup shows plain text. Save it as .txt and update ui.license.", "ui.license");
            }
        }
        if (!_pages.Contains("DIRECTORY") && _pages.Count > 0) ctx.Info(ctx.Script, "The script has no directory page; FluentSetup lets users choose the folder (set install.allowCustomDir false to prevent it).", "install.allowCustomDir");
    }

    private void FindMainExecutable()
    {
        var appName = ctx.Resolve(ctx.Project.App.Name);
        string? From(string canonical) => ctx.TryInstallRelative(canonical, out var rel) && rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rel : null;
        (string? Exe, SourceRef At, string How) found = (null, ctx.Script, "");
        foreach (var s in ctx.Shortcuts.Where(s => s.Location == "startMenu" && s.Arguments.Trim().Length == 0))
        {
            if (From(s.Target) is not { } exe) continue;
            if (found.Exe is null || ctx.Resolve(s.Name).Equals(appName, StringComparison.OrdinalIgnoreCase)) found = (exe, s.At, "the Start menu shortcut");
            if (ctx.Resolve(s.Name).Equals(appName, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (found.Exe is null && ctx.Variables.TryGetValue("MUI_FINISHPAGE_RUN", out var run) && run is not null && From(Text(run)) is { } r)
            found = (r, ctx.VariableNotes.TryGetValue("MUI_FINISHPAGE_RUN", out var n) ? n.At : ctx.Script, "MUI_FINISHPAGE_RUN");
        if (found.Exe is null && ctx.Registry.FirstOrDefault(w => w.Name is not null && ctx.Resolve(w.Name).Equals("DisplayIcon", StringComparison.OrdinalIgnoreCase)) is { } di &&
            From(ctx.Resolve(di.Data).Split(',')[0].Trim('"')) is { } fromIcon)
            found = (fromIcon, di.At, "the uninstall entry's DisplayIcon");
        if (found.Exe is null)
            foreach (var s in ctx.Shortcuts)
                if (From(s.Target) is { } exe) { found = (exe, s.At, "a shortcut"); break; }
        if (found.Exe is null)
        {
            var exes = ctx.Files.Where(f => f.DestDir.Length == 0 && f.DestName.Length == 0 && f.Source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !f.Recurse).ToList();
            if (exes.Count == 1) found = (Path.GetFileName(exes[0].Source), exes[0].At, "the only program the script installs");
        }
        if (found.Exe is null)
        {
            ctx.Manual(ctx.Script, "The app's main program could not be determined; set app.mainExecutable (shortcuts and \"launch\" need it).", "app.mainExecutable");
            return;
        }
        ctx.MainExecutable = found.Exe;
        ctx.Project.App.MainExecutable = found.Exe;
        ctx.Info(found.At, $"app.mainExecutable \"{found.Exe}\" (from {found.How})", "app.mainExecutable");
    }

    private void ApplyFinishPage()
    {
        var p = ctx.Project;
        if (!ctx.Variables.TryGetValue("MUI_FINISHPAGE_RUN", out var run) || run is null)
        {
            if (ctx.Variables.TryGetValue("MUI_FINISHPAGE_RUN_FUNCTION", out var fn) && fn is not null)
                ctx.Manual(ctx.VariableNotes.TryGetValue("MUI_FINISHPAGE_RUN_FUNCTION", out var n0) ? n0.At : ctx.Script, $"The finish page runs function {fn}; set install.launchAfterInstall if it launches the app.", "install.launchAfterInstall");
            return;
        }
        var at = ctx.VariableNotes.TryGetValue("MUI_FINISHPAGE_RUN", out var n) ? n.At : ctx.Script;
        if (run.Length == 0 && ctx.Variables.TryGetValue("MUI_FINISHPAGE_RUN_FUNCTION", out var fn2) && fn2 is not null)
        {
            p.Install.LaunchAfterInstall = ctx.Variables.ContainsKey("MUI_FINISHPAGE_RUN_NOTCHECKED") ? "optionalOff" : "optionalOn";
            ctx.Warn(at, $"The finish page's \"run\" checkbox calls function {fn2} → install.launchAfterInstall \"{p.Install.LaunchAfterInstall}\" (launches app.mainExecutable; check what the function did).", "install.launchAfterInstall");
            return;
        }
        if (ctx.TryInstallRelative(Text(run), out var rel) && ctx.MainExecutable.Length > 0 && ImportContext.SamePath(rel, ctx.MainExecutable))
        {
            p.Install.LaunchAfterInstall = ctx.Variables.ContainsKey("MUI_FINISHPAGE_RUN_NOTCHECKED") ? "optionalOff" : "optionalOn";
            ctx.Info(at, $"MUI_FINISHPAGE_RUN → install.launchAfterInstall \"{p.Install.LaunchAfterInstall}\" (never in quiet installs)", "install.launchAfterInstall");
            if (ctx.Variables.TryGetValue("MUI_FINISHPAGE_RUN_PARAMETERS", out var args) && args is { Length: > 0 })
                ctx.Warn(at, $"The app is launched without MUI_FINISHPAGE_RUN_PARAMETERS ({args}).", "install.launchAfterInstall");
        }
        else ctx.Manual(at, $"MUI_FINISHPAGE_RUN {Plain(run)}: FluentSetup offers to launch app.mainExecutable only.", "install.launchAfterInstall");
        foreach (var key in new[] { "MUI_FINISHPAGE_SHOWREADME", "MUI_FINISHPAGE_LINK" })
            if (ctx.Variables.TryGetValue(key, out var v) && v is not null) ctx.Info(ctx.VariableNotes.TryGetValue(key, out var kn) ? kn.At : ctx.Script, $"{key}: the finish page has no readme or link option.");
    }

    // ---- functions --------------------------------------------------------------------------------------------

    private static readonly string[] Callbacks =
    [
        ".onInit", ".onGUIInit", ".onGUIEnd", ".onInstSuccess", ".onInstFailed", ".onUserAbort", ".onVerifyInstDir", ".onSelChange", ".onMouseOverSection",
        ".onRebootFailed", ".onUserAbort", "un.onInit", "un.onGUIInit", "un.onGUIEnd", "un.onUninstSuccess", "un.onUninstFailed", "un.onUserAbort", "un.onRebootFailed",
    ];

    private void ReportFunctions()
    {
        var unused = new List<string>();
        foreach (var f in _blocks.Where(b => b.Kind == "function"))
        {
            var isCallback = Callbacks.Contains(f.Name, StringComparer.OrdinalIgnoreCase);
            var called = _blocks.Any(b => b.Lines.Any(l => l.Command.Equals("Call", StringComparison.OrdinalIgnoreCase) && l.Arg(0).Equals(f.Name, StringComparison.OrdinalIgnoreCase)));
            var page = _pageFunctions.Contains(f.Name) || ctx.Variables.Values.Any(v => v is not null && v.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
            if (!isCallback && !page)
            {
                if (!called) unused.Add(f.Name);
                continue;  // reported where it is called
            }
            var hints = Hints(f);
            ctx.Manual(f.At, $"Function {f.Name} ({f.Lines.Count} lines) is not imported{(isCallback ? "" : " (used by a page)")}." + (hints.Count > 0 ? " It " + string.Join("; ", hints) + "." : ""));
        }
        if (unused.Count > 0) ctx.Info(ctx.Script, $"Functions that nothing calls directly (called from macros or plug-ins?): {string.Join(", ", unused.Take(20))}; not imported.");
    }

    private List<string> Hints(Block f)
    {
        var hints = new List<string>();
        bool Has(Func<Line, bool> test) => f.Lines.Any(test);
        bool Cmd(string name) => Has(l => l.Command.Equals(name, StringComparison.OrdinalIgnoreCase));
        bool Text(string pattern) => Has(l => l.Tokens.Any(t => (Expand(t) ?? t).Contains(pattern, StringComparison.OrdinalIgnoreCase)));
        if (Text("CreateMutex")) hints.Add("prevents two setups at once (FluentSetup does that itself)");
        if (Cmd("UserInfo::GetAccountType")) hints.Add("checks for administrator rights (install.scope decides that)");
        if (Text(@"CurrentVersion\Uninstall") && (Cmd("ReadRegStr") || Cmd("ExecWait"))) hints.Add("looks for or runs a previous version's uninstaller (FluentSetup upgrades in place through app.id / install.legacyUninstallKeys)");
        if (Has(l => l.Command.Equals("StrCpy", StringComparison.OrdinalIgnoreCase) && l.Arg(0).Equals("$INSTDIR", StringComparison.OrdinalIgnoreCase))) hints.Add("sets the install folder (install.userDir/machineDir)");
        if (Cmd("SetShellVarContext")) hints.Add("chooses all users or the current user (install.scope)");
        if (Text("GetParameters") || Text("GetOptions")) hints.Add("reads command-line switches (Setup.exe understands /S, /D= and /UPDATE)");
        if (Cmd("ExecWait") || Cmd("Exec") || Cmd("nsExec::ExecToLog") || Cmd("nsExec::Exec")) hints.Add("runs programs (actions[] for the app's own programs)");
        if (Cmd("MessageBox")) hints.Add("shows message boxes");
        if (Text("nsProcess") || Text("FindProcDLL") || Text("taskkill") || Cmd("FindWindow")) hints.Add("closes or waits for the running app (install.closeProcesses)");
        if (Cmd("ReadRegStr") && Text("NET Framework Setup")) hints.Add("checks for .NET Framework (requirements.netFramework)");
        if (Text("WebView2") || Text("F3017226-FE2A-4295-8BDF-00C3A9A7E4C5")) hints.Add("checks for WebView2 (requirements.webView2)");
        if (Text("VC\\Runtimes") || Text("vc_redist")) hints.Add("checks for the Visual C++ runtime (requirements.vcRedist)");
        if (Text("RunningX64") || Text("IsNativeARM64")) hints.Add("checks the architecture (output.architecture; build one package per architecture)");
        if (Cmd("RMDir") || Cmd("Delete")) hints.Add("deletes files or folders (uninstall.removeData)");
        if (Cmd("Abort") || Cmd("Quit")) hints.Add("can stop setup");
        return hints;
    }
}
