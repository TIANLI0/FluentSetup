using System.Text;

namespace FluentSetup.Builder.Core.Import;

/// <summary>The import report (import-report.md): a checklist of what needs manual work, what to check and what was mapped.</summary>
public static class ImportReport
{
    public static string ToMarkdown(ImportResult result)
    {
        var p = result.Project;
        var scriptDir = Path.GetDirectoryName(result.ScriptPath)!;
        var script = Path.GetFileName(result.ScriptPath);
        var projectRel = Path.GetRelativePath(scriptDir, result.ProjectPath).Replace('\\', '/');
        string Where(ImportNote n)
        {
            if (n.File.Length == 0) return "";
            var rel = Path.GetRelativePath(scriptDir, n.File).Replace('\\', '/');
            return n.Line > 0 ? $"`{rel}:{n.Line}`" : $"`{rel}`";
        }
        string Value(string s) => Resolve(p, s);
        var scope = p.Install.Scope switch
        {
            "user" => "per-user",
            "machine" => "per-machine",
            _ => $"per-user or per-machine (default {(p.Install.DefaultScope == "user" ? "per-user" : "per-machine")})",
        };
        var manual = result.Notes.Where(n => n.Severity == ImportSeverity.Manual).ToList();
        var warnings = result.Notes.Where(n => n.Severity == ImportSeverity.Warning).ToList();
        var infos = result.Notes.Where(n => n.Severity == ImportSeverity.Info).ToList();

        var sb = new StringBuilder();
        sb.Append("# Import report: ").AppendLine(Escape(script)).AppendLine();
        sb.Append("`fluentsetup import` converted the ").Append(result.FormatName).Append(" script **").Append(Escape(script)).Append("** into **")
          .Append(Escape(projectRel)).AppendLine("**. Work through the lists below, then build the app and run")
          .Append("`fluentsetup validate ").Append(projectRel).AppendLine("`.").AppendLine();
        sb.AppendLine("| | |").AppendLine("|---|---|");
        sb.Append("| App | ").Append(Escape(Value(p.App.Name))).Append(' ').Append(Escape(Value(p.App.Version))).Append(" (").Append(Code(p.App.Id)).AppendLine(") |");
        if (p.App.Publisher.Length > 0) sb.Append("| Publisher | ").Append(Escape(Value(p.App.Publisher))).AppendLine(" |");
        sb.Append("| Install | ").Append(scope).Append(", ").Append(Code(p.Install.Scope == "machine" ? p.Install.MachineDir : p.Install.UserDir)).AppendLine(" |");
        sb.Append("| Main program | ").Append(p.App.MainExecutable.Length > 0 ? Code(p.App.MainExecutable) : "not set").AppendLine(" |");
        sb.Append("| Files | ").Append(p.Files.Source.Length > 0 ? Code(p.Files.Source) : "not set").Append(p.Files.Extra.Count > 0 ? $" + {p.Files.Extra.Count} extra" : "").AppendLine(" |");
        if (p.Variables.Count > 0) sb.Append("| Variables | ").Append(string.Join(", ", p.Variables.Select(v => $"{Code(v.Key)} = {Code(v.Value)}"))).AppendLine(" |");
        sb.Append("| Result | ").Append(manual.Count).Append(" to do by hand, ").Append(warnings.Count).Append(" to check, ").Append(infos.Count).AppendLine(" mapped or not needed |");
        sb.AppendLine();

        void Section(string title, string intro, List<ImportNote> notes, bool checkbox)
        {
            sb.Append("## ").Append(title).Append(" (").Append(notes.Count).AppendLine(")").AppendLine();
            if (notes.Count == 0) { sb.AppendLine("Nothing.").AppendLine(); return; }
            sb.AppendLine(intro).AppendLine();
            foreach (var n in notes)
            {
                sb.Append(checkbox ? "- [ ] " : "- ");
                var where = Where(n);
                if (where.Length > 0) sb.Append(where).Append(' ');
                sb.AppendLine(Escape(n.Message));
            }
            sb.AppendLine();
        }
        Section("To do by hand", "The project cannot express these as the script did. Decide what to do with each one.", manual, true);
        Section("To check", "Imported, but FluentSetup behaves somewhat differently. Make sure that is fine for the app.", warnings, true);
        Section("Mapped", "Imported as described, or not needed with FluentSetup.", infos, false);

        sb.AppendLine("## Next steps").AppendLine();
        sb.AppendLine("1. Work through the two checklists above and edit the project (in the FluentSetup Builder or a text editor).");
        sb.Append("2. Build the app so that `files.source` exists, then run `fluentsetup validate ").Append(projectRel).AppendLine("`.");
        sb.Append("3. Run `fluentsetup build ").Append(projectRel).AppendLine("` and test the installer on a test PC or VM: `Setup.exe /quiet /log setup.log`,");
        sb.AppendLine("   an upgrade over the version the old installer installed, and the uninstall.");
        return sb.ToString();
    }

    private static string Resolve(Model.SetupProject p, string s)
    {
        foreach (var (k, v) in p.Variables) s = s.Replace("$(" + k + ")", v, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    /// <summary>A code span (shown as written; '|' escaped for tables).</summary>
    private static string Code(string text) => "`" + text.Replace('`', '\'').Replace("|", "\\|", StringComparison.Ordinal) + "`";

    /// <summary>Escapes Markdown so paths and globs ("**/*.pdb", "unins???.exe", "{InstallDir}\logs") show as written.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '|' or '#') sb.Append('\\');
            sb.Append(c == '\n' ? ' ' : c);
        }
        return sb.ToString();
    }
}
