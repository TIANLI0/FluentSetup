// Built-in updates of an installed app: uninstall.exe /check-update and /install-update. Also finds the Setup.exe of
// the installed version for a repair.
#pragma once

#include "Bootstrapper/args.h"
#include "Bootstrapper/localization.h"
#include "Common/win.h"
#include "Install.Core/installed.h"
#include "Install.Core/package.h"

#include <atomic>
#include <functional>

namespace fsetup::boot {

// Runs /check-update or /install-update with the package's "update" settings and returns the process exit code:
// /check-update: 0 up to date, 18 (UpdateAvailable) a newer version is published, 19 (UpdateFailed) no answer.
// /install-update: 0 up to date or the new Setup.exe was started, 2 the user said "Not now" or cancelled, 19 the update
// could not be checked, downloaded or verified. Also 3 (no update source), 11 (not installed), 16 (already running).
int RunUpdate(const install::Package& pkg, const Args& args, const Strings& strings);

// A verified Setup.exe of exactly the installed version, for a repair. The file is kept open (read-only, no other
// writers) until the object goes away; a downloaded copy is deleted then.
struct RepairPackage {
    std::wstring path;
    UniqueHandle lock;
    bool downloaded = false;
    RepairPackage() = default;
    RepairPackage(const RepairPackage&) = delete;
    RepairPackage& operator=(const RepairPackage&) = delete;
    ~RepairPackage();
};
enum class RepairLookup { Found, None, Failed };

// Looks for the setup of `version`: first the Setup.exe recorded at install time (record->sourcePackage, only while its
// SHA-256 still matches the record), then the package's update source: the GitHub release tagged v<version> or
// <version>, or the feed if it advertises exactly that version. A download gets the same checks as an update (https,
// published SHA-256, a FluentSetup package of this app id, architecture and version, update.signer). None: no source has
// it (`error` says why); Failed: a source could not be read or the download was refused.
RepairLookup FindRepairPackage(const install::Package& pkg, const std::wstring& version, const install::InstallRecord* record,
                               const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress,
                               RepairPackage& out, std::wstring& error);

// Runs a setup with /passive (or /quiet) and the UI language, and waits for it. False if it could not be started.
bool RunSetup(const std::wstring& file, bool quiet, const std::wstring& language, DWORD& exitCode, std::wstring& error);

}  // namespace fsetup::boot
