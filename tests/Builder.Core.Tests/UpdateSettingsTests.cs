using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>The project's "update" section: validation, the Setup.exe name the installer looks for, and the header.</summary>
public sealed class UpdateSettingsTests
{
    private static SetupProject Project(TempDir dir, UpdateSettings update, string fileName = "{AppName}-{Version}-Setup.exe")
    {
        dir.File("app/App.exe");
        return new SetupProject
        {
            App = new AppInfo { Id = "Test.App", Name = "Test App", Publisher = "Me", Version = "1.2.3", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
            Output = new OutputSettings { FileName = fileName },
            Update = update,
        };
    }

    private static List<string> Codes(SetupProject p, string dir) =>
        ProjectValidator.Validate(p, dir).Items.Where(d => d.Code.StartsWith("FS17", StringComparison.Ordinal)).Select(d => d.Code).ToList();

    [Fact]
    public void Off_by_default_and_without_findings()
    {
        using var dir = new TempDir();
        var p = Project(dir, new UpdateSettings());
        Assert.False(p.Update.Enabled);
        Assert.Empty(Codes(p, dir.Path));
    }

    [Fact]
    public void GitHub_source_is_valid()
    {
        using var dir = new TempDir();
        var p = Project(dir, new UpdateSettings { GitHub = "contoso/notes", Checksums = "SHA256SUMS.txt" });
        Assert.True(p.Update.Enabled);
        Assert.Empty(Codes(p, dir.Path));
        Assert.Equal("Test App-{Version}-Setup.exe", ProjectValidator.UpdateAssetTemplate(p));
    }

    [Theory]
    [InlineData("contoso", "FS1702")]
    [InlineData("https://github.com/contoso/notes", "FS1702")]
    [InlineData("contoso/notes.git", "FS1702")]
    [InlineData("a/b/c", "FS1702")]
    public void Rejects_repository_names(string repo, string code)
    {
        using var dir = new TempDir();
        Assert.Contains(code, Codes(Project(dir, new UpdateSettings { GitHub = repo }), dir.Path));
    }

    [Fact]
    public void Rejects_both_sources_and_plain_http_feeds()
    {
        using var dir = new TempDir();
        Assert.Contains("FS1701", Codes(Project(dir, new UpdateSettings { GitHub = "a/b", Feed = "https://x.example/f.json" }), dir.Path));
        Assert.Contains("FS1703", Codes(Project(dir, new UpdateSettings { Feed = "http://x.example/f.json" }), dir.Path));
        Assert.Empty(Codes(Project(dir, new UpdateSettings { Feed = "https://x.example/f.json" }), dir.Path));
    }

    [Fact]
    public void Asset_must_be_an_exe_name_with_only_the_version_placeholder()
    {
        using var dir = new TempDir();
        Assert.Contains("FS1704", Codes(Project(dir, new UpdateSettings { GitHub = "a/b", Asset = "dist/App-{Version}.exe" }), dir.Path));
        Assert.Contains("FS1704", Codes(Project(dir, new UpdateSettings { GitHub = "a/b", Asset = "App-{Version}.zip" }), dir.Path));
        Assert.Contains("FS1704", Codes(Project(dir, new UpdateSettings { GitHub = "a/b", Asset = "App-{Build}.exe" }), dir.Path));
        var ok = Project(dir, new UpdateSettings { GitHub = "a/b", Asset = "{AppName}_{Architecture}_v{Version}.exe" });
        Assert.Empty(Codes(ok, dir.Path));
        Assert.Equal("Test App_x64_v{Version}.exe", ProjectValidator.UpdateAssetTemplate(ok));
    }

    [Fact]
    public void Warns_when_the_file_name_holds_this_version_literally()
    {
        // "$(Version)" in output.fileName is replaced before validation: the next release would be looked for under 1.2.3.
        using var dir = new TempDir();
        var p = Project(dir, new UpdateSettings { GitHub = "a/b" }, fileName: "TestApp-1.2.3-Setup.exe");
        Assert.Contains("FS1705", Codes(p, dir.Path));
        Assert.DoesNotContain("FS1705", Codes(Project(dir, new UpdateSettings { GitHub = "a/b" }, fileName: "TestApp-Setup.exe"), dir.Path));
    }

    [Fact]
    public void Warns_about_settings_that_do_nothing()
    {
        using var dir = new TempDir();
        Assert.Contains("FS1709", Codes(Project(dir, new UpdateSettings { Asset = "x.exe" }), dir.Path));
        Assert.Contains("FS1707", Codes(Project(dir, new UpdateSettings { Feed = "https://x.example/f.json", Prerelease = true }), dir.Path));
        Assert.Contains("FS1710", Codes(Project(dir, new UpdateSettings { GitHub = "a/b", Signer = "Contoso Ltd" }), dir.Path));
        Assert.Contains("FS1708", Codes(Project(dir, new UpdateSettings { GitHub = "a/b", Signer = " Contoso" }), dir.Path));
    }

    [Fact]
    public void Round_trips_through_the_project_file()
    {
        using var dir = new TempDir();
        var p = Project(dir, new UpdateSettings { GitHub = "contoso/notes", Prerelease = true, Checksums = "SHA256SUMS.txt", Signer = "Contoso Ltd" });
        var path = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"github\": \"contoso/notes\"", text);
        var back = ProjectSerializer.Load(path);
        Assert.Equal("contoso/notes", back.Update.GitHub);
        Assert.True(back.Update.Prerelease);
        Assert.Equal("Contoso Ltd", back.Update.Signer);
    }

    [Fact]
    public async Task Header_carries_the_update_source()
    {
        var runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
        Assert.True(File.Exists(Path.Combine(runtime, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        var p = Project(dir, new UpdateSettings { GitHub = "contoso/notes", Checksums = "SHA256SUMS.txt" });
        p.Output.Directory = "out";
        p.Output.Compression = "fast";
        var path = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, path);
        var report = await new PackageBuilder().BuildAsync(p, path, new BuildOptions { RuntimeRoot = runtime });
        Assert.True(report.Success, report.Error);
        var update = PackageInspector.Inspect(report.OutputPath!, verifyStreams: false).Header!.RootElement.GetProperty("update");
        Assert.Equal("contoso/notes", update.GetProperty("github").GetString());
        Assert.Equal("Test App-{Version}-Setup.exe", update.GetProperty("asset").GetString());
        Assert.Equal("SHA256SUMS.txt", update.GetProperty("checksums").GetString());
        Assert.False(update.GetProperty("prerelease").GetBoolean());
        Assert.False(update.TryGetProperty("feed", out _));
    }
}
