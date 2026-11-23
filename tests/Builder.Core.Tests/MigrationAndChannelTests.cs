using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>install.legacyMsi (taking over an MSI installation) and update channels: validation, header, feed writing.</summary>
public sealed class MigrationAndChannelTests
{
    private const string Code = "{12345678-90AB-CDEF-1234-567890ABCDEF}";
    private static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    private static SetupProject Project(TempDir dir)
    {
        dir.File("app/App.exe", "MZ fake");
        return new SetupProject
        {
            App = new AppInfo { Id = "Test.Migrate", Name = "Migrate", Publisher = "Me", Version = "1.2.3", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
            Output = new OutputSettings { Directory = "out", Compression = "fast", IncludeWinUI = false },
        };
    }

    private static List<string> Codes(SetupProject p, string dir) => ProjectValidator.Validate(p, dir).Items.Select(d => d.Code).ToList();

    // ---- install.legacyMsi ---------------------------------------------------------------------------------------

    [Fact]
    public void Legacy_msi_codes_are_checked()
    {
        using var dir = new TempDir();
        var p = Project(dir);
        p.Install.LegacyMsi = new LegacyMsiSettings { ProductCodes = [Code.ToLowerInvariant()], UpgradeCodes = [Code] };
        Assert.DoesNotContain("FS1684", Codes(p, dir.Path));
        Assert.DoesNotContain("FS1685", Codes(p, dir.Path));

        p.Install.LegacyMsi = new LegacyMsiSettings { UpgradeCodes = ["12345678-90AB-CDEF-1234-567890ABCDEF"] };  // no braces
        Assert.Contains("FS1684", Codes(p, dir.Path));
        p.Install.LegacyMsi = new LegacyMsiSettings { ProductCodes = [.. Enumerable.Repeat(Code, 17)] };
        Assert.Contains("FS1684", Codes(p, dir.Path));

        p.Install.LegacyMsi = new LegacyMsiSettings();
        var empty = ProjectValidator.Validate(p, dir.Path).Items.Single(d => d.Code == "FS1685");
        Assert.Equal(Severity.Warning, empty.Severity);
        p.Install.LegacyMsi = null;
        Assert.DoesNotContain("FS1685", Codes(p, dir.Path));
    }

    [Fact]
    public void Legacy_msi_round_trips_through_the_project_file()
    {
        using var dir = new TempDir();
        var p = Project(dir);
        var path = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, path);
        Assert.Contains("\"legacyMsi\": null", File.ReadAllText(path), StringComparison.Ordinal);
        p.Install.LegacyMsi = new LegacyMsiSettings { UpgradeCodes = [Code] };
        ProjectSerializer.Save(p, path);
        var back = ProjectSerializer.Load(path);
        Assert.Equal([Code], back.Install.LegacyMsi!.UpgradeCodes);
        Assert.Empty(back.Install.LegacyMsi.ProductCodes);
    }

