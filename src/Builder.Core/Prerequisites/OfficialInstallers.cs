using FluentSetup.Builder.Core.Model;

namespace FluentSetup.Builder.Core.Prerequisites;

/// <summary>Kinds of prerequisites FluentSetup knows Microsoft's official installer for.</summary>
public enum PrerequisiteKind { WebView2, NetFramework, DotNet, WindowsAppRuntime, VcRedist }

/// <summary>Where Microsoft publishes the installer of a prerequisite, and how to run it silently.</summary>
/// <param name="Url">https link that always serves the current installer (fwlink / aka.ms redirect).</param>
/// <param name="FileName">Name the installer is stored under (in the package's redist set).</param>
/// <param name="Arguments">Silent-install arguments.</param>
/// <param name="Signer">Subject name of the Authenticode certificate the installer must be signed with.</param>
public sealed record OfficialInstaller(string Url, string FileName, IReadOnlyList<string> Arguments, string Signer);

/// <summary>
/// The official installers used for <c>"auto": "bundle"</c> and <c>"auto": "download"</c>. The links redirect to the
/// latest release of each installer, which is why downloads at install time are pinned by signature, not by SHA-256.
/// </summary>
public static class OfficialInstallers
{
    public const string MicrosoftSigner = "Microsoft Corporation";

    /// <summary>The official installer for a prerequisite of a package built for <paramref name="architecture"/> (x64 | arm64), or null when there is none.</summary>
    public static OfficialInstaller? For(PrerequisiteKind kind, string architecture, string version = "", string runtime = "", string vcArchitecture = "")
    {
        var arch = architecture == "arm64" ? "arm64" : "x64";
        switch (kind)
        {
            case PrerequisiteKind.WebView2:
                // The Evergreen bootstrapper (about 2 MB) installs the runtime for the PC's architecture; it needs the internet.
                return new("https://go.microsoft.com/fwlink/p/?LinkId=2124703", "MicrosoftEdgeWebview2Setup.exe", ["/silent", "/install"], MicrosoftSigner);
            case PrerequisiteKind.NetFramework:
                // Offline installers: 4.8 still runs on Windows 10 1607+, 4.8.1 needs 21H2+.
                return version == "4.8.1"
                    ? new("https://go.microsoft.com/fwlink/?linkid=2203305", "NDP481-x86-x64-AllOS-ENU.exe", ["/q", "/norestart"], MicrosoftSigner)
                    : new("https://go.microsoft.com/fwlink/?linkid=2088631", "NDP48-x86-x64-AllOS-ENU.exe", ["/q", "/norestart"], MicrosoftSigner);
            case PrerequisiteKind.DotNet:
            {
                var channel = Channel(version);
                if (channel is null) return null;
                var product = runtime switch
                {
                    "core" or "Microsoft.NETCore.App" => "dotnet",
                    "aspnetcore" or "Microsoft.AspNetCore.App" => "aspnetcore",
                    _ => "windowsdesktop",
                };
                var file = $"{product}-runtime-win-{arch}.exe";
                return new($"https://aka.ms/dotnet/{channel}/{file}", $"{product}-runtime-{channel}-win-{arch}.exe", ["/install", "/quiet", "/norestart"], MicrosoftSigner);
            }
            case PrerequisiteKind.WindowsAppRuntime:
            {
                var channel = Channel(version);
                if (channel is null) return null;
                return new($"https://aka.ms/windowsappsdk/{channel}/latest/windowsappruntimeinstall-{arch}.exe",
                           $"WindowsAppRuntimeInstall-{channel}-{arch}.exe", ["--quiet"], MicrosoftSigner);
            }
            case PrerequisiteKind.VcRedist:
            {
                var vcArch = vcArchitecture is "x64" or "x86" or "arm64" ? vcArchitecture : arch;
                return new($"https://aka.ms/vs/17/release/vc_redist.{vcArch}.exe", $"vc_redist.{vcArch}.exe", ["/install", "/quiet", "/norestart"], MicrosoftSigner);
            }
            default:
                return null;
        }
    }

    /// <summary>"Major.Minor" of a version ("10.0.2" → "10.0", "1.6" → "1.6", "2" → "2.0"), or null.</summary>
    public static string? Channel(string version)
    {
        var parts = version.Split('-')[0].Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)) return null;
        var minor = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
        return $"{major}.{minor}";
    }

    /// <summary>Every prerequisite of a project that can use an official installer, with its kind and parameters.</summary>
    public static IEnumerable<(PrerequisiteSource Source, string Field, PrerequisiteKind Kind, string Version, string Runtime, string VcArchitecture)> Sources(SetupProject p)
    {
        var r = p.Requirements;
        if (!string.IsNullOrEmpty(r.NetFramework) && r.NetFrameworkSetup is { } nf) yield return (nf, "requirements.netFrameworkSetup", PrerequisiteKind.NetFramework, r.NetFramework, "", "");
        if (r.WebView2.Required) yield return (r.WebView2, "requirements.webView2", PrerequisiteKind.WebView2, r.WebView2.MinVersion, "", "");
        if (r.WindowsAppRuntime.Required) yield return (r.WindowsAppRuntime, "requirements.windowsAppRuntime", PrerequisiteKind.WindowsAppRuntime, r.WindowsAppRuntime.Version, "", "");
        if (r.VcRedist.Required) yield return (r.VcRedist, "requirements.vcRedist", PrerequisiteKind.VcRedist, r.VcRedist.MinVersion, "", r.VcRedist.Architecture);
        foreach (var (d, i) in r.DotNet.Select((d, i) => (d, i))) yield return (d, $"requirements.dotNet[{i}]", PrerequisiteKind.DotNet, d.Version, d.Runtime, "");
    }
}
