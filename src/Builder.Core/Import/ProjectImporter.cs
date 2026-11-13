using System.Text;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Import;

/// <summary>
/// Imports an Inno Setup (.iss) or NSIS (.nsi/.nsh) script into a FluentSetup project. The result says, line by line,
/// what was mapped, what differs and what needs manual work (<see cref="ImportResult.Notes"/>); nothing in the script
/// is dropped silently. Paths in the project are relative to <c>outputDirectory</c>, where the project belongs.
/// </summary>
public static partial class ProjectImporter
{
    public const string ProjectFileName = "fluentsetup.json";
    public const string ReportFileName = "import-report.md";

    /// <summary>The script's format from its extension (.iss, .nsi, .nsh) or, for other names, its content; null if neither.</summary>
    public static ScriptFormat? Detect(string scriptPath)
    {
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        if (ext == ".iss") return ScriptFormat.InnoSetup;
        if (ext is ".nsi" or ".nsh") return ScriptFormat.Nsis;
        if (!File.Exists(scriptPath) || new FileInfo(scriptPath).Length > 8 * 1024 * 1024) return null;
        string[] lines;
        try { lines = ScriptText.ReadLines(scriptPath, out _); }
        catch (InvalidDataException) { return null; }
        var inno = 0;
        var nsis = 0;
        foreach (var raw in lines.Take(2000))
        {
            var t = raw.Trim();
            if (Regex.IsMatch(t, @"^\[(Setup|Files|Icons|Run|Registry|Tasks|Code)\]$", RegexOptions.IgnoreCase)) inno += 3;
            else if (Regex.IsMatch(t, @"^(AppName|AppVersion|DefaultDirName|AppId)\s*=", RegexOptions.IgnoreCase)) inno++;
            else if (Regex.IsMatch(t, @"^(!include|!define|!insertmacro|OutFile|InstallDir|Section|SectionEnd|Function|RequestExecutionLevel)\b", RegexOptions.IgnoreCase)) nsis++;
        }
        return inno == 0 && nsis == 0 ? null : inno >= nsis ? ScriptFormat.InnoSetup : ScriptFormat.Nsis;
    }

    /// <summary>
    /// Reads the script (and the files it includes) and builds the project. Throws <see cref="FileNotFoundException"/>
    /// if the script does not exist and <see cref="InvalidDataException"/> if it is not an Inno Setup or NSIS script.
    /// Nothing is written; see <see cref="Save"/>.
    /// </summary>
    public static ImportResult Import(string scriptPath, string outputDirectory)
    {
        var full = Path.GetFullPath(scriptPath);
        if (!File.Exists(full)) throw new FileNotFoundException($"Script not found: {full}", full);
        var format = Detect(full) ?? throw new InvalidDataException($"{full} is neither an Inno Setup script (.iss) nor an NSIS script (.nsi).");
        var ctx = new ImportContext(full, outputDirectory);
        if (format == ScriptFormat.InnoSetup) new InnoSetupImporter(ctx).Run();
        else new NsisImporter(ctx).Run();
        ImportMapper.Finish(ctx);
        Validate(ctx);
        var script = ctx.ScriptPath;
        var notes = ctx.Notes.OrderBy(n => n.File.Equals(script, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n.File, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Line).ToList();
        return new ImportResult { Project = ctx.Project, Notes = notes, Format = format, ScriptPath = full, OutputDirectory = ctx.OutputDirectory };
    }

    /// <summary>
    /// Writes <see cref="ImportResult.ProjectPath"/> and <see cref="ImportResult.ReportPath"/> (creating the folder).
    /// Without <paramref name="overwrite"/> an existing project or report is an <see cref="IOException"/>.
    /// </summary>
    public static void Save(ImportResult result, bool overwrite)
    {
        Directory.CreateDirectory(result.OutputDirectory);
        foreach (var path in new[] { result.ProjectPath, result.ReportPath })
            if (!overwrite && File.Exists(path)) throw new IOException($"{path} already exists.");
        ProjectSerializer.Save(result.Project, result.ProjectPath);
        File.WriteAllText(result.ReportPath, ImportReport.ToMarkdown(result), new UTF8Encoding(false));
    }

    // ---- validation --------------------------------------------------------------------------------------------

    /// <summary>Codes about files that need not exist yet (the app is built later): reported, not fixed.</summary>
    private static readonly string[] MissingFileCodes = ["FS1102", "FS1103", "FS1010", "FS1305", "FS1310"];

    private static void Validate(ImportContext ctx)
    {
        static List<Diagnostic> Errors(List<Diagnostic> all) =>
            all.Where(d => d.Severity == Severity.Error && !MissingFileCodes.Contains(d.Code) && d.Code != "FS1101").ToList();
        var diagnostics = Diagnose(ctx);
        var errors = Errors(diagnostics);
        if (errors.Count > 0)
        {
            Fix(ctx, errors);
            diagnostics = Diagnose(ctx);
            errors = Errors(diagnostics);
        }
        foreach (var d in diagnostics.Where(d => d.Severity != Severity.Error || MissingFileCodes.Contains(d.Code))) Report(ctx, d);
        foreach (var d in errors) ctx.Manual(ctx.Script, $"The generated project still has an error: {d.Message}", d.Field);
    }

    private static List<Diagnostic> Diagnose(ImportContext ctx)
    {
        var bag = new DiagnosticBag();
        var expanded = ProjectVariables.Expand(ctx.Project, null, bag);
        var result = ProjectValidator.Validate(expanded, ctx.OutputDirectory).Items.ToList();
        result.AddRange(bag.Items);
        return result;
    }

    private static void Report(ImportContext ctx, Diagnostic d)
    {
        // "Source folder not found: C:\full\path" → the path as the project writes it.
        var message = d.Message;
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && Path.IsPathRooted(message[(colon + 2)..])) message = $"{message[..colon]}: {d.Field} \"{ctx.ProjectPath(message[(colon + 2)..])}\"";
        switch (d.Code)
        {
            case "FS1102" or "FS1103":
                ctx.Warn(ctx.Script, $"{message} (the app's build output may not exist yet: build it before 'fluentsetup build').", d.Field);
                break;
            case "FS1010" or "FS1305" or "FS1310":
                ctx.Warn(ctx.Script, $"{message} (fix the path, or copy the file).", d.Field);
                break;
            case "FS1006":
                ctx.Manual(ctx.Script, "No publisher: set app.publisher (Settings > Apps shows it).", d.Field);
                break;
            case "FS1012" or "FS1015" or "FS1627":
                break;  // no icon (default is used), no main program (already reported), rollback hint
            default:
                if (d.Severity == Severity.Warning) ctx.Warn(ctx.Script, $"{d.Code}: {d.Message}", d.Field);
                break;
        }
    }

