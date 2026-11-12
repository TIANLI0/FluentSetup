using System.Text.Json;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Package text per language, kept out of fluentsetup.json: <c>ui.localization</c> names a folder with one
/// <c>&lt;language&gt;.json</c> per language, for example <c>locales/ja-JP.json</c>:
/// <code>
/// {
///   "options": { "autostart": "サインイン時に THRM を起動する" },
///   "actions": { "pawnio": "PawnIO ドライバーのインストール" },
///   "license": "license_ja-JP.txt",
///   "strings": { "welcome.running": "…" }
/// }
/// </code>
/// Texts given inline in the project (<c>options[].label</c>, <c>actions[].name</c>, <c>ui.licenses</c>, <c>ui.strings</c>)
/// still work; a localization file wins for its language.
/// </summary>
public static class Localization
{
    /// <summary>Languages the installer user interface ships with (shared/localization/*.json).</summary>
    public static readonly string[] SupportedLanguages = ["en-US", "zh-CN", "ja-JP", "ko-KR"];

    public sealed class LocaleFile
    {
        public Dictionary<string, string> Options { get; set; } = [];
        public Dictionary<string, string> Actions { get; set; } = [];
        public string License { get; set; } = "";
        public Dictionary<string, string> Strings { get; set; } = [];
    }

    /// <summary>Package text after merging the localization folder into the inline texts.</summary>
    public sealed record Texts(
        Dictionary<string, Dictionary<string, string>> OptionLabels,   // option id -> language -> text
        Dictionary<int, Dictionary<string, string>> ActionNames,       // action index -> language -> text
        Dictionary<string, string> LicenseFiles,                       // language -> absolute file path
        Dictionary<string, Dictionary<string, string>> Strings);       // language -> key -> text

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Texts Load(SetupProject p, string projectDirectory, DiagnosticBag? d = null)
    {
        var options = p.Options.ToDictionary(o => o.Id, o => new Dictionary<string, string>(o.Label), StringComparer.Ordinal);
        var actions = p.Actions.Select((a, i) => (a, i)).ToDictionary(x => x.i, x => new Dictionary<string, string>(x.a.Name));
        var licenses = p.Ui.Licenses.ToDictionary(kv => kv.Key, kv => ProjectValidator.Resolve(projectDirectory, kv.Value));
        var strings = p.Ui.Strings.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        if (string.IsNullOrWhiteSpace(p.Ui.Localization)) return new(options, actions, licenses, strings);

        var folder = ProjectValidator.Resolve(projectDirectory, p.Ui.Localization);
        if (!Directory.Exists(folder))
        {
            d?.Error("FS1320", "ui.localization", $"Localization folder not found: {folder}");
            return new(options, actions, licenses, strings);
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var lang = Path.GetFileNameWithoutExtension(file);
            var field = $"ui.localization/{Path.GetFileName(file)}";
            if (!SupportedLanguages.Contains(lang, StringComparer.Ordinal))
            {
                d?.Warning("FS1321", field, $"'{lang}' is ignored: the installer supports {string.Join(", ", SupportedLanguages)}.");
                continue;
            }
            LocaleFile? locale;
            try
            {
                locale = JsonSerializer.Deserialize<LocaleFile>(File.ReadAllText(file), Json);
            }
            catch (JsonException ex)
            {
                d?.Error("FS1322", field, $"Invalid JSON: {ex.Message}");
                continue;
            }
            if (locale is null) continue;
            foreach (var (id, text) in locale.Options)
            {
                if (options.TryGetValue(id, out var labels)) labels[lang] = text;
                else d?.Warning("FS1323", field, $"No option '{id}' in the project.");
            }
            foreach (var (id, text) in locale.Actions)
            {
                var index = p.Actions.FindIndex(a => a.Id == id);
                if (index >= 0) actions[index][lang] = text;
                else d?.Warning("FS1324", field, $"No action with id '{id}' in the project.");
            }
            if (!string.IsNullOrEmpty(locale.License))
            {
                var path = Path.GetFullPath(Path.Combine(folder, locale.License));
                if (!File.Exists(path)) d?.Error("FS1325", field + ".license", $"License file not found: {path}");
                else if (new FileInfo(path).Length > 1024 * 1024) d?.Error("FS1306", field + ".license", "License file is larger than 1 MB.");
                else licenses[lang] = path;
            }
            if (locale.Strings.Count > 0)
            {
                if (!strings.TryGetValue(lang, out var table)) strings[lang] = table = [];
                foreach (var (k, v) in locale.Strings) table[k] = v;
            }
        }
        return new(options, actions, licenses, strings);
    }
}
