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
#include <mutex>
#include <optional>
#include <thread>

namespace fsetup::boot {

enum class Mode { Install, Uninstall };

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

    // The "hello" document shared by both front ends (see shared/protocols/ui-protocol.md).
    [[nodiscard]] json::Value Hello(const std::wstring& iconPath) const;
    // Reply to a "checkPath" request: {t:"pathCheck", path, normalized, ok, message, requiredBytes, freeBytes}.
    [[nodiscard]] json::Value CheckPath(const std::wstring& dir, install::Scope scope) const;
    [[nodiscard]] install::InstallOptions DefaultOptions() const;
    // Validates options coming from a front end against the package's offers. Returns false if they are not allowed.
    bool SanitizeOptions(const json::Value& v, install::InstallOptions& out) const;

    // Starts the operation asynchronously. Ignored if one is running. `owner` parents the UAC prompt.
    void Start(const install::InstallOptions& options, HWND owner);
    void Cancel();
    [[nodiscard]] bool Started() const;
    [[nodiscard]] bool Running() const;
    bool WaitDone(DWORD ms) const;
    [[nodiscard]] std::optional<install::Outcome> Result() const;

    // Atomically returns the latest state and installs `listener` (nullptr to detach).
    void Attach(IListener* listener, install::Progress* last, std::optional<install::Outcome>* outcome, bool* started);

    [[nodiscard]] json::Value DoneMessage(const install::Outcome& o) const;
    [[nodiscard]] bool CanLaunch() const;
    void LaunchApp() const;
    void MarkClosedByUser() { closedByUser_ = true; }
    [[nodiscard]] int ExitCode() const;

    // Deletes files the engine could not remove while the original uninstaller was running (per-user uninstall).
    void CompleteDeferred(HANDLE originProcess) const;

private:
    void Run(install::InstallOptions options, HWND owner);
    install::Outcome RunElevated(const install::InstallOptions& options, HWND owner);
    void Publish(const install::Progress& p);
    void Finish(install::Outcome o);

    const install::Package& pkg_;
    const archive::Container& container_;
    Mode mode_;
    const Strings& strings_;
    const Args& args_;
    std::wstring logPath_;
    install::Existing existing_;

    mutable std::mutex mutex_;          // session state
    std::recursive_mutex listenerMutex_; // listener installation + delivery
    IListener* listener_ = nullptr;
    install::Progress last_;
    std::optional<install::Outcome> outcome_;
    bool started_ = false;
    bool running_ = false;
    bool closedByUser_ = false;
    std::atomic<bool> cancel_{false};
    ipc::Channel* workerChannel_ = nullptr;
    std::thread thread_;
    UniqueHandle doneEvent_;
};

// Classification used by both front ends and the exit code.
bool IsRetryable(ErrorCode code) noexcept;

}  // namespace fsetup::boot
