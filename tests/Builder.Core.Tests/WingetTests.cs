using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Winget;
using Xunit;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>
/// fluentsetup winget: the manifests are read back with a YAML parser and checked against the rules of the winget
/// manifest schema 1.28.0 (required fields, patterns, enums) that matter for a FluentSetup Setup.exe.
/// </summary>
public class WingetTests
{
    private const string MitText = "MIT License\n\nCopyright (c) 2026 Contoso\n\nPermission is hereby granted, free of charge, to any person obtaining a copy...";

    private static readonly string[] ReturnResponses =
    [
        "packageInUse", "packageInUseByApplication", "installInProgress", "fileInUse", "missingDependency", "diskFull",
        "insufficientMemory", "invalidParameter", "noNetwork", "contactSupport", "rebootRequiredToFinish",
        "rebootRequiredForInstall", "rebootInitiated", "cancelledByUser", "alreadyInstalled", "downgrade", "blockedByPolicy",
        "systemNotSupported", "custom",
    ];

    private static async Task<string> BuildSetup(TempDir dir, Action<SetupProject>? configure = null)
    {
        dir.File("app/App.exe", "MZ fake");
        dir.File("LICENSE.txt", MitText);
        var project = new SetupProject
        {
            App = new AppInfo
            {
                Id = "Contoso.WingetDemo", Name = "Winget Demo", Publisher = "Contoso Ltd.", Version = "1.2.0", MainExecutable = "App.exe",
                Description = "A demo app: it shows what winget gets.", Website = "https://example.com/demo",
            },
            Files = new FileSettings { Source = "app" },
            Install = new InstallSettings { Scope = "userOrMachine" },
            Ui = new UiSettings { License = "LICENSE.txt" },
            Output = new OutputSettings { Directory = "dist", Compression = "fast" },
        };
        configure?.Invoke(project);
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(project, projectPath);
        var report = await new PackageBuilder().BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime") });
        Assert.True(report.Success, report.Error);
        return report.OutputPath!;
    }

    private static Dictionary<object, object> Yaml(string text) => new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(text);

    private static Dictionary<object, object> Map(object o) => Assert.IsType<Dictionary<object, object>>(o);

    private static List<object> Seq(object o) => Assert.IsType<List<object>>(o);

    private static void Required(Dictionary<object, object> m, params string[] keys)
    {
        foreach (var k in keys) Assert.True(m.ContainsKey(k) && m[k] is string { Length: > 0 }, $"missing {k}");
    }

