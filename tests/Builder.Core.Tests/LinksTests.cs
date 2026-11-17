using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>app.supportUrl, app.releaseNotesUrl, app.links and ui.licenseStyle: validation, header, defaults.</summary>
public class LinksTests
{
    private static SetupProject Valid(TempDir dir)
    {
        dir.File("app/App.exe");
        return new SetupProject
        {
            App = new AppInfo { Id = "Test.App", Name = "Test", Publisher = "Me", Version = "1.2.3", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
        };
    }

    private static HashSet<string> Errors(SetupProject p, TempDir dir) =>
        ProjectValidator.Validate(p, dir.Path).Items.Where(d => d.Severity == Severity.Error).Select(d => d.Code).ToHashSet();

    [Theory]
    [InlineData("https://github.com/owner/repo", true)]
    [InlineData("http://example.com/help?x=1#y", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://exa mple.com", false)]
    [InlineData("https://example.com/\"x", false)]
    [InlineData("https://example.com/<b>", false)]
    [InlineData("example.com", false)]
    public void WebUrlsFollowTheInstallersRule(string url, bool ok) => Assert.Equal(ok, ProjectValidator.IsWebUrl(url));

    [Fact]
    public void ValidLinksPass()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.App.SupportUrl = "https://example.com/help";
        p.App.ReleaseNotesUrl = "https://github.com/o/r/releases";
        p.App.Links = [new AppLink { Url = "https://github.com/o/r" }, new AppLink { Url = "https://example.com/forum", Label = new() { ["en-US"] = "Forum" } }];
        p.Ui.LicenseStyle = "inline";
        Assert.Empty(Errors(p, dir));
    }

    [Fact]
    public void BadLinksAreReported()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.App.SupportUrl = "mailto:help@example.com";
        p.App.ReleaseNotesUrl = "releases";
        p.App.Links = [new AppLink { Url = "file:///C:/x" }, new AppLink { Url = "https://a.example", Label = new() { ["en-US"] = " " } },
                       new AppLink { Url = "https://b.example" }, new AppLink { Url = "https://c.example" }, new AppLink { Url = "https://d.example" }];
        p.Ui.LicenseStyle = "checkbox";
        var codes = Errors(p, dir);
        foreach (var c in new[] { "FS1016", "FS1017", "FS1018", "FS1019", "FS1020", "FS1315" }) Assert.Contains(c, codes);
    }

    [Fact]
    public void NewFieldsHaveSafeDefaults()
    {
        var p = new SetupProject();
        Assert.Equal("page", p.Ui.LicenseStyle);
        Assert.Empty(p.App.Links);
        Assert.Equal("", p.App.SupportUrl);
    }

    [Fact]
    public void ProjectRoundTripsLinks()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.App.Links = [new AppLink { Url = "https://github.com/o/r", Label = new() { ["zh-CN"] = "源代码" } }];
        p.App.ReleaseNotesUrl = "https://github.com/o/r/releases";
        var path = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, path);
        var back = ProjectSerializer.Load(path);
        Assert.Equal("https://github.com/o/r", Assert.Single(back.App.Links).Url);
        Assert.Equal("源代码", back.App.Links[0].Label["zh-CN"]);
        Assert.Equal("https://github.com/o/r/releases", back.App.ReleaseNotesUrl);
    }

    [Fact]
    public void IdsAreDerivedFromPublisherAndName()
    {
        Assert.Equal("ContosoLtd.MyNotes", ProjectGuess.MakeId("Contoso Ltd.", "My Notes"));
        Assert.Equal("Notes", ProjectGuess.MakeId("", "Notes!"));
        Assert.True(ProjectGuess.MakeId(new string('a', 50), new string('b', 50)).Length <= 64);
    }

    [Fact]
    public void GuessSkipsUninstallersAndPrefersTheFolderName()
    {
        using var dir = new TempDir();
        dir.File("Notes/unins000.exe", new string('x', 5000));
        dir.File("Notes/Notes.exe", "MZ");
        dir.File("Notes/createdump.exe", new string('x', 9000));
        var guess = ProjectGuess.FromFolder(Path.Combine(dir.Path, "Notes"));
        Assert.Equal("Notes.exe", guess.MainExecutable);
        Assert.DoesNotContain("unins000.exe", guess.Executables);
        Assert.DoesNotContain("createdump.exe", guess.Executables);
    }
}
