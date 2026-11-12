// Constants shared by the bootstrapper, the WinUI front end and the elevated worker.
// The full message catalogue is documented in shared/protocols/ui-protocol.md.
#pragma once

namespace fsetup::ipc {

constexpr int kProtocolVersion = 1;

// Exit codes of FluentSetup.UI.exe. They tell the bootstrapper *why* the WinUI front end could not be used.
// Values >= 0x20 never collide with the installer result codes (fsetup::ErrorCode).
enum UiExitCode : int {
    kUiExitOk = 0,
    kUiExitBadArguments = 0x20,
    kUiExitPipeConnectFailed = 0x21,
    kUiExitRuntimeNotFound = 0x22,      // MddBootstrapInitialize2: no compatible Windows App Runtime
    kUiExitRuntimeInitFailed = 0x23,    // MddBootstrapInitialize2 failed for another reason
    kUiExitXamlStartFailed = 0x24,      // Application::Start threw / XAML could not start
    kUiExitUnhandledException = 0x25,   // XAML UnhandledException after start
    kUiExitProtocolError = 0x26,        // peer sent something we do not understand / peer is not who it claims
};

// How long the bootstrapper waits for the WinUI front end to report "ready" before falling back to Win32.
constexpr unsigned kUiReadyTimeoutMs = 20000;
// How long a child waits for the pipe server to appear.
constexpr unsigned kConnectTimeoutMs = 10000;

}  // namespace fsetup::ipc
