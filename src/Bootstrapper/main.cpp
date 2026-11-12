// FluentSetup bootstrapper: the entry point of every generated Setup.exe and of the installed uninstall.exe.
// Native Win32 + C++20 only; no .NET, no Windows App Runtime, no WebView2, no network access.
#include "Archive/container.h"
#include "Bootstrapper/args.h"
#include "Bootstrapper/controller.h"
#include "Bootstrapper/localization.h"
#include "Bootstrapper/winui_host.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/security.h"
#include "Common/text.h"
#include "Install.Core/package.h"
#include "Install.Worker/worker.h"
#include "Setup.Win32/fallback_ui.h"

#include <commctrl.h>
#include <shellapi.h>

#include <memory>

using namespace fsetup;

namespace {

constexpr wchar_t kProductVersion[] = L"1.0.0";

DWORD OsBuild() {
    using RtlGetVersionFn = LONG(WINAPI*)(OSVERSIONINFOW*);
    const auto fn = reinterpret_cast<RtlGetVersionFn>(::GetProcAddress(::GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
    OSVERSIONINFOW info{sizeof(info)};
    return fn && fn(&info) == 0 ? info.dwBuildNumber : 0;
}

std::wstring Timestamp() {
    SYSTEMTIME t{};
    ::GetLocalTime(&t);
    return Printf(L"%04u%02u%02u-%02u%02u%02u", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
}

void ShowError(const std::wstring& title, const std::wstring& instruction, const std::wstring& content) {
    TASKDIALOGCONFIG tdc{sizeof(tdc)};
    tdc.dwFlags = TDF_SIZE_TO_CONTENT;
    tdc.pszWindowTitle = title.c_str();
    tdc.pszMainIcon = TD_ERROR_ICON;
    tdc.pszMainInstruction = instruction.c_str();
    tdc.pszContent = content.c_str();
    tdc.dwCommonButtons = TDCBF_CLOSE_BUTTON;
    if (FAILED(::TaskDialogIndirect(&tdc, nullptr, nullptr, nullptr))) {
        ::MessageBoxW(nullptr, (instruction + L"\n\n" + content).c_str(), title.c_str(), MB_ICONERROR | MB_OK);
    }
}

// The installed uninstall.exe cannot delete itself while running. Phase 1 copies it to a private temp directory,
// runs the copy (phase 2) and waits for its result, so callers (Settings, scripts) still get the real exit code.
int RunUninstallPhase1(const std::wstring& self) {
    const std::wstring tempDir = path::Join(path::TempRoot(), L"FluentSetup-u-" + RandomToken().substr(0, 16));
    if (security::CreatePrivateDirectory(tempDir) != 0) return static_cast<int>(ErrorCode::IoError);
    const std::wstring copy = path::Join(tempDir, L"uninstall.exe");
    if (!::CopyFileW(self.c_str(), copy.c_str(), TRUE)) return static_cast<int>(ErrorCode::IoError);

    SECURITY_ATTRIBUTES sa{sizeof(sa), nullptr, TRUE};
    HANDLE readEnd = nullptr, writeEnd = nullptr;
    if (!::CreatePipe(&readEnd, &writeEnd, &sa, 0)) return static_cast<int>(ErrorCode::Internal);
    UniqueHandle reader(readEnd), writer(writeEnd);
    ::SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0);
    HANDLE selfHandle = nullptr;
    ::DuplicateHandle(::GetCurrentProcess(), ::GetCurrentProcess(), ::GetCurrentProcess(), &selfHandle, SYNCHRONIZE, TRUE, 0);
    UniqueHandle selfDup(selfHandle);

    // Forward the public switches the user gave us, then add the phase-2 handshake.
    int argc = 0;
    LPWSTR* argv = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    std::wstring cmd = boot::QuoteArg(copy);
    for (int i = 1; argv && i < argc; ++i) cmd += L" " + boot::QuoteArg(argv[i]);
    if (argv) ::LocalFree(argv);
    cmd += L" --phase2 --origin-dir " + boot::QuoteArg(path::Parent(self)) + L" --origin-process " +
           std::to_wstring(reinterpret_cast<uintptr_t>(selfHandle)) + L" --result-pipe " +
           std::to_wstring(reinterpret_cast<uintptr_t>(writeEnd));

    SIZE_T attrSize = 0;
    ::InitializeProcThreadAttributeList(nullptr, 1, 0, &attrSize);
    auto attrBuffer = std::make_unique<uint8_t[]>(attrSize);
    auto* attrs = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attrBuffer.get());
    ::InitializeProcThreadAttributeList(attrs, 1, 0, &attrSize);
    HANDLE inherit[] = {writeEnd, selfHandle};
    ::UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherit, sizeof(inherit), nullptr, nullptr);
    STARTUPINFOEXW si{};
    si.StartupInfo.cb = sizeof(si);
    si.lpAttributeList = attrs;
    PROCESS_INFORMATION pi{};
    const BOOL ok = ::CreateProcessW(copy.c_str(), cmd.data(), nullptr, nullptr, TRUE, EXTENDED_STARTUPINFO_PRESENT, nullptr,
                                     tempDir.c_str(), &si.StartupInfo, &pi);
    ::DeleteProcThreadAttributeList(attrs);
    if (!ok) return static_cast<int>(ErrorCode::Internal);
    UniqueHandle process(pi.hProcess), thread(pi.hThread);
    writer.reset();  // only the child holds the write end now

    int code = 0;
    DWORD read = 0;
    if (::ReadFile(reader.get(), &code, sizeof(code), &read, nullptr) && read == sizeof(code)) return code;
    ::WaitForSingleObject(process.get(), INFINITE);
    DWORD exitCode = static_cast<DWORD>(ErrorCode::Internal);
    ::GetExitCodeProcess(process.get(), &exitCode);
    return static_cast<int>(exitCode);
}

}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    // Hardening before anything else: system32-only DLL search (Setup.exe usually runs from Downloads), terminate on
    // heap corruption.
    ::SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
    ::HeapSetInformation(nullptr, HeapEnableTerminationOnCorruption, nullptr, 0);
    ::SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);

    boot::Args args;
    std::wstring argError;
    const bool argsOk = boot::ParseArgs(::GetCommandLineW(), args, argError);
    const bool quiet = argsOk ? args.quiet : (wcsstr(::GetCommandLineW(), L"/quiet") || wcsstr(::GetCommandLineW(), L"/silent"));
    const std::wstring self = path::ModulePath();

    // ---- open and validate our own package --------------------------------------------------------------------
    std::unique_ptr<archive::Container> container;
    install::Package pkg;
    std::wstring packageError;
    archive::SignatureState signature = archive::SignatureState::Unsigned;
    long signatureStatus = 0;
    try {
        container = archive::Container::Open(self);
        signature = archive::CheckSignature(self, &signatureStatus);
        if (signature == archive::SignatureState::Tampered) {
            throw Failure(ErrorCode::PackageCorrupt, Printf(L"The digital signature does not match the file (0x%08lX)", signatureStatus));
        }
        pkg = install::ParsePackage(container->HeaderJson(),
                                    [&](uint64_t offset, uint64_t size) { return container->StreamRangeValid(offset, size); });
    } catch (const Failure& f) {
        packageError = f.detail();
    }

    boot::Strings strings;
    strings.Load(argsOk ? args.language : std::wstring(), pkg.ui["defaultLanguage"].asWide(), pkg.ui["strings"]);
    const std::wstring appName = pkg.name.empty() ? path::FileName(self) : pkg.name;
    const bool uninstallMode = argsOk && (args.uninstall || pkg.IsUninstaller());

    std::wstring logPath = argsOk ? args.logPath : std::wstring();
    if (logPath.empty()) {
        const std::wstring id = pkg.appId.empty() ? L"package" : pkg.appId;
        logPath = path::Join(path::Join(path::TempRoot(), L"FluentSetup"),
                             id + (uninstallMode ? L"-uninstall-" : L"-install-") + Timestamp() + L".log");
    }
    log::Open(logPath, args.worker ? L"worker" : args.phase2 ? L"uninstall" : L"setup");
    log::Info(L"FluentSetup %s (%s) started: %s", kProductVersion,
#if defined(_M_ARM64)
              L"arm64",
#else
              L"x64",
#endif
              ::GetCommandLineW());
    log::Info(L"UI language %s (user UI language 0x%04X)", strings.Language().c_str(), ::GetUserDefaultUILanguage());
    log::Info(L"Windows build %lu, elevated=%d, signature=%d (0x%08lX)", OsBuild(), security::IsElevated() ? 1 : 0,
              static_cast<int>(signature), signatureStatus);

    if (!packageError.empty()) {
        log::Error(L"Package validation failed: %s", packageError.c_str());
        if (!quiet) ShowError(appName, strings.Get("error.PackageCorrupt"), packageError);
        return static_cast<int>(ErrorCode::PackageCorrupt);
    }
    if (!argsOk) {
        log::Error(L"Invalid command line: %s", argError.c_str());
        if (!quiet) ShowError(appName, strings.Get("error.InvalidArguments"), argError + L"\n\n" + strings.Get("usage"));
        return static_cast<int>(ErrorCode::InvalidArguments);
    }
    if (args.help) {
        if (!quiet) ::MessageBoxW(nullptr, strings.Get("usage").c_str(), appName.c_str(), MB_ICONINFORMATION | MB_OK);
        return 0;
    }
    log::Info(L"Package %s %s (%s), %zu files, %s", pkg.appId.c_str(), pkg.version.c_str(), pkg.kind.c_str(),
              pkg.payload.files.size(), FormatBytes(pkg.payload.totalRaw).c_str());

    if (args.worker) {
        const int code = worker::RunWorker(args, pkg, *container);
        log::Info(L"Worker exit code %d", code);
        return code;
    }
    if (pkg.minWindowsBuild && OsBuild() < pkg.minWindowsBuild) {
        log::Error(L"Windows build %lu is older than the required %u", OsBuild(), pkg.minWindowsBuild);
        if (!quiet) ShowError(appName, strings.Get("error.UnsupportedSystem"), Printf(L"Windows build %lu < %u", OsBuild(), pkg.minWindowsBuild));
        return static_cast<int>(ErrorCode::UnsupportedSystem);
    }
    if (uninstallMode && pkg.IsUninstaller() && !args.phase2) {
        log::Info(L"Relocating the uninstaller to a temporary directory");
        log::Close();
        return RunUninstallPhase1(self);
    }

    // One setup/uninstall per app at a time (across sessions).
    UniqueHandle mutex(::CreateMutexW(nullptr, FALSE, (L"Global\\FluentSetup-" + pkg.appId).c_str()));
    if (mutex && ::WaitForSingleObject(mutex.get(), 0) == WAIT_TIMEOUT) {
        log::Error(L"Another setup of %s is running", pkg.appId.c_str());
        if (!quiet) ShowError(appName, strings.Fmt("error.AlreadyRunning", {appName}), {});
        return static_cast<int>(ErrorCode::AlreadyRunning);
    }

    const boot::Mode mode = uninstallMode ? boot::Mode::Uninstall : boot::Mode::Install;
    boot::Controller controller(pkg, *container, mode, strings, args, logPath);
    if (mode == boot::Mode::Uninstall && !controller.existing().product) {
        log::Error(L"%s is not installed", pkg.appId.c_str());
        if (!quiet) ShowError(appName, strings.Get("error.NotInstalled"), {});
        return static_cast<int>(ErrorCode::NotInstalled);
    }

    int exitCode = 0;
    if (quiet) {
        const install::InstallOptions options = controller.DefaultOptions();
        const bool machine = mode == boot::Mode::Install ? options.scope == install::Scope::Machine
                                                         : controller.existing().product->scope == install::Scope::Machine;
        if (machine && !security::IsElevated()) {
            // Silent mode never shows UI, and the UAC prompt is UI: run silent per-machine setups from an elevated context.
            log::Error(L"Silent per-machine operation requires an elevated process");
            exitCode = static_cast<int>(ErrorCode::ElevationRequired);
        } else {
            controller.Start(options, nullptr);
            controller.WaitDone(INFINITE);
            exitCode = controller.ExitCode();
        }
    } else {
        bool finished = false, resume = false;
        if (args.ui != boot::UiChoice::Win32) {
            const boot::WinUiResult r = boot::RunWinUi(controller, args.uiSimulate);
            if (r.outcome == boot::WinUiOutcome::Finished) {
                finished = true;
                log::Info(L"Front end: WinUI");
            } else {
                resume = r.outcome == boot::WinUiOutcome::FallbackResume;
                log::Warn(L"Front end: falling back to Win32 (%s): %s", resume ? L"resuming session" : L"fresh", r.reason.c_str());
            }
        } else {
            log::Info(L"Front end: Win32 (requested)");
        }
        if (!finished) win32ui::Run(controller, resume);
        if (controller.Running()) controller.WaitDone(INFINITE);
        exitCode = controller.ExitCode();
    }

    if (args.phase2) {
        HANDLE resultPipe = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(args.resultPipe));
        DWORD written = 0;
        ::WriteFile(resultPipe, &exitCode, sizeof(exitCode), &written, nullptr);
        ::CloseHandle(resultPipe);
        controller.CompleteDeferred(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(args.originProcess)));
    }
    log::Info(L"Exit code %d", exitCode);
    log::Close();
    return exitCode;
}
