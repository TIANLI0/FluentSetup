#include "Common/log.h"

#include "Common/fs.h"
#include "Common/security.h"
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
    // The log path is chosen by the (possibly unelevated) parent, typically below the user's %TEMP%. An elevated process
    // must not be redirected through a planted junction/symlink/hard link into creating or appending to another file.
    const bool elevated = security::IsElevated();
    if (elevated && fs::HasLinkInPath(path)) return false;
    CreateParents(path);
    UniqueHandle h(::CreateFileW(path.c_str(), FILE_APPEND_DATA | FILE_READ_ATTRIBUTES | DELETE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                                 FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!h) return false;
    const bool created = ::GetLastError() != ERROR_ALREADY_EXISTS;
    BY_HANDLE_FILE_INFORMATION info{};
    bool safe = ::GetFileType(h.get()) == FILE_TYPE_DISK && ::GetFileInformationByHandle(h.get(), &info) &&
                !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) && info.nNumberOfLinks == 1;
    if (safe && elevated) {
        // A parent swapped for a junction between the check above and CreateFileW shows up in the final path.
        std::wstring full(32768, L'\0'), longName(32768, L'\0');
        const DWORD n = ::GetFullPathNameW(path.c_str(), static_cast<DWORD>(full.size()), full.data(), nullptr);
        full.resize(n < full.size() ? n : 0);
        const DWORD m = full.empty() ? 0 : ::GetLongPathNameW(full.c_str(), longName.data(), static_cast<DWORD>(longName.size()));
        longName.resize(m < longName.size() ? m : 0);
        safe = !longName.empty() && _wcsicmp(fs::FinalPath(h.get()).c_str(), longName.c_str()) == 0;
    }
    if (!safe) {
        if (created) {
            FILE_DISPOSITION_INFO dispose{TRUE};
            ::SetFileInformationByHandle(h.get(), FileDispositionInfo, &dispose, sizeof(dispose));
        }
        return false;
    }
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
