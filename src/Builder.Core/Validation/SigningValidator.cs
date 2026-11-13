using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Validation;

/// <summary>Checks of the <c>signing</c> section (FS1501–FS1519).</summary>
public static partial class SigningValidator
{
    /// <summary>DefaultAzureCredential sources that Artifact Signing's metadata.json can exclude.</summary>
    public static readonly string[] AzureCredentials =
    [
        "EnvironmentCredential", "WorkloadIdentityCredential", "ManagedIdentityCredential", "SharedTokenCacheCredential",
        "VisualStudioCredential", "VisualStudioCodeCredential", "AzureCliCredential", "AzurePowerShellCredential",
        "AzureDeveloperCliCredential", "InteractiveBrowserCredential",
    ];

    [GeneratedRegex(@"^[0-9A-Fa-f]{40}$")]
    private static partial Regex ThumbprintRegex();

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex PlaceholderRegex();

    public static bool IsSigningCode(string code) =>
        code.Length == 6 && code.StartsWith("FS15", StringComparison.Ordinal) && int.TryParse(code.AsSpan(2), out var n) && n is >= 1501 and <= 1519;

    /// <summary>
    /// The diagnostics a build uses: the project's, minus the signing checks when this build does not sign
    /// (<c>build --no-sign</c>), plus them when it signs although the project does not (<c>build --sign</c>). A provider
    /// that needs Windows is an error only when this build signs on another OS (FS1519).
    /// </summary>
    public static DiagnosticBag ForBuild(DiagnosticBag projectDiagnostics, SigningSettings settings, string projectDirectory, bool sign)
    {
        var result = new DiagnosticBag();
        result.AddRange(projectDiagnostics.Items.Where(d => !IsSigningCode(d.Code)));
        if (sign) Validate(settings, projectDirectory, result, signsHere: true);
        return result;
    }

    /// <param name="signsHere">This machine is about to sign: a provider that cannot run here is an error, not a warning.</param>
    public static void Validate(SigningSettings s, string projectDirectory, DiagnosticBag d, bool signsHere = false)
    {
        var provider = Signer.EffectiveProvider(s);
        if (!Signer.Providers.Contains(provider))
        {
            d.Error("FS1504", "signing.provider", $"'{s.Provider}' is not a signing provider. Use signtool, trustedSigning or command.");
            return;
        }
        if (!string.IsNullOrEmpty(s.TimestampUrl) &&
            !(Uri.TryCreate(s.TimestampUrl, UriKind.Absolute, out var ts) && (ts.Scheme == Uri.UriSchemeHttp || ts.Scheme == Uri.UriSchemeHttps)))
            d.Error("FS1503", "signing.timestampUrl", "Timestamp URL is not valid (an http:// or https:// URL, or empty for no timestamp).");
        if (provider != Signer.ProviderCommand && !OperatingSystem.IsWindows())
        {
            var message = $"The {provider} provider uses signtool.exe, which runs only on Windows. On Linux and macOS use the command provider " +
                          "(e.g. osslsigncode or jsign; see docs/cross-platform.md), or build with --no-sign.";
            if (signsHere) d.Error("FS1519", "signing.provider", message);
            else d.Warning("FS1519", "signing.provider", message);
        }

        var signtoolSettings = !string.IsNullOrEmpty(s.CertificateThumbprint) || !string.IsNullOrEmpty(s.CertificateFile) || !string.IsNullOrEmpty(s.PasswordEnvironmentVariable);
        if (provider != Signer.ProviderSignTool && signtoolSettings)
            d.Info("FS1517", "signing", $"certificateThumbprint, certificateFile and passwordEnvironmentVariable are ignored with the {provider} provider.");
        if (provider != Signer.ProviderTrustedSigning && s.TrustedSigning is not null)
            d.Info("FS1517", "signing.trustedSigning", $"Ignored with the {provider} provider.");
        if (provider != Signer.ProviderCommand && s.Command is not null)
            d.Info("FS1517", "signing.command", $"Ignored with the {provider} provider.");

        switch (provider)
        {
            case Signer.ProviderSignTool:
                if (string.IsNullOrEmpty(s.CertificateThumbprint) && string.IsNullOrEmpty(s.CertificateFile))
                    d.Error("FS1501", "signing", "Signing needs a certificate thumbprint (from the certificate store) or a .pfx file.");
                if (!string.IsNullOrEmpty(s.CertificateThumbprint) && !ThumbprintRegex().IsMatch(Signer.NormalizeThumbprint(s.CertificateThumbprint)))
                    d.Error("FS1518", "signing.certificateThumbprint", "The thumbprint must be the certificate's SHA-1 hash: 40 hexadecimal characters (spaces are allowed).");
                else if (string.IsNullOrEmpty(s.CertificateThumbprint) && !string.IsNullOrEmpty(s.CertificateFile) && !File.Exists(ProjectValidator.Resolve(projectDirectory, s.CertificateFile)))
                    d.Error("FS1502", "signing.certificateFile", $"Certificate file not found: {ProjectValidator.Resolve(projectDirectory, s.CertificateFile)}");
                break;
            case Signer.ProviderTrustedSigning:
                ValidateTrustedSigning(s.TrustedSigning ?? new TrustedSigningSettings(), projectDirectory, d);
                break;
            case Signer.ProviderCommand:
                ValidateCommand(s.Command ?? new SigningCommand(), projectDirectory, d);
                break;
        }
    }

