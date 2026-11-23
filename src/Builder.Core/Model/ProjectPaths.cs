using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Model;

/// <summary>The paths of a project that are relative to the project file's folder.</summary>
public static class ProjectPaths
{
    /// <summary>
    /// Rewrites the project's relative paths so they point at the same files when the project file is saved in
    /// <paramref name="newDirectory"/> instead of <paramref name="oldDirectory"/> (Save As to another folder).
    /// Absolute paths, paths with $(Name) placeholders or {Templates}, and empty values are left as they are; a path
    /// that cannot be made relative (another drive) becomes absolute. Returns the fields that changed.
    /// </summary>
    public static List<string> Rebase(SetupProject p, string oldDirectory, string newDirectory)
    {
        var changed = new List<string>();
        var from = Path.GetFullPath(oldDirectory);
        var to = Path.GetFullPath(newDirectory);
        if (string.Equals(Path.TrimEndingDirectorySeparator(from), Path.TrimEndingDirectorySeparator(to), StringComparison.OrdinalIgnoreCase)) return changed;

        string Move(string field, string path)
        {
            var moved = RebasePath(path, from, to);
            if (!string.Equals(moved, path, StringComparison.Ordinal)) changed.Add(field);
            return moved;
        }

        p.App.Icon = Move("app.icon", p.App.Icon);
        p.Files.Source = Move("files.source", p.Files.Source);
        for (var i = 0; i < p.Files.Extra.Count; i++) p.Files.Extra[i].Source = Move($"files.extra[{i}].source", p.Files.Extra[i].Source);
        for (var i = 0; i < p.Files.Variants.Count; i++) p.Files.Variants[i].Source = Move($"files.variants[{i}].source", p.Files.Variants[i].Source);
        p.Ui.License = Move("ui.license", p.Ui.License);
        foreach (var lang in p.Ui.Licenses.Keys.ToList()) p.Ui.Licenses[lang] = Move($"ui.licenses.{lang}", p.Ui.Licenses[lang]);
        p.Ui.Localization = Move("ui.localization", p.Ui.Localization);
        p.Ui.Banner = Move("ui.banner", p.Ui.Banner);
        p.Output.Directory = Move("output.directory", p.Output.Directory);
        p.Signing.CertificateFile = Move("signing.certificateFile", p.Signing.CertificateFile);
        p.Signing.SignToolPath = Move("signing.signToolPath", p.Signing.SignToolPath);
        if (p.Signing.TrustedSigning is { } ts) ts.DlibPath = Move("signing.trustedSigning.dlibPath", ts.DlibPath);
        // A bare program name is looked up on PATH; only a path is relative to the project.
        if (p.Signing.Command is { } cmd && Signer.IsCommandPath(cmd.Run)) cmd.Run = Move("signing.command.run", cmd.Run);
        return changed;
    }

    /// <summary>One path rebased from <paramref name="from"/> to <paramref name="to"/> (both full paths).</summary>
    public static string RebasePath(string? path, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains("$(", StringComparison.Ordinal) || path.Contains('{', StringComparison.Ordinal)) return path ?? "";
        var native = OperatingSystem.IsWindows() ? path : path.Replace('\\', '/');
        var full = Path.GetFullPath(Path.Combine(from, native));
        var rel = Path.GetRelativePath(to, full);
        if (Path.IsPathRooted(rel)) return full;  // another drive: only an absolute path still works
        // Keep the path's own separator style ("assets/app.ico" stays with forward slashes).
        if (path.Contains('/', StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal)) rel = rel.Replace('\\', '/');
        else if (OperatingSystem.IsWindows()) rel = rel.Replace('/', '\\');
        return rel;
    }
}
