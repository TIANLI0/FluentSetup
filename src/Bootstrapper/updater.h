// Built-in updates of an installed app: uninstall.exe /check-update and /install-update.
#pragma once

#include "Bootstrapper/args.h"
#include "Bootstrapper/localization.h"
#include "Install.Core/package.h"

namespace fsetup::boot {

// Runs /check-update or /install-update with the package's "update" settings and returns the process exit code:
// /check-update: 0 up to date, 18 (UpdateAvailable) a newer version is published, 19 (UpdateFailed) no answer.
// /install-update: 0 up to date or the new Setup.exe was started, 2 the user said "Not now" or cancelled, 19 the update
// could not be checked, downloaded or verified. Also 3 (no update source), 11 (not installed), 16 (already running).
int RunUpdate(const install::Package& pkg, const Args& args, const Strings& strings);

}  // namespace fsetup::boot
