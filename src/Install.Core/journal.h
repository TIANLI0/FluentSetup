// Write-ahead journal of every side effect of an install/uninstall, so that a failed, cancelled or interrupted
// (crash, power loss, reboot) operation can be rolled back. Stored as JSON lines in <installDir>\.fsetup\journal.jsonl.
//
// Rules:
//  * an entry is appended *before* the action it describes;
//  * rollback walks the entries newest-first and tolerates actions that never happened;
//  * paths in the journal are only acted on if they are inside the install directory or a shortcut folder of the
//    scope, and registry entries only for the product's own ARP key.
#pragma once

#include "Common/json.h"
#include "Common/win.h"
#include "Install.Core/package.h"

#include <string>
#include <vector>

namespace fsetup::install {

class Journal {
public:
    explicit Journal(std::wstring file);
    Journal(const Journal&) = delete;
    Journal& operator=(const Journal&) = delete;

    void Begin(const std::wstring& mode, const std::wstring& installDir, Scope scope, const std::wstring& appId);
    void Mkdir(const std::wstring& dir);
    void Backup(const std::wstring& from, const std::wstring& to);
    void Place(const std::wstring& file);
    void Shortcut(const std::wstring& link, const std::wstring& backup);
    void Registry(const json::Value& snapshot);
    void Commit();
    void Flush();
    [[nodiscard]] const std::wstring& FilePath() const noexcept { return file_; }
    // In-memory copy of everything appended (the file is opened exclusively for writing).
    [[nodiscard]] std::vector<json::Value> Entries() const { return entries_; }

private:
    void Append(const json::Value& entry, bool flush);

    std::wstring file_;
    UniqueHandle handle_;
    unsigned unflushed_ = 0;
    std::vector<json::Value> entries_;
};

std::vector<json::Value> ReadJournal(const std::wstring& file);
bool JournalCommitted(const std::vector<json::Value>& entries);

struct RollbackStats {
    unsigned undone = 0;
    unsigned failed = 0;
};
// Undoes the recorded operations newest-first (best effort, never throws).
RollbackStats Rollback(const std::vector<json::Value>& entries, const std::wstring& installDir, Scope scope,
                       const std::wstring& appId);

}  // namespace fsetup::install
