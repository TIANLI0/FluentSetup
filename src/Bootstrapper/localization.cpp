#include "Bootstrapper/localization.h"

#include "Bootstrapper/resource.h"
#include "Common/text.h"
#include "Common/win.h"

#include <vector>

namespace fsetup::boot {

namespace {

json::Value LoadTable(int resourceId) {
    json::Value v;
    HRSRC res = ::FindResourceW(nullptr, MAKEINTRESOURCEW(resourceId), RT_RCDATA);
    if (!res) return v;
    HGLOBAL data = ::LoadResource(nullptr, res);
    const DWORD size = ::SizeofResource(nullptr, res);
    const void* bytes = data ? ::LockResource(data) : nullptr;
    if (bytes && size) json::Parse(std::string_view(static_cast<const char*>(bytes), size), v);
    return v;
}

// The user's Windows display languages in preference order (Settings > Time & language > Language).
std::vector<std::wstring> UserLanguages() {
    std::vector<std::wstring> out;
    ULONG count = 0, size = 0;
    if (::GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, &count, nullptr, &size) && size > 0) {
        std::wstring buffer(size, L'\0');
        if (::GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, &count, buffer.data(), &size)) {
            for (const wchar_t* p = buffer.c_str(); *p; p += wcslen(p) + 1) out.emplace_back(p);
        }
    }
    wchar_t name[LOCALE_NAME_MAX_LENGTH] = {};
    if (::LCIDToLocaleName(MAKELCID(::GetUserDefaultUILanguage(), SORT_DEFAULT), name, LOCALE_NAME_MAX_LENGTH, 0) > 0) {
        out.emplace_back(name);
    }
    return out;
}

// One embedded table per language (shared/localization/<tag>.json). Adding a language = a JSON file, an RCDATA line
// (localization.rc2), an id (resource.h), an entry here and in Localization.SupportedLanguages (Builder.Core);
// scripts/check-localization.ps1 checks the files and the wiring.
struct Language {
    const wchar_t* name;     // BCP-47 tag of the table; its primary language ("zh") is matched against other tags
    const wchar_t* markers;  // script or region subtags that pick this table among those of its language
    int resource;
};
// The first table of a language is its default: de-AT -> de-DE, es-MX -> es-ES, pt-PT -> pt-BR, zh-SG -> zh-CN.
constexpr Language kLanguages[] = {
    {L"en-US", L"", IDR_STRINGS_EN_US},
    {L"zh-CN", L"Hans", IDR_STRINGS_ZH_CN},
    {L"zh-TW", L"Hant TW HK MO", IDR_STRINGS_ZH_TW},
    {L"ja-JP", L"", IDR_STRINGS_JA_JP},
    {L"ko-KR", L"", IDR_STRINGS_KO_KR},
    {L"de-DE", L"", IDR_STRINGS_DE_DE},
    {L"fr-FR", L"", IDR_STRINGS_FR_FR},
    {L"es-ES", L"", IDR_STRINGS_ES_ES},
    {L"pt-BR", L"", IDR_STRINGS_PT_BR},
    {L"ru-RU", L"", IDR_STRINGS_RU_RU},
    {L"it-IT", L"", IDR_STRINGS_IT_IT},
};

std::wstring_view Primary(std::wstring_view tag) { return tag.substr(0, tag.find(L'-')); }

bool HasMarker(std::wstring_view markers, std::wstring_view subtag) {
    while (!markers.empty()) {
        const size_t space = markers.find(L' ');
        if (EqualsIgnoreCase(markers.substr(0, space), subtag)) return true;
        markers = space == std::wstring_view::npos ? std::wstring_view() : markers.substr(space + 1);
    }
    return false;
}

}  // namespace

std::wstring BuiltInLanguage(std::wstring_view tag) {
    for (const auto& l : kLanguages) {
        if (EqualsIgnoreCase(tag, l.name)) return l.name;
    }
    const std::wstring_view primary = Primary(tag);
    if (primary.empty()) return {};
    // Script before region, as in the tag itself: zh-Hant-HK and zh-MO -> zh-TW, zh-Hans-HK -> zh-CN.
    for (std::wstring_view rest = tag.substr(primary.size()); rest.size() > 1;) {
        rest.remove_prefix(1);
        const std::wstring_view subtag = rest.substr(0, rest.find(L'-'));
        for (const auto& l : kLanguages) {
            if (EqualsIgnoreCase(Primary(l.name), primary) && HasMarker(l.markers, subtag)) return l.name;
        }
        rest.remove_prefix(subtag.size());
    }
    for (const auto& l : kLanguages) {
        if (EqualsIgnoreCase(Primary(l.name), primary)) return l.name;
    }
    return {};
}

void Strings::Load(const std::wstring& requested, const std::wstring& packageDefault, const json::Value& overrides) {
    // Languages = the built-in tables plus any language the package brings its own strings for (ui.strings.<tag>),
    // so a package can add e.g. nl-NL or pl-PL without a new installer build. Order: the package's table for exactly
    // this tag, then the built-in table (exact tag, script/region, language), then a package table of the language.
    const auto resolve = [&](const std::wstring& tag) -> std::wstring {
        if (tag.empty()) return {};
        for (const auto& [k, v] : overrides.object()) {
            if (v.isObject() && EqualsIgnoreCase(Utf8ToWide(k), tag)) return Utf8ToWide(k);
        }
        if (std::wstring builtIn = BuiltInLanguage(tag); !builtIn.empty()) return builtIn;
        const std::wstring primary = tag.substr(0, tag.find(L'-'));
        for (const auto& [k, v] : overrides.object()) {
            const std::wstring key = Utf8ToWide(k);
            if (v.isObject() && EqualsIgnoreCase(key.substr(0, key.find(L'-')), primary)) return key;
        }
        return {};
    };
    std::wstring lang = resolve(requested);
    if (lang.empty() && !packageDefault.empty() && !EqualsIgnoreCase(packageDefault, L"auto")) {
        // A project can pin the installer language; otherwise follow the user's Windows display language.
        lang = resolve(packageDefault);
    }
    for (const auto& candidate : UserLanguages()) {
        if (!lang.empty()) break;
        lang = resolve(candidate);
    }
    if (lang.empty()) lang = L"en-US";
    language_ = lang;

    // English is the base so that a key missing from a translation still shows sensible text.
    table_ = LoadTable(IDR_STRINGS_EN_US);
    for (const auto& l : kLanguages) {
        if (!EqualsIgnoreCase(lang, l.name) || l.resource == IDR_STRINGS_EN_US) continue;
        // Keep the table alive for the loop: iterating LoadTable(...).object() would read a destroyed temporary.
        const json::Value translated = LoadTable(l.resource);
        for (const auto& [k, v] : translated.object()) table_.set(k, v);
    }
    for (const auto& [k, v] : overrides[WideToUtf8(lang)].object()) {
        if (v.isString() && v.asString().size() <= 4096) table_.set(k, v);
    }
}

std::wstring Strings::Get(std::string_view key) const {
    const auto& v = table_[key];
    return v.isString() ? v.asWide() : Utf8ToWide(key);
}

std::wstring Strings::Fmt(std::string_view key, std::initializer_list<std::wstring_view> args) const {
    return FormatIndexed(Get(key), args);
}

}  // namespace fsetup::boot
