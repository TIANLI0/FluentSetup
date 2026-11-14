// Installer languages: which built-in table a tag gets, the order of /lang, ui.defaultLanguage and package strings,
// and that every embedded table (tests/Native/strings.rc = the stub's localization.rc2) loads with all keys.
#include "test.h"

#include "Bootstrapper/localization.h"
#include "Install.Core/extensions.h"
#include "Common/json.h"
#include "Common/text.h"

using namespace fsetup;

namespace {

const wchar_t* const kBuiltIn[] = {L"en-US", L"zh-CN", L"zh-TW", L"ja-JP", L"ko-KR", L"de-DE",
                                   L"fr-FR", L"es-ES", L"pt-BR", L"ru-RU", L"it-IT"};

json::Value Overrides(std::string_view text) {
    json::Value v;
    CHECK(json::Parse(text, v));
    return v;
}

std::wstring LanguageFor(const std::wstring& requested, const std::wstring& packageDefault = L"auto",
                         const json::Value& overrides = {}) {
    boot::Strings s;
    s.Load(requested, packageDefault, overrides);
    return s.Language();
}

}  // namespace

TEST(built_in_language_matching) {
    for (const wchar_t* tag : kBuiltIn) CHECK(boot::BuiltInLanguage(tag) == tag);
    CHECK(boot::BuiltInLanguage(L"ZH-tw") == L"zh-TW");
    // Chinese: Traditional by script or region, Simplified otherwise.
    for (const wchar_t* tag : {L"zh-HK", L"zh-MO", L"zh-Hant", L"zh-Hant-TW", L"zh-Hant-HK", L"zh-hant-cn"}) {
        CHECK(boot::BuiltInLanguage(tag) == L"zh-TW");
    }
    for (const wchar_t* tag : {L"zh", L"zh-SG", L"zh-Hans", L"zh-Hans-CN", L"zh-Hans-HK", L"zh-Hans-MO"}) {
        CHECK(boot::BuiltInLanguage(tag) == L"zh-CN");
    }
    // Other languages: the primary language decides.
    CHECK(boot::BuiltInLanguage(L"de-AT") == L"de-DE" && boot::BuiltInLanguage(L"de-CH") == L"de-DE" && boot::BuiltInLanguage(L"de") == L"de-DE");
    CHECK(boot::BuiltInLanguage(L"fr-CA") == L"fr-FR" && boot::BuiltInLanguage(L"fr-BE") == L"fr-FR");
    CHECK(boot::BuiltInLanguage(L"es-MX") == L"es-ES" && boot::BuiltInLanguage(L"es-419") == L"es-ES");
    CHECK(boot::BuiltInLanguage(L"pt-PT") == L"pt-BR" && boot::BuiltInLanguage(L"pt") == L"pt-BR");
    CHECK(boot::BuiltInLanguage(L"ru-UA") == L"ru-RU" && boot::BuiltInLanguage(L"it-CH") == L"it-IT");
    CHECK(boot::BuiltInLanguage(L"en-GB") == L"en-US" && boot::BuiltInLanguage(L"ja") == L"ja-JP" && boot::BuiltInLanguage(L"ko") == L"ko-KR");
    // Languages the installer does not ship, and look-alikes.
    for (const wchar_t* tag : {L"nl-NL", L"pl", L"sr-Latn-RS", L"zhx", L"zhx-TW", L"e", L"", L"-", L"english"}) {
        CHECK(boot::BuiltInLanguage(tag).empty());
    }
    CHECK(boot::BuiltInLanguage(L"zh--") == L"zh-CN");
}

TEST(language_choice_order) {
    // /lang, then ui.defaultLanguage; both go through the same matching.
    CHECK(LanguageFor(L"zh-HK") == L"zh-TW");
    CHECK(LanguageFor(L"de-CH", L"ja-JP") == L"de-DE");
    CHECK(LanguageFor(L"", L"pt-PT") == L"pt-BR");
    CHECK(LanguageFor(L"", L"fr-CA") == L"fr-FR");
    // A package's table for exactly the tag wins over the built-in one ...
    const json::Value frCa = Overrides(R"json({"fr-CA": {"button.install": "Installer (CA)"}})json");
    CHECK(LanguageFor(L"fr-CA", L"auto", frCa) == L"fr-CA");
    // ... a built-in table of the language beats a package table of another region ...
    CHECK(LanguageFor(L"fr-BE", L"auto", frCa) == L"fr-FR");
    // ... and a package table still serves the other regions of a language the installer does not ship.
    const json::Value nl = Overrides(R"json({"nl-NL": {"button.install": "Installeren"}})json");
    CHECK(LanguageFor(L"nl-BE", L"auto", nl) == L"nl-NL");
    CHECK(LanguageFor(L"", L"nl-NL", nl) == L"nl-NL");
}

TEST(built_in_tables_load) {
    boot::Strings en;
    en.Load(L"en-US", L"auto", {});
    const auto& keys = en.Table().object();
    CHECK(keys.size() > 200 && en.Table()["language"].asString() == "en-US");
    for (const wchar_t* tag : kBuiltIn) {
        boot::Strings s;
        s.Load(tag, L"auto", {});
        CHECK(s.Language() == tag);
        // Each table names its own language, so this also proves the resource id behind the tag.
        CHECK(s.Table()["language"].asWide() == tag);
        CHECK(s.Table().object().size() == keys.size());
        if (std::wstring_view(tag) != L"en-US") CHECK(s.Get("button.cancel") != en.Get("button.cancel"));
        CHECK(s.Fmt("ask.install", {L"App", L"1.0"}).find(L"App") != std::wstring::npos);
    }
    // Package strings go on top of the built-in table of the chosen language.
    boot::Strings tw;
    tw.Load(L"zh-TW", L"auto", Overrides(R"json({"zh-TW": {"button.install": "立即安裝"}})json"));
    CHECK(tw.Get("button.install") == L"立即安裝" && tw.Get("button.cancel") == L"取消");
}

TEST(package_texts_fall_back_to_the_same_language) {
    using install::ClosestLanguage;
    CHECK(ClosestLanguage("zh-TW", {"en-US", "zh-CN"}) == "zh-CN");             // 1.1 showed zh-CN here too
    CHECK(ClosestLanguage("zh-TW", {"zh-CN", "zh-HK", "en-US"}) == "zh-HK");    // same script before zh-CN
    CHECK(ClosestLanguage("zh-CN", {"zh-TW", "zh-SG"}) == "zh-SG");
    CHECK(ClosestLanguage("de-AT", {"en-US", "de-DE"}) == "de-DE");
    CHECK(ClosestLanguage("ja-jp", {"JA-JP"}) == "JA-JP");
    CHECK(ClosestLanguage("fr-FR", {"en-US", "de-DE"}).empty());
    install::LocalizedText t;
    t.byLanguage = {{"en-US", L"Enable"}, {"zh-CN", L"启用"}, {"zh-TW", L""}};
    CHECK(t.Get(L"zh-TW", L"x") == L"启用");  // an empty zh-TW text does not count
    CHECK(t.Get(L"fr-FR", L"x") == L"Enable");
    t.byLanguage = {{"*", L"PawnIO"}, {"de-DE", L"Treiber"}};
    CHECK(t.Get(L"de-CH", L"x") == L"Treiber" && t.Get(L"ko-KR", L"x") == L"PawnIO");
}
