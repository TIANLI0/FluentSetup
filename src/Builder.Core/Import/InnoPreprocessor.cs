using System.Globalization;
using System.Text;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Import;

/// <summary>
/// The part of Inno Setup's preprocessor (ISPP) that installer scripts commonly use: #define/#undef, #include,
/// #if/#ifdef/#ifndef/#ifexist/#elif/#else/#endif and {#...} inline expressions with string functions. A {#Name} of a
/// #define becomes $(Name), so the project keeps the variable (for -D on the command line); other expressions are
/// evaluated. What cannot be evaluated is reported.
/// </summary>
internal sealed class InnoPreprocessor(ImportContext ctx)
{
    private readonly List<ScriptLine> _output = [];
    private readonly HashSet<string> _builtIns = new(StringComparer.OrdinalIgnoreCase) { "SourcePath", "CompilerPath", "__FILE__", "__PATHFILENAME__", "__DIR__", "__LINE__", "Ver", "PREPROCVER", "WINDOWS", "UNICODE", "ISPP_INVOKED", "__OPT_E__" };
    private int _files;
    private int _lines;
    private ImportContext Ctx => ctx;

    public List<ScriptLine> Run()
    {
        Process(ctx.ScriptPath, 0);
        return _output;
    }

    /// <summary>An ISPP predefined name the script did not redefine.</summary>
    private bool IsBuiltIn(string name) => _builtIns.Contains(name) && !ctx.Variables.ContainsKey(name);

    /// <summary>The value of a predefined name; "" = not known here (Ver, CompilerPath …).</summary>
    private string BuiltIn(string name, SourceRef at) => name.ToUpperInvariant() switch
    {
        "SOURCEPATH" => ctx.ScriptDirectory + Path.DirectorySeparatorChar,
        "__FILE__" => Path.GetFileName(at.File),
        "__PATHFILENAME__" => at.File,
        "__DIR__" => Path.GetDirectoryName(at.File) + Path.DirectorySeparatorChar,
        "__LINE__" => at.Line.ToString(CultureInfo.InvariantCulture),
        "WINDOWS" or "UNICODE" or "ISPP_INVOKED" => "1",
        _ => "",
    };

    private sealed class Frame
    {
        public bool Parent;   // the enclosing block is active
        public bool Active;   // this branch is active
        public bool Taken;    // some branch of this #if was taken
    }

