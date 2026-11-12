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

// One embedded table per language (shared/localization/<lang>.json). Adding a language = a JSON file, an RCDATA line
// and an entry here.
struct Language {
    const wchar_t* prefix;  // matched against the start of a BCP-47 tag ("zh-Hans-CN" -> zh)
    const wchar_t* name;
    int resource;
};
constexpr Language kLanguages[] = {
    {L"en", L"en-US", IDR_STRINGS_EN_US},
    {L"zh", L"zh-CN", IDR_STRINGS_ZH_CN},
    {L"ja", L"ja-JP", IDR_STRINGS_JA_JP},
    {L"ko", L"ko-KR", IDR_STRINGS_KO_KR},
};

std::wstring Resolve(const std::wstring& tag) {
    for (const auto& l : kLanguages) {
        const size_t n = wcslen(l.prefix);
        if (tag.size() >= n && _wcsnicmp(tag.c_str(), l.prefix, n) == 0 && (tag.size() == n || tag[n] == L'-')) return l.name;
    }
    return {};
}

}  // namespace

void Strings::Load(const std::wstring& requested, const std::wstring& packageDefault, const json::Value& overrides) {
    std::wstring lang = Resolve(requested);
    if (lang.empty() && !packageDefault.empty() && !EqualsIgnoreCase(packageDefault, L"auto")) {
        // A project can pin the installer language; otherwise follow the user's Windows display language.
        lang = Resolve(packageDefault);
    }
    for (const auto& candidate : UserLanguages()) {
        if (!lang.empty()) break;
        lang = Resolve(candidate);
    }
    if (lang.empty()) lang = Resolve(packageDefault);
    if (lang.empty()) lang = L"en-US";
    language_ = lang;

    // English is the base so that a key missing from a translation still shows sensible text.
    table_ = LoadTable(IDR_STRINGS_EN_US);
    for (const auto& l : kLanguages) {
        if (lang != l.name || l.resource == IDR_STRINGS_EN_US) continue;
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
