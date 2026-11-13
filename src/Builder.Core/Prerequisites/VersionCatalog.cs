using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentSetup.Builder.Core.Prerequisites;

/// <summary>Prerequisites whose versions the catalog lists.</summary>
public enum VersionProduct
{
    /// <summary>Microsoft Edge WebView2 Runtime (requirements.webView2.minVersion): Edge versions for Windows.</summary>
    WebView2,
    /// <summary>Windows App SDK runtime (requirements.windowsAppRuntime.version): "1.6" for 1.x, "2.5.1" for 2.x and later.</summary>
    WindowsAppRuntime,
    /// <summary>.NET shared runtimes (requirements.dotNet[].version): "10.0.0" per channel.</summary>
    DotNet,
    /// <summary>Visual C++ v14 Redistributable (requirements.vcRedist.minVersion): common minimums, no online feed.</summary>
    VcRedist,
}

/// <summary>Where a <see cref="VersionList"/> came from.</summary>
public enum VersionListSource
{
    /// <summary>Downloaded from Microsoft just now.</summary>
    Online,
    /// <summary>A download of the last day, kept in memory or on disk.</summary>
    Cache,
    /// <summary>Microsoft could not be reached: an older download kept on disk.</summary>
    StaleCache,
    /// <summary>The list that comes with FluentSetup (Visual C++ always; others when offline and nothing was saved).</summary>
    BuiltIn,
}

/// <summary>A version to offer for a prerequisite's version field.</summary>
public sealed record CatalogVersion
{
    /// <summary>The value for the project field, e.g. "157.0.4341.57", "1.6", "2.5.1", "10.0.0", "14.40".</summary>
    public required string Version { get; init; }
    /// <summary>"Stable"/"Beta" (WebView2), "1.6"/"2.5" (Windows App SDK), "10.0" (.NET), the Visual Studio release (Visual C++).</summary>
    public string Channel { get; init; } = "";
    /// <summary>Newest release of that channel, e.g. the latest .NET 10 runtime "10.0.15" (empty if unknown).</summary>
    public string Latest { get; init; } = "";
    /// <summary>When the newest release of the channel was published (if the feed says).</summary>
    public DateOnly? Released { get; init; }
    /// <summary>.NET: "lts" or "sts".</summary>
    public string ReleaseType { get; init; } = "";
    /// <summary>.NET: "active", "maintenance", "eol", …</summary>
    public string SupportPhase { get; init; } = "";
    /// <summary>.NET: end of support.</summary>
    public DateOnly? EndOfSupport { get; init; }
    /// <summary>The newest entry of the list (the first one).</summary>
    public bool IsNewest { get; init; }
    /// <summary>One line in English, e.g. ".NET 10.0 (LTS, active), latest runtime 10.0.15".</summary>
    public string Description { get; init; } = "";

