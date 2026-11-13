#include "Install.Core/journal.h"

#include "Common/error.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Install.Core/installed.h"
#include "Install.Core/integration.h"

#include <algorithm>
#include <functional>

namespace fsetup::install {

Journal::Journal(std::wstring file) : file_(std::move(file)) {
    handle_.reset(::CreateFileW(path::Long(file_).c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                                FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!handle_) ThrowWin32(L"Cannot create journal " + file_);
}

void Journal::Append(const json::Value& entry, bool flush) {
    entries_.push_back(entry);
    std::string line = json::Serialize(entry);
    line.push_back('\n');
    DWORD written = 0;
    if (!::WriteFile(handle_.get(), line.data(), static_cast<DWORD>(line.size()), &written, nullptr) ||
        written != line.size()) {
        ThrowWin32(L"Cannot write journal");
    }
    // Process crashes cannot lose written data (it is in the cache manager); power loss can lose the tail. Flushing
    // periodically and at every non-file step bounds that window without an fsync per file.
    if (flush || ++unflushed_ >= 64) Flush();
}

void Journal::Flush() {
    ::FlushFileBuffers(handle_.get());
    unflushed_ = 0;
}

void Journal::Begin(const std::wstring& mode, const std::wstring& installDir, Scope scope, const std::wstring& appId) {
    json::Value e;
    e.set("op", "begin");
    e.set("mode", mode);
    e.set("dir", installDir);
    e.set("scope", ScopeName(scope));
    e.set("appId", appId);
    Append(e, true);
}

void Journal::Mkdir(const std::wstring& dir) {
    json::Value e;
    e.set("op", "mkdir");
    e.set("path", dir);
    Append(e, false);
}

void Journal::Backup(const std::wstring& from, const std::wstring& to) {
    json::Value e;
    e.set("op", "backup");
    e.set("from", from);
    e.set("to", to);
    Append(e, false);
}

void Journal::Place(const std::wstring& file) {
    json::Value e;
    e.set("op", "place");
    e.set("path", file);
    Append(e, false);
}

void Journal::Shortcut(const std::wstring& link, const std::wstring& backup) {
    json::Value e;
    e.set("op", "shortcut");
    e.set("path", link);
    if (!backup.empty()) e.set("backup", backup);
    Append(e, true);
}

void Journal::Registry(const json::Value& snapshot) {
    json::Value e;
    e.set("op", "reg");
    e.set("snapshot", snapshot);
    Append(e, true);
}

void Journal::RegistryKey(const std::wstring& key, const json::Value& snapshot) {
    json::Value e;
    e.set("op", "regkey");
    e.set("key", key);
    e.set("snapshot", snapshot);
    Append(e, true);
}

void Journal::RegistryValue(const std::wstring& key, const std::wstring& name, const json::Value& snapshot) {
    json::Value e;
    e.set("op", "regvalue");
    e.set("key", key);
    e.set("name", name);
    e.set("snapshot", snapshot);
    Append(e, true);
}

void Journal::ServiceStopped(const std::wstring& name) {
    json::Value e;
    e.set("op", "svcstop");
    e.set("name", name);
    Append(e, true);
}

void Journal::Commit() {
    json::Value e;
    e.set("op", "commit");
    Append(e, true);
}

std::vector<json::Value> ReadJournal(const std::wstring& file) {
    std::vector<json::Value> entries;
    std::vector<uint8_t> bytes;
    try {
        bytes = fs::ReadAll(file, 512ull * 1024 * 1024);
    } catch (const Failure&) {
        return entries;
    }
    std::string_view text(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    size_t start = 0;
    while (start < text.size()) {
        size_t end = text.find('\n', start);
        if (end == std::string_view::npos) break;  // a torn final line is ignored
        json::Value v;
        if (json::Parse(text.substr(start, end - start), v) && v.isObject()) entries.push_back(std::move(v));
        start = end + 1;
    }
    return entries;
}

bool JournalCommitted(const std::vector<json::Value>& entries) {
    for (const auto& e : entries) {
        if (e["op"].asString() == "commit") return true;
    }
    return false;
}

RollbackStats Rollback(const std::vector<json::Value>& entries, const std::wstring& installDir, Scope scope,
                       const std::wstring& appId) {
    RollbackStats stats;
    const std::wstring workDir = path::Join(installDir, kWorkDirName);
    const auto insideDir = [&](const std::wstring& p) {
        std::wstring n;
        return path::ValidateAbsoluteSyntax(p, n) == path::Problem::None && path::IsSameOrUnder(n, installDir);
    };
    const json::Value* current = nullptr;
    const auto note = [&](bool ok, const wchar_t* what, const std::wstring& p) {
        if (ok) {
            ++stats.undone;
        } else {
            ++stats.failed;
            stats.remaining.push_back(*current);  // retried by the next run; nothing else is repeated
            log::Warn(L"Rollback: could not undo %s %s (error %lu)", what, p.c_str(), ::GetLastError());
        }
    };
    // Files the shell or a virus scanner has just opened (a new shortcut, a placed file) are released within moments.
    const auto retry = [](const std::function<DWORD()>& step) {
        DWORD e = step();
        for (int i = 0; i < 30 && (e == ERROR_ACCESS_DENIED || e == ERROR_SHARING_VIOLATION || e == ERROR_LOCK_VIOLATION); ++i) {
            ::Sleep(100);
            e = step();
        }
        if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND) e = 0;  // nothing left to undo
        ::SetLastError(e);
        return e;
    };

    for (auto it = entries.rbegin(); it != entries.rend(); ++it) {
        const json::Value& e = *it;
        current = &e;
        const std::string& op = e["op"].asString();
        if (op == "place") {
            const std::wstring p = e["path"].asWide();
            if (!insideDir(p)) continue;
            note(retry([&] { return fs::DeleteFileForce(p); }) == 0, L"place", p);
        } else if (op == "backup") {
            const std::wstring from = e["from"].asWide(), to = e["to"].asWide();
            const bool shortcutOrigin = IsAllowedShortcutPath(from, scope);
            if ((!insideDir(from) && !shortcutOrigin) || !path::IsSameOrUnder(to, workDir)) continue;
            if (!fs::Exists(to)) continue;  // the move never happened
            note(retry([&] { return fs::Move(to, from, true); }) == 0, L"backup", from);
        } else if (op == "mkdir") {
            const std::wstring p = e["path"].asWide();
            if (!insideDir(p)) continue;
            if (fs::IsDirectory(p) && fs::IsDirectoryEmpty(p)) note(::RemoveDirectoryW(path::Long(p).c_str()) != FALSE, L"mkdir", p);
        } else if (op == "shortcut") {
            const std::wstring p = e["path"].asWide();
            if (!IsAllowedShortcutPath(p, scope)) continue;
            const std::wstring backup = e["backup"].asWide();
            if (!backup.empty() && path::IsSameOrUnder(backup, workDir) && fs::Exists(backup)) {
                note(retry([&] { return fs::Move(backup, p, true); }) == 0, L"shortcut", p);
            } else {
                const bool ok = retry([&] { return fs::DeleteFileForce(p); }) == 0;
                if (ok) RemoveEmptyShortcutFolder(p, scope);  // a Start menu folder created for it
                note(ok, L"shortcut", p);
            }
        } else if (op == "reg") {
            RestoreArp(appId, scope, e["snapshot"]);
            ++stats.undone;
        } else if (op == "regkey") {
            const std::wstring key = e["key"].asWide();
            // Owned trees are restored; an extension key may only be deleted again (it did not exist before).
            if (!IsOwnedRegistryKey(key, true) && !(e["snapshot"].isNull() && IsCreatableRegistryKey(key))) continue;
            RestoreRegistryKey(RootFor(scope), key, e["snapshot"]);
            log::Info(L"Rollback: registry key %s %s", key.c_str(), e["snapshot"].isNull() ? L"removed" : L"restored");
            ++stats.undone;
        } else if (op == "regvalue") {
            const std::wstring key = e["key"].asWide();
            if (!IsOwnedRegistryKey(key)) continue;
            const std::wstring name = e["name"].asWide().substr(0, 256);
            RestoreRegistryValue(RootFor(scope), key, name, e["snapshot"]);
            log::Info(L"Rollback: registry value %s [%s] %s", key.c_str(), name.c_str(), e["snapshot"].isNull() ? L"removed" : L"restored");
            ++stats.undone;
        } else if (op == "svcstop") {
            const std::wstring name = e["name"].asWide();
            if (scope != Scope::Machine || !IsServiceName(name)) continue;
            note(StartServiceByName(name), L"service stop", name);
        }
    }
    std::reverse(stats.remaining.begin(), stats.remaining.end());
    if (!stats.remaining.empty() && !entries.empty()) stats.remaining.insert(stats.remaining.begin(), entries.front());  // "begin"
    return stats;
}

void RewriteJournal(const std::wstring& file, const std::vector<json::Value>& entries) {
    std::string text;
    for (const auto& e : entries) text += json::Serialize(e) + "\n";
    try {
        fs::WriteAllAtomic(file, {reinterpret_cast<const uint8_t*>(text.data()), text.size()});
    } catch (const Failure& f) {
        log::Warn(L"Could not rewrite the journal: %s", f.detail().c_str());
    }
}

}  // namespace fsetup::install
