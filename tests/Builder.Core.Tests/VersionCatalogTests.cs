using System.Net;
using System.Text;
using FluentSetup.Builder.Core.Prerequisites;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>Version catalogs for the prerequisite pickers: feed parsing (captured samples), caching and offline fallback (no network).</summary>
public sealed class VersionCatalogTests : IDisposable
{
    // Captured from https://edgeupdates.microsoft.com/api/products on 2026-11-14 (CVE lists and most artifacts trimmed).
    private const string EdgeSample = """
        [{"Product":"Stable","Releases":[
          {"ReleaseId":128293,"Platform":"MacOS","Architecture":"universal","CVEs":["CVE-2026-50662"],"SeverityLevel":"Critical","ProductVersion":"152.0.4191.53","Artifacts":[],"PublishedTime":"2026-08-29T19:19:00","ExpectedExpiryDate":"2027-08-28T06:18:00"},
          {"ReleaseId":134970,"Platform":"Windows","Architecture":"arm64","CVEs":["CVE-2026-125342"],"SeverityLevel":"Critical","ProductVersion":"157.0.4341.57","Artifacts":[{"ArtifactName":"msi","Location":"https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/ce47388d-cb8e-43c5-882b-7a69c1183352/MicrosoftEdgeEnterpriseARM64.msi","Hash":"76B5D9F564E629C7A16B77789B19E99419EF511AF3A4FED116C7F0F4A144C015","HashAlgorithm":"SHA256","SizeInBytes":219209728}],"PublishedTime":"2026-11-13T00:52:00","ExpectedExpiryDate":"2027-11-13T00:24:00"},
          {"ReleaseId":134971,"Platform":"Windows","Architecture":"x64","CVEs":[],"SeverityLevel":"Critical","ProductVersion":"157.0.4341.57","Artifacts":[],"PublishedTime":"2026-11-13T00:58:00","ExpectedExpiryDate":"2027-11-13T00:25:00"},
          {"ReleaseId":134972,"Platform":"Windows","Architecture":"x86","CVEs":[],"SeverityLevel":"Critical","ProductVersion":"157.0.4341.56","Artifacts":[],"PublishedTime":"2026-11-13T01:00:00","ExpectedExpiryDate":"2027-11-13T00:30:00"},
          {"ReleaseId":134973,"Platform":"Linux","Architecture":"x64","CVEs":[],"ProductVersion":"158.0.1.1","Artifacts":[],"PublishedTime":"2026-11-13T00:40:00","ExpectedExpiryDate":"2027-11-13T00:40:00"}]},
         {"Product":"Beta","Releases":[
          {"ReleaseId":134900,"Platform":"MacOS","Architecture":"universal","CVEs":[],"ProductVersion":"153.0.4234.13","Artifacts":[],"PublishedTime":"2026-08-31T17:19:00","ExpectedExpiryDate":"2026-11-30T17:19:00"},
          {"ReleaseId":134901,"Platform":"Windows","Architecture":"x64","CVEs":[],"ProductVersion":"158.0.4370.16","Artifacts":[],"PublishedTime":"2026-11-10T23:27:00","ExpectedExpiryDate":"2027-02-10T23:27:00"}]},
         {"Product":"Dev","Releases":[
          {"ReleaseId":134800,"Platform":"Windows","Architecture":"x64","CVEs":[],"ProductVersion":"159.0.4393.0","Artifacts":[],"PublishedTime":"2026-11-10T22:04:00","ExpectedExpiryDate":"2027-02-10T22:04:00"}]},
         {"Product":"EdgeUpdate","Releases":[
          {"ReleaseId":134700,"Platform":"Windows","Architecture":"x86","CVEs":[],"ProductVersion":"1.3.315.17","Artifacts":[],"PublishedTime":"2026-11-03T13:31:00","ExpectedExpiryDate":"2027-11-03T13:31:00"}]}]
        """;

