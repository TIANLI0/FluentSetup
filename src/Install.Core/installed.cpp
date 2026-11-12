#include "Install.Core/installed.h"

#include "Common/error.h"
#include "Common/fs.h"
#include "Common/path.h"
#include "Common/text.h"

#include <algorithm>

#pragma comment(lib, "advapi32.lib")

namespace fsetup::install {

namespace {

constexpr REGSAM kView = KEY_WOW64_64KEY;
constexpr wchar_t kMarkerValue[] = L"FluentSetup";

std::wstring ReadString(HKEY key, const wchar_t* name) {
    DWORD type = 0, size = 0;
    if (::RegQueryValueExW(key, name, nullptr, &type, nullptr, &size) != ERROR_SUCCESS) return {};
    if ((type != REG_SZ && type != REG_EXPAND_SZ) || size == 0 || size > 64 * 1024) return {};
    std::wstring value(size / sizeof(wchar_t) + 1, L'\0');
    if (::RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(value.data()), &size) != ERROR_SUCCESS) {
        return {};
    }
    value.resize(wcsnlen(value.c_str(), value.size()));
    return value;
}

DWORD ReadDword(HKEY key, const wchar_t* name, DWORD fallback = 0) {
    DWORD type = 0, value = 0, size = sizeof(value);
    if (::RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(&value), &size) != ERROR_SUCCESS ||
        type != REG_DWORD) {
        return fallback;
    }
    return value;
}

void SetString(HKEY key, const wchar_t* name, const std::wstring& value) {
    const LSTATUS st = ::RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
                                        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    if (st != ERROR_SUCCESS) throw Failure(ErrorCode::RegistryError, std::wstring(L"Cannot write registry value ") + name, st);
}

void SetDword(HKEY key, const wchar_t* name, DWORD value) {
    const LSTATUS st = ::RegSetValueExW(key, name, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value));
    if (st != ERROR_SUCCESS) throw Failure(ErrorCode::RegistryError, std::wstring(L"Cannot write registry value ") + name, st);
}

std::wstring PendingKeyPath(const std::wstring& appId) { return L"Software\\FluentSetup\\Pending\\" + appId; }

}  // namespace

std::wstring ArpKeyPath(const std::wstring& appId) {
    return L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + appId;
}

HKEY RootFor(Scope scope) noexcept { return scope == Scope::Machine ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER; }

std::optional<InstalledProduct> QueryInstalled(const std::wstring& appId, Scope scope) {
    UniqueHkey key;
    if (::RegOpenKeyExW(RootFor(scope), ArpKeyPath(appId).c_str(), 0, KEY_READ | kView, key.put()) != ERROR_SUCCESS) {
        return std::nullopt;
    }
    if (ReadDword(key.get(), kMarkerValue) != 1) return std::nullopt;
    InstalledProduct p;
    p.scope = scope;
    p.appId = appId;
    p.name = ReadString(key.get(), L"DisplayName");
    p.version = ReadString(key.get(), L"DisplayVersion");
    p.publisher = ReadString(key.get(), L"Publisher");
    p.installDir = ReadString(key.get(), L"InstallLocation");
    p.recordSha256 = ReadString(key.get(), L"FluentSetupRecordSha256");
    p.variant = WideToUtf8(ReadString(key.get(), L"FluentSetupVariant")).substr(0, 32);
    std::wstring normalized;
    if (path::ValidateAbsoluteSyntax(p.installDir, normalized) == path::Problem::None) p.installDir = normalized;
    return p;
}

std::optional<InstalledProduct> FindInstalled(const std::wstring& appId) {
    if (auto p = QueryInstalled(appId, Scope::User)) return p;
    return QueryInstalled(appId, Scope::Machine);
}

bool ForeignArpEntryExists(const std::wstring& appId, Scope scope) {
    UniqueHkey key;
    if (::RegOpenKeyExW(RootFor(scope), ArpKeyPath(appId).c_str(), 0, KEY_READ | kView, key.put()) != ERROR_SUCCESS) {
        return false;
    }
    return ReadDword(key.get(), kMarkerValue) != 1;
}

