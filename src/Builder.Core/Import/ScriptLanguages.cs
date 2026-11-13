namespace FluentSetup.Builder.Core.Import;

/// <summary>Language names of Inno Setup (.isl files) and NSIS (MUI_LANGUAGE, LANG_*) as BCP-47 tags.</summary>
internal static class ScriptLanguages
{
    private static readonly Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en-US", ["default"] = "en-US", ["en"] = "en-US",
        ["chinesesimplified"] = "zh-CN", ["simpchinese"] = "zh-CN", ["chinese"] = "zh-CN",
        ["chinesetraditional"] = "zh-TW", ["tradchinese"] = "zh-TW",
        ["japanese"] = "ja-JP", ["korean"] = "ko-KR",
        ["german"] = "de-DE", ["french"] = "fr-FR", ["spanish"] = "es-ES", ["italian"] = "it-IT",
        ["brazilianportuguese"] = "pt-BR", ["portuguesebr"] = "pt-BR", ["portuguese"] = "pt-PT",
        ["russian"] = "ru-RU", ["ukrainian"] = "uk-UA", ["polish"] = "pl-PL", ["dutch"] = "nl-NL",
        ["czech"] = "cs-CZ", ["slovak"] = "sk-SK", ["slovenian"] = "sl-SI", ["hungarian"] = "hu-HU",
        ["turkish"] = "tr-TR", ["greek"] = "el-GR", ["hebrew"] = "he-IL", ["arabic"] = "ar-SA",
        ["danish"] = "da-DK", ["finnish"] = "fi-FI", ["norwegian"] = "nb-NO", ["swedish"] = "sv-SE",
        ["catalan"] = "ca-ES", ["corsican"] = "co-FR", ["armenian"] = "hy-AM", ["bulgarian"] = "bg-BG",
        ["icelandic"] = "is-IS", ["romanian"] = "ro-RO", ["serbianlatin"] = "sr-Latn-RS", ["serbiancyrillic"] = "sr-Cyrl-RS",
        ["croatian"] = "hr-HR", ["lithuanian"] = "lt-LT", ["latvian"] = "lv-LV", ["estonian"] = "et-EE",
        ["thai"] = "th-TH", ["vietnamese"] = "vi-VN", ["indonesian"] = "id-ID", ["malay"] = "ms-MY",
        ["persian"] = "fa-IR", ["farsi"] = "fa-IR", ["hindi"] = "hi-IN", ["tamil"] = "ta-IN",
    };

    /// <summary>The tag for a language name ("ChineseSimplified", "LANG_SIMPCHINESE", "compiler:Languages\German.isl"), or "".</summary>
    public static string Tag(string name)
    {
        var n = name.Trim().Trim('"');
        var slash = n.LastIndexOfAny(['\\', '/', ':']);
        if (slash >= 0) n = n[(slash + 1)..];
        if (n.EndsWith(".isl", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (n.StartsWith("LANG_", StringComparison.OrdinalIgnoreCase)) n = n[5..];
        if (n.Equals("Default", StringComparison.OrdinalIgnoreCase)) return "en-US";
        return Tags.TryGetValue(n, out var tag) ? tag : "";
    }
}
