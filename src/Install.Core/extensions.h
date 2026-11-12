// Declarative install behaviour beyond copying files - everything validated, nothing shell-based:
//   * closeProcesses     - ask, then force, processes with listed image names to exit (before install/uninstall)
//   * requirements       - .NET Framework release / WebView2 runtime presence checks (optionally run a bundled installer)
//   * options            - user-selectable switches ("Start when I sign in") that gate actions
//   * actions            - run an executable *from the installed payload* with an argument list (CreateProcess, no shell)
//   * uninstall cleanup  - remove listed scheduled tasks, Run-key values and app data folders under AppData/ProgramData
#pragma once

#include "Common/json.h"
#include "Common/version.h"
#include "Common/win.h"

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
    enum class Kind { NetFramework, WebView2 } kind = Kind::NetFramework;
    std::wstring name;            // shown to the user
    uint32_t minRelease = 0;      // .NET Framework "Release" value
    std::wstring installer;       // optional relative payload path run when missing (WebView2 only)
    std::vector<std::wstring> installerArgs;
};

struct UserOption {
    std::string id;
    LocalizedText label;
    bool defaultValue = false;
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
};

struct UninstallCleanup {
    std::vector<std::wstring> removeData;        // templates like {CommonAppData}\App
    std::vector<std::wstring> removeScheduledTasks;
    std::vector<std::wstring> removeRunValues;
};

struct Extensions {
    std::vector<std::wstring> closeProcesses;
    std::wstring adoptUninstallKey;
    std::vector<Requirement> requirements;
    std::vector<UserOption> options;
    std::vector<Action> actions;
    UninstallCleanup cleanup;
};

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

bool RegistryVersionAtLeast(const RegistryVersionCondition& c);

struct ActionResult {
    bool ran = false;
    bool ok = true;
    int64_t exitCode = 0;
    std::wstring detail;
};
// Runs `exe` (absolute, already validated to be inside the install directory) with `args`; kills the process tree on
// timeout. No shell, no PATH lookup.
ActionResult RunProgram(const std::wstring& exe, const std::vector<std::wstring>& args, uint32_t timeoutSeconds);

// Expands {CommonAppData}/{LocalAppData}/{AppData} in a removeData template; empty if invalid.
std::wstring ExpandDataTemplate(const std::wstring& tpl);
bool DeleteScheduledTask(const std::wstring& name);
void DeleteRunValue(const std::wstring& name);

}  // namespace fsetup::install
