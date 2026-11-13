using System.Reflection;
using System.Text.Json;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Updates;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Cli;

/// <summary>
/// fluentsetup - command-line front end of FluentSetup.
/// Exit codes: 0 success, 1 validation or build failure, 2 usage error, 3 unexpected error, 4 cancelled.
/// </summary>
public static class Program
{
    private const string Usage = """
        FluentSetup - build Windows installers.

        Usage:
          fluentsetup build <project.json> [options]   Build Setup.exe
              -o, --output <dir>        Output folder (default: project output.directory)
              --arch x64|arm64          Target architecture (default: project output.architecture)
              --variant <id>[,<id>]     Variants to include (default: project output.variants, else all)
              --sign | --no-sign        Override the project's signing setting
              --report <file.json>      Write a machine-readable build report
              --runtime <dir>           Use another installer runtime folder
              -D, --define <name>=<value>
                                        Value for $(name) in the project (repeatable; overrides "variables"),
                                        e.g. -D Version=1.2.1 with "version": "$(Version)"
              -v, --verbose             Detailed log
              --json                    Print the build report as JSON instead of text
              --non-interactive         Never prompt (the default; accepted for CI scripts)
              --refresh-installers      Download official prerequisite installers ("auto": "bundle") again
          fluentsetup validate <project.json> [--json] [-D <name>=<value> ...]
          fluentsetup inspect <Setup.exe> [--verify] [--json]
          fluentsetup init [folder] --name <name> --id <app.id> --source <files folder> [--publisher <name>]
          fluentsetup locale add <language> <project.json> [--force]
                                        Create <ui.localization>/<language>.json with every text to translate
                                        (installer strings, option/action/variant ids); any BCP-47 tag, e.g. fr-FR
          fluentsetup update [--check] [--prerelease] [--json]
                                        Update FluentSetup (Builder and CLI) to the latest release on GitHub:
                                        downloads Setup.exe, verifies its SHA-256 and installs it.
                                        --check only reports; --json (with --check) prints the result as JSON
          fluentsetup guide                     Print the complete project-authoring guide (for people and AI tools)
          fluentsetup schema                    Print the JSON Schema of fluentsetup.json
          fluentsetup --version | --help

        Exit codes: 0 success, 1 validation/build failure, 2 usage error, 3 unexpected error, 4 cancelled.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "/?" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }
            if (args[0] is "--version" or "-V")
            {
                Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0");
                return 0;
            }
            var parsed = ParsedArgs.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "build" => await BuildAsync(parsed, cts.Token),
                "validate" => Validate(parsed),
                "inspect" => Inspect(parsed),
                "init" => Init(parsed),
                "guide" => PrintResource("FluentSetup.Cli.ai-guide.md"),
                "schema" => PrintResource("FluentSetup.Cli.fluentsetup.project.schema.json"),
                "locale" => Locale(parsed),
                "update" => await UpdateAsync(parsed, cts.Token),
                _ => UsageError($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            return UsageError(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 4;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> UpdateAsync(ParsedArgs a, CancellationToken ct)
    {
        bool checkOnly = a.Flag("check");
        bool prerelease = a.Flag("prerelease");
        bool json = a.Flag("json");
        a.EnsureAllUsed();
        if (a.PositionalOrDefault(0) is { } extra) throw new UsageException($"Unexpected argument '{extra}'.");
        if (json && !checkOnly) throw new UsageException("--json is only for --check.");

        string current = FluentSetupUpdates.CurrentVersion;
        using var http = FluentSetupUpdates.CreateClient();
        FluentSetupRelease? latest;
        try
        {
            latest = await FluentSetupUpdates.GetLatestAsync(http, prerelease, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Console.Error.WriteLine($"error: could not check for updates: {(ex is TaskCanceledException ? "GitHub did not answer in time." : ex.Message)}");
            return 1;
        }
        bool newer = latest is not null && FluentSetupUpdates.CompareVersions(latest.Version, current) > 0;
        string? installed = FluentSetupUpdates.InstalledFolder();

        if (json)
        {
            var report = new Dictionary<string, object?>
            {
                ["currentVersion"] = current,
                ["latestVersion"] = latest?.Version,
                ["updateAvailable"] = newer,
                ["releaseUrl"] = latest?.PageUrl,
                ["installedWithSetup"] = installed is not null,
            };
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (!newer)
        {
            Console.WriteLine($"FluentSetup {current} is up to date.");
            return 0;
        }
        Console.WriteLine($"FluentSetup {latest!.Version} is available (this is {current}): {latest.PageUrl}");
        if (checkOnly) return 0;
        if (installed is null)
        {
            // A copy from the zip (or a build): replacing the running files is the user's call.
            Console.Error.WriteLine("This copy was not installed with FluentSetup's Setup.exe, so it cannot update itself.");
            if (latest.CliZip is { } zip) Console.Error.WriteLine($"Download the CLI: {zip.Url}");
            Console.Error.WriteLine($"Or install everything with: {latest.Setup!.Url}");
            return 1;
        }

        bool showProgress = !Console.IsErrorRedirected;
        int lastPercent = -1;
        var progress = new Progress<double>(f =>
        {
            int percent = (int)(f * 100);
            if (!showProgress || percent == lastPercent) return;
            lastPercent = percent;
            Console.Error.Write("\r");
            Console.Error.Write($"Downloading {latest.Setup!.Name}... {percent}%");
        });
        string setup;
        try
        {
            setup = await FluentSetupUpdates.DownloadVerifiedAsync(http, latest, latest.Setup!, progress, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            if (showProgress) Console.Error.WriteLine();
            Console.Error.WriteLine($"error: download failed: {(ex is TaskCanceledException ? "the server stopped answering." : ex.Message)}");
            return 1;
        }
        if (showProgress) Console.Error.WriteLine();
        Console.WriteLine("SHA-256 verified. Starting Setup (it closes FluentSetup if it is running).");
        FluentSetupUpdates.StartSetup(setup, relaunch: false);
        return 0;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine("Run 'fluentsetup --help' for usage.");
        return 2;
    }

    private static SetupProject LoadProject(string path)
    {
        if (!File.Exists(path)) throw new UsageException($"Project file not found: {path}");
        return ProjectSerializer.Load(path);
    }

    private static async Task<int> BuildAsync(ParsedArgs a, CancellationToken ct)
    {
        var projectPath = Path.GetFullPath(a.Positional(0, "project file"));
        var project = LoadProject(projectPath);
        var verbose = a.Flag("verbose", "v");
        var json = a.Flag("json");
        var options = new BuildOptions
        {
            OutputDirectory = a.Value("output", "o") is { } o ? Path.GetFullPath(o) : null,
            Architecture = a.Value("arch"),
            Variants = a.Value("variant") is { Length: > 0 } vs ? [.. vs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null,
            RuntimeRoot = a.Value("runtime"),
            Sign = a.Flag("sign") ? true : a.Flag("no-sign") ? false : null,
            Log = verbose && !json ? m => Console.WriteLine($"  {m}") : null,
            Defines = a.Defines(),
            RefreshInstallers = a.Flag("refresh-installers"),
            TestHooks = a.Flag("test-hooks"),  // undocumented: packages for FluentSetup's own tests
        };
        a.Flag("non-interactive");
        var reportPath = a.Value("report");
        a.EnsureAllUsed();
        if (options.Architecture is not null and not ("x64" or "arm64")) throw new UsageException("--arch must be x64 or arm64.");

        var lastStage = (BuildStage)(-1);
        var progress = new SyncProgress<BuildProgress>(p =>
        {
            if (json || p.Stage == lastStage) return;
            lastStage = p.Stage;
            Console.WriteLine($"> {p.Message}");
        });
        var builder = new PackageBuilder();
        var report = await builder.BuildAsync(project, projectPath, options, progress, ct);

        if (reportPath is not null)
        {
            File.WriteAllText(Path.GetFullPath(reportPath), JsonSerializer.Serialize(report, ReportJson));
        }
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, ReportJson));
        }
        else
        {
            PrintDiagnostics(report.Diagnostics, verbose);
            if (report.Success)
            {
                var s = report.Sizes;
                Console.WriteLine();
                Console.WriteLine($"Built {report.OutputPath}");
                Console.WriteLine($"  {report.FileCount:N0} files, {PackageBuilder.Format(s.PayloadRaw)} -> {PackageBuilder.Format(s.PayloadCompressed)} compressed");
                Console.WriteLine($"  Setup.exe {PackageBuilder.Format(s.Total)} (installer overhead {PackageBuilder.Format(s.InstallerOverhead)}: " +
                                  $"stub {PackageBuilder.Format(s.Stub)}, WinUI {PackageBuilder.Format(s.WinUiFrontEnd)}, header {PackageBuilder.Format(s.Header)}" +
                                  (s.Signature > 0 ? $", signature {PackageBuilder.Format(s.Signature)}" : "") + ")");
                Console.WriteLine($"  SHA-256 {report.Sha256}");
                foreach (var b in report.BundledInstallers)
                    Console.WriteLine($"  Bundled {b.FileName} ({PackageBuilder.Format(b.Size)}, {b.Url}, downloaded {b.Downloaded.LocalDateTime:g}{(b.SignatureChecked ? ", signature checked" : "")})");
                Console.WriteLine($"  {report.DurationSeconds:F1} s{(report.Signed ? ", signed" : "")}{(report.UninstallerSigned ? " (also uninstall.exe)" : "")}{(report.IncludesWinUI ? "" : ", Win32 interface only")}");
            }
            else
            {
                Console.Error.WriteLine($"Build failed: {report.Error}");
            }
        }
        return report.Success ? 0 : 1;
    }

    private static int PrintResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Resource {name} is missing from this build.");
        using var reader = new StreamReader(stream);
        Console.Out.Write(reader.ReadToEnd());
        return 0;
    }

    /// <summary>
    /// locale add &lt;lang&gt; &lt;project.json&gt;: writes a localization file listing every text of the package and, for a
    /// language the installer does not ship, every installer string (English as the starting point).
    /// </summary>
    private static int Locale(ParsedArgs a)
    {
        if (a.PositionalOrDefault(0) != "add") throw new UsageException("Use: fluentsetup locale add <language> <project.json>");
        var lang = a.PositionalOrDefault(1) ?? throw new UsageException("Give a language tag, e.g. fr-FR.");
        var projectPath = a.PositionalOrDefault(2) ?? throw new UsageException("Give the project file.");
        if (!Localization.IsLanguageTag(lang)) throw new UsageException($"'{lang}' is not a language tag (e.g. fr-FR, de, pt-BR).");
        var project = ProjectSerializer.Load(projectPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        if (string.IsNullOrEmpty(project.Ui.Localization))
            throw new UsageException("Set ui.localization in the project first (e.g. \"localization\": \"locales\").");
        var folder = ProjectValidator.Resolve(dir, project.Ui.Localization);
        var file = Path.Combine(folder, lang + ".json");
        if (File.Exists(file) && !a.Flag("force")) throw new UsageException($"{file} exists (use --force to overwrite).");

        static string Pick(Dictionary<string, string> text, string lang) =>
            text.TryGetValue(lang, out var t) ? t : text.TryGetValue("en-US", out var en) ? en : text.Values.FirstOrDefault() ?? "";
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["options"] = new System.Text.Json.Nodes.JsonObject(project.Options.Select(o => KeyValuePair.Create(o.Id, (System.Text.Json.Nodes.JsonNode?)Pick(o.Label, lang)))),
            ["actions"] = new System.Text.Json.Nodes.JsonObject(project.Actions.Where(x => x.Id.Length > 0).Select(x => KeyValuePair.Create(x.Id, (System.Text.Json.Nodes.JsonNode?)Pick(x.Name, lang)))),
            ["variants"] = new System.Text.Json.Nodes.JsonObject(project.Files.Variants.Select(v => KeyValuePair.Create(v.Id, (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            {
                ["label"] = Pick(v.Label, lang), ["description"] = Pick(v.Description, lang),
            }))),
            ["license"] = "",
        };
        var builtIn = Localization.SupportedLanguages.Contains(lang);
        var strings = new System.Text.Json.Nodes.JsonObject();
        if (!builtIn)
        {
            // The installer's own texts, in English, to be translated. Placeholders {0}, {1} and "&" mnemonics stay.
            var english = new RuntimeAssets().LocalizationFile("en-US");
            if (!File.Exists(english)) throw new InvalidDataException($"Installer strings not found: {english}");
            foreach (var p in System.Text.Json.JsonDocument.Parse(File.ReadAllText(english)).RootElement.EnumerateObject())
                strings[p.Name] = p.Value.GetString();
        }
        json["strings"] = strings;
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, json.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n", new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Created {file}");
        Console.WriteLine(builtIn
            ? $"The installer already speaks {lang}; translate the package texts (options, actions, variants, license)."
            : $"The installer has no {lang} strings: translate the {strings.Count} entries under \"strings\" too (keep {{0}} placeholders and & mnemonics). Untranslated entries show in English.");
        return 0;
    }
    private static int Validate(ParsedArgs a)
    {
        var projectPath = Path.GetFullPath(a.Positional(0, "project file"));
        var json = a.Flag("json");
        var defines = a.Defines();
        a.EnsureAllUsed();
        var dir = Path.GetDirectoryName(projectPath)!;
        var variableDiagnostics = new DiagnosticBag();
        var project = ProjectVariables.Expand(LoadProject(projectPath), defines, variableDiagnostics);
        var bag = ProjectValidator.Validate(project, dir);
        bag.AddRange(variableDiagnostics.Items);
        var files = FileCollector.Collect(project, dir, project.Files.Variants);
        bag.AddRange(files.Diagnostics.Items);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { valid = !bag.HasErrors, files = files.Files.Count, bytes = files.TotalSize, diagnostics = bag.Items }, ReportJson));
        }
        else
        {
            PrintDiagnostics(bag.Items, verbose: true);
            Console.WriteLine(bag.HasErrors ? "Project has errors." : $"Project is valid: {files.Files.Count:N0} files, {PackageBuilder.Format(files.TotalSize)}.");
        }
        return bag.HasErrors ? 1 : 0;
    }

    private static int Inspect(ParsedArgs a)
    {
        var path = Path.GetFullPath(a.Positional(0, "Setup.exe"));
        var verify = a.Flag("verify");
        var json = a.Flag("json");
        a.EnsureAllUsed();
        if (!File.Exists(path)) throw new UsageException($"File not found: {path}");
        var info = PackageInspector.Inspect(path, verify);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                path = info.Path, fileSize = info.FileSize, signed = info.Signed, overlayStart = info.OverlayStart,
                payloadFiles = info.PayloadFiles, payloadRawBytes = info.PayloadRawBytes, payloadStoredBytes = info.PayloadStoredBytes,
                frontendFiles = info.FrontendFiles, frontendStoredBytes = info.FrontendStoredBytes, streamsVerified = info.StreamsVerified,
                problems = info.Problems, header = info.Header?.RootElement,
            }, ReportJson));
        }
        else
        {
            Console.WriteLine($"{info.Path} ({PackageBuilder.Format(info.FileSize)})");
            if (info.Header is not null)
            {
                var app = info.Header.RootElement.GetProperty("app");
                Console.WriteLine($"  {app.GetProperty("name").GetString()} {app.GetProperty("version").GetString()} ({app.GetProperty("id").GetString()}, {app.GetProperty("architecture").GetString()})");
                Console.WriteLine($"  kind: {info.Header.RootElement.GetProperty("kind").GetString()}, scope: {info.Header.RootElement.GetProperty("install").GetProperty("scope").GetString()}");
            }
            Console.WriteLine($"  stub/PE part: {PackageBuilder.Format(info.OverlayStart)}");
            Console.WriteLine($"  WinUI front end: {info.FrontendFiles} files, {PackageBuilder.Format(info.FrontendStoredBytes)} stored");
            Console.WriteLine($"  payload: {info.PayloadFiles:N0} files, {PackageBuilder.Format(info.PayloadRawBytes)} -> {PackageBuilder.Format(info.PayloadStoredBytes)}");
            Console.WriteLine($"  signed: {(info.Signed ? "yes" : "no")}");
            Console.WriteLine(info.Problems.Count == 0 ? $"  OK{(verify ? " (all stream checksums verified)" : "")}" : "  PROBLEMS: " + string.Join("; ", info.Problems));
        }
        return info.Problems.Count == 0 ? 0 : 1;
    }

    private static int Init(ParsedArgs a)
    {
        var folder = Path.GetFullPath(a.PositionalOrDefault(0) ?? ".");
        var name = a.Value("name") ?? throw new UsageException("--name is required.");
        var id = a.Value("id") ?? throw new UsageException("--id is required.");
        var source = a.Value("source") ?? throw new UsageException("--source is required.");
        var publisher = a.Value("publisher") ?? "";
        a.EnsureAllUsed();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fluentsetup.json");
        if (File.Exists(path)) throw new UsageException($"{path} already exists.");
        var sourceFull = Path.GetFullPath(source);
        var exe = Directory.Exists(sourceFull) ? Directory.EnumerateFiles(sourceFull, "*.exe").Select(Path.GetFileName).FirstOrDefault() : null;
        var project = new SetupProject
        {
            App = new AppInfo { Id = id, Name = name, Publisher = publisher, MainExecutable = exe ?? "" },
            Files = new FileSettings { Source = Path.GetRelativePath(folder, sourceFull) },
        };
        ProjectSerializer.Save(project, path);
        Console.WriteLine($"Created {path}");
        return 0;
    }

    private static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics, bool verbose)
    {
        foreach (var d in diagnostics)
        {
            if (d.Severity == Severity.Info && !verbose) continue;
            var w = d.Severity == Severity.Error ? Console.Error : Console.Out;
            w.WriteLine(d.ToString());
        }
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Progress that reports synchronously on the calling thread (no SynchronizationContext in a console app).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _lock = new();
        public void Report(T value)
        {
            lock (_lock) handler(value);
        }
    }
}

internal sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal strict argument parser: "--name value", "--name=value", "-x value", flags, positionals.</summary>
internal sealed class ParsedArgs
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = [];
    private readonly List<string> _defines = [];
    private static readonly HashSet<string> Flags = ["verbose", "v", "json", "sign", "no-sign", "verify", "non-interactive", "check", "prerelease", "refresh-installers", "test-hooks"];

    public static ParsedArgs Parse(string[] args)
    {
        var p = new ParsedArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith('-') && a.Length > 1)
            {
                var name = a.TrimStart('-');
                string? value = null;
                var eq = name.IndexOf('=');
                if (name is "D" or "define")
                {
                    // Repeatable: -D Name=Value, --define Name=Value, --define=Name=Value.
                    if (eq >= 0) p._defines.Add(name[(eq + 1)..]);
                    else if (i + 1 < args.Length) p._defines.Add(args[++i]);
                    else throw new UsageException($"Missing value for {a}.");
                    continue;
                }
                if (eq >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
                else if (!Flags.Contains(name))
                {
                    if (i + 1 >= args.Length) throw new UsageException($"Missing value for {a}.");
                    value = args[++i];
                }
                if (p._options.ContainsKey(name)) throw new UsageException($"{a} given twice.");
                p._options[name] = value;
            }
            else
            {
                p._positional.Add(a);
            }
        }
        return p;
    }

    /// <summary>-D values as a table (later ones win); null if none were given.</summary>
    public Dictionary<string, string>? Defines()
    {
        if (_defines.Count == 0) return null;
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _defines)
        {
            if (!ProjectVariables.TryParseDefine(d, out var name, out var value)) throw new UsageException($"-D needs name=value with a valid name, got '{d}'.");
            table[name] = value;
        }
        return table;
    }

    public string Positional(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new UsageException($"Missing {what}.");

    public string? PositionalOrDefault(int index) => index < _positional.Count ? _positional[index] : null;

    public string? Value(string name, string? alias = null)
    {
        foreach (var n in alias is null ? [name] : new[] { name, alias })
        {
            if (_options.TryGetValue(n, out var v))
            {
                _used.Add(n);
                return v ?? throw new UsageException($"--{n} needs a value.");
            }
        }
        return null;
    }

    public bool Flag(string name, string? alias = null)
    {
        foreach (var n in alias is null ? [name] : new[] { name, alias })
        {
            if (_options.TryGetValue(n, out var v))
            {
                _used.Add(n);
                if (v is not null) throw new UsageException($"--{n} does not take a value.");
                return true;
            }
        }
        return false;
    }

    public void EnsureAllUsed()
    {
        var unknown = _options.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new UsageException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
    }
}
