using System.Text.Json;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fsetup-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string rel, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }
    public void Dispose()
    {
        // Remove junctions first (never recurse through them), then the tree.
        foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
        {
            try { Directory.Delete(d); } catch (IOException) { }
        }
        try { Directory.Delete(Path, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public class PathRulesTests
{
    [Theory]
    [InlineData("bin\\app.exe")]
    [InlineData("bin/sub/x.dll")]
    [InlineData("中文\\说明.txt")]
    public void AcceptsNormalPaths(string p) => Assert.True(PathRules.TryNormalizeRelative(p, out _, out _));

    [Theory]
    [InlineData("..\\evil.dll")]
    [InlineData("a/../../b")]
    [InlineData("C:\\x")]
    [InlineData("\\\\srv\\share")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("trailing.")]
    [InlineData("")]
    public void RejectsUnsafePaths(string p) => Assert.False(PathRules.TryNormalizeRelative(p, out _, out _));

    [Fact]
    public void ReservedInstallerNames()
    {
        Assert.True(PathRules.IsInstallerReserved("uninstall.exe"));
        Assert.True(PathRules.IsInstallerReserved(".fsetup\\x"));
        Assert.False(PathRules.IsInstallerReserved("bin\\uninstall.exe"));
    }

    [Theory]
    [InlineData("Contoso.Notes", true)]
    [InlineData("a_b-c", true)]
    [InlineData(".bad", false)]
    [InlineData("has space", false)]
    [InlineData("..\\x", false)]
    public void AppIds(string id, bool ok) => Assert.Equal(ok, PathRules.IsValidAppId(id));
}

public class GlobTests
{
    [Theory]
    [InlineData("*.pdb", "a.pdb", true)]
    [InlineData("*.pdb", "bin/deep/a.pdb", true)]
    [InlineData("bin/*.dll", "bin/a.dll", true)]
    [InlineData("bin/*.dll", "bin/sub/a.dll", false)]
    [InlineData("bin/**/*.dll", "bin/sub/a.dll", true)]
    [InlineData("**/.git/**", ".git/config", true)]
    [InlineData("config/user-settings.json", "Config\\User-Settings.JSON", true)]
    [InlineData("a?.txt", "ab.txt", true)]
    [InlineData("a?.txt", "a/.txt", false)]
    [InlineData("App.exe", "old/App.exe", true)]    // no separator: any folder
    [InlineData("./App.exe", "App.exe", true)]       // leading ./: top level only
    [InlineData("./App.exe", "old/App.exe", false)]
    public void Matches(string pattern, string path, bool expected) => Assert.Equal(expected, new Glob(pattern).IsMatch(path));
}

public class ValidatorTests
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

    [Fact]
    public void ValidProjectHasNoErrors()
    {
        using var dir = new TempDir();
        var bag = ProjectValidator.Validate(Valid(dir), dir.Path);
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.2.3.4")]
    [InlineData("2.0.0-beta.1")]
    [InlineData("4294967295.0")]
    public void AcceptsVersions(string v) => Assert.True(ProjectValidator.IsValidVersion(v));

    [Theory]
    [InlineData("v1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("")]
    [InlineData("4294967296.0")]  // the installer stores each part in 32 bits
    public void RejectsVersions(string v) => Assert.False(ProjectValidator.IsValidVersion(v));

    [Fact]
    public void ReportsPreciseErrors()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.App.Id = "bad id";
        p.App.Version = "one";
        p.App.Website = "ftp://x";
        p.Install.Scope = "everyone";
        p.Install.UserDir = "{ProgramFiles}\\X";
        p.Install.DesktopShortcut = "maybe";
        p.Ui.Theme = "neon";
        p.Ui.UseSystemAccent = false;
        p.Ui.AccentColor = "blue";
        p.Output.FileName = "setup.msi";
        var codes = ProjectValidator.Validate(p, dir.Path).Items.Where(d => d.Severity == Severity.Error).Select(d => d.Code).ToHashSet();
        foreach (var c in new[] { "FS1002", "FS1007", "FS1009", "FS1201", "FS1205", "FS1301", "FS1303", "FS1403" }) Assert.Contains(c, codes);
    }

    [Fact]
    public void ValidatesPrerequisitesAndCleanup()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.Requirements.WebView2 = new WebView2Settings { Required = true, MinVersion = "x", OnFailure = "abort", Download = new DownloadSettings { Url = "http://example.com/a.exe", Sha256 = "123" } };
        p.Requirements.DotNet.Add(new DotNetRequirement { Runtime = "java", Version = "8", RollForward = "latest" });
        p.Install.LegacyUninstallKeys.Add(p.App.Id);
        p.Install.Cleanup.RemoveShortcuts.Add("Old.exe");
        var codes = ProjectValidator.Validate(p, dir.Path).Items.Where(d => d.Severity == Severity.Error).Select(d => d.Code).ToHashSet();
        foreach (var c in new[] { "FS1606", "FS1607", "FS1660", "FS1614", "FS1617", "FS1619", "FS1650", "FS1653" }) Assert.Contains(c, codes);
    }

    [Fact]
    public void LocalizationFolderMergesPerLanguageTexts()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.Options.Add(new UserOptionSettings { Id = "auto", Label = new() { ["en-US"] = "Start with Windows" } });
        p.Actions.Add(new ActionSettings { Id = "drv", Run = "App.exe", Name = new() { ["en-US"] = "Driver" } });
        p.Ui.Localization = "locales";
        dir.File("locales/license-ja.txt", "規約");
        dir.File("locales/ja-JP.json", """{ "options": { "auto": "自動起動" }, "actions": { "drv": "ドライバー" }, "license": "license-ja.txt", "strings": { "button.next": "進む" } }""");
        dir.File("locales/fr-FR.json", """{ "options": { "auto": "Démarrer avec Windows" }, "strings": { "button.install": "Installer" } }""");
        dir.File("locales/notes.json", "{}");
        var bag = new DiagnosticBag();
        var t = Localization.Load(p, dir.Path, bag);
        Assert.Equal("自動起動", t.OptionLabels["auto"]["ja-JP"]);
        Assert.Equal("Start with Windows", t.OptionLabels["auto"]["en-US"]);
        Assert.Equal("ドライバー", t.ActionNames[0]["ja-JP"]);
        Assert.EndsWith("license-ja.txt", t.LicenseFiles["ja-JP"]);
        Assert.Equal("進む", t.Strings["ja-JP"]["button.next"]);
        Assert.Equal("Installer", t.Strings["fr-FR"]["button.install"]);     // a language the installer does not ship
        Assert.Equal("Démarrer avec Windows", t.OptionLabels["auto"]["fr-FR"]);
        Assert.Contains(bag.Items, d => d.Code == "FS1321");  // "notes" is not a language tag: reported, not used
    }

    [Fact]
    public void UserScopeCannotTargetProgramFiles()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.Install.UserDir = "{ProgramFiles}\\Test";
        Assert.Contains(ProjectValidator.Validate(p, dir.Path).Items, d => d.Code == "FS1203");
    }

    [Fact]
    public void MigrationFeaturesValidate()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.Install.StartMenuFolder = "Contoso";
        p.Install.Cleanup.RemoveFiles.AddRange(["unins*.exe", "unins???.dat", "bin\\old.dll"]);
        p.Install.Cleanup.RemoveShortcuts.Add("Contoso\\Uninstall Test.lnk");
        p.Options.Add(new UserOptionSettings { Id = "auto", Label = new() { ["en-US"] = "Auto" }, QuietDefault = false, Detect = new() { RunValue = "Test", ScheduledTask = "Test\\Task" } });
        p.Uninstall.RemoveData.Add("{InstallDir}\\logs");
        p.Uninstall.UserData.Paths.Add("{InstallDir}\\config");
        p.Uninstall.RemoveRegistryKeys.Add("HKCU\\Software\\Contoso\\Test");
        p.Uninstall.Elevated.RemoveRegistryKeys.Add("HKLM\\Software\\Contoso");
        var bag = ProjectValidator.Validate(p, dir.Path);
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData("bin*\\x.dll")]
    [InlineData("..\\x.exe")]
    [InlineData(".fsetup\\journal.jsonl")]
    public void RejectsDangerousFilePatterns(string pattern) => Assert.False(ExtensionValidator.IsFilePattern(pattern));

    [Theory]
    [InlineData("HKCU\\Software")]
    [InlineData("HKCU\\Software\\Microsoft\\Windows")]
    [InlineData("HKCU\\Software\\Classes\\App")]
    [InlineData("HKCR\\Software\\App")]
    [InlineData("HKLM\\System\\App")]
    public void RejectsSharedRegistryKeys(string key) => Assert.False(ExtensionValidator.IsRemovableRegistryKey(key));

    [Fact]
    public void RejectsInvalidMigrationSettings()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.Install.StartMenuFolder = "A\\B";
        p.Uninstall.RemoveData.Add("{InstallDir}\\.fsetup");
        p.Uninstall.Elevated.RemoveData.Add("{InstallDir}\\logs");
        p.Uninstall.RemoveRegistryKeys.Add("HKLM\\Software\\Contoso");  // per-user list: HKCU only
        var codes = ProjectValidator.Validate(p, dir.Path).Items.Where(d => d.Severity == Severity.Error).Select(d => d.Code).ToHashSet();
        foreach (var c in new[] { "FS1667", "FS1630", "FS1655", "FS1653" }) Assert.Contains(c, codes);
    }
}

