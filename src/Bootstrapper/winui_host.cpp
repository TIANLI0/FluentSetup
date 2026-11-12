#include "Bootstrapper/winui_host.h"

#include "Bootstrapper/args.h"
#include "Bootstrapper/runtime_probe.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/security.h"
#include "Common/text.h"
#include "IPC/protocol.h"

#include <vector>

namespace fsetup::boot {

namespace {

// Private temp directory with the extracted bundle; deleted on destruction.
class BundleDir {
public:
    ~BundleDir() {
        locks_.clear();
        if (!dir_.empty()) fs::DeleteTree(dir_);
    }
    bool Extract(const install::Package& pkg, const archive::Container& container, std::wstring& error) {
        dir_ = path::Join(path::TempRoot(), L"FluentSetup-" + RandomToken().substr(0, 16));
        if (const DWORD e = security::CreatePrivateDirectory(dir_); e != 0) {
            error = L"Cannot create a private temp directory: " + Win32Message(e);
            dir_.clear();
            return false;
        }
        try {
            const auto& files = pkg.frontend.files;
            size_t index = 0;
            std::vector<uint8_t> buffer(1u << 20);
            for (uint32_t s = 0; s < pkg.frontend.streams.size(); ++s) {
                const auto& si = pkg.frontend.streams[s];
                archive::StreamReader reader(container, si.offset, si.size, si.rawSize, si.sha256);
                for (; index < files.size() && files[index].stream == s; ++index) {
                    const auto& f = files[index];
                    const std::wstring target = path::Join(dir_, f.path);
                    fs::CreateDirectories(path::Parent(target));
                    {
                        UniqueHandle h(::CreateFileW(path::Long(target).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                                     FILE_ATTRIBUTE_NORMAL, nullptr));
                        if (!h) ThrowWin32(L"Cannot create " + target);
                        uint64_t left = f.size;
                        while (left > 0) {
                            const size_t want = static_cast<size_t>(std::min<uint64_t>(left, buffer.size()));
                            size_t got = 0;
                            while (got < want) {
                                const size_t n = reader.Read(buffer.data() + got, want - got);
                                if (n == 0) throw Failure(ErrorCode::PackageCorrupt, L"UI stream ended early");
                                got += n;
                            }
                            DWORD written = 0;
                            if (!::WriteFile(h.get(), buffer.data(), static_cast<DWORD>(got), &written, nullptr)) ThrowWin32(L"Write failed");
                            left -= got;
                        }
                    }
                    // Re-open read-only, deny write/delete, and verify what is actually on disk: this exact content is
                    // what will be loaded, and nobody can swap it while the UI runs.
                    UniqueHandle lock(::CreateFileW(path::Long(target).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                                                    OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
                    if (!lock) ThrowWin32(L"Cannot lock " + target);
                    Sha256 hash;
                    uint64_t verified = 0;
                    for (;;) {
                        DWORD read = 0;
                        if (!::ReadFile(lock.get(), buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr) || read == 0) break;
                        hash.Update(buffer.data(), read);
                        verified += read;
                    }
                    if (verified != f.size || hash.Finish() != f.sha256) {
                        throw Failure(ErrorCode::PackageCorrupt, L"UI file failed verification: " + f.path);
                    }
                    locks_.push_back(std::move(lock));
                }
                uint8_t extra = 0;
                if (reader.Read(&extra, 1) != 0) throw Failure(ErrorCode::PackageCorrupt, L"UI stream has trailing data");
            }
        } catch (const Failure& f) {
            error = f.detail();
            return false;
        }
        return true;
    }
    [[nodiscard]] const std::wstring& dir() const noexcept { return dir_; }

private:
    std::wstring dir_;
    std::vector<UniqueHandle> locks_;
};

// Forwards controller events to the UI process.
class Bridge : public IListener {
public:
    Bridge(ipc::Channel& channel, Controller& controller) : channel_(channel), controller_(controller) {}
    void OnProgress(const install::Progress& p) override {
        json::Value m = install::ProgressToJson(p);
        m.set("t", "progress");
        channel_.Send(m);
    }
    void OnDone(const install::Outcome& o) override { channel_.Send(controller_.DoneMessage(o)); }

private:
    ipc::Channel& channel_;
    Controller& controller_;
};

std::wstring ExitReason(DWORD code) {
    switch (code) {
        case ipc::kUiExitBadArguments: return L"bad arguments";
        case ipc::kUiExitPipeConnectFailed: return L"could not connect to the setup pipe";
        case ipc::kUiExitRuntimeNotFound: return L"no compatible Windows App Runtime (MddBootstrapInitialize2: no match)";
        case ipc::kUiExitRuntimeInitFailed: return L"Windows App Runtime initialization failed";
        case ipc::kUiExitXamlStartFailed: return L"XAML failed to start";
        case ipc::kUiExitUnhandledException: return L"unhandled exception";
        case ipc::kUiExitProtocolError: return L"protocol error";
        default: return Printf(L"process exited with 0x%08lX", code);
    }
}

void StopProcess(HANDLE process) {
    if (::WaitForSingleObject(process, 3000) != WAIT_OBJECT_0) {
        ::TerminateProcess(process, ipc::kUiExitProtocolError);
        ::WaitForSingleObject(process, 3000);
    }
}

}  // namespace

WinUiResult RunWinUi(Controller& controller, const std::wstring& simulate) {
    WinUiResult result;
    const install::Package& pkg = controller.package();
    if (!pkg.HasFrontend()) {
        result.reason = L"package has no WinUI front end";
        return result;
    }
    if (simulate != L"runtime-missing") {
        const RuntimeProbe probe = ProbeWindowsAppRuntime();
        log::Info(L"Runtime probe: %s", probe.detail.c_str());
        if (!probe.compatible) {
            result.reason = probe.detail;
            return result;
        }
    }

    const ULONGLONG t0 = ::GetTickCount64();
    BundleDir bundle;
    std::wstring error;
    if (!bundle.Extract(pkg, controller.container(), error)) {
        result.reason = L"UI bundle extraction failed: " + error;
        return result;
    }
    const std::wstring exe = path::Join(bundle.dir(), pkg.frontendEntry);
    const std::wstring icon = pkg.iconFile.empty() ? std::wstring() : path::Join(bundle.dir(), pkg.iconFile);

    const std::wstring pipeName = L"FluentSetup-" + RandomToken();
    ipc::Channel channel = ipc::Channel::CreateServer(pipeName);
    if (!channel.valid()) {
        result.reason = L"cannot create the UI pipe";
        return result;
    }

    // Kill the UI if this process goes away, so it can never outlive the engine it is showing.
    UniqueHandle job(::CreateJobObjectW(nullptr, nullptr));
    if (job) {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        ::SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits));
    }
    std::wstring cmd = QuoteArg(exe) + L" --pipe " + pipeName + L" --server-pid " + std::to_wstring(::GetCurrentProcessId());
    if (!simulate.empty()) cmd += L" --simulate " + simulate;
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    if (!::CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr, bundle.dir().c_str(), &si, &pi)) {
        result.reason = L"cannot start the UI process: " + Win32Message(::GetLastError());
        return result;
    }
    UniqueHandle process(pi.hProcess);
    UniqueHandle thread(pi.hThread);
    if (job) ::AssignProcessToJobObject(job.get(), process.get());
    ::ResumeThread(thread.get());

