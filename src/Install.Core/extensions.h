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
    std::vector<std::wstring> removeData;  // templates like {CommonAppData}\App (uninstall only)
    std::vector<std::wstring> removeScheduledTasks;
    std::vector<std::wstring> removeRunValues;
    std::vector<std::wstring> removeShortcuts;      // .lnk file names in Start menu, Desktop and Startup folders
    std::vector<std::wstring> removeUninstallKeys;  // foreign uninstall entries (install only)
};
using UninstallCleanup = Cleanup;

struct Extensions {
    std::vector<std::wstring> closeProcesses;
    // Asks the installed app to exit cleanly before files are replaced or removed (e.g. "App.exe --exit").
    std::wstring closeCommand;
    std::vector<std::wstring> closeCommandArgs;
    std::wstring adoptUninstallKey;
    std::vector<std::wstring> legacyUninstallKeys;  // older product names: adopted if found, removed after install
    std::vector<std::wstring> ngen;                 // relative payload paths of .NET Framework assemblies
    Cleanup installCleanup;
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

// Expands {CommonAppData}/{LocalAppData}/{AppData} in a removeData template; empty if invalid.
std::wstring ExpandDataTemplate(const std::wstring& tpl);
bool DeleteScheduledTask(const std::wstring& name);
void DeleteRunValue(const std::wstring& name);
bool WriteRunValue(const std::wstring& name, const std::wstring& exe, const std::vector<std::wstring>& args);
// Deletes <name> from the per-user and all-users Start menu Programs, Desktop and Startup folders.
void DeleteShortcutsNamed(const std::wstring& name);
// ngen.exe install /queue (install = true) or uninstall for an assembly; best effort, false if ngen is unavailable.
bool RunNGen(const std::wstring& assembly, bool install);

}  // namespace fsetup::install
