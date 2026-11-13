using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>A package built on Linux or macOS must be the one Windows would get: Windows path rules everywhere.</summary>
public class CrossPlatformPathTests
{
    [Fact]
    public void InvalidFileNameCharsAreTheWindowsSet()
    {
        Assert.Equal(41, PathRules.InvalidFileNameChars.Length);
        foreach (var c in "\"<>|:*?\\/\0\x1F") Assert.Contains(c, PathRules.InvalidFileNameChars);
        if (OperatingSystem.IsWindows()) Assert.Equal(Path.GetInvalidFileNameChars().Order(), PathRules.InvalidFileNameChars.Order());
    }

    [Fact]
    public void OutputFileNameDropsCharactersWindowsRejects()
    {
        var p = new SetupProject { App = new AppInfo { Name = "A:B*C?\"D\" <E>|F/G\\H", Version = "1.0.0" } };
        Assert.Equal("ABCDEFGH-1.0.0-Setup.exe", ProjectValidator.ExpandFileName(p.Output.FileName, p).Replace(" ", "", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("C:\\x", true)]
    [InlineData("C:x", true)]
    [InlineData("\\\\server\\share", true)]
    [InlineData("/etc/passwd", true)]
    [InlineData("\\x", true)]
    [InlineData("bin/x", false)]
    [InlineData("bin\\x", false)]
    [InlineData("**/*.pdb", false)]
    public void RootedInTheWindowsSense(string path, bool rooted) => Assert.Equal(rooted, PathRules.IsRooted(path));

    [Fact]
    public void GlobsMustBeRelativeOnEveryOs()
    {
        using var dir = new TempDir();
        dir.File("app/App.exe");
        var p = new SetupProject
        {
            App = new AppInfo { Id = "T.T", Name = "T", Publisher = "P", Version = "1.0", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app", Exclude = ["C:/secret/*"] },
        };
        Assert.Contains(ProjectValidator.Validate(p, dir.Path).Items, d => d.Code == "FS1106");
    }

    [Fact]
    public void ProjectPathsAcceptBothSeparators()
    {
        using var dir = new TempDir();
        var file = dir.File("bin/Release/App.exe");
        Assert.Equal(Path.GetFullPath(file), ProjectValidator.Resolve(dir.Path, "bin\\Release\\App.exe"));
        Assert.Equal(Path.GetFullPath(file), ProjectValidator.Resolve(dir.Path, "bin/Release/App.exe"));
        Assert.Equal(Path.GetFullPath(file), ProjectValidator.Resolve(Path.Combine(dir.Path, "bin", "Debug"), "..\\Release\\App.exe"));
    }

    [Fact]
    public void ProjectWrittenOnWindowsCollectsEverywhere()
    {
        using var dir = new TempDir();
        dir.File("publish/App.exe");
        dir.File("publish/sub/lib.dll");
        dir.File("extras/tool.exe");
        var p = new SetupProject
        {
            App = new AppInfo { MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "publish\\", Extra = [new ExtraSource { Source = "extras\\tool.exe", Target = "tools\\bin" }] },
        };
        var r = FileCollector.Collect(p, dir.Path);
        Assert.False(r.Diagnostics.HasErrors, string.Join("\n", r.Diagnostics.Items));
        Assert.Equal(["App.exe", "sub\\lib.dll", "tools\\bin\\tool.exe"], r.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TargetsDifferingOnlyInCaseCollide()
    {
        // Windows file names are case-insensitive: "App.exe" and "app.EXE" are the same installed file.
        using var dir = new TempDir();
        dir.File("app/App.exe");
        var extra = dir.File("other/app.EXE");
        var p = new SetupProject { App = new AppInfo { MainExecutable = "App.exe" }, Files = new FileSettings { Source = "app", Extra = [new ExtraSource { Source = extra }] } };
        Assert.Contains(FileCollector.Collect(p, dir.Path).Diagnostics.Items, d => d.Code == "FS1109");
    }

    [Fact]
    public void LinuxOnlyNamesAreRejected()
    {
        if (OperatingSystem.IsWindows()) return;  // such names cannot exist on Windows
        using var dir = new TempDir();
        dir.File("app/App.exe");
        dir.File("app/a\\b.txt");
        dir.File("app/Readme.txt");
        dir.File("app/readme.TXT");
        dir.File("app/what?.txt");
        dir.File("app/.hidden-on-linux");
        var p = new SetupProject { App = new AppInfo { MainExecutable = "App.exe" }, Files = new FileSettings { Source = "app" } };
        var r = FileCollector.Collect(p, dir.Path);
        Assert.Contains(r.Diagnostics.Items, d => d.Code == "FS1107" && d.Message.Contains("a\\b.txt", StringComparison.Ordinal));
        Assert.Contains(r.Diagnostics.Items, d => d.Code == "FS1107" && d.Message.Contains("what?", StringComparison.Ordinal));
        Assert.Contains(r.Diagnostics.Items, d => d.Code == "FS1109");
        Assert.Equal((FileAttributes)0, r.Files.Single(f => f.RelativePath == ".hidden-on-linux").Attributes);  // a dot is not the Hidden attribute
    }

    [Fact]
    public void SymbolicLinksAreNotFollowed()
    {
        using var dir = new TempDir();
        dir.File("app/App.exe");
        var target = dir.File("outside/secret.txt");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir.Path, "app", "secret.txt"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;  // Windows without the symbolic-link privilege (junctions are tested in CollectorTests)
        }
        var p = new SetupProject { App = new AppInfo { MainExecutable = "App.exe" }, Files = new FileSettings { Source = "app" } };
        var r = FileCollector.Collect(p, dir.Path);
        Assert.DoesNotContain(r.Files, f => f.RelativePath.Contains("secret", StringComparison.Ordinal));
        Assert.Equal(1, r.SkippedLinks);
    }
}
