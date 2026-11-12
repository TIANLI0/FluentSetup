// Central Windows include + small RAII handle wrappers used by every native module.
#pragma once

#include <windows.h>

#include <cstdint>
#include <string>
#include <string_view>
#include <utility>

namespace fsetup {

// Owns a kernel HANDLE. Treats both nullptr and INVALID_HANDLE_VALUE as "empty".
class UniqueHandle {
public:
    UniqueHandle() noexcept = default;
    explicit UniqueHandle(HANDLE h) noexcept : h_(h) {}
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    UniqueHandle(UniqueHandle&& o) noexcept : h_(o.release()) {}
    UniqueHandle& operator=(UniqueHandle&& o) noexcept {
        if (this != &o) reset(o.release());
        return *this;
    }
    ~UniqueHandle() { reset(); }

    [[nodiscard]] bool valid() const noexcept { return h_ != nullptr && h_ != INVALID_HANDLE_VALUE; }
    explicit operator bool() const noexcept { return valid(); }
    [[nodiscard]] HANDLE get() const noexcept { return h_; }
    HANDLE release() noexcept { return std::exchange(h_, nullptr); }
    void reset(HANDLE h = nullptr) noexcept {
        if (valid()) ::CloseHandle(h_);
        h_ = h;
    }

private:
    HANDLE h_ = nullptr;
};

class UniqueHkey {
public:
    UniqueHkey() noexcept = default;
    explicit UniqueHkey(HKEY h) noexcept : h_(h) {}
    UniqueHkey(const UniqueHkey&) = delete;
    UniqueHkey& operator=(const UniqueHkey&) = delete;
    UniqueHkey(UniqueHkey&& o) noexcept : h_(std::exchange(o.h_, nullptr)) {}
    UniqueHkey& operator=(UniqueHkey&& o) noexcept {
        if (this != &o) {
            reset();
            h_ = std::exchange(o.h_, nullptr);
        }
        return *this;
    }
    ~UniqueHkey() { reset(); }
    [[nodiscard]] HKEY get() const noexcept { return h_; }
    HKEY* put() noexcept {
        reset();
        return &h_;
    }
    explicit operator bool() const noexcept { return h_ != nullptr; }
    void reset() noexcept {
        if (h_) ::RegCloseKey(h_);
        h_ = nullptr;
    }

private:
    HKEY h_ = nullptr;
};

class UniqueFind {
public:
    explicit UniqueFind(HANDLE h) noexcept : h_(h) {}
    UniqueFind(const UniqueFind&) = delete;
    UniqueFind& operator=(const UniqueFind&) = delete;
    ~UniqueFind() {
        if (h_ != INVALID_HANDLE_VALUE) ::FindClose(h_);
    }
    [[nodiscard]] bool valid() const noexcept { return h_ != INVALID_HANDLE_VALUE; }
    [[nodiscard]] HANDLE get() const noexcept { return h_; }

private:
    HANDLE h_;
};

// Runs a callable at scope exit.
template <typename F>
class ScopeExit {
public:
    explicit ScopeExit(F f) : f_(std::move(f)) {}
    ScopeExit(const ScopeExit&) = delete;
    ScopeExit& operator=(const ScopeExit&) = delete;
    ~ScopeExit() {
        if (armed_) f_();
    }
    void dismiss() noexcept { armed_ = false; }

private:
    F f_;
    bool armed_ = true;
};

}  // namespace fsetup
