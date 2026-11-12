// File-system helpers with explicit error reporting. All functions take absolute paths and use the \\?\ form
// internally, so they work beyond MAX_PATH.
#pragma once

#include "Common/win.h"

#include <cstdint>
#include <span>
#include <string>
#include <vector>

namespace fsetup::fs {

bool Exists(const std::wstring& p);
bool IsDirectory(const std::wstring& p);
DWORD Attributes(const std::wstring& p);  // INVALID_FILE_ATTRIBUTES if missing

// True if the path itself (not its target) is a symbolic link, junction or volume mount point.
bool IsLinkLike(const std::wstring& p);

// Walks from the drive root to `p` and reports whether any *existing* component is a symlink/junction/mount point.
bool HasLinkInPath(const std::wstring& p);

// Final, link-resolved path of an open handle, without the \\?\ prefix. Empty on failure.
std::wstring FinalPath(HANDLE h);

// Creates every missing directory of `dir`. Newly created directories (outermost first) are appended to `created`.
// Throws Failure on error.
void CreateDirectories(const std::wstring& dir, std::vector<std::wstring>* created = nullptr,
                       SECURITY_ATTRIBUTES* sa = nullptr);

bool IsDirectoryEmpty(const std::wstring& dir);

std::vector<uint8_t> ReadAll(const std::wstring& p, uint64_t maxBytes);
// Writes to "<p>.tmp-<random>" then atomically replaces `p` (write-through). Throws Failure.
void WriteAllAtomic(const std::wstring& p, std::span<const uint8_t> data);

// Free bytes available to the caller on the volume that holds `p` (walks up to the nearest existing ancestor).
uint64_t FreeSpace(const std::wstring& p);

// Moves/renames on the same volume, replacing nothing. Returns the Win32 error (0 on success).
DWORD Move(const std::wstring& from, const std::wstring& to, bool replace = false);

// Deletes a file, clearing the read-only attribute first. Returns the Win32 error (0 on success / already gone).
DWORD DeleteFileForce(const std::wstring& p);

// Recursively deletes a directory tree that this process created itself (staging/backup/temp directories).
// Never follows links: link entries are removed as links. Returns false if anything remained.
bool DeleteTree(const std::wstring& dir);

}  // namespace fsetup::fs
