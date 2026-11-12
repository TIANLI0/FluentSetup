using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentSetup.Builder.Core.Updates;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>Self-update of FluentSetup: release selection and download verification, against a fake GitHub (no network).</summary>
public sealed class UpdatesTests
{
    private sealed class FakeGitHub(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            var response = responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private const string Api = "https://api.github.com/repos/" + FluentSetupUpdates.Repository + "/releases";
    private const string Dl = "https://github.com/" + FluentSetupUpdates.Repository + "/releases/download";

    private static string Asset(string tag, string name, long size, string? digest = null) =>
        $$"""{ "name": "{{name}}", "browser_download_url": "{{Dl}}/{{tag}}/{{name}}", "size": {{size}}{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} }""";

    private static string Release(string tag, bool draft = false, bool prerelease = false, params string[] assets) =>
        $$"""{ "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}}, "html_url": "https://github.com/x/releases/tag/{{tag}}", "body": "notes {{tag}}", "assets": [{{string.Join(",", assets)}}] }""";

    private static HttpClient Client(Dictionary<string, byte[]> responses) => new(new FakeGitHub(responses));

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Theory]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("2.0.0-rc.1", "1.9.0", 1)]
    public void CompareVersions_orders_like_the_installer(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(FluentSetupUpdates.CompareVersions(a, b)));
        Assert.Equal(-expected, Math.Sign(FluentSetupUpdates.CompareVersions(b, a)));
    }

    [Fact]
    public async Task GetLatest_skips_drafts_prereleases_and_incomplete_releases()
    {
        var json = "[" + string.Join(",",
            Release("v9.0.0", draft: true, assets: Asset("v9.0.0", "FluentSetup-9.0.0-Setup.exe", 10)),
            Release("v8.0.0-beta.1", prerelease: true, assets: Asset("v8.0.0-beta.1", "FluentSetup-8.0.0-beta.1-Setup.exe", 10)),
            Release("v7.0.0", assets: Asset("v7.0.0", "fluentsetup-7.0.0-win-x64.zip", 10)),               // no Setup.exe
            Release("v6.0.0", assets: Asset("v6.0.0", "FluentSetup-5.0.0-Setup.exe", 10)),                 // wrong version in the name
            Release("v1.2.0", assets: [Asset("v1.2.0", "FluentSetup-1.2.0-Setup.exe", 10), Asset("v1.2.0", "fluentsetup-1.2.0-win-x64.zip", 5), Asset("v1.2.0", "SHA256SUMS.txt", 3)]),
            Release("v1.1.0", assets: Asset("v1.1.0", "FluentSetup-1.1.0-Setup.exe", 10))) + "]";
        using var http = Client(new() { [Api] = Encoding.UTF8.GetBytes(json) });

        var stable = await FluentSetupUpdates.GetLatestAsync(http, includePrerelease: false, CancellationToken.None);
        Assert.NotNull(stable);
        Assert.Equal("1.2.0", stable.Version);
        Assert.Equal("FluentSetup-1.2.0-Setup.exe", stable.Setup!.Name);
        Assert.Equal("fluentsetup-1.2.0-win-x64.zip", stable.CliZip!.Name);
        Assert.NotNull(stable.Checksums);

        var pre = await FluentSetupUpdates.GetLatestAsync(http, includePrerelease: true, CancellationToken.None);
        Assert.Equal("8.0.0-beta.1", pre!.Version);
    }

    [Fact]
    public async Task GetLatest_returns_null_without_releases()
    {
        using var http = Client(new() { [Api] = "[]"u8.ToArray() });
        Assert.Null(await FluentSetupUpdates.GetLatestAsync(http, false, CancellationToken.None));
    }

    private static (FluentSetupRelease Release, Dictionary<string, byte[]> Responses) Published(byte[] setup, string? sums, string? digest)
    {
        var name = "FluentSetup-2.0.0-Setup.exe";
        var asset = new ReleaseAsset(name, $"{Dl}/v2.0.0/{name}", setup.Length, digest);
        var sumsAsset = sums is null ? null : new ReleaseAsset("SHA256SUMS.txt", $"{Dl}/v2.0.0/SHA256SUMS.txt", sums.Length, null);
        var responses = new Dictionary<string, byte[]> { [asset.Url] = setup };
        if (sums is not null) responses[sumsAsset!.Url] = Encoding.UTF8.GetBytes(sums);
        return (new FluentSetupRelease("2.0.0", "v2.0.0", "https://example.invalid", "", false, asset, null, sumsAsset), responses);
    }

    [Fact]
    public async Task Download_accepts_a_file_matching_SHA256SUMS_and_the_GitHub_digest()
    {
        var data = Encoding.UTF8.GetBytes("new setup");
        var (release, responses) = Published(data, $"{Sha(Encoding.UTF8.GetBytes("zip"))}  fluentsetup-2.0.0-win-x64.zip\n{Sha(data)}  FluentSetup-2.0.0-Setup.exe\n", Sha(data));
        using var http = Client(responses);
        var path = await FluentSetupUpdates.DownloadVerifiedAsync(http, release, release.Setup!, null, CancellationToken.None);
        try
        {
            Assert.Equal(data, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task Download_rejects_a_tampered_file()
    {
        var (release, responses) = Published(Encoding.UTF8.GetBytes("evil setup"), $"{Sha(Encoding.UTF8.GetBytes("new setup"))}  FluentSetup-2.0.0-Setup.exe\n", null);
        using var http = Client(responses);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => FluentSetupUpdates.DownloadVerifiedAsync(http, release, release.Setup!, null, CancellationToken.None));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public async Task Download_rejects_disagreeing_checksums_and_missing_checksums()
    {
        var data = Encoding.UTF8.GetBytes("new setup");
        var (disagree, r1) = Published(data, $"{Sha(Encoding.UTF8.GetBytes("other"))}  FluentSetup-2.0.0-Setup.exe\n", Sha(data));
        using (var http = Client(r1))
            await Assert.ThrowsAsync<InvalidDataException>(() => FluentSetupUpdates.DownloadVerifiedAsync(http, disagree, disagree.Setup!, null, CancellationToken.None));

        var (none, r2) = Published(data, null, null);
        using (var http = Client(r2))
            await Assert.ThrowsAsync<InvalidDataException>(() => FluentSetupUpdates.DownloadVerifiedAsync(http, none, none.Setup!, null, CancellationToken.None));
    }
}
