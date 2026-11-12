using System.Text;
using System.Text.RegularExpressions;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Case-insensitive glob over relative paths with '/' or '\' separators:
/// <c>*</c> matches within one segment, <c>**</c> matches any number of segments, <c>?</c> one character.
/// A pattern without a separator matches the file name in any folder ("*.pdb" == "**/*.pdb"); a leading "./" anchors
/// it to the source folder ("./App.exe" matches only the top-level App.exe, not "old/App.exe").
/// </summary>
public sealed class Glob
{
    private readonly Regex _regex;
    public string Pattern { get; }

    public Glob(string pattern)
    {
        Pattern = pattern;
        var p = pattern.Replace('\\', '/').Trim();
        var anchored = p.StartsWith("./", StringComparison.Ordinal);
        if (anchored) p = p[2..];
        if (!anchored && !p.Contains('/')) p = "**/" + p;
        if (p.EndsWith('/')) p += "**";
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*')
            {
                if (i + 1 < p.Length && p[i + 1] == '*')
                {
                    var slash = i + 2 < p.Length && p[i + 2] == '/';
                    sb.Append(slash ? "(?:.*/)?" : ".*");
                    i += slash ? 2 : 1;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        _regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath.Replace('\\', '/'));

    public static bool MatchesAny(IEnumerable<Glob> globs, string relativePath) => globs.Any(g => g.IsMatch(relativePath));
}
