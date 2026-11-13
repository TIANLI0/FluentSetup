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

// Applies the "Program Files" style ACL (Admins/SYSTEM full control, Users and AppContainers read+execute) to a
// directory created for a per-machine install outside Program Files, so standard users cannot plant files in it.
DWORD ApplyMachineInstallAcl(const std::wstring& dir);

// Owner SID of a file or directory as a string ("S-1-5-32-544"). Empty on failure.
std::wstring OwnerSid(const std::wstring& path);

}  // namespace fsetup::security
