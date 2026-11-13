using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Authenticode signing (<c>signing.provider</c>): <c>signtool</c> with a certificate from the store or a .pfx file,
/// <c>trustedSigning</c> (Azure Artifact Signing through signtool and Microsoft's dlib), or <c>command</c>, any signing
/// tool (osslsigncode, jsign, AzureSignTool, ...), which also works on Linux and macOS.
/// <para>
/// No secret is put on a command line or into the log: a password-protected .pfx is imported into a temporary
/// certificate store of the current user, signed from there by thumbprint, and removed again with its private key.
/// </para>
/// </summary>
public static partial class Signer
{
    public const string ProviderSignTool = "signtool", ProviderTrustedSigning = "trustedSigning", ProviderCommand = "command";
    public static readonly string[] Providers = [ProviderSignTool, ProviderTrustedSigning, ProviderCommand];
    /// <summary>Placeholders of signing.command.arguments.</summary>
    public static readonly string[] CommandPlaceholders = ["file", "output", "description", "timestampUrl"];

    public static string EffectiveProvider(SigningSettings settings) => string.IsNullOrEmpty(settings.Provider) ? ProviderSignTool : settings.Provider;

    public static string NormalizeThumbprint(string thumbprint) =>
        string.Concat(thumbprint.Where(c => !char.IsWhiteSpace(c) && c != '‎' && c != '‏'));

