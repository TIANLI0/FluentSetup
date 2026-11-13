using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Prerequisites;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>The 1.2 sections: validation rules and what the package header says.</summary>
public class IntegrationTests
{
    private static SetupProject Valid(TempDir dir, string scope = "user")
    {
        dir.File("app/App.exe");
        dir.File("app/tools/Agent.exe");
        return new SetupProject
        {
            App = new AppInfo { Id = "Test.App", Name = "Test", Publisher = "Me", Version = "1.2.3", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
            Install = new InstallSettings { Scope = scope },
            Options = [new UserOptionSettings { Id = "extras", Label = new() { ["en-US"] = "Extras" } }],
        };
    }

    private static List<string> Errors(SetupProject p, TempDir dir) =>
        [.. ProjectValidator.Validate(p, dir.Path).Items.Where(d => d.Severity == Severity.Error).Select(d => d.Code)];

    [Fact]
    public void AcceptsAProjectUsingEverySection()
    {
        using var dir = new TempDir();
        var p = Valid(dir, "machine");
        p.App.AppUserModelId = "Test.App";
        p.App.ToastActivatorClsid = "{0F2C9E6A-7B39-4B8E-9D3A-6A0B3E3C9C21}";
        p.Install.AppPaths = true;
        p.Install.LockedFiles = "reboot";
        p.Shortcuts = [new ShortcutSettings { Name = "Agent", Target = "tools/Agent.exe", Location = "startup", Arguments = ["--bg", "{Input:server}"] }];
        p.Associations = [new FileAssociationSettings { Extensions = [".tst"], ProgId = "Test.App.Doc", Description = "Test document" }];
        p.Protocols = [new ProtocolSettings { Scheme = "test-app" }];
        p.Registry = [new RegistryEntrySettings { Key = @"Software\Test\App", Values = [new RegistryValueSettings { Name = "N", Type = "dword", Data = "0x10" }] }];
        p.Environment = [new EnvironmentVariableSettings { Name = "TEST_APP_HOME", Value = "{InstallDir}" }];
        p.Services = [new ServiceSettings { Name = "TestAgent", Run = "tools/Agent.exe" }];
        p.Firewall = [new FirewallRuleSettings { Name = "Test", Protocol = "tcp", LocalPorts = "8080,9000-9010", Profiles = ["private"] }];
        p.Inputs = [new InputSettings { Id = "server", Label = new() { ["en-US"] = "Server" }, Default = "https://x", Pattern = "^https://", Required = true }];
        p.ConfigFiles = [new ConfigFileSettings { Path = "settings.json", Values = [new ConfigValueSettings { Key = "server.url", Value = "{Input:server}" }] }];
        p.Requirements.VcRedist = new VcRedistRequirement { Required = true, MinVersion = "14.40", Auto = "download" };
        p.Requirements.WebView2 = new WebView2Settings { Required = true, Auto = "bundle" };
        p.Requirements.Custom = [new CustomRequirement { Id = "drv", Name = "Driver", Detect = new() { File = new() { Path = @"{System}\drivers\x.sys" } }, HelpUrl = "https://example.com" }];
        Assert.Empty(Errors(p, dir));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".lnk")]
    [InlineData("txt")]
    [InlineData(".a/b")]
    public void RefusesDangerousExtensions(string ext) => Assert.False(IntegrationValidator.IsAssociableExtension(ext));

    [Theory]
    [InlineData("http")]
    [InlineData("ms-settings")]
    [InlineData("exefile")]
    [InlineData("Folder")]
    [InlineData("x")]
    public void RefusesReservedSchemes(string scheme) => Assert.False(IntegrationValidator.IsCustomScheme(scheme));

