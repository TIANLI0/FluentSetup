// Unit tests for the 1.2 additions: validation rules of associations, protocols, registry keys and environment
// names, placeholders, input checks, registry snapshots and the journal confinement, JSON duplicate keys, Base64.
#include "test.h"

#include "Common/error.h"
#include "Common/json.h"
#include "Common/text.h"
#include "Install.Core/extensions.h"
#include "Install.Core/integration.h"
#include "Install.Core/journal.h"

using namespace fsetup;
using namespace fsetup::install;

TEST(owned_registry_keys) {
    CHECK(IsOwnedRegistryKey(L"Software\\Classes\\Contoso.Notes.Document", true));
    CHECK(IsOwnedRegistryKey(L"Software\\Classes\\Contoso.Notes.Document\\shell\\open\\command"));
    CHECK(!IsOwnedRegistryKey(L"Software\\Classes\\Contoso.Notes.Document\\shell", true));  // only the whole ProgID tree
    CHECK(IsOwnedRegistryKey(L"Software\\Classes\\.cnote\\OpenWithProgids"));
    CHECK(!IsOwnedRegistryKey(L"Software\\Classes\\.cnote", true));  // extensions are shared: values only
    CHECK(IsOwnedRegistryKey(L"Software\\Classes\\contoso-notes", true));
    CHECK(IsOwnedRegistryKey(L"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\Notes.exe", true));
    CHECK(IsOwnedRegistryKey(L"Software\\FluentSetup\\Capabilities\\Contoso.Notes", true));
    CHECK(IsOwnedRegistryKey(L"Software\\RegisteredApplications"));
    CHECK(!IsOwnedRegistryKey(L"Software\\RegisteredApplications", true));
    CHECK(IsOwnedRegistryKey(L"Software\\Contoso\\Notes", true));
    for (const wchar_t* bad : {L"Software\\Classes\\.exe\\OpenWithProgids", L"Software\\Classes\\exefile", L"Software\\Classes\\*",
                               L"Software\\Classes\\CLSID\\{0}", L"Software\\Classes\\http", L"Software\\Classes\\ms-settings",
                               L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", L"Software\\Microsoft", L"Software\\Policies\\X",
                               L"Software\\Classes", L"Software", L"System\\CurrentControlSet\\Services\\X", L"Software\\Wow6432Node\\X",
                               L"Software\\FluentSetup\\Pending\\X", L"\\Software\\X", L"Software\\X\\", L""}) {
        CHECK(!IsOwnedRegistryKey(bad));
    }
}

TEST(environment_and_service_names) {
    CHECK(IsEnvironmentName(L"CONTOSO_HOME"));
    CHECK(IsEnvironmentName(L"Contoso.Data(1)"));
    for (const wchar_t* bad : {L"Path", L"PATH", L"TEMP", L"SystemRoot", L"PROCESSOR_ARCHITECTURE", L"1ABC", L"A=B", L"", L"A B"}) CHECK(!IsEnvironmentName(bad));
    CHECK(IsServiceName(L"Contoso.Agent_2"));
    CHECK(!IsServiceName(L"Contoso Agent"));
    CHECK(!IsServiceName(L"a\\b"));
    CHECK(!IsServiceName(std::wstring(81, L'a')));
}

TEST(registry_numbers) {
    uint64_t n = 0;
    CHECK(ParseRegistryNumber(L"16", false, n) && n == 16);
    CHECK(ParseRegistryNumber(L"0x10", false, n) && n == 16);
    CHECK(ParseRegistryNumber(L"4294967295", false, n));
    CHECK(!ParseRegistryNumber(L"4294967296", false, n));
    CHECK(ParseRegistryNumber(L"4294967296", true, n) && n == 4294967296ull);
    for (const wchar_t* bad : {L"", L"-1", L"0x", L"1e3", L"12a", L"0xZZ", L"99999999999999999999999"}) CHECK(!ParseRegistryNumber(bad, true, n));
}