    // Captured from https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/index.json on 2026-11-14 (shortened).
    private const string WindowsAppSdkSample = """
        {
          "versions": [
            "1.0.0-experimental1", "1.0.0-preview1", "1.0.0", "1.0.4", "1.1.0-preview1", "1.1.5",
            "1.2.220727.1-experimental1", "1.2.221109.1", "1.2.230313.1",
            "1.6.240829007", "1.6.250108002", "1.7.250310001", "1.7.250606001",
            "1.8.260710003", "1.8.260804001", "1.8.260921001",
            "2.0.0-experimental2", "2.0.0-preview1", "2.0.1", "2.0.250930001-experimental1", "2.1.3", "2.1.4-experimental8",
            "2.2.0", "2.3.1", "2.4.0", "2.5.1", "2.5.4-experimental", "2.6.4", "2.6.5", "2.6.6-experimental"
          ]
        }
        """;

    // Captured from https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json on 2026-11-14
    // (2.x and older entries and the releases.json links trimmed; a preview channel added).
    private const string DotNetSample = """
        {
          "$schema": "https://json.schemastore.org/dotnet-releases-index.json",
          "releases-index": [
            { "channel-version": "12.0", "latest-release": "12.0.0-preview.1", "latest-release-date": "2027-02-09", "security": false,
              "latest-runtime": "12.0.0-preview.1.27101.1", "latest-sdk": "12.0.100-preview.1", "product": ".NET", "support-phase": "preview", "release-type": "lts" },
            { "channel-version": "11.0", "latest-release": "11.0.0", "latest-release-date": "2026-11-10", "security": true,
              "latest-runtime": "11.0.0", "latest-sdk": "11.0.100", "product": ".NET", "support-phase": "active", "release-type": "sts", "eol-date": "2028-11-14" },
            { "channel-version": "10.0", "latest-release": "10.0.15", "latest-release-date": "2026-11-10", "security": true,
              "latest-runtime": "10.0.15", "latest-sdk": "10.0.404", "product": ".NET", "support-phase": "active", "release-type": "lts", "eol-date": "2028-11-14" },
            { "channel-version": "9.0", "latest-release": "9.0.23", "latest-release-date": "2026-11-10", "security": true,
              "latest-runtime": "9.0.23", "latest-sdk": "9.0.321", "product": ".NET", "support-phase": "eol", "release-type": "sts", "eol-date": "2026-11-10" },
            { "channel-version": "8.0", "latest-release": "8.0.34", "latest-release-date": "2026-11-10", "security": true,
              "latest-runtime": "8.0.34", "latest-sdk": "8.0.428", "product": ".NET", "support-phase": "eol", "release-type": "lts", "eol-date": "2026-11-10" },
            { "channel-version": "3.1", "latest-release": "3.1.32", "latest-release-date": "2022-12-13", "security": true,
              "latest-runtime": "3.1.32", "latest-sdk": "3.1.426", "product": ".NET Core", "support-phase": "eol", "release-type": "lts", "eol-date": "2022-12-13" },
            { "channel-version": "2.1", "latest-release": "2.1.30", "latest-release-date": "2021-08-19", "security": true,
              "latest-runtime": "2.1.30", "latest-sdk": "2.1.818", "product": ".NET Core", "support-phase": "eol", "release-type": "lts", "eol-date": "2021-08-21" }
          ]
        }
        """;

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "fs-versions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Serves fixed bodies per URL (or fails), counting requests.</summary>
    private sealed class FakeFeeds : HttpMessageHandler
    {
        public Dictionary<string, string> Bodies { get; } = [];
        public bool Offline { get; set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Offline) throw new HttpRequestException("No such host is known.");
            var response = Bodies.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void Edge_lists_the_newest_windows_stable_and_beta()
    {
        var list = VersionCatalog.ParseEdge(EdgeSample);
        Assert.Equal(["157.0.4341.57", "158.0.4370.16"], list.Select(v => v.Version));
        Assert.Equal(["Stable", "Beta"], list.Select(v => v.Channel));
        Assert.True(list[0].IsNewest);
        Assert.False(list[1].IsNewest);
        Assert.Equal(new DateOnly(2026, 11, 13), list[0].Released);
    }