    [GeneratedRegex(@"^(?<list>[A-Za-z.]+)\[(?<index>\d+)\]")]
    private static partial Regex IndexedFieldRegex();

    /// <summary>Repairs what the importer produced from unusual script values; each repair is reported.</summary>
    private static void Fix(ImportContext ctx, List<Diagnostic> errors)
    {
        var p = ctx.Project;
        var removals = new List<(string List, int Index, Diagnostic D)>();
        foreach (var d in errors)
        {
            void Note(string what) => ctx.Manual(ctx.Script, $"{d.Message} {what}", d.Field);
            switch (d.Code)
            {
                case "FS1001" or "FS1002":
                    p.App.Id = ImportHelpers.DeriveAppId(ctx.Resolve(p.App.Publisher), ctx.Resolve(p.App.Name), ctx.MainExecutable, ctx.ScriptPath);
                    Note($"app.id is now \"{p.App.Id}\".");
                    break;
                case "FS1003":
                    p.App.Name = ImportContext.Literal(Path.GetFileNameWithoutExtension(ctx.ScriptPath));
                    Note($"app.name is \"{p.App.Name}\" for now.");
                    break;
                case "FS1004":
                    p.App.Name = ImportContext.Literal(ctx.Resolve(p.App.Name)[..128]);
                    Note("app.name was shortened.");
                    break;
                case "FS1005":
                    p.App.Publisher = ImportContext.Literal(ctx.Resolve(p.App.Publisher)[..128]);
                    Note("app.publisher was shortened.");
                    break;
                case "FS1007":
                {
                    var fixedVersion = ImportHelpers.FixVersion(ctx.Resolve(p.App.Version)) ?? "1.0.0";
                    var m = Regex.Match(p.App.Version, @"^\$\(([^)]+)\)$");
                    if (m.Success) p.Variables[m.Groups[1].Value] = fixedVersion;
                    else p.App.Version = fixedVersion;
                    Note($"The version is \"{fixedVersion}\" for now.");
                    break;
                }
                case "FS1008":
                    p.App.Description = ImportContext.Literal(ctx.Resolve(p.App.Description)[..1024]);
                    Note("app.description was shortened.");
                    break;
                case "FS1009": p.App.Website = ""; Note("app.website was cleared."); break;
                case "FS1011": p.App.Icon = ""; Note("app.icon was cleared (use an .ico or .png file)."); break;
                case "FS1013" or "FS1014": p.App.MainExecutable = ""; Note("app.mainExecutable was cleared; set it to the app's .exe."); break;
                case "FS1106":
                    p.Files.Include.RemoveAll(IsBadPattern); p.Files.Exclude.RemoveAll(IsBadPattern); p.Files.Preserve.RemoveAll(IsBadPattern);
                    if (p.Files.Include.Count == 0) p.Files.Include.Add("**/*");
                    Note("The pattern was removed.");
                    break;
                case "FS1203" or "FS1210" or "FS1211" or "FS1212" when d.Field == "install.userDir":
                    p.Install.UserDir = new InstallSettings().UserDir; Note($"install.userDir is \"{p.Install.UserDir}\".");
                    break;
                case "FS1204" or "FS1210" or "FS1211" or "FS1212" when d.Field == "install.machineDir":
                    p.Install.MachineDir = new InstallSettings().MachineDir; Note($"install.machineDir is \"{p.Install.MachineDir}\".");
                    break;
                case "FS1205": p.Install.DesktopShortcut = "off"; Note("Set to off."); break;
                case "FS1306": p.Ui.License = ""; Note("ui.license was cleared."); break;
                case "FS1308": p.Ui.Title = ""; p.Ui.WelcomeText = ""; p.Ui.FinishText = ""; Note("The texts were cleared."); break;
                case "FS1403": p.Output.FileName = new OutputSettings().FileName; Note($"output.fileName is \"{p.Output.FileName}\"."); break;
                case "FS1667": p.Install.StartMenuFolder = ""; Note("install.startMenuFolder was cleared."); break;
                case "FS1801":
                {
                    var name = Regex.Match(d.Message, @"\$\(([^)]+)\)").Groups[1].Value;
                    if (name.Length > 0) p.Variables[name] = "";
                    Note($"variables.{name} was added (empty).");
                    break;
                }
                case "FS1802":
                    p.Variables.Remove(d.Field["variables.".Length..]);
                    Note("The variable was removed.");
                    break;
                case "FS1612":
                    if (IndexedFieldRegex().Match(d.Field) is { Success: true } om && int.Parse(om.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture) is var oi && oi < p.Options.Count)
                    {
                        p.Options[oi].Label = new Dictionary<string, string> { ["en-US"] = p.Options[oi].Id };
                        Note("The option's label is its id for now.");
                    }
                    break;
                case "FS1625":
                    if (IndexedFieldRegex().Match(d.Field) is { Success: true } am && int.Parse(am.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture) is var ai && ai < p.Actions.Count)
                    {
                        p.Actions[ai].IfOption = "";
                        Note("The action runs without that condition.");
                    }
                    break;
                default:
                    if (IndexedFieldRegex().Match(d.Field) is { Success: true } m2)
                        removals.Add((m2.Groups["list"].Value, int.Parse(m2.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture), d));
                    else ctx.Manual(ctx.Script, $"The generated project has an error the importer could not repair: {d.Message}", d.Field);
                    break;
            }
        }
        foreach (var (list, index, d) in removals.OrderByDescending(r => r.Index))
        {
            if (ListOf(p, list) is { } items && index < items.Count)
            {
                items.RemoveAt(index);
                ctx.Manual(ctx.Script, $"{d.Message} The entry was removed from {list}.", list);
            }
            else ctx.Manual(ctx.Script, $"The generated project has an error the importer could not repair: {d.Message}", d.Field);
        }
    }

