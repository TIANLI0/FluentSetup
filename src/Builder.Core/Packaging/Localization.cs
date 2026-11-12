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
    /// <summary>Languages the installer user interface ships with (shared/localization/*.json). A package can add any
    /// other language by giving its localization file a full "strings" table (see <see cref="Coverage"/>).</summary>
    public static readonly string[] SupportedLanguages = ["en-US", "zh-CN", "ja-JP", "ko-KR"];

    /// <summary>BCP-47 tag such as fr-FR, de, pt-BR or zh-Hant-TW.</summary>
    public static bool IsLanguageTag(string tag) =>
        System.Text.RegularExpressions.Regex.IsMatch(tag, "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8}){0,3}$");

    /// <summary>Keys of the installer's own strings (from the runtime's en-US table); empty if the runtime is not found.</summary>
    public static IReadOnlyCollection<string> InstallerStringKeys(string? runtimeRoot = null)
    {
        var file = new RuntimeAssets(runtimeRoot).LocalizationFile("en-US");
        if (!File.Exists(file)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    /// <summary>For a language the installer does not ship: how many installer strings the package translates.</summary>
    public static (int Translated, int Total, List<string> Missing) Coverage(IReadOnlyDictionary<string, string> strings, IReadOnlyCollection<string> keys)
    {
        var missing = keys.Where(k => !strings.ContainsKey(k)).ToList();
        return (keys.Count - missing.Count, keys.Count, missing);
    }

    public sealed class LocaleFile
    {
        public Dictionary<string, string> Options { get; set; } = [];
        public Dictionary<string, string> Actions { get; set; } = [];
        public Dictionary<string, VariantText> Variants { get; set; } = [];
        public string License { get; set; } = "";
        public Dictionary<string, string> Strings { get; set; } = [];
    }

    public sealed class VariantText
    {
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>Package text after merging the localization folder into the inline texts.</summary>
    public sealed record Texts(
        Dictionary<string, Dictionary<string, string>> OptionLabels,   // option id -> language -> text
        Dictionary<int, Dictionary<string, string>> ActionNames,       // action index -> language -> text
        Dictionary<string, string> LicenseFiles,                       // language -> absolute file path
        Dictionary<string, Dictionary<string, string>> Strings,        // language -> key -> text
        Dictionary<string, Dictionary<string, string>> VariantLabels,  // variant id -> language -> text
        Dictionary<string, Dictionary<string, string>> VariantDescriptions);

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
        var variantLabels = p.Files.Variants.ToDictionary(v => v.Id, v => new Dictionary<string, string>(v.Label), StringComparer.Ordinal);
        var variantDescriptions = p.Files.Variants.ToDictionary(v => v.Id, v => new Dictionary<string, string>(v.Description), StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(p.Ui.Localization)) return new(options, actions, licenses, strings, variantLabels, variantDescriptions);

        var folder = ProjectValidator.Resolve(projectDirectory, p.Ui.Localization);
        if (!Directory.Exists(folder))
        {
            d?.Error("FS1320", "ui.localization", $"Localization folder not found: {folder}");
            return new(options, actions, licenses, strings, variantLabels, variantDescriptions);
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var lang = Path.GetFileNameWithoutExtension(file);
            var field = $"ui.localization/{Path.GetFileName(file)}";
            if (!IsLanguageTag(lang))
            {
                d?.Warning("FS1321", field, $"'{lang}' is not a language tag (use e.g. fr-FR); the file is ignored.");
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
            foreach (var (id, text) in locale.Variants)
            {
                if (!variantLabels.ContainsKey(id))
                {
                    d?.Warning("FS1326", field, $"No variant '{id}' in files.variants.");
                    continue;
                }
                if (!string.IsNullOrEmpty(text.Label)) variantLabels[id][lang] = text.Label;
                if (!string.IsNullOrEmpty(text.Description)) variantDescriptions[id][lang] = text.Description;
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
        // Languages the installer does not ship need their own strings; untranslated keys show in English.
        var keys = d is null ? [] : InstallerStringKeys();
        foreach (var (lang, table) in strings)
        {
            if (SupportedLanguages.Contains(lang) || keys.Count == 0) continue;
            var (done, total, missing) = Coverage(table, keys);
            if (missing.Count > 0)
                d!.Warning("FS1327", $"ui.localization/{lang}.json", $"{done} of {total} installer strings translated; missing ones are shown in English (e.g. {string.Join(", ", missing.Take(5))}). fluentsetup locale add {lang} lists them all.");
        }
        foreach (var lang in options.Values.SelectMany(t => t.Keys).Concat(licenses.Keys).Distinct())
        {
            if (!SupportedLanguages.Contains(lang) && !strings.ContainsKey(lang))
                d?.Warning("FS1328", "ui.localization", $"Texts for ''{lang}'' exist, but the installer has no strings for it, so it is never shown in {lang}. Add a \"strings\" table to {lang}.json.");
        }
        return new(options, actions, licenses, strings, variantLabels, variantDescriptions);
    }
}