public class VariableTests
{
    [Fact]
    public void ExpandsProjectValuesAndOverrides()
    {
        var p = new SetupProject { Variables = new() { ["Version"] = "1.0.0", ["Driver"] = "2.2.0.0" } };
        p.App.Version = "$(Version)";
        p.App.Description = "Driver $(driver), literal $$(x)";
        p.Actions.Add(new ActionSettings { Run = "a.exe", Arguments = ["--v=$(Version)"] });
        var bag = new DiagnosticBag();
        var e = ProjectVariables.Expand(p, new Dictionary<string, string> { ["Version"] = "1.0.1" }, bag);
        Assert.False(bag.HasErrors);
        Assert.Equal("1.0.1", e.App.Version);
        Assert.Equal("Driver 2.2.0.0, literal $(x)", e.App.Description);
        Assert.Equal("--v=1.0.1", e.Actions[0].Arguments[0]);
        Assert.Equal("$(Version)", p.App.Version);  // the original is untouched
    }

    [Fact]
    public void ReportsUnknownVariables()
    {
        var p = new SetupProject();
        p.App.Version = "$(Nope)";
        var bag = new DiagnosticBag();
        var e = ProjectVariables.Expand(p, null, bag);
        Assert.Contains(bag.Items, d => d.Code == "FS1801" && d.Field == "app.version");
        Assert.Equal("$(Nope)", e.App.Version);
    }