    private void Process(string file, int depth)
    {
        if (++_files > ScriptText.MaxFiles) { ctx.Warn(new SourceRef(file, 0), $"More than {ScriptText.MaxFiles} files are included; the rest is not read."); return; }
        var lines = ScriptText.ReadLines(file, out var legacy);
        if (legacy) ctx.Info(new SourceRef(file, 0), $"{Path.GetFileName(file)} is not UTF-8; it was read as Windows-1252. Check non-English text in the project.");
        var stack = new Stack<Frame>();
        bool Active() => stack.Count == 0 || (stack.Peek().Parent && stack.Peek().Active);
        var inSub = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (++_lines > ScriptText.MaxLines) { ctx.Warn(new SourceRef(file, i + 1), $"The script has more than {ScriptText.MaxLines} lines; the rest is not read."); return; }
            var at = new SourceRef(file, i + 1);
            var text = lines[i];
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith('#'))
            {
                if (Active() && !inSub) _output.Add(new ScriptLine(at, Substitute(text, at)));
                continue;
            }
            // Directive; "\" at the end continues it on the next line.
            var directive = trimmed[1..].Trim();
            while (directive.EndsWith('\\') && i + 1 < lines.Length) directive = directive[..^1] + " " + lines[++i].Trim();
            var nameEnd = 0;
            while (nameEnd < directive.Length && char.IsAsciiLetter(directive[nameEnd])) nameEnd++;
            var name = directive[..nameEnd].ToLowerInvariant();
            var rest = directive[nameEnd..].Trim();
            switch (name)
            {
                case "if" or "ifdef" or "ifndef" or "ifexist" or "ifnexist":
                {
                    var parent = Active();
                    var value = parent && Condition(name, rest, at);
                    stack.Push(new Frame { Parent = parent, Active = value, Taken = value });
                    continue;
                }
                case "elif":
                    if (stack.Count == 0) { ctx.Warn(at, "#elif without #if; ignored."); continue; }
                    {
                        var f = stack.Peek();
                        f.Active = !f.Taken && f.Parent && Condition("if", rest, at);
                        f.Taken |= f.Active;
                    }
                    continue;
                case "else":
                    if (stack.Count == 0) { ctx.Warn(at, "#else without #if; ignored."); continue; }
                    {
                        var f = stack.Peek();
                        f.Active = !f.Taken;
                        f.Taken = true;
                    }
                    continue;
                case "endif":
                    if (stack.Count == 0) ctx.Warn(at, "#endif without #if; ignored.");
                    else stack.Pop();
                    continue;
            }
            if (!Active()) continue;
            switch (name)
            {
                case "define" or "dim":
                    Define(rest, at);
                    break;
                case "undef":
                    foreach (var n in rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(n => n is not ("public" or "private" or "protected"))) ctx.Variables.Remove(n);
                    break;
                case "include":
                    Include(rest, at, depth);
                    break;
                case "sub":
                    inSub = true;
                    ctx.Manual(at, "ISPP #sub procedures are not evaluated; their output is missing from the import.");
                    break;
                case "endsub":
                    inSub = false;
                    break;
                case "for" or "emit" or "expr" or "insert" or "append" or "file" or "redim" or "exec":
                    ctx.Manual(at, $"ISPP #{name} is not evaluated; check what it generates and add that to the project.");
                    break;
                case "error":
                    ctx.Warn(at, $"The script stops here when compiled: #error {rest}");
                    break;
                case "pragma" or "preproc":
                    ctx.Info(at, $"#{name} {rest} (preprocessor setting) is ignored.");
                    break;
                default:
                    ctx.Info(at, $"Preprocessor line \"#{directive}\" is ignored.");
                    break;
            }
        }
        if (stack.Count > 0) ctx.Warn(new SourceRef(file, lines.Length), $"{stack.Count} #if block(s) are not closed with #endif.");
    }

    private bool Condition(string kind, string rest, SourceRef at)
    {
        switch (kind)
        {
            case "ifdef": return ctx.Variables.ContainsKey(rest.Trim()) || _builtIns.Contains(rest.Trim());
            case "ifndef": return !(ctx.Variables.ContainsKey(rest.Trim()) || _builtIns.Contains(rest.Trim()));
            case "ifexist" or "ifnexist":
            {
                var v = Evaluate(rest, at);
                if (v is not string path)
                {
                    ctx.Warn(at, $"#{kind} {rest}: cannot tell; assumed {(kind == "ifexist" ? "it exists" : "it does not exist")}.");
                    return true;
                }
                var exists = File.Exists(ctx.ScriptFile(path)) || Directory.Exists(ctx.ScriptFile(path));
                return kind == "ifexist" ? exists : !exists;
            }
            default:
            {
                var v = Evaluate(rest, at);
                if (v is null)
                {
                    ctx.Warn(at, $"#if {rest}: the condition cannot be evaluated here; the first branch is imported.");
                    return true;
                }
                return Truthy(v);
            }
        }
    }

    private static bool Truthy(object v) => v switch { bool b => b, long n => n != 0, string s => s.Length > 0, _ => false };

    private void Define(string rest, SourceRef at)
    {
        var text = rest;
        foreach (var visibility in new[] { "public ", "private ", "protected " })
            if (text.StartsWith(visibility, StringComparison.OrdinalIgnoreCase)) text = text[visibility.Length..].TrimStart();
        var end = 0;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
        var name = text[..end];
        if (name.Length == 0) { ctx.Warn(at, $"#define {rest}: no name; ignored."); return; }
        var expr = text[end..].Trim();
        if (expr.StartsWith('('))
        {
            ctx.Variables[name] = null;
            ctx.Info(at, $"ISPP macro {name}(…) is not evaluated; lines that call it are reported.");
            return;
        }
        if (expr.StartsWith('=')) expr = expr[1..].Trim();
        if (expr.Length == 0)
        {
            ctx.Variables[name] = "";
            return;
        }
        var value = Evaluate(expr, at, out var fileVersionOf);
        if (Template(expr) is { } template) _templates[name] = template;
        else _templates.Remove(name);
        if (value is null)
        {
            ctx.Variables[name] = null;
            if (!_templates.ContainsKey(name)) ctx.VariableNotes[name] = (at, ImportSeverity.Manual, $"#define {name} {expr} is computed when the script is compiled.");
            return;
        }
        var s = Format(value);
        ctx.Variables[name] = s;
        ctx.VariableNotes[name] = fileVersionOf is null
            ? (at, ImportSeverity.Info, $"#define {name} → variables.{name} = \"{s}\"")
            : (at, ImportSeverity.Warning, $"#define {name} reads the version of {fileVersionOf}; it is {s} now. Pass the version of each release with -D {name}=… (or update variables.{name}).");
    }

    private static string Format(object v) => v switch { bool b => b ? "1" : "0", long n => n.ToString(CultureInfo.InvariantCulture), _ => (string)v };

    /// <summary>Defines built by concatenation, as project text: Staged = "dist\App-" + Version → "dist\App-$(Version)".</summary>
    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The template of "literal" + Name + … (other defines kept as $(Name)); null for anything else.</summary>
    private string? Template(string expr)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in expr)
        {
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            if (c == '+') { parts.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            if (c is '(' or ')' or ',') return null;
            sb.Append(c);
        }
        parts.Add(sb.ToString().Trim());
        if (parts.Count < 2) return null;
        var result = new StringBuilder();
        var usesVariable = false;
        foreach (var part in parts)
        {
            if (part.Length >= 2 && part[0] is '"' or '\'' && part[^1] == part[0])
                result.Append(ImportContext.Literal(part[1..^1].Replace(new string(part[0], 2), part[0].ToString(), StringComparison.Ordinal)));
            else if (IsIdentifier(part) && _templates.TryGetValue(part, out var inner)) { result.Append(inner); usesVariable = true; }
            else if (IsIdentifier(part) && ctx.Variables.ContainsKey(part) && Model.ProjectVariables.IsValidName(part)) { result.Append("$(").Append(part).Append(')'); usesVariable = true; }
            else if (IsIdentifier(part) && IsBuiltIn(part) && BuiltIn(part, default) is { Length: > 0 } b) result.Append(ImportContext.Literal(b));
            else return null;
        }
        return usesVariable ? result.ToString() : null;
    }

    private void Include(string rest, SourceRef at, int depth)
    {
        string? name;
        if (rest.StartsWith('<') && rest.EndsWith('>')) name = rest[1..^1];
        else name = Evaluate(rest, at) as string;
        if (name is null) { ctx.Manual(at, $"#include {rest}: the file name cannot be evaluated; that file is not imported."); return; }
        if (depth + 1 > ScriptText.MaxIncludeDepth) { ctx.Warn(at, $"#include {name}: includes nest deeper than {ScriptText.MaxIncludeDepth}; not read."); return; }
        var candidate = name;
        // A path built from SourcePath is inside the script's folder: use it relative to that folder.
        var root = ctx.ScriptDirectory + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(candidate) && Path.GetFullPath(candidate).StartsWith(root, StringComparison.OrdinalIgnoreCase)) candidate = Path.GetRelativePath(ctx.ScriptDirectory, Path.GetFullPath(candidate));
        var path = ScriptText.ResolveInclude(candidate, at.File, ctx.ScriptDirectory);
        if (path is null)
        {
            ctx.Manual(at, $"#include \"{name}\" was not found (or is not a relative path); its contents are not imported.");
            return;
        }
        ctx.Info(at, $"#include \"{name}\" read.");
        Process(path, depth + 1);
    }

    /// <summary>The line with {#...} replaced: $(Name) for a #define, the value for other expressions; literal "$(" escaped.</summary>
    private string Substitute(string line, SourceRef at)
    {
        if (!line.Contains("{#", StringComparison.Ordinal)) return ImportContext.Literal(line);
        var sb = new StringBuilder();
        var pos = 0;
        while (true)
        {
            var start = line.IndexOf("{#", pos, StringComparison.Ordinal);
            if (start < 0) { sb.Append(ImportContext.Literal(line[pos..])); break; }
            sb.Append(ImportContext.Literal(line[pos..start]));
            var end = FindClose(line, start + 2);
            if (end < 0) { sb.Append(ImportContext.Literal(line[start..])); break; }
            var expr = line[(start + 2)..end].Trim();
            if (expr.StartsWith("emit ", StringComparison.OrdinalIgnoreCase)) expr = expr[5..].Trim();
            sb.Append(Inline(expr, at));
            pos = end + 1;
        }
        return sb.ToString();
    }

    private static int FindClose(string s, int from)
    {
        char quote = '\0';
        for (var i = from; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '}') return i;
        }
        return -1;
    }

    private string Inline(string expr, SourceRef at)
    {
        if (IsIdentifier(expr))
        {
            if (IsBuiltIn(expr) && BuiltIn(expr, at) is { Length: > 0 } builtIn) return ImportContext.Literal(builtIn);
            if (_templates.TryGetValue(expr, out var template)) return template;
            if (ctx.Variables.ContainsKey(expr) && Model.ProjectVariables.IsValidName(expr)) return $"$({expr})";
            if (!ctx.Variables.ContainsKey(expr) && !_builtIns.Contains(expr)) ctx.Warn(at, $"{{#{expr}}} refers to an undefined name.");
        }
        var value = Evaluate(expr, at);
        if (value is null)
        {
            ctx.Manual(at, $"{{#{expr}}} cannot be evaluated by the importer; the value is missing.");
            return "{?#" + expr.Replace("}", "", StringComparison.Ordinal) + "}";
        }
        return ImportContext.Literal(Format(value));
    }

    private static bool IsIdentifier(string s) => s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private object? Evaluate(string expr, SourceRef at) => Evaluate(expr, at, out _);

    private object? Evaluate(string expr, SourceRef at, out string? fileVersionOf)
    {
        var parser = new Expression(expr, this, at);
        var v = parser.Parse();
        fileVersionOf = parser.FileVersionOf;
        return v;
    }

    /// <summary>Recursive-descent evaluator of ISPP expressions: strings, numbers, defines, + - == != &lt; &gt; &amp;&amp; || !, functions. null = unknown.</summary>
    private sealed class Expression(string text, InnoPreprocessor pp, SourceRef at)
    {
        private int _pos;
        private bool _failed;
        public string? FileVersionOf { get; private set; }

        public object? Parse()
        {
            var v = Or();
            Skip();
            return _failed || _pos < text.Length ? null : v;
        }

        private void Skip() { while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++; }
        private bool Eat(string op)
        {
            Skip();
            if (string.CompareOrdinal(text, _pos, op, 0, op.Length) != 0) return false;
            _pos += op.Length;
            return true;
        }

        private object? Or()
        {
            var l = And();
            while (Eat("||")) { var r = And(); l = l is null || r is null ? null : Truthy(l) || Truthy(r); }
            return l;
        }

        private object? And()
        {
            var l = Compare();
            while (Eat("&&")) { var r = Compare(); l = l is null || r is null ? null : Truthy(l) && Truthy(r); }
            return l;
        }

        private object? Compare()
        {
            var l = Add();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
            {
                if (!Eat(op)) continue;
                var r = Add();
                if (l is null || r is null) return null;
                int c = l is long a && r is long b ? a.CompareTo(b) : string.Compare(Format(l), Format(r), StringComparison.OrdinalIgnoreCase);
                return op switch { "==" => c == 0, "!=" => c != 0, "<=" => c <= 0, ">=" => c >= 0, "<" => c < 0, _ => c > 0 };
            }
            return l;
        }

        private object? Add()
        {
            var l = Unary();
            while (true)
            {
                if (Eat("+")) { var r = Unary(); l = l is null || r is null ? null : l is long a && r is long b ? a + b : Format(l) + Format(r); }
                else if (Eat("-")) { var r = Unary(); l = l is long a && r is long b ? a - b : null; }
                else return l;
            }
        }

        private object? Unary()
        {
            if (Eat("!")) { var v = Unary(); return v is null ? null : !Truthy(v); }
            return Primary();
        }

        private object? Primary()
        {
            Skip();
            if (_pos >= text.Length) { _failed = true; return null; }
            var c = text[_pos];
            if (c == '(')
            {
                _pos++;
                var v = Or();
                if (!Eat(")")) _failed = true;
                return v;
            }
            if (c is '"' or '\'')
            {
                var sb = new StringBuilder();
                _pos++;
                while (_pos < text.Length)
                {
                    if (text[_pos] == c)
                    {
                        if (_pos + 1 < text.Length && text[_pos + 1] == c) { sb.Append(c); _pos += 2; continue; }
                        _pos++;
                        return sb.ToString();
                    }
                    sb.Append(text[_pos++]);
                }
                _failed = true;
                return null;
            }
            if (char.IsAsciiDigit(c))
            {
                var start = _pos;
                if (c == '0' && _pos + 1 < text.Length && text[_pos + 1] is 'x' or 'X')
                {
                    _pos += 2;
                    while (_pos < text.Length && char.IsAsciiHexDigit(text[_pos])) _pos++;
                    return long.TryParse(text.AsSpan(start + 2, _pos - start - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null;
                }
                while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
                return long.TryParse(text.AsSpan(start, _pos - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                var name = text[start.._pos];
                if (name.Equals("defined", StringComparison.OrdinalIgnoreCase))
                {
                    var paren = Eat("(");
                    Skip();
                    var s = _pos;
                    while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                    var id = text[s.._pos];
                    if (paren && !Eat(")")) _failed = true;
                    return pp.Ctx.Variables.ContainsKey(id) || pp._builtIns.Contains(id);
                }
                if (Eat("("))
                {
                    var args = new List<object?>();
                    if (!Eat(")"))
                    {
                        do args.Add(Or()); while (Eat(","));
                        if (!Eat(")")) { _failed = true; return null; }
                    }
                    return Call(name, args);
                }
                if (pp.IsBuiltIn(name)) return pp.BuiltIn(name, at) is { Length: > 0 } b ? b : null;
                return pp.Ctx.Variables.TryGetValue(name, out var v) ? v : null;
            }
            _failed = true;
            return null;
        }

        private object? Call(string name, List<object?> args)
        {
            string? S(int i) => i < args.Count && args[i] is { } a ? Format(a) : null;
            long? N(int i) => i < args.Count ? args[i] switch { long n => n, string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n, _ => null } : null;
            switch (name.ToLowerInvariant())
            {
                case "stringchange":
                    return S(0) is { } s0 && S(1) is { Length: > 0 } from && S(2) is { } to ? s0.Replace(from, to, StringComparison.OrdinalIgnoreCase) : null;
                case "lowercase": return S(0)?.ToLowerInvariant();
                case "uppercase": return S(0)?.ToUpperInvariant();
                case "trim": return S(0)?.Trim();
                case "len": return S(0) is { } l ? (long)l.Length : null;
                case "str": return S(0);
                case "int": return N(0);
                case "copy":
                {
                    if (S(0) is not { } s || N(1) is not { } i) return null;
                    var startIndex = (int)Math.Clamp(i - 1, 0, s.Length);
                    var count = N(2) is { } n ? (int)Math.Clamp(n, 0, s.Length - startIndex) : s.Length - startIndex;
                    return s.Substring(startIndex, count);
                }
                case "pos": return S(0) is { } sub && S(1) is { } str ? (long)(str.IndexOf(sub, StringComparison.Ordinal) + 1) : null;
                case "removebackslash": return S(0)?.TrimEnd('\\', '/');
                case "addbackslash": return S(0) is { } ab ? (ab.EndsWith('\\') ? ab : ab + "\\") : null;
                case "extractfilename": return S(0) is { } fn ? fn[(fn.LastIndexOfAny(['\\', '/']) + 1)..] : null;
                case "extractfiledir": return S(0) is { } fd ? fd[..Math.Max(0, fd.LastIndexOfAny(['\\', '/']))] : null;
                case "extractfilepath": return S(0) is { } fp ? fp[..(fp.LastIndexOfAny(['\\', '/']) + 1)] : null;
                case "extractfileext": return S(0) is { } fe ? Path.GetExtension(fe) : null;
                case "changefileext": return S(0) is { } cf && S(1) is { } ext ? Path.ChangeExtension(cf, ext.Length == 0 ? null : ext.TrimStart('.')) : null;
                case "removefileext": return S(0) is { } rf ? Path.ChangeExtension(rf, null) : null;
                case "addquotes": return S(0) is { } aq ? (aq.Contains(' ') ? $"\"{aq}\"" : aq) : null;
                case "removequotes": return S(0)?.Trim('"');
                case "fileexists": return S(0) is { } fx ? File.Exists(pp.Ctx.ScriptFile(fx)) : null;
                case "direxists": return S(0) is { } dx ? Directory.Exists(pp.Ctx.ScriptFile(dx)) : null;
                case "getfileversion" or "getversionnumbersstring" or "getfileproductversion" or "getstringfileinfo":
                {
                    if (S(0) is not { } file) return null;
                    var full = pp.Ctx.ScriptFile(file);
                    FileVersionOf = PathRules.TryNormalizeRelative(file, out _, out _) ? file : Path.GetFileName(full);
                    if (!File.Exists(full)) return null;
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(full);
                    var field = name.Equals("GetStringFileInfo", StringComparison.OrdinalIgnoreCase) ? S(1) ?? "" : "";
                    var version = name.ToLowerInvariant() switch
                    {
                        "getfileproductversion" => info.ProductVersion,
                        "getstringfileinfo" => field.Equals("ProductVersion", StringComparison.OrdinalIgnoreCase) ? info.ProductVersion :
                                               field.Equals("FileVersion", StringComparison.OrdinalIgnoreCase) ? info.FileVersion :
                                               field.Equals("CompanyName", StringComparison.OrdinalIgnoreCase) ? info.CompanyName :
                                               field.Equals("ProductName", StringComparison.OrdinalIgnoreCase) ? info.ProductName : null,
                        _ => $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}",
                    };
                    return string.IsNullOrWhiteSpace(version) || version == "0.0.0.0" ? null : version.Trim();
                }
                default:
                    return null;  // GetEnv, GetDateTimeString, SetupSetting, user macros …: unknown here
            }
        }
    }
}
