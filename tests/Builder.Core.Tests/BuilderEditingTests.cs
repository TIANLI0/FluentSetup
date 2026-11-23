using System.Globalization;
using System.Xml.Linq;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>What the Builder GUI relies on: Save As rebasing, the text forms of its editors, translatable diagnostics.</summary>
public class BuilderEditingTests
{
    // ---- ProjectPaths.Rebase ---------------------------------------------------------------------------------------

    private static string Root => OperatingSystem.IsWindows() ? @"C:\work" : "/work";
    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void Rebase_RewritesRelativePathsForTheNewFolder()
    {
        var p = new SetupProject();
        p.App.Icon = "assets/app.ico";
        p.Files.Source = @"..\bin\Release";
        p.Files.Extra.Add(new ExtraSource { Source = "extras", Target = "x" });
        p.Files.Variants.Add(new VariantSettings { Id = "a", Source = @"build\a" });
        p.Ui.License = "license.txt";
        p.Ui.Licenses["zh-CN"] = @"docs\license.zh.txt";
        p.Ui.Localization = "locales";
        p.Ui.Banner = "banner.png";
        p.Output.Directory = "dist";
        p.Signing.CertificateFile = "cert.pfx";
        p.Signing.SignToolPath = @"tools\signtool.exe";
        p.Signing.TrustedSigning = new TrustedSigningSettings { DlibPath = @"tools\Azure.CodeSigning.Dlib.dll" };
        p.Signing.Command = new SigningCommand { Run = @"tools\sign.cmd" };

        var changed = ProjectPaths.Rebase(p, P("app", "installer"), P("app", "installer", "new"));

        var sep = Path.DirectorySeparatorChar;
        Assert.Equal("../assets/app.ico", p.App.Icon);  // forward slashes stay forward slashes
        Assert.Equal(Path.GetFullPath(P("app", "bin", "Release")), Path.GetFullPath(Path.Combine(P("app", "installer", "new"), p.Files.Source)));
        Assert.Equal($"..{sep}extras", p.Files.Extra[0].Source);
        Assert.Equal($"..{sep}build{sep}a", p.Files.Variants[0].Source);
        Assert.Equal($"..{sep}license.txt", p.Ui.License);
        Assert.Equal($"..{sep}docs{sep}license.zh.txt", p.Ui.Licenses["zh-CN"]);
        Assert.Equal($"..{sep}locales", p.Ui.Localization);
        Assert.Equal($"..{sep}banner.png", p.Ui.Banner);
        Assert.Equal($"..{sep}dist", p.Output.Directory);
        Assert.Equal($"..{sep}cert.pfx", p.Signing.CertificateFile);
        Assert.Equal($"..{sep}tools{sep}signtool.exe", p.Signing.SignToolPath);
        Assert.Equal($"..{sep}tools{sep}Azure.CodeSigning.Dlib.dll", p.Signing.TrustedSigning.DlibPath);
        Assert.Equal($"..{sep}tools{sep}sign.cmd", p.Signing.Command.Run);
        Assert.Equal(13, changed.Count);
    }

    [Fact]
    public void Rebase_LeavesAbsolutePlaceholdersTemplatesAndProgramNamesAlone()
    {
        var absolute = P("assets", "app.ico");
        var p = new SetupProject();
        p.App.Icon = absolute;
        p.Files.Source = "$(BinDir)";
        p.Output.Directory = @"out\{Version}";
        p.Ui.License = "";
        p.Signing.Command = new SigningCommand { Run = "osslsigncode" };  // found on PATH, not a path

        var changed = ProjectPaths.Rebase(p, P("a"), P("b"));

        Assert.Equal(absolute, p.App.Icon);
        Assert.Equal("$(BinDir)", p.Files.Source);
        Assert.Equal(@"out\{Version}", p.Output.Directory);
        Assert.Equal("", p.Ui.License);
        Assert.Equal("osslsigncode", p.Signing.Command.Run);
        Assert.Empty(changed);
    }

    [Fact]
    public void Rebase_SameFolderChangesNothing()
    {
        var p = new SetupProject();
        p.Files.Source = "bin";
        Assert.Empty(ProjectPaths.Rebase(p, P("a"), P("a") + Path.DirectorySeparatorChar));
        Assert.Equal("bin", p.Files.Source);
    }