    [Fact]
    public async Task Header_carries_legacy_msi_codes_and_the_channel()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeRoot, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        var p = Project(dir);
        p.Install.LegacyMsi = new LegacyMsiSettings { ProductCodes = [Code.ToLowerInvariant()], UpgradeCodes = [Code, Code.ToLowerInvariant()] };
        p.Update = new UpdateSettings { Feed = "https://contoso.com/latest.json", Channel = "beta" };
        var path = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, path);
        var report = await new PackageBuilder().BuildAsync(p, path, new BuildOptions { RuntimeRoot = RuntimeRoot });
        Assert.True(report.Success, report.Error);
        var header = PackageInspector.Inspect(report.OutputPath!, verifyStreams: false).Header!.RootElement;
        var msi = header.GetProperty("install").GetProperty("legacyMsi");
        Assert.Equal(Code, Assert.Single(msi.GetProperty("productCodes").EnumerateArray()).GetString());  // upper case, once
        Assert.Equal(Code, Assert.Single(msi.GetProperty("upgradeCodes").EnumerateArray()).GetString());
        Assert.Equal("beta", header.GetProperty("update").GetProperty("channel").GetString());

        // Without the settings the header has neither (stable is the installer's default).
        p.Install.LegacyMsi = null;
        p.Update.Channel = "stable";
        report = await new PackageBuilder().BuildAsync(p, path, new BuildOptions { RuntimeRoot = RuntimeRoot });
        Assert.True(report.Success, report.Error);
        header = PackageInspector.Inspect(report.OutputPath!, verifyStreams: false).Header!.RootElement;
        Assert.False(header.GetProperty("install").TryGetProperty("legacyMsi", out _));
        Assert.False(header.GetProperty("update").TryGetProperty("channel", out _));
    }

    // ---- update channels --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("stable", true)]
    [InlineData("beta", true)]
    [InlineData("insider-2", true)]
    [InlineData("", false)]
    [InlineData("Beta", false)]
    [InlineData("2beta", false)]
    [InlineData("be ta", false)]
    [InlineData("beta.1", false)]
    public void Channel_names(string name, bool valid) => Assert.Equal(valid, UpdateFeed.IsValidChannel(name));

    [Fact]
    public void Channel_is_checked()
    {
        using var dir = new TempDir();
        var p = Project(dir);
        p.Update = new UpdateSettings { Feed = "https://contoso.com/latest.json", Channel = "beta" };
        Assert.DoesNotContain("FS1711", Codes(p, dir.Path));
        p.Update.Channel = "";  // = stable
        Assert.DoesNotContain("FS1711", Codes(p, dir.Path));
        p.Update.Channel = "Beta Channel";
        Assert.Contains("FS1711", Codes(p, dir.Path));
        // Without a source a channel has no effect.
        p.Update = new UpdateSettings { Channel = "beta" };
        Assert.Contains("FS1709", Codes(p, dir.Path));
        p.Update = new UpdateSettings();
        Assert.Equal("stable", p.Update.Channel);
        Assert.DoesNotContain("FS1709", Codes(p, dir.Path));
    }

    private static readonly UpdateFeed.Entry Stable = new("1.2.0", "https://contoso.com/App-1.2.0.exe", new string('A', 64), 100, "https://contoso.com/notes/1.2.0");
    private static readonly UpdateFeed.Entry Beta = new("1.3.0-beta.1", "https://contoso.com/App-1.3.0-beta.1.exe", new string('b', 64), 120, null);

    [Fact]
    public void Feed_without_channels_is_the_old_format()
    {
        var feed = JsonNode.Parse(UpdateFeed.Merge(null, "stable", Stable))!.AsObject();
        Assert.Equal("1.2.0", (string?)feed["version"]);
        Assert.Equal("https://contoso.com/App-1.2.0.exe", (string?)feed["url"]);
        Assert.Equal(new string('a', 64), (string?)feed["sha256"]);  // lower case
        Assert.Equal(100, (long)feed["size"]!);
        Assert.Equal("https://contoso.com/notes/1.2.0", (string?)feed["notes"]);
        Assert.Null(feed["channels"]);
    }

    [Fact]
    public void Feed_channels_are_merged()
    {
        // A beta into a stable feed: the stable release stays as it is.
        var withBeta = UpdateFeed.Merge(UpdateFeed.Merge(null, "stable", Stable), "beta", Beta);
        var feed = JsonNode.Parse(withBeta)!.AsObject();
        Assert.Equal("1.2.0", (string?)feed["version"]);
        Assert.Equal("1.3.0-beta.1", (string?)feed["channels"]!["beta"]!["version"]);
        Assert.Null(feed["channels"]!["beta"]!["notes"]);

        // The next stable release keeps the beta channel and fields FluentSetup does not know; its notes replace the old ones.
        feed["publishedBy"] = "ci";
        var next = JsonNode.Parse(UpdateFeed.Merge(feed.ToJsonString(), "stable", Stable with { Version = "1.2.1", Notes = null }))!.AsObject();
        Assert.Equal("1.2.1", (string?)next["version"]);
        Assert.Null(next["notes"]);
        Assert.Equal("ci", (string?)next["publishedBy"]);
        Assert.Equal("1.3.0-beta.1", (string?)next["channels"]!["beta"]!["version"]);

        // A channel can come first.
        var betaOnly = JsonNode.Parse(UpdateFeed.Merge("", "beta", Beta))!.AsObject();
        Assert.Null(betaOnly["version"]);
        Assert.Equal("1.3.0-beta.1", (string?)betaOnly["channels"]!["beta"]!["version"]);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("not json")]
    [InlineData("{\"version\":\"1.0.0\",\"channels\":[]}")]
    public void Unusable_feed_files_are_refused(string existing) =>
        Assert.Throws<InvalidDataException>(() => UpdateFeed.Merge(existing, "beta", Beta));

    [Fact]
    public async Task Cli_merges_a_channel_into_the_feed()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeRoot, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        var p = Project(dir);
        p.Update = new UpdateSettings { Feed = "https://contoso.com/latest.json" };
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, projectPath);
        var feedPath = Path.Combine(dir.Path, "feed", "latest.json");
        string[] common = ["build", projectPath, "--runtime", RuntimeRoot, "--feed", feedPath, "--feed-url", "https://contoso.com/{FileName}"];

        Assert.Equal(0, await global::FluentSetup.Cli.Program.Main(common));
        p.App.Version = "1.3.0-beta.1";
        ProjectSerializer.Save(p, projectPath);
        Assert.Equal(0, await global::FluentSetup.Cli.Program.Main([.. common, "--feed-channel", "beta"]));

        using var feed = JsonDocument.Parse(File.ReadAllText(feedPath));
        Assert.Equal("1.2.3", feed.RootElement.GetProperty("version").GetString());
        var beta = feed.RootElement.GetProperty("channels").GetProperty("beta");
        Assert.Equal("1.3.0-beta.1", beta.GetProperty("version").GetString());
        Assert.Equal("https://contoso.com/Migrate-1.3.0-beta.1-Setup.exe", beta.GetProperty("url").GetString());
        Assert.Equal(64, beta.GetProperty("sha256").GetString()!.Length);
    }

    [Theory]
    [InlineData("--feed-channel", "Beta")]
    [InlineData("--feed-channel", "be ta")]
    public async Task Cli_refuses_bad_channels_before_building(string option, string value)
    {
        using var dir = new TempDir();
        var p = Project(dir);
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, projectPath);
        Assert.Equal(2, await global::FluentSetup.Cli.Program.Main(["build", projectPath, "--runtime", RuntimeRoot, "--feed", Path.Combine(dir.Path, "f.json"),
            "--feed-url", "https://contoso.com/{FileName}", option, value]));
        Assert.Equal(2, await global::FluentSetup.Cli.Program.Main(["build", projectPath, "--runtime", RuntimeRoot, "--feed-channel", "beta"]));  // without --feed
        // An existing feed file that cannot be merged into stops the build before anything is written.
        dir.File("broken.json", "[]");
        Assert.Equal(2, await global::FluentSetup.Cli.Program.Main(["build", projectPath, "--runtime", RuntimeRoot, "--feed", Path.Combine(dir.Path, "broken.json"),
            "--feed-url", "https://contoso.com/{FileName}"]));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "out")));
    }
}