std::optional<InstalledProduct> QueryForeign(const std::wstring& appId, Scope scope) {
    UniqueHkey key;
    if (::RegOpenKeyExW(RootFor(scope), ArpKeyPath(appId).c_str(), 0, KEY_READ | kView, key.put()) != ERROR_SUCCESS) {
        return std::nullopt;
    }
    if (ReadDword(key.get(), kMarkerValue) == 1) return std::nullopt;
    InstalledProduct p;
    p.scope = scope;
    p.appId = appId;
    p.name = ReadString(key.get(), L"DisplayName");
    p.version = ReadString(key.get(), L"DisplayVersion");
    p.publisher = ReadString(key.get(), L"Publisher");
    // InstallLocation, else the folder of the uninstaller or of the icon ("C:\App\app.exe",0). Older installers
    // (NSIS, Inno, hand-written keys) do not always write InstallLocation.
    const auto folderOf = [](std::wstring v) -> std::wstring {
        if (v.empty()) return {};
        if (v.front() == L'"') {
            const size_t close = v.find(L'"', 1);
            v = close == std::wstring::npos ? v.substr(1) : v.substr(1, close - 1);
        } else if (const size_t exe = ToLower(v).find(L".exe"); exe != std::wstring::npos) {
            v = v.substr(0, exe + 4);
        }
        return path::Parent(v);
    };
    for (const std::wstring& candidate : {ReadString(key.get(), L"InstallLocation"), folderOf(ReadString(key.get(), L"UninstallString")),
                                          folderOf(ReadString(key.get(), L"DisplayIcon"))}) {
        std::wstring normalized;
        if (!candidate.empty() && path::ValidateAbsoluteSyntax(candidate, normalized) == path::Problem::None && fs::IsDirectory(normalized)) {
            p.installDir = normalized;
            break;
        }
    }
    return p;
}

bool DeleteForeignArpEntry(const std::wstring& keyName, Scope scope) {
    if (!ForeignArpEntryExists(keyName, scope)) return false;  // never one of ours
    return ::RegDeleteKeyExW(RootFor(scope), ArpKeyPath(keyName).c_str(), kView, 0) == ERROR_SUCCESS;
}

json::Value RecordToJson(const InstallRecord& r) {
    json::Value v;
    v.set("format", 1);
    v.set("appId", r.appId);
    v.set("name", r.name);
    v.set("version", r.version);
    v.set("publisher", r.publisher);
    v.set("scope", ScopeName(r.scope));
    v.set("installDir", r.installDir);
    v.set("mainExecutable", r.mainExecutable);
    v.set("createdRoot", r.createdRoot);
    json::Value files = json::Value::MakeArray();
    for (const auto& f : r.files) {
        json::Value e;
        e.set("path", f.path);
        e.set("size", f.size);
        e.set("sha256", ToHex(f.sha256));
        if (f.preserve) e.set("preserve", true);
        files.push(e);
    }
    v.set("files", files);
    json::Value dirs = json::Value::MakeArray();
    for (const auto& d : r.directories) dirs.push(d);
    v.set("directories", dirs);
    json::Value links = json::Value::MakeArray();
    for (const auto& s : r.shortcuts) links.push(s);
    v.set("shortcuts", links);
    json::Value choices = json::Value::MakeObject();
    for (const auto& [id, on] : r.choices) choices.set(id, on);
    v.set("choices", choices);
    return v;
}

InstallRecord RecordFromJson(const json::Value& v) {
    const auto bad = [](const std::wstring& why) -> Failure {
        return Failure(ErrorCode::NotInstalled, L"Install record is invalid: " + why);
    };
    if (!v.isObject() || v["format"].asInt() != 1) throw bad(L"format");
    InstallRecord r;
    r.appId = v["appId"].asWide();
    r.name = v["name"].asWide();
    r.version = v["version"].asWide();
    r.publisher = v["publisher"].asWide();
    r.scope = v["scope"].asString() == "machine" ? Scope::Machine : Scope::User;
    r.createdRoot = v["createdRoot"].asBool();
    std::wstring dir;
    if (path::ValidateAbsoluteSyntax(v["installDir"].asWide(), dir) != path::Problem::None) throw bad(L"installDir");
    r.installDir = dir;
    if (!v["mainExecutable"].asWide().empty()) {
        if (!path::ValidateRelative(v["mainExecutable"].asWide(), r.mainExecutable)) throw bad(L"mainExecutable");
    }
    for (const auto& f : v["files"].array()) {
        RecordFile rf;
        if (!path::ValidateRelative(f["path"].asWide(), rf.path)) throw bad(L"file path " + f["path"].asWide());
        rf.size = f["size"].asUint();
        std::vector<uint8_t> digest;
        if (!FromHex(f["sha256"].asString(), digest) || digest.size() != rf.sha256.size()) throw bad(L"sha256");
        std::copy(digest.begin(), digest.end(), rf.sha256.begin());
        rf.preserve = f["preserve"].asBool();
        r.files.push_back(std::move(rf));
    }
    for (const auto& d : v["directories"].array()) {
        std::wstring rel;
        if (!path::ValidateRelative(d.asWide(), rel)) throw bad(L"directory " + d.asWide());
        r.directories.push_back(rel);
    }
    for (const auto& s : v["shortcuts"].array()) {
        const std::wstring p = s.asWide();
        if (!IsAllowedShortcutPath(p, r.scope)) throw bad(L"shortcut " + p);
        r.shortcuts.push_back(p);
    }
    for (const auto& [id, on] : v["choices"].object()) {  // absent in records written before 1.0
        if (id.size() <= 32 && r.choices.size() < 16) r.choices[id] = on.asBool();
    }
    return r;
}

