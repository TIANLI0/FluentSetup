#include "Install.Core/integration.h"

#include "Common/error.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Install.Core/journal.h"

#include <netfw.h>
#include <restartmanager.h>
#include <shlobj.h>

#include <algorithm>
#include <cwchar>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "rstrtmgr.lib")

namespace fsetup::install {

namespace {

constexpr REGSAM kView = KEY_WOW64_64KEY;
constexpr wchar_t kClasses[] = L"Software\\Classes\\";
constexpr wchar_t kRegisteredApplications[] = L"Software\\RegisteredApplications";

// Bounds of a registry snapshot (a tree setup replaces is normally a handful of keys).
constexpr int kMaxDepth = 8;
constexpr size_t kMaxNodes = 4096;
constexpr size_t kMaxData = 4u << 20;

std::wstring Lower(const std::wstring& s) { return ToLower(s); }

std::wstring Parent(const std::wstring& key) {
    const size_t slash = key.rfind(L'\\');
    return slash == std::wstring::npos ? std::wstring() : key.substr(0, slash);
}

struct SnapshotBudget {
    size_t nodes = 0;
    size_t data = 0;
};

json::Value ValueToJson(DWORD type, const std::vector<uint8_t>& data) {
    json::Value v;
    v.set("t", static_cast<int64_t>(type));
    const auto* w = reinterpret_cast<const wchar_t*>(data.data());
    const size_t chars = data.size() / sizeof(wchar_t);
    if (type == REG_SZ || type == REG_EXPAND_SZ) {
        v.set("d", std::wstring(w, wcsnlen(w, chars)));
    } else if (type == REG_MULTI_SZ) {
        json::Value list = json::Value::MakeArray();
        for (size_t i = 0; i < chars && w[i];) {
            const size_t len = wcsnlen(w + i, chars - i);
            list.push(std::wstring(w + i, len));
            i += len + 1;
        }
        v.set("d", list);
    } else if (type == REG_DWORD && data.size() >= 4) {
        v.set("d", static_cast<int64_t>(*reinterpret_cast<const uint32_t*>(data.data())));
    } else {
        v.set("d", ToHex(data));  // REG_QWORD, REG_BINARY, REG_NONE, ...: raw bytes
    }
    return v;
}

std::vector<uint8_t> ValueFromJson(const json::Value& v, DWORD& type) {
    type = static_cast<DWORD>(v["t"].asUint(REG_SZ));
    std::vector<uint8_t> out;
    const auto appendWide = [&](const std::wstring& s) {
        const auto* b = reinterpret_cast<const uint8_t*>(s.c_str());
        out.insert(out.end(), b, b + (s.size() + 1) * sizeof(wchar_t));
    };
    if (type == REG_SZ || type == REG_EXPAND_SZ) {
        appendWide(v["d"].asWide());
    } else if (type == REG_MULTI_SZ) {
        for (const auto& s : v["d"].array()) appendWide(s.asWide());
        appendWide(L"");
    } else if (type == REG_DWORD && v["d"].isNumber()) {
        const uint32_t d = static_cast<uint32_t>(v["d"].asUint());
        out.resize(4);
        memcpy(out.data(), &d, 4);
    } else {
        FromHex(v["d"].asString(), out);
    }
    return out;
}

json::Value SnapshotOpen(HKEY key, int depth, SnapshotBudget& budget) {
    if (depth > kMaxDepth || ++budget.nodes > kMaxNodes) throw Failure(ErrorCode::RegistryError, L"Registry key too large to restore safely");
    json::Value node;
    DWORD subkeys = 0, values = 0, maxName = 0, maxValueName = 0, maxData = 0;
    if (::RegQueryInfoKeyW(key, nullptr, nullptr, nullptr, &subkeys, &maxName, nullptr, &values, &maxValueName, &maxData, nullptr, nullptr) != ERROR_SUCCESS) {
        throw Failure(ErrorCode::RegistryError, L"Cannot read a registry key");
    }
    json::Value list = json::Value::MakeArray();
    std::vector<wchar_t> name(maxValueName + 2);
    std::vector<uint8_t> data(maxData + 2);
    for (DWORD i = 0; i < values; ++i) {
        DWORD nameLen = static_cast<DWORD>(name.size()), dataLen = static_cast<DWORD>(data.size()), type = 0;
        if (::RegEnumValueW(key, i, name.data(), &nameLen, nullptr, &type, data.data(), &dataLen) != ERROR_SUCCESS) continue;
        budget.data += dataLen;
        if (budget.data > kMaxData) throw Failure(ErrorCode::RegistryError, L"Registry key too large to restore safely");
        json::Value item = ValueToJson(type, std::vector<uint8_t>(data.begin(), data.begin() + dataLen));
        item.set("n", std::wstring(name.data(), nameLen));
        list.push(item);
    }
    node.set("v", list);
    json::Value keys = json::Value::MakeObject();
    std::vector<wchar_t> sub(maxName + 2);
    for (DWORD i = 0; i < subkeys; ++i) {
        DWORD len = static_cast<DWORD>(sub.size());
        if (::RegEnumKeyExW(key, i, sub.data(), &len, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) continue;
        UniqueHkey child;
        const std::wstring childName(sub.data(), len);
        if (::RegOpenKeyExW(key, childName.c_str(), 0, KEY_READ | kView, child.put()) != ERROR_SUCCESS) continue;
        keys.set(WideToUtf8(childName), SnapshotOpen(child.get(), depth + 1, budget));
    }
    node.set("k", keys);
    return node;
}

void RestoreOpen(HKEY key, const json::Value& node, int depth) {
    if (depth > kMaxDepth) return;
    for (const auto& v : node["v"].array()) {
        DWORD type = REG_SZ;
        const std::vector<uint8_t> data = ValueFromJson(v, type);
        const std::wstring name = v["n"].asWide();
        ::RegSetValueExW(key, name.c_str(), 0, type, data.empty() ? nullptr : data.data(), static_cast<DWORD>(data.size()));
    }
    for (const auto& [name, child] : node["k"].object()) {
        UniqueHkey sub;
        if (::RegCreateKeyExW(key, Utf8ToWide(name).c_str(), 0, nullptr, 0, KEY_ALL_ACCESS | kView, nullptr, sub.put(), nullptr) == ERROR_SUCCESS) {
            RestoreOpen(sub.get(), child, depth + 1);
        }
    }
}

void DeleteTreeRaw(HKEY root, const std::wstring& key) {
    const std::wstring parent = Parent(key);
    UniqueHkey p;
    if (::RegOpenKeyExW(root, parent.c_str(), 0, KEY_ALL_ACCESS | kView, p.put()) != ERROR_SUCCESS) return;
    const std::wstring leaf = key.substr(parent.size() + 1);
    if (::RegDeleteTreeW(p.get(), leaf.c_str()) == ERROR_SUCCESS) ::RegDeleteKeyExW(p.get(), leaf.c_str(), kView, 0);
}

std::vector<uint8_t> EncodeValue(DWORD type, const std::wstring& data) {
    std::vector<uint8_t> out;
    const auto appendWide = [&](const std::wstring& s) {
        const auto* b = reinterpret_cast<const uint8_t*>(s.c_str());
        out.insert(out.end(), b, b + (s.size() + 1) * sizeof(wchar_t));
    };
    switch (type) {
        case REG_NONE: break;
        case REG_DWORD:
        case REG_QWORD: {
            uint64_t n = 0;
            if (!ParseRegistryNumber(std::wstring(Trim(data)), type == REG_QWORD, n)) {
                throw Failure(ErrorCode::InvalidArguments, L"Not a registry number: " + data);
            }
            out.resize(type == REG_DWORD ? 4 : 8);
            if (type == REG_DWORD) {
                const uint32_t d = static_cast<uint32_t>(n);
                memcpy(out.data(), &d, 4);
            } else {
                memcpy(out.data(), &n, 8);
            }
            break;
        }
        case REG_MULTI_SZ: {
            size_t start = 0;
            while (start <= data.size()) {
                size_t end = data.find(L'\n', start);
                if (end == std::wstring::npos) end = data.size();
                std::wstring line = data.substr(start, end - start);
                if (!line.empty() && line.back() == L'\r') line.pop_back();
                if (!line.empty()) appendWide(line);
                start = end + 1;
            }
            appendWide(L"");
            break;
        }
        default: appendWide(data); break;
    }
    return out;
}

bool KeyIsEmpty(HKEY root, const std::wstring& key) {
    UniqueHkey k;
    if (::RegOpenKeyExW(root, key.c_str(), 0, KEY_READ | kView, k.put()) != ERROR_SUCCESS) return false;
    DWORD subkeys = 0, values = 0;
    if (::RegQueryInfoKeyW(k.get(), nullptr, nullptr, nullptr, &subkeys, nullptr, nullptr, &values, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) return false;
    return subkeys == 0 && values == 0;
}

// "C:\App\app.exe" "%1" --flag: the program always quoted, file/URL placeholders always quoted.
std::wstring CommandLine(const std::wstring& exe, const std::vector<std::wstring>& args, const PlaceholderContext& ctx) {
    const std::wstring rest = QuoteArguments(args, ctx);
    return L"\"" + exe + L"\"" + (rest.empty() ? L"" : L" " + rest);
}

}  // namespace

std::wstring QuoteArguments(const std::vector<std::wstring>& args, const PlaceholderContext& ctx) {
    std::wstring out;
    for (const auto& raw : args) {
        const std::wstring a = ExpandPlaceholders(raw, ctx);
        if (!out.empty()) out += L' ';
        const bool shellToken = a.find(L"%1") != std::wstring::npos || a.find(L"%L") != std::wstring::npos || a.find(L"%V") != std::wstring::npos;
        if (shellToken && a.find(L'"') == std::wstring::npos) {
            out += L"\"" + a + L"\"";  // the shell substitutes a path that may contain spaces
        } else if (!a.empty() && a.find_first_of(L" \t\"") == std::wstring::npos) {
            out += a;
        } else {
            // CommandLineToArgvW quoting
            std::wstring q = L"\"";
            for (size_t i = 0; i < a.size(); ++i) {
                size_t bs = 0;
                while (i < a.size() && a[i] == L'\\') ++bs, ++i;
                if (i == a.size()) {
                    q.append(bs * 2, L'\\');
                    break;
                }
                q.append(a[i] == L'"' ? bs * 2 + 1 : bs, L'\\');
                q.push_back(a[i]);
            }
            out += q + L"\"";
        }
    }
    return out;
}

// ---- registry snapshots --------------------------------------------------------------------------------------------

json::Value SnapshotRegistryKey(HKEY root, const std::wstring& key) {
    UniqueHkey k;
    if (::RegOpenKeyExW(root, key.c_str(), 0, KEY_READ | kView, k.put()) != ERROR_SUCCESS) return json::Value();
    SnapshotBudget budget;
    return SnapshotOpen(k.get(), 0, budget);
}

void RestoreRegistryKey(HKEY root, const std::wstring& key, const json::Value& snapshot) {
    DeleteTreeRaw(root, key);
    if (!snapshot.isObject()) return;
    UniqueHkey k;
    if (::RegCreateKeyExW(root, key.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS | kView, nullptr, k.put(), nullptr) != ERROR_SUCCESS) return;
    RestoreOpen(k.get(), snapshot, 0);
}

json::Value SnapshotRegistryValue(HKEY root, const std::wstring& key, const std::wstring& name) {
    UniqueHkey k;
    if (::RegOpenKeyExW(root, key.c_str(), 0, KEY_QUERY_VALUE | kView, k.put()) != ERROR_SUCCESS) return json::Value();
    DWORD type = 0, size = 0;
    if (::RegQueryValueExW(k.get(), name.c_str(), nullptr, &type, nullptr, &size) != ERROR_SUCCESS || size > (1u << 20)) return json::Value();
    std::vector<uint8_t> data(size);
    if (::RegQueryValueExW(k.get(), name.c_str(), nullptr, &type, data.data(), &size) != ERROR_SUCCESS) return json::Value();
    data.resize(size);
    return ValueToJson(type, data);
}

void RestoreRegistryValue(HKEY root, const std::wstring& key, const std::wstring& name, const json::Value& snapshot) {
    UniqueHkey k;
    if (!snapshot.isObject()) {
        if (::RegOpenKeyExW(root, key.c_str(), 0, KEY_SET_VALUE | kView, k.put()) == ERROR_SUCCESS) ::RegDeleteValueW(k.get(), name.c_str());
        return;
    }
    if (::RegCreateKeyExW(root, key.c_str(), 0, nullptr, 0, KEY_SET_VALUE | kView, nullptr, k.put(), nullptr) != ERROR_SUCCESS) return;
    DWORD type = REG_SZ;
    const std::vector<uint8_t> data = ValueFromJson(snapshot, type);
    ::RegSetValueExW(k.get(), name.c_str(), 0, type, data.empty() ? nullptr : data.data(), static_cast<DWORD>(data.size()));
}

// ---- RegistryWriter ------------------------------------------------------------------------------------------------

bool RegistryWriter::Covered(const std::wstring& key) const {
    const std::wstring k = Lower(key);
    for (const auto& t : trees_) {
        if (k == t || (k.size() > t.size() && k.compare(0, t.size(), t) == 0 && k[t.size()] == L'\\')) return true;
    }
    return false;
}

bool RegistryWriter::KeyExists(const std::wstring& key) const {
    UniqueHkey k;
    return ::RegOpenKeyExW(root_, key.c_str(), 0, KEY_READ | kView, k.put()) == ERROR_SUCCESS;
}

std::wstring RegistryWriter::ReadString(const std::wstring& key, const std::wstring& name) const {
    const json::Value v = SnapshotRegistryValue(root_, key, name);
    const auto type = v["t"].asUint(0);
    return type == REG_SZ || type == REG_EXPAND_SZ ? v["d"].asWide() : std::wstring();
}

void RegistryWriter::ReplaceTree(const std::wstring& key) {
    if (!IsOwnedRegistryKey(key, true)) throw Failure(ErrorCode::RegistryError, L"Refusing to replace registry key " + key);
    if (!Covered(key)) {
        if (journal_) journal_->RegistryKey(key, SnapshotRegistryKey(root_, key));
        trees_.insert(Lower(key));
    }
    DeleteTreeRaw(root_, key);
    UniqueHkey k;
    const LSTATUS st = ::RegCreateKeyExW(root_, key.c_str(), 0, nullptr, 0, KEY_WRITE | kView, nullptr, k.put(), nullptr);
    if (st != ERROR_SUCCESS) throw Failure(st == ERROR_ACCESS_DENIED ? ErrorCode::AccessDenied : ErrorCode::RegistryError, L"Cannot create registry key " + key, st);
}

void RegistryWriter::EnsureKey(const std::wstring& key) {
    if (!IsOwnedRegistryKey(key)) throw Failure(ErrorCode::RegistryError, L"Refusing to write registry key " + key);
    if (KeyExists(key)) return;
    // The first missing ancestor setup may delete as a whole is journaled, so a rollback removes everything created.
    if (!Covered(key)) {
        size_t pos = 0;
        do {
            pos = key.find(L'\\', pos + 1);
            const std::wstring prefix = pos == std::wstring::npos ? key : key.substr(0, pos);
            if (!KeyExists(prefix) && IsCreatableRegistryKey(prefix)) {
                if (journal_) journal_->RegistryKey(prefix, json::Value());
                trees_.insert(Lower(prefix));
                break;
            }
        } while (pos != std::wstring::npos);
    }
    UniqueHkey k;
    const LSTATUS st = ::RegCreateKeyExW(root_, key.c_str(), 0, nullptr, 0, KEY_WRITE | kView, nullptr, k.put(), nullptr);
    if (st != ERROR_SUCCESS) throw Failure(st == ERROR_ACCESS_DENIED ? ErrorCode::AccessDenied : ErrorCode::RegistryError, L"Cannot create registry key " + key, st);
}

void RegistryWriter::SetValue(const std::wstring& key, const std::wstring& name, DWORD type, const std::wstring& data) {
    EnsureKey(key);
    const std::vector<uint8_t> bytes = EncodeValue(type, data);
    if (!Covered(key) && values_.insert(Lower(key) + L"|" + Lower(name)).second && journal_) {
        journal_->RegistryValue(key, name, SnapshotRegistryValue(root_, key, name));
    }
    UniqueHkey k;
    LSTATUS st = ::RegOpenKeyExW(root_, key.c_str(), 0, KEY_SET_VALUE | kView, k.put());
    if (st == ERROR_SUCCESS) st = ::RegSetValueExW(k.get(), name.c_str(), 0, type, bytes.empty() ? nullptr : bytes.data(), static_cast<DWORD>(bytes.size()));
    if (st != ERROR_SUCCESS) throw Failure(st == ERROR_ACCESS_DENIED ? ErrorCode::AccessDenied : ErrorCode::RegistryError, L"Cannot write registry value " + key + L"\\" + name, st);
}

void RegistryWriter::DeleteTree(const std::wstring& key) {
    if (!IsOwnedRegistryKey(key, true) || !KeyExists(key)) return;
    if (!Covered(key)) {
        if (journal_) journal_->RegistryKey(key, SnapshotRegistryKey(root_, key));
        trees_.insert(Lower(key));
    }
    DeleteTreeRaw(root_, key);
    log::Info(L"Removed registry key %s", key.c_str());
}

void RegistryWriter::DeleteValue(const std::wstring& key, const std::wstring& name) {
    if (!IsOwnedRegistryKey(key)) return;
    const json::Value before = SnapshotRegistryValue(root_, key, name);
    if (before.isNull()) return;
    if (!Covered(key) && values_.insert(Lower(key) + L"|" + Lower(name)).second && journal_) journal_->RegistryValue(key, name, before);
    UniqueHkey k;
    if (::RegOpenKeyExW(root_, key.c_str(), 0, KEY_SET_VALUE | kView, k.put()) == ERROR_SUCCESS) ::RegDeleteValueW(k.get(), name.c_str());
}

void RegistryWriter::DeleteKeyIfEmpty(const std::wstring& key) {
    for (std::wstring k = key; !k.empty(); k = Parent(k)) {
        if (EqualsIgnoreCase(k, kRegisteredApplications) || !IsOwnedRegistryKey(k) || !KeyIsEmpty(root_, k)) return;
        if (!Covered(k) && journal_) journal_->RegistryKey(k, SnapshotRegistryKey(root_, k));
        DeleteTreeRaw(root_, k);
    }
}

// ---- registration --------------------------------------------------------------------------------------------------

std::vector<RegistryItem> ApplyRegistration(const RegistrationContext& ctx, RegistryWriter& writer) {
    const Package& pkg = *ctx.pkg;
    const Extensions& e = pkg.ext;
    std::vector<RegistryItem> items;
    const auto selected = [&](const std::string& option) { return OptionSelected(e, option, *ctx.choices); };
    const auto abs = [&](const std::wstring& rel) { return path::Join(ctx.installDir, rel); };
    const auto icon = [&](const std::wstring& rel, const std::wstring& fallback, int index) {
        return abs(rel.empty() ? fallback : rel) + L"," + std::to_wstring(index);
    };
    const auto tree = [&](const std::wstring& key) { items.push_back({key, L"", L"", true, false}); };
    std::vector<std::pair<std::wstring, std::wstring>> capFiles, capUrls;

    for (const auto& a : e.associations) {
        if (!selected(a.ifOption)) continue;
        const std::wstring key = kClasses + a.progId;
        writer.ReplaceTree(key);
        writer.SetString(key, L"", a.description.empty() ? pkg.name : a.description);
        writer.SetString(key + L"\\DefaultIcon", L"", icon(a.icon, a.target, a.iconIndex));
        writer.SetString(key + L"\\shell", L"", L"open");
        writer.SetString(key + L"\\shell\\open\\command", L"", CommandLine(abs(a.target), a.args, ctx.placeholders));
        tree(key);
        for (const auto& ext : a.extensions) {
            const std::wstring extKey = kClasses + ext;
            writer.SetValue(extKey + L"\\OpenWithProgids", a.progId, REG_NONE, L"");
            items.push_back({extKey + L"\\OpenWithProgids", a.progId, L"", false, false});
            // Become the default handler only where no other app is (Windows asks the user otherwise).
            const std::wstring current = writer.ReadString(extKey, L"");
            if (current.empty() || EqualsIgnoreCase(current, a.progId)) {
                writer.SetString(extKey, L"", a.progId);
                items.push_back({extKey, L"", a.progId, false, true});
            }
            if (!a.contentType.empty() && writer.ReadString(extKey, L"Content Type").empty()) {
                writer.SetString(extKey, L"Content Type", a.contentType);
                items.push_back({extKey, L"Content Type", a.contentType, false, true});
            }
            capFiles.emplace_back(ext, a.progId);
        }
        log::Info(L"Registered file type %s", a.progId.c_str());
    }
    for (const auto& p : e.protocols) {
        if (!selected(p.ifOption)) continue;
        const std::wstring key = kClasses + p.scheme;
        // Only ever take over a URL scheme, never a file or shell class that happens to have the same name.
        if (writer.KeyExists(key) && SnapshotRegistryValue(writer.Root(), key, L"URL Protocol").isNull()) {
            log::Warn(L"%s exists and is not a URL scheme; it is left alone", key.c_str());
            continue;
        }
        writer.ReplaceTree(key);
        writer.SetString(key, L"", L"URL:" + (p.description.empty() ? p.scheme : p.description));
        writer.SetString(key, L"URL Protocol", L"");
        writer.SetString(key + L"\\DefaultIcon", L"", icon(p.icon, p.target, p.iconIndex));
        writer.SetString(key + L"\\shell", L"", L"open");
        writer.SetString(key + L"\\shell\\open\\command", L"", CommandLine(abs(p.target), p.args, ctx.placeholders));
        tree(key);
        capUrls.emplace_back(p.scheme, p.scheme);
        log::Info(L"Registered URL scheme %s", p.scheme.c_str());
    }
    if (!capFiles.empty() || !capUrls.empty()) {
        // Default apps: the app appears in Settings with the types and links it can open.
        const std::wstring cap = L"Software\\FluentSetup\\Capabilities\\" + pkg.appId;
        writer.ReplaceTree(cap);
        writer.SetString(cap, L"ApplicationName", pkg.name);
        writer.SetString(cap, L"ApplicationDescription", pkg.description.empty() ? pkg.name : pkg.description);
        for (const auto& [ext, progId] : capFiles) writer.SetString(cap + L"\\FileAssociations", ext, progId);
        for (const auto& [scheme, progId] : capUrls) writer.SetString(cap + L"\\URLAssociations", scheme, progId);
        tree(cap);
        writer.SetString(kRegisteredApplications, pkg.appId, cap);
        items.push_back({kRegisteredApplications, pkg.appId, cap, false, true});
    }
    if (e.appPaths && !pkg.mainExecutable.empty()) {
        const std::wstring key = L"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" + path::FileName(pkg.mainExecutable);
        writer.ReplaceTree(key);
        writer.SetString(key, L"", abs(pkg.mainExecutable));
        writer.SetString(key, L"Path", ctx.installDir);
        tree(key);
    }
    for (const auto& r : e.registry) {
        if (!selected(r.ifOption)) continue;
        const bool existed = writer.KeyExists(r.key);
        writer.EnsureKey(r.key);
        for (const auto& v : r.values) writer.SetValue(r.key, v.name, v.type, ExpandPlaceholders(v.data, ctx.placeholders));
        if (r.uninstall == RegistryEntryDef::Uninstall::Key) {
            tree(r.key);
        } else if (r.uninstall == RegistryEntryDef::Uninstall::Values) {
            for (const auto& v : r.values) items.push_back({r.key, v.name, L"", false, false});
            if (r.values.empty() && !existed) tree(r.key);
        }
    }
    return items;
}

bool SameRegistryItem(const RegistryItem& a, const RegistryItem& b) {
    return a.tree == b.tree && EqualsIgnoreCase(a.key, b.key) && (a.tree || EqualsIgnoreCase(a.value, b.value));
}

void RemoveRegistryItems(const std::vector<RegistryItem>& items, RegistryWriter& writer) {
    for (auto it = items.rbegin(); it != items.rend(); ++it) {
        const RegistryItem& item = *it;
        if (item.tree) {
            writer.DeleteTree(item.key);
            writer.DeleteKeyIfEmpty(Parent(item.key));
            continue;
        }
        if (item.ifEquals && !EqualsIgnoreCase(writer.ReadString(item.key, item.value), item.data)) continue;  // another app took it over
        writer.DeleteValue(item.key, item.value);
        writer.DeleteKeyIfEmpty(item.key);
    }
}

void NotifyAssociationsChanged() { ::SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, nullptr, nullptr); }

// ---- environment ---------------------------------------------------------------------------------------------------

namespace {

constexpr wchar_t kMachineEnvironment[] = L"SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment";

HKEY EnvironmentKey(bool machine, REGSAM access) {
    HKEY key = nullptr;
    if (::RegOpenKeyExW(machine ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER, machine ? kMachineEnvironment : L"Environment", 0, access, &key) != ERROR_SUCCESS) {
        if (machine || ::RegCreateKeyExW(HKEY_CURRENT_USER, L"Environment", 0, nullptr, 0, access, nullptr, &key, nullptr) != ERROR_SUCCESS) return nullptr;
    }
    return key;
}

std::wstring ReadEnvironment(bool machine, const std::wstring& name) {
    UniqueHkey key(EnvironmentKey(machine, KEY_QUERY_VALUE));
    if (!key) return {};
    DWORD type = 0, size = 0;
    if (::RegQueryValueExW(key.get(), name.c_str(), nullptr, &type, nullptr, &size) != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ) ||
        size > 64 * 1024) {
        return {};
    }
    std::wstring value(size / sizeof(wchar_t) + 1, L'\0');
    ::RegQueryValueExW(key.get(), name.c_str(), nullptr, &type, reinterpret_cast<BYTE*>(value.data()), &size);
    value.resize(wcsnlen(value.c_str(), value.size()));
    return value;
}

void Broadcast() {
    DWORD_PTR result = 0;
    ::SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, reinterpret_cast<LPARAM>(L"Environment"), SMTO_ABORTIFHUNG, 3000, &result);
}

}  // namespace