    [Theory]
    [InlineData("Version=1.2.3", "Version", "1.2.3")]
    [InlineData("A_b.c=x=y", "A_b.c", "x=y")]
    public void ParsesDefines(string text, string name, string value)
    {
        Assert.True(ProjectVariables.TryParseDefine(text, out var n, out var v));
        Assert.Equal(name, n);
        Assert.Equal(value, v);
    }

    [Theory]
    [InlineData("=1")]
    [InlineData("1x=1")]
    [InlineData("noequals")]
    public void RejectsBadDefines(string text) => Assert.False(ProjectVariables.TryParseDefine(text, out _, out _));
}

public class CollectorTests
{
    [Fact]
    public void VariantsShareIdenticalFilesAndKeepTheRestApart()
    {
        using var dir = new TempDir();
        dir.File("sc/App.exe", "apphost-sc");
        dir.File("sc/App.dll", "same");
        dir.File("sc/coreclr.dll", "runtime");
        dir.File("fd/App.exe", "apphost-fd");
        dir.File("fd/App.dll", "same");
        var p = new SetupProject
        {
            App = new AppInfo { Id = "T", Name = "T", MainExecutable = "App.exe" },
            Files = new FileSettings { Variants = [new VariantSettings { Id = "sc", Source = "sc" }, new VariantSettings { Id = "fd", Source = "fd" }] },
        };
        var all = FileCollector.Collect(p, dir.Path, p.Files.Variants).Files;
        Assert.Contains(all, f => f.RelativePath == "App.dll" && f.Variant == "");                // identical: stored once
        Assert.Contains(all, f => f.RelativePath == "App.exe" && f.Variant == "sc");
        Assert.Contains(all, f => f.RelativePath == "App.exe" && f.Variant == "fd");              // differs: one per variant
        Assert.Contains(all, f => f.RelativePath == "coreclr.dll" && f.Variant == "sc");          // only in one variant
        Assert.Equal(4, all.Count);
        var single = FileCollector.Collect(p, dir.Path, [p.Files.Variants[1]]).Files;           // --variant fd
        Assert.All(single, f => Assert.Equal("", f.Variant));
        Assert.Equal(2, single.Count);
    }

