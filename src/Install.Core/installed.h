// What is installed already: the Windows "Apps & features" (ARP) entry, the install record and the pending-operation
// marker used for crash recovery.
#pragma once

#include "Common/hash.h"
#include "Common/json.h"
#include "Install.Core/package.h"

#include <map>
#include <optional>
#include <string>
#include <vector>

namespace fsetup::install {

struct InstalledProduct {
    Scope scope = Scope::User;
    std::wstring appId, name, version, publisher, installDir, recordSha256;
    std::string variant;  // package variant that is installed ("" = none)
};

struct RecordFile {
    std::wstring path;  // relative
    uint64_t size = 0;
    Sha256Digest sha256{};
    bool preserve = false;
};

struct InstallRecord {
    std::wstring appId, name, version, publisher, mainExecutable;
    Scope scope = Scope::User;
    std::wstring installDir;
    std::vector<RecordFile> files;
    std::vector<std::wstring> directories;  // relative, every directory setup created or populated
    std::vector<std::wstring> shortcuts;    // absolute .lnk paths
    bool createdRoot = false;
    std::map<std::string, bool> choices;  // package-defined options as chosen at install time (for beforeUninstall actions)
};

std::wstring ArpKeyPath(const std::wstring& appId);
HKEY RootFor(Scope scope) noexcept;

// Reads the ARP entry written by FluentSetup for `appId` in one hive. Entries without our marker are ignored.
std::optional<InstalledProduct> QueryInstalled(const std::wstring& appId, Scope scope);
// Looks in both hives (per-user first).
std::optional<InstalledProduct> FindInstalled(const std::wstring& appId);
// True if an ARP key with this id exists but was not written by FluentSetup (we must not take it over).
bool ForeignArpEntryExists(const std::wstring& appId, Scope scope);
// InstallLocation and DisplayVersion of such a foreign entry (used when a package adopts an older installer's install).
std::optional<InstalledProduct> QueryForeign(const std::wstring& appId, Scope scope);
// Removes an ARP key written by another installer (legacy product names). Keys carrying our marker are never touched.
bool DeleteForeignArpEntry(const std::wstring& keyName, Scope scope);

json::Value RecordToJson(const InstallRecord& r);
// Validates every field; relative paths must be safe, shortcuts must live in Start menu / desktop folders of the scope.
InstallRecord RecordFromJson(const json::Value& v);
// Loads <installDir>\uninstall.json and checks its SHA-256 against the value stored in the ARP key.
// Throws Failure(NotInstalled) if the record is missing, altered or inconsistent with the registry.
InstallRecord LoadVerifiedRecord(const InstalledProduct& product);

// Folders where shortcuts of a given scope may be created (and therefore deleted).
std::vector<std::wstring> ShortcutFolders(Scope scope);
bool IsAllowedShortcutPath(const std::wstring& p, Scope scope);

// ARP snapshot: every value of the key as JSON (null if the key does not exist). Used for rollback.
json::Value SnapshotArp(const std::wstring& appId, Scope scope);
void RestoreArp(const std::wstring& appId, Scope scope, const json::Value& snapshot);

struct ArpEntry {
    std::wstring displayName, displayVersion, publisher, displayIcon, installLocation, uninstallString,
        quietUninstallString, urlInfoAbout, comments, recordSha256, variant;
    uint32_t estimatedSizeKb = 0;
    uint32_t versionMajor = 0, versionMinor = 0;
};
void WriteArp(const std::wstring& appId, Scope scope, const ArpEntry& e);  // throws Failure(RegistryError)
void DeleteArp(const std::wstring& appId, Scope scope);

// Pending-operation marker (Software\FluentSetup\Pending\<appId>) pointing at the journal of an unfinished operation.
void SetPending(const std::wstring& appId, Scope scope, const std::wstring& installDir);
std::optional<std::wstring> GetPending(const std::wstring& appId, Scope scope);
void ClearPending(const std::wstring& appId, Scope scope);

}  // namespace fsetup::install
