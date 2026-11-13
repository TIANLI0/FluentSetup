using FluentSetup.Builder.Core.Model;

namespace FluentSetup.Builder.Core.Import;

/// <summary>Kind of installer script a project was imported from.</summary>
public enum ScriptFormat { InnoSetup, Nsis }

/// <summary>info = mapped (or harmlessly ignored), warning = mapped with a difference worth checking, manual = needs manual work.</summary>
public enum ImportSeverity { Info, Warning, Manual }

/// <summary>One finding of an import, with the script line it comes from.</summary>
/// <param name="File">Full path of the script file ("" = the import as a whole).</param>
/// <param name="Line">1-based line in <paramref name="File"/> (0 = no particular line).</param>
/// <param name="Message">What was mapped, what differs or what to do.</param>
/// <param name="Field">The project property the note is about, e.g. "install.scope" ("" = none).</param>
public sealed record ImportNote(ImportSeverity Severity, string File, int Line, string Message, string Field = "")
{
    public override string ToString() =>
        $"{Severity.ToString().ToLowerInvariant()} {(File.Length == 0 ? "" : System.IO.Path.GetFileName(File) + (Line > 0 ? $":{Line}" : "") + ": ")}{Message}";
}

/// <summary>The project generated from an Inno Setup or NSIS script and what the importer found on the way.</summary>
public sealed class ImportResult
{
    public SetupProject Project { get; set; } = new();
    public List<ImportNote> Notes { get; set; } = [];
    public ScriptFormat Format { get; init; }
    /// <summary>Full path of the imported script.</summary>
    public string ScriptPath { get; init; } = "";
    /// <summary>Folder the project belongs in: its relative paths are relative to this folder.</summary>
    public string OutputDirectory { get; init; } = "";

    public string ProjectPath => Path.Combine(OutputDirectory, "fluentsetup.json");
    public string ReportPath => Path.Combine(OutputDirectory, "import-report.md");
    public string FormatName => Format == ScriptFormat.InnoSetup ? "Inno Setup" : "NSIS";
    public int Count(ImportSeverity severity) => Notes.Count(n => n.Severity == severity);
}
