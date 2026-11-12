// Path handling. Everything that turns untrusted text (package manifests, install records, command lines, user input)
// into a file-system location goes through this module.
#pragma once

#include "Common/win.h"

#include <shlobj.h>

#include <string>
#include <string_view>
#include <vector>

namespace fsetup::path {

// Reasons a path can be rejected. Each maps to a "path.*" localization key.
enum class Problem {
    None,
    Empty,
    NotAbsolute,
    Network,
    InvalidChars,
    ReservedName,
    TooLong,
    Root,
    Protected,
    NotEmpty,
    OtherApp,
    Reparse,
    NoDrive,
    NotWritable,
    Space,
    Locked,
};
const char* ProblemKey(Problem p) noexcept;

constexpr size_t kMaxRelativePath = 1024;
constexpr size_t kMaxComponent = 255;
constexpr size_t kMaxInstallDir = 200;  // leaves room for relative paths below MAX_PATH-ish limits of other tools

// A *relative* path inside the install directory as written in a manifest or install record.
// Accepted: components separated by '\' (or '/', normalised to '\'); every component is a valid Windows file name.
// Rejected: empty, absolute, drive-relative, UNC, "." / ".." components, ':' (alternate data streams), control
// characters, <>"|?*, trailing dots/spaces, DOS device names (CON, NUL, COM1 ...), over-long paths.
bool ValidateRelative(std::wstring_view rel, std::wstring& normalized, std::wstring* reason = nullptr);

bool IsReservedDeviceName(std::wstring_view component) noexcept;
bool IsValidComponent(std::wstring_view component) noexcept;

// Absolute local directory path syntax check (drive-letter form only). On success `normalized` has no trailing slash.
Problem ValidateAbsoluteSyntax(std::wstring_view input, std::wstring& normalized);

std::wstring Join(std::wstring_view base, std::wstring_view rel);
std::wstring Parent(std::wstring_view p);
std::wstring FileName(std::wstring_view p);
// "\\?\C:\..." form for file-system calls on paths that might exceed MAX_PATH.
std::wstring Long(std::wstring_view absolute);
// True if `child` equals `parent` or lies below it (case-insensitive, component-boundary aware).
bool IsSameOrUnder(std::wstring_view child, std::wstring_view parent) noexcept;
bool IsStrictlyUnder(std::wstring_view child, std::wstring_view parent) noexcept;

std::wstring KnownFolder(REFKNOWNFOLDERID id);
std::wstring ModulePath();
std::wstring TempRoot();

// Replaces characters that cannot appear in a file name and trims dots/spaces; never returns empty.
std::wstring SanitizeName(std::wstring_view name);

// Splits "a\b\c" into components.
std::vector<std::wstring> Components(std::wstring_view rel);

}  // namespace fsetup::path
