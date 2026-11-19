using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

public class AutoVariantTests
{
    private static SetupProject Project(TempDir dir, string selection, bool limitShared = true)
    {
        dir.File("sc/App.exe", "apphost-sc");
        dir.File("sc/coreclr.dll", "runtime");
        dir.File("fd/App.exe", "apphost-fd");
        return new SetupProject
        {
            App = new AppInfo { Id = "Test.Auto", Name = "Auto Test", Publisher = "P", Version = "1.0.0", MainExecutable = "App.exe" },
            Files = new FileSettings
            {
                VariantSelection = selection,
                Variants =
                [
                    new VariantSettings { Id = "selfContained", Label = new() { ["en-US"] = "Self-contained" }, Default = true, Source = "sc" },
                    new VariantSettings { Id = "shared", Label = new() { ["en-US"] = "Shared" }, Source = "fd" },
                ],
            },
            Requirements = new RequirementSettings
            {
                DotNet = [new DotNetRequirement { Runtime = "core", Version = "10.0.0", Variants = limitShared ? ["shared"] : [] }],
            },
            Output = new OutputSettings { Directory = "out", Compression = "fast" },
        };
    }

    private static HashSet<string> Codes(SetupProject p, TempDir dir) =>
        ProjectValidator.Validate(p, dir.Path).Items.Select(d => d.Code).ToHashSet();

    [Fact]
    public void AutoWithARequirementLimitedToTheSharedBuildIsClean()
    {
        using var dir = new TempDir();
        var codes = Codes(Project(dir, "auto"), dir);
        Assert.DoesNotContain("FS1117", codes);
        Assert.DoesNotContain("FS1118", codes);
        Assert.DoesNotContain("FS1119", codes);
    }

    [Fact]
    public void UnknownSelectionIsAnError()
    {
        using var dir = new TempDir();
        Assert.Contains("FS1117", Codes(Project(dir, "smart"), dir));
    }

    [Fact]
    public void AutoWarnsAboutAVariantThatWouldAlwaysWin()
    {
        using var dir = new TempDir();
        Assert.Contains("FS1119", Codes(Project(dir, "auto", limitShared: false), dir));
    }

    [Fact]
    public void AutoWithOneVariantHasNoEffect()
    {
        using var dir = new TempDir();
        var p = Project(dir, "auto");
        p.Files.Variants.RemoveAt(1);
        Assert.Contains("FS1118", Codes(p, dir));
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("user", false)]
    public async Task HeaderCarriesTheSelection(string selection, bool written)
    {
        using var dir = new TempDir();
        var project = Project(dir, selection);
        var path = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(project, path);
        var report = await new PackageBuilder().BuildAsync(project, path, new BuildOptions { RuntimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime") });
        Assert.True(report.Success, report.Error);
        var root = PackageInspector.Inspect(report.OutputPath!, verifyStreams: false).Header!.RootElement;
        Assert.Equal(written, root.TryGetProperty("variantSelection", out var value));
        if (written) Assert.Equal("auto", value.GetString());
        Assert.Equal(2, root.GetProperty("variants").GetArrayLength());
    }

    [Fact]
    public void SelectionRoundTripsThroughTheProjectFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(Project(dir, "auto"), path);
        Assert.Contains("\"variantSelection\": \"auto\"", File.ReadAllText(path));
        Assert.Equal("auto", ProjectSerializer.Load(path).Files.VariantSelection);
    }
}
