// Stable error taxonomy shared by the engine, both UIs, the worker and the process exit code.
#pragma once

#include "Common/win.h"

#include <exception>
#include <string>

namespace fsetup {

// Values are part of the public contract (Setup.exe exit codes). Never renumber.
enum class ErrorCode : uint32_t {
    Ok = 0,
    Failed = 1,              // unclassified failure
    Cancelled = 2,           // user cancelled, system rolled back
    InvalidArguments = 3,    // bad command line / bad option value
    PackageCorrupt = 4,      // container, manifest or payload failed validation
    UnsupportedSystem = 5,   // OS too old / wrong architecture
    ElevationRequired = 6,   // needs administrator rights and they were not granted
    DiskFull = 7,
    FileInUse = 8,
    AccessDenied = 9,
    DowngradeBlocked = 10,
    NotInstalled = 11,       // uninstall requested but no valid install record
    IoError = 12,
    RegistryError = 13,
    Internal = 14,           // IPC / protocol / unexpected state
    UnsafePath = 15,         // path escapes the install root or crosses a reparse point
    AlreadyRunning = 16,     // another setup/uninstall of the same app is in progress
    PrerequisiteMissing = 17,  // a required Windows component (.NET Framework, WebView2) is not installed
    UpdateAvailable = 18,    // /check-update: a newer version is published (not a failure)
    UpdateFailed = 19,       // /check-update, /install-update: no answer from the update source, or the download was refused
};
inline constexpr uint32_t kLastErrorCode = 19;

inline const wchar_t* ErrorCodeName(ErrorCode c) noexcept {
    switch (c) {
        case ErrorCode::Ok: return L"Ok";
        case ErrorCode::Failed: return L"Failed";
        case ErrorCode::Cancelled: return L"Cancelled";
        case ErrorCode::InvalidArguments: return L"InvalidArguments";
        case ErrorCode::PackageCorrupt: return L"PackageCorrupt";
        case ErrorCode::UnsupportedSystem: return L"UnsupportedSystem";
        case ErrorCode::ElevationRequired: return L"ElevationRequired";
        case ErrorCode::DiskFull: return L"DiskFull";
        case ErrorCode::FileInUse: return L"FileInUse";
        case ErrorCode::AccessDenied: return L"AccessDenied";
        case ErrorCode::DowngradeBlocked: return L"DowngradeBlocked";
        case ErrorCode::NotInstalled: return L"NotInstalled";
        case ErrorCode::IoError: return L"IoError";
        case ErrorCode::RegistryError: return L"RegistryError";
        case ErrorCode::Internal: return L"Internal";
        case ErrorCode::UnsafePath: return L"UnsafePath";
        case ErrorCode::AlreadyRunning: return L"AlreadyRunning";
        case ErrorCode::PrerequisiteMissing: return L"PrerequisiteMissing";
        case ErrorCode::UpdateAvailable: return L"UpdateAvailable";
        case ErrorCode::UpdateFailed: return L"UpdateFailed";
    }
    return L"Unknown";
}

// The single exception type thrown across module boundaries inside one process.
// `detail` is a diagnostic (English, goes to the log and the "details" area); the user-facing
// sentence is derived from `code` through the localization table.
class Failure : public std::exception {
public:
    Failure(ErrorCode code, std::wstring detail, DWORD systemError = 0)
        : code_(code), detail_(std::move(detail)), system_(systemError) {}

    [[nodiscard]] ErrorCode code() const noexcept { return code_; }
    [[nodiscard]] const std::wstring& detail() const noexcept { return detail_; }
    [[nodiscard]] DWORD systemError() const noexcept { return system_; }
    const char* what() const noexcept override { return "fsetup::Failure"; }

private:
    ErrorCode code_;
    std::wstring detail_;
    DWORD system_;
};

// Maps a Win32 error to the closest taxonomy entry.
inline ErrorCode ClassifyWin32(DWORD e) noexcept {
    switch (e) {
        case ERROR_ACCESS_DENIED:
        case ERROR_PRIVILEGE_NOT_HELD:
        case ERROR_WRITE_PROTECT: return ErrorCode::AccessDenied;
        case ERROR_SHARING_VIOLATION:
        case ERROR_LOCK_VIOLATION:
        case ERROR_USER_MAPPED_FILE: return ErrorCode::FileInUse;
        case ERROR_DISK_FULL:
        case ERROR_HANDLE_DISK_FULL: return ErrorCode::DiskFull;
        case ERROR_ELEVATION_REQUIRED: return ErrorCode::ElevationRequired;
        default: return ErrorCode::IoError;
    }
}

[[noreturn]] inline void ThrowWin32(std::wstring detail, DWORD e = ::GetLastError()) {
    throw Failure(ClassifyWin32(e), std::move(detail), e);
}

}  // namespace fsetup
