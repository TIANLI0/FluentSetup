#include "Common/fs.h"

#include "Common/error.h"
#include "Common/hash.h"
#include "Common/path.h"
#include "Common/text.h"

namespace fsetup::fs {

bool Exists(const std::wstring& p) { return ::GetFileAttributesW(path::Long(p).c_str()) != INVALID_FILE_ATTRIBUTES; }

DWORD Attributes(const std::wstring& p) { return ::GetFileAttributesW(path::Long(p).c_str()); }

bool IsDirectory(const std::wstring& p) {
    const DWORD a = Attributes(p);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

bool IsLinkLike(const std::wstring& p) {
    WIN32_FIND_DATAW fd{};
    UniqueFind f(::FindFirstFileExW(path::Long(p).c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, 0));
    if (!f.valid()) return false;
    if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
    // dwReserved0 carries the reparse tag. Cloud-file placeholders, dedup etc. are not redirections.
    return fd.dwReserved0 == IO_REPARSE_TAG_SYMLINK || fd.dwReserved0 == IO_REPARSE_TAG_MOUNT_POINT;
}

bool HasLinkInPath(const std::wstring& p) {
    // Start below the drive root ("C:\" itself is never a link we care about).
    for (size_t pos = 3; pos <= p.size(); ++pos) {
        if (pos == p.size() || p[pos] == L'\\') {
            const std::wstring prefix = p.substr(0, pos);
            if (!Exists(prefix)) return false;  // nothing deeper exists either
            if (IsLinkLike(prefix)) return true;
        }
    }
    return false;
}

std::wstring FinalPath(HANDLE h) {
    std::wstring buffer(512, L'\0');
    for (;;) {
        const DWORD n = ::GetFinalPathNameByHandleW(h, buffer.data(), static_cast<DWORD>(buffer.size()),
                                                    FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (n == 0) return {};
        if (n < buffer.size()) {
            buffer.resize(n);
            break;
        }
        buffer.resize(n + 1);
    }
    if (StartsWithIgnoreCase(buffer, L"\\\\?\\UNC\\")) return L"\\\\" + buffer.substr(8);
    if (StartsWithIgnoreCase(buffer, L"\\\\?\\")) return buffer.substr(4);
    return buffer;
}

void CreateDirectories(const std::wstring& dir, std::vector<std::wstring>* created, SECURITY_ATTRIBUTES* sa) {
    if (IsDirectory(dir)) return;
    const std::wstring parent = path::Parent(dir);
    if (!parent.empty() && parent != dir && parent.size() > 3) CreateDirectories(parent, created, sa);
    if (::CreateDirectoryW(path::Long(dir).c_str(), sa)) {
        if (created) created->push_back(dir);
        return;
    }
    const DWORD e = ::GetLastError();
    if (e == ERROR_ALREADY_EXISTS && IsDirectory(dir)) return;
    ThrowWin32(L"CreateDirectory failed: " + dir, e);
}

bool IsDirectoryEmpty(const std::wstring& dir) {
    WIN32_FIND_DATAW fd{};
    UniqueFind f(::FindFirstFileExW(path::Long(path::Join(dir, L"*")).c_str(), FindExInfoBasic, &fd,
                                    FindExSearchNameMatch, nullptr, 0));
    if (!f.valid()) return true;
    do {
        if (wcscmp(fd.cFileName, L".") != 0 && wcscmp(fd.cFileName, L"..") != 0) return false;
    } while (::FindNextFileW(f.get(), &fd));
    return true;
}

std::vector<uint8_t> ReadAll(const std::wstring& p, uint64_t maxBytes) {
    UniqueHandle h(::CreateFileW(path::Long(p).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                 FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!h) ThrowWin32(L"Cannot open " + p);
    LARGE_INTEGER size{};
    if (!::GetFileSizeEx(h.get(), &size)) ThrowWin32(L"Cannot get size of " + p);
    if (static_cast<uint64_t>(size.QuadPart) > maxBytes) throw Failure(ErrorCode::IoError, L"File too large: " + p);
    std::vector<uint8_t> data(static_cast<size_t>(size.QuadPart));
    size_t done = 0;
    while (done < data.size()) {
        DWORD read = 0;
        const DWORD chunk = static_cast<DWORD>(std::min<size_t>(data.size() - done, 1u << 24));
        if (!::ReadFile(h.get(), data.data() + done, chunk, &read, nullptr)) ThrowWin32(L"Read failed: " + p);
        if (read == 0) break;
        done += read;
    }
    data.resize(done);
    return data;
}

void WriteAllAtomic(const std::wstring& p, std::span<const uint8_t> data) {
    const std::wstring temp = p + L".tmp-" + RandomToken().substr(0, 8);
    {
        UniqueHandle h(::CreateFileW(path::Long(temp).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                     FILE_ATTRIBUTE_NORMAL, nullptr));
        if (!h) ThrowWin32(L"Cannot create " + temp);
        size_t done = 0;
        while (done < data.size()) {
            DWORD written = 0;
            const DWORD chunk = static_cast<DWORD>(std::min<size_t>(data.size() - done, 1u << 24));
            if (!::WriteFile(h.get(), data.data() + done, chunk, &written, nullptr)) {
                const DWORD e = ::GetLastError();
                h.reset();
                ::DeleteFileW(path::Long(temp).c_str());
                ThrowWin32(L"Write failed: " + temp, e);
            }
            done += written;
        }
        ::FlushFileBuffers(h.get());
    }
    if (!::MoveFileExW(path::Long(temp).c_str(), path::Long(p).c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const DWORD e = ::GetLastError();
        ::DeleteFileW(path::Long(temp).c_str());
        ThrowWin32(L"Cannot replace " + p, e);
    }
}

uint64_t FreeSpace(const std::wstring& p) {
    std::wstring probe = p;
    while (!probe.empty() && !IsDirectory(probe)) {
        const std::wstring parent = path::Parent(probe);
        if (parent == probe) break;
        probe = parent;
    }
    if (probe.empty()) return 0;
    ULARGE_INTEGER avail{};
    if (!::GetDiskFreeSpaceExW(path::Long(probe).c_str(), &avail, nullptr, nullptr)) return 0;
    return avail.QuadPart;
}

DWORD Move(const std::wstring& from, const std::wstring& to, bool replace) {
    const DWORD flags = replace ? MOVEFILE_REPLACE_EXISTING : 0;
    if (::MoveFileExW(path::Long(from).c_str(), path::Long(to).c_str(), flags)) return 0;
    return ::GetLastError();
}

DWORD DeleteFileForce(const std::wstring& p) {
    const std::wstring lp = path::Long(p);
    if (::DeleteFileW(lp.c_str())) return 0;
    DWORD e = ::GetLastError();
    if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND) return 0;
    if (e == ERROR_ACCESS_DENIED) {
        const DWORD a = ::GetFileAttributesW(lp.c_str());
        if (a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_READONLY)) {
            ::SetFileAttributesW(lp.c_str(), a & ~FILE_ATTRIBUTE_READONLY);
            if (::DeleteFileW(lp.c_str())) return 0;
            e = ::GetLastError();
        }
    }
    return e;
}

bool DeleteTree(const std::wstring& dir) {
    if (!Exists(dir)) return true;
    if (IsLinkLike(dir)) return ::RemoveDirectoryW(path::Long(dir).c_str()) != FALSE;
    bool ok = true;
    WIN32_FIND_DATAW fd{};
    {
        UniqueFind f(::FindFirstFileExW(path::Long(path::Join(dir, L"*")).c_str(), FindExInfoBasic, &fd,
                                        FindExSearchNameMatch, nullptr, 0));
        if (f.valid()) {
            do {
                if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
                const std::wstring child = path::Join(dir, fd.cFileName);
                if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                    if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) {
                        ok &= ::RemoveDirectoryW(path::Long(child).c_str()) != FALSE;  // unlink, never recurse
                    } else {
                        ok &= DeleteTree(child);
                    }
                } else {
                    ok &= DeleteFileForce(child) == 0;
                }
            } while (::FindNextFileW(f.get(), &fd));
        }
    }
    ok &= ::RemoveDirectoryW(path::Long(dir).c_str()) != FALSE;
    return ok;
}

}  // namespace fsetup::fs