std::vector<EnvironmentItem> ApplyEnvironment(const RegistrationContext& ctx, const std::vector<EnvironmentItem>& previous,
                                              std::vector<std::wstring>& warnings) {
    const bool machine = ctx.scope == Scope::Machine;
    std::vector<EnvironmentItem> written;
    bool changed = false;
    for (const auto& d : ctx.pkg->ext.environment) {
        if (!OptionSelected(ctx.pkg->ext, d.ifOption, *ctx.choices)) continue;
        const std::wstring value = ExpandPlaceholders(d.value, ctx.placeholders);
        UniqueHkey key(EnvironmentKey(machine, KEY_SET_VALUE));
        const bool ok = key && ::RegSetValueExW(key.get(), d.name.c_str(), 0, d.expand ? REG_EXPAND_SZ : REG_SZ,
                                                reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
        if (!ok) {
            warnings.push_back(L"Could not set the environment variable " + d.name);
            continue;
        }
        log::Info(L"Set %s environment variable %s", machine ? L"machine" : L"user", d.name.c_str());
        written.push_back({d.name, value});
        changed = true;
    }
    for (const auto& old : previous) {
        const bool kept = std::any_of(written.begin(), written.end(), [&](const EnvironmentItem& w) { return EqualsIgnoreCase(w.name, old.name); });
        if (kept || ReadEnvironment(machine, old.name) != old.value) continue;
        UniqueHkey key(EnvironmentKey(machine, KEY_SET_VALUE));
        if (key) ::RegDeleteValueW(key.get(), old.name.c_str());
        changed = true;
    }
    if (changed) Broadcast();
    return written;
}

void RemoveEnvironment(const std::vector<EnvironmentItem>& items, bool machine) {
    bool changed = false;
    for (const auto& item : items) {
        if (ReadEnvironment(machine, item.name) != item.value) continue;  // the user changed it: keep
        UniqueHkey key(EnvironmentKey(machine, KEY_SET_VALUE));
        if (key && ::RegDeleteValueW(key.get(), item.name.c_str()) == ERROR_SUCCESS) {
            log::Info(L"Removed environment variable %s", item.name.c_str());
            changed = true;
        }
    }
    if (changed) Broadcast();
}

// ---- services ------------------------------------------------------------------------------------------------------

namespace {
struct ScHandle {
    SC_HANDLE h = nullptr;
    explicit ScHandle(SC_HANDLE handle = nullptr) : h(handle) {}
    ScHandle(const ScHandle&) = delete;
    ScHandle& operator=(const ScHandle&) = delete;
    ~ScHandle() {
        if (h) ::CloseServiceHandle(h);
    }
    explicit operator bool() const { return h != nullptr; }
};
}  // namespace

bool ServiceExists(const std::wstring& name) {
    ScHandle scm(::OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    if (!scm) return false;
    ScHandle svc(::OpenServiceW(scm.h, name.c_str(), SERVICE_QUERY_STATUS));
    return static_cast<bool>(svc);
}

bool ServiceRunning(const std::wstring& name) {
    ScHandle scm(::OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    ScHandle svc(scm ? ::OpenServiceW(scm.h, name.c_str(), SERVICE_QUERY_STATUS) : nullptr);
    SERVICE_STATUS_PROCESS status{};
    DWORD needed = 0;
    return svc && ::QueryServiceStatusEx(svc.h, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &needed) &&
           status.dwCurrentState != SERVICE_STOPPED;
}

bool StopServiceByName(const std::wstring& name, DWORD timeoutMs, bool* wasRunning) {
    if (wasRunning) *wasRunning = false;
    ScHandle scm(::OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    if (!scm) return false;
    ScHandle svc(::OpenServiceW(scm.h, name.c_str(), SERVICE_STOP | SERVICE_QUERY_STATUS));
    if (!svc) return ::GetLastError() == ERROR_SERVICE_DOES_NOT_EXIST;
    SERVICE_STATUS_PROCESS status{};
    DWORD needed = 0;
    if (!::QueryServiceStatusEx(svc.h, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &needed)) return false;
    if (status.dwCurrentState == SERVICE_STOPPED) return true;
    if (wasRunning) *wasRunning = true;
    SERVICE_STATUS plain{};
    if (status.dwCurrentState != SERVICE_STOP_PENDING) ::ControlService(svc.h, SERVICE_CONTROL_STOP, &plain);
    const ULONGLONG deadline = ::GetTickCount64() + timeoutMs;
    while (::GetTickCount64() < deadline) {
        if (!::QueryServiceStatusEx(svc.h, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &needed)) return false;
        if (status.dwCurrentState == SERVICE_STOPPED) {
            log::Info(L"Stopped service %s", name.c_str());
            return true;
        }
        ::Sleep(250);
    }
    log::Warn(L"Service %s did not stop within %lu ms", name.c_str(), timeoutMs);
    return false;
}

bool StartServiceByName(const std::wstring& name, std::wstring* error) {
    ScHandle scm(::OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    ScHandle svc(scm ? ::OpenServiceW(scm.h, name.c_str(), SERVICE_START | SERVICE_QUERY_STATUS) : nullptr);
    if (svc && (::StartServiceW(svc.h, 0, nullptr) || ::GetLastError() == ERROR_SERVICE_ALREADY_RUNNING)) {
        log::Info(L"Started service %s", name.c_str());
        return true;
    }
    if (error) *error = Win32Message(::GetLastError());
    return false;
}

bool InstallService(const ServiceDef& def, const std::wstring& exe, std::wstring& error) {
    ScHandle scm(::OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE));
    if (!scm) {
        error = L"Cannot open the service manager: " + Win32Message(::GetLastError());
        return false;
    }
    PlaceholderContext none;
    const std::wstring binary = CommandLine(exe, def.args, none);
    const wchar_t* account = def.account == ServiceDef::Account::LocalService     ? L"NT AUTHORITY\\LocalService"
                             : def.account == ServiceDef::Account::NetworkService ? L"NT AUTHORITY\\NetworkService"
                                                                                  : L"LocalSystem";
    std::wstring deps;
    for (const auto& d : def.dependencies) deps += d + L'\0';
    deps += L'\0';
    ScHandle svc(::OpenServiceW(scm.h, def.name.c_str(), SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG | SERVICE_START | SERVICE_QUERY_STATUS));
    if (svc) {
        if (!::ChangeServiceConfigW(svc.h, SERVICE_WIN32_OWN_PROCESS, def.startType, SERVICE_ERROR_NORMAL, binary.c_str(), nullptr, nullptr,
                                    deps.c_str(), account, L"", def.displayName.c_str())) {
            error = L"Cannot update service " + def.name + L": " + Win32Message(::GetLastError());
            return false;
        }
        log::Info(L"Updated service %s", def.name.c_str());
    } else {
        svc.h = ::CreateServiceW(scm.h, def.name.c_str(), def.displayName.c_str(), SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG | SERVICE_START | SERVICE_QUERY_STATUS,
                                 SERVICE_WIN32_OWN_PROCESS, def.startType, SERVICE_ERROR_NORMAL, binary.c_str(), nullptr, nullptr, deps.c_str(), account, L"");
        if (!svc) {
            error = L"Cannot create service " + def.name + L": " + Win32Message(::GetLastError());
            return false;
        }
        log::Info(L"Created service %s", def.name.c_str());
    }
    std::wstring description = def.description;
    SERVICE_DESCRIPTIONW desc{description.empty() ? nullptr : description.data()};
    ::ChangeServiceConfig2W(svc.h, SERVICE_CONFIG_DESCRIPTION, &desc);
    SERVICE_DELAYED_AUTO_START_INFO delayed{def.startType == SERVICE_AUTO_START && def.delayed};
    ::ChangeServiceConfig2W(svc.h, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &delayed);
    SC_ACTION actions[3] = {{SC_ACTION_RESTART, 5000}, {SC_ACTION_RESTART, 30000}, {SC_ACTION_RESTART, 60000}};
    SERVICE_FAILURE_ACTIONSW failure{};
    failure.dwResetPeriod = 86400;
    failure.cActions = def.restartOnFailure ? 3 : 0;
    failure.lpsaActions = def.restartOnFailure ? actions : nullptr;
    ::ChangeServiceConfig2W(svc.h, SERVICE_CONFIG_FAILURE_ACTIONS, &failure);
    return true;
}

bool DeleteServiceByName(const std::wstring& name) {
    StopServiceByName(name, 30000);
    ScHandle scm(::OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    ScHandle svc(scm ? ::OpenServiceW(scm.h, name.c_str(), DELETE) : nullptr);
    if (!svc) return ::GetLastError() == ERROR_SERVICE_DOES_NOT_EXIST;
    const bool ok = ::DeleteService(svc.h) || ::GetLastError() == ERROR_SERVICE_MARKED_FOR_DELETE;
    if (ok) log::Info(L"Deleted service %s", name.c_str());
    return ok;
}

// ---- firewall ------------------------------------------------------------------------------------------------------

namespace {
template <typename T>
struct Com {
    T* p = nullptr;
    Com() = default;
    Com(const Com&) = delete;
    Com& operator=(const Com&) = delete;
    ~Com() {
        if (p) p->Release();
    }
    T** put() { return &p; }
    T* operator->() const { return p; }
    explicit operator bool() const { return p != nullptr; }
};
struct Bstr {
    BSTR b;
    explicit Bstr(const std::wstring& s) : b(::SysAllocStringLen(s.data(), static_cast<UINT>(s.size()))) {}
    Bstr(const Bstr&) = delete;
    Bstr& operator=(const Bstr&) = delete;
    ~Bstr() { ::SysFreeString(b); }
};

bool OpenRules(Com<INetFwRules>& rules) {
    Com<INetFwPolicy2> policy;
    if (FAILED(::CoCreateInstance(__uuidof(NetFwPolicy2), nullptr, CLSCTX_INPROC_SERVER, __uuidof(INetFwPolicy2), reinterpret_cast<void**>(policy.put())))) return false;
    return SUCCEEDED(policy->get_Rules(rules.put()));
}

// Names of the rules in `group`, and names that other rules (not in the group) use as well.
void ScanRules(INetFwRules* rules, const std::wstring& group, std::vector<std::wstring>& ours, std::vector<std::wstring>& shared) {
    Com<IUnknown> unknown;
    if (FAILED(rules->get__NewEnum(unknown.put()))) return;
    Com<IEnumVARIANT> e;
    if (FAILED(unknown->QueryInterface(__uuidof(IEnumVARIANT), reinterpret_cast<void**>(e.put())))) return;
    std::vector<std::pair<std::wstring, bool>> all;
    VARIANT item;
    ::VariantInit(&item);
    ULONG fetched = 0;
    while (e->Next(1, &item, &fetched) == S_OK && fetched == 1) {
        if (item.vt == VT_DISPATCH && item.pdispVal) {
            Com<INetFwRule> rule;
            if (SUCCEEDED(item.pdispVal->QueryInterface(__uuidof(INetFwRule), reinterpret_cast<void**>(rule.put())))) {
                BSTR name = nullptr, grouping = nullptr;
                rule->get_Name(&name);
                rule->get_Grouping(&grouping);
                all.emplace_back(name ? name : L"", grouping && group == grouping);
                ::SysFreeString(name);
                ::SysFreeString(grouping);
            }
        }
        ::VariantClear(&item);
    }
    for (const auto& [name, mine] : all) {
        if (mine) {
            if (std::find(ours.begin(), ours.end(), name) == ours.end()) ours.push_back(name);
        } else if (std::find(shared.begin(), shared.end(), name) == shared.end()) {
            shared.push_back(name);
        }
    }
}

void RemoveGroup(INetFwRules* rules, const std::wstring& group) {
    std::vector<std::wstring> ours, others;
    ScanRules(rules, group, ours, others);
    for (const auto& name : ours) {
        // INetFwRules removes by name: never touch a name another app's rule uses too.
        if (std::find(others.begin(), others.end(), name) != others.end()) {
            log::Warn(L"Firewall rule %s is not removed: another rule has the same name", name.c_str());
            continue;
        }
        // Remove deletes one rule per call; repeat while rules of that name are left.
        for (int i = 0; i < 64 && SUCCEEDED(rules->Remove(Bstr(name).b)); ++i) {
            std::vector<std::wstring> left, unused;
            ScanRules(rules, group, left, unused);
            if (std::find(left.begin(), left.end(), name) == left.end()) break;
        }
        log::Info(L"Removed firewall rule %s", name.c_str());
    }
}
}  // namespace

bool ReplaceFirewallRules(const std::wstring& group, const std::vector<FirewallRule>& list, std::wstring& error) {
    Com<INetFwRules> rules;
    if (!OpenRules(rules)) {
        error = L"Windows Firewall is not available";
        return false;
    }
    RemoveGroup(rules.p, group);
    for (const auto& r : list) {
        Com<INetFwRule> rule;
        if (FAILED(::CoCreateInstance(__uuidof(NetFwRule), nullptr, CLSCTX_INPROC_SERVER, __uuidof(INetFwRule), reinterpret_cast<void**>(rule.put())))) {
            error = L"Cannot create a firewall rule";
            return false;
        }
        rule->put_Name(Bstr(r.def.name).b);
        rule->put_ApplicationName(Bstr(r.program).b);
        rule->put_Protocol(r.def.protocol);
        if (!r.def.localPorts.empty()) rule->put_LocalPorts(Bstr(r.def.localPorts).b);
        rule->put_Direction(r.def.inbound ? NET_FW_RULE_DIR_IN : NET_FW_RULE_DIR_OUT);
        rule->put_Profiles(r.def.profiles);
        rule->put_Action(NET_FW_ACTION_ALLOW);
        rule->put_Enabled(VARIANT_TRUE);
        rule->put_Grouping(Bstr(group).b);
        const HRESULT hr = rules->Add(rule.p);
        if (FAILED(hr)) {
            error = Printf(L"Cannot add firewall rule %s (0x%08lX)", r.def.name.c_str(), static_cast<unsigned long>(hr));
            return false;
        }
        log::Info(L"Added firewall rule %s", r.def.name.c_str());
    }
    return true;
}

void RemoveFirewallRules(const std::wstring& group) {
    Com<INetFwRules> rules;
    if (OpenRules(rules)) RemoveGroup(rules.p, group);
}

// ---- config files --------------------------------------------------------------------------------------------------

namespace {
void SetJsonPath(json::Value& node, const std::vector<std::string>& parts, size_t index, json::Value value) {
    if (index + 1 == parts.size()) {
        node.set(parts[index], std::move(value));
        return;
    }
    json::Value child = node[parts[index]];
    if (!child.isObject()) child = json::Value::MakeObject();
    SetJsonPath(child, parts, index + 1, std::move(value));
    node.set(parts[index], std::move(child));
}
}  // namespace

void WriteConfigFile(const ConfigFileDef& def, const std::wstring& file, const PlaceholderContext& ctx) {
    if (fs::HasLinkInPath(path::Parent(file)) || fs::IsLinkLike(file)) throw Failure(ErrorCode::UnsafePath, L"Config file goes through a link: " + file);
    fs::CreateDirectories(path::Parent(file));
    if (def.ini) {
        if (!fs::Exists(file)) {
            // A UTF-16 file makes WritePrivateProfileString keep any language.
            static constexpr uint8_t kBom[] = {0xFF, 0xFE};
            fs::WriteAllAtomic(file, {kBom, sizeof(kBom)});
        }
        for (const auto& v : def.values) {
            const std::wstring value = ExpandPlaceholders(v.value, ctx);
            if (!::WritePrivateProfileStringW(v.section.c_str(), v.key.c_str(), value.c_str(), file.c_str())) {
                ThrowWin32(L"Cannot write " + file);
            }
        }
        log::Info(L"Wrote %zu value(s) to %s", def.values.size(), file.c_str());
        return;
    }
    json::Value root = json::Value::MakeObject();
    if (fs::Exists(file)) {
        std::vector<uint8_t> bytes = fs::ReadAll(file, 16u << 20);
        std::string_view text(reinterpret_cast<const char*>(bytes.data()), bytes.size());
        if (text.size() >= 3 && static_cast<uint8_t>(text[0]) == 0xEF && static_cast<uint8_t>(text[1]) == 0xBB && static_cast<uint8_t>(text[2]) == 0xBF) text.remove_prefix(3);
        if (!Trim(Utf8ToWide(text)).empty() && (!json::Parse(text, root) || !root.isObject())) {
            throw Failure(ErrorCode::Failed, L"The config file is not a JSON object: " + file);
        }
    }
    for (const auto& v : def.values) {
        const std::wstring text = ExpandPlaceholders(v.value, ctx);
        json::Value value;
        if (v.type == ConfigValueDef::Type::Number) {
            wchar_t* end = nullptr;
            const double n = wcstod(text.c_str(), &end);
            if (text.empty() || !end || *end != L'\0') throw Failure(ErrorCode::InvalidArguments, L"Not a number for " + v.key + L": " + text);
            const int64_t asInt = static_cast<int64_t>(n);
            value = static_cast<double>(asInt) == n ? json::Value(asInt) : json::Value(n);
        } else if (v.type == ConfigValueDef::Type::Bool) {
            const std::wstring t = ToLower(text);
            if (t != L"true" && t != L"false" && t != L"1" && t != L"0" && t != L"yes" && t != L"no") throw Failure(ErrorCode::InvalidArguments, L"Not a boolean for " + v.key + L": " + text);
            value = json::Value(t == L"true" || t == L"1" || t == L"yes");
        } else {
            value = json::Value(text);
        }
        std::vector<std::string> parts;
        const std::string key = WideToUtf8(v.key);
        for (size_t start = 0; start <= key.size();) {
            size_t end = key.find('.', start);
            if (end == std::string::npos) end = key.size();
            parts.push_back(key.substr(start, end - start));
            start = end + 1;
        }
        SetJsonPath(root, parts, 0, std::move(value));
    }
    const std::string out = json::Serialize(root, 2) + "\n";
    fs::WriteAllAtomic(file, {reinterpret_cast<const uint8_t*>(out.data()), out.size()});
    log::Info(L"Wrote %zu value(s) to %s", def.values.size(), file.c_str());
}

// ---- Restart Manager -----------------------------------------------------------------------------------------------

RestartManagerSession::~RestartManagerSession() {
    if (active_) ::RmEndSession(session_);
}

bool RestartManagerSession::Start(const std::vector<std::wstring>& files) {
    if (files.empty()) return false;
    WCHAR key[CCH_RM_SESSION_KEY + 1] = {};
    if (::RmStartSession(&session_, 0, key) != ERROR_SUCCESS) return false;
    active_ = true;
    std::vector<LPCWSTR> list;
    list.reserve(files.size());
    for (const auto& f : files) list.push_back(f.c_str());
    for (size_t i = 0; i < list.size(); i += 1000) {
        const UINT n = static_cast<UINT>(std::min<size_t>(1000, list.size() - i));
        if (::RmRegisterResources(session_, n, list.data() + i, 0, nullptr, 0, nullptr) != ERROR_SUCCESS) return false;
    }
    return true;
}

std::vector<std::wstring> RestartManagerSession::Apps() const {
    std::vector<std::wstring> names;
    if (!active_) return names;
    UINT needed = 0, count = 0;
    DWORD reasons = 0;
    std::vector<RM_PROCESS_INFO> info;
    DWORD rc = ERROR_MORE_DATA;
    for (int attempt = 0; attempt < 3 && rc == ERROR_MORE_DATA; ++attempt) {
        info.resize(needed + 8);
        count = static_cast<UINT>(info.size());
        rc = ::RmGetList(session_, &needed, &count, info.data(), &reasons);
    }
    if (rc != ERROR_SUCCESS) return names;
    for (UINT i = 0; i < count; ++i) {
        if (info[i].Process.dwProcessId == ::GetCurrentProcessId()) continue;
        if (info[i].AppStatus & (RmStatusStopped | RmStatusStoppedOther)) continue;
        std::wstring name = info[i].strAppName;
        if (!name.empty() && std::none_of(names.begin(), names.end(), [&](const std::wstring& n) { return EqualsIgnoreCase(n, name); })) {
            names.push_back(std::move(name));
        }
    }
    return names;
}

std::vector<std::wstring> RestartManagerSession::Shutdown() {
    if (!active_) return {};
    const auto running = Apps();
    if (running.empty()) return {};
    std::wstring list;
    for (const auto& n : running) list += (list.empty() ? L"" : L", ") + n;
    log::Info(L"Closing programs that use the app's files: %s", list.c_str());
    shutDown_ = true;
    if (::RmShutdown(session_, 0, nullptr) != ERROR_SUCCESS) ::RmShutdown(session_, RmForceShutdown, nullptr);
    return Apps();
}

void RestartManagerSession::Restart() {
    if (!active_) return;
    if (shutDown_) {
        const DWORD rc = ::RmRestart(session_, 0, nullptr);
        log::Info(L"Restart Manager restarted the closed programs (result %lu)", rc);
    }
    ::RmEndSession(session_);
    active_ = false;
}

}  // namespace fsetup::install
