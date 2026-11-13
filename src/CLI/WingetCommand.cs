using System.Text.Json;
using FluentSetup.Builder.Core.Winget;

namespace FluentSetup.Cli;

/// <summary>fluentsetup winget &lt;Setup.exe&gt;: winget manifests for a built package (docs/winget.md).</summary>
internal static class WingetCommand
{
    public static int Run(ParsedArgs a)
    {
        var setup = Path.GetFullPath(a.Positional(0, "Setup.exe"));
        var options = new WingetOptions
        {
            InstallerUrl = a.Value("url"),
            GitHubRepository = a.Value("github"),
            Identifier = a.Value("identifier"),
            License = a.Value("license"),
            LicenseUrl = a.Value("license-url"),
            ShortDescription = a.Value("short-description"),
            PublisherUrl = a.Value("publisher-url"),
            ReleaseNotesUrl = a.Value("release-notes-url"),
            Tags = a.Value("tags") is { } tags ? [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [],
        };
        var output = Path.GetFullPath(a.Value("output", "o") ?? ".");
        var json = a.Flag("json");
        a.EnsureAllUsed();
        if (a.PositionalOrDefault(1) is { } extra) throw new UsageException($"Unexpected argument '{extra}'.");
        if ((options.InstallerUrl is null) == (options.GitHubRepository is null))
            throw new UsageException("Give the download address of Setup.exe: --url <https://...> or --github <owner/repo>.");
        if (!File.Exists(setup)) throw new UsageException($"File not found: {setup}");

        WingetManifestSet manifests;
        try
        {
            manifests = WingetManifests.Create(setup, options);
        }
        catch (WingetManifestException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        var files = manifests.Write(output);
        var folder = Path.GetDirectoryName(files[0])!;
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                identifier = manifests.Identifier, version = manifests.Version, manifestVersion = WingetManifests.ManifestVersion,
                folder, files, installerUrl = manifests.InstallerUrl, installerSha256 = manifests.InstallerSha256,
                architecture = manifests.Architecture, scopes = manifests.Scopes, license = manifests.License, notes = manifests.Notes,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        Console.WriteLine($"Wrote the winget manifests of {manifests.Identifier} {manifests.Version} to {folder}");
        foreach (var f in files) Console.WriteLine($"  {Path.GetFileName(f)}");
        Console.WriteLine($"  installer: {manifests.InstallerUrl} ({manifests.Architecture}, {string.Join(" + ", manifests.Scopes)})");
        Console.WriteLine($"  SHA-256 {manifests.InstallerSha256}");
        foreach (var n in manifests.Notes) Console.WriteLine($"note: {n}");
        Console.WriteLine();
        Console.WriteLine("Upload Setup.exe to that address first, then check and try the manifests:");
        Console.WriteLine($"  winget validate --manifest \"{folder}\"");
        Console.WriteLine($"  winget install --manifest \"{folder}\"   (after: winget settings --enable LocalManifestFiles)");
        return 0;
    }
}