TEST(placeholders_and_inputs) {
    std::map<std::string, std::wstring> inputs{{"server", L"https://x"}};
    PlaceholderContext ctx{L"C:\\App", L"C:\\App\\a.exe", L"1.2.0", L"Contoso.App", &inputs};
    CHECK(ExpandPlaceholders(L"{InstallDir}\\data;{AppVersion};{AppId};{MainExecutable}", ctx) == L"C:\\App\\data;1.2.0;Contoso.App;C:\\App\\a.exe");
    CHECK(ExpandPlaceholders(L"{Input:server}/api {Input:missing}|{Unknown}", ctx) == L"https://x/api |{Unknown}");
    CHECK(QuoteArguments({L"%1", L"--name", L"a b", L"{Input:server}"}, ctx) == L"\"%1\" --name \"a b\" https://x");

    InputDef text;
    text.id = "server";
    text.required = true;
    text.pattern = L"^https://";
    CHECK(CheckInputValue(text, L"https://example").empty());
    CHECK(!CheckInputValue(text, L"").empty());
    CHECK(!CheckInputValue(text, L"http://example").empty());
    InputDef choice;
    choice.type = InputDef::Type::Choice;
    choice.choices = {{L"fast", {}}, {L"safe", {}}};
    CHECK(CheckInputValue(choice, L"safe").empty());
    CHECK(!CheckInputValue(choice, L"other").empty());
    CHECK(CheckInputValue(choice, L"").empty());  // optional
}

TEST(json_rejects_duplicate_keys) {
    json::Value v;
    CHECK(json::Parse(R"({"a":1,"b":2})", v));
    CHECK(!json::Parse(R"({"a":1,"a":2})", v));
    std::string big = "{";
    for (int i = 0; i < 100; ++i) big += "\"k" + std::to_string(i) + "\":" + std::to_string(i) + ",";
    CHECK(json::Parse(big + "\"z\":0}", v));
    CHECK(!json::Parse(big + "\"k50\":0}", v));
}

TEST(base64) {
    std::vector<uint8_t> out;
    CHECK(FromBase64("", out) && out.empty());
    CHECK(FromBase64("TWFu", out) && std::string(out.begin(), out.end()) == "Man");
    CHECK(FromBase64("TWE=", out) && std::string(out.begin(), out.end()) == "Ma");
    CHECK(FromBase64("TQ==", out) && std::string(out.begin(), out.end()) == "M");
    for (const char* bad : {"TWF", "T===", "TW=u", "TQ==TQ==", "TW!u"}) CHECK(!FromBase64(bad, out));
}

namespace {
// A test key below HKCU that the confinement rules accept as a vendor key.
constexpr wchar_t kTestKey[] = L"Software\\FluentSetupTests\\Native";
void CleanupTestKeys() { ::RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\FluentSetupTests"), ::RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\FluentSetupTests"); }
}  // namespace

TEST(registry_writer_rollback_restores_previous_state) {
    CleanupTestKeys();
    {
        // Existing state: one value and a subkey.
        RegistryWriter setup(HKEY_CURRENT_USER, nullptr);
        setup.SetValue(kTestKey, L"Keep", REG_SZ, L"before");
        setup.SetValue(std::wstring(kTestKey) + L"\\Sub", L"N", REG_DWORD, L"7");
    }
    wchar_t temp[MAX_PATH];
    ::GetTempPathW(MAX_PATH, temp);
    const std::wstring dir = std::wstring(temp) + L"fsetup-journal-test-" + std::to_wstring(::GetCurrentProcessId());
    ::CreateDirectoryW(dir.c_str(), nullptr);
    ::CreateDirectoryW((dir + L"\\.fsetup").c_str(), nullptr);
    std::vector<json::Value> entries;
    {
        Journal journal(dir + L"\\.fsetup\\journal.jsonl");
        journal.Begin(L"install", dir, Scope::User, L"Test.App");
        RegistryWriter writer(HKEY_CURRENT_USER, &journal);
        writer.SetValue(kTestKey, L"Keep", REG_SZ, L"after");
        writer.SetValue(kTestKey, L"New", REG_MULTI_SZ, L"x\ny");
        writer.SetValue(L"Software\\FluentSetupTests\\Created\\Deep", L"V", REG_QWORD, L"0x100000000");
        writer.DeleteTree(std::wstring(kTestKey) + L"\\Sub");
        CHECK(writer.ReadString(kTestKey, L"Keep") == L"after");
        entries = journal.Entries();
    }
    const auto stats = Rollback(entries, dir, Scope::User, L"Test.App");
    CHECK(stats.failed == 0);
    RegistryWriter check(HKEY_CURRENT_USER, nullptr);
    CHECK(check.ReadString(kTestKey, L"Keep") == L"before");
    CHECK(SnapshotRegistryValue(HKEY_CURRENT_USER, kTestKey, L"New").isNull());
    CHECK(SnapshotRegistryValue(HKEY_CURRENT_USER, std::wstring(kTestKey) + L"\\Sub", L"N")["d"].asInt() == 7);
    CHECK(!check.KeyExists(L"Software\\FluentSetupTests\\Created"));
    ::DeleteFileW((dir + L"\\.fsetup\\journal.jsonl").c_str());
    ::RemoveDirectoryW((dir + L"\\.fsetup").c_str());
    ::RemoveDirectoryW(dir.c_str());
    CleanupTestKeys();
}

