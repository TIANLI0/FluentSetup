using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Packaging;

public sealed record CollectedFile(string SourcePath, string RelativePath, long Size, bool Preserve, FileAttributes Attributes, string Variant = "");

public sealed class CollectionResult
{
    public List<CollectedFile> Files { get; } = [];
    public List<string> Directories { get; } = [];
    public DiagnosticBag Diagnostics { get; } = new();
    public long TotalSize => Files.Sum(f => f.Size);
    public int SkippedLinks { get; internal set; }
}

/// <summary>
/// Expands the project's file rules into the list of files to install. Never follows symbolic links or junctions,
/// rejects unsafe or duplicate target paths, and skips the output folder so a build never packages its own output.
/// </summary>
public static class FileCollector
{
    public const int MaxFiles = 2_000_000;

    /// <summary>
    /// Collects every variant's build (files.variants[].source, with the project's include/exclude/preserve and extra
    /// files). Files with the same path and content in all variants become shared (stored and installed once); the rest
    /// is tagged with its variant. With one variant, everything is shared and the package has no variants.
    /// </summary>
    public static CollectionResult Collect(SetupProject project, string projectDirectory, IReadOnlyList<VariantSettings> variants,
                                           CancellationToken cancellationToken = default)
    {
        if (variants.Count == 0) return Collect(project, projectDirectory, cancellationToken);
        var perVariant = new List<CollectionResult>();
        var result = new CollectionResult();
        foreach (var v in variants)
        {
            var files = new FileSettings
            {
                Source = v.Source, Include = project.Files.Include, Exclude = project.Files.Exclude, Preserve = project.Files.Preserve,
                Extra = project.Files.Extra, EmptyDirectories = project.Files.EmptyDirectories,
            };
            var r = Collect(new SetupProject { Files = files, Output = project.Output }, projectDirectory, cancellationToken);
            result.Diagnostics.AddRange(r.Diagnostics.Items.Select(d => d with { Field = $"files.variants[{v.Id}]: {d.Field}" }));
            perVariant.Add(r);
        }
        foreach (var d in perVariant.SelectMany(r => r.Directories).Distinct(StringComparer.OrdinalIgnoreCase)) result.Directories.Add(d);
        if (variants.Count == 1)
        {
            result.Files.AddRange(perVariant[0].Files);
            return result;
        }

        var hashes = new Dictionary<string, byte[]>(StringComparer.FromComparison(PathRules.HostPathComparison));
        byte[] Hash(CollectedFile f)
        {
            if (!hashes.TryGetValue(f.SourcePath, out var h))
            {
                using var s = File.OpenRead(f.SourcePath);
                hashes[f.SourcePath] = h = System.Security.Cryptography.SHA256.HashData(s);
            }
            return h;
        }
        var maps = perVariant.Select(r => r.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)).ToList();
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in perVariant[0].Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var same = maps.Skip(1).All(m => m.TryGetValue(f.RelativePath, out var g) && g.Size == f.Size && g.Preserve == f.Preserve &&
                                             g.Attributes == f.Attributes && (g.SourcePath == f.SourcePath || Hash(g).AsSpan().SequenceEqual(Hash(f))));
            if (same) shared.Add(f.RelativePath);
        }
        result.Files.AddRange(perVariant[0].Files.Where(f => shared.Contains(f.RelativePath)));
        for (var i = 0; i < variants.Count; i++)
        {
            result.Files.AddRange(perVariant[i].Files.Where(f => !shared.Contains(f.RelativePath)).Select(f => f with { Variant = variants[i].Id }));
        }
        return result;
    }
    public static CollectionResult Collect(SetupProject project, string projectDirectory, CancellationToken cancellationToken = default)
    {
        var result = new CollectionResult();
        var include = project.Files.Include.Select(g => new Glob(g)).ToList();
        var exclude = project.Files.Exclude.Select(g => new Glob(g)).ToList();
        var preserve = project.Files.Preserve.Select(g => new Glob(g)).ToList();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outputDir = ProjectValidator.Resolve(projectDirectory, project.Output.Directory);
        string? outputFile = null;
        try
        {
            outputFile = Path.GetFullPath(Path.Combine(outputDir, ProjectValidator.ExpandFileName(project.Output.FileName, project)));
        }
        catch (ArgumentException)
        {
            // An invalid file name is reported by the validator.
        }

        void Add(string source, string rel, FileInfo info)
        {
            if (!PathRules.TryNormalizeRelative(rel, out var normalized, out var why))
            {
                result.Diagnostics.Error("FS1107", "files", $"'{rel}' cannot be installed: {why}.");
                return;
            }
            if (PathRules.IsInstallerReserved(normalized))
            {
                result.Diagnostics.Error("FS1108", "files", $"'{normalized}' is reserved by the installer (uninstall.exe, uninstall.json and .fsetup).");
                return;
            }
            if (seen.TryGetValue(normalized, out var other))
            {
                result.Diagnostics.Error("FS1109", "files", $"Two sources map to '{normalized}': {other} and {source}.");
                return;
            }
            seen[normalized] = source;
            // On Linux and macOS .NET calls dot files hidden; that is not the Windows attribute, so only read-only is taken.
            var attrs = info.Attributes & (OperatingSystem.IsWindows() ? FileAttributes.ReadOnly | FileAttributes.Hidden : FileAttributes.ReadOnly);
            result.Files.Add(new CollectedFile(source, normalized, info.Length, Glob.MatchesAny(preserve, normalized), attrs));
        }

        void Walk(string root, string targetPrefix, bool applyFilters)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                // A build never packages its own output: an output folder inside the source is skipped. When the
                // output folder is the source itself (build\bin holding the app and its installer), only the
                // installer file is left out (below).
                if (IsSameOrUnder(dir, outputDir) && !IsSameOrUnder(root, outputDir)) continue;
                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        AttributesToSkip = 0,
                        IgnoreInaccessible = false,
                        ReturnSpecialDirectories = false,
                    }).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    result.Diagnostics.Error("FS1111", "files.source", $"Cannot read folder {dir}: {ex.Message}");
                    continue;
                }
                foreach (var entry in entries)
                {
                    var relFromRoot = Path.GetRelativePath(root, entry.FullName);
                    var target = string.IsNullOrEmpty(targetPrefix) ? relFromRoot : Path.Combine(targetPrefix, relFromRoot);
                    if (PathRules.IsLink(entry))
                    {
                        result.SkippedLinks++;
                        result.Diagnostics.Warning("FS1112", "files.source", $"Skipped link (symbolic link or junction): {entry.FullName}");
                        continue;
                    }
                    if (!OperatingSystem.IsWindows() && entry.Name.Contains('\\'))
                    {
                        // A backslash is a folder separator on Windows: "a\b.txt" would land in a folder "a".
                        result.Diagnostics.Error("FS1107", "files", $"'{relFromRoot}' cannot be installed: the name contains '\\'.");
                        continue;
                    }
                    if (entry is DirectoryInfo)
                    {
                        if (applyFilters && Glob.MatchesAny(exclude, relFromRoot + "/")) continue;
                        stack.Push(entry.FullName);
                        continue;
                    }
                    if (applyFilters && (!Glob.MatchesAny(include, relFromRoot) || Glob.MatchesAny(exclude, relFromRoot))) continue;
                    if (outputFile is not null && entry.FullName.Equals(outputFile, PathRules.HostPathComparison)) continue;
                    if (result.Files.Count >= MaxFiles)
                    {
                        result.Diagnostics.Error("FS1113", "files", $"More than {MaxFiles:N0} files.");
                        return;
                    }
                    Add(entry.FullName, target, (FileInfo)entry);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(project.Files.Source))
        {
            var source = ProjectValidator.Resolve(projectDirectory, project.Files.Source);
            if (Directory.Exists(source)) Walk(source, "", applyFilters: true);
        }
        foreach (var extra in project.Files.Extra)
        {
            var src = ProjectValidator.Resolve(projectDirectory, extra.Source);
            if (File.Exists(src))
            {
                var info = new FileInfo(src);
                Add(src, string.IsNullOrEmpty(extra.Target) ? info.Name : Path.Combine(extra.Target, info.Name), info);
            }
            else if (Directory.Exists(src))
            {
                Walk(src, extra.Target, applyFilters: false);
            }
        }
        foreach (var d in project.Files.EmptyDirectories)
        {
            if (PathRules.TryNormalizeRelative(d, out var n, out _)) result.Directories.Add(n);
        }

        // A file may not also be a directory of another file.
        foreach (var f in result.Files)
        {
            var parts = f.RelativePath.Split('\\');
            for (var i = 1; i < parts.Length; i++)
            {
                var prefix = string.Join('\\', parts.Take(i));
                if (seen.ContainsKey(prefix)) result.Diagnostics.Error("FS1114", "files", $"'{prefix}' is both a file and a folder.");
            }
        }

        if (result.Files.Count == 0) result.Diagnostics.Error("FS1115", "files.source", "No files to install: the source folder is empty or everything is excluded.");
        if (!string.IsNullOrEmpty(project.App.MainExecutable) &&
            PathRules.TryNormalizeRelative(project.App.MainExecutable, out var main, out _) && !seen.ContainsKey(main))
        {
            result.Diagnostics.Error("FS1110", "app.mainExecutable", $"Main executable '{main}' is not among the files to install.");
        }
        return result;
    }

    private static bool IsSameOrUnder(string path, string parent)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return p.Equals(r, PathRules.HostPathComparison) ||
               p.StartsWith(r + Path.DirectorySeparatorChar, PathRules.HostPathComparison);
    }
}
