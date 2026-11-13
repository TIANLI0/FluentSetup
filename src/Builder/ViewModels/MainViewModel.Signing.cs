using CommunityToolkit.Mvvm.ComponentModel;
using FluentSetup.Builder.Core.Model;

namespace FluentSetup.Builder.ViewModels;

/// <summary>Signing providers (signing.provider): signtool, Azure Trusted Signing, or any signing command.</summary>
public partial class MainViewModel
{
    private static readonly string[] SigningProviders = ["signtool", "trustedSigning", "command"];

    [ObservableProperty] public partial int SigningProviderIndex { get; set; }
    [ObservableProperty] public partial string TrustedSigningEndpoint { get; set; } = "";
    [ObservableProperty] public partial string TrustedSigningAccount { get; set; } = "";
    [ObservableProperty] public partial string TrustedSigningProfile { get; set; } = "";
    [ObservableProperty] public partial string TrustedSigningDlib { get; set; } = "";
    [ObservableProperty] public partial string TrustedSigningCorrelationId { get; set; } = "";
    [ObservableProperty] public partial string SigningCommandRun { get; set; } = "";
    /// <summary>One argument per line ({file}, {output}, {description}, {timestampUrl}).</summary>
    [ObservableProperty] public partial string SigningCommandArguments { get; set; } = "";
    /// <summary>NAME=value per line.</summary>
    [ObservableProperty] public partial string SigningCommandEnvironment { get; set; } = "";
    [ObservableProperty] public partial double SigningCommandTimeout { get; set; } = 600;

    public bool IsSignToolProvider => SigningProviderIndex == 0;
    public bool IsTrustedSigningProvider => SigningProviderIndex == 1;
    public bool IsCommandProvider => SigningProviderIndex == 2;
    /// <summary>The timestamp server is used by every provider; signtool.exe by the first two.</summary>
    public bool UsesSignTool => SigningProviderIndex is 0 or 1;

    partial void OnSigningProviderIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSignToolProvider));
        OnPropertyChanged(nameof(IsTrustedSigningProvider));
        OnPropertyChanged(nameof(IsCommandProvider));
        OnPropertyChanged(nameof(UsesSignTool));
    }

    private void LoadSigning(SetupProject p)
    {
        SigningProviderIndex = Math.Max(0, Array.IndexOf(SigningProviders, string.IsNullOrEmpty(p.Signing.Provider) ? "signtool" : p.Signing.Provider));
        var ts = p.Signing.TrustedSigning;
        TrustedSigningEndpoint = ts?.Endpoint ?? "";
        TrustedSigningAccount = ts?.AccountName ?? "";
        TrustedSigningProfile = ts?.CertificateProfile ?? "";
        TrustedSigningDlib = ts?.DlibPath ?? "";
        TrustedSigningCorrelationId = ts?.CorrelationId ?? "";
        var cmd = p.Signing.Command;
        SigningCommandRun = cmd?.Run ?? "";
        SigningCommandArguments = cmd is null ? "" : string.Join(Environment.NewLine, cmd.Arguments);
        SigningCommandEnvironment = cmd is null ? "" : string.Join(Environment.NewLine, cmd.Environment.Select(kv => $"{kv.Key}={kv.Value}"));
        SigningCommandTimeout = cmd?.TimeoutSeconds ?? 600;
    }

    private void ApplySigning(SetupProject p)
    {
        p.Signing.Provider = SigningProviderIndex is >= 0 and < 3 ? SigningProviders[SigningProviderIndex] : p.Signing.Provider;
        var anyTrusted = new[] { TrustedSigningEndpoint, TrustedSigningAccount, TrustedSigningProfile, TrustedSigningDlib, TrustedSigningCorrelationId }.Any(s => s.Trim().Length > 0);
        if (anyTrusted || p.Signing.TrustedSigning is not null)
        {
            var ts = p.Signing.TrustedSigning ?? new TrustedSigningSettings();  // keeps excludeCredentials
            ts.Endpoint = TrustedSigningEndpoint.Trim();
            ts.AccountName = TrustedSigningAccount.Trim();
            ts.CertificateProfile = TrustedSigningProfile.Trim();
            ts.DlibPath = TrustedSigningDlib.Trim();
            ts.CorrelationId = TrustedSigningCorrelationId.Trim();
            p.Signing.TrustedSigning = ts;
        }
        static List<string> Lines(string text) => [.. text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0)];
        if (SigningCommandRun.Trim().Length > 0 || SigningCommandArguments.Trim().Length > 0 || p.Signing.Command is not null)
        {
            var cmd = p.Signing.Command ?? new SigningCommand();
            cmd.Run = SigningCommandRun.Trim();
            cmd.Arguments = Lines(SigningCommandArguments);
            cmd.Environment = Lines(SigningCommandEnvironment)
                .Select(l => l.Split('=', 2))
                .Where(kv => kv[0].Trim().Length > 0)
                .GroupBy(kv => kv[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Length > 1 ? g.Last()[1] : "", StringComparer.OrdinalIgnoreCase);
            cmd.TimeoutSeconds = double.IsNaN(SigningCommandTimeout) ? 600 : (int)Math.Clamp(SigningCommandTimeout, 10, 3600);
            p.Signing.Command = cmd;
        }
    }
}
