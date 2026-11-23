// Older installations made by Windows Installer (install.legacyMsi): products of an MSI (WiX, InstallShield, Visual
// Studio installer projects) that this package replaces. Setup finds them by product code or upgrade code and removes
// them through Windows Installer itself, so neither their files nor their registration (which could restore files by
// self-repair) are left behind. msi.dll is delay-loaded (FluentSetup.Stub.vcxproj): only setups that use the setting
// load it.
#pragma once

#include "Common/win.h"

#include <string>
#include <string_view>
#include <vector>

namespace fsetup::install {

// "{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}" (braces required, any case) -> upper case. False for anything else.
bool NormalizeMsiCode(std::wstring_view code, std::wstring& out);

// Where Windows Installer registered a product: for all users, or for the current user (unmanaged: installed by the
// user; managed: assigned by policy, which only an administrator can remove).
enum class MsiContext { Machine, UserUnmanaged, UserManaged };
const wchar_t* MsiContextName(MsiContext c) noexcept;
// Removing it needs administrator rights.
inline bool MsiNeedsElevation(MsiContext c) noexcept { return c != MsiContext::UserUnmanaged; }

struct MsiProduct {
    std::wstring productCode;  // normalized
    MsiContext context = MsiContext::Machine;
    std::wstring name, version, installLocation;  // as registered (any may be empty)
};

// The installed products among `productCodes` and those related to `upgradeCodes` (MsiEnumRelatedProducts), each with
// the context it is installed in (per machine, or for the current user), without duplicates. Read-only.
std::vector<MsiProduct> FindMsiProducts(const std::vector<std::wstring>& productCodes, const std::vector<std::wstring>& upgradeCodes);

// What a removal's result means for setup.
enum class MsiOutcome {
    Removed,         // 0
    NotInstalled,    // 1605 ERROR_UNKNOWN_PRODUCT (gone meanwhile): fine
    RebootRequired,  // 3010 / 1641: removed, a restart finishes it (setup then exits with 3010)
    Busy,            // 1618 ERROR_INSTALL_ALREADY_RUNNING: another installation is in progress
    Declined,        // the UAC prompt for msiexec was declined (ERROR_CANCELLED) or the user cancelled (1602)
    Failed,          // anything else
};
MsiOutcome ClassifyMsiResult(DWORD code) noexcept;

// The command line for an elevated removal through msiexec (no UI, never restarts the PC).
std::wstring MsiexecRemoveArguments(const std::wstring& productCode);

// Removes a product, quietly and without restarting the PC (REBOOT=ReallySuppress). With administrator rights, or for a
// per-user product of the current user, in this process (MsiConfigureProductEx, no UI); a per-machine (or managed)
// product without them through %SystemRoot%\System32\msiexec.exe /x started with "runas" (one UAC prompt). `code`
// receives the Windows Installer result.
MsiOutcome RemoveMsiProduct(const MsiProduct& product, DWORD& code);

}  // namespace fsetup::install
