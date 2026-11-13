#pragma once

#include <string>

namespace fsetup::install {

struct ShortcutSpec {
    std::wstring target;       // absolute path of the program or file
    std::wstring arguments;    // already quoted command-line arguments
    std::wstring workingDir;
    std::wstring description;
    std::wstring icon;         // absolute path; empty = target
    int iconIndex = 0;
    std::wstring appUserModelId;       // System.AppUserModel.ID (toast notifications of unpackaged apps)
    std::wstring toastActivatorClsid;  // System.AppUserModel.ToastActivatorCLSID, "{...}"
};

// Creates (or overwrites) a .lnk file. COM must be initialised on the calling thread. Throws Failure.
void CreateShortcut(const std::wstring& linkPath, const ShortcutSpec& spec);

inline void CreateShortcut(const std::wstring& linkPath, const std::wstring& target, const std::wstring& workingDir,
                           const std::wstring& description) {
    ShortcutSpec spec;
    spec.target = target;
    spec.workingDir = workingDir;
    spec.description = description;
    CreateShortcut(linkPath, spec);
}

}  // namespace fsetup::install
