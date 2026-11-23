using System.Globalization;
using System.Text;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Model;

public enum TextLineProblem
{
    /// <summary>The line does not have the expected form ("language = text", "language: key = text").</summary>
    NotUnderstood,
    /// <summary>The part before "=" (or ":") is not a language tag.</summary>
    NotALanguage,
    /// <summary>The same language (or language and key) is given twice; the later line wins.</summary>
    Duplicate,
    /// <summary>A part of a number list is not a whole number.</summary>
    NotANumber,
}

/// <param name="Line">1-based line of the text (0 for number lists).</param>
/// <param name="Text">The line (or the number list part) as written.</param>
/// <param name="Value">The language tag, key or part the problem is about.</param>
public sealed record TextLineError(int Line, string Text, TextLineProblem Problem, string Value);

/// <summary>
/// Text forms of per-language tables (one "en-US = text" line each), installer string overrides ("zh-CN: key = text")
/// and number lists, as the Builder's editors show them. Line breaks inside a value are written as \n; backslashes in
/// front of an "n" or a line break are doubled, so every value (also C:\new) reads back exactly as it was written.
/// </summary>
public static class TextTables
{
    public static string EscapeValue(string? value)
    {
        var v = (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var sb = new StringBuilder(v.Length + 8);
        var i = 0;
        while (i < v.Length)
        {
            var run = 0;
            while (i + run < v.Length && v[i + run] == '\\') run++;
            var next = i + run < v.Length ? v[i + run] : '\0';
            var doubled = run > 0 && (next == 'n' || next == '\n');
            sb.Append('\\', doubled ? run * 2 : run);
            i += run;
            if (i >= v.Length) break;
            if (v[i] == '\n') sb.Append("\\n");
            else sb.Append(v[i]);
            i++;
        }
        return sb.ToString();
    }

    public static string UnescapeValue(string? text)
    {
        var v = text ?? "";
        var sb = new StringBuilder(v.Length);
        var i = 0;
        while (i < v.Length)
        {
            var run = 0;
            while (i + run < v.Length && v[i + run] == '\\') run++;
            if (run > 0 && i + run < v.Length && v[i + run] == 'n')
            {
                sb.Append('\\', run / 2);
                sb.Append(run % 2 == 1 ? '\n' : 'n');
                i += run + 1;
                continue;
            }
            sb.Append('\\', run);
            i += run;
            if (i < v.Length) sb.Append(v[i++]);
        }
        return sb.ToString();
    }

    /// <summary>Non-blank lines with their 1-based line numbers.</summary>
    private static IEnumerable<(int Number, string Line)> Lines(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', '\r');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length > 0) yield return (i + 1, line);
        }
    }

    // ---- per-language texts ------------------------------------------------------------------------------------

    public static string FormatLocalized(Dictionary<string, string>? texts) =>
        texts is null ? "" : string.Join(Environment.NewLine, texts.Select(kv => $"{kv.Key} = {EscapeValue(kv.Value)}"));

    /// <summary>
    /// "language = text" per line. A line without "=" is English text (en-US). A line whose part before "=" is not a
    /// language tag is reported (write "en-US = a = b" for English text with "="); it is not taken as a language.
    /// </summary>
    public static Dictionary<string, string> ParseLocalized(string? text, List<TextLineError>? errors = null)
    {
        var result = new Dictionary<string, string>();
        foreach (var (number, line) in Lines(text))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            string lang, value;
            if (eq < 0)
            {
                (lang, value) = ("en-US", line);
            }
            else
            {
                lang = line[..eq].Trim();
                value = line[(eq + 1)..].Trim();
                if (!Localization.IsLanguageTag(lang))
                {
                    errors?.Add(new TextLineError(number, line, lang.Length == 0 ? TextLineProblem.NotUnderstood : TextLineProblem.NotALanguage, lang));
                    continue;
                }
            }
            var existing = result.Keys.FirstOrDefault(k => string.Equals(k, lang, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                errors?.Add(new TextLineError(number, line, TextLineProblem.Duplicate, lang));
                result.Remove(existing);
            }
            result[lang] = UnescapeValue(value);
        }
        return result;
    }

    // ---- installer string overrides ----------------------------------------------------------------------------

    public static string FormatStrings(Dictionary<string, Dictionary<string, string>>? strings) =>
        strings is null ? "" : string.Join(Environment.NewLine, strings.SelectMany(lang => (lang.Value ?? []).Select(kv => $"{lang.Key}: {kv.Key} = {EscapeValue(kv.Value)}")));

    /// <summary>"language: key = text" per line; other lines are reported, not dropped silently.</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseStrings(string? text, List<TextLineError>? errors = null)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (number, line) in Lines(text))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var eq = colon < 0 ? -1 : line.IndexOf('=', colon + 1);
            var lang = colon < 0 ? "" : line[..colon].Trim();
            var key = eq < 0 ? "" : line[(colon + 1)..eq].Trim();
            if (colon <= 0 || eq < 0 || lang.Length == 0 || key.Length == 0 || key.Any(char.IsWhiteSpace))
            {
                errors?.Add(new TextLineError(number, line, TextLineProblem.NotUnderstood, ""));
                continue;
            }
            if (!Localization.IsLanguageTag(lang))
            {
                errors?.Add(new TextLineError(number, line, TextLineProblem.NotALanguage, lang));
                continue;
            }
            if (!result.TryGetValue(lang, out var table)) result[lang] = table = [];
            if (table.ContainsKey(key)) errors?.Add(new TextLineError(number, line, TextLineProblem.Duplicate, $"{lang}: {key}"));
            table[key] = UnescapeValue(line[(eq + 1)..].Trim());
        }
        return result;
    }

    // ---- number lists ------------------------------------------------------------------------------------------

    public static string FormatInts(IEnumerable<int>? values) =>
        values is null ? "" : string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Whole numbers separated by commas, semicolons or white space; other parts are reported.</summary>
    public static List<int> ParseInts(string? text, List<TextLineError>? errors = null)
    {
        var result = new List<int>();
        foreach (var part in (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) result.Add(n);
            else errors?.Add(new TextLineError(0, part, TextLineProblem.NotANumber, part));
        }
        return result;
    }
}
