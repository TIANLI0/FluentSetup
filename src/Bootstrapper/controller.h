// The non-elevated install controller. Owns one installer session: computes defaults, validates what a front end asks
// for, runs the engine (in this process, or in the elevated worker for per-machine work) and fans progress out to the
// current front end. Front ends can be swapped mid-session (WinUI -> Win32 fallback) without re-running anything.
#pragma once

#include "Archive/container.h"
#include "Bootstrapper/args.h"
#include "Bootstrapper/localization.h"
#include "Common/json.h"
#include "IPC/pipe.h"
#include "Install.Core/engine.h"

#include <atomic>
#include <functional>
#include <mutex>
#include <optional>
#include <thread>
#include <vector>

namespace fsetup::boot {

// Install: Setup.exe. Uninstall: /uninstall (or uninstall.exe). Modify: /modify or /repair on an installed app.
enum class Mode { Install, Uninstall, Modify };
// What the current (or last) operation of the session is: the mode's own operation, or a repair in modify mode.
enum class Operation { Install, Uninstall, Modify, Repair };

class IListener {
public:
    virtual ~IListener() = default;
    virtual void OnProgress(const install::Progress& p) = 0;
    virtual void OnDone(const install::Outcome& o) = 0;
};

class Controller {
public:
    Controller(const install::Package& pkg, const archive::Container& container, Mode mode, const Strings& strings,
               const Args& args, std::wstring logPath);
    ~Controller();
    Controller(const Controller&) = delete;
    Controller& operator=(const Controller&) = delete;

    [[nodiscard]] Mode mode() const noexcept { return mode_; }
    [[nodiscard]] const install::Package& package() const noexcept { return pkg_; }
    [[nodiscard]] const archive::Container& container() const noexcept { return container_; }
    [[nodiscard]] const Strings& strings() const noexcept { return strings_; }
    [[nodiscard]] const std::wstring& logPath() const noexcept { return logPath_; }
    [[nodiscard]] const install::Existing& existing() const noexcept { return existing_; }

    // The "hello" document shared by both front ends (see shared/protocols/ui-protocol.md). bannerPath: the extracted
    // ui.banner file (WinUI); the Win32 front end reads the banner with ReadFrontendFile instead.
    [[nodiscard]] json::Value Hello(const std::wstring& iconPath, const std::wstring& bannerPath = {}) const;
    // Reply to a "checkPath" request: {t:"pathCheck", path, normalized, ok, message, requiredBytes, freeBytes}.
    [[nodiscard]] json::Value CheckPath(const std::wstring& dir, install::Scope scope) const;
    // Reply to a "checkInput" request: {t:"inputCheck", id, value, ok, message} (message localized, empty when ok).
    [[nodiscard]] json::Value CheckInput(const std::string& id, const std::wstring& value) const;
    [[nodiscard]] install::InstallOptions DefaultOptions() const;
    // Validates options coming from a front end against the package's offers. Returns false if they are not allowed.
    bool SanitizeOptions(const json::Value& v, install::InstallOptions& out) const;
    // English description of the first input problem (an id the package does not define, a missing required value, a
    // value that does not match); empty if every input value is acceptable.
    [[nodiscard]] std::wstring InputProblem(const install::InstallOptions& o) const;
    // Decodes one file of the package's frontend set into memory and verifies its SHA-256 (e.g. the banner).
    bool ReadFrontendFile(const std::wstring& relativePath, std::vector<uint8_t>& out, uint64_t maxBytes) const;

    // Starts the operation asynchronously. Ignored if one is running. `owner` parents the UAC prompt.
    void Start(const install::InstallOptions& options, HWND owner);
    // Modify mode: verifies the installed files and, if any is missing or damaged, runs the same version's Setup.exe
    // (recorded original or update source) with /passive (/quiet in quiet mode). Asynchronous like Start.
    void Repair(HWND owner);
    // The per-app single-instance lock: a repair releases it for the Setup.exe it runs and takes it back afterwards.
    void SetInstanceLock(std::function<void()> release, std::function<bool()> reacquire) {
        releaseLock_ = std::move(release);
        reacquireLock_ = std::move(reacquire);
    }
    void Cancel();
    [[nodiscard]] bool Started() const;
    [[nodiscard]] bool Running() const;
    bool WaitDone(DWORD ms) const;
    [[nodiscard]] std::optional<install::Outcome> Result() const;
    [[nodiscard]] Operation operation() const;

    // Atomically returns the latest state and installs `listener` (nullptr to detach).
    void Attach(IListener* listener, install::Progress* last, std::optional<install::Outcome>* outcome, bool* started);

    [[nodiscard]] json::Value DoneMessage(const install::Outcome& o) const;
    [[nodiscard]] bool CanLaunch() const;
    void LaunchApp(const std::vector<std::wstring>& args = {}) const;
    // After a quiet install started by an app's own updater (/relaunch): start the app again with install.relaunchArgs.
    void Relaunch() const { LaunchApp(pkg_.ext.relaunchArgs); }
    void MarkClosedByUser() { closedByUser_ = true; }
    // The outcome's ErrorCode, 3010 (ERROR_SUCCESS_REBOOT_REQUIRED) for a success that needs a restart.
    [[nodiscard]] int ExitCode() const;

    // Deletes files the engine could not remove while the original uninstaller was running (per-user uninstall).
    void CompleteDeferred(HANDLE originProcess) const;

private:
    enum class RepairResult { None, NoProblems, Repaired, NoSource };
    bool Begin(Operation op);
    void Run(install::InstallOptions options, HWND owner);
    void RunRepair();
    [[nodiscard]] std::optional<install::InstallRecord> PreviousRecord() const;
    install::Outcome RunElevated(const install::InstallOptions& options, HWND owner, const char* op = nullptr);
    void Publish(const install::Progress& p);
    void Finish(install::Outcome o);

    const install::Package& pkg_;
    const archive::Container& container_;
    Mode mode_;
    const Strings& strings_;
    const Args& args_;
    std::wstring logPath_;
    install::Existing existing_;
    std::function<void()> releaseLock_;
    std::function<bool()> reacquireLock_;
    mutable std::once_flag signerOnce_;
    mutable archive::SignerInfo signer_;

    mutable std::mutex mutex_;          // session state
    std::recursive_mutex listenerMutex_; // listener installation + delivery
    IListener* listener_ = nullptr;
    install::Progress last_;
    std::optional<install::Outcome> outcome_;
    bool started_ = false;
    bool running_ = false;
    bool closedByUser_ = false;
    Operation operation_ = Operation::Install;
    RepairResult repair_ = RepairResult::None;
    std::atomic<bool> cancel_{false};
    ipc::Channel* workerChannel_ = nullptr;
    std::thread thread_;
    UniqueHandle doneEvent_;
};

// Classification used by both front ends and the exit code.
bool IsRetryable(ErrorCode code) noexcept;

}  // namespace fsetup::boot
