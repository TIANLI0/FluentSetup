using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>--arch, variant collection, the CLI's argument checks and diagnostic code uniqueness.</summary>
public class BuildFixesTests
{
    private static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Builder.Core"))) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task ArchitectureOverrideReachesFileNameAndUpdateAsset()
    {
        if (!File.Exists(Path.Combine(RuntimeRoot, "arm64", "FluentSetup.Stub.exe"))) return;  // arm64 runtime not built
        using var dir = new TempDir();
        dir.File("app/App.exe", "MZ fake");
        var project = new SetupProject
        {
            App = new AppInfo { Id = "Test.Arch", Name = "Arch", Publisher = "P", Version = "1.0.0", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
            Output = new OutputSettings { Directory = "out", Compression = "fast", IncludeWinUI = false, FileName = "{AppName}-{Version}-{Architecture}.exe" },
            Update = new UpdateSettings { GitHub = "contoso/arch" },
        };
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(project, projectPath);

        var arm = await new PackageBuilder().BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, Architecture = "arm64" });
        Assert.True(arm.Success, arm.Error);
        Assert.Equal("Arch-1.0.0-arm64.exe", Path.GetFileName(arm.OutputPath));
        Assert.Equal("arm64", arm.Architecture);
        var update = PackageInspector.Inspect(arm.OutputPath!, verifyStreams: false).Header!.RootElement.GetProperty("update");
        Assert.Equal("Arch-{Version}-arm64.exe", update.GetProperty("asset").GetString());
        Assert.Equal("x64", project.Output.Architecture);  // the caller's project is unchanged

