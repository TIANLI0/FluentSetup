#include "Common/log.h"

#include "Common/text.h"

#include <cstdarg>
#include <mutex>

namespace fsetup::log {

namespace {

struct State {
    std::mutex mutex;
    UniqueHandle file;
    std::wstring path;
    std::wstring tag;
};

State& S() {
    static State s;
    return s;
}

void CreateParents(const std::wstring& path) {
    for (size_t i = 3; i < path.size(); ++i) {
        if (path[i] == L'\\') {
            ::CreateDirectoryW(path.substr(0, i).c_str(), nullptr);
        }
    }
}

std::wstring VFormat(const wchar_t* fmt, va_list args) {
    va_list copy;
    va_copy(copy, args);
    const int n = _vscwprintf(fmt, copy);
    va_end(copy);
    std::wstring out;
    if (n > 0) {
        out.resize(static_cast<size_t>(n));
        vswprintf_s(out.data(), out.size() + 1, fmt, args);
    }
    return out;
}

}  // namespace

bool Open(const std::wstring& path, std::wstring_view tag) {
    State& s = S();
    std::lock_guard lock(s.mutex);
    CreateParents(path);
    UniqueHandle h(::CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                                 FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!h) return false;
    LARGE_INTEGER size{};
    if (::GetFileSizeEx(h.get(), &size) && size.QuadPart == 0) {
        static constexpr unsigned char kBom[] = {0xEF, 0xBB, 0xBF};
        DWORD written = 0;
        ::WriteFile(h.get(), kBom, sizeof(kBom), &written, nullptr);
    }
    s.file = std::move(h);
    s.path = path;
    s.tag = tag;
    return true;
}

void Close() {
    State& s = S();
    std::lock_guard lock(s.mutex);
    s.file.reset();
}

std::wstring Path() {
    State& s = S();
    std::lock_guard lock(s.mutex);
    return s.path;
}

void Write(Level level, std::wstring_view message) {
    State& s = S();
    std::lock_guard lock(s.mutex);
    if (!s.file) return;
    SYSTEMTIME t{};
    ::GetLocalTime(&t);
    static constexpr const wchar_t* kNames[] = {L"DEBUG", L"INFO ", L"WARN ", L"ERROR"};
    std::wstring line = Printf(L"%04u-%02u-%02u %02u:%02u:%02u.%03u [%s] [%s:%lu] ", t.wYear, t.wMonth, t.wDay, t.wHour,
                               t.wMinute, t.wSecond, t.wMilliseconds, kNames[static_cast<int>(level)], s.tag.c_str(),
                               ::GetCurrentProcessId());
    line.append(message);
    line.append(L"\r\n");
    const std::string utf8 = WideToUtf8(line);
    DWORD written = 0;
    ::WriteFile(s.file.get(), utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
}

#define FSETUP_LOG_IMPL(level)             \
    va_list args;                          \
    va_start(args, fmt);                   \
    const std::wstring m = VFormat(fmt, args); \
    va_end(args);                          \
    Write(level, m)

void Info(const wchar_t* fmt, ...) { FSETUP_LOG_IMPL(Level::Info); }
void Warn(const wchar_t* fmt, ...) { FSETUP_LOG_IMPL(Level::Warn); }
void Error(const wchar_t* fmt, ...) { FSETUP_LOG_IMPL(Level::Error); }
void Debug(const wchar_t* fmt, ...) { FSETUP_LOG_IMPL(Level::Debug); }

}  // namespace fsetup::log
