// Connection between the WinUI front end and the bootstrapper process that owns the install engine.
#pragma once

#include "Common/json.h"
#include "IPC/pipe.h"

#include <atomic>
#include <functional>
#include <thread>

namespace fsui {

class UiChannel {
public:
    static UiChannel& Get();

    // Real mode: connect to the bootstrapper's pipe and verify the server process.
    bool Connect(const std::wstring& pipeName, DWORD expectedServerPid);
    // Preview mode (Builder): no peer, the hello message is read from a file.
    void SetPreview(fsetup::json::Value hello);

    [[nodiscard]] bool preview() const noexcept { return preview_; }
    [[nodiscard]] const fsetup::json::Value& hello() const noexcept { return hello_; }

    // Blocking read of the initial hello message (real mode).
    bool ReceiveHello(DWORD timeoutMs);

    // Starts the background reader. Callbacks run on the reader thread; callers marshal to the UI thread.
    void StartReader(std::function<void(fsetup::json::Value)> onMessage, std::function<void()> onDisconnected);

    void Send(const fsetup::json::Value& message);
    void SendType(const char* type);
    void Shutdown();

private:
    fsetup::ipc::Channel channel_;
    fsetup::json::Value hello_;
    bool preview_ = false;
    std::thread reader_;
    std::atomic<bool> stopping_{false};
};

}  // namespace fsui
