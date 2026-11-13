using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

public class SigningValidationTests
{
    private static HashSet<string> Codes(SigningSettings s, string dir, Severity severity = Severity.Error)
    {
        var bag = new DiagnosticBag();
        SigningValidator.Validate(s, dir, bag);
        return bag.Items.Where(d => d.Severity == severity).Select(d => d.Code).ToHashSet();
    }

    /// <summary>Errors other than FS1519 (signtool needs Windows; a warning off Windows, never an error here).</summary>
    private static HashSet<string> ErrorsBesidesPlatform(SigningSettings s, string dir)
    {
        var codes = Codes(s, dir);
        codes.Remove("FS1519");
        return codes;
    }

    [Fact]
    public void DefaultProviderIsSigntool()
    {
        using var dir = new TempDir();
        var s = new SigningSettings { Enabled = true };
        Assert.Equal("signtool", s.Provider);
        Assert.Contains("FS1501", Codes(s, dir.Path));
        s.Provider = "";
        Assert.Contains("FS1501", Codes(s, dir.Path));
        s.CertificateThumbprint = "‎ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01";
        Assert.Empty(ErrorsBesidesPlatform(s, dir.Path));
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("zz cd ef 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01")]
    public void RejectsBadThumbprints(string thumbprint)
    {
        using var dir = new TempDir();
        Assert.Contains("FS1518", Codes(new SigningSettings { Enabled = true, CertificateThumbprint = thumbprint }, dir.Path));
    }

    [Fact]
    public void ChecksProviderAndTimestamp()
    {
        using var dir = new TempDir();
        Assert.Contains("FS1504", Codes(new SigningSettings { Provider = "azure" }, dir.Path));
        Assert.Contains("FS1503", Codes(new SigningSettings { CertificateThumbprint = new string('a', 40), TimestampUrl = "ftp://ts" }, dir.Path));
        Assert.Contains("FS1502", Codes(new SigningSettings { CertificateFile = "missing.pfx" }, dir.Path));
    }

    [Fact]
    public void ValidatesTrustedSigning()
    {
        using var dir = new TempDir();
        var s = new SigningSettings { Provider = "trustedSigning", TrustedSigning = new() { Endpoint = "http://eus.codesigning.azure.net", ExcludeCredentials = ["NoSuchCredential"] } };
        var codes = Codes(s, dir.Path);
        foreach (var c in new[] { "FS1505", "FS1506", "FS1507", "FS1508" }) Assert.Contains(c, codes);
        Assert.Contains("FS1509", Codes(s, dir.Path, Severity.Warning));
        Assert.Contains("FS1508", Codes(new SigningSettings { Provider = "trustedSigning" }, dir.Path));  // section missing

        var dlib = dir.File("dlib/x64/Azure.CodeSigning.Dlib.dll");
        s.TrustedSigning = new() { Endpoint = "https://eus.codesigning.azure.net", AccountName = "contoso", CertificateProfile = "public", DlibPath = "dlib/x64/Azure.CodeSigning.Dlib.dll", ExcludeCredentials = ["ManagedIdentityCredential"] };
        Assert.Empty(ErrorsBesidesPlatform(s, dir.Path));
        s.CertificateFile = "x.pfx";
        Assert.Contains("FS1517", Codes(s, dir.Path, Severity.Info));
        Assert.True(File.Exists(dlib));
    }

    [Fact]
    public void TrustedSigningMetadataHasTheDlibNames()
    {
        var json = Signer.TrustedSigningMetadata(new TrustedSigningSettings
        {
            Endpoint = "https://weu.codesigning.azure.net", AccountName = "acct", CertificateProfile = "prof", CorrelationId = "build-7",
            ExcludeCredentials = ["AzureCliCredential"],
        });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("https://weu.codesigning.azure.net", root.GetProperty("Endpoint").GetString());
        Assert.Equal("acct", root.GetProperty("CodeSigningAccountName").GetString());
        Assert.Equal("prof", root.GetProperty("CertificateProfileName").GetString());
        Assert.Equal("build-7", root.GetProperty("CorrelationId").GetString());
        Assert.Equal("AzureCliCredential", root.GetProperty("ExcludeCredentials")[0].GetString());
        Assert.False(JsonDocument.Parse(Signer.TrustedSigningMetadata(new TrustedSigningSettings())).RootElement.TryGetProperty("CorrelationId", out _));
    }

