// FluentSetup install engine: the only code that changes the system. UI-agnostic; reports through IProgressSink.
//
// State machine (install):
//   Idle -> Validating -> Preparing -> Extracting -> Installing -> Registering -> Finalizing -> Completed
//                 \___________ any failure / cancel before commit ___________/-> RollingBack -> Failed|Cancelled
// Uninstall: Idle -> Validating -> Preparing -> Removing -> Registering -> Finalizing -> Completed (same rollback rules).
#pragma once

#include "Archive/container.h"
#include "Common/error.h"
#include "Common/json.h"
#include "Common/path.h"
#include "Install.Core/installed.h"
#include "Install.Core/package.h"

#include <atomic>
#include <map>
#include <optional>
#include <string>
#include <vector>

namespace fsetup::install {

enum class State {
    Idle,
    Validating,
    WaitingForElevation,
    Preparing,
    Extracting,
    Installing,
    Registering,
    Finalizing,
    Removing,
    Completed,
    Cancelling,
    RollingBack,
    Failed,
    Downloading,               // a missing prerequisite (only when the package allows downloads)
    InstallingPrerequisites,
};
const char* StateName(State s) noexcept;

struct Progress {
    State state = State::Idle;
    double fraction = -1;  // < 0: indeterminate
    std::wstring detail;
    bool cancellable = true;
};
json::Value ProgressToJson(const Progress& p);
Progress ProgressFromJson(const json::Value& v);

class IProgressSink {
public:
    virtual ~IProgressSink() = default;
    virtual void OnProgress(const Progress& p) = 0;
};

struct InstallOptions {
    std::wstring installDir;
    Scope scope = Scope::User;
    bool desktopShortcut = false;
    bool startMenuShortcut = true;
    std::map<std::string, bool> choices;  // package-defined options (id -> selected); missing ids use the default
};
json::Value OptionsToJson(const InstallOptions& o);
bool OptionsFromJson(const json::Value& v, InstallOptions& o);

struct Outcome {
    ErrorCode code = ErrorCode::Ok;
    std::wstring detail;          // diagnostic, English
    bool rolledBack = false;      // all changes were undone
    bool upgrade = false;         // a previous version was involved (rolled back => it was restored)
    bool leftovers = false;       // uninstall: the install directory still contains files not installed by setup
    std::wstring installDir;
    std::wstring mainExecutable;  // absolute, empty if none
    std::vector<std::wstring> deferredDeletes;  // uninstall: delete once the original uninstaller process has exited
    std::vector<std::wstring> warnings;         // non-fatal problems (e.g. an optional step that failed), English
};
json::Value OutcomeToJson(const Outcome& o);
Outcome OutcomeFromJson(const json::Value& v);

enum class Relation { None, Upgrade, Same, Downgrade };
struct Existing {
    Relation relation = Relation::None;
    std::optional<InstalledProduct> product;
    bool blocked = false;
    bool foreign = false;  // installed by another installer; adopted because the package says so
};

struct DirCheck {
    path::Problem problem = path::Problem::None;
    std::wstring normalized;
    uint64_t freeBytes = 0;
    uint64_t requiredBytes = 0;
};

class Engine {
public:
    Engine(const Package& pkg, const archive::Container& container);

    [[nodiscard]] Existing DetectExisting() const;
    [[nodiscard]] std::wstring DefaultInstallDir(Scope scope) const;
    [[nodiscard]] uint64_t RequiredBytes() const;
    // probeWritable: also verify the current (non-elevated) user can create the directory.
    [[nodiscard]] DirCheck CheckInstallDir(const std::wstring& dir, Scope scope, bool probeWritable) const;
    [[nodiscard]] bool ScopeAllowed(Scope s) const noexcept;
    // Enforces what the package allows (scope, custom folder, visible shortcuts, defined option ids) on options that
    // came from a less trusted process. Hidden shortcut offers and missing options get the package defaults.
    [[nodiscard]] bool SanitizeOptions(InstallOptions& o) const;

    Outcome Install(const InstallOptions& options, IProgressSink& sink, const std::atomic<bool>& cancel);
    // originDir: directory of the uninstaller that was started (selects the matching installation), may be empty.
    Outcome Uninstall(const std::wstring& originDir, IProgressSink& sink, const std::atomic<bool>& cancel);

    // Test hook: fail with IoError after placing this many files (exercises rollback). 0 = off.
    void SetFaultAfterFiles(unsigned n) noexcept { faultAfter_ = n; }

private:
    void RecoverPending(Scope scope) const;
    void InstallPayload(const std::wstring& dir, const std::wstring& backup, class Journal& journal, class Reporter& rep,
                        const std::atomic<bool>& cancel, std::vector<RecordFile>& recordFiles);
    void WriteUninstaller(const std::wstring& dest) const;
    // Runs the package's actions for `when` (payload executables only). Fail-policy errors throw; others become warnings.
    void RunActions(Action::When when, const std::wstring& dir, const std::map<std::string, bool>& choices, Outcome& out) const;
    // Our own installation, or (if the package adopts it) one made by a previous installer under the same uninstall key.
    [[nodiscard]] std::optional<InstalledProduct> FindExisting(bool* foreign = nullptr) const;

    const Package& pkg_;
    const archive::Container& container_;
    unsigned faultAfter_ = 0;
};

}  // namespace fsetup::install
