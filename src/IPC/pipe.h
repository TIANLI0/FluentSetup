// Message channel over a local named pipe.
//
// Framing: every message is a little-endian uint32 byte length followed by that many bytes of UTF-8 JSON.
// All handles are overlapped so that a pending read never blocks a concurrent write from another thread.
//
// Security model (see docs/security.md):
//  * the server creates a single-instance pipe (FILE_FLAG_FIRST_PIPE_INSTANCE) with a random 128-bit name, a DACL that
//    only admits the current user, Administrators and SYSTEM, and PIPE_REJECT_REMOTE_CLIENTS;
//  * the server verifies the connecting client's PID equals the process it launched itself;
//  * a client verifies the server's PID equals the PID it was told on its command line.
#pragma once

#include "Common/json.h"
#include "Common/win.h"

#include <mutex>
#include <string>

namespace fsetup::ipc {

constexpr uint32_t kMaxMessageBytes = 16u * 1024u * 1024u;

class Channel {
public:
    Channel() = default;
    Channel(const Channel&) = delete;
    Channel& operator=(const Channel&) = delete;
    Channel(Channel&& o) noexcept { *this = std::move(o); }
    Channel& operator=(Channel&& o) noexcept;
    ~Channel() { Close(); }

    // Creates the server end. `name` is the bare name (without \\.\pipe\). Returns an invalid channel on failure
    // (GetLastError() describes why).
    static Channel CreateServer(const std::wstring& name);

    // Waits for the single client. Returns false on timeout, on cancel (cancelEvent signalled) or error.
    bool Accept(DWORD timeoutMs, HANDLE cancelEvent = nullptr);

    // Connects to an existing server, retrying until timeoutMs elapses.
    static Channel Connect(const std::wstring& name, DWORD timeoutMs);

    [[nodiscard]] bool valid() const noexcept { return pipe_.valid(); }

    // Thread-safe. Returns false if the peer is gone.
    bool Send(const json::Value& message);

    // Blocks until a full message arrives. Returns false on disconnect, malformed data, timeout or cancel.
    // Only one thread may call Receive at a time.
    bool Receive(json::Value& out, DWORD timeoutMs = INFINITE, HANDLE cancelEvent = nullptr);

    // PID of the process on the other end (0 if unknown).
    [[nodiscard]] DWORD PeerProcessId() const noexcept;

    // Aborts a Receive() blocked in another thread (it returns false).
    void CancelIo() noexcept {
        if (valid()) ::CancelIoEx(pipe_.get(), nullptr);
    }

    void Close() noexcept;

    static std::wstring FullName(const std::wstring& name) { return L"\\\\.\\pipe\\" + name; }

private:
    bool ReadExact(void* buffer, DWORD size, DWORD timeoutMs, HANDLE cancelEvent);

    UniqueHandle pipe_;
    UniqueHandle readEvent_;
    UniqueHandle writeEvent_;
    bool server_ = false;
    std::mutex writeMutex_;
};

}  // namespace fsetup::ipc