    private static bool IsBadPattern(string pattern) => string.IsNullOrWhiteSpace(pattern) || pattern.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(pattern);

    private static System.Collections.IList? ListOf(SetupProject p, string name) => name switch
    {
        "files.extra" => p.Files.Extra,
        "files.emptyDirectories" => p.Files.EmptyDirectories,
        "install.closeProcesses" => p.Install.CloseProcesses,
        "install.legacyUninstallKeys" => p.Install.LegacyUninstallKeys,
        "install.addToPath" => p.Install.AddToPath,
        "install.cleanup.removeFiles" => p.Install.Cleanup.RemoveFiles,
        "install.cleanup.removeScheduledTasks" => p.Install.Cleanup.RemoveScheduledTasks,
        "install.cleanup.removeRunValues" => p.Install.Cleanup.RemoveRunValues,
        "install.cleanup.removeShortcuts" => p.Install.Cleanup.RemoveShortcuts,
        "install.cleanup.removeUninstallKeys" => p.Install.Cleanup.RemoveUninstallKeys,
        "uninstall.removeData" => p.Uninstall.RemoveData,
        "uninstall.removeScheduledTasks" => p.Uninstall.RemoveScheduledTasks,
        "uninstall.removeRunValues" => p.Uninstall.RemoveRunValues,
        "uninstall.removeShortcuts" => p.Uninstall.RemoveShortcuts,
        "uninstall.removeRegistryKeys" => p.Uninstall.RemoveRegistryKeys,
        "uninstall.userData.paths" => p.Uninstall.UserData.Paths,
        "uninstall.elevated.removeRegistryKeys" => p.Uninstall.Elevated.RemoveRegistryKeys,
        "actions" => p.Actions,
        "options" => p.Options,
        "shortcuts" => p.Shortcuts,
        "associations" => p.Associations,
        "protocols" => p.Protocols,
        "registry" => p.Registry,
        "environment" => p.Environment,
        "configFiles" => p.ConfigFiles,
        "requirements.dotNet" => p.Requirements.DotNet,
        _ => null,
    };
}
