using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Import;

/// <summary>Reads an Inno Setup script (.iss) into the import context.</summary>
internal sealed partial class InnoSetupImporter(ImportContext ctx)
{
    private sealed record Entry(SourceRef At, Dictionary<string, string> Params)
    {
        public string Get(string name) => Params.TryGetValue(name, out var v) ? v : "";
        public bool Has(string name) => Params.ContainsKey(name);
        public HashSet<string> Flags => new(Get("Flags").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, (string Value, SourceRef At)> _setup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _messages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _taskIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _componentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, string Tag, SourceRef At)> _languages = [];
    private readonly List<ScriptLine> _code = [];
    private string _group = "";

    private static readonly string[] EntrySections = ["Types", "Components", "Tasks", "Dirs", "Files", "Icons", "INI", "InstallDelete", "Languages", "Registry", "Run", "UninstallDelete", "UninstallRun"];
    private static readonly string[] KeyValueSections = ["Setup", "CustomMessages", "Messages", "LangOptions"];

    /// <summary>[Setup] directives with no FluentSetup equivalent that change nothing an installed app depends on.</summary>
    private static readonly HashSet<string> CosmeticDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "WizardStyle", "WizardImageFile", "WizardSmallImageFile", "WizardImageStretch", "WizardImageAlphaFormat", "WizardImageBackColor", "WizardSmallImageBackColor",
        "WizardResizable", "WizardSizePercent", "WindowVisible", "WindowShowCaption", "WindowStartMaximized", "WindowResizable", "BackColor", "BackColor2",
        "BackColorDirection", "BackSolid", "FlatComponentsList", "ShowComponentSizes", "ShowTasksTreeLines", "DisableWelcomePage", "DisableReadyPage",
        "DisableReadyMemo", "DisableFinishedPage", "DisableStartupPrompt", "AlwaysShowComponentsList", "AlwaysShowDirOnReadyPage", "AlwaysShowGroupOnReadyPage",
        "ShowLanguageDialog", "LanguageDetectionMethod", "AppVerName", "VersionInfoVersion",
        "VersionInfoProductVersion", "VersionInfoProductTextVersion", "VersionInfoTextVersion", "VersionInfoCompany", "VersionInfoCopyright", "VersionInfoDescription",
        "VersionInfoProductName", "VersionInfoOriginalFileName", "OutputManifestFile", "SolidCompression", "LZMAUseSeparateProcess", "LZMANumBlockThreads",
        "LZMADictionarySize", "LZMANumFastBytes", "LZMAAlgorithm", "LZMAMatchFinder", "LZMABlockSize", "InternalCompressLevel", "CompressionThreads",
        "DiskSpanning", "SlicesPerDisk", "DiskSliceSize", "DiskClusterSize", "ReserveBytes", "UseSetupLdr", "TimeStampsInUTC", "TimeStampRounding",
        "TouchDate", "TouchTime", "SetupLogging", "UninstallDisplayIcon", "UninstallDisplaySize", "UninstallLogMode", "UninstallFilesDir", "UninstallRestartComputer",
        "UpdateUninstallLogAppName", "UninstallLogging", "DirExistsWarning", "AppendDefaultDirName", "AppendDefaultGroupName", "UsePreviousAppDir", "UsePreviousGroup",
        "UsePreviousLanguage", "UsePreviousSetupType", "UsePreviousTasks", "UsePreviousUserInfo", "UsePreviousPrivileges", "UsedUserAreasWarning", "AllowNoIcons",
        "AllowUNCPath", "AllowRootDirectory", "AllowCancelDuringInstall", "AllowNetworkDrive", "DisableProgramGroupPage", "EnableDirDoesntExistWarning",
        "ExtraDiskSpaceRequired", "MergeDuplicateFiles", "Output", "SourceDir", "ASLRCompatible", "DEPCompatible", "TerminalServicesAware", "SetupMutex",
        "MissingMessagesWarning", "NotRecognizedMessagesWarning", "UninstallDisplayName", "AppModifyPath", "ChangesAssociations", "ChangesEnvironment",
        "ShowUndisplayableLanguages", "UserInfoPage", "DefaultUserInfoName", "DefaultUserInfoOrg", "DefaultUserInfoSerial",
        "SetupArchitecture", "AppIdAsGuid", "RedirectionGuard", "UninstallStyle", "WizardImageOpacity", "WizardBackImageFile",
    };

    /// <summary>Details Inno Setup writes into the uninstall entry; Settings > Apps shows FluentSetup's app.* instead.</summary>
    private static readonly string[] UninstallEntryDirectives = ["AppSupportURL", "AppUpdatesURL", "AppContact", "AppSupportPhone", "AppReadmeFile", "AppCopyright"];

    [GeneratedRegex(@"^\[([^\]]+)\]\s*(;.*)?$")]
    private static partial Regex SectionRegex();

    public void Run()
    {
        var lines = new InnoPreprocessor(ctx).Run();
        Split(lines);
        ReadSetup();
        ReadLanguages();
        ApplyIdentity();
        ApplyScopeAndFolders();
        ApplyOutputAndUi();
        ReadTypesAndComponents();
        ReadTasks();
        FindMainExecutable();
        ReadFiles();
        ReadDirs();
        ReadIcons();
        ReadRegistry();
        ReadRun("Run", uninstall: false);
        ReadRun("UninstallRun", uninstall: true);
        ReadDelete();
        ReadIni();
        ReadCode();
    }

    // ---- reading -----------------------------------------------------------------------------------------------