    [JsonIgnore] public bool IsLts => ReleaseType.Equals("lts", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsEndOfLife => SupportPhase.Equals("eol", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The versions of one prerequisite, newest first.</summary>
public sealed record VersionList(
    VersionProduct Product,
    IReadOnlyList<CatalogVersion> Versions,
    VersionListSource Source,
    DateTimeOffset? RetrievedAt,
    string FeedUrl,
    string? Error);

/// <summary>
/// "Cloud" version lists for the prerequisite version fields: Edge (WebView2) versions for Windows from Microsoft's Edge
/// update API, stable Windows App SDK versions from NuGet and the .NET channels from the .NET release metadata. Each
/// list is downloaded at most once a day (10 s timeout) and kept in memory and under
/// %LocalAppData%\FluentSetup\cache\versions; offline, an older saved list or a built-in one is returned instead.
/// Thread-safe.
/// </summary>
public sealed class VersionCatalog : IDisposable
{
    public const string EdgeFeed = "https://edgeupdates.microsoft.com/api/products";
    public const string WindowsAppSdkFeed = "https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/index.json";
    public const string DotNetFeed = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    /// <summary>How long a downloaded list is used before it is downloaded again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    /// <summary>How long one download may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const int MaxFeedBytes = 4 * 1024 * 1024;

    private static readonly Lazy<VersionCatalog> SharedInstance = new(() => new VersionCatalog());
    /// <summary>One catalog for the whole process (the Builder app).</summary>
    public static VersionCatalog Shared => SharedInstance.Value;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<VersionProduct, VersionList> _memory = [];
    private readonly Dictionary<VersionProduct, Task<VersionList>> _pending = [];

    /// <summary>Folder of the saved feeds (one &lt;product&gt;.json each); null = nothing is saved.</summary>
    public string? CacheFolder { get; }

    public static string DefaultCacheFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentSetup", "cache", "versions");

    /// <param name="http">Client to use (not disposed); null = an own client.</param>
    /// <param name="cacheFolder">Where downloaded feeds are saved; null = <see cref="DefaultCacheFolder"/>, "" = memory only.</param>
    /// <param name="time">Clock (tests).</param>
    public VersionCatalog(HttpClient? http = null, string? cacheFolder = null, TimeProvider? time = null)
    {
        _ownsHttp = http is null;
        _http = http ?? CreateClient();
        CacheFolder = cacheFolder is null ? DefaultCacheFolder : cacheFolder.Length == 0 ? null : cacheFolder;
        _time = time ?? TimeProvider.System;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout + TimeSpan.FromSeconds(2) };
        var version = (typeof(VersionCatalog).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "1.0.0").Split('+')[0];
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluentSetup", version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>"webview2", "windowsappruntime" (also "windowsappsdk", "war"), "dotnet" or "vcredist".</summary>
    public static bool TryParseProduct(string? name, out VersionProduct product)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "webview2": product = VersionProduct.WebView2; return true;
            case "windowsappruntime" or "windowsappsdk" or "war": product = VersionProduct.WindowsAppRuntime; return true;
            case "dotnet" or ".net" or "net": product = VersionProduct.DotNet; return true;
            case "vcredist" or "vc" or "vcruntime": product = VersionProduct.VcRedist; return true;
            default: product = default; return false;
        }
    }

    /// <summary>The feed a product's list comes from ("" for Visual C++, which has none).</summary>
    public static string FeedUrl(VersionProduct product) => product switch
    {
        VersionProduct.WebView2 => EdgeFeed,
        VersionProduct.WindowsAppRuntime => WindowsAppSdkFeed,
        VersionProduct.DotNet => DotNetFeed,
        _ => "",
    };

    /// <summary>
    /// The versions of a prerequisite. Uses a list downloaded within the last day unless <paramref name="refresh"/>;
    /// never throws for network or feed problems (see <see cref="VersionList.Source"/> and <see cref="VersionList.Error"/>).
    /// </summary>
    public Task<VersionList> GetAsync(VersionProduct product, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (product == VersionProduct.VcRedist)
            return Task.FromResult(new VersionList(product, VcRedistVersions, VersionListSource.BuiltIn, null, "", null));
        lock (_lock)
        {
            if (!refresh && _memory.TryGetValue(product, out var cached) && IsFresh(cached.RetrievedAt)) return Task.FromResult(cached);
            if (_pending.TryGetValue(product, out var running)) return running;
            var task = LoadAsync(product, refresh, cancellationToken);
            if (!task.IsCompleted) _pending[product] = task;
            return task;
        }
    }

    private bool IsFresh(DateTimeOffset? at) => at is { } t && _time.GetUtcNow() - t < MaxAge && t <= _time.GetUtcNow() + TimeSpan.FromMinutes(5);

    private async Task<VersionList> LoadAsync(VersionProduct product, bool refresh, CancellationToken cancellationToken)
    {
        try
        {
            var url = FeedUrl(product);
            var saved = ReadSaved(product);
            if (!refresh && saved is { } s && IsFresh(s.At) && TryParse(product, s.Body, out var fromDisk))
            {
                return Remember(new VersionList(product, fromDisk, VersionListSource.Cache, s.At, url, null));
            }
            string? error;
            try
            {
                var body = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
                var versions = Parse(product, body);
                var now = _time.GetUtcNow();
                Save(product, body, now);
                return Remember(new VersionList(product, versions, VersionListSource.Online, now, url, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException)
            {
                error = ex is TaskCanceledException ? $"No answer within {Timeout.TotalSeconds:0} s." : ex.Message;
            }
            if (saved is { } old && TryParse(product, old.Body, out var stale))
            {
                return new VersionList(product, stale, VersionListSource.StaleCache, old.At, url, error);
            }
            return new VersionList(product, BuiltIn(product), VersionListSource.BuiltIn, null, url, error);
        }
        finally
        {
            lock (_lock) _pending.Remove(product);
        }
    }

    private VersionList Remember(VersionList list)
    {
        lock (_lock) _memory[list.Product] = list;
        return list;
    }

    private async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new InvalidDataException("The version list is unexpectedly large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFeedBytes) throw new InvalidDataException("The version list is unexpectedly large.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // ---- disk cache: { "retrievedAt": "<ISO 8601>", "body": <the feed as downloaded> } --------------------------------

    private string? SavedPath(VersionProduct product) =>
        CacheFolder is null ? null : Path.Combine(CacheFolder, product.ToString().ToLowerInvariant() + ".json");

    private (string Body, DateTimeOffset At)? ReadSaved(VersionProduct product)
    {
        var path = SavedPath(product);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("retrievedAt", out var at) || !at.TryGetDateTimeOffset(out var when) ||
                !root.TryGetProperty("body", out var body)) return null;
            return (body.GetRawText(), when);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Save(VersionProduct product, string body, DateTimeOffset at)
    {
        var path = SavedPath(product);
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(CacheFolder!);
            using var doc = JsonDocument.Parse(body);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            using (var file = File.Create(temp))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartObject();
                writer.WriteString("retrievedAt", at);
                writer.WritePropertyName("body");
                doc.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The cache is only an optimization.
        }
    }

    // ---- parsing ------------------------------------------------------------------------------------------------

    /// <summary>Parses a feed of <paramref name="product"/>; throws <see cref="JsonException"/> or <see cref="InvalidDataException"/> if it is not one.</summary>
    public static IReadOnlyList<CatalogVersion> Parse(VersionProduct product, string json) => product switch
    {
        VersionProduct.WebView2 => ParseEdge(json),
        VersionProduct.WindowsAppRuntime => ParseWindowsAppSdk(json),
        VersionProduct.DotNet => ParseDotNet(json),
        _ => VcRedistVersions,
    };

    private static bool TryParse(VersionProduct product, string json, out IReadOnlyList<CatalogVersion> versions)
    {
        try
        {
            versions = Parse(product, json);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            versions = [];
            return false;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateOnly? Date(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? DateOnly.FromDateTime(d.UtcDateTime) : null;

    /// <summary>Numeric version (2 to 4 parts) or null.</summary>
    private static Version? Numeric(string text) =>
        System.Version.TryParse(text, out var v) && v.Major >= 0 && !text.Contains('-', StringComparison.Ordinal) ? v : null;

    /// <summary>Edge Stable and Beta versions for Windows (the newest of x64, x86 and arm64 per channel).</summary>
    public static IReadOnlyList<CatalogVersion> ParseEdge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unexpected Edge product list.");
        var result = new List<CatalogVersion>();
        foreach (var channel in new[] { "Stable", "Beta" })
        {
            var product = doc.RootElement.EnumerateArray().FirstOrDefault(p => Str(p, "Product").Equals(channel, StringComparison.OrdinalIgnoreCase));
            if (product.ValueKind != JsonValueKind.Object || !product.TryGetProperty("Releases", out var releases) || releases.ValueKind != JsonValueKind.Array) continue;
            (Version Number, string Text, DateOnly? Published)? best = null;
            foreach (var r in releases.EnumerateArray())
            {
                if (!Str(r, "Platform").Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                var text = Str(r, "ProductVersion");
                if (Numeric(text) is not { } number) continue;
                if (best is null || number > best.Value.Number) best = (number, text, Date(Str(r, "PublishedTime")));
            }
            if (best is { } b)
            {
                result.Add(new CatalogVersion
                {
                    Version = b.Text, Channel = channel, Latest = b.Text, Released = b.Published,
                    Description = $"Microsoft Edge {channel} for Windows" + (b.Published is { } d ? $", released {d:yyyy-MM-dd}" : ""),
                });
            }
        }
        if (result.Count == 0) throw new InvalidDataException("The Edge product list has no Windows releases.");
        result[0] = result[0] with { IsNewest = true };
        return result;
    }

    /// <summary>
    /// Stable Windows App SDK versions in the form the project uses: one "1.N" entry per 1.x channel (any runtime of
    /// that channel), every "2.N.P" (and later majors) release; newest first.
    /// </summary>
    public static IReadOnlyList<CatalogVersion> ParseWindowsAppSdk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("versions", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unexpected NuGet version list.");
        var stable = list.EnumerateArray()
            .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "")
            .Select(t => (Text: t, Number: Numeric(t)))
            .Where(x => x.Number is not null)
            .Select(x => (x.Text, Number: x.Number!))
            .ToList();
        var entries = new List<(Version Sort, CatalogVersion Entry)>();
        foreach (var channel in stable.Where(x => x.Number.Major == 1).GroupBy(x => x.Number.Minor))
        {
            var latest = channel.MaxBy(x => x.Number);
            var value = $"1.{channel.Key}";
            entries.Add((new Version(1, channel.Key), new CatalogVersion
            {
                Version = value, Channel = value, Latest = latest.Text,
                Description = $"Windows App SDK {value} (any {value} runtime), latest {latest.Text}",
            }));
        }
        foreach (var v in stable.Where(x => x.Number.Major >= 2))
        {
            // The project's format has at most four parts; "2.5.1.0" is written as published.
            var channel = $"{v.Number.Major}.{v.Number.Minor}";
            entries.Add((v.Number, new CatalogVersion { Version = v.Text, Channel = channel, Latest = v.Text, Description = $"Windows App SDK {v.Text}" }));
        }
        if (entries.Count == 0) throw new InvalidDataException("The NuGet version list has no stable Windows App SDK release.");
        var result = entries.OrderByDescending(e => e.Sort).Select(e => e.Entry).DistinctBy(e => e.Version).ToList();
        result[0] = result[0] with { IsNewest = true };
        return result;
    }

    /// <summary>
    /// One "M.m.0" minimum per released .NET channel (3.0 and later: the versions with a Windows Desktop Runtime),
    /// with its latest runtime, release type (LTS/STS) and support phase; newest first.
    /// </summary>
    public static IReadOnlyList<CatalogVersion> ParseDotNet(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("releases-index", out var index) || index.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unexpected .NET release index.");
        var entries = new List<(Version Sort, CatalogVersion Entry)>();
        foreach (var r in index.EnumerateArray())
        {
            var channel = Str(r, "channel-version");
            var latest = Str(r, "latest-runtime");
            if (Numeric(channel) is not { } ch || ch.Major < 3) continue;
            if (Numeric(latest) is null) continue;  // a preview channel: nothing released yet
            var type = Str(r, "release-type");
            var phase = Str(r, "support-phase");
            var eol = Date(Str(r, "eol-date"));
            var parts = new List<string>();
            if (type.Length > 0) parts.Add(type.ToUpperInvariant());
            if (phase.Length > 0) parts.Add(phase == "eol" ? "end of support" : phase);
            var description = $".NET {channel}" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "") + $", latest runtime {latest}" +
                              (eol is { } e ? (phase == "eol" ? $", support ended {e:yyyy-MM-dd}" : $", supported until {e:yyyy-MM-dd}") : "");
            entries.Add((ch, new CatalogVersion
            {
                Version = $"{ch.Major}.{ch.Minor}.0", Channel = channel, Latest = latest, Released = Date(Str(r, "latest-release-date")),
                ReleaseType = type, SupportPhase = phase, EndOfSupport = eol, Description = description,
            }));
        }
        if (entries.Count == 0) throw new InvalidDataException("The .NET release index has no released channel.");
        var result = entries.OrderByDescending(e => e.Sort).Select(e => e.Entry).DistinctBy(e => e.Version).ToList();
        result[0] = result[0] with { IsNewest = true };
        return result;
    }

    /// <summary>
    /// Common minimum versions of the Visual C++ 2015-2022 (v14) Redistributable. There is no version feed: Microsoft's
    /// official installer (aka.ms/vs/17/release/vc_redist.&lt;arch&gt;.exe) is always the latest one.
    /// </summary>
    public static IReadOnlyList<CatalogVersion> VcRedistVersions { get; } =
    [
        new() { Version = "14.44", Channel = "Visual Studio 2022 17.14", IsNewest = true, Description = "Visual C++ 14.44 (Visual Studio 2022 17.14)" },
        new() { Version = "14.42", Channel = "Visual Studio 2022 17.12", Description = "Visual C++ 14.42 (Visual Studio 2022 17.12)" },
        new() { Version = "14.40", Channel = "Visual Studio 2022 17.10", Description = "Visual C++ 14.40 (Visual Studio 2022 17.10)" },
        new() { Version = "14.36", Channel = "Visual Studio 2022 17.6", Description = "Visual C++ 14.36 (Visual Studio 2022 17.6)" },
        new() { Version = "14.29", Channel = "Visual Studio 2019 16.11", Description = "Visual C++ 14.29 (Visual Studio 2019 16.10 and 16.11)" },
        new() { Version = "14.0", Channel = "Visual Studio 2015", Description = "Any Visual C++ 2015-2022 (v14) runtime" },
    ];

    /// <summary>A short list for when Microsoft cannot be reached and nothing was saved before.</summary>
    public static IReadOnlyList<CatalogVersion> BuiltIn(VersionProduct product) => product switch
    {
        VersionProduct.WindowsAppRuntime =>
        [
            new() { Version = "2.5.1", Channel = "2.5", Latest = "2.5.1", IsNewest = true, Description = "Windows App SDK 2.5.1" },
            new() { Version = "1.8", Channel = "1.8", Description = "Windows App SDK 1.8 (any 1.8 runtime)" },
            new() { Version = "1.7", Channel = "1.7", Description = "Windows App SDK 1.7 (any 1.7 runtime)" },
            new() { Version = "1.6", Channel = "1.6", Description = "Windows App SDK 1.6 (any 1.6 runtime)" },
        ],
        VersionProduct.DotNet =>
        [
            new() { Version = "10.0.0", Channel = "10.0", ReleaseType = "lts", IsNewest = true, Description = ".NET 10.0 (LTS)" },
            new() { Version = "9.0.0", Channel = "9.0", ReleaseType = "sts", Description = ".NET 9.0 (STS)" },
            new() { Version = "8.0.0", Channel = "8.0", ReleaseType = "lts", Description = ".NET 8.0 (LTS)" },
        ],
        VersionProduct.VcRedist => VcRedistVersions,
        _ => [],
    };
}
