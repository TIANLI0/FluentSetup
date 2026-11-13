// System integration beyond files (1.2): registry entries with journal snapshots, file associations, URL protocols,
// App Paths, environment variables, Windows services, firewall rules, config files and Windows Restart Manager.
// Everything is confined to what the package declares and validated again here; see docs/project-file.md.
#pragma once

#include "Common/json.h"
#include "Common/win.h"
#include "Install.Core/installed.h"
#include "Install.Core/package.h"

#include <set>
#include <string>
#include <vector>

namespace fsetup::install {

class Journal;

// ---- registry ---------------------------------------------------------------------------------------------------

// Whole key tree as JSON (null if the key does not exist), bounded in depth and size. Throws Failure(RegistryError)
// for a tree too large to restore safely.
json::Value SnapshotRegistryKey(HKEY root, const std::wstring& key);
// Deletes the key tree and recreates it from a snapshot (nothing if the snapshot is null). Best effort.
void RestoreRegistryKey(HKEY root, const std::wstring& key, const json::Value& snapshot);
// One value as {t, d} (null if missing).
json::Value SnapshotRegistryValue(HKEY root, const std::wstring& key, const std::wstring& name);
void RestoreRegistryValue(HKEY root, const std::wstring& key, const std::wstring& name, const json::Value& snapshot);

// Writes registry values of the scope's hive and journals what it is about to change, so a rollback restores the
// previous state: keys it creates are removed again, values it changes get their old data back, trees it replaces
// (ProgIDs, schemes, App Paths, capabilities) are restored as they were. A null journal writes without journaling.
class RegistryWriter {
public:
    RegistryWriter(HKEY root, Journal* journal) : root_(root), journal_(journal) {}
    // Deletes and recreates a key setup owns as a whole (the previous tree is journaled).
    void ReplaceTree(const std::wstring& key);
    // Makes sure the key exists (a newly created key, or its first missing owned ancestor, is journaled for deletion).
    void EnsureKey(const std::wstring& key);
    void SetValue(const std::wstring& key, const std::wstring& name, DWORD type, const std::wstring& data);
    void SetString(const std::wstring& key, const std::wstring& name, const std::wstring& value) { SetValue(key, name, REG_SZ, value); }
    void DeleteTree(const std::wstring& key);
    void DeleteValue(const std::wstring& key, const std::wstring& name);
    // Removes the key, and then its parents, while they are empty and setup may remove them.
    void DeleteKeyIfEmpty(const std::wstring& key);
    [[nodiscard]] std::wstring ReadString(const std::wstring& key, const std::wstring& name) const;
    [[nodiscard]] bool KeyExists(const std::wstring& key) const;
    [[nodiscard]] HKEY Root() const noexcept { return root_; }

private:
    bool Covered(const std::wstring& key) const;  // inside a tree already journaled as a whole
    HKEY root_;
    Journal* journal_;
    std::set<std::wstring> trees_;   // lower-case keys journaled as trees (replaced or created)
    std::set<std::wstring> values_;  // lower-case "key|name" journaled as values
};

struct RegistrationContext {
    const Package* pkg = nullptr;
    std::wstring installDir;
    Scope scope = Scope::User;
    const std::map<std::string, bool>* choices = nullptr;
    PlaceholderContext placeholders;
};

// File associations, URL protocols, App Paths and the package's registry entries (those whose option is selected).
// Returns what uninstall has to remove again.
std::vector<RegistryItem> ApplyRegistration(const RegistrationContext& ctx, RegistryWriter& writer);
// Removes recorded items (uninstall, or items an update no longer writes).
void RemoveRegistryItems(const std::vector<RegistryItem>& items, RegistryWriter& writer);
bool SameRegistryItem(const RegistryItem& a, const RegistryItem& b);
// Arguments for a command line or shortcut: placeholders expanded, quoted for CommandLineToArgvW, "%1" always quoted.
std::wstring QuoteArguments(const std::vector<std::wstring>& args, const PlaceholderContext& ctx);
// Explorer re-reads file associations.
void NotifyAssociationsChanged();

// ---- environment ------------------------------------------------------------------------------------------------

// Writes the variables whose option is selected (per-user or machine environment), removes variables of the previous
// version that are gone while they still hold the old value, and tells running programs. Returns what was written.
std::vector<EnvironmentItem> ApplyEnvironment(const RegistrationContext& ctx, const std::vector<EnvironmentItem>& previous,
                                              std::vector<std::wstring>& warnings);
void RemoveEnvironment(const std::vector<EnvironmentItem>& items, bool machine);

// ---- services ---------------------------------------------------------------------------------------------------

// Stops a service and waits up to `timeoutMs`; `wasRunning` tells whether it ran. False if it could not be stopped.
bool StopServiceByName(const std::wstring& name, DWORD timeoutMs, bool* wasRunning = nullptr);
bool StartServiceByName(const std::wstring& name, std::wstring* error = nullptr);
bool ServiceExists(const std::wstring& name);
bool ServiceRunning(const std::wstring& name);  // running, starting or paused
// Creates the service or brings an existing one up to date (binary, start type, account, description, recovery).
bool InstallService(const ServiceDef& def, const std::wstring& exe, std::wstring& error);
bool DeleteServiceByName(const std::wstring& name);

// ---- firewall ---------------------------------------------------------------------------------------------------

struct FirewallRule {
    FirewallRuleDef def;
    std::wstring program;  // absolute
};
// Replaces every rule of the group ("FluentSetup:<appId>") with these. False with `error` on failure.
bool ReplaceFirewallRules(const std::wstring& group, const std::vector<FirewallRule>& rules, std::wstring& error);
void RemoveFirewallRules(const std::wstring& group);

// ---- config files -----------------------------------------------------------------------------------------------

// Writes the values into a JSON or INI file (created if missing; other settings kept). Throws Failure.
void WriteConfigFile(const ConfigFileDef& def, const std::wstring& file, const PlaceholderContext& ctx);

// ---- Restart Manager --------------------------------------------------------------------------------------------

// Closes the programs that use files about to be replaced or removed and restarts them afterwards (those that
// registered for restart with Windows).
class RestartManagerSession {
public:
    RestartManagerSession() = default;
    RestartManagerSession(const RestartManagerSession&) = delete;
    RestartManagerSession& operator=(const RestartManagerSession&) = delete;
    ~RestartManagerSession();
    // Registers the files; true if a session could be started.
    bool Start(const std::vector<std::wstring>& files);
    [[nodiscard]] std::vector<std::wstring> Apps() const;
    // Shuts the programs down (politely, then forced); returns the names still running.
    std::vector<std::wstring> Shutdown();
    // Restarts what Shutdown closed (only programs registered for restart); ends the session.
    void Restart();

private:
    DWORD session_ = 0;
    bool active_ = false;
    bool shutDown_ = false;
};

}  // namespace fsetup::install
