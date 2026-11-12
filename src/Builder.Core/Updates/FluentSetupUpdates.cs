using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentSetup.Builder.Core.Updates;

/// <summary>A FluentSetup release on GitHub.</summary>
public sealed record FluentSetupRelease(string Version, string Tag, string PageUrl, string Notes, bool Prerelease,
                                        ReleaseAsset? Setup, ReleaseAsset? CliZip, ReleaseAsset? Checksums);

public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>
/// Updates of FluentSetup itself (Builder app and CLI): the latest release of TIANLI0/FluentSetup on GitHub, its
/// <c>FluentSetup-&lt;version&gt;-Setup.exe</c> verified against <c>SHA256SUMS.txt</c> (and GitHub's own digest), installed
/// with <c>/passive /relaunch</c> so the user sees progress and the Builder opens again.
/// </summary>
public static partial class FluentSetupUpdates
{
    public const string Repository = "TIANLI0/FluentSetup";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const long MaxDownload = 512L * 1024 * 1024;

    [GeneratedRegex(@"^FluentSetup-(?<v>[0-9A-Za-z.\-]+)-Setup\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex SetupAsset();

    [GeneratedRegex(@"^fluentsetup-(?<v>[0-9A-Za-z.\-]+)-win-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex CliAsset();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S.*?)\s*$")]
    private static partial Regex ChecksumLine();

    /// <summary>This build's version ("1.0.0" of "1.0.0+commit").</summary>
    public static string CurrentVersion =>
        (typeof(FluentSetupUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluentSetup", CurrentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    /// <summary>The newest release (pre-releases only if asked for), or null if there is none.</summary>
    public static async Task<FluentSetupRelease?> GetLatestAsync(HttpClient http, bool includePrerelease, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=20", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        FluentSetupRelease? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            bool prerelease = r.GetProperty("prerelease").GetBoolean();
            if (prerelease && !includePrerelease) continue;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            string version = tag.TrimStart('v', 'V');
            if (!Validation.ProjectValidator.IsValidVersion(version)) continue;
            ReleaseAsset? setup = null, cli = null, sums = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith("https://", StringComparison.Ordinal)) continue;
                string? digest = a.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? s[7..] : null;
                var asset = new ReleaseAsset(name, url, a.GetProperty("size").GetInt64(), digest);
                if (SetupAsset().Match(name) is { Success: true } m && m.Groups["v"].Value == version) setup = asset;
                else if (CliAsset().Match(name) is { Success: true } c && c.Groups["v"].Value == version) cli = asset;
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = asset;
            }
            if (setup is null) continue;  // not a complete release
            var release = new FluentSetupRelease(version, tag, r.GetProperty("html_url").GetString() ?? ReleasesPage,
                                                 r.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", prerelease, setup, cli, sums);
            if (best is null || CompareVersions(release.Version, best.Version) > 0) best = release;
        }
        return best;
    }

    /// <summary>
    /// Downloads an asset into a new private folder and verifies its size and SHA-256 (from SHA256SUMS.txt, and GitHub's
    /// digest when it has one; at least one is required). Returns the file path.
    /// </summary>
    public static async Task<string> DownloadVerifiedAsync(HttpClient http, FluentSetupRelease release, ReleaseAsset asset,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string? expected = asset.Sha256;
        if (release.Checksums is { } sums)
        {
            string text = await http.GetStringAsync(sums.Url, cancellationToken).ConfigureAwait(false);
            foreach (var line in text.Split('\n'))
            {
                if (ChecksumLine().Match(line.Trim()) is { Success: true } m && m.Groups["name"].Value.Equals(asset.Name, StringComparison.OrdinalIgnoreCase))
                {
                    string listed = m.Groups["hash"].Value;
                    if (expected is not null && !expected.Equals(listed, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("SHA256SUMS.txt and GitHub disagree about the file's SHA-256.");
                    expected = listed;
                }
            }
        }
        if (expected is null) throw new InvalidDataException($"No SHA-256 is published for {asset.Name}; it is not installed.");
        if (asset.Size <= 0 || asset.Size > MaxDownload) throw new InvalidDataException($"Unexpected size of {asset.Name}.");

        string folder = Path.Combine(Path.GetTempPath(), "FluentSetup-update-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, asset.Name);
        using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("The download left https.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                done += read;
                if (done > asset.Size) throw new InvalidDataException($"{asset.Name} is larger than announced.");
                sha.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report((double)done / asset.Size);
            }
            if (done != asset.Size) throw new InvalidDataException($"{asset.Name} is incomplete.");
            if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The SHA-256 of {asset.Name} does not match the published one.");
        }
        return path;
    }

    /// <summary>The folder FluentSetup is installed in, if this copy was installed by its own Setup.exe (else null).</summary>
    public static string? InstalledFolder()
    {
        // The Builder lives in the install folder, the CLI in its cli\ sub-folder.
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        foreach (var dir in new[] { here, here.Parent })
        {
            if (dir is not null && File.Exists(Path.Combine(dir.FullName, "uninstall.json")) && File.Exists(Path.Combine(dir.FullName, "uninstall.exe"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>Starts the downloaded Setup.exe: progress window only (/passive); /relaunch opens the Builder afterwards.</summary>
    public static System.Diagnostics.Process StartSetup(string setupPath, bool relaunch) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            Arguments = relaunch ? "/passive /relaunch" : "/passive",
            WorkingDirectory = Path.GetDirectoryName(setupPath)!,
        }) ?? throw new InvalidOperationException("Setup did not start.");

    /// <summary>Numeric comparison part by part; a release is newer than its pre-releases (same rules as the installer).</summary>
    public static int CompareVersions(string a, string b)
    {
        static (long[] Parts, string Tag) Split(string v)
        {
            var dash = v.IndexOf('-', StringComparison.Ordinal);
            var core = dash < 0 ? v : v[..dash];
            var parts = core.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
            return (parts, dash < 0 ? "" : v[(dash + 1)..]);
        }
        var (pa, ta) = Split(a);
        var (pb, tb) = Split(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (ta.Length == 0 && tb.Length == 0) return 0;
        if (ta.Length == 0) return 1;   // a release is newer than its pre-releases
        if (tb.Length == 0) return -1;
        var sa = ta.Split('.');
        var sb = tb.Split('.');
        for (int i = 0; i < Math.Min(sa.Length, sb.Length); i++)
        {
            bool na = long.TryParse(sa[i], out var x), nb = long.TryParse(sb[i], out var y);
            int c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(sa[i], sb[i]);
            if (c != 0) return c;
        }
        return sa.Length.CompareTo(sb.Length);
    }
}