InstallRecord LoadVerifiedRecord(const InstalledProduct& product) {
    if (product.installDir.empty()) throw Failure(ErrorCode::NotInstalled, L"ARP entry has no InstallLocation");
    const std::wstring recordPath = path::Join(product.installDir, kRecordName);
    if (fs::HasLinkInPath(recordPath)) throw Failure(ErrorCode::UnsafePath, L"Install location goes through a link");
    std::vector<uint8_t> bytes;
    try {
        bytes = fs::ReadAll(recordPath, 256ull * 1024 * 1024);
    } catch (const Failure& f) {
        throw Failure(ErrorCode::NotInstalled, L"Cannot read install record: " + f.detail(), f.systemError());
    }
    const std::string hex = ToHex(Sha256::Of(bytes));
    if (product.recordSha256.empty() || !EqualsIgnoreCase(Utf8ToWide(hex), product.recordSha256)) {
        throw Failure(ErrorCode::NotInstalled, L"Install record does not match the registered checksum");
    }
    json::Value v;
    if (!json::Parse(std::string_view(reinterpret_cast<const char*>(bytes.data()), bytes.size()), v)) {
        throw Failure(ErrorCode::NotInstalled, L"Install record is not valid JSON");
    }
    InstallRecord r = RecordFromJson(v);
    if (!EqualsIgnoreCase(r.appId, product.appId) || r.scope != product.scope ||
        !EqualsIgnoreCase(r.installDir, product.installDir)) {
        throw Failure(ErrorCode::NotInstalled, L"Install record belongs to a different installation");
    }
    return r;
}

std::vector<std::wstring> ShortcutFolders(Scope scope) {
    if (scope == Scope::Machine) {
        return {path::KnownFolder(FOLDERID_CommonPrograms), path::KnownFolder(FOLDERID_PublicDesktop)};
    }
    return {path::KnownFolder(FOLDERID_Programs), path::KnownFolder(FOLDERID_Desktop)};
}

bool IsAllowedShortcutPath(const std::wstring& p, Scope scope) {
    if (p.size() < 5 || !EqualsIgnoreCase(std::wstring_view(p).substr(p.size() - 4), L".lnk")) return false;
    std::wstring normalized;
    if (path::ValidateAbsoluteSyntax(p, normalized) != path::Problem::None) return false;
    const std::wstring parent = path::Parent(normalized);
    for (const auto& folder : ShortcutFolders(scope)) {
        if (!folder.empty() && EqualsIgnoreCase(parent, folder)) return true;
    }
    return false;
}

json::Value SnapshotArp(const std::wstring& appId, Scope scope) {
    UniqueHkey key;
    if (::RegOpenKeyExW(RootFor(scope), ArpKeyPath(appId).c_str(), 0, KEY_READ | kView, key.put()) != ERROR_SUCCESS) {
        return json::Value();
    }
    json::Value values = json::Value::MakeObject();
    for (DWORD i = 0;; ++i) {
        wchar_t name[256];
        DWORD nameLen = 256, type = 0, size = 0;
        if (::RegEnumValueW(key.get(), i, name, &nameLen, nullptr, &type, nullptr, &size) != ERROR_SUCCESS) break;
        if (type == REG_DWORD) {
            values.set(WideToUtf8(name), json::Value(static_cast<int64_t>(ReadDword(key.get(), name))));
        } else if (type == REG_SZ || type == REG_EXPAND_SZ) {
            values.set(WideToUtf8(name), json::Value(ReadString(key.get(), name)));
        }
    }
    return values;
}