    [Fact]
    public void CollectsWithIncludeExcludePreserve()
    {
        using var dir = new TempDir();
        dir.File("app/App.exe");
        dir.File("app/App.pdb");
        dir.File("app/config/user-settings.json");
        dir.File("app/data/a.txt");
        var p = new SetupProject
        {
            App = new AppInfo { Id = "T", Name = "T", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app", Preserve = ["config/user-settings.json"] },
        };
        var r = FileCollector.Collect(p, dir.Path);
        Assert.False(r.Diagnostics.HasErrors, string.Join("\n", r.Diagnostics.Items));
        Assert.Equal(["App.exe", "config\\user-settings.json", "data\\a.txt"], r.Files.Select(f => f.RelativePath).Order().ToArray());
        Assert.True(r.Files.Single(f => f.RelativePath.StartsWith("config")).Preserve);
    }

    [Fact]
    public void RejectsReservedNamesAndMissingMainExe()
    {
        using var dir = new TempDir();
        dir.File("app/uninstall.exe");
        var p = new SetupProject { App = new AppInfo { MainExecutable = "Missing.exe" }, Files = new FileSettings { Source = "app" } };
        var codes = FileCollector.Collect(p, dir.Path).Diagnostics.Items.Select(d => d.Code).ToList();
        Assert.Contains("FS1108", codes);
        Assert.Contains("FS1110", codes);
    }

    [Fact]
    public void DoesNotFollowJunctionsAndSkipsOutputFolder()
    {
        using var dir = new TempDir();
        dir.File("app/App.exe");
        dir.File("outside/secret.txt");
        dir.File("app/dist/old-Setup.exe");
        var junction = Path.Combine(dir.Path, "app", "link");
        using (var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.Combine(dir.Path, "outside")}\"") { CreateNoWindow = true, UseShellExecute = false }))
        {
            proc!.WaitForExit();
        }
        var p = new SetupProject { App = new AppInfo { MainExecutable = "App.exe" }, Files = new FileSettings { Source = "app" }, Output = new OutputSettings { Directory = "app/dist" } };
        var r = FileCollector.Collect(p, dir.Path);
        Assert.DoesNotContain(r.Files, f => f.RelativePath.Contains("secret"));
        Assert.DoesNotContain(r.Files, f => f.RelativePath.Contains("old-Setup"));
        Assert.Equal(1, r.SkippedLinks);
    }

    [Fact]
    public void OutputFolderThatIsTheSourceOnlySkipsTheInstaller()
    {
        // build\bin holds the app and, after a build, its installer (THRM's layout).
        using var dir = new TempDir();
        dir.File("bin/App.exe");
        dir.File("bin/sub/lib.dll");
        dir.File("bin/App-installer.exe");
        var p = new SetupProject
        {
            App = new AppInfo { MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "bin" },
            Output = new OutputSettings { Directory = "bin", FileName = "App-installer.exe" },
        };
        var r = FileCollector.Collect(p, dir.Path);
        Assert.Contains(r.Files, f => f.RelativePath == "App.exe");
        Assert.Contains(r.Files, f => f.RelativePath == "sub\\lib.dll");
        Assert.DoesNotContain(r.Files, f => f.RelativePath.Contains("installer"));
    }

    [Fact]
    public void DetectsTwoSourcesForOneTarget()
    {
        using var dir = new TempDir();
        dir.File("app/App.exe");
        var extra = dir.File("other/App.exe");
        var p = new SetupProject { App = new AppInfo { MainExecutable = "App.exe" }, Files = new FileSettings { Source = "app", Extra = [new ExtraSource { Source = extra }] } };
        Assert.Contains(FileCollector.Collect(p, dir.Path).Diagnostics.Items, d => d.Code == "FS1109");
    }
}

public class SerializerTests
{
    [Fact]
    public void RoundTripKeepsUnknownProperties()
    {
        const string json = """{ "schemaVersion": 1, "app": { "id": "A", "name": "N", "futureField": 42 }, "futureSection": { "x": true } }""";
        var p = ProjectSerializer.Parse(json);
        var again = ProjectSerializer.Serialize(p);
        using var doc = JsonDocument.Parse(again);
        Assert.Equal(42, doc.RootElement.GetProperty("app").GetProperty("futureField").GetInt32());
        Assert.True(doc.RootElement.GetProperty("futureSection").GetProperty("x").GetBoolean());
    }