    [Fact]
    public void ValidatesCommand()
    {
        using var dir = new TempDir();
        var codes = Codes(new SigningSettings { Provider = "command" }, dir.Path);
        Assert.Contains("FS1510", codes);
        Assert.Contains("FS1511", codes);
        var s = new SigningSettings
        {
            Provider = "command",
            Command = new() { Run = "tools/missing-signer", Arguments = ["sign", "{input}", "{File}"], Environment = { ["A=B"] = "x", [""] = "y" }, TimeoutSeconds = 5 },
        };
        codes = Codes(s, dir.Path);
        foreach (var c in new[] { "FS1511", "FS1512", "FS1513", "FS1515", "FS1516" }) Assert.Contains(c, codes);
        Assert.Contains("FS1514", Codes(new SigningSettings { Provider = "command", Command = new() { Run = "no-such-signer-" + Guid.NewGuid().ToString("N"), Arguments = ["{file}"] } }, dir.Path, Severity.Warning));

        dir.File("tools/signer.exe");
        s.Command = new() { Run = "tools\\signer.exe", Arguments = ["sign", "-n", "{description}", "-t", "{timestampUrl}", "-in", "{file}", "-out", "{output}"], Environment = { ["SIGN_MODE"] = "ci" } };
        Assert.Empty(Codes(s, dir.Path));
        Assert.Empty(Codes(s, dir.Path, Severity.Warning));
    }

    [Fact]
    public void BuildChoiceDecidesWhetherSigningIsChecked()
    {
        using var dir = new TempDir();
        var project = new SetupProject { Signing = { Enabled = true, CertificateFile = "missing.pfx" } };
        var projectBag = new DiagnosticBag();
        projectBag.Error("FS1001", "app.id", "x");
        SigningValidator.Validate(project.Signing, dir.Path, projectBag);
        Assert.Contains(projectBag.Items, d => d.Code == "FS1502");

        var noSign = SigningValidator.ForBuild(projectBag, project.Signing, dir.Path, sign: false);   // build --no-sign
        Assert.DoesNotContain(noSign.Items, d => d.Code == "FS1502");
        Assert.Contains(noSign.Items, d => d.Code == "FS1001");

        var disabled = new SigningSettings { Provider = "command" };
        var forced = SigningValidator.ForBuild(new DiagnosticBag(), disabled, dir.Path, sign: true);  // build --sign
        Assert.Contains(forced.Items, d => d.Code == "FS1510");
    }
}

/// <summary>Signing for real: the command provider with a stand-in tool, and signtool with a password-protected .pfx.</summary>
public class SignerTests
{
    // A stand-in signing tool: appends an 8-byte certificate table and points the security directory at it.
    private const string FakeSigner = """
        param([string] $In, [string] $Out, [string] $Description)
        if ($env:FS_FAKE_SIGNER_EXIT) { exit [int]$env:FS_FAKE_SIGNER_EXIT }
        $b = [IO.File]::ReadAllBytes($In)
        $opt = [BitConverter]::ToInt32($b, 0x3C) + 24
        $sec = $(if ([BitConverter]::ToUInt16($b, $opt) -eq 0x20B) { $opt + 112 } else { $opt + 96 }) + 32
        [BitConverter]::GetBytes([uint32]$b.Length).CopyTo($b, $sec)
        [BitConverter]::GetBytes([uint32]8).CopyTo($b, $sec + 4)
        $target = $(if ($Out) { $Out } else { $In })
        [IO.File]::WriteAllBytes($target, [byte[]]($b + [byte[]](8, 0, 0, 0, 0, 2, 2, 0)))
        [IO.File]::WriteAllText("$In.description.txt", "$Description|$env:FS_FAKE_SIGNER_MODE")
        """;

