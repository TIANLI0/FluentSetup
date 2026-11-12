#include "Install.Core/shortcuts.h"

#include "Common/error.h"
#include "Common/win.h"

#include <objbase.h>
#include <shlobj.h>
#include <shobjidl.h>

namespace fsetup::install {

namespace {
template <typename T>
struct ComPtr {
    T* p = nullptr;
    ~ComPtr() {
        if (p) p->Release();
    }
    T** put() { return &p; }
    T* operator->() const { return p; }
};
}  // namespace

void CreateShortcut(const std::wstring& linkPath, const std::wstring& target, const std::wstring& workingDir,
                    const std::wstring& description) {
    ComPtr<IShellLinkW> link;
    HRESULT hr = ::CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_IShellLinkW,
                                    reinterpret_cast<void**>(link.put()));
    if (FAILED(hr)) throw Failure(ErrorCode::IoError, L"Cannot create ShellLink object", static_cast<DWORD>(hr));
    link->SetPath(target.c_str());
    link->SetWorkingDirectory(workingDir.c_str());
    if (!description.empty()) link->SetDescription(description.substr(0, MAX_PATH - 1).c_str());
    link->SetIconLocation(target.c_str(), 0);
    ComPtr<IPersistFile> file;
    hr = link->QueryInterface(IID_IPersistFile, reinterpret_cast<void**>(file.put()));
    if (FAILED(hr)) throw Failure(ErrorCode::IoError, L"IPersistFile unavailable", static_cast<DWORD>(hr));
    hr = file->Save(linkPath.c_str(), TRUE);
    if (FAILED(hr)) {
        const DWORD e = HRESULT_FACILITY(hr) == FACILITY_WIN32 ? HRESULT_CODE(hr) : static_cast<DWORD>(hr);
        throw Failure(ClassifyWin32(e), L"Cannot save shortcut " + linkPath, e);
    }
    ::SHChangeNotify(SHCNE_CREATE, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, linkPath.c_str(), nullptr);
}

}  // namespace fsetup::install
