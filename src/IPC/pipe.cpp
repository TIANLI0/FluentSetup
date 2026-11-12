#include "IPC/pipe.h"

#include <sddl.h>

#include <memory>
#include <vector>

#pragma comment(lib, "advapi32.lib")

namespace fsetup::ipc {

namespace {

// Builds "D:P(A;;GA;;;<user>)(A;;GA;;;BA)(A;;GA;;;SY)" for the current process user.
// Administrators are admitted because an over-the-shoulder elevated worker runs as a different user that is a member of BA.
std::wstring BuildSddl() {
    std::wstring sddl = L"D:P";
    HANDLE token = nullptr;
    if (::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) {
        UniqueHandle t(token);
        DWORD size = 0;
        ::GetTokenInformation(token, TokenUser, nullptr, 0, &size);
        std::vector<uint8_t> buffer(size);
        if (size && ::GetTokenInformation(token, TokenUser, buffer.data(), size, &size)) {
            wchar_t* sidString = nullptr;
            if (::ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sidString)) {
                sddl += L"(A;;GA;;;";
                sddl += sidString;
                sddl += L")";
                ::LocalFree(sidString);
            }
        }
    }
    sddl += L"(A;;GA;;;BA)(A;;GA;;;SY)";
    return sddl;
}

// Waits for an overlapped operation. Cancels it on timeout/cancel so the OVERLAPPED can go out of scope safely.
bool WaitOverlapped(HANDLE file, OVERLAPPED& ov, DWORD timeoutMs, HANDLE cancelEvent, DWORD& transferred) {
    HANDLE handles[2] = {ov.hEvent, cancelEvent};
    const DWORD count = cancelEvent ? 2 : 1;
    const DWORD wait = ::WaitForMultipleObjects(count, handles, FALSE, timeoutMs);
    if (wait == WAIT_OBJECT_0) {
        return ::GetOverlappedResult(file, &ov, &transferred, FALSE) != FALSE;
    }
    ::CancelIoEx(file, &ov);
    ::GetOverlappedResult(file, &ov, &transferred, TRUE);  // wait for the cancellation to complete
    ::SetLastError(wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_CANCELLED);
    return false;
}

}  // namespace

Channel& Channel::operator=(Channel&& o) noexcept {
    if (this != &o) {
        Close();
        pipe_ = std::move(o.pipe_);
        readEvent_ = std::move(o.readEvent_);
        writeEvent_ = std::move(o.writeEvent_);
        server_ = o.server_;
    }
    return *this;
}

Channel Channel::CreateServer(const std::wstring& name) {
    Channel ch;
    PSECURITY_DESCRIPTOR sd = nullptr;
    const std::wstring sddl = BuildSddl();
    if (!::ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) {
        return ch;
    }
    SECURITY_ATTRIBUTES sa{sizeof(sa), sd, FALSE};
    HANDLE h = ::CreateNamedPipeW(FullName(name).c_str(),
                                  PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                  PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                                  1, 64 * 1024, 64 * 1024, 0, &sa);
    const DWORD err = ::GetLastError();
    ::LocalFree(sd);
    if (h == INVALID_HANDLE_VALUE) {
        ::SetLastError(err);
        return ch;
    }
    ch.pipe_.reset(h);
    ch.readEvent_.reset(::CreateEventW(nullptr, TRUE, FALSE, nullptr));
    ch.writeEvent_.reset(::CreateEventW(nullptr, TRUE, FALSE, nullptr));
    ch.server_ = true;
    return ch;
}

bool Channel::Accept(DWORD timeoutMs, HANDLE cancelEvent) {
    if (!valid()) return false;
    OVERLAPPED ov{};
    ov.hEvent = readEvent_.get();
    ::ResetEvent(ov.hEvent);
    if (::ConnectNamedPipe(pipe_.get(), &ov)) return true;
    const DWORD err = ::GetLastError();
    if (err == ERROR_PIPE_CONNECTED) return true;
    if (err != ERROR_IO_PENDING) return false;
    DWORD unused = 0;
    return WaitOverlapped(pipe_.get(), ov, timeoutMs, cancelEvent, unused);
}