    private static string? PowerShell() => Signer.FindOnPath(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");

    private static string Executable(TempDir dir)
    {
        var exe = Path.Combine(dir.Path, "Setup.exe");
        File.Copy(typeof(PeResources).Assembly.Location, exe);
        return exe;
    }

    private static SigningSettings Command(TempDir dir, string shell, bool output, Dictionary<string, string>? environment = null)
    {
        var script = dir.File("fake-signer.ps1", FakeSigner);
        var args = new List<string> { "-NoProfile", "-NonInteractive" };
        if (OperatingSystem.IsWindows()) args.AddRange(["-ExecutionPolicy", "Bypass"]);
        args.AddRange(["-File", script, "-In", "{file}", "-Description", "{description}"]);
        if (output) args.AddRange(["-Out", "{output}"]);
        return new SigningSettings
        {
            Enabled = true, Provider = "command",
            Command = new SigningCommand { Run = shell, Arguments = args, Environment = environment ?? new() { ["FS_FAKE_SIGNER_MODE"] = output ? "copy" : "in-place" } },
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandProviderSignsWithoutAShell(bool output)
    {
        var shell = PowerShell();
        if (shell is null) return;  // no PowerShell on this machine
        using var dir = new TempDir();
        var exe = Executable(dir);
        var log = new List<string>();
        Signer.Sign(Command(dir, shell, output), dir.Path, exe, "Contoso & Co Setup", log.Add);
        using (var fs = File.OpenRead(exe)) Assert.NotEqual((0L, 0L), PackageFormat.ReadSecurityDirectory(fs));
        Assert.Equal($"Contoso & Co Setup|{(output ? "copy" : "in-place")}", File.ReadAllText(exe + ".description.txt"));  // '&' reached the tool as text
        Assert.Single(Directory.GetFiles(dir.Path, "*.exe"));  // no {output} copy left behind
        Assert.Contains(log, l => l.Contains("fake-signer.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandProviderReportsFailures()
    {
        var shell = PowerShell();
        if (shell is null) return;
        using var dir = new TempDir();
        var exe = Executable(dir);
        var failing = Command(dir, shell, false, new() { ["FS_FAKE_SIGNER_EXIT"] = "3" });
        Assert.Contains("exit code 3", Assert.Throws<BuildFailedException>(() => Signer.Sign(failing, dir.Path, exe, "x", _ => { })).Message);

        // A tool that succeeds without signing is caught too.
        var noop = new SigningSettings { Provider = "command", Command = new() { Run = shell, Arguments = ["-NoProfile", "-Command", "$null = '{file}'; exit 0"] } };
        Assert.Contains("no Authenticode signature", Assert.Throws<BuildFailedException>(() => Signer.Sign(noop, dir.Path, exe, "x", _ => { })).Message);
    }

    [Fact]
    public void SigntoolNeverSeesThePfxPassword()
    {
        if (!OperatingSystem.IsWindows() || Signer.FindSignTool(null) is null) return;
        using var dir = new TempDir();
        var exe = Executable(dir);
        var password = "pw-" + Guid.NewGuid().ToString("N");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FluentSetup Test Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], critical: false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(2));
        File.WriteAllBytes(Path.Combine(dir.Path, "signer.pfx"), cert.Export(X509ContentType.Pfx, password));

        var variable = "FS_TEST_PFX_PASSWORD_" + Guid.NewGuid().ToString("N")[..8];
        var keysBefore = PersistedKeyFiles();

        // A store left by a killed build (its process id is not running): removed by the next signing run, key included.
        var stale = $"FluentSetup-Signing-{int.MaxValue - 3}-test{Guid.NewGuid().ToString("N")[..6]}";
        using (var staleStore = new X509Store(stale, StoreLocation.CurrentUser))
        using (var staleCert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet))
        {
            staleStore.Open(OpenFlags.ReadWrite);
            staleStore.Add(staleCert);
        }
        Assert.NotEqual(keysBefore, PersistedKeyFiles());

        Environment.SetEnvironmentVariable(variable, password);
        var log = new List<string>();
        try
        {
            Signer.Sign(new SigningSettings { Enabled = true, CertificateFile = "signer.pfx", PasswordEnvironmentVariable = variable, TimestampUrl = "" },
                        dir.Path, exe, "Test Setup", log.Add);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
        Assert.DoesNotContain(log, l => l.Contains(password, StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("/sha1 " + cert.Thumbprint, StringComparison.OrdinalIgnoreCase) && !l.Contains("/p ", StringComparison.Ordinal));

        // Signed with this certificate: the PKCS #7 blob in the certificate table carries it.
        var signed = File.ReadAllBytes(exe);
        long offset, size;
        using (var fs = File.OpenRead(exe)) (offset, size) = PackageFormat.ReadSecurityDirectory(fs);
        Assert.True(signed.AsSpan((int)offset, (int)size).IndexOf(cert.RawData) >= 0);

        // The temporary store and the persisted private key are gone.
        using (var stores = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\SystemCertificates"))
            Assert.DoesNotContain(stores!.GetSubKeyNames(), n => n.StartsWith("FluentSetup-Signing-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(keysBefore, PersistedKeyFiles());
    }

    /// <summary>Private key files of the current user (CNG and CAPI).</summary>
    private static HashSet<string> PersistedKeyFiles()
    {
        var crypto = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in new[] { "Keys", "RSA" })
        {
            var folder = Path.Combine(crypto, sub);
            if (Directory.Exists(folder)) files.UnionWith(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
        }
        return files;
    }
}