    [Fact]
    public void ReportsMistakesInTheNewSections()
    {
        using var dir = new TempDir();
        var p = Valid(dir);
        p.Shortcuts = [new ShortcutSettings { Name = "A", Location = "taskbar", IfOption = "nope" }];
        p.Associations = [new FileAssociationSettings { Extensions = [".bat"], ProgId = "NoDot" }];
        p.Protocols = [new ProtocolSettings { Scheme = "https" }];
        p.Registry = [new RegistryEntrySettings { Key = @"Software\Microsoft\Windows", Values = [new RegistryValueSettings { Type = "dword", Data = "big" }] }];
        p.Environment = [new EnvironmentVariableSettings { Name = "Path" }];
        p.Services = [new ServiceSettings { Name = "Svc", Run = "App.exe" }];
        p.Inputs = [new InputSettings { Id = "x", Label = new() { ["en-US"] = "X" }, Type = "choice", Pattern = "(" }];
        p.ConfigFiles = [new ConfigFileSettings { Path = @"..\outside.json", Values = [new ConfigValueSettings { Key = "a", Value = "{Input:missing}" }] }];
        var errors = Errors(p, dir);
        foreach (var code in new[] { "FS1907", "FS1909", "FS1922", "FS1923", "FS1933", "FS1941", "FS1945", "FS1951", "FS1966", "FS1985", "FS1986", "FS1991", "FS1998" })
            Assert.Contains(code, errors);
    }

    [Fact]
    public void OfficialInstallersFollowTheVersion()
    {
        Assert.Equal("https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-arm64.exe", OfficialInstallers.For(PrerequisiteKind.DotNet, "arm64", "10.0.2", "desktop")!.Url);
        Assert.Equal("https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe", OfficialInstallers.For(PrerequisiteKind.WindowsAppRuntime, "x64", "2.5.1")!.Url);
        Assert.Equal("https://aka.ms/vs/17/release/vc_redist.x86.exe", OfficialInstallers.For(PrerequisiteKind.VcRedist, "x64", vcArchitecture: "x86")!.Url);
        Assert.Contains("2203305", OfficialInstallers.For(PrerequisiteKind.NetFramework, "x64", "4.8.1")!.Url);
        Assert.Equal(OfficialInstallers.MicrosoftSigner, OfficialInstallers.For(PrerequisiteKind.WebView2, "x64")!.Signer);
    }

    private static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    [Fact]
    public async Task HeaderCarriesTheNewSectionsWithDefaultsResolved()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeRoot, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        var p = Valid(dir);
        p.Install.StartMenuFolder = "Test Co";
        p.Ui.Banner = dir.File("banner.png", "not really a png");
        p.Shortcuts = [new ShortcutSettings { Name = "Safe", Arguments = ["--safe"] }];
        p.Associations = [new FileAssociationSettings { Extensions = [".TST"], ProgId = "Test.App.Doc" }];
        p.Requirements.VcRedist = new VcRedistRequirement { Required = true, Auto = "download" };
        p.Output = new OutputSettings { Directory = "out", Compression = "fast", IncludeWinUI = false };
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(p, projectPath);
        var report = await new PackageBuilder().BuildAsync(p, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, TestHooks = true });
        Assert.True(report.Success, report.Error);
        var info = PackageInspector.Inspect(report.OutputPath!, verifyStreams: true);
        Assert.Empty(info.Problems);
        var root = info.Header!.RootElement;
        var shortcut = root.GetProperty("shortcuts")[0];
        Assert.Equal("App.exe", shortcut.GetProperty("target").GetString());
        Assert.Equal("Test Co", shortcut.GetProperty("folder").GetString());
        Assert.Equal(".tst", root.GetProperty("associations")[0].GetProperty("extensions")[0].GetString());
        Assert.Equal("%1", root.GetProperty("associations")[0].GetProperty("args")[0].GetString());
        var vc = root.GetProperty("requirements").EnumerateArray().Single(r => r.GetProperty("type").GetString() == "vcredist");
        Assert.Equal("x64", vc.GetProperty("architecture").GetString());
        Assert.Equal("https://aka.ms/vs/17/release/vc_redist.x64.exe", vc.GetProperty("download").GetProperty("url").GetString());
        Assert.Equal("Microsoft Corporation", vc.GetProperty("download").GetProperty("signer").GetString());
        Assert.False(vc.GetProperty("download").TryGetProperty("sha256", out _));
        // Without WinUI the banner still travels in a front-end set (no entry).
        Assert.Equal("banner.png", root.GetProperty("ui").GetProperty("banner").GetString());
        Assert.False(root.GetProperty("frontend").TryGetProperty("entry", out _));
        Assert.True(root.GetProperty("testHooks").GetBoolean());
    }
}
