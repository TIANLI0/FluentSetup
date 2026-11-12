#pragma once

#include <string>

namespace fsetup::install {

// Creates (or overwrites) a .lnk file. COM must be initialised on the calling thread. Throws Failure.
void CreateShortcut(const std::wstring& linkPath, const std::wstring& target, const std::wstring& workingDir,
                    const std::wstring& description);

}  // namespace fsetup::install