Channel Channel::Connect(const std::wstring& name, DWORD timeoutMs) {
    Channel ch;
    const std::wstring full = FullName(name);
    const ULONGLONG deadline = ::GetTickCount64() + timeoutMs;
    for (;;) {
        // SECURITY_IDENTIFICATION: the server may identify but never impersonate us.
        HANDLE h = ::CreateFileW(full.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                                 FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            ch.pipe_.reset(h);
            break;
        }
        const DWORD err = ::GetLastError();
        if (::GetTickCount64() >= deadline) return ch;
        if (err == ERROR_PIPE_BUSY) {
            ::WaitNamedPipeW(full.c_str(), 200);
        } else if (err == ERROR_FILE_NOT_FOUND) {
            ::Sleep(50);
        } else {
            return ch;
        }
    }
    ch.readEvent_.reset(::CreateEventW(nullptr, TRUE, FALSE, nullptr));
    ch.writeEvent_.reset(::CreateEventW(nullptr, TRUE, FALSE, nullptr));
    return ch;
}

bool Channel::Send(const json::Value& message) {
    const std::string body = json::Serialize(message);
    if (body.size() > kMaxMessageBytes) return false;
    std::lock_guard lock(writeMutex_);
    if (!valid()) return false;
    std::vector<uint8_t> frame(4 + body.size());
    const uint32_t len = static_cast<uint32_t>(body.size());
    memcpy(frame.data(), &len, 4);
    memcpy(frame.data() + 4, body.data(), body.size());
    size_t offset = 0;
    while (offset < frame.size()) {
        OVERLAPPED ov{};
        ov.hEvent = writeEvent_.get();
        ::ResetEvent(ov.hEvent);
        DWORD written = 0;
        const DWORD chunk = static_cast<DWORD>(frame.size() - offset);
        if (!::WriteFile(pipe_.get(), frame.data() + offset, chunk, &written, &ov)) {
            if (::GetLastError() != ERROR_IO_PENDING) return false;
            if (!WaitOverlapped(pipe_.get(), ov, 30000, nullptr, written)) return false;
        } else {
            ::GetOverlappedResult(pipe_.get(), &ov, &written, FALSE);
        }
        if (written == 0) return false;
        offset += written;
    }
    return true;
}

bool Channel::ReadExact(void* buffer, DWORD size, DWORD timeoutMs, HANDLE cancelEvent) {
    auto* p = static_cast<uint8_t*>(buffer);
    DWORD got = 0;
    while (got < size) {
        OVERLAPPED ov{};
        ov.hEvent = readEvent_.get();
        ::ResetEvent(ov.hEvent);
        DWORD read = 0;
        if (!::ReadFile(pipe_.get(), p + got, size - got, &read, &ov)) {
            if (::GetLastError() != ERROR_IO_PENDING) return false;
            if (!WaitOverlapped(pipe_.get(), ov, timeoutMs, cancelEvent, read)) return false;
        } else {
            ::GetOverlappedResult(pipe_.get(), &ov, &read, FALSE);
        }
        if (read == 0) return false;
        got += read;
    }
    return true;
}

bool Channel::Receive(json::Value& out, DWORD timeoutMs, HANDLE cancelEvent) {
    if (!valid()) return false;
    uint32_t len = 0;
    if (!ReadExact(&len, 4, timeoutMs, cancelEvent)) return false;
    if (len == 0 || len > kMaxMessageBytes) return false;
    std::string body(len, '\0');
    if (!ReadExact(body.data(), len, timeoutMs, cancelEvent)) return false;
    json::ParseLimits limits;
    limits.maxDepth = 32;
    limits.maxBytes = kMaxMessageBytes;
    return json::Parse(body, out, nullptr, limits) && out.isObject();
}

DWORD Channel::PeerProcessId() const noexcept {
    if (!valid()) return 0;
    ULONG pid = 0;
    const BOOL ok = server_ ? ::GetNamedPipeClientProcessId(pipe_.get(), &pid) : ::GetNamedPipeServerProcessId(pipe_.get(), &pid);
    return ok ? pid : 0;
}

void Channel::Close() noexcept {
    std::lock_guard lock(writeMutex_);
    if (pipe_.valid()) {
        if (server_) {
            ::FlushFileBuffers(pipe_.get());
            ::DisconnectNamedPipe(pipe_.get());
        }
        pipe_.reset();
    }
}

}  // namespace fsetup::ipc
