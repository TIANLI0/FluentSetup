#include "Common/fs.h"

#include "Common/error.h"
#include "Common/hash.h"
#include "Common/parallel.h"
#include "Common/path.h"
#include "Common/security.h"
#include "Common/text.h"

#include <winternl.h>

#include <atomic>
#include <mutex>

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

Sha256Digest HashFile(HANDLE file) {
    Sha256 hash;
    std::vector<uint8_t> buffer(1u << 20);
    for (uint64_t offset = 0;;) {
        OVERLAPPED ov{};
        ov.Offset = static_cast<DWORD>(offset);
        ov.OffsetHigh = static_cast<DWORD>(offset >> 32);
        DWORD read = 0;
        if (!::ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, &ov)) {
            const DWORD e = ::GetLastError();
            if (e == ERROR_HANDLE_EOF) break;
            ThrowWin32(L"Read failed while hashing a file", e);
        }
        if (read == 0) break;
        hash.Update(buffer.data(), read);
        offset += read;
    }
    return hash.Finish();
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

namespace {

// Handle-based deletion (DeleteTree). Every entry is opened relative to the handle of the directory it was listed in,
// with FILE_OPEN_REPARSE_POINT, so nothing is ever reached through a path that someone could redirect between the
// listing and the deletion: a folder swapped for a junction (by a standard user, under a folder such as ProgramData
// that an elevated uninstall cleans) is opened as the junction itself and only the link is removed. Whether an entry is
// a directory, a link or a file is decided from the opened handle, never from the listing.
using NtCreateFileFn = LONG(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, PLARGE_INTEGER, ULONG, ULONG, ULONG,
                                    ULONG, PVOID, ULONG);

constexpr LONG kStatusObjectNameNotFound = static_cast<LONG>(0xC0000034);
constexpr LONG kStatusObjectPathNotFound = static_cast<LONG>(0xC000003A);
constexpr LONG kStatusDeletePending = static_cast<LONG>(0xC0000056);
constexpr ULONG kFileOpen = 0x00000001;                   // FILE_OPEN
constexpr ULONG kFileSynchronousIoNonAlert = 0x00000020;  // FILE_SYNCHRONOUS_IO_NONALERT
constexpr ULONG kFileOpenForBackupIntent = 0x00004000;    // FILE_OPEN_FOR_BACKUP_INTENT
constexpr ULONG kFileOpenReparsePoint = 0x00200000;       // FILE_OPEN_REPARSE_POINT
constexpr DWORD kEntryAccess = DELETE | SYNCHRONIZE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES;

NtCreateFileFn NtCreateFilePtr() {
    static const auto fn = reinterpret_cast<NtCreateFileFn>(::GetProcAddress(::GetModuleHandleW(L"ntdll.dll"), "NtCreateFile"));
    return fn;
}

// Opens `name` (one component) relative to `parent`, never following a reparse point. Empty handle when it is gone
// (`gone` = true) or cannot be opened.
UniqueHandle OpenRelative(HANDLE parent, std::wstring_view name, DWORD access, bool& gone) {
    gone = false;
    const NtCreateFileFn create = NtCreateFilePtr();
    if (!create || name.empty() || name.size() > 32767) return UniqueHandle();
    UNICODE_STRING us{};
    us.Buffer = const_cast<wchar_t*>(name.data());
    us.Length = us.MaximumLength = static_cast<USHORT>(name.size() * sizeof(wchar_t));
    OBJECT_ATTRIBUTES oa{};
    oa.Length = sizeof(oa);
    oa.RootDirectory = parent;
    oa.ObjectName = &us;
    oa.Attributes = 0;  // the exact name from the listing (also in case-sensitive directories)
    IO_STATUS_BLOCK io{};
    HANDLE h = nullptr;
    const LONG st = create(&h, access, &oa, &io, nullptr, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, kFileOpen,
                           kFileSynchronousIoNonAlert | kFileOpenForBackupIntent | kFileOpenReparsePoint, nullptr, 0);
    if (st < 0) {
        gone = st == kStatusObjectNameNotFound || st == kStatusObjectPathNotFound || st == kStatusDeletePending;
        return UniqueHandle();
    }
    return UniqueHandle(h);
}

// FILE_DISPOSITION_INFO_EX (Windows 10 1709+; IGNORE_READONLY 1809+), declared here: the SDK hides it behind
// NTDDI_VERSION, which this project leaves at the Windows 10 baseline.
constexpr int kFileDispositionInfoEx = 21;  // FILE_INFO_BY_HANDLE_CLASS::FileDispositionInfoEx
constexpr DWORD kDispositionDelete = 0x1, kDispositionPosix = 0x2, kDispositionIgnoreReadOnly = 0x10;

// Marks the open entry for deletion: POSIX semantics (the name goes when the handle closes, so the parent directory is
// empty right away) and read-only ignored. Falls back to the classic disposition where the file system lacks the
// extended one.
bool DeleteByHandle(HANDLE h, DWORD attributes) {
    DWORD flags = kDispositionDelete | kDispositionPosix | kDispositionIgnoreReadOnly;
    if (::SetFileInformationByHandle(h, static_cast<FILE_INFO_BY_HANDLE_CLASS>(kFileDispositionInfoEx), &flags, sizeof(flags))) return true;
    const DWORD e = ::GetLastError();
    if (e != ERROR_INVALID_PARAMETER && e != ERROR_NOT_SUPPORTED && e != ERROR_INVALID_FUNCTION) return false;
    if (attributes & FILE_ATTRIBUTE_READONLY) {
        FILE_BASIC_INFO basic{};
        basic.FileAttributes = attributes & ~FILE_ATTRIBUTE_READONLY;
        if (basic.FileAttributes == 0) basic.FileAttributes = FILE_ATTRIBUTE_NORMAL;
        ::SetFileInformationByHandle(h, FileBasicInfo, &basic, sizeof(basic));
    }
    FILE_DISPOSITION_INFO classic{TRUE};
    return ::SetFileInformationByHandle(h, FileDispositionInfo, &classic, sizeof(classic)) != FALSE;
}

// Names in the directory, listed through its handle.
bool ListDirectory(HANDLE dir, std::vector<std::wstring>& names) {
    std::vector<uint8_t> buffer(64 * 1024);
    FILE_INFO_BY_HANDLE_CLASS cls = FileFullDirectoryRestartInfo;
    for (;;) {
        if (!::GetFileInformationByHandleEx(dir, cls, buffer.data(), static_cast<DWORD>(buffer.size()))) {
            return ::GetLastError() == ERROR_NO_MORE_FILES;
        }
        cls = FileFullDirectoryInfo;
        for (size_t offset = 0;;) {
            const auto* e = reinterpret_cast<const FILE_FULL_DIR_INFO*>(buffer.data() + offset);
            const std::wstring_view name(e->FileName, e->FileNameLength / sizeof(wchar_t));
            if (name != L"." && name != L"..") names.emplace_back(name);
            if (e->NextEntryOffset == 0) break;
            offset += e->NextEntryOffset;
        }
    }
}

// Deletes everything inside the open directory (not the directory itself).
bool DeleteContents(HANDLE dir, int depth) {
    // Deeper than any path Windows can address: something is wrong; stop rather than recurse on.
    if (depth > 1024) return false;
    std::vector<std::wstring> names;
    if (!ListDirectory(dir, names)) return false;
    // Files and links go right away, from several threads when there are many (per-file latency dominates: a backup
    // folder holds a whole app version). Every thread works relative to the same directory handle, so this changes
    // nothing about what can be reached. Subdirectories are kept open and emptied afterwards, one level at a time.
    std::atomic<bool> ok{true};
    std::mutex subdirsLock;
    std::vector<std::pair<UniqueHandle, DWORD>> subdirs;
    ParallelFor(names.size(), [&](size_t i) {
        bool gone = false;
        // Listing access too, in case it is a directory; an entry that refuses read access is opened for deletion only.
        UniqueHandle h = OpenRelative(dir, names[i], kEntryAccess | FILE_LIST_DIRECTORY, gone);
        if (!h && !gone) h = OpenRelative(dir, names[i], kEntryAccess, gone);
        FILE_BASIC_INFO basic{};
        if (!h) {
            if (!gone) ok = false;
        } else if (!::GetFileInformationByHandleEx(h.get(), FileBasicInfo, &basic, sizeof(basic))) {
            ok = false;
        } else if ((basic.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) && !(basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
            std::lock_guard lock(subdirsLock);
            subdirs.emplace_back(std::move(h), basic.FileAttributes);
        } else if (!DeleteByHandle(h.get(), basic.FileAttributes)) {
            // A link (junction, symbolic link, mount point, any reparse point) is removed as a link, never entered.
            ok = false;
        }
    }, names.size() >= 32 ? IoThreads() : 1);
    for (auto& [h, attributes] : subdirs) {
        if (!DeleteContents(h.get(), depth + 1) || !DeleteByHandle(h.get(), attributes)) ok = false;
        h.reset();
    }
    return ok;
}

}  // namespace

bool DeleteTree(const std::wstring& dir) {
    if (!Exists(dir)) return true;
    // The root itself is opened without following a link; a link is removed as a link.
    const auto open = [&](DWORD access) {
        return UniqueHandle(::CreateFileW(path::Long(dir).c_str(), access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                          OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    };
    UniqueHandle root = open(kEntryAccess | FILE_LIST_DIRECTORY);
    if (!root && ::GetLastError() == ERROR_ACCESS_DENIED) root = open(kEntryAccess);  // a file that refuses read access
    if (!root) {
        const DWORD e = ::GetLastError();
        return e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND;
    }
    FILE_BASIC_INFO basic{};
    if (!::GetFileInformationByHandleEx(root.get(), FileBasicInfo, &basic, sizeof(basic))) return false;
    if (security::IsElevated()) {
        // An elevated process deletes only what it was asked to: a parent folder swapped for a junction between the
        // caller's checks and the open above shows up in the final path.
        std::wstring full(32768, L'\0'), longName(32768, L'\0');
        const DWORD n = ::GetFullPathNameW(path::Long(dir).c_str(), static_cast<DWORD>(full.size()), full.data(), nullptr);
        full.resize(n < full.size() ? n : 0);
        const DWORD m = full.empty() ? 0 : ::GetLongPathNameW(full.c_str(), longName.data(), static_cast<DWORD>(longName.size()));
        longName.resize(m < longName.size() ? m : 0);
        if (StartsWithIgnoreCase(longName, L"\\\\?\\UNC\\")) longName = L"\\\\" + longName.substr(8);
        else if (StartsWithIgnoreCase(longName, L"\\\\?\\")) longName = longName.substr(4);
        while (longName.size() > 3 && longName.back() == L'\\') longName.pop_back();
        if (longName.empty() || !EqualsIgnoreCase(FinalPath(root.get()), longName)) return false;
    }
    const DWORD attributes = basic.FileAttributes;
    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT) && !DeleteContents(root.get(), 0)) return false;
    return DeleteByHandle(root.get(), attributes);
}

}  // namespace fsetup::fs