    [Fact]
    public void Rebase_ToAnotherDriveMakesThePathAbsolute()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(@"C:\work\bin", ProjectPaths.RebasePath("bin", @"C:\work", @"D:\elsewhere"));
    }

    // ---- TextTables ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\new")]
    [InlineData("line one\nline two")]
    [InlineData(@"ends with \")]
    [InlineData("backslash then newline \\\nnext")]
    [InlineData(@"\\n literally")]
    [InlineData(@"\\\n")]
    [InlineData("plain")]
    [InlineData("")]
    public void Escape_RoundTrips(string value)
    {
        var escaped = TextTables.EscapeValue(value);
        Assert.DoesNotContain('\n', escaped);
        Assert.Equal(value, TextTables.UnescapeValue(escaped));
    }

    [Fact]
    public void Escape_KeepsPathsReadable()
    {
        Assert.Equal(@"C:\Program Files\App", TextTables.EscapeValue(@"C:\Program Files\App"));
        Assert.Equal(@"C:\\new", TextTables.EscapeValue(@"C:\new"));
        Assert.Equal(@"a\nb", TextTables.EscapeValue("a\nb"));
    }

    [Fact]
    public void Localized_RoundTripsValuesWithBackslashesAndLineBreaks()
    {
        var texts = new Dictionary<string, string> { ["en-US"] = "C:\\new\nsecond line", ["zh-CN"] = @"licenses\zh\new.txt", ["de-DE"] = "a = b" };
        var errors = new List<TextLineError>();
        var back = TextTables.ParseLocalized(TextTables.FormatLocalized(texts), errors);
        Assert.Empty(errors);
        Assert.Equal(texts, back);
    }

    [Fact]
    public void Localized_ReportsLinesItCannotRead()
    {
        var errors = new List<TextLineError>();
        var result = TextTables.ParseLocalized("en-US = Hello\r\nUse a = b\r\n= nothing\r\nzh-CN = 你好\r\nen-US = Again", errors);

        Assert.Equal(new Dictionary<string, string> { ["en-US"] = "Again", ["zh-CN"] = "你好" }, result);
        Assert.Collection(errors,
            e => Assert.Equal((2, TextLineProblem.NotALanguage, "Use a"), (e.Line, e.Problem, e.Value)),
            e => Assert.Equal((3, TextLineProblem.NotUnderstood), (e.Line, e.Problem)),
            e => Assert.Equal((5, TextLineProblem.Duplicate, "en-US"), (e.Line, e.Problem, e.Value)));
    }

    [Fact]
    public void Localized_ALineWithoutEqualsIsEnglish()
    {
        var errors = new List<TextLineError>();
        Assert.Equal(new Dictionary<string, string> { ["en-US"] = "Just text" }, TextTables.ParseLocalized("Just text", errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void Strings_RoundTripAndReportBadLines()
    {
        var strings = new Dictionary<string, Dictionary<string, string>>
        {
            ["zh-CN"] = new() { ["welcome.body"] = "C:\\new\n第二行", ["finish.title"] = "完成" },
            ["en-US"] = new() { ["welcome.title"] = "a: b = c" },
        };
        var errors = new List<TextLineError>();
        Assert.Equal(strings, TextTables.ParseStrings(TextTables.FormatStrings(strings), errors));
        Assert.Empty(errors);

        var result = TextTables.ParseStrings("zh-CN: welcome.title = 欢迎\nno colon here\nnot a lang: key = x\nzh-CN welcome.body = missing colon", errors);
        Assert.Equal("欢迎", result["zh-CN"]["welcome.title"]);
        Assert.Single(result);
        Assert.Equal([TextLineProblem.NotUnderstood, TextLineProblem.NotALanguage, TextLineProblem.NotUnderstood], errors.Select(e => e.Problem));
        Assert.Equal([2, 3, 4], errors.Select(e => e.Line));
    }

    [Fact]
    public void Ints_ReportPartsThatAreNotNumbers()
    {
        var errors = new List<TextLineError>();
        Assert.Equal([0, 3010, -1], TextTables.ParseInts("0, 3010; -1 x", errors));
        Assert.Equal("x", Assert.Single(errors).Value);
        Assert.Equal("0, 3010", TextTables.FormatInts([0, 3010]));
    }

    // ---- translatable diagnostics ----------------------------------------------------------------------------------

    [Fact]
    public void DiagnosticMessage_KeepsTheTextAndATemplate()
    {
        var bag = new DiagnosticBag();
        var path = @"C:\a{b}";
        var count = 1234;
        bag.Error("FS9999", "f", $"File {path} has {count:N0} lines; use {{Version}}.");
        bag.Warning("FS9998", "g", "Plain {AppName} text.");
        var d = bag.Items[0];
        Assert.Equal($"File {path} has {count.ToString("N0", CultureInfo.CurrentCulture)} lines; use {{Version}}.", d.Message);
        Assert.Equal("File {0} has {1} lines; use {{Version}}.", d.Template);
        Assert.Equal([path, count.ToString("N0", CultureInfo.CurrentCulture)], d.Arguments);
        Assert.Equal(d.Message, string.Format(CultureInfo.CurrentCulture, d.Template!, [.. d.Arguments]));
        Assert.Equal("Plain {AppName} text.", bag.Items[1].Message);
        Assert.Equal("Plain {{AppName}} text.", bag.Items[1].Template);
        // The template does not take part in equality (diagnostics are compared by what they say).
        Assert.Equal(d, new Diagnostic(Severity.Error, "FS9999", "f", d.Message));
        Assert.Equal(DiagnosticMessage.Key("FS9999", d.Template!), DiagnosticMessage.Key("FS9999", d.Template!));
        Assert.Matches("^Diag_FS9999_[0-9a-f]{8}$", DiagnosticMessage.Key("FS9999", d.Template!));
    }

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Builder", "Strings", "en-US", "Resources.resw"))) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> Resw(string language) =>
        XDocument.Load(Path.Combine(RepoRoot, "src", "Builder", "Strings", language, "Resources.resw")).Root!
            .Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    /// <summary>
    /// Every message a broken project gets has a translation in the Builder (keyed by its template), the English entry
    /// is the template itself, and the Chinese one formats with the same values.
    /// </summary>
    [Fact]
    public void ValidationMessages_HaveBuilderTranslations()
    {
        var en = Resw("en-US");
        var zh = Resw("zh-CN");
        var dir = Directory.CreateTempSubdirectory("fs-diag-").FullName;
        try
        {
            var p = new SetupProject();
            p.App.Id = ".bad";
            p.App.Name = new string('n', 200);
            p.App.Version = "one";
            p.App.Website = "ftp://x";
            p.App.Icon = "missing.ico";
            p.App.MainExecutable = "app.txt";
            p.Files.Source = "missing-folder";
            p.Files.Extra.Add(new ExtraSource { Source = "missing-extra", Target = "x" });
            p.Install.Scope = "everyone";
            p.Install.UserDir = "relative";
            p.Install.CloseProcesses = [@"c:\x.exe"];
            p.Ui.AccentColor = "red";
            p.Ui.UseSystemAccent = false;
            p.Ui.DefaultLanguage = "not a tag";
            p.Ui.License = "missing.txt";
            p.Output.FileName = "setup.txt";
            p.Signing.Enabled = true;
            p.Signing.Provider = "magic";
            p.Requirements.NetFramework = "9.9";
            p.Variables["1bad"] = "x";
            p.App.Publisher = "$(Missing)";
            var variables = new DiagnosticBag();
            var expanded = ProjectVariables.Expand(p, null, variables);
            var all = ProjectValidator.Validate(expanded, dir).Items.Concat(variables.Items).ToList();
            Assert.True(all.Count >= 15, string.Join(Environment.NewLine, all));
            foreach (var d in all)
            {
                Assert.NotNull(d.Template);
                var key = DiagnosticMessage.Key(d.Code, d.Template!);
                Assert.True(en.TryGetValue(key, out var english), $"No Builder string {key} for {d}");
                Assert.Equal(d.Template, english);
                Assert.True(zh.TryGetValue(key, out var chinese), $"No Chinese string {key} for {d}");
                var text = string.Format(CultureInfo.InvariantCulture, chinese!, [.. d.Arguments]);
                Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>All diagnostic entries of the two resource files are valid format strings with the same placeholders.</summary>
    [Fact]
    public void DiagnosticTranslations_MatchTheEnglishPlaceholders()
    {
        var en = Resw("en-US");
        var zh = Resw("zh-CN");
        var keys = en.Keys.Where(k => k.StartsWith("Diag_", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count > 250);
        foreach (var key in keys)
        {
            Assert.True(zh.ContainsKey(key), key);
            var args = Enumerable.Range(0, 10).Select(i => (object)$"<{i}>").ToArray();
            var a = string.Format(CultureInfo.InvariantCulture, en[key], args);
            var b = string.Format(CultureInfo.InvariantCulture, zh[key], args);
            var used = Enumerable.Range(0, 10).Where(i => a.Contains($"<{i}>", StringComparison.Ordinal)).ToList();
            Assert.Equal(used, Enumerable.Range(0, 10).Where(i => b.Contains($"<{i}>", StringComparison.Ordinal)).ToList());
        }
    }
}