    private static void ValidateTrustedSigning(TrustedSigningSettings t, string projectDirectory, DiagnosticBag d)
    {
        const string f = "signing.trustedSigning";
        if (!(Uri.TryCreate(t.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps))
            d.Error("FS1505", f + ".endpoint", "Give the account's regional endpoint, e.g. https://eus.codesigning.azure.net.");
        if (string.IsNullOrWhiteSpace(t.AccountName) || t.AccountName.Trim() != t.AccountName)
            d.Error("FS1506", f + ".accountName", "Give the Artifact Signing account name.");
        if (string.IsNullOrWhiteSpace(t.CertificateProfile) || t.CertificateProfile.Trim() != t.CertificateProfile)
            d.Error("FS1507", f + ".certificateProfile", "Give the certificate profile name.");
        if (string.IsNullOrWhiteSpace(t.DlibPath))
            d.Error("FS1508", f + ".dlibPath", "Give the path of Azure.CodeSigning.Dlib.dll (NuGet package Microsoft.ArtifactSigning.Client, bin\\x64).");
        else if (!File.Exists(ProjectValidator.Resolve(projectDirectory, t.DlibPath)))
            d.Error("FS1508", f + ".dlibPath", $"File not found: {ProjectValidator.Resolve(projectDirectory, t.DlibPath)}");
        var unknown = t.ExcludeCredentials.Where(c => !AzureCredentials.Contains(c, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            d.Warning("FS1509", f + ".excludeCredentials", $"Unknown credential type(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", AzureCredentials)}.");
    }

    private static void ValidateCommand(SigningCommand c, string projectDirectory, DiagnosticBag d)
    {
        const string f = "signing.command";
        if (string.IsNullOrWhiteSpace(c.Run))
        {
            d.Error("FS1510", f + ".run", "Give the signing program (a name on PATH or a path).");
        }
        else if (Signer.IsCommandPath(c.Run))
        {
            var path = ProjectValidator.Resolve(projectDirectory, c.Run);
            if (!File.Exists(path)) d.Error("FS1513", f + ".run", $"Signing program not found: {path}");
        }
        else if (Signer.FindOnPath(c.Run) is null)
        {
            d.Warning("FS1514", f + ".run", $"'{c.Run}' was not found on PATH.");
        }
        if (!c.Arguments.Any(a => a.Contains("{file}", StringComparison.Ordinal)))
            d.Error("FS1511", f + ".arguments", "The arguments must contain {file} (the file to sign).");
        var unknown = c.Arguments.SelectMany(a => PlaceholderRegex().Matches(a)).Select(m => m.Groups[1].Value)
                                 .Where(n => !Signer.CommandPlaceholders.Contains(n, StringComparer.Ordinal)).Distinct().ToList();
        if (unknown.Count > 0)
            d.Error("FS1512", f + ".arguments", $"Unknown placeholder(s) {string.Join(", ", unknown.Select(n => "{" + n + "}"))}. Known: {string.Join(", ", Signer.CommandPlaceholders.Select(n => "{" + n + "}"))}.");
        foreach (var name in c.Environment.Keys)
        {
            if (name.Length == 0 || name.Length > 256 || name.Contains('=') || name.Any(char.IsControl) || name.Trim() != name)
                d.Error("FS1515", $"{f}.environment.{name}", "Not a valid environment variable name.");
        }
        if (c.TimeoutSeconds is < 10 or > 3600) d.Error("FS1516", f + ".timeoutSeconds", "The timeout must be between 10 and 3600 seconds.");
    }
}