    private void Split(List<ScriptLine> lines)
    {
        string? section = null;
        var messages = new List<string>();
        SourceRef messagesAt = default;
        foreach (var line in lines)
        {
            var t = line.Text.Trim();
            if (t.Length == 0) continue;
            var m = SectionRegex().Match(t);
            // Inside [Code] only a known section name ends the code ("[ssPostInstall];" is Pascal).
            if (m.Success && (section != "Code" || IsKnownSection(m.Groups[1].Value.Trim())))
            {
                section = m.Groups[1].Value.Trim();
                var known = EntrySections.Concat(KeyValueSections).FirstOrDefault(s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
                if (known is not null) section = known;
                else if (section.Equals("Code", StringComparison.OrdinalIgnoreCase)) section = "Code";
                else ctx.Manual(line.At, $"Section [{section}] is not known to the importer; its entries are not imported.");
                if (section == "Code" && _code.Count == 0) _code.Add(line);
                continue;
            }
            if (section is null) { if (!t.StartsWith(';')) ctx.Info(line.At, $"Line outside any section is ignored: {t}"); continue; }
            if (section == "Code") { _code.Add(line); continue; }
            if (t.StartsWith(';') || t.StartsWith("//", StringComparison.Ordinal)) continue;
            if (KeyValueSections.Contains(section))
            {
                var eq = t.IndexOf('=');
                if (eq <= 0) { ctx.Warn(line.At, $"[{section}] line without '=' is ignored: {t}"); continue; }
                var key = t[..eq].Trim();
                var value = Unquote(t[(eq + 1)..].Trim());
                if (section == "Setup") _setup[key] = (value, line.At);
                else if (section == "CustomMessages") AddMessage(key, value);
                else if (section == "Messages")
                {
                    if (messages.Count == 0) messagesAt = line.At;
                    messages.Add(key);
                }
                else ctx.Info(line.At, $"[LangOptions] {key} is not used (FluentSetup's interface follows Windows' settings).");
                continue;
            }
            if (!EntrySections.Contains(section)) continue;
            var p = ParseParams(t);
            if (p is null) { ctx.Warn(line.At, $"[{section}] entry cannot be read: {t}"); continue; }
            if (!_sections.TryGetValue(section, out var list)) _sections[section] = list = [];
            list.Add(new Entry(line.At, p));
        }
        if (messages.Count > 0)
            ctx.Manual(messagesAt, $"[Messages] changes Inno Setup's own texts ({string.Join(", ", messages.Take(12))}{(messages.Count > 12 ? ", …" : "")}); not imported. FluentSetup's texts can be changed in ui.strings (keys in shared/localization/en-US.json).", "ui.strings");
    }

    private static bool IsKnownSection(string name) =>
        name.Equals("Code", StringComparison.OrdinalIgnoreCase) || EntrySections.Concat(KeyValueSections).Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string Unquote(string v) => v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : v;

    /// <summary>Parses "Name: value; Name: "quoted ""value"""; …".</summary>
    private static Dictionary<string, string>? ParseParams(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            var colon = line.IndexOf(':', i);
            if (colon < 0) return result.Count > 0 ? result : null;
            var name = line[i..colon].Trim();
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;
            i = colon + 1;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            var sb = new StringBuilder();
            if (i < line.Length && line[i] == '"')
            {
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(line[i++]);
                }
                while (i < line.Length && line[i] != ';') i++;
            }
            else
            {
                while (i < line.Length && line[i] != ';') sb.Append(line[i++]);
            }
            result[name] = sb.ToString().Trim();
            if (i < line.Length && line[i] == ';') i++;
        }
        return result;
    }

    private void AddMessage(string key, string value)
    {
        var dot = key.IndexOf('.');
        var lang = dot > 0 ? key[..dot] : "";
        var name = dot > 0 ? key[(dot + 1)..] : key;
        if (!_messages.TryGetValue(name, out var byLang)) _messages[name] = byLang = new(StringComparer.OrdinalIgnoreCase);
        byLang[lang] = value;
    }

    private string? Setup(string name) => _setup.TryGetValue(name, out var v) ? v.Value : null;
    private SourceRef SetupAt(string name) => _setup.TryGetValue(name, out var v) ? v.At : ctx.Script;

    private void ReadSetup()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AppId", "AppName", "AppVersion", "AppPublisher", "AppPublisherURL", "AppComments", "AppMutex", "CloseApplications", "CloseApplicationsFilter",
            "RestartApplications", "DefaultDirName", "DefaultGroupName", "DisableDirPage", "PrivilegesRequired", "PrivilegesRequiredOverridesAllowed",
            "OutputBaseFilename", "OutputDir", "SetupIconFile", "LicenseFile", "InfoBeforeFile", "InfoAfterFile", "ArchitecturesAllowed",
            "ArchitecturesInstallIn64BitMode", "MinVersion", "OnlyBelowVersion", "Compression", "SignTool", "SignedUninstaller", "SignToolRunMinimized",
            "SignToolRetryCount", "SignToolRetryDelay", "SignToolMinimumTimeBetween", "Password", "Encryption", "RestartIfNeededByRun", "AlwaysRestart",
            "Uninstallable", "CreateUninstallRegKey", "CreateAppDir", "AlwaysUsePersonalGroup",
        };
        var cosmetic = new List<string>();
        var entry = new List<string>();
        var websiteFrom = new[] { "AppPublisherURL", "AppSupportURL", "AppUpdatesURL" }.FirstOrDefault(k => Setup(k) is { Length: > 0 });
        foreach (var (key, (_, at)) in _setup)
        {
            if (known.Contains(key) || key.Equals(websiteFrom, StringComparison.OrdinalIgnoreCase)) continue;
            if (CosmeticDirectives.Contains(key)) cosmetic.Add($"{key} (line {at.Line})");
            else if (UninstallEntryDirectives.Contains(key, StringComparer.OrdinalIgnoreCase)) entry.Add($"{key} (line {at.Line})");
            else ctx.Info(at, $"[Setup] {key} is not imported (no FluentSetup equivalent).");
        }
        if (entry.Count > 0)
            ctx.Info(_setup.Values.First().At, $"[Setup] {string.Join(", ", entry)}: Settings > Apps shows app.publisher and app.website from the project; these details are not imported.");
        if (cosmetic.Count > 0)
            ctx.Info(_setup.Values.First().At, $"[Setup] directives about the wizard's look, compression details or Inno Setup's own bookkeeping are not needed: {string.Join(", ", cosmetic)}.");

        foreach (var key in new[] { "SignTool", "SignedUninstaller" })
            if (Setup(key) is not null) ctx.Manual(SetupAt(key), $"{key}: set up code signing in the project's signing section (certificate thumbprint or .pfx; signing.signUninstaller signs uninstall.exe too).", "signing");
        foreach (var key in new[] { "Password", "Encryption" })
            if (Setup(key) is { } v && !v.Equals("no", StringComparison.OrdinalIgnoreCase)) ctx.Manual(SetupAt(key), $"{key}: password-protected or encrypted installers are not supported.");
        foreach (var key in new[] { "RestartIfNeededByRun", "AlwaysRestart" })
            if (Setup(key) is { } v && v.Equals("yes", StringComparison.OrdinalIgnoreCase)) ctx.Warn(SetupAt(key), $"{key}=yes: FluentSetup asks for a restart only when a file in use is replaced at the next restart (install.lockedFiles: reboot).");
        if (Setup("Uninstallable") is { } u && u.Equals("no", StringComparison.OrdinalIgnoreCase)) ctx.Manual(SetupAt("Uninstallable"), "Uninstallable=no: FluentSetup apps can always be uninstalled.");
        if (Setup("CreateUninstallRegKey") is { } c && c.Equals("no", StringComparison.OrdinalIgnoreCase)) ctx.Manual(SetupAt("CreateUninstallRegKey"), "CreateUninstallRegKey=no: FluentSetup always registers the app in Settings > Apps.");
        if (Setup("CreateAppDir") is { } d && d.Equals("no", StringComparison.OrdinalIgnoreCase)) ctx.Manual(SetupAt("CreateAppDir"), "CreateAppDir=no: FluentSetup always installs into an app folder.");
        foreach (var key in new[] { "InfoBeforeFile", "InfoAfterFile" })
            if (Setup(key) is not null) ctx.Warn(SetupAt(key), $"{key}: information pages are not supported; put the text into ui.welcomeText or ui.finishText.", "ui.welcomeText");
        if (Setup("OnlyBelowVersion") is not null) ctx.Info(SetupAt("OnlyBelowVersion"), "OnlyBelowVersion is not imported (FluentSetup has a minimum Windows build only).");
        if (Setup("AlwaysUsePersonalGroup") is not null) ctx.Info(SetupAt("AlwaysUsePersonalGroup"), "AlwaysUsePersonalGroup: shortcuts follow the install scope (per-user or all users).");
    }

    private void ReadLanguages()
    {
        foreach (var e in Entries("Languages"))
        {
            var name = e.Get("Name");
            var tag = ScriptLanguages.Tag(e.Get("MessagesFile").Length > 0 ? e.Get("MessagesFile") : name);
            if (tag.Length == 0) tag = ScriptLanguages.Tag(name);
            _languages.Add((name, tag, e.At));
            if (e.Get("LicenseFile") is { Length: > 0 } lic && tag.Length > 0)
            {
                var path = ctx.ProjectPathOf(lic);
                ctx.Project.Ui.Licenses[tag] = path;
                ctx.Info(e.At, $"License of language {name} → ui.licenses[\"{tag}\"]", "ui.licenses");
                LicenseFormat(lic, e.At);
            }
            foreach (var key in new[] { "InfoBeforeFile", "InfoAfterFile" })
                if (e.Has(key)) ctx.Warn(e.At, $"{key} of language {name}: information pages are not supported.");
        }
        if (_languages.Count == 0) return;
        var builtIn = _languages.Where(l => Localization.SupportedLanguages.Contains(l.Tag)).Select(l => $"{l.Name} ({l.Tag})").ToList();
        var other = _languages.Where(l => !Localization.SupportedLanguages.Contains(l.Tag)).ToList();
        ctx.Info(_languages[0].At, $"Languages: the installer shows Windows' display language when it has it{(builtIn.Count > 0 ? ": built in " + string.Join(", ", builtIn) : "")}.", "ui.defaultLanguage");
        foreach (var l in other)
        {
            ctx.Manual(l.At, l.Tag.Length > 0
                ? $"Language {l.Name}: the installer's texts are built in for en-US, zh-CN, ja-JP and ko-KR. Add {l.Tag} with 'fluentsetup locale add {l.Tag} fluentsetup.json' (set ui.localization first)."
                : $"Language {l.Name} is not recognised; add it with 'fluentsetup locale add <tag> fluentsetup.json'.", "ui.localization");
        }
        if (_languages.Count == 1 && _languages[0].Tag is { Length: > 0 } only && only != "en-US" && Localization.SupportedLanguages.Contains(only))
        {
            ctx.Project.Ui.DefaultLanguage = only;
            ctx.Info(_languages[0].At, $"The script has only {_languages[0].Name} → ui.defaultLanguage \"{only}\"", "ui.defaultLanguage");
        }
    }

    private List<Entry> Entries(string section) => _sections.TryGetValue(section, out var list) ? list : [];

    // ---- constants and custom messages ------------------------------------------------------------------------

    private string DefaultLanguageKey => _languages.Count > 0 ? _languages[0].Name : "english";

    /// <summary>Text of a custom message in a language ("" = the default language), with %1… filled in.</summary>
    private string? Message(string name, string language, IReadOnlyList<string> args)
    {
        string? text = null;
        if (_messages.TryGetValue(name, out var byLang))
        {
            if (language.Length > 0 && byLang.TryGetValue(language, out var t)) text = t;
            else if (byLang.TryGetValue("", out var any)) text = any;
            else if (byLang.TryGetValue(DefaultLanguageKey, out var d)) text = d;
            else if (language.Length == 0) text = byLang.Values.FirstOrDefault();
        }
        var tag = language.Length > 0 ? _languages.FirstOrDefault(l => l.Name.Equals(language, StringComparison.OrdinalIgnoreCase)).Tag ?? "" : "en-US";
        text ??= BuiltInMessage(name, tag.Length > 0 ? tag : "en-US");
        if (text is null) return null;
        for (var i = args.Count; i >= 1; i--) text = text.Replace("%" + i.ToString(CultureInfo.InvariantCulture), args[i - 1], StringComparison.Ordinal);
        return text.Replace("%n", "\n", StringComparison.Ordinal);
    }

    private static string? BuiltInMessage(string name, string tag) => (name.ToLowerInvariant(), tag) switch
    {
        ("createdesktopicon", "zh-CN") => "创建桌面快捷方式(&D)",
        ("createdesktopicon", _) => "Create a &desktop shortcut",
        ("createquicklaunchicon", _) => "Create a &Quick Launch shortcut",
        ("additionalicons", "zh-CN") => "附加快捷方式:",
        ("additionalicons", _) => "Additional shortcuts:",
        ("launchprogram", "zh-CN") => "运行 %1",
        ("launchprogram", _) => "Launch %1",
        ("programontheweb", _) => "%1 on the Web",
        ("uninstallprogram", "zh-CN") => "卸载 %1",
        ("uninstallprogram", _) => "Uninstall %1",
        ("assocfileextension", "zh-CN") => "将 %2 文件扩展名与 %1 建立关联(&A)",
        ("assocfileextension", _) => "&Associate %1 with the %2 file extension",
        ("autostartprogram", "zh-CN") => "自动启动 %1",
        ("autostartprogram", _) => "Automatically start %1",
        ("autostartprogramgroupdescription", _) => "Startup:",
        ("nameandversion", _) => "%1 version %2",
        _ => null,
    };

    [GeneratedRegex(@"\s*\(&[A-Za-z0-9]\)")]
    private static partial Regex ParenAcceleratorRegex();

    /// <summary>A label per language for text with {cm:…} (option labels); plain text gives { "en-US": text }.</summary>
    private Dictionary<string, string> Labels(string raw)
    {
        static string Clean(string s) => ImportContext.StripAccelerators(ParenAcceleratorRegex().Replace(s, "")).Trim();
        var result = new Dictionary<string, string>();
        var english = Clean(Constants(raw, "english"));
        if (Canonical.IsUnsupported(english)) english = Clean(Constants(raw, ""));
        if (english.Length > 0 && !Canonical.IsUnsupported(english)) result["en-US"] = english;
        if (!raw.Contains("{cm:", StringComparison.OrdinalIgnoreCase)) return result;
        foreach (var (name, tag, _) in _languages)
        {
            if (tag.Length == 0 || result.ContainsKey(tag)) continue;
            var text = Clean(Constants(raw, name));
            // A language without its own text would only repeat the English one.
            if (text.Length > 0 && !Canonical.IsUnsupported(text) && text != english) result[tag] = text;
        }
        return result;
    }

    private string Constants(string raw) => Constants(raw, "");

    /// <summary>Inno constants as canonical tokens (see <see cref="ImportContext"/>); {cm:…} resolved; "{{" is a literal "{".</summary>
    private string Constants(string raw, string language)
    {
        if (!raw.Contains('{')) return raw;
        var sb = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '{') { sb.Append(c); continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '{') { sb.Append('{'); i++; continue; }
            var depth = 0;
            var end = -1;
            for (var j = i; j < raw.Length; j++)
            {
                if (raw[j] == '{') depth++;
                else if (raw[j] == '}' && --depth == 0) { end = j; break; }
            }
            if (end < 0) { sb.Append(raw[i..]); break; }
            sb.Append(Constant(raw[(i + 1)..end], language));
            i = end;
        }
        return sb.ToString();
    }

    private string Constant(string inner, string language)
    {
        var scope = ctx.Project.Install.Scope;
        var lower = inner.ToLowerInvariant();
        if (lower.StartsWith("cm:", StringComparison.Ordinal))
        {
            var parts = SplitArgs(inner[3..]);
            var text = Message(parts[0].Trim(), language, parts.Skip(1).Select(a => Constants(a.Trim(), language)).ToList());
            return text ?? "{?" + inner + "}";
        }
        if (lower.StartsWith('?')) return "{" + inner + "}";
        return lower switch
        {
            "app" => "{InstallDir}",
            "group" => _group.Length > 0 ? @"{Programs}\" + _group : "{Programs}",
            "autoprograms" or "commonprograms" or "userprograms" => "{Programs}",
            "autodesktop" or "commondesktop" or "userdesktop" or "desktop" => "{Desktop}",
            "autostartup" or "commonstartup" or "userstartup" or "startup" => "{Startup}",
            "tmp" => "{Temp}",
            "src" => "{SourceDir}",
            "sys" or "syswow64" or "sysnative" or "sysx86" or "sysx64" => "{System}",
            "win" => "{Windows}",
            "localappdata" => "{LocalAppData}",
            "userappdata" or "appdata" => "{AppData}",
            "commonappdata" or "allusersappdata" => "{CommonAppData}",
            "autoappdata" => scope == "user" ? "{AppData}" : scope == "machine" ? "{CommonAppData}" : "{?autoappdata}",
            "autopf" => scope == "user" ? @"{LocalAppData}\Programs" : scope == "machine" ? "{ProgramFiles}" : "{?autopf}",
            "pf" or "commonpf" or "pf64" or "commonpf64" or "pf32" or "commonpf32" => "{ProgramFiles}",
            "userpf" => @"{LocalAppData}\Programs",
            "uninstallexe" => "{Uninstaller}",
            "olddata" => "{OldData}",
            "break" => "\n",
            "cmd" => @"{System}\cmd.exe",
            "\\" => "\\",
            _ => "{?" + inner.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal) + "}",
        };
    }

    private static List<string> SplitArgs(string s)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == ',' && depth == 0) { result.Add(s[start..i]); start = i + 1; }
        }
        result.Add(s[start..]);
        return result;
    }

    // ---- [Setup] ----------------------------------------------------------------------------------------------

    private void ApplyIdentity()
    {
        var p = ctx.Project;
        if (Setup("AppName") is { } name) { p.App.Name = Constants(name); ctx.Info(SetupAt("AppName"), "AppName → app.name", "app.name"); }
        else ctx.Manual(ctx.Script, "The script has no AppName; set app.name.", "app.name");
        if (Setup("AppVersion") is { } version) ImportHelpers.SetVersion(ctx, version, SetupAt("AppVersion"), "AppVersion");
        else ctx.Manual(ctx.Script, "The script has no AppVersion; app.version is 1.0.0.", "app.version");
        if (Setup("AppPublisher") is { } pub) { p.App.Publisher = Constants(pub); ctx.Info(SetupAt("AppPublisher"), "AppPublisher → app.publisher", "app.publisher"); }
        var url = new[] { "AppPublisherURL", "AppSupportURL", "AppUpdatesURL" }.FirstOrDefault(k => Setup(k) is { Length: > 0 });
        if (url is not null) ImportHelpers.SetWebsite(ctx, Setup(url)!, SetupAt(url), url);
        if (Setup("AppComments") is { } comments) { p.App.Description = Constants(comments); ctx.Info(SetupAt("AppComments"), "AppComments → app.description", "app.description"); }

        // Inno's uninstall key is "<AppId>_is1" (AppId defaults to AppName); "{{" in AppId is a literal brace.
        var appIdRaw = Setup("AppId") ?? Setup("AppName") ?? "";
        var appId = ctx.Resolve(appIdRaw).Replace("{{", "{", StringComparison.Ordinal).Trim();
        var at = Setup("AppId") is not null ? SetupAt("AppId") : SetupAt("AppName");
        if (appId.Length > 0)
        {
            var legacy = appId + "_is1";
            p.Install.LegacyUninstallKeys.Add(legacy);
            p.App.Id = PathRules.IsValidAppId(appId) && !appId.Contains('{') ? appId : ImportHelpers.DeriveAppId(ctx.Resolve(p.App.Publisher), ctx.Resolve(p.App.Name), ctx.ScriptPath);
            ctx.Info(at, $"{(Setup("AppId") is not null ? "AppId" : "AppName (Inno's default AppId)")}: existing installations are adopted through their uninstall key \"{legacy}\" → install.legacyUninstallKeys; app.id is \"{p.App.Id}\"", "install.legacyUninstallKeys");
            p.Install.Cleanup.RemoveFiles.AddRange(["unins???.exe", "unins???.dat", "unins???.msg"]);
            ctx.Info(at, "Inno Setup's uninstaller files (unins000.exe/.dat/.msg) are removed from adopted installations → install.cleanup.removeFiles", "install.cleanup.removeFiles");
        }
        else p.App.Id = ImportHelpers.DeriveAppId(ctx.Resolve(p.App.Publisher), ctx.Resolve(p.App.Name), ctx.ScriptPath);

        if (Setup("AppMutex") is { Length: > 0 }) ctx.Info(SetupAt("AppMutex"), "AppMutex: FluentSetup closes the running app instead of waiting for it (install.closeProcesses with app.mainExecutable, Restart Manager).", "install.closeProcesses");
        var close = Setup("CloseApplications")?.ToLowerInvariant();
        if (close is "no")
        {
            p.Install.RestartManager = "off";
            ctx.Info(SetupAt("CloseApplications"), "CloseApplications=no → install.restartManager \"off\"", "install.restartManager");
        }
        else ctx.Info(SetupAt("CloseApplications"), "CloseApplications: programs that use the app's files are closed and restarted afterwards (install.restartManager \"auto\").", "install.restartManager");
        if (Setup("CloseApplicationsFilter") is not null) ctx.Info(SetupAt("CloseApplicationsFilter"), "CloseApplicationsFilter: Restart Manager covers the app's .exe and .dll files and every file being replaced.");
        if (Setup("RestartApplications") is { } ra && ra.Equals("no", StringComparison.OrdinalIgnoreCase)) ctx.Info(SetupAt("RestartApplications"), "RestartApplications=no: programs that registered for restart are restarted after the update.");
    }

    private void ApplyScopeAndFolders()
    {
        var p = ctx.Project;
        var privileges = (Setup("PrivilegesRequired") ?? "admin").Trim().ToLowerInvariant();
        var overrides = (Setup("PrivilegesRequiredOverridesAllowed") ?? "").ToLowerInvariant();
        var canOverride = overrides.Contains("dialog", StringComparison.Ordinal) || overrides.Contains("commandline", StringComparison.Ordinal);
        var lowest = privileges is "lowest" or "none";
        p.Install.Scope = canOverride ? "userOrMachine" : lowest ? "user" : "machine";
        p.Install.DefaultScope = lowest ? "user" : "machine";
        var at = Setup("PrivilegesRequired") is not null ? SetupAt("PrivilegesRequired") : ctx.Script;
        ctx.Info(at, $"PrivilegesRequired={(Setup("PrivilegesRequired") is null ? "admin (Inno's default)" : privileges)}{(canOverride ? $", PrivilegesRequiredOverridesAllowed={overrides}" : "")} → install.scope \"{p.Install.Scope}\"" +
                     (canOverride ? $", defaultScope \"{p.Install.DefaultScope}\"" : ""), "install.scope");
        if (overrides.Contains("commandline", StringComparison.Ordinal) && !overrides.Contains("dialog", StringComparison.Ordinal))
            ctx.Info(SetupAt("PrivilegesRequiredOverridesAllowed"), "With scope userOrMachine the installer also shows the choice (Inno: command line only); /CURRENTUSER and /ALLUSERS still work.", "install.scope");

        if (Setup("DefaultGroupName") is { Length: > 0 } group) _group = Constants(group);
        if (Setup("DisableDirPage") is { } ddp && ddp.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            p.Install.AllowCustomDir = false;
            ctx.Info(SetupAt("DisableDirPage"), "DisableDirPage=yes → install.allowCustomDir false (note: /DIR= is then refused too)", "install.allowCustomDir");
        }

        var dir = Setup("DefaultDirName");
        if (dir is null) { ctx.Manual(ctx.Script, "The script has no DefaultDirName; the install folders are FluentSetup's defaults.", "install.userDir"); return; }
        var dirAt = SetupAt("DefaultDirName");
        var m = Regex.Match(dir.Trim(), @"^\{([^{}]+)\}(.*)$");
        if (!m.Success)
        {
            var path = ctx.Resolve(Constants(dir)).Trim();
            if (path.Length > 2 && path[1] == ':' && !Canonical.IsUnsupported(path))
            {
                p.Install.MachineDir = p.Install.UserDir = path;
                ctx.Warn(dirAt, $"DefaultDirName \"{path}\" is a fixed folder → install.userDir and machineDir.", "install.userDir");
            }
            else ctx.Manual(dirAt, $"DefaultDirName \"{Canonical.Display(Constants(dir))}\" cannot be converted; the install folders are FluentSetup's defaults.", "install.userDir");
            return;
        }
        var constant = m.Groups[1].Value.ToLowerInvariant();
        var constantAsWritten = m.Groups[1].Value;
        var rest = DirRest(m.Groups[2].Value, dirAt);
        if (rest is null) return;
        string? user = null, machine = null;
        switch (constant)
        {
            case "autopf": user = @"{LocalAppData}\Programs" + rest; machine = "{ProgramFiles}" + rest; break;
            case "pf" or "commonpf" or "pf64" or "commonpf64" or "pf32" or "commonpf32":
                machine = "{ProgramFiles}" + rest; user = @"{LocalAppData}\Programs" + rest;
                if (constant.EndsWith("32", StringComparison.Ordinal)) ctx.Warn(dirAt, $"{{{constant}}} is Program Files (x86); FluentSetup installs into the native Program Files.", "install.machineDir");
                if (p.Install.Scope == "user") ctx.Warn(dirAt, "A per-user install cannot write to Program Files; install.userDir uses %LocalAppData%\\Programs.", "install.userDir");
                break;
            case "userpf": user = @"{LocalAppData}\Programs" + rest; machine = "{ProgramFiles}" + rest; break;
            case "localappdata": user = "{LocalAppData}" + rest; break;
            case "userappdata": user = "{AppData}" + rest; break;
            case "commonappdata": machine = "{ProgramData}" + rest; break;
            case "sd": machine = user = "C:" + rest; ctx.Warn(dirAt, "{sd} (the system drive) is written as C:.", "install.machineDir"); break;
            default:
                ctx.Manual(dirAt, $"DefaultDirName starts with {{{constantAsWritten}}}, which FluentSetup cannot use; the install folders are its defaults.", "install.userDir");
                return;
        }
        if (user is not null) p.Install.UserDir = user;
        if (machine is not null) p.Install.MachineDir = machine;
        if (p.Install.Scope != "user" && machine is null)
            ctx.Warn(dirAt, $"DefaultDirName is in the user's profile; a per-machine install goes to {p.Install.MachineDir} instead.", "install.machineDir");
        if (p.Install.Scope != "machine" && user is null)
            ctx.Warn(dirAt, $"DefaultDirName is not in the user's profile; a per-user install goes to {p.Install.UserDir} instead.", "install.userDir");
        ctx.Info(dirAt, $"DefaultDirName → install.userDir \"{p.Install.UserDir}\", install.machineDir \"{p.Install.MachineDir}\"", "install.userDir");
    }

    /// <summary>The part of DefaultDirName after its folder constant, with the app's name and publisher as {AppName} and {Publisher}.</summary>
    private string? DirRest(string rest, SourceRef at)
    {
        var converted = Constants(rest);
        if (Canonical.IsUnsupported(converted) || ctx.HasUnknownVariable(converted))
        {
            ctx.Manual(at, $"DefaultDirName contains {(Canonical.FirstUnsupported(converted) is { Length: > 0 } u ? u : "a value computed by the script")}; the install folders are FluentSetup's defaults.", "install.userDir");
            return null;
        }
        var appName = ctx.Resolve(ctx.Project.App.Name);
        var publisher = ctx.Resolve(ctx.Project.App.Publisher);
        var segments = ctx.Resolve(converted).Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(s =>
            s.Equals(appName, StringComparison.Ordinal) && appName.Length > 0 ? "{AppName}" :
            s.Equals(publisher, StringComparison.Ordinal) && publisher.Length > 0 ? "{Publisher}" :
            s.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal));
        var joined = string.Join('\\', segments);
        return joined.Length == 0 ? "" : "\\" + joined;
    }

    private void ApplyOutputAndUi()
    {
        var p = ctx.Project;
        if (Setup("OutputBaseFilename") is { Length: > 0 } baseName)
        {
            p.Output.FileName = Constants(baseName) + ".exe";
            ctx.Info(SetupAt("OutputBaseFilename"), "OutputBaseFilename → output.fileName (in-app updaters that download a fixed name keep working)", "output.fileName");
        }
        if (Setup("OutputDir") is { Length: > 0 } outDir && !Canonical.IsUnsupported(Constants(outDir)))
        {
            p.Output.Directory = ctx.ProjectPathOf(outDir);
            ctx.Info(SetupAt("OutputDir"), "OutputDir → output.directory", "output.directory");
        }
        else if (Setup("OutputDir") is null) p.Output.Directory = ctx.ProjectPath(ctx.ScriptFile("Output"));
        if (Setup("SetupIconFile") is { Length: > 0 } icon)
        {
            p.App.Icon = ctx.ProjectPathOf(icon);
            ctx.Info(SetupAt("SetupIconFile"), "SetupIconFile → app.icon", "app.icon");
        }
        if (Setup("LicenseFile") is { Length: > 0 } license)
        {
            p.Ui.License = ctx.ProjectPathOf(license);
            ctx.Info(SetupAt("LicenseFile"), "LicenseFile → ui.license (the user accepts it before installing)", "ui.license");
            LicenseFormat(license, SetupAt("LicenseFile"));
        }
        else if (p.Ui.Licenses.Count > 0)
        {
            var first = p.Ui.Licenses.TryGetValue("en-US", out var en) ? en : p.Ui.Licenses.Values.First();
            p.Ui.License = first;
        }

        // Architecture
        var allowed = (Setup("ArchitecturesAllowed") ?? "").ToLowerInvariant();
        var in64 = (Setup("ArchitecturesInstallIn64BitMode") ?? "").ToLowerInvariant();
        var tokens = Regex.Split(allowed + " " + in64, @"[^a-z0-9]+").Where(t => t.Length > 0).ToHashSet();
        var x64 = tokens.Overlaps(["x64", "x64os", "x64compatible", "win64"]);
        var arm = tokens.Contains("arm64");
        var archAt = Setup("ArchitecturesInstallIn64BitMode") is not null ? SetupAt("ArchitecturesInstallIn64BitMode") : SetupAt("ArchitecturesAllowed");
        p.Output.Architecture = !x64 && arm ? "arm64" : "x64";
        if (in64.Length == 0)
            ctx.Warn(archAt, "The script builds a 32-bit installation (no ArchitecturesInstallIn64BitMode); FluentSetup packages are x64 or arm64 and install into the native Program Files. A 32-bit app runs from there unchanged.", "output.architecture");
        else ctx.Info(archAt, $"ArchitecturesInstallIn64BitMode → output.architecture \"{p.Output.Architecture}\"", "output.architecture");
        if (x64 && arm) ctx.Info(archAt, "For Arm64 PCs build a second package: fluentsetup build fluentsetup.json --arch arm64.", "output.architecture");

        if (Setup("MinVersion") is { Length: > 0 } minVersion)
        {
            var v = minVersion.Contains(',') ? minVersion[(minVersion.IndexOf(',') + 1)..] : minVersion;
            var parts = Regex.Matches(v, @"\d+").Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture)).ToList();
            var build = parts.Count >= 3 && parts[0] >= 10 ? parts[2] : 0;
            p.Install.MinWindowsBuild = Math.Max(build, 17763);
            ctx.Info(SetupAt("MinVersion"), $"MinVersion {minVersion} → install.minWindowsBuild {p.Install.MinWindowsBuild}" + (build < 17763 ? " (FluentSetup itself needs Windows 10 1809, build 17763)" : ""), "install.minWindowsBuild");
        }
        if (Setup("Compression") is { Length: > 0 } compression)
        {
            var c = compression.ToLowerInvariant();
            p.Output.Compression = c.Contains("ultra", StringComparison.Ordinal) || c.Contains("max", StringComparison.Ordinal) ? "max" : c.Contains("fast", StringComparison.Ordinal) || c is "zip" or "none" || c.StartsWith("zip/", StringComparison.Ordinal) ? "fast" : "normal";
            ctx.Info(SetupAt("Compression"), $"Compression={compression} → output.compression \"{p.Output.Compression}\" (Zstandard)", "output.compression");
        }
    }

    private void LicenseFormat(string license, SourceRef at)
    {
        if (ctx.Resolve(license).EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
            ctx.Manual(at, $"{ctx.Resolve(license)} is RTF; FluentSetup shows plain text. Save it as .txt and update ui.license.", "ui.license");
    }

    // ---- tasks and components ---------------------------------------------------------------------------------

    private void ReadTypesAndComponents()
    {
        var types = Entries("Types");
        var firstType = types.FirstOrDefault(t => !t.Flags.Contains("iscustom"))?.Get("Name") ?? "";
        if (types.Count > 0) ctx.Info(types[0].At, $"[Types] (setup types) are not used: FluentSetup shows each component as an option{(firstType.Length > 0 ? $"; their defaults follow the type \"{firstType}\"" : "")}.", "options");
        foreach (var e in Entries("Components"))
        {
            var name = e.Get("Name");
            var id = ctx.NewOptionId(name);
            _componentIds[name] = id;
            var inTypes = e.Get("Types").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var flags = e.Flags;
            ctx.Options.Add(new OptionCandidate
            {
                Id = id, At = e.At, Origin = $"component \"{name}\"", Label = Labels(e.Get("Description")),
                Required = flags.Contains("fixed"), Default = firstType.Length == 0 || inTypes.Contains(firstType, StringComparer.OrdinalIgnoreCase),
            });
            if (flags.Contains("exclusive")) ctx.Warn(e.At, $"Component \"{name}\" is exclusive (radio button); FluentSetup options are independent checkboxes.", "options");
            if (e.Has("Check")) ctx.Warn(e.At, $"Component \"{name}\" has Check: {e.Get("Check")}; FluentSetup always offers it.", "options");
        }
    }

    private void ReadTasks()
    {
        foreach (var e in Entries("Tasks"))
        {
            var name = e.Get("Name");
            var flags = e.Flags;
            var id = ctx.NewOptionId(name);
            _taskIds[name] = id;
            ctx.Options.Add(new OptionCandidate
            {
                Id = id, At = e.At, Origin = $"task \"{name}\"", Label = Labels(e.Get("Description")),
                Default = !flags.Contains("unchecked"),
            });
            if (flags.Contains("exclusive")) ctx.Warn(e.At, $"Task \"{name}\" is exclusive (radio button); FluentSetup options are independent checkboxes.", "options");
            if (flags.Contains("checkedonce")) ctx.Info(e.At, $"Task \"{name}\" (checkedonce): on updates FluentSetup keeps the choice made at install time.", "options");
            if (flags.Contains("restart")) ctx.Warn(e.At, $"Task \"{name}\" asks for a restart; FluentSetup does not.", "options");
            if (e.Has("Check")) ctx.Warn(e.At, $"Task \"{name}\" has Check: {e.Get("Check")}; FluentSetup always offers it.", "options");
            if (e.Has("Components")) ctx.Info(e.At, $"Task \"{name}\" depends on components ({e.Get("Components")}); FluentSetup shows it always.", "options");
        }
    }

    /// <summary>The option candidate for an entry's Tasks/Components parameters ("" = always). Complex expressions are reported.</summary>
    private string OptionOf(Entry e, string what, bool gatesFiles = false)
    {
        var id = "";
        foreach (var (param, ids, kind) in new[] { ("Tasks", _taskIds, "task"), ("Components", _componentIds, "component") })
        {
            if (!e.Has(param)) continue;
            var expr = e.Get(param).Trim();
            var names = Regex.Split(expr, @"[\s()]+").Where(n => n.Length > 0 && !n.Equals("or", StringComparison.OrdinalIgnoreCase) && !n.Equals("and", StringComparison.OrdinalIgnoreCase)).ToList();
            var single = names.Count == 1 && !names[0].StartsWith('!') && !names[0].Equals("not", StringComparison.OrdinalIgnoreCase);
            var name = single ? names[0].TrimStart('*') : "";
            if (!single || !ids.TryGetValue(name, out var found))
            {
                if (!(single && gatesFiles)) ctx.Warn(e.At, $"{what} depends on {param}: {expr}; only a single {kind} can be expressed, so it is imported without that condition.");
                continue;
            }
            if (gatesFiles)
            {
                ctx.FindOption(found)?.Unexpressed.Add("files");
                ctx.Warn(e.At, $"{what} depends on {kind} \"{name}\"; files cannot depend on an option, so it is always installed.", "files");
                continue;
            }
            if (id.Length > 0) ctx.Warn(e.At, $"{what} depends on both a task and a component; only the task is kept.");
            else id = found;
        }
        return id;
    }

    private void Conditions(Entry e, string what)
    {
        if (e.Has("Check")) ctx.Warn(e.At, $"{what} has Check: {e.Get("Check")} ([Code]); it is imported without that condition.");
        if (e.Has("Languages")) ctx.Warn(e.At, $"{what} is only for languages {e.Get("Languages")}; it is imported for all languages.");
        if (e.Has("BeforeInstall") || e.Has("AfterInstall")) ctx.Manual(e.At, $"{what} calls [Code] ({string.Join(", ", new[] { e.Get("BeforeInstall"), e.Get("AfterInstall") }.Where(s => s.Length > 0))}); that code is not imported.");
        if (e.Has("MinVersion") || e.Has("OnlyBelowVersion")) ctx.Info(e.At, $"{what} depends on the Windows version; it is imported for all versions.");
    }

    // ---- main executable --------------------------------------------------------------------------------------

    private void FindMainExecutable()
    {
        string? From(string canonical) => ctx.TryInstallRelative(canonical, out var rel) && rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rel : null;
        var icons = Entries("Icons");
        var appName = ctx.Resolve(ctx.Project.App.Name);
        (string? Exe, SourceRef At, string How) found = (null, ctx.Script, "");
        foreach (var e in icons)
        {
            var name = Constants(e.Get("Name"));
            if (!name.StartsWith("{Programs}", StringComparison.OrdinalIgnoreCase) || e.Get("Parameters").Length > 0) continue;
            if (From(Constants(e.Get("Filename"))) is not { } exe) continue;
            if (found.Exe is null || ctx.Resolve(name).EndsWith("\\" + appName, StringComparison.OrdinalIgnoreCase)) found = (exe, e.At, "the Start menu shortcut");
            if (ctx.Resolve(name).EndsWith("\\" + appName, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (found.Exe is null)
            foreach (var e in Entries("Run").Where(e => e.Flags.Contains("postinstall")))
                if (From(Constants(e.Get("Filename"))) is { } exe) { found = (exe, e.At, "the program [Run] offers to launch"); break; }
        if (found.Exe is null && ctx.Variables.FirstOrDefault(v => v.Key.Contains("ExeName", StringComparison.OrdinalIgnoreCase)).Value is { } exeName &&
            exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && PathRules.TryNormalizeRelative(exeName, out var n, out _))
            found = (n.Replace('\\', '/'), ctx.Script, "the #define of the program's name");
        if (found.Exe is null && Setup("UninstallDisplayIcon") is { } udi && From(Constants(udi).Split(',')[0]) is { } fromIcon)
            found = (fromIcon, SetupAt("UninstallDisplayIcon"), "UninstallDisplayIcon");
        if (found.Exe is null)
            foreach (var e in icons)
                if (From(Constants(e.Get("Filename"))) is { } exe) { found = (exe, e.At, "a shortcut"); break; }
        if (found.Exe is null)
        {
            var exes = Entries("Files").Where(e => Constants(e.Get("DestDir")).Equals("{InstallDir}", StringComparison.OrdinalIgnoreCase) && !e.Has("DestName") && ctx.Resolve(e.Get("Source")).EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (exes.Count == 1) found = (Path.GetFileName(ctx.Resolve(exes[0].Get("Source")).Replace('\\', '/').Split('/')[^1]), exes[0].At, "the only program in [Files]");
        }
        if (found.Exe is null)
        {
            ctx.Manual(ctx.Script, "The app's main program could not be determined; set app.mainExecutable (shortcuts and \"launch\" need it).", "app.mainExecutable");
            return;
        }
        ctx.MainExecutable = found.Exe;
        ctx.Project.App.MainExecutable = found.Exe;
        ctx.Info(found.At, $"app.mainExecutable \"{found.Exe}\" (from {found.How})", "app.mainExecutable");
    }

    // ---- [Files], [Dirs] ---------------------------------------------------------------------------------------

    private readonly HashSet<string> _tempFiles = new(StringComparer.OrdinalIgnoreCase);

    private void ReadFiles()
    {
        foreach (var e in Entries("Files"))
        {
            var sourceRaw = e.Get("Source");
            var what = $"[Files] {ctx.Resolve(sourceRaw)}";
            var flags = e.Flags;
            if (flags.Contains("dontcopy")) { ctx.Info(e.At, $"{what} is only extracted by [Code] (dontcopy); not installed."); continue; }
            if (flags.Contains("external")) { ctx.Manual(e.At, $"{what} is copied from outside the installer at install time (external); FluentSetup installs only files in the package.", "files"); continue; }
            if (ctx.HasUnknownVariable(sourceRaw) || Canonical.IsUnsupported(Constants(sourceRaw)))
            {
                ctx.Manual(e.At, $"{what}: the source path uses a value computed by the script; add these files to the project by hand.", "files");
                continue;
            }
            var dest = Constants(e.Get("DestDir"));
            var source = ctx.ScriptFile(ctx.Resolve(sourceRaw));
            if (dest.StartsWith("{Temp}", StringComparison.OrdinalIgnoreCase))
            {
                _tempFiles.Add(Path.GetFileName(source));
                ctx.Info(e.At, $"{what} → {{tmp}}: a temporary file for [Run] (see that entry).");
                continue;
            }
            if (!ctx.TryInstallRelative(dest, out var destDir))
            {
                ctx.Manual(e.At, $"{what} goes to {Canonical.Display(dest)}: FluentSetup installs files into the install folder only. Let the app copy it on first run, or ship it in the install folder.", "files");
                continue;
            }
            Conditions(e, what);
            OptionOf(e, what, gatesFiles: true);
            var preserve = flags.Contains("onlyifdoesntexist") || flags.Contains("uninsneveruninstall") || flags.Contains("confirmoverwrite");
            if (preserve) ctx.Info(e.At, $"{what}: {string.Join("/", flags.Where(f => f is "onlyifdoesntexist" or "uninsneveruninstall" or "confirmoverwrite"))} → files.preserve (installed only if missing, never overwritten, kept on uninstall)", "files.preserve");
            if (flags.Contains("regserver") || flags.Contains("regtypelib")) ctx.Manual(e.At, $"{what} is registered as a COM server/type library; FluentSetup does not register COM objects.", "files");
            if (flags.Contains("sharedfile")) ctx.Warn(e.At, $"{what} is a shared file (reference counted); FluentSetup removes it with the app.", "files");
            if (flags.Contains("restartreplace"))
            {
                if (ctx.Project.Install.Scope == "machine")
                {
                    ctx.Project.Install.LockedFiles = "reboot";
                    ctx.Info(e.At, $"{what} (restartreplace) → install.lockedFiles \"reboot\"", "install.lockedFiles");
                }
                else ctx.Warn(e.At, $"{what} (restartreplace): replacing files at the next restart needs a per-machine install (install.lockedFiles: reboot).", "install.lockedFiles");
            }
            if (flags.Contains("isreadme")) ctx.Info(e.At, $"{what} is the readme; FluentSetup has no \"view readme\" choice.");
            if (flags.Contains("deleteafterinstall")) ctx.Warn(e.At, $"{what} is deleted after installing (deleteafterinstall); FluentSetup keeps it.", "files");
            if (flags.Contains("gacinstall") || e.Has("StrongAssemblyName")) ctx.Manual(e.At, $"{what} goes into the .NET global assembly cache; not supported.", "files");
            if (e.Has("FontInstall") || flags.Contains("fontisnttruetype")) ctx.Manual(e.At, $"{what} installs a font; not supported (fonts can be installed per user by the app).", "files");
            if (e.Has("Permissions")) ctx.Warn(e.At, $"{what} sets permissions ({e.Get("Permissions")}); FluentSetup keeps the folder's permissions.", "files");
            if (flags.Contains("sign") || flags.Contains("signonce") || flags.Contains("signcheck")) ctx.Info(e.At, $"{what} is signed by Inno Setup; sign the files in your build instead.", "signing");
            ctx.Files.Add(new FileEntry
            {
                At = e.At, Source = source, DestDir = destDir, Recurse = flags.Contains("recursesubdirs"),
                DestName = e.Get("DestName").Length > 0 ? ctx.Resolve(Constants(e.Get("DestName"))) : "",
                Preserve = preserve,
                Excludes = [.. e.Get("Excludes").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ctx.Resolve)],
                Variables = [.. ImportContext.ReferencedVariables(sourceRaw)],
            });
        }
    }

    private void ReadDirs()
    {
        foreach (var e in Entries("Dirs"))
        {
            var name = Constants(e.Get("Name"));
            var what = $"[Dirs] {Canonical.Display(ctx.Resolve(name))}";
            Conditions(e, what);
            if (ctx.TryInstallRelative(name, out var rel))
            {
                if (rel.Length > 0) ctx.EmptyDirectories.Add((e.At, rel));
                if (e.Has("Permissions")) ctx.Warn(e.At, $"{what} gets permissions ({e.Get("Permissions")}): FluentSetup does not change folder permissions. Keep data the app writes in %LocalAppData% or %ProgramData% instead.", "files.emptyDirectories");
                if (e.Flags.Contains("uninsneveruninstall")) ctx.Info(e.At, $"{what} (uninsneveruninstall): FluentSetup removes it only while it is empty.");
                continue;
            }
            if (Canonical.AsDataTemplate(ctx.Resolve(name)) is not null)
            {
                ctx.Info(e.At, $"{what}: the app creates its data folders itself; not needed.");
                if (e.Has("Permissions")) ctx.Manual(e.At, $"{what} gets permissions ({e.Get("Permissions")}); FluentSetup does not change folder permissions.");
                continue;
            }
            ctx.Manual(e.At, $"{what} is outside the install folder; FluentSetup creates folders inside it only.", "files.emptyDirectories");
        }
    }

    // ---- [Icons] -----------------------------------------------------------------------------------------------

    private void ReadIcons()
    {
        foreach (var e in Entries("Icons"))
        {
            var name = Constants(e.Get("Name"));
            var what = $"[Icons] {Canonical.Display(ctx.Resolve(name))}";
            Conditions(e, what);
            var flags = e.Flags;
            string location, folder = "", unsupported = "";
            var slash = name.LastIndexOf('\\');
            var dir = slash >= 0 ? name[..slash] : "";
            var leaf = slash >= 0 ? name[(slash + 1)..] : name;
            if (dir.StartsWith("{Programs}", StringComparison.OrdinalIgnoreCase)) { location = "startMenu"; folder = dir["{Programs}".Length..].Trim('\\'); }
            else if (dir.Equals("{Desktop}", StringComparison.OrdinalIgnoreCase)) location = "desktop";
            else if (dir.Equals("{Startup}", StringComparison.OrdinalIgnoreCase)) location = "startup";
            else if (dir.Contains("Quick Launch", StringComparison.OrdinalIgnoreCase)) location = "quickLaunch";
            else { location = "other"; unsupported = dir; }
            if (flags.Contains("foldershortcut")) { ctx.Manual(e.At, $"{what} is a folder shortcut; not supported.", "shortcuts"); continue; }
            if (flags.Contains("createonlyiffileexists")) ctx.Info(e.At, $"{what} (createonlyiffileexists): the target is always one of the app's files.");
            if (flags.Contains("runminimized") || flags.Contains("runmaximized")) ctx.Info(e.At, $"{what}: the window state ({string.Join(", ", flags.Where(f => f.StartsWith("run", StringComparison.Ordinal)))}) is not set.");
            if (flags.Contains("excludefromshowinnewinstall") || flags.Contains("preventpinning")) ctx.Info(e.At, $"{what}: {string.Join(", ", flags.Where(f => f is "excludefromshowinnewinstall" or "preventpinning"))} is not set.");
            if (e.Has("HotKey")) ctx.Info(e.At, $"{what}: hot key {e.Get("HotKey")} is not set.");
            var ifOption = OptionOf(e, what);
            ctx.Shortcuts.Add(new ShortcutEntry
            {
                At = e.At, Location = location, Folder = folder, Name = leaf, Unsupported = unsupported,
                Target = Constants(e.Get("Filename")), Arguments = Constants(e.Get("Parameters")),
                WorkingDirectory = Constants(e.Get("WorkingDir")), Icon = Constants(e.Get("IconFilename")),
                IconIndex = int.TryParse(ctx.Resolve(e.Get("IconIndex")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : 0,
                Description = Constants(e.Get("Comment")), IfOption = ifOption,
                AppUserModelId = Constants(e.Get("AppUserModelID")), ToastActivatorClsid = Constants(e.Get("AppUserModelToastActivatorCLSID")),
            });
        }
    }

    // ---- [Registry] --------------------------------------------------------------------------------------------

    private void ReadRegistry()
    {
        foreach (var e in Entries("Registry"))
        {
            var rootRaw = e.Get("Root").Trim().ToUpperInvariant();
            var subkey = Constants(e.Get("Subkey")).Trim('\\');
            var what = $"[Registry] {rootRaw}\\{Canonical.Display(ctx.Resolve(subkey))}";
            var flags = e.Flags;
            if (rootRaw.EndsWith("32", StringComparison.Ordinal)) ctx.Warn(e.At, $"{what}: the 32-bit registry view is not used; FluentSetup writes the native view.", "registry");
            var root = rootRaw.TrimEnd('3', '2', '6', '4');
            if (root == "HKCR") { root = "HKA"; subkey = @"Software\Classes\" + subkey; }
            if (root is not ("HKA" or "HKCU" or "HKLM"))
            {
                ctx.Manual(e.At, $"{what}: only HKA, HKCU, HKLM and HKCR are supported.", "registry");
                continue;
            }
            if (Canonical.IsUnsupported(subkey) || ctx.HasUnknownVariable(subkey))
            {
                ctx.Manual(e.At, $"{what}: the key uses {Canonical.FirstUnsupported(subkey)}, which the importer cannot fill in.", "registry");
                continue;
            }
            if (flags.Contains("deletekey") || flags.Contains("deletevalue"))
            {
                ctx.Manual(e.At, $"{what}: the script deletes this {(flags.Contains("deletekey") ? "key" : "value")} before installing; FluentSetup does not delete registry data during installation.", "registry");
                if (e.Get("ValueType").Equals("none", StringComparison.OrdinalIgnoreCase) || e.Get("ValueType").Length == 0) continue;
            }
            Conditions(e, what);
            if (e.Has("Permissions")) ctx.Warn(e.At, $"{what} sets permissions ({e.Get("Permissions")}); not supported.", "registry");
            if (flags.Contains("createvalueifdoesntexist")) ctx.Warn(e.At, $"{what}: written on every install (createvalueifdoesntexist is not supported).", "registry");
            var valueType = e.Get("ValueType").ToLowerInvariant();
            var type = valueType switch { "string" => "string", "expandsz" => "expandString", "multisz" => "multiString", "dword" => "dword", "qword" => "qword", "binary" => "binary", _ => "none" };
            var uninstall = flags.Contains("uninsdeletekey") ? RegistryUninstall.Key : flags.Contains("uninsdeletevalue") || flags.Contains("uninsclearvalue") ? RegistryUninstall.Values :
                            flags.Contains("uninsdeletekeyifempty") ? RegistryUninstall.KeyIfEmpty : RegistryUninstall.Keep;
            ctx.Registry.Add(new RegistryWrite
            {
                At = e.At, Root = root, Key = subkey,
                Name = type == "none" ? null : Constants(e.Get("ValueName")),
                Type = type, Data = Constants(e.Get("ValueData")), Uninstall = uninstall,
                IfOption = OptionOf(e, what), Conditional = e.Has("Check"),
            });
        }
    }

    // ---- [Run], [UninstallRun] --------------------------------------------------------------------------------

    private void ReadRun(string section, bool uninstall)
    {
        var p = ctx.Project;
        foreach (var e in Entries(section))
        {
            var file = Constants(e.Get("Filename"));
            var parameters = Constants(e.Get("Parameters"));
            var what = $"[{section}] {Canonical.Display(ctx.Resolve(file))}";
            var flags = e.Flags;
            var finishChoice = false;
            if (!uninstall && flags.Contains("postinstall"))
            {
                var inApp = ctx.TryInstallRelative(file, out var rel) && rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (inApp && !flags.Contains("nowait") && !flags.Contains("shellexec") && ctx.MainExecutable.Length > 0 && !ImportContext.SamePath(rel, ctx.MainExecutable))
                {
                    // A finish-page checkbox that runs another program of the app and waits ("Enable now"): an option plus an action.
                    finishChoice = true;
                }
                else if (inApp && ctx.MainExecutable.Length > 0 && ImportContext.SamePath(rel, ctx.MainExecutable))
                {
                    p.Install.LaunchAfterInstall = flags.Contains("unchecked") ? "optionalOff" : "optionalOn";
                    ctx.Info(e.At, $"{what} (postinstall) → install.launchAfterInstall \"{p.Install.LaunchAfterInstall}\" (never in quiet installs)", "install.launchAfterInstall");
                    if (parameters.Trim().Length > 0) ctx.Warn(e.At, $"{what}: the app is launched without the arguments \"{Canonical.Display(parameters)}\".", "install.launchAfterInstall");
                    if (e.Has("Tasks") || e.Has("Check")) ctx.Warn(e.At, $"{what}: the launch choice has no condition in FluentSetup.", "install.launchAfterInstall");
                }
                else ctx.Manual(e.At, $"{what}: the finish page offered to open it; FluentSetup offers to launch app.mainExecutable only.", "install.launchAfterInstall");
                if (!finishChoice) continue;
            }
            var args = ScriptText.SplitCommandLine(parameters);
            if (ImportHelpers.TryPrerequisite(ctx, file, e.At)) continue;
            if (ImportHelpers.TrySystemCommand(ctx, file, args, uninstall, e.At)) continue;
            if (!ctx.TryInstallRelative(file, out var program) || program.Length == 0)
            {
                ctx.Manual(e.At, $"{what}: FluentSetup runs only .exe files among the app's installed files" +
                                 (file.StartsWith("{Temp}", StringComparison.OrdinalIgnoreCase) ? " (ship it in the install folder with files.extra, then add an action)" : "") + ". Not imported.", "actions");
                continue;
            }
            if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Manual(e.At, $"{what}: only .exe files can run (no scripts, documents or shell verbs). Not imported.", "actions");
                continue;
            }
            var arguments = new List<string>();
            var ok = true;
            foreach (var a in args)
            {
                if (Canonical.AsValue(a) is { } v) arguments.Add(v);
                else { ok = false; break; }
            }
            if (!ok) { ctx.Manual(e.At, $"{what}: the arguments \"{Canonical.Display(parameters)}\" use folders or constants FluentSetup cannot fill in. Not imported.", "actions"); continue; }
            var isMain = ctx.MainExecutable.Length > 0 && ImportContext.SamePath(program, ctx.MainExecutable);
            if (ImportHelpers.TryAppLaunch(ctx, isMain, uninstall, flags.Contains("nowait"), arguments, what, e.At)) continue;
            if (p.Actions.Count >= 32) { ctx.Manual(e.At, $"{what} is not imported: at most 32 actions.", "actions"); continue; }
            Conditions(e, what);
            var status = ctx.Resolve(Constants(e.Get("StatusMsg").Length > 0 ? e.Get("StatusMsg") : e.Get("Description")));
            var ifOption = uninstall ? "" : OptionOf(e, what);
            if (finishChoice)
            {
                var label = Labels(e.Get("Description"));
                if (label.Count == 0) label["en-US"] = "Run " + Path.GetFileNameWithoutExtension(program);
                var choice = new OptionCandidate
                {
                    Id = ctx.NewOptionId(Path.GetFileNameWithoutExtension(program)), At = e.At, Origin = $"the finish-page choice \"{label.Values.First()}\"",
                    Label = label, Default = !flags.Contains("unchecked"), QuietDefault = flags.Contains("skipifsilent") ? false : null,
                };
                ctx.Options.Add(choice);
                if (ifOption.Length > 0) ctx.Warn(e.At, $"{what}: the choice also depended on {ctx.FindOption(ifOption)?.Origin}; it is shown always.", "options");
                ifOption = choice.Id;
            }
            var action = new ActionSettings
            {
                When = uninstall ? "beforeUninstall" : "afterInstall", Run = program, Arguments = arguments, OnFailure = "warn",
                IfOption = ctx.UseOption(ifOption),
                Name = status.Length > 0 && !Canonical.IsUnsupported(status) ? new Dictionary<string, string> { ["en-US"] = ImportContext.Literal(ImportContext.StripAccelerators(status)) } : [],
            };
            p.Actions.Add(action);
            ctx.Info(e.At, $"{what} → actions[{p.Actions.Count - 1}] ({action.When}, onFailure \"warn\": Inno Setup did not check exit codes)", "actions");
            if (flags.Contains("nowait")) ctx.Info(e.At, $"{what} (nowait): FluentSetup waits for it to finish (timeout {action.TimeoutSeconds} s).", "actions");
            if (!flags.Contains("runhidden")) ctx.Info(e.At, $"{what}: actions run without a window; programs that need one cannot be used as actions.", "actions");
            if (finishChoice) ctx.Info(e.At, $"{what} (postinstall): the finish-page checkbox is option \"{action.IfOption}\" (first page){(flags.Contains("skipifsilent") ? ", off in quiet installs (quietDefault)" : "")}; the action runs before the installation completes.", "options");
            else if (flags.Contains("skipifsilent") || flags.Contains("skipifnotsilent")) ctx.Warn(e.At, $"{what} ({string.Join(", ", flags.Where(f => f.StartsWith("skipif", StringComparison.Ordinal)))}): actions also run in quiet installs.", "actions");
            if (flags.Contains("runasoriginaluser") || flags.Contains("runascurrentuser")) ctx.Info(e.At, $"{what}: actions run with the installer's rights (elevated in per-machine installs).", "actions");
            if (uninstall && e.Has("Tasks")) ctx.Info(e.At, $"{what}: runs on every uninstall.", "actions");
        }
    }

    // ---- [UninstallDelete], [InstallDelete] -------------------------------------------------------------------

    private void ReadDelete()
    {
        var p = ctx.Project;
        foreach (var e in Entries("UninstallDelete"))
        {
            var name = ctx.Resolve(Constants(e.Get("Name")));
            var type = e.Get("Type").ToLowerInvariant();
            var what = $"[UninstallDelete] {Canonical.Display(name)}";
            Conditions(e, what);
            if (type == "dirifempty") { ctx.Info(e.At, $"{what} (dirifempty): FluentSetup removes empty folders it created."); continue; }
            var target = name;
            if (target.EndsWith("\\*", StringComparison.Ordinal) || target.EndsWith("\\*.*", StringComparison.Ordinal)) target = target[..target.LastIndexOf('\\')];
            if (ctx.TryInstallRelative(target, out var rel) && rel.Length == 0)
            {
                ctx.Warn(e.At, $"{what}: FluentSetup does not empty the whole install folder (it removes the files it installed and keeps others). List the app's own folders in uninstall.removeData ({{InstallDir}}\\logs …).", "uninstall.removeData");
                continue;
            }
            if (Canonical.AsDataTemplate(target) is not { } template)
            {
                ctx.Manual(e.At, $"{what}: FluentSetup removes {{InstallDir}}\\X, {{LocalAppData}}\\X, {{AppData}}\\X or {{CommonAppData}}\\X files and folders (no wildcards). Not imported.", "uninstall.removeData");
                continue;
            }
            // Inno Setup deleted it on every uninstall: removeData does the same (userData would ask first).
            if (!p.Uninstall.RemoveData.Contains(template, StringComparer.OrdinalIgnoreCase)) p.Uninstall.RemoveData.Add(template);
            ctx.Info(e.At, $"{what} → uninstall.removeData \"{template}\"" +
                           (template.StartsWith("{InstallDir}", StringComparison.Ordinal) ? "" : " (if these are the user's settings, uninstall.userData lets the user decide instead)"), "uninstall.removeData");
        }
        foreach (var e in Entries("InstallDelete"))
        {
            var name = ctx.Resolve(Constants(e.Get("Name")));
            var what = $"[InstallDelete] {Canonical.Display(name)}";
            Conditions(e, what);
            if (ctx.TryInstallRelativePattern(name, out var pattern) && Validation.ExtensionValidator.IsFilePattern(pattern) && e.Get("Type").Equals("files", StringComparison.OrdinalIgnoreCase))
            {
                if (!p.Install.Cleanup.RemoveFiles.Contains(pattern, StringComparer.OrdinalIgnoreCase)) p.Install.Cleanup.RemoveFiles.Add(pattern);
                ctx.Info(e.At, $"{what} → install.cleanup.removeFiles \"{pattern}\"", "install.cleanup.removeFiles");
            }
            else ctx.Manual(e.At, $"{what}: FluentSetup removes leftover files of earlier versions inside the install folder (install.cleanup.removeFiles, wildcards in the file name only); folders are not deleted. Not imported.", "install.cleanup.removeFiles");
        }
    }

    // ---- [INI] -------------------------------------------------------------------------------------------------

    private void ReadIni()
    {
        var p = ctx.Project;
        var files = new Dictionary<string, ConfigFileSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries("INI"))
        {
            var file = Constants(e.Get("Filename"));
            var resolved = ctx.Resolve(file);
            var what = $"[INI] {Canonical.Display(resolved)} [{e.Get("Section")}] {e.Get("Key")}";
            if (resolved.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) { ctx.Info(e.At, $"{what}: an internet shortcut file; FluentSetup has no web-link shortcuts (shortcuts to it are reported too)."); continue; }
            string path;
            if (ctx.TryInstallRelative(file, out var rel) && rel.Length > 0) path = rel;
            else if (Canonical.AsDataTemplate(resolved) is { } t && !t.StartsWith("{InstallDir}", StringComparison.Ordinal)) path = t;
            else { ctx.Manual(e.At, $"{what}: FluentSetup writes INI files in the install folder, {{LocalAppData}}, {{AppData}} or {{CommonAppData}} only. Not imported.", "configFiles"); continue; }
            if (!e.Has("Key")) { ctx.Manual(e.At, $"{what}: deleting INI sections is not supported. Not imported.", "configFiles"); continue; }
            if (!e.Has("String")) { ctx.Manual(e.At, $"{what}: deleting INI keys is not supported. Not imported.", "configFiles"); continue; }
            if (Canonical.AsValue(Constants(e.Get("String"))) is not { } value) { ctx.Manual(e.At, $"{what}: the value uses a folder or constant FluentSetup cannot fill in. Not imported.", "configFiles"); continue; }
            Conditions(e, what);
            var ifOption = ctx.UseOption(OptionOf(e, what));
            var key = path + "|" + ifOption;
            if (!files.TryGetValue(key, out var cfg))
            {
                if (p.ConfigFiles.Count >= 16) { ctx.Manual(e.At, $"{what} is not imported: at most 16 config files.", "configFiles"); continue; }
                cfg = new ConfigFileSettings { Path = path, Format = "ini", IfOption = ifOption };
                files[key] = cfg;
                p.ConfigFiles.Add(cfg);
            }
            if (cfg.Values.Count >= 64) { ctx.Manual(e.At, $"{what} is not imported: at most 64 values per file.", "configFiles"); continue; }
            cfg.Values.Add(new ConfigValueSettings { Section = Constants(e.Get("Section")), Key = Constants(e.Get("Key")), Value = value });
            ctx.Info(e.At, $"{what} → configFiles[{p.ConfigFiles.IndexOf(cfg)}] (ini)", "configFiles");
            var flags = e.Flags;
            if (flags.Contains("createkeyifdoesntexist")) ctx.Warn(e.At, $"{what}: written on every install (createkeyifdoesntexist is not supported).", "configFiles");
            if (flags.Any(f => f.StartsWith("uninsdelete", StringComparison.Ordinal))) ctx.Info(e.At, $"{what}: values written to config files stay after uninstall.", "configFiles");
        }
    }

    // ---- [Code] ------------------------------------------------------------------------------------------------

    private static readonly string[] EventFunctions =
    [
        "InitializeSetup", "InitializeWizard", "DeinitializeSetup", "CurStepChanged", "CurPageChanged", "CurInstallProgressChanged", "NextButtonClick",
        "BackButtonClick", "CancelButtonClick", "ShouldSkipPage", "UpdateReadyMemo", "RegisterPreviousData", "CheckPassword", "NeedRestart",
        "GetCustomSetupExitCode", "PrepareToInstall", "RegisterExtraCloseApplicationsResources", "CheckSerial", "GetUninstallProgressForm",
        "InitializeUninstall", "InitializeUninstallProgressForm", "DeinitializeUninstall", "CurUninstallStepChanged", "UninstallNeedRestart",
    ];

    [GeneratedRegex(@"^\s*(?:procedure|function)\s+([A-Za-z_][A-Za-z0-9_]*)\s*[(;:]", RegexOptions.IgnoreCase)]
    private static partial Regex RoutineRegex();

    private void ReadCode()
    {
        if (_code.Count == 0) return;
        var at = _code[0].At;
        var routines = _code.Select(l => RoutineRegex().Match(l.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var events = routines.Where(r => EventFunctions.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        ctx.Manual(at, $"[Code] ({_code.Count - 1} lines) is not converted. Event functions: {(events.Count > 0 ? string.Join(", ", events) : "none")}" +
                       $"{(routines.Count > events.Count ? $"; other routines: {string.Join(", ", routines.Except(events, StringComparer.OrdinalIgnoreCase).Take(20))}" : "")}. " +
                       "Check what each one does and use the matching project setting (hints below).", "");
        var text = string.Join('\n', _code.Select(l => l.Text));
        void Hint(string pattern, string message, string field)
        {
            var line = _code.FirstOrDefault(l => Regex.IsMatch(l.Text, pattern, RegexOptions.IgnoreCase));
            if (line is not null) ctx.Manual(line.At, "[Code] " + message, field);
        }
        Hint(@"CreateInput(Query|Option|Dir|File)Page|CreateCustomPage", "creates wizard pages: values the user enters can be inputs[] (a Settings page) used as {Input:id} in registry, config files and arguments.", "inputs");
        Hint(@"NET Framework Setup\\NDP|IsDotNetInstalled|\\NDP\\v4", "checks for .NET Framework: use requirements.netFramework.", "requirements.netFramework");
        Hint(@"dotnet|Microsoft\.(WindowsDesktop|NETCore|AspNetCore)\.App", "checks for .NET: use requirements.dotNet.", "requirements.dotNet");
        Hint(@"WebView2|F3017226-FE2A-4295-8BDF-00C3A9A7E4C5", "checks for WebView2: use requirements.webView2.", "requirements.webView2");
        Hint(@"VC\\Runtimes|vc_?redist|VisualStudio\\14\.0", "checks for the Visual C++ runtime: use requirements.vcRedist.", "requirements.vcRedist");
        Hint(@"\b(Exec|ShellExec|ExecAsOriginalUser)\s*\(", "runs programs: programs of the app itself can be actions[]; system tools are not allowed.", "actions");
        Hint(@"\b(DelTree|DeleteFile|RemoveDir)\s*\(", "deletes files or folders: use uninstall.removeData or uninstall.userData.", "uninstall.removeData");
        Hint(@"\bRegDelete(KeyIncludingSubkeys|Value|KeyIfEmpty)\s*\(", "deletes registry data: use uninstall.removeRegistryKeys or registry[].uninstall.", "uninstall.removeRegistryKeys");
        Hint(@"\b(RegWrite\w*|RegCreateKey)\s*\(", "writes registry values: use registry[].", "registry");
        Hint(@"CheckForMutexes|FindWindowByClassName|FindWindowByWindowName", "waits for the app to close: use install.closeProcesses and install.closeCommand.", "install.closeProcesses");
        Hint(@"\b(Is64BitInstallMode|ProcessorArchitecture|IsArm64|IsX64)\b", "checks the architecture: FluentSetup packages are built per architecture (output.architecture).", "output.architecture");
        Hint(@"UninstallString|QuietUninstallString|_is1", "looks for a previous installation: FluentSetup upgrades it through install.legacyUninstallKeys / adoptExistingInstall.", "install.legacyUninstallKeys");
        Hint(@"\b(MsgBox|SuppressibleMsgBox|TaskDialogMsgBox)\s*\(", "shows message boxes: FluentSetup shows no custom messages (ui.welcomeText and ui.finishText are shown on its pages).", "ui.welcomeText");
        if (text.Contains("WizardIsTaskSelected", StringComparison.OrdinalIgnoreCase) || text.Contains("IsTaskSelected", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (name, id) in _taskIds)
                if (Regex.IsMatch(text, $@"TaskSelected\s*\(\s*'{Regex.Escape(name)}'", RegexOptions.IgnoreCase)) ctx.FindOption(id)?.Unexpressed.Add("[Code]");
        }
    }
}

internal sealed partial class ImportContext
{
    /// <summary>Like <see cref="TryInstallRelative"/>, but the file name may hold * and ? (install.cleanup.removeFiles).</summary>
    public bool TryInstallRelativePattern(string canonical, out string relative)
    {
        relative = "";
        var s = Resolve(canonical).Trim().Trim('"');
        const string token = "{InstallDir}";
        if (!s.StartsWith(token + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        relative = s[(token.Length + 1)..].Replace('\\', '/');
        return relative.Length > 0 && !Canonical.IsUnsupported(relative);
    }
}
