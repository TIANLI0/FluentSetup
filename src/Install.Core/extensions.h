// Declarative install behaviour beyond copying files - everything validated, nothing shell-based:
//   * closeProcesses     - ask, then force, processes with listed image names to exit (before install/uninstall)
//   * requirements       - .NET Framework release / WebView2 runtime presence checks (optionally run a bundled installer)
//   * options            - user-selectable switches ("Start when I sign in") that gate actions
//   * actions            - run an executable *from the installed payload* with an argument list (CreateProcess, no shell)
//   * install cleanup    - after the files are in place: remove legacy scheduled tasks, Run values, shortcuts and
//                          uninstall entries of older product names (legacyUninstallKeys are also adopted)
//   * ngen               - queue native images for listed .NET Framework assemblies (best effort)
//   * uninstall cleanup  - remove listed scheduled tasks, Run-key values, shortcuts and app data folders
#pragma once

#include "Common/hash.h"
#include "Common/json.h"
#include "Common/version.h"
#include "Common/win.h"

#include <algorithm>
#include <atomic>
#include <functional>

#include <map>
#include <optional>
#include <string>
#include <vector>

namespace fsetup::install {

struct LocalizedText {
    std::map<std::string, std::wstring> byLanguage;  // "en-US" -> text
    [[nodiscard]] std::wstring Get(const std::wstring& language, const std::wstring& fallback) const;
};

struct Requirement {
    enum class Kind { NetFramework, DotNet, WebView2, WindowsAppRuntime } kind = Kind::NetFramework;
    std::wstring name;            // shown to the user
    uint32_t minRelease = 0;      // .NET Framework "Release" value
    std::wstring runtime;         // .NET: Microsoft.NETCore.App | Microsoft.WindowsDesktop.App | Microsoft.AspNetCore.App
                                  // Windows App Runtime: package family, e.g. Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe
    std::optional<Version> minVersion;  // .NET runtime / WebView2 runtime
    std::wstring minVersionText;        // as written in the package, for messages
    bool mustSucceed = false;           // still missing after its installer ran: stop and roll back (else a warning)
    enum class RollForward { Patch, Minor, Major } rollForward = RollForward::Minor;  // which newer .NET versions count
    // Where to get it when missing (bundled installer first, then the download); neither = setup stops.
    std::wstring installer;       // relative payload path
    std::vector<std::wstring> installerArgs;
    std::string downloadUrl;      // https only
    Sha256Digest downloadSha256{};
    uint64_t downloadSize = 0;    // exact size in bytes, 0 = unknown (then capped at 512 MiB)
    std::string helpUrl;          // shown when missing and not installable
    std::vector<std::string> variants;  // only needed for these package variants (empty = always)
    [[nodiscard]] bool AppliesTo(const std::string& variant) const {
        return variants.empty() || std::find(variants.begin(), variants.end(), variant) != variants.end();
    }
    [[nodiscard]] bool Installable() const noexcept { return !installer.empty() || !downloadUrl.empty(); }
};

struct UserOption {
    std::string id;
    LocalizedText label;
    bool defaultValue = false;
    bool required = false;  // shown checked and locked (a component the app always needs)
    // When selected: HKCU\...\Run\<startupName> = "<main executable>" <startupArgs>; removed on uninstall.
    std::wstring startupName;
    std::vector<std::wstring> startupArgs;
    // On an update the option starts from what is on the PC: selected if this Run value (HKCU or HKLM) or scheduled
    // task exists. A startup option detects its own Run value unless told otherwise.
    std::wstring detectRunValue;
    std::wstring detectScheduledTask;
    [[nodiscard]] bool HasDetection() const noexcept { return !detectRunValue.empty() || !detectScheduledTask.empty() || !startupName.empty(); }
    // Value in quiet and passive installs that are not updates (Inno Setup "skipifsilent"); unset = defaultValue.
    std::optional<bool> quietDefault;
};

struct RegistryVersionCondition {
    HKEY hive = nullptr;
    std::wstring key, value;
    Version atLeast;
};

struct Action {
    enum class When { AfterInstall, BeforeUninstall } when = When::AfterInstall;
    enum class OnFailure { Fail, Warn, Ignore } onFailure = OnFailure::Fail;
    std::wstring run;  // relative path inside the install directory
    std::vector<std::wstring> args;
    uint32_t timeoutSeconds = 300;
    std::vector<int64_t> successExitCodes{0};
    std::string ifOption;  // run only if this option is selected
    std::optional<RegistryVersionCondition> skipIfInstalled;
    LocalizedText name;
    // If the first run does not succeed: run again with these arguments, visibly (e.g. an installer's interactive mode).
    std::optional<std::vector<std::wstring>> fallbackArgs;
    // If it still does not succeed: register `"<run>" <args>` under HKCU\...\Run with this value name instead.
    std::wstring fallbackRunValue;
    std::vector<std::wstring> fallbackRunArgs;
};

struct Cleanup {
    std::vector<std::wstring> removeData;  // templates like {CommonAppData}\App or {InstallDir}\logs (uninstall only)
    std::vector<std::wstring> removeScheduledTasks;
    std::vector<std::wstring> removeRunValues;
    std::vector<std::wstring> removeShortcuts;      // .lnk file names in Start menu, Desktop and Startup folders
    std::vector<std::wstring> removeUninstallKeys;  // foreign uninstall entries (install only)
    std::vector<std::wstring> removeFiles;          // install only: files in the install folder ("unins*.exe")
    std::vector<std::wstring> removeRegistryKeys;   // uninstall only: HKCU\Software\<Vendor>\... keys
};
using UninstallCleanup = Cleanup;

// Changes the app made with administrator rights (e.g. after a UAC prompt from inside the app). Undone on uninstall
// with administrator rights: directly when uninstall already runs elevated, else after one UAC prompt (interactive
// uninstalls only), and only if one of `ifExists` exists (empty = always).
struct ServiceAccess {
    std::wstring service;
    std::vector<std::wstring> rights;  // SDDL rights of ACEs granted to user accounts (S-1-5-21-...), e.g. LCRPWP
};
struct ElevatedCleanup {
    std::vector<std::wstring> ifExists;        // {CommonAppData}|{LocalAppData}|{AppData}\... files or folders
    std::vector<std::wstring> traceSessions;   // ETW sessions to stop, with their boot-time autologger
    std::vector<std::wstring> scheduledTasks;  // task paths, e.g. "App\Restore hook"
    std::vector<ServiceAccess> serviceAccess;
    std::vector<std::wstring> removeData;
    std::vector<std::wstring> removeRegistryKeys;  // HKLM\Software\<Vendor>\... (or HKCU) keys
    [[nodiscard]] bool Empty() const noexcept {
        return traceSessions.empty() && scheduledTasks.empty() && serviceAccess.empty() && removeData.empty() && removeRegistryKeys.empty();
    }
};

// Files/folders the user may want to keep (settings, caches); the uninstall UI asks, /remove-user-data in scripts.
struct UserData {
    std::vector<std::wstring> paths;
    bool removeByDefault = false;
};

struct Extensions {
    std::vector<std::wstring> closeProcesses;
    // Asks the installed app to exit cleanly before files are replaced or removed (e.g. "App.exe --exit").
    std::wstring closeCommand;
    std::vector<std::wstring> closeCommandArgs;
    std::wstring adoptUninstallKey;
    std::vector<std::wstring> legacyUninstallKeys;  // older product names: adopted if found, removed after install
    std::vector<std::wstring> ngen;                 // relative payload paths of .NET Framework assemblies
    Cleanup installCleanup;
    ElevatedCleanup elevatedCleanup;
    UserData userData;
    std::vector<std::wstring> relaunchArgs;  // arguments for the app when an updater asked for /relaunch
    std::wstring startMenuFolder;            // Start menu sub-folder for the app's shortcut ("" = Programs itself)
    std::vector<std::wstring> addToPath;     // install sub-folders ("" = the install folder) added to PATH
    std::vector<Requirement> requirements;
    std::vector<UserOption> options;
    std::vector<Action> actions;
    UninstallCleanup cleanup;
};

// "text" or { "en-US": "text", ... } (each up to 256 characters).
LocalizedText ParseLocalizedText(const json::Value& v);

// Parses and validates the optional sections of a package header. `payloadHas(rel)` reports whether a relative path
// is part of the payload. Throws Failure(PackageCorrupt) on invalid input.
Extensions ParseExtensions(const json::Value& root, const std::function<bool(const std::wstring&)>& payloadHas);

// Names of listed processes that are running right now (for a warning before the user starts).
std::vector<std::wstring> RunningProcesses(const std::vector<std::wstring>& imageNames);

// Asks matching processes to close (WM_CLOSE to their windows), waits, then terminates the rest.
// Returns the names that could not be stopped (e.g. access denied).
std::vector<std::wstring> CloseProcesses(const std::vector<std::wstring>& imageNames, unsigned graceMs = 4000);

// Empty if satisfied, otherwise the requirement that is missing.
bool RequirementSatisfied(const Requirement& r);
// Installed version found for a .NET/WebView2 requirement ("" if none), for logs and the UI.
std::wstring InstalledRequirementVersion(const Requirement& r);

// Downloads an https URL to `dest` (created new), verifying the size limit and SHA-256. Progress is (done, total|0).
bool Download(const std::string& url, const std::wstring& dest, const Sha256Digest& sha256, uint64_t expectedSize,
              const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress, std::wstring& error);

// GETs an https URL into memory (at most maxBytes). `headers`: extra request headers ("Name: value\r\n...") or null.
// False with `error` on a transport failure or any status but 200 (`status` keeps the code, 0 if none came).
bool HttpGetText(const std::string& url, const wchar_t* headers, size_t maxBytes, std::string& body, unsigned long& status,
                 std::wstring& error);

bool RegistryVersionAtLeast(const RegistryVersionCondition& c);

struct ActionResult {
    bool ran = false;
    bool ok = true;
    int64_t exitCode = 0;
    std::wstring detail;
};
// Runs `exe` (absolute, already validated to be inside the install directory) with `args`; kills the process tree on
// timeout. No shell, no PATH lookup.
ActionResult RunProgram(const std::wstring& exe, const std::vector<std::wstring>& args, uint32_t timeoutSeconds, bool visible = false);

// Expands {CommonAppData}/{LocalAppData}/{AppData}/{InstallDir} in a removeData template; empty if invalid (or
// {InstallDir} without an install folder). Never anything inside the installer's own work folder.
std::wstring ExpandDataTemplate(const std::wstring& tpl, const std::wstring& installDir = {});
// `name` may include folders ("Folder\Task"); an emptied folder is removed too.
bool DeleteScheduledTask(const std::wstring& name);
// Deletes a file or a folder tree from a removeData template; false if nothing was there or it goes through a link.
bool RemoveDataPath(const std::wstring& tpl, const std::wstring& installDir = {});
bool StopTraceSession(const std::wstring& name);  // stops the ETW session and deletes its autologger (boot-time) key
bool RemoveServiceAccess(const ServiceAccess& access);
bool AnyExists(const std::vector<std::wstring>& templates, const std::wstring& installDir = {});
// "HKCU\Software\Vendor\App" / "HKLM\Software\Vendor\App": never Software itself or a shared tree (Microsoft, Classes, ...).
bool IsRemovableRegistryKey(const std::wstring& key);
bool DeleteRegistryKey(const std::wstring& key);  // whole tree, 64-bit view; false if it was not there
bool RunValueExists(const std::wstring& name);   // HKCU or HKLM ...\CurrentVersion\Run
bool ScheduledTaskExists(const std::wstring& name);
// install.cleanup.removeFiles: a relative path whose file name may use * and ? ("unins*.exe", "bin\old?.dll").
bool IsFilePattern(const std::wstring& pattern);
// Files in `dir` matching a removeFiles pattern (regular files only, no links on the way).
std::vector<std::wstring> MatchFiles(const std::wstring& dir, const std::wstring& pattern);
// Adds/removes a folder in the user (HKCU\Environment) or machine PATH and tells running programs (WM_SETTINGCHANGE).
bool AddToPath(const std::wstring& dir, bool machine);
void RemoveFromPath(const std::wstring& dir, bool machine);
void DeleteRunValue(const std::wstring& name);
bool WriteRunValue(const std::wstring& name, const std::wstring& exe, const std::vector<std::wstring>& args);
// Deletes <name> ("App.lnk", or "Folder\App.lnk" for a Start menu folder, which is removed once empty) from the
// per-user and all-users Start menu Programs, Desktop and Startup folders.
void DeleteShortcutsNamed(const std::wstring& name);
bool IsShortcutName(const std::wstring& name);
// ngen.exe install /queue (install = true) or uninstall for an assembly; best effort, false if ngen is unavailable.
bool RunNGen(const std::wstring& assembly, bool install);

}  // namespace fsetup::install