TEST(journal_registry_entries_are_confined) {
    // A tampered journal must not make rollback touch keys outside what setup owns.
    ::RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\FluentSetupTests");
    std::vector<json::Value> entries;
    json::Value begin;
    begin.set("op", "begin");
    entries.push_back(begin);
    json::Value e;
    e.set("op", "regkey");
    e.set("key", L"Software\\Microsoft\\Windows\\CurrentVersion\\Run");
    e.set("snapshot", json::Value());  // would delete the Run key
    entries.push_back(e);
    json::Value v;
    v.set("op", "regvalue");
    v.set("key", L"Environment");
    v.set("name", L"Path");
    v.set("snapshot", json::Value());
    entries.push_back(v);
    Rollback(entries, L"C:\\NoSuchDir", Scope::User, L"Test.App");
    HKEY key = nullptr;
    CHECK(::RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", 0, KEY_READ, &key) == ERROR_SUCCESS);
    if (key) ::RegCloseKey(key);
}

TEST(partial_rollback_keeps_only_what_is_left) {
    wchar_t temp[MAX_PATH];
    ::GetTempPathW(MAX_PATH, temp);
    const std::wstring dir = std::wstring(temp) + L"fsetup-partial-" + std::to_wstring(::GetCurrentProcessId());
    ::CreateDirectoryW(dir.c_str(), nullptr);
    const std::wstring placed = dir + L"\\placed.txt", locked = dir + L"\\locked.txt";
    for (const auto& f : {placed, locked}) {
        HANDLE h = ::CreateFileW(f.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        CHECK(h != INVALID_HANDLE_VALUE);
        ::CloseHandle(h);
    }
    json::Value begin, a, b;
    begin.set("op", "begin");
    a.set("op", "place");
    a.set("path", placed);
    b.set("op", "place");
    b.set("path", locked);
    // A file that stays open without delete sharing cannot be removed: only its entry is kept for the next run.
    HANDLE hold = ::CreateFileW(locked.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    const auto stats = Rollback({begin, a, b}, dir, Scope::User, L"Test.App");
    ::CloseHandle(hold);
    CHECK(stats.failed == 1 && stats.undone == 1);
    CHECK(stats.remaining.size() == 2 && stats.remaining[0]["op"].asString() == "begin" && stats.remaining[1]["path"].asWide() == locked);
    CHECK(::GetFileAttributesW(placed.c_str()) == INVALID_FILE_ATTRIBUTES);
    const auto again = Rollback(stats.remaining, dir, Scope::User, L"Test.App");
    CHECK(again.failed == 0 && again.remaining.empty());
    ::RemoveDirectoryW(dir.c_str());
}

TEST(registry_writer_refuses_foreign_keys) {
    RegistryWriter writer(HKEY_CURRENT_USER, nullptr);
    CHECK_THROWS_CODE(writer.SetValue(L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", L"X", REG_SZ, L"y"), ErrorCode::RegistryError);
    CHECK_THROWS_CODE(writer.ReplaceTree(L"Software\\Classes\\.txt"), ErrorCode::RegistryError);
}