    [Fact]
    public void WindowsAppSdk_maps_1x_to_channels_and_keeps_2x_releases()
    {
        var list = VersionCatalog.ParseWindowsAppSdk(WindowsAppSdkSample);
        Assert.Equal(
            ["2.6.5", "2.6.4", "2.5.1", "2.4.0", "2.3.1", "2.2.0", "2.1.3", "2.0.1", "1.8", "1.7", "1.6", "1.2", "1.1", "1.0"],
            list.Select(v => v.Version));
        Assert.True(list[0].IsNewest);
        var one8 = list.Single(v => v.Version == "1.8");
        Assert.Equal("1.8.260921001", one8.Latest);
        Assert.Equal("2.5", list.Single(v => v.Version == "2.5.1").Channel);
        // Every value fits requirements.windowsAppRuntime.version.
        Assert.All(list, v => Assert.Matches(@"^(1\.\d+|[2-9]\d*\.\d+(\.\d+){0,2})$", v.Version));
    }

    [Fact]
    public void DotNet_lists_released_channels_with_support_state()
    {
        var list = VersionCatalog.ParseDotNet(DotNetSample);
        Assert.Equal(["11.0.0", "10.0.0", "9.0.0", "8.0.0", "3.1.0"], list.Select(v => v.Version));
        var ten = list.Single(v => v.Version == "10.0.0");
        Assert.True(ten.IsLts);
        Assert.False(ten.IsEndOfLife);
        Assert.Equal("10.0.15", ten.Latest);
        Assert.Equal(new DateOnly(2028, 11, 14), ten.EndOfSupport);
        Assert.Contains("LTS", ten.Description, StringComparison.Ordinal);
        var nine = list.Single(v => v.Version == "9.0.0");
        Assert.False(nine.IsLts);
        Assert.True(nine.IsEndOfLife);
        Assert.True(list[0].IsNewest);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"Product":"Stable","Releases":[{"Platform":"MacOS","ProductVersion":"150.0.1.2"}]}]""")]
    [InlineData("""{"versions":"x"}""")]
    public void Unexpected_feeds_are_rejected(string json)
    {
        Assert.ThrowsAny<Exception>(() => VersionCatalog.ParseEdge(json));
        Assert.ThrowsAny<Exception>(() => VersionCatalog.ParseWindowsAppSdk(json));
        Assert.ThrowsAny<Exception>(() => VersionCatalog.ParseDotNet(json));
    }

    [Fact]
    public void VcRedist_offers_common_minimums()
    {
        Assert.Equal(["14.44", "14.42", "14.40", "14.36", "14.29", "14.0"], VersionCatalog.VcRedistVersions.Select(v => v.Version));
    }

    [Theory]
    [InlineData("webview2", VersionProduct.WebView2)]
    [InlineData("WindowsAppRuntime", VersionProduct.WindowsAppRuntime)]
    [InlineData("windowsappsdk", VersionProduct.WindowsAppRuntime)]
    [InlineData("dotnet", VersionProduct.DotNet)]
    [InlineData("vcredist", VersionProduct.VcRedist)]
    public void Product_names_parse(string name, VersionProduct expected)
    {
        Assert.True(VersionCatalog.TryParseProduct(name, out var product));
        Assert.Equal(expected, product);
        Assert.False(VersionCatalog.TryParseProduct("java", out _));
    }

    [Fact]
    public async Task Downloads_once_a_day_and_keeps_the_list_on_disk()
    {
        var feeds = new FakeFeeds();
        feeds.Bodies[VersionCatalog.DotNetFeed] = DotNetSample;
        var clock = new Clock(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        using var http = new HttpClient(feeds);

        using (var catalog = new VersionCatalog(http, _cache, clock))
        {
            var first = await catalog.GetAsync(VersionProduct.DotNet);
            Assert.Equal(VersionListSource.Online, first.Source);
            Assert.Equal(clock.Now, first.RetrievedAt);
            Assert.Null(first.Error);
            var again = await catalog.GetAsync(VersionProduct.DotNet);
            Assert.Equal(VersionListSource.Online, again.Source);  // the same list from memory
            Assert.Equal(1, feeds.Requests);
        }

        // A new catalog (the next start of the Builder) reads the saved list instead of downloading it.
        clock.Now += TimeSpan.FromHours(20);
        using (var catalog = new VersionCatalog(http, _cache, clock))
        {
            var saved = await catalog.GetAsync(VersionProduct.DotNet);
            Assert.Equal(VersionListSource.Cache, saved.Source);
            Assert.Equal("11.0.0", saved.Versions[0].Version);
            Assert.Equal(1, feeds.Requests);

            // A day later it is downloaded again; "refresh" downloads right away.
            clock.Now += TimeSpan.FromHours(5);
            Assert.Equal(VersionListSource.Online, (await catalog.GetAsync(VersionProduct.DotNet)).Source);
            Assert.Equal(2, feeds.Requests);
            Assert.Equal(VersionListSource.Online, (await catalog.GetAsync(VersionProduct.DotNet, refresh: true)).Source);
            Assert.Equal(3, feeds.Requests);
        }
    }

    [Fact]
    public async Task Offline_falls_back_to_the_saved_list_then_to_the_built_in_one()
    {
        var feeds = new FakeFeeds();
        feeds.Bodies[VersionCatalog.EdgeFeed] = EdgeSample;
        var clock = new Clock(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        using var http = new HttpClient(feeds);
        using (var catalog = new VersionCatalog(http, _cache, clock))
        {
            Assert.Equal(VersionListSource.Online, (await catalog.GetAsync(VersionProduct.WebView2)).Source);
        }

        feeds.Offline = true;
        clock.Now += TimeSpan.FromDays(3);
        using (var catalog = new VersionCatalog(http, _cache, clock))
        {
            var stale = await catalog.GetAsync(VersionProduct.WebView2);
            Assert.Equal(VersionListSource.StaleCache, stale.Source);
            Assert.NotNull(stale.Error);
            Assert.Equal("157.0.4341.57", stale.Versions[0].Version);
            Assert.Equal(clock.Now - TimeSpan.FromDays(3), stale.RetrievedAt);

            var builtIn = await catalog.GetAsync(VersionProduct.WindowsAppRuntime);
            Assert.Equal(VersionListSource.BuiltIn, builtIn.Source);
            Assert.NotNull(builtIn.Error);
            Assert.NotEmpty(builtIn.Versions);
        }
    }

    [Fact]
    public async Task A_broken_feed_is_not_saved()
    {
        var feeds = new FakeFeeds();
        feeds.Bodies[VersionCatalog.WindowsAppSdkFeed] = "<html>maintenance</html>";
        using var http = new HttpClient(feeds);
        using var catalog = new VersionCatalog(http, _cache);
        var list = await catalog.GetAsync(VersionProduct.WindowsAppRuntime);
        Assert.Equal(VersionListSource.BuiltIn, list.Source);
        Assert.NotNull(list.Error);
        Assert.False(File.Exists(Path.Combine(_cache, "windowsappruntime.json")));
    }

    [Fact]
    public async Task VcRedist_needs_no_network()
    {
        var feeds = new FakeFeeds { Offline = true };
        using var http = new HttpClient(feeds);
        using var catalog = new VersionCatalog(http, "");
        var list = await catalog.GetAsync(VersionProduct.VcRedist);
        Assert.Equal(VersionListSource.BuiltIn, list.Source);
        Assert.Null(list.Error);
        Assert.Equal(0, feeds.Requests);
    }
}
