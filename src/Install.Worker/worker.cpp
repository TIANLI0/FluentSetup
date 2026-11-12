#include "Install.Worker/worker.h"

#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/security.h"
#include "IPC/pipe.h"
#include "IPC/protocol.h"
#include "Install.Core/engine.h"

#include <atomic>
#include <thread>

namespace fsetup::worker {

namespace {

class PipeSink : public install::IProgressSink {
public:
    explicit PipeSink(ipc::Channel& channel) : channel_(channel) {}
    void OnProgress(const install::Progress& p) override {
        json::Value m;
        m.set("t", "progress");
        m.set("progress", install::ProgressToJson(p));
        channel_.Send(m);
    }

private:
    ipc::Channel& channel_;
};

}  // namespace

int RunWorker(const boot::Args& args, const install::Package& pkg, const archive::Container& container) {
    const int internal = static_cast<int>(ErrorCode::Internal);
    if (!security::IsElevated()) {
        log::Error(L"Worker started without elevation");
        return internal;
    }
    // Open the processes we depend on now, so their PIDs cannot be recycled under us.
    UniqueHandle parent(::OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, args.parentPid));
    if (!parent) {
        log::Error(L"Worker: parent process %lu not found", args.parentPid);
        return internal;
    }
    UniqueHandle origin;
    if (args.waitPid) origin.reset(::OpenProcess(SYNCHRONIZE, FALSE, args.waitPid));

    ipc::Channel channel = ipc::Channel::Connect(args.pipe, ipc::kConnectTimeoutMs);
    if (!channel.valid()) {
        log::Error(L"Worker: cannot connect to pipe %s", args.pipe.c_str());
        return internal;
    }
    if (channel.PeerProcessId() != args.parentPid) {
        log::Error(L"Worker: pipe server is process %lu, expected parent %lu; refusing", channel.PeerProcessId(), args.parentPid);
        return internal;
    }

    json::Value run;
    if (!channel.Receive(run, 60000) || run["t"].asString() != "run") {
        log::Error(L"Worker: no valid run request");
        return internal;
    }
    const std::string& op = run["op"].asString();
    const bool uninstall = op == "uninstall";
    if ((op != "install" && !uninstall) || uninstall != args.uninstall || (!uninstall && pkg.IsUninstaller())) {
        log::Error(L"Worker: operation '%S' is not allowed for this package/command line", op.c_str());
        return internal;
    }
    install::InstallOptions options;
    // Re-check against the package: the unelevated parent is not trusted to respect allowCustomDir, scopes or option ids.
    if (!uninstall && (!install::OptionsFromJson(run["options"], options) || !install::Engine(pkg, container).SanitizeOptions(options))) {
        log::Error(L"Worker: malformed or disallowed install options");
        return static_cast<int>(ErrorCode::InvalidArguments);
    }

    std::atomic<bool> cancel{false};
    std::thread reader([&] {
        json::Value m;
        while (channel.Receive(m)) {
            if (m["t"].asString() == "cancel") cancel = true;
        }
        // Controller gone (closed or crashed): stop and roll back rather than finishing invisibly.
        cancel = true;
    });

    PipeSink sink(channel);
    install::Engine engine(pkg, container);
    const install::Outcome outcome = uninstall ? engine.Uninstall(args.originDir, sink, cancel) : engine.Install(options, sink, cancel);
    json::Value done;
    done.set("t", "done");
    done.set("outcome", install::OutcomeToJson(outcome));
    channel.Send(done);

    if (!outcome.deferredDeletes.empty() && origin) {
        // The original uninstaller (inside the install directory) is still running; finish once it has exited.
        ::WaitForSingleObject(origin.get(), 60000);
        for (const auto& p : outcome.deferredDeletes) {
            for (int attempt = 0; attempt < 20; ++attempt) {
                const DWORD attrs = fs::Attributes(p);
                if (attrs == INVALID_FILE_ATTRIBUTES) break;
                if (attrs & FILE_ATTRIBUTE_DIRECTORY) {
                    if (!fs::IsDirectoryEmpty(p) || ::RemoveDirectoryW(path::Long(p).c_str())) break;
                } else if (fs::DeleteFileForce(p) == 0) {
                    break;
                }
                ::Sleep(250);
            }
        }
    }
    channel.CancelIo();
    reader.join();
    channel.Close();
    return static_cast<int>(outcome.code);
}

}  // namespace fsetup::worker
