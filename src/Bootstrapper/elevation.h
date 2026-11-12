#pragma once

#include "Common/win.h"

#include <string>

namespace fsetup::boot {

// Starts `exe` elevated (UAC prompt) with `parameters`. Returns the process handle; on failure returns an empty handle
// and sets `error` (ERROR_CANCELLED when the user declined the prompt).
UniqueHandle LaunchElevated(const std::wstring& exe, const std::wstring& parameters, HWND owner, DWORD& error);

// Opens a file/program as the interactive, non-elevated user. If this process is not elevated it simply uses
// ShellExecuteEx; if it is elevated it asks the desktop's Explorer process to do it, so that the app never inherits an
// administrator token from setup.
bool ShellOpenAsUser(const std::wstring& file, const std::wstring& workingDir);

}  // namespace fsetup::boot
