using System.Diagnostics;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Model;

/// <summary>
/// Starting values for a new project from the folder of a built app: the main program (named like the folder, else the
/// largest .exe that is not an uninstaller or setup) and name, publisher and version from its version resource.
/// Shared by "fluentsetup init" and the Builder's New project dialog.
/// </summary>
public sealed record ProjectGuess(string Name, string Id, string Publisher, string Version, string MainExecutable, IReadOnlyList<string> Executables)
{
    public static ProjectGuess FromFolder(string folder)
    {
        var exes = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>()
                       .Where(n => !IsHelperExe(n)).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var main = exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                   ?? exes.OrderByDescending(e => new FileInfo(Path.Combine(folder, e)).Length).FirstOrDefault();

        string name = folderName, publisher = "", version = "1.0.0";
        if (main is not null)
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(folder, main));
            if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName.Trim();
            else name = Path.GetFileNameWithoutExtension(main);
            if (!string.IsNullOrWhiteSpace(info.CompanyName)) publisher = info.CompanyName.Trim();
            var v = (info.ProductVersion ?? "").Split('+')[0].Trim();
            if (ProjectValidator.IsValidVersion(v)) version = v;
            else if (info.FileMajorPart > 0 || info.FileMinorPart > 0) version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        if (name.Length > 128) name = name[..128];
        return new ProjectGuess(name, MakeId(publisher, name), publisher, version, main ?? "", exes);
    }

    /// <summary>"Contoso Ltd" + "Notes" -> "ContosoLtd.Notes" (letters, digits, '-', '_', at most 64 characters).</summary>
    public static string MakeId(string publisher, string name)
    {
        static string Clean(string s) => string.Concat(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        var id = string.Join('.', new[] { Clean(publisher), Clean(name) }.Where(p => p.Length > 0));
        return id.Length > 64 ? id[..64] : id;
    }

    /// <summary>Uninstallers, setups, updaters and crash/debug helpers that ship next to an app's main program.</summary>
    private static bool IsHelperExe(string name)
    {
        var n = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return n.Contains("unins") || n.Contains("setup") || n.Contains("install") || n is "update" or "updater" or "createdump" or "crashpad_handler"
               || n.EndsWith("crashreporter") || n.StartsWith("vc_redist");
    }
}