        // The x64 build of the same project does not overwrite it.
        var x64 = await new PackageBuilder().BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot });
        Assert.True(x64.Success, x64.Error);
        Assert.Equal("Arch-1.0.0-x64.exe", Path.GetFileName(x64.OutputPath));
        Assert.True(File.Exists(arm.OutputPath));
    }

    private static (TempDir Dir, SetupProject Project) VariantProject()
    {
        var dir = new TempDir();
        dir.File("builds/a/App.exe", "MZ a");
        dir.File("builds/a/common.dll", "same");
        dir.File("builds/b/App.exe", "MZ b");
        dir.File("builds/b/common.dll", "same");
        var project = new SetupProject
        {
            App = new AppInfo { Id = "Test.Variants", Name = "Var App", Version = "2.0.0", MainExecutable = "App.exe" },
            Files = new FileSettings
            {
                Variants =
                [
                    new VariantSettings { Id = "a", Label = { ["en-US"] = "A" }, Default = true, Source = "builds/a" },
                    new VariantSettings { Id = "b", Label = { ["en-US"] = "B" }, Source = "builds/b" },
                ],
            },
            Output = new OutputSettings { Directory = "builds/a", FileName = "{AppName}-{Version}-Setup.exe" },
        };
        return (dir, project);
    }

    [Fact]
    public void VariantCollectionLeavesTheInstallerOut()
    {
        var (dir, project) = VariantProject();
        using var _ = dir;
        // A Setup.exe of an earlier build in the output folder, which is a variant's source.
        dir.File("builds/a/Var App-2.0.0-Setup.exe", "MZ old installer");
        var result = FileCollector.Collect(project, dir.Path, project.Files.Variants);
        Assert.False(result.Diagnostics.HasErrors, string.Join("; ", result.Diagnostics.Items.Select(d => d.Message)));
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Files, f => f.RelativePath == "App.exe" && f.Variant == "a");
        Assert.Contains(result.Files, f => f.RelativePath == "common.dll" && f.Variant == "");
    }

    [Fact]
    public void VariantCollectionChecksTheMainExecutable()
    {
        var (dir, project) = VariantProject();
        using var _ = dir;
        File.Delete(Path.Combine(dir.Path, "builds", "b", "App.exe"));
        var result = FileCollector.Collect(project, dir.Path, project.Files.Variants);
        var missing = Assert.Single(result.Diagnostics.Items, d => d.Code == "FS1110");
        Assert.StartsWith("files.variants[b]", missing.Field, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--define=Version=1.2.3")]
    [InlineData("-D=Version=1.2.3")]
    public void DefineWithEqualsSign(string arg)
    {
        var args = global::FluentSetup.Cli.ParsedArgs.Parse(["build", arg, "--define", "Channel=beta", "-D", "Dir=C:\\out\\"]);
        var defines = args.Defines()!;
        Assert.Equal("1.2.3", defines["Version"]);
        Assert.Equal("beta", defines["Channel"]);
        Assert.Equal("C:\\out\\", defines["Dir"]);
        args.EnsureAllUsed();  // nothing was taken for an unknown option
    }

    [Theory]
    [InlineData("http://contoso.com/{FileName}", null)]
    [InlineData("https://contoso.com/{FileName}", "ftp://contoso.com/notes")]
    [InlineData("not a url", null)]
    public async Task FeedUrlsAreCheckedBeforeTheBuild(string url, string? notes)
    {
        using var dir = new TempDir();
        dir.File("app/App.exe", "MZ fake");
        var project = new SetupProject
        {
            App = new AppInfo { Id = "Test.Feed", Name = "Feed", Publisher = "P", Version = "1.0.0", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
            Output = new OutputSettings { Directory = "out", Compression = "fast" },
        };
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(project, projectPath);
        List<string> args = ["build", projectPath, "--runtime", RuntimeRoot, "--feed", Path.Combine(dir.Path, "feed.json"), "--feed-url", url];
        if (notes is not null) args.AddRange(["--feed-notes", notes]);
        Assert.Equal(2, await global::FluentSetup.Cli.Program.Main([.. args]));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "out")));  // nothing was built
        Assert.False(File.Exists(Path.Combine(dir.Path, "feed.json")));
    }

    /// <summary>
    /// A diagnostic code means one thing. Codes are emitted where the check is; a code that appears in two places of
    /// src/ (besides the importer, which maps them) has to be one check made twice, listed here with the reason.
    /// </summary>
    [Fact]
    public void DiagnosticCodesAreNotReused()
    {
        var sharedOnPurpose = new Dictionary<string, string>
        {
            ["FS1306"] = "license file too large: checked for ui.license(s) and for license files named in localization files",
            ["FS1662"] = "unknown variant: output.variants (validator) and --variant (builder)",
        };
        var src = Path.Combine(FindRepoRoot(), "src");
        var sites = new Dictionary<string, HashSet<string>>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.StartsWith("Builder.Core/Import/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"(FS[0-9]{4})\""))
            {
                if (!sites.TryGetValue(m.Groups[1].Value, out var files)) sites[m.Groups[1].Value] = files = [];
                files.Add(rel);
            }
        }
        Assert.NotEmpty(sites);
        var reused = sites.Where(s => s.Value.Count > 1 && !sharedOnPurpose.ContainsKey(s.Key))
                          .Select(s => $"{s.Key}: {string.Join(", ", s.Value.Order())}").ToList();
        Assert.True(reused.Count == 0, "Diagnostic codes used in more than one file:" + Environment.NewLine + string.Join(Environment.NewLine, reused));
    }

    [Fact]
    public void FileCollectorCodesDifferFromVariantCodes()
    {
        // FS1111-FS1119 are the variant checks of the validator; the collector has its own codes.
        using var dir = new TempDir();
        var project = new SetupProject { App = new AppInfo { Name = "Empty", Version = "1.0.0" }, Files = new FileSettings { Source = "empty" } };
        Directory.CreateDirectory(Path.Combine(dir.Path, "empty"));
        var result = FileCollector.Collect(project, dir.Path);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "FS1124");
        Assert.DoesNotContain(result.Diagnostics.Items, d => d.Code == "FS1115");
    }

    /// <summary>FluentSetup.MSBuild.targets: a -D value ending in a backslash does not swallow its closing quote.</summary>
    [Fact]
    public async Task MSBuildDefinesEndingInABackslash()
    {
        if (!OperatingSystem.IsWindows()) return;  // the fake fsetup is a .cmd file
        using var dir = new TempDir();
        var targets = Path.Combine(FindRepoRoot(), "src", "MSBuild", "build", "FluentSetup.MSBuild.targets");
        dir.File("fluentsetup.json", "{}");
        dir.File("fake-fsetup.cmd", "@echo %*> \"%~dp0args.txt\"\r\n");
        var proj = dir.File("App.proj", $"""
            <Project>
              <PropertyGroup>
                <FluentSetupProject>fluentsetup.json</FluentSetupProject>
                <FluentSetupCliPath>fake-fsetup.cmd</FluentSetupCliPath>
                <FluentSetupDefines>Dir=C:\out\;Plain=x</FluentSetupDefines>
                <PublishDir>pub\</PublishDir>
                <Version>1.0.0</Version>
              </PropertyGroup>
              <Import Project="{targets}" />
            </Project>
            """);
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            ArgumentList = { "msbuild", proj, "-t:FluentSetupBuild", "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:q" },
            WorkingDirectory = dir.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using (var process = System.Diagnostics.Process.Start(psi)!)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await errors);
        }
        var args = File.ReadAllText(Path.Combine(dir.Path, "args.txt"));
        // Windows' rule: 2n backslashes before a quote are n backslashes and the quote ends the argument.
        Assert.Contains(@"-D ""Dir=C:\out\\"" ", args, StringComparison.Ordinal);
        Assert.Contains(@"-D ""Plain=x""", args, StringComparison.Ordinal);
    }
}