    public static string? FindSignTool(string? configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        if (!OperatingSystem.IsWindows()) return null;
        var roots = new List<string>();
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(pf)) roots.Add(Path.Combine(pf, "Windows Kits", "10", "bin"));
        }
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots");
            if (key?.GetValue("KitsRoot10") is string kits) roots.Insert(0, Path.Combine(kits, "bin"));
        }
        catch (System.Security.SecurityException) { }
        var hostArch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        foreach (var root in roots.Where(Directory.Exists))
        {
            var candidate = Directory.EnumerateDirectories(root, "10.*")
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version())
                .Select(d => Path.Combine(d, hostArch, "signtool.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }
        return FindOnPath("signtool.exe");
    }

    /// <summary>
    /// Signs <paramref name="file"/> in place with the configured provider and checks that it then carries an
    /// Authenticode signature. <paramref name="description"/> is the signature's description (signtool /d).
    /// </summary>
    public static void Sign(SigningSettings settings, string projectDir, string file, string description, Action<string> log)
    {
        var provider = EffectiveProvider(settings);
        if (provider == ProviderCommand)
        {
            SignWithCommand(settings, projectDir, file, description, log);
        }
        else if (provider is ProviderSignTool or ProviderTrustedSigning)
        {
            if (!OperatingSystem.IsWindows())
                throw new BuildFailedException($"The {provider} signing provider uses signtool.exe, which runs only on Windows. " +
                                               "On Linux and macOS use the command provider (see docs/cross-platform.md).");
            if (provider == ProviderSignTool) SignWithSignTool(settings, projectDir, file, description, log);
            else SignWithTrustedSigning(settings, projectDir, file, description, log);
        }
        else
        {
            throw new BuildFailedException($"'{settings.Provider}' is not a signing provider (signtool, trustedSigning or command).");
        }
        using var fs = File.OpenRead(file);
        var (offset, size) = PackageFormat.ReadSecurityDirectory(fs);
        if (offset == 0 || size < 8 || offset + size > fs.Length)
            throw new BuildFailedException($"Signing finished, but {Path.GetFileName(file)} has no Authenticode signature.");
    }

    public static (int ExitCode, string Output) Verify(string file, string? signTool = null)
    {
        var tool = FindSignTool(signTool) ?? throw new FileNotFoundException("signtool.exe not found");
        return Run(new ProcessStartInfo(tool), ["verify", "/pa", "/all", file], TimeSpan.FromMinutes(5), "signtool");
    }

    // ---- signtool -------------------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static void SignWithSignTool(SigningSettings s, string projectDir, string file, string description, Action<string> log)
    {
        var tool = RequireSignTool(s, projectDir);
        var args = SignToolArguments(s, description);
        if (!string.IsNullOrEmpty(s.CertificateThumbprint))
        {
            args.AddRange(["/sha1", NormalizeThumbprint(s.CertificateThumbprint)]);
            RunSignTool(tool, args, file, log);
            return;
        }
        var pfx = ProjectValidator.Resolve(projectDir, s.CertificateFile);
        if (string.IsNullOrEmpty(s.PasswordEnvironmentVariable))
        {
            args.AddRange(["/f", pfx]);
            RunSignTool(tool, args, file, log);
            return;
        }
        var password = Environment.GetEnvironmentVariable(s.PasswordEnvironmentVariable)
                       ?? throw new BuildFailedException($"Environment variable {s.PasswordEnvironmentVariable} (certificate password) is not set.");
        // signtool only takes a .pfx password on its command line (/p), where other processes, process-creation auditing
        // and EDR logs can read it. Sign from a temporary store instead.
        using var store = TemporaryCertificateStore.Import(pfx, password);
        args.AddRange(["/s", store.Name, "/sha1", store.Thumbprint]);
        RunSignTool(tool, args, file, log);
    }

    [SupportedOSPlatform("windows")]
    private static void SignWithTrustedSigning(SigningSettings s, string projectDir, string file, string description, Action<string> log)
    {
        var t = s.TrustedSigning ?? throw new BuildFailedException("signing.trustedSigning is missing.");
        var tool = RequireSignTool(s, projectDir);
        var dlib = ProjectValidator.Resolve(projectDir, t.DlibPath);
        if (string.IsNullOrEmpty(dlib) || !File.Exists(dlib)) throw new BuildFailedException($"Azure.CodeSigning.Dlib.dll not found: {dlib} (signing.trustedSigning.dlibPath).");
        var folder = Path.Combine(Path.GetTempPath(), "FluentSetup-sign-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        try
        {
            var metadataPath = Path.Combine(folder, "metadata.json");
            File.WriteAllText(metadataPath, TrustedSigningMetadata(t), PackageFormat.Utf8NoBom);
            var args = SignToolArguments(s, description);
            args.AddRange(["/dlib", dlib, "/dmdf", metadataPath]);
            RunSignTool(tool, args, file, log);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The metadata.json the Artifact Signing dlib reads (/dmdf).</summary>
    internal static string TrustedSigningMetadata(TrustedSigningSettings t)
    {
        var metadata = new Dictionary<string, object>
        {
            ["Endpoint"] = t.Endpoint,
            ["CodeSigningAccountName"] = t.AccountName,
            ["CertificateProfileName"] = t.CertificateProfile,
        };
        if (!string.IsNullOrEmpty(t.CorrelationId)) metadata["CorrelationId"] = t.CorrelationId;
        if (t.ExcludeCredentials.Count > 0) metadata["ExcludeCredentials"] = t.ExcludeCredentials;
        return JsonSerializer.Serialize(metadata);
    }

    private static string RequireSignTool(SigningSettings s, string projectDir) =>
        FindSignTool(string.IsNullOrEmpty(s.SignToolPath) ? null : ProjectValidator.Resolve(projectDir, s.SignToolPath))
        ?? throw new BuildFailedException("signtool.exe was not found. Install the Windows SDK or set signing.signToolPath.");

    private static List<string> SignToolArguments(SigningSettings s, string description)
    {
        var args = new List<string> { "sign", "/fd", "SHA256", "/d", description };
        if (!string.IsNullOrEmpty(s.TimestampUrl)) args.AddRange(["/tr", s.TimestampUrl, "/td", "SHA256"]);
        return args;
    }

    private static void RunSignTool(string tool, List<string> args, string file, Action<string> log)
    {
        args.Add(file);
        log("signtool " + string.Join(' ', args));
        var (code, output) = Run(new ProcessStartInfo(tool), args, TimeSpan.FromMinutes(10), "signtool");
        if (code != 0) throw new BuildFailedException($"Signing failed (signtool exit code {code}): {output.Trim()}");
        log(output.Trim());
    }

    /// <summary>
    /// A .pfx imported into a certificate store of its own (CurrentUser\FluentSetup-Signing-&lt;pid&gt;-&lt;random&gt;) for
    /// one signing operation. Disposing removes the certificates, the store and the persisted private keys. Stores left
    /// by a build that was killed while signing (its process is gone) are removed the next time.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class TemporaryCertificateStore : IDisposable
    {
        internal const string Prefix = "FluentSetup-Signing-";
        private readonly X509Certificate2Collection _certificates;
        private readonly X509Store _store;
        public string Name { get; }
        public string Thumbprint { get; }

        private TemporaryCertificateStore(X509Certificate2Collection certificates, X509Store store, string name, string thumbprint)
        {
            _certificates = certificates;
            _store = store;
            Name = name;
            Thumbprint = thumbprint;
        }

        public static TemporaryCertificateStore Import(string pfxPath, string password)
        {
            RemoveStaleStores();
            X509Certificate2Collection certificates;
            try
            {
                certificates = X509CertificateLoader.LoadPkcs12CollectionFromFile(pfxPath, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            }
            catch (CryptographicException ex)
            {
                throw new BuildFailedException($"Cannot open {Path.GetFileName(pfxPath)}: {ex.Message} (wrong password in the environment variable?)");
            }
            var signing = certificates.FirstOrDefault(c => c.HasPrivateKey);
            if (signing is null)
            {
                DeleteKeys(certificates);
                throw new BuildFailedException($"{Path.GetFileName(pfxPath)} contains no certificate with a private key.");
            }
            var name = $"{Prefix}{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..12]}";
            var store = new X509Store(name, StoreLocation.CurrentUser);
            try
            {
                store.Open(OpenFlags.ReadWrite);
                store.AddRange(certificates);  // intermediates too, for the chain in the signature
            }
            catch (CryptographicException ex)
            {
                store.Dispose();
                DeleteStore(name);
                DeleteKeys(certificates);
                throw new BuildFailedException($"Cannot prepare the certificate for signing: {ex.Message}");
            }
            return new TemporaryCertificateStore(certificates, store, name, signing.Thumbprint);
        }

        public void Dispose()
        {
            try { _store.RemoveRange(_certificates); } catch (CryptographicException) { }
            _store.Dispose();
            DeleteStore(Name);
            DeleteKeys(_certificates);
        }

        private static void DeleteKeys(X509Certificate2Collection certificates)
        {
            foreach (var c in certificates)
            {
                try
                {
                    if (c.HasPrivateKey)
                    {
                        using var rsa = c.GetRSAPrivateKey();
                        if (rsa is RSACng rsaCng) rsaCng.Key.Delete();
                        else if (rsa is RSACryptoServiceProvider rsaCsp)
                        {
                            rsaCsp.PersistKeyInCsp = false;
                            rsaCsp.Clear();
                        }
                        else if (rsa is null)
                        {
                            using var ec = c.GetECDsaPrivateKey();
                            if (ec is ECDsaCng ecCng) ecCng.Key.Delete();
                        }
                    }
                }
                catch (CryptographicException) { }
                c.Dispose();
            }
        }

        /// <summary>Removes FluentSetup-Signing-&lt;pid&gt;-* stores (with their keys) whose process no longer runs.</summary>
        internal static void RemoveStaleStores()
        {
            string[] names;
            try
            {
                using var stores = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\SystemCertificates");
                names = stores?.GetSubKeyNames() ?? [];
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return;
            }
            foreach (var name in names.Where(n => n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                if (int.TryParse(name.AsSpan(Prefix.Length).ToString().Split('-')[0], out var pid) && IsRunning(pid)) continue;
                try
                {
                    using var store = new X509Store(name, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
                    var certificates = store.Certificates;
                    store.RemoveRange(certificates);
                    store.Close();
                    DeleteKeys(certificates);
                }
                catch (CryptographicException) { }
                DeleteStore(name);
            }
        }

        private static bool IsRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);  // throws if no such process
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void DeleteStore(string name)
        {
            const uint CertSystemStoreCurrentUser = 1u << 16, CertStoreDeleteFlag = 0x10;
            // With CERT_STORE_DELETE_FLAG the store is deleted instead of opened; the result is always NULL.
            _ = CertOpenStore(10 /* CERT_STORE_PROV_SYSTEM_W */, 0, IntPtr.Zero, CertSystemStoreCurrentUser | CertStoreDeleteFlag, name);
        }
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CertOpenStore", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CertOpenStore(IntPtr storeProvider, uint encodingType, IntPtr cryptProvider, uint flags, string storeName);

    // ---- command --------------------------------------------------------------------------------------------------

    private static void SignWithCommand(SigningSettings s, string projectDir, string file, string description, Action<string> log)
    {
        var c = s.Command ?? throw new BuildFailedException("signing.command is missing.");
        if (string.IsNullOrWhiteSpace(c.Run)) throw new BuildFailedException("signing.command.run is empty.");
        file = Path.GetFullPath(file);  // the program runs in the project folder
        var exe = IsCommandPath(c.Run) ? ProjectValidator.Resolve(projectDir, c.Run) : FindOnPath(c.Run);
        if (exe is null || !File.Exists(exe)) throw new BuildFailedException($"Signing program not found: {c.Run}");
        // {output}: the tool writes the signed copy there (osslsigncode cannot sign in place); it then replaces the file.
        string? output = c.Arguments.Any(a => a.Contains("{output}", StringComparison.Ordinal))
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.signed{Path.GetExtension(file)}")
            : null;
        var args = c.Arguments.Select(a => a.Replace("{file}", file, StringComparison.Ordinal)
                                            .Replace("{output}", output ?? "", StringComparison.Ordinal)
                                            .Replace("{description}", description, StringComparison.Ordinal)
                                            .Replace("{timestampUrl}", s.TimestampUrl, StringComparison.Ordinal)).ToList();
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = projectDir };
        foreach (var (name, value) in c.Environment) psi.Environment[name] = value;
        var shown = Path.GetFileName(exe);
        log(shown + " " + string.Join(' ', args.Select(Quote)));
        try
        {
            var (code, text) = Run(psi, args, TimeSpan.FromSeconds(Math.Clamp(c.TimeoutSeconds, 10, 3600)), shown);
            if (code != 0) throw new BuildFailedException($"Signing failed ({shown} exit code {code}): {text.Trim()}");
            if (text.Trim().Length > 0) log(text.Trim());
            if (output is not null)
            {
                if (!File.Exists(output)) throw new BuildFailedException($"{shown} finished but did not write the signed file ({{output}}).");
                File.Move(output, file, overwrite: true);
            }
        }
        finally
        {
            if (output is not null && File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>signing.command.run is a path (resolved against the project folder) rather than a name looked up on PATH.</summary>
    public static bool IsCommandPath(string run) => run.Contains('/') || run.Contains('\\') || Path.IsPathRooted(run);

    /// <summary>The full path of a program found on PATH (on Windows also with the PATHEXT extensions), or null.</summary>
    public static string? FindOnPath(string name)
    {
        var extensions = new List<string> { "" };
        if (OperatingSystem.IsWindows() && !Path.HasExtension(name))
            extensions.AddRange((Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static string Quote(string a) => a.Length > 0 && !a.Any(ch => char.IsWhiteSpace(ch) || ch == '"') ? a : "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // ---- processes ------------------------------------------------------------------------------------------------

    /// <summary>Runs a program without a shell; arguments are passed as a list (no quoting issues, no expansion).</summary>
    private static (int ExitCode, string Output) Run(ProcessStartInfo psi, IEnumerable<string> args, TimeSpan timeout, string what)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new BuildFailedException($"Could not start {what}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new BuildFailedException($"Could not start {what}: {ex.Message}");
        }
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                p.WaitForExit();
                throw new BuildFailedException($"{what} did not finish within {timeout.TotalSeconds:N0} seconds.");
            }
            p.WaitForExit();
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
    }
}