    [Fact]
    public async Task WritesValidMultiFileManifests()
    {
        using var dir = new TempDir();
        var setup = await BuildSetup(dir);
        var set = WingetManifests.Create(setup, new WingetOptions { GitHubRepository = "contoso/winget-demo", Tags = ["Demo App", "installer", "demo app"] });

        Assert.Equal("Contoso.WingetDemo", set.Identifier);
        Assert.Equal("manifests/c/Contoso/WingetDemo/1.2.0", set.RelativeFolder);
        Assert.Equal(new[] { "Contoso.WingetDemo.installer.yaml", "Contoso.WingetDemo.locale.en-US.yaml", "Contoso.WingetDemo.yaml" }, set.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://github.com/contoso/winget-demo/releases/download/v1.2.0/Winget.Demo-1.2.0-Setup.exe", set.InstallerUrl);
        using (var fs = File.OpenRead(setup)) Assert.Equal(Convert.ToHexString(SHA256.HashData(fs)), set.InstallerSha256);

        foreach (var (name, text) in set.Files)
        {
            Assert.StartsWith("# Created with FluentSetup", text);
            Assert.Contains("# yaml-language-server: $schema=https://aka.ms/winget-manifest.", text);
            Assert.DoesNotContain("\r", text);
            var m = Yaml(text);
            Required(m, "PackageIdentifier", "PackageVersion", "ManifestType", "ManifestVersion");
            Assert.Equal("Contoso.WingetDemo", m["PackageIdentifier"]);
            Assert.Equal("1.2.0", m["PackageVersion"]);
            Assert.Equal(WingetManifests.ManifestVersion, m["ManifestVersion"]);
            Assert.Matches(@"^\d+\.\d+\.\d+$", (string)m["ManifestVersion"]);
            Assert.Contains($"winget-manifest.{m["ManifestType"]}.{WingetManifests.ManifestVersion}.schema.json", text);
            Assert.True(WingetManifests.IsValidIdentifier((string)m["PackageIdentifier"]), name);
        }

        var version = Yaml(set.Files["Contoso.WingetDemo.yaml"]);
        Assert.Equal("version", version["ManifestType"]);
        Assert.Equal("en-US", version["DefaultLocale"]);

        var locale = Yaml(set.Files["Contoso.WingetDemo.locale.en-US.yaml"]);
        Required(locale, "PackageLocale", "Publisher", "PackageName", "License", "ShortDescription");
        Assert.Equal("defaultLocale", locale["ManifestType"]);
        Assert.Equal("Contoso Ltd.", locale["Publisher"]);
        Assert.Equal("Winget Demo", locale["PackageName"]);
        Assert.Equal("MIT", locale["License"]);
        Assert.Equal("A demo app: it shows what winget gets.", locale["ShortDescription"]);
        Assert.False(locale.ContainsKey("Description"));  // same as the short one
        Assert.Equal("https://example.com/demo", locale["PackageUrl"]);
        Assert.Equal("https://github.com/contoso", locale["PublisherUrl"]);
        Assert.Equal("https://github.com/contoso/winget-demo/releases/tag/v1.2.0", locale["ReleaseNotesUrl"]);
        Assert.Equal(new object[] { "demo-app", "installer" }, Seq(locale["Tags"]));
        Assert.Contains(set.Notes, n => n.Contains("MIT", StringComparison.Ordinal));

        var installer = Yaml(set.Files["Contoso.WingetDemo.installer.yaml"]);
        Assert.Equal("installer", installer["ManifestType"]);
        Assert.Equal("exe", installer["InstallerType"]);
        Assert.Equal("install", installer["UpgradeBehavior"]);
        Assert.Equal("10.0.17763.0", installer["MinimumOSVersion"]);
        Assert.Equal("Contoso.WingetDemo", installer["ProductCode"]);
        Assert.Equal(new object[] { "Windows.Desktop" }, Seq(installer["Platform"]));
        var arp = Map(Assert.Single(Seq(installer["AppsAndFeaturesEntries"])));
        Assert.Equal("Winget Demo", arp["DisplayName"]);
        Assert.Equal("Contoso Ltd.", arp["Publisher"]);
        Assert.Equal("1.2.0", arp["DisplayVersion"]);
        Assert.Equal("Contoso.WingetDemo", arp["ProductCode"]);

        var codes = Seq(installer["ExpectedReturnCodes"]).Select(Map).ToDictionary(c => int.Parse((string)c["InstallerReturnCode"]), c => (string)c["ReturnResponse"]);
        Assert.All(codes.Values, r => Assert.Contains(r, ReturnResponses));
        Assert.DoesNotContain(0, codes.Keys);
        Assert.Equal("cancelledByUser", codes[2]);
        Assert.Equal("diskFull", codes[7]);
        Assert.Equal("downgrade", codes[10]);
        Assert.Equal("installInProgress", codes[16]);
        Assert.Equal("missingDependency", codes[17]);
        Assert.Equal("rebootRequiredToFinish", codes[3010]);

        var installers = Seq(installer["Installers"]).Select(Map).ToList();
        Assert.Equal(2, installers.Count);
        foreach (var i in installers)
        {
            Assert.Equal("x64", i["Architecture"]);
            Assert.Equal(set.InstallerUrl, i["InstallerUrl"]);
            Assert.Matches("^[A-F0-9]{64}$", (string)i["InstallerSha256"]);
            var switches = Map(i["InstallerSwitches"]);
            Assert.Equal("/quiet", switches["Silent"]);
            Assert.Equal("/passive", switches["SilentWithProgress"]);
            Assert.Equal("/dir \"<INSTALLPATH>\"", switches["InstallLocation"]);
            Assert.Equal("/log \"<LOGPATH>\"", switches["Log"]);
            Assert.Equal("/scope " + i["Scope"], switches["Custom"]);
        }
        var user = installers.Single(i => (string)i["Scope"] == "user");
        var machine = installers.Single(i => (string)i["Scope"] == "machine");
        Assert.False(user.ContainsKey("ElevationRequirement"));
        Assert.Equal("elevatesSelf", machine["ElevationRequirement"]);
        Assert.Equal(@"%LocalAppData%\Programs\Winget Demo", Map(user["InstallationMetadata"])["DefaultInstallLocation"]);
        Assert.Equal(@"%ProgramFiles%\Winget Demo", Map(machine["InstallationMetadata"])["DefaultInstallLocation"]);

        // Written into the winget-pkgs layout.
        var written = set.Write(Path.Combine(dir.Path, "pkgs"));
        Assert.Equal(3, written.Count);
        Assert.All(written, f => Assert.Equal(Path.Combine(dir.Path, "pkgs", "manifests", "c", "Contoso", "WingetDemo", "1.2.0"), Path.GetDirectoryName(f)));
        Assert.Equal(set.Files["Contoso.WingetDemo.installer.yaml"], File.ReadAllText(written.Single(f => f.EndsWith(".installer.yaml", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task MachineScopeAndOverrides()
    {
        using var dir = new TempDir();
        var setup = await BuildSetup(dir, p =>
        {
            p.App.Id = "WingetDemo";          // no Publisher.App form
            p.App.Version = "2.0";            // a YAML float unless quoted
            p.App.Description = "First line of the story. More details follow.\nSecond line: \"quoted\" # not a comment";
            p.Install.Scope = "machine";
            p.Install.AllowCustomDir = false;
            p.Install.MinWindowsBuild = 0;
            p.Ui.License = "";
        });
        var set = WingetManifests.Create(setup, new WingetOptions
        {
            InstallerUrl = "https://downloads.example.com/demo/Setup%202.0.exe",
            License = "Proprietary",
            LicenseUrl = "https://example.com/eula",
            PublisherUrl = "https://example.com",
        });
        Assert.Equal("Contoso.WingetDemo", WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "https://x.example/s.exe", Identifier = "Contoso.WingetDemo" }).Identifier);
        Assert.Equal("ContosoLtd.WingetDemo", set.Identifier);
        Assert.Contains(set.Notes, n => n.Contains("PackageIdentifier", StringComparison.Ordinal));
        Assert.Equal("manifests/c/ContosoLtd/WingetDemo/2.0", set.RelativeFolder);
        Assert.Contains("PackageVersion: '2.0'", set.Files["ContosoLtd.WingetDemo.yaml"]);

        var locale = Yaml(set.Files["ContosoLtd.WingetDemo.locale.en-US.yaml"]);
        Assert.Equal("Proprietary", locale["License"]);
        Assert.Equal("https://example.com/eula", locale["LicenseUrl"]);
        Assert.Equal("First line of the story.", locale["ShortDescription"]);
        Assert.Equal("First line of the story. More details follow.\nSecond line: \"quoted\" # not a comment", locale["Description"]);
        Assert.False(locale.ContainsKey("ReleaseNotesUrl"));

        var installer = Yaml(set.Files["ContosoLtd.WingetDemo.installer.yaml"]);
        Assert.False(installer.ContainsKey("MinimumOSVersion"));
        Assert.Equal("WingetDemo", installer["ProductCode"]);
        var only = Map(Assert.Single(Seq(installer["Installers"])));
        Assert.Equal("machine", only["Scope"]);
        Assert.Equal("elevatesSelf", only["ElevationRequirement"]);
        Assert.Equal("https://downloads.example.com/demo/Setup%202.0.exe", only["InstallerUrl"]);
        var switches = Map(only["InstallerSwitches"]);
        Assert.False(switches.ContainsKey("InstallLocation"));   // allowCustomDir: false
        Assert.False(switches.ContainsKey("Custom"));            // one scope only: no /scope
    }

    [Fact]
    public async Task RejectsWhatWingetWouldReject()
    {
        using var dir = new TempDir();
        var setup = await BuildSetup(dir);
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "http://example.com/Setup.exe" }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "Setup.exe" }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { GitHubRepository = "not a repo" }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "https://x.example/s.exe", Identifier = "NoDots" }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "https://x.example/s.exe", Identifier = "Has Space.App" }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "https://x.example/s.exe", License = "X" }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "https://x.example/s.exe", PublisherUrl = "ftp://x" }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(setup, new WingetOptions { InstallerUrl = "https://x.example/s.exe", Tags = [.. Enumerable.Range(0, 17).Select(i => "t" + i)] }));
        Assert.Throws<WingetManifestException>(() => WingetManifests.Create(dir.File("not-setup.exe", "MZ nothing here"), new WingetOptions { InstallerUrl = "https://x.example/s.exe" }));

        using var dir2 = new TempDir();
        var noPublisher = await BuildSetup(dir2, p => p.App.Publisher = "");
        var ex = Assert.Throws<WingetManifestException>(() => WingetManifests.Create(noPublisher, new WingetOptions { InstallerUrl = "https://x.example/s.exe" }));
        Assert.Contains("app.publisher", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("/dir \"<INSTALLPATH>\"")]
    [InlineData("https://example.com/a?b=c#frag")]
    [InlineData(@"%LocalAppData%\Programs\X")]
    [InlineData("1.0")]
    [InlineData("10")]
    [InlineData("yes")]
    [InlineData("Off")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("2026-11-14")]
    [InlineData("key: value")]
    [InlineData("ends with colon:")]
    [InlineData("a # b")]
    [InlineData("-dash")]
    [InlineData("[flow]")]
    [InlineData("{flow}")]
    [InlineData("a, b")]
    [InlineData("it's")]
    [InlineData("'single'")]
    [InlineData("\"double\"")]
    [InlineData(" padded ")]
    [InlineData("line1\nline2")]
    [InlineData("tab\there")]
    [InlineData("back\\slash")]
    [InlineData("中文 名称 — ünïcödé")]
    [InlineData("&anchor")]
    [InlineData("*alias")]
    [InlineData("!tag")]
    [InlineData("@at")]
    [InlineData("`tick")]
    [InlineData("| pipe")]
    [InlineData("> gt")]
    [InlineData("? q")]
    public void YamlScalarsReadBackUnchanged(string value)
    {
        var scalar = WingetManifests.YamlScalar(value);
        var stream = new YamlStream();
        stream.Load(new StringReader("k: " + scalar + "\n"));
        var node = Assert.IsType<YamlScalarNode>(((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("k")]);
        Assert.Equal(value, node.Value);
        // A plain scalar must not be something a YAML 1.1/1.2 reader turns into a number, bool, null or date.
        if (node.Style == YamlDotNet.Core.ScalarStyle.Plain)
            Assert.DoesNotMatch(new Regex(@"^([-+]?[0-9._]+([eE][-+]?[0-9]+)?|0x[0-9a-f]+|true|false|yes|no|on|off|y|n|null|~|\d{4}-\d\d?-\d\d?.*)$", RegexOptions.IgnoreCase), value);
    }

    [Theory]
    [InlineData(MitText, "MIT")]
    [InlineData("                                 Apache License\n                           Version 2.0, January 2004", "Apache-2.0")]
    [InlineData("GNU GENERAL PUBLIC LICENSE\nVersion 3, 29 June 2007", "GPL-3.0")]
    [InlineData("GNU GENERAL PUBLIC LICENSE\nVersion 2, June 1991", "GPL-2.0")]
    [InlineData("GNU LESSER GENERAL PUBLIC LICENSE\nVersion 2.1, February 1999", "LGPL-2.1")]
    [InlineData("Mozilla Public License Version 2.0", "MPL-2.0")]
    [InlineData("Redistribution and use in source and binary forms, with or without modification... Neither the name of", "BSD-3-Clause")]
    [InlineData("END USER LICENSE AGREEMENT. You may not copy this software.", null)]
    public void RecognizesWellKnownLicenses(string text, string? spdx) => Assert.Equal(spdx, WingetManifests.GuessLicense(text));

    [Theory]
    [InlineData(@"{LocalAppData}\Programs\{AppName}", @"%LocalAppData%\Programs\My App")]
    [InlineData(@"{UserProgramFiles}\{Publisher}\{AppName}", @"%LocalAppData%\Programs\Contoso\My App")]
    [InlineData(@"{ProgramFiles}\{AppId}", @"%ProgramFiles%\Contoso.MyApp")]
    [InlineData(@"D:\Apps\X", @"D:\Apps\X")]
    public void InstallLocationsUseWingetVariables(string template, string expected) =>
        Assert.Equal(expected, WingetManifests.ExpandInstallDir(template, "My App", "Contoso", "Contoso.MyApp"));

    [Theory]
    [InlineData("A/B: C?", "A_B_ C_")]
    [InlineData("  Trailing dots... ", "Trailing dots")]
    [InlineData("CON", "AppCON")]
    [InlineData("", "App")]
    public void SanitizesNamesLikeTheEngine(string name, string expected) => Assert.Equal(expected, WingetManifests.SanitizeName(name));
}
