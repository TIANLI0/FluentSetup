using System.Text;

namespace FluentSetup.Builder.Core.Import;

/// <summary>A place in a script: full file path and 1-based line.</summary>
internal readonly record struct SourceRef(string File, int Line);

/// <summary>One line of a script after includes (and, for Inno Setup, the preprocessor) with the place it came from.</summary>
internal sealed record ScriptLine(SourceRef At, string Text);

/// <summary>Reads script files: UTF-8 or UTF-16 with a byte-order mark, UTF-8 without one, else Windows-1252.</summary>
internal static class ScriptText
{
    /// <summary>Includes nest at most this deep, and an import reads at most <see cref="MaxFiles"/> files and <see cref="MaxLines"/> lines.</summary>
    public const int MaxIncludeDepth = 8;
    public const int MaxFiles = 64;
    public const int MaxLines = 200_000;
    public const long MaxFileBytes = 8 * 1024 * 1024;

    public static string[] ReadLines(string path, out bool legacyEncoding)
    {
        legacyEncoding = false;
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"{path} is larger than {MaxFileBytes / (1024 * 1024)} MB; this is not an installer script.");
        var bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // ANSI script: the code page it was written in is unknown; Windows-1252 keeps ASCII intact.
                legacyEncoding = true;
                text = (System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1).GetString(bytes);
            }
        }
        if (text.Contains('\0')) throw new InvalidDataException($"{path} is a binary file, not an installer script.");
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// Resolves an include path against the including file's folder, then the main script's folder. Absolute paths and
    /// paths that do not exist give null.
    /// </summary>
    public static string? ResolveInclude(string name, string includingFile, string scriptDirectory)
    {
        var rel = name.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (rel.Length == 0 || Path.IsPathRooted(rel) || (rel.Length > 1 && rel[1] == ':')) return null;
        foreach (var dir in new[] { Path.GetDirectoryName(includingFile)!, scriptDirectory })
        {
            var full = Path.GetFullPath(Path.Combine(dir, rel));
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>Splits a Windows command line the way CommandLineToArgvW does (quotes group, "" inside quotes is a quote).</summary>
    public static List<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, any = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any || sb.Length > 0) args.Add(sb.ToString());
                sb.Clear();
                any = false;
            }
            else
            {
                sb.Append(c);
                any = true;
            }
        }
        if (any || sb.Length > 0) args.Add(sb.ToString());
        return args;
    }
}