    // ---- handshake --------------------------------------------------------------------------------------------
    const auto fail = [&](std::wstring why) {
        ::TerminateProcess(process.get(), ipc::kUiExitProtocolError);
        ::WaitForSingleObject(process.get(), 3000);
        DWORD code = 0;
        ::GetExitCodeProcess(process.get(), &code);
        result.reason = std::move(why);
        return result;
    };
    if (!channel.Accept(ipc::kConnectTimeoutMs, process.get())) {
        DWORD code = STILL_ACTIVE;
        ::GetExitCodeProcess(process.get(), &code);
        return fail(code == STILL_ACTIVE ? L"UI did not connect" : L"UI exited before connecting: " + ExitReason(code));
    }
    if (channel.PeerProcessId() != pi.dwProcessId) return fail(L"unexpected pipe client");
    channel.Send(controller.Hello(icon));

    const ULONGLONG deadline = ::GetTickCount64() + ipc::kUiReadyTimeoutMs;
    for (;;) {
        const ULONGLONG now = ::GetTickCount64();
        if (now >= deadline) return fail(L"UI did not become ready within the timeout");
        json::Value m;
        if (!channel.Receive(m, static_cast<DWORD>(deadline - now))) {
            ::WaitForSingleObject(process.get(), 2000);
            DWORD code = STILL_ACTIVE;
            ::GetExitCodeProcess(process.get(), &code);
            return fail(code == STILL_ACTIVE ? L"UI handshake timed out" : L"UI exited during start-up: " + ExitReason(code));
        }
        const std::string& t = m["t"].asString();
        if (t == "ready") break;
        if (t == "init_failed") {
            return fail(Printf(L"UI initialization failed at stage '%s' (HRESULT 0x%08llX)", m["stage"].asWide().c_str(),
                               static_cast<unsigned long long>(m["hr"].asUint())));
        }
        if (t == "close") {  // closed before ready: still counts as the user's decision
            result.outcome = WinUiOutcome::Finished;
            controller.MarkClosedByUser();
            StopProcess(process.get());
            return result;
        }
    }
    log::Info(L"WinUI front end ready after %llu ms", ::GetTickCount64() - t0);

