using System.Security.Cryptography;
using System.Text;
using FluentSetup.Builder.Core.Prerequisites;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Compressed streams of earlier builds, so a rebuild compresses only what changed: the WinUI front end (level 19,
/// the same on every build of an app), bundled installers and every payload block whose files are unchanged. A stream
/// is found by the content of its files (sizes and SHA-256, in order) and the compression parameters; the compressed
/// bytes are checked against their recorded SHA-256 before they are used. Lives next to the installer cache
/// (<c>%LocalAppData%\FluentSetup\cache\streams</c>, or <c>FLUENTSETUP_CACHE</c>); entries unused for
/// <see cref="MaxAge"/> are removed, and the oldest ones once the cache grows past <see cref="MaxBytes"/>.
/// The signed copies of the app's programs (signing.files, <see cref="SignedExtension"/>) live here too, so a rebuild
/// does not sign them again with a new timestamp (which would change their hash and defeat the stream cache and
/// differential updates).
/// </summary>
internal static class StreamCache
{
    /// <summary>Changes whenever the compressor could produce other bytes for the same input.</summary>
    private static readonly string Generation = "fsetup-stream-1;zstd " + typeof(ZstdSharp.Compressor).Assembly.GetName().Version;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private static readonly Lock PruneLock = new();

    public static string Root => Path.Combine(Path.GetDirectoryName(InstallerCache.Root)!, "streams");

    /// <summary>File extension of compressed streams.</summary>
    public const string StreamExtension = ".zst";
    /// <summary>File extension of signed copies of the app's programs.</summary>
    public const string SignedExtension = ".signed";

    /// <summary>The cache key of a stream: compression parameters and, per file, its size and SHA-256.</summary>
    public static string Key(int level, int workers, int windowLog, IReadOnlyList<(long Size, byte[] Sha256)> files, string filters = "")
    {
        var sb = new StringBuilder(Generation).Append(';').Append(level).Append(';').Append(workers > 1 ? "mt" : "st").Append(';').Append(windowLog)
            .Append(";f").Append(filters);
        foreach (var (size, sha) in files) sb.Append(';').Append(size).Append(':').Append(Convert.ToHexStringLower(sha));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// Copies the cached stream for <paramref name="key"/> to <paramref name="dest"/>; null if there is none or it is
    /// damaged (then it is removed). Returns the stored size and SHA-256.
    /// </summary>
    public static (long Size, byte[] Sha256)? TryGet(string key, string dest, string extension = StreamExtension)
    {
        var data = Path.Combine(Root, key + extension);
        var meta = data + ".sha256";
        try
        {
            if (!File.Exists(data) || !File.Exists(meta)) return null;
            var expected = File.ReadAllText(meta).Trim();
            byte[] sha;
            long size;
            using (var input = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    output.Write(buffer, 0, n);
                }
                size = input.Length;
                sha = hash.GetHashAndReset();
            }
            if (!Convert.ToHexStringLower(sha).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(dest);
                Remove(data);
                return null;
            }
            File.SetLastWriteTimeUtc(meta, DateTime.UtcNow);  // last use, for pruning
            return (size, sha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(dest); } catch (IOException) { }
            return null;
        }
    }

    /// <summary>Stores a freshly compressed stream; failures are ignored (the cache is only an optimisation).</summary>
    public static void Put(string key, string source, byte[] sha256, string extension = StreamExtension)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var data = Path.Combine(Root, key + extension);
            var temp = data + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.Copy(source, temp);
            File.Move(temp, data, overwrite: true);
            File.WriteAllText(data + ".sha256", Convert.ToHexStringLower(sha256));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes entries unused for two weeks, then the least recently used ones above the size limit.</summary>
    public static void Prune()
    {
        lock (PruneLock)
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                var root = new DirectoryInfo(Root);
                var entries = root.GetFiles("*" + StreamExtension).Concat(root.GetFiles("*" + SignedExtension))
                    .Select(f => (Data: f, Used: File.Exists(f.FullName + ".sha256") ? File.GetLastWriteTimeUtc(f.FullName + ".sha256") : DateTime.MinValue))
                    .OrderByDescending(e => e.Used).ToList();
                long total = 0;
                foreach (var (data, used) in entries)
                {
                    total += data.Length;
                    if (DateTime.UtcNow - used > MaxAge || total > MaxBytes) Remove(data.FullName);
                }
                foreach (var stale in Directory.EnumerateFiles(Root, "*.tmp"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromDays(1)) File.Delete(stale);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Remove(string data)
    {
        try { File.Delete(data); } catch (IOException) { }
        try { File.Delete(data + ".sha256"); } catch (IOException) { }
    }
}