    [Fact]
    public void RejectsNewerSchema() => Assert.Throws<InvalidDataException>(() => ProjectSerializer.Parse("""{ "schemaVersion": 99 }"""));
}

public class IconAndResourceTests
{
    [Fact]
    public void WrapsPngIntoIco()
    {
        using var dir = new TempDir();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 64, 0, 0, 0, 64, 8, 6, 0, 0, 0 };
        var path = Path.Combine(dir.Path, "i.png");
        File.WriteAllBytes(path, png);
        var ico = IconConverter.LoadAsIco(path);
        var (images, group) = IconConverter.ToResources(ico, 1);
        Assert.Single(images);
        Assert.Equal(64, ico[6]);
        Assert.Equal(6 + 14, group.Length);
    }

    [Fact]
    public void RejectsBrokenIco()
    {
        using var dir = new TempDir();
        var path = dir.File("bad.ico", "not an icon");
        Assert.Throws<InvalidDataException>(() => IconConverter.LoadAsIco(path));
    }

    [Fact]
    public void VersionResourceIsWellFormed()
    {
        var bytes = PeResources.BuildVersionResource(new PeResources.VersionInfo("Prod", "Co", "Desc", "2.3.4-beta", "Setup.exe", "(c)"));
        Assert.Equal(bytes.Length, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(0xFEEF04BD, BitConverter.ToUInt32(bytes, 40));
        Assert.Equal(PeResources.NumericVersion("2.3.4-beta"), ((ushort)2, (ushort)3, (ushort)4, (ushort)0));
    }
}

public class PackagingTests
{
    private static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    [Fact]
    public async Task BuildsAndInspectsPackage()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeRoot, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        dir.File("app/App.exe", "MZ fake");
        for (var i = 0; i < 50; i++) dir.File($"app/data/f{i}.txt", new string('x', i * 100));
        var project = new SetupProject
        {
            App = new AppInfo { Id = "Test.Pkg", Name = "Pkg Test", Publisher = "P", Version = "3.1.0", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
            Output = new OutputSettings { Directory = "out", Compression = "fast" },
        };
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(project, projectPath);
        var report = await new PackageBuilder().BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot });
        Assert.True(report.Success, report.Error);
        Assert.Equal(51, report.FileCount);
        Assert.True(report.Sizes.InstallerOverhead < 5 * 1024 * 1024);

        var info = PackageInspector.Inspect(report.OutputPath!, verifyStreams: true);
        Assert.Empty(info.Problems);
        Assert.Equal(51, info.PayloadFiles);
        var app = info.Header!.RootElement.GetProperty("app");
        Assert.Equal("Test.Pkg", app.GetProperty("id").GetString());
        Assert.Equal("3.1.0", app.GetProperty("version").GetString());

        // The version resource was applied to the stub.
        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(report.OutputPath!);
        Assert.Equal("Pkg Test", fvi.ProductName);
        Assert.Equal("3.1.0", fvi.ProductVersion);

        // Tampering is detected by the inspector.
        var bytes = File.ReadAllBytes(report.OutputPath!);
        bytes[(int)report.Sizes.Stub + 100] ^= 0xFF;
        var tampered = Path.Combine(dir.Path, "tampered.exe");
        File.WriteAllBytes(tampered, bytes);
        Assert.NotEmpty(PackageInspector.Inspect(tampered, verifyStreams: true).Problems);
    }

    [Fact]
    public async Task InvalidProjectFailsWithDiagnostics()
    {
        using var dir = new TempDir();
        var project = new SetupProject { App = new AppInfo { Id = "", Name = "" } };
        var path = Path.Combine(dir.Path, "p.json");
        var report = await new PackageBuilder().BuildAsync(project, path, new BuildOptions { RuntimeRoot = RuntimeRoot });
        Assert.False(report.Success);
        Assert.Contains(report.Diagnostics, d => d.Code == "FS1001");
    }

    [Fact]
    public async Task CancellationStopsTheBuild()
    {
        using var dir = new TempDir();
        dir.File("app/App.exe");
        var project = new SetupProject { App = new AppInfo { Id = "T.C", Name = "C", MainExecutable = "App.exe" }, Files = new FileSettings { Source = "app" } };
        var path = Path.Combine(dir.Path, "p.json");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageBuilder().BuildAsync(project, path, new BuildOptions { RuntimeRoot = RuntimeRoot }, null, cts.Token));
    }
}