void RestoreArp(const std::wstring& appId, Scope scope, const json::Value& snapshot) {
    DeleteArp(appId, scope);
    if (!snapshot.isObject()) return;
    UniqueHkey key;
    if (::RegCreateKeyExW(RootFor(scope), ArpKeyPath(appId).c_str(), 0, nullptr, 0, KEY_WRITE | kView, nullptr, key.put(),
                          nullptr) != ERROR_SUCCESS) {
        return;
    }
    for (const auto& [name, value] : snapshot.object()) {
        const std::wstring wname = Utf8ToWide(name);
        try {
            if (value.isNumber()) SetDword(key.get(), wname.c_str(), static_cast<DWORD>(value.asInt()));
            else if (value.isString()) SetString(key.get(), wname.c_str(), value.asWide());
        } catch (const Failure&) {
            // best effort during rollback
        }
    }
}

void WriteArp(const std::wstring& appId, Scope scope, const ArpEntry& e) {
    UniqueHkey key;
    const LSTATUS st = ::RegCreateKeyExW(RootFor(scope), ArpKeyPath(appId).c_str(), 0, nullptr, 0, KEY_WRITE | kView,
                                         nullptr, key.put(), nullptr);
    if (st != ERROR_SUCCESS) {
        throw Failure(st == ERROR_ACCESS_DENIED ? ErrorCode::AccessDenied : ErrorCode::RegistryError,
                      L"Cannot create uninstall registry key", st);
    }
    SetString(key.get(), L"DisplayName", e.displayName);
    SetString(key.get(), L"DisplayVersion", e.displayVersion);
    if (!e.publisher.empty()) SetString(key.get(), L"Publisher", e.publisher);
    SetString(key.get(), L"DisplayIcon", e.displayIcon);
    SetString(key.get(), L"InstallLocation", e.installLocation);
    SetString(key.get(), L"UninstallString", e.uninstallString);
    SetString(key.get(), L"QuietUninstallString", e.quietUninstallString);
    if (!e.urlInfoAbout.empty()) SetString(key.get(), L"URLInfoAbout", e.urlInfoAbout);
    if (!e.comments.empty()) SetString(key.get(), L"Comments", e.comments);
    SYSTEMTIME t{};
    ::GetLocalTime(&t);
    wchar_t date[16];
    swprintf_s(date, L"%04u%02u%02u", t.wYear, t.wMonth, t.wDay);
    SetString(key.get(), L"InstallDate", date);
    SetDword(key.get(), L"EstimatedSize", e.estimatedSizeKb);
    SetDword(key.get(), L"VersionMajor", e.versionMajor);
    SetDword(key.get(), L"VersionMinor", e.versionMinor);
    SetDword(key.get(), L"NoModify", 1);
    SetDword(key.get(), L"NoRepair", 1);
    SetDword(key.get(), kMarkerValue, 1);
    SetString(key.get(), L"FluentSetupRecordSha256", e.recordSha256);
    if (!e.variant.empty()) SetString(key.get(), L"FluentSetupVariant", e.variant);
    else ::RegDeleteValueW(key.get(), L"FluentSetupVariant");
}

void DeleteArp(const std::wstring& appId, Scope scope) {
    ::RegDeleteKeyExW(RootFor(scope), ArpKeyPath(appId).c_str(), kView, 0);
}

void SetPending(const std::wstring& appId, Scope scope, const std::wstring& installDir) {
    UniqueHkey key;
    if (::RegCreateKeyExW(RootFor(scope), PendingKeyPath(appId).c_str(), 0, nullptr, 0, KEY_WRITE | kView, nullptr,
                          key.put(), nullptr) != ERROR_SUCCESS) {
        throw Failure(ErrorCode::RegistryError, L"Cannot create the pending-operation marker");
    }
    SetString(key.get(), L"InstallDir", installDir);
}

std::optional<std::wstring> GetPending(const std::wstring& appId, Scope scope) {
    UniqueHkey key;
    if (::RegOpenKeyExW(RootFor(scope), PendingKeyPath(appId).c_str(), 0, KEY_READ | kView, key.put()) != ERROR_SUCCESS) {
        return std::nullopt;
    }
    std::wstring dir = ReadString(key.get(), L"InstallDir");
    std::wstring normalized;
    if (path::ValidateAbsoluteSyntax(dir, normalized) != path::Problem::None) return std::nullopt;
    return normalized;
}

void ClearPending(const std::wstring& appId, Scope scope) {
    ::RegDeleteKeyExW(RootFor(scope), PendingKeyPath(appId).c_str(), kView, 0);
}

}  // namespace fsetup::install