    // ---- session ----------------------------------------------------------------------------------------------
    Bridge bridge(channel, controller);
    install::Progress last;
    std::optional<install::Outcome> done;
    bool started = false;
    controller.Attach(&bridge, &last, &done, &started);

    bool finished = false;
    json::Value m;
    while (!finished && channel.Receive(m)) {
        const std::string& t = m["t"].asString();
        if (t == "checkPath") {
            const std::string& scope = m["scope"].asString();
            channel.Send(controller.CheckPath(m["path"].asWide(), scope == "machine" ? install::Scope::Machine : install::Scope::User));
        } else if (t == "start") {
            install::InstallOptions options = controller.DefaultOptions();
            if (controller.mode() == Mode::Install && !controller.SanitizeOptions(m["options"], options)) {
                log::Warn(L"UI sent options that are not allowed; ignoring the request");
                install::Outcome rejected;
                rejected.code = ErrorCode::InvalidArguments;
                rejected.detail = L"Options rejected by setup";
                rejected.rolledBack = true;
                channel.Send(controller.DoneMessage(rejected));
                continue;
            }
            // Parent the UAC prompt (if any) to the WinUI window so it comes to the foreground.
            std::pair<DWORD, HWND> owner{pi.dwProcessId, nullptr};
            ::EnumWindows(
                [](HWND h, LPARAM lp) -> BOOL {
                    DWORD pid = 0;
                    ::GetWindowThreadProcessId(h, &pid);
                    auto* ctx = reinterpret_cast<std::pair<DWORD, HWND>*>(lp);
                    if (pid == ctx->first && ::IsWindowVisible(h)) {
                        ctx->second = h;
                        return FALSE;
                    }
                    return TRUE;
                },
                reinterpret_cast<LPARAM>(&owner));
            controller.Start(options, owner.second);
        } else if (t == "cancel") {
            controller.Cancel();
        } else if (t == "finish") {
            if (m["launch"].asBool() && controller.CanLaunch()) controller.LaunchApp();
            finished = true;
        } else if (t == "close") {
            if (!controller.Started()) controller.MarkClosedByUser();
            if (!controller.Running()) finished = true;
        }
    }
    controller.Attach(nullptr, nullptr, nullptr, nullptr);

    if (finished) {
        StopProcess(process.get());
        result.outcome = WinUiOutcome::Finished;
        return result;
    }
    // The UI went away without finishing: crash, kill, or a broken pipe. Never restart it.
    ::WaitForSingleObject(process.get(), 2000);
    DWORD code = STILL_ACTIVE;
    ::GetExitCodeProcess(process.get(), &code);
    if (code == STILL_ACTIVE) ::TerminateProcess(process.get(), ipc::kUiExitProtocolError);
    result.outcome = WinUiOutcome::FallbackResume;
    result.reason = L"UI stopped after start-up: " + ExitReason(code);
    return result;
}

}  // namespace fsetup::boot
