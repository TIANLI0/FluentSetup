namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Locates the prebuilt installer runtime that ships next to the CLI/Builder:
/// <c>runtime/&lt;arch&gt;/FluentSetup.Stub.exe</c>, <c>runtime/&lt;arch&gt;/ui/*</c> (WinUI front end),
/// <c>runtime/localization/*.json</c> and <c>runtime/default-app.ico</c>.
/// </summary>
public sealed class RuntimeAssets
{
    /// <summary>Files of the WinUI front end that are actually needed at run time.</summary>
    public static readonly string[] UiFilePatterns = ["*.exe", "*.dll", "*.pri", "*.xbf"];

    public string Root { get; }

    public RuntimeAssets(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("FLUENTSETUP_RUNTIME") ?? Path.Combine(AppContext.BaseDirectory, "runtime");
    }

    public string StubPath(string arch) => Path.Combine(Root, arch, "FluentSetup.Stub.exe");
    public string UiDirectory(string arch) => Path.Combine(Root, arch, "ui");
    public string UiEntry => "FluentSetup.UI.exe";
    public string DefaultIcon => Path.Combine(Root, "default-app.ico");
    public string LocalizationFile(string language) => Path.Combine(Root, "localization", language + ".json");

    public bool HasStub(string arch) => File.Exists(StubPath(arch));
    public bool HasUi(string arch) => File.Exists(Path.Combine(UiDirectory(arch), UiEntry));

    public IReadOnlyList<string> UiFiles(string arch)
    {
        var dir = UiDirectory(arch);
        if (!Directory.Exists(dir)) return [];
        return UiFilePatterns.SelectMany(p => Directory.EnumerateFiles(dir, p, SearchOption.TopDirectoryOnly))
                             .Where(f => !f.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                             .ToList();
    }
}
