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
    std::wstring innoTasks;  // adopted Inno Setup install: its "Inno Setup: Selected Tasks" (e.g. "desktopicon,autostart")
};

struct RecordFile {
    std::wstring path;  // relative
    uint64_t size = 0;
    Sha256Digest sha256{};
    bool preserve = false;
};

// Something the installer wrote into the registry of its scope's hive (HKCU per-user, HKLM per-machine), and how
// uninstall removes it: the whole key (`tree`), one value (`value`), or a default value only while it still holds
// `data` (an extension's default ProgID).
struct RegistryItem {
    std::wstring key;    // below the hive, e.g. Software\Classes\Contoso.Doc
    std::wstring value;  // value name ("" = default value); ignored when tree
    std::wstring data;   // ifEquals: only removed while the value still equals this
    bool tree = false;   // remove the whole key
    bool ifEquals = false;
};

struct EnvironmentItem {
    std::wstring name, value;  // removed on uninstall while the variable still holds `value`
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
    std::map<std::string, std::wstring> inputs;  // remembered input values (never passwords), pre-filled on updates
    std::wstring sourcePackage;                  // the Setup.exe that installed this version (for Repair), may be gone
    std::wstring sourcePackageSha256;
    std::vector<RegistryItem> registry;          // registry keys/values written (removed on uninstall / when gone)
    std::vector<EnvironmentItem> environment;    // environment variables written
    std::vector<std::wstring> services;          // services created
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
// A .lnk directly in one of ShortcutFolders(scope), or one folder level below the Start menu Programs folder
// (install.startMenuFolder, e.g. "Programs\App\App.lnk").
bool IsAllowedShortcutPath(const std::wstring& p, Scope scope);
// After a shortcut in a Start menu sub-folder was removed: removes that sub-folder if it is now empty.
void RemoveEmptyShortcutFolder(const std::wstring& linkPath, Scope scope);

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
