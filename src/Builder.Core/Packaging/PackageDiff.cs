using System.Text.Json;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>A payload file that differs between two packages (sizes are -1 where the file does not exist).</summary>
public sealed record FileChange(string Path, string Variant, long OldSize, long NewSize);

/// <summary>
/// What changed between two Setup.exe files of an app (<c>fsetup inspect new.exe --diff old.exe</c>): payload files
/// added, removed and changed (by SHA-256), and how much of the new file a differential update
/// (<c>update.differential</c>) could take from the old one: its streams with the same SHA-256 and size.
/// </summary>
public sealed class PackageDiff
{
    public string OldVersion { get; init; } = "";
    public string NewVersion { get; init; } = "";
    public List<FileChange> Added { get; } = [];
    public List<FileChange> Removed { get; } = [];
    public List<FileChange> Changed { get; } = [];
    public int Unchanged { get; set; }
    public long NewFileSize { get; init; }
    /// <summary>Bytes of the new file in streams the old file has too.</summary>
    public long ReusableBytes { get; set; }
    /// <summary>About what a differential update downloads (the rest of the file); the updater downloads the whole
    /// file when less than 1 MiB or an eighth of it can be reused.</summary>
    public long DifferentialDownload => ReusableBytes >= 1024 * 1024 && ReusableBytes >= NewFileSize / 8 ? NewFileSize - ReusableBytes : NewFileSize;

    public static PackageDiff Compare(PackageInfo older, PackageInfo newer)
    {
        if (older.Header is null || newer.Header is null) throw new InvalidDataException("Both files must be readable FluentSetup packages.");
        var diff = new PackageDiff
        {
            OldVersion = Version(older.Header), NewVersion = Version(newer.Header), NewFileSize = newer.FileSize,
        };
        var oldFiles = Files(older.Header);
        var newFiles = Files(newer.Header);
        foreach (var (key, f) in newFiles)
        {
            if (!oldFiles.TryGetValue(key, out var o)) diff.Added.Add(new FileChange(f.Path, f.Variant, -1, f.Size));
            else if (o.Size != f.Size || !o.Sha256.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) diff.Changed.Add(new FileChange(f.Path, f.Variant, o.Size, f.Size));
            else diff.Unchanged++;
        }
        foreach (var (key, o) in oldFiles)
        {
            if (!newFiles.ContainsKey(key)) diff.Removed.Add(new FileChange(o.Path, o.Variant, o.Size, -1));
        }
        foreach (var list in new[] { diff.Added, diff.Removed, diff.Changed }) list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        var oldStreams = Streams(older.Header).ToHashSet();
        diff.ReusableBytes = Streams(newer.Header).Where(oldStreams.Contains).Sum(s => s.Size);
        return diff;
    }

    private static string Version(JsonDocument header) =>
        header.RootElement.TryGetProperty("app", out var app) && app.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

    private sealed record Entry(string Path, string Variant, long Size, string Sha256);

    private static Dictionary<string, Entry> Files(JsonDocument header)
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (!header.RootElement.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("files", out var files)) return result;
        foreach (var f in files.EnumerateArray())
        {
            var path = f.GetProperty("path").GetString() ?? "";
            var variant = f.TryGetProperty("variant", out var v) ? v.GetString() ?? "" : "";
            result[variant + "|" + path] = new Entry(path, variant, f.GetProperty("size").GetInt64(), f.GetProperty("sha256").GetString() ?? "");
        }
        return result;
    }

    private static IEnumerable<(string Sha256, long Size)> Streams(JsonDocument header)
    {
        foreach (var set in new[] { "payload", "frontend", "redist" })
        {
            if (!header.RootElement.TryGetProperty(set, out var s) || !s.TryGetProperty("streams", out var streams)) continue;
            foreach (var st in streams.EnumerateArray()) yield return ((st.GetProperty("sha256").GetString() ?? "").ToLowerInvariant(), st.GetProperty("size").GetInt64());
        }
    }
}
