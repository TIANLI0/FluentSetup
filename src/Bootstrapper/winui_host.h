// Runs the WinUI 3 front end (FluentSetup.UI.exe) as a child process and bridges it to the controller.
//
//   1. quick runtime probe (no compatible Windows App Runtime -> fallback without extracting anything)
//   2. extract the verified UI bundle into a fresh private temp directory; files stay open deny-write while in use
//   3. start the UI in a kill-on-close job, accept its pipe connection (PID verified), send "hello"
//   4. wait for "ready" (timeout ipc::kUiReadyTimeoutMs); "init_failed", exit or timeout -> fallback
//   5. bridge messages until the user finishes; if the UI dies afterwards -> fallback *resumes* the same session
// The WinUI front end is attempted at most once per session, so there is no restart loop.
#pragma once

#include "Bootstrapper/controller.h"

#include <string>

namespace fsetup::boot {

enum class WinUiOutcome {
    Finished,      // the user completed or closed the session in WinUI
    FallbackFresh, // WinUI never became ready; nothing was started
    FallbackResume // WinUI was ready but stopped; the session (possibly running/done) continues in Win32
};

struct WinUiResult {
    WinUiOutcome outcome = WinUiOutcome::FallbackFresh;
    std::wstring reason;  // diagnostic for the log
};

WinUiResult RunWinUi(Controller& controller, const std::wstring& simulate);

}  // namespace fsetup::boot
