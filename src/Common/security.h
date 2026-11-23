// Token and ACL helpers.
#pragma once

#include "Common/win.h"

#include <string>

namespace fsetup::security {

bool IsElevated();

// True when this process runs with the full token of a UAC split-token account (an admin that also has a filtered,
// unelevated token). False when UAC is off or the account is the built-in Administrator: then the signed-in user's
// own processes, Explorer included, run with this same token.
bool HasLinkedToken();

// True when the process runs as the user signed in to this Windows session (not an over-the-shoulder account).
bool IsSessionUser();

// Creates a fresh directory that only the current user, SYSTEM and Administrators can access. Fails if it exists
// (prevents pre-created/hijacked temp directories). Returns the Win32 error (0 on success).
DWORD CreatePrivateDirectory(const std::wstring& dir);

// A fresh, randomly named directory for files this process is about to run: "<prefix><random>". Unelevated, in the
// user's %TEMP% (CreatePrivateDirectory). Elevated, in %SystemRoot%\Temp, owned by Administrators, with only
// Administrators and SYSTEM in its DACL: the user's own %TEMP% is writable by their unelevated programs, which could
// plant DLLs next to what runs elevated, or rename the folder (they may delete anything in %TEMP%) and put another in
// its place. In %SystemRoot%\Temp standard users may create entries but not rename or delete those of others.
// Returns the path, or "" with `error` set.
std::wstring CreateRunDirectory(const std::wstring& prefix, DWORD& error);

// Applies the "Program Files" style security (owner Administrators; protected DACL: Admins/SYSTEM full control, Users
// and AppContainers read+execute) to a directory for a per-machine install outside Program Files, so standard users
// cannot plant files in it nor take it back as its owner. Needs administrator rights.
DWORD ApplyMachineInstallAcl(const std::wstring& dir);

// The same security as a SECURITY_ATTRIBUTES, for creating per-machine install directories with it (nothing can be
// planted between the creation and a later ACL change). get() is nullptr if the descriptor could not be built.
class MachineInstallSecurity {
public:
    MachineInstallSecurity();
    ~MachineInstallSecurity();
    MachineInstallSecurity(const MachineInstallSecurity&) = delete;
    MachineInstallSecurity& operator=(const MachineInstallSecurity&) = delete;
    [[nodiscard]] SECURITY_ATTRIBUTES* get() noexcept { return sd_ ? &sa_ : nullptr; }

private:
    PSECURITY_DESCRIPTOR sd_ = nullptr;
    SECURITY_ATTRIBUTES sa_{sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE};
};

// True when the directory is owned by Administrators, SYSTEM or TrustedInstaller and its DACL lets nobody else write,
// delete or change permissions (inheritable entries included). False when its security cannot be read.
bool IsAdministratorControlled(const std::wstring& dir);

// Owner SID of a file or directory as a string ("S-1-5-32-544"). Empty on failure.
std::wstring OwnerSid(const std::wstring& path);

}  // namespace fsetup::security
