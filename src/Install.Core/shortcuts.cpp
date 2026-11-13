#include "Install.Core/shortcuts.h"

#include "Common/error.h"
#include "Common/win.h"

#include <objbase.h>
#include <propkey.h>
#include <propvarutil.h>
#include <shlobj.h>
#include <shobjidl.h>

#pragma comment(lib, "propsys.lib")

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

void SetStringProperty(IPropertyStore* store, REFPROPERTYKEY key, const std::wstring& value) {
    PROPVARIANT v;
    if (FAILED(::InitPropVariantFromString(value.c_str(), &v))) throw Failure(ErrorCode::IoError, L"Cannot set a shortcut property");
    const HRESULT hr = store->SetValue(key, v);
    ::PropVariantClear(&v);
    if (FAILED(hr)) throw Failure(ErrorCode::IoError, L"Cannot set a shortcut property", static_cast<DWORD>(hr));
}
}  // namespace

void CreateShortcut(const std::wstring& linkPath, const ShortcutSpec& spec) {
    ComPtr<IShellLinkW> link;
    HRESULT hr = ::CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_IShellLinkW,
                                    reinterpret_cast<void**>(link.put()));
    if (FAILED(hr)) throw Failure(ErrorCode::IoError, L"Cannot create ShellLink object", static_cast<DWORD>(hr));
    link->SetPath(spec.target.c_str());
    link->SetWorkingDirectory(spec.workingDir.c_str());
    if (!spec.arguments.empty()) link->SetArguments(spec.arguments.c_str());
    if (!spec.description.empty()) link->SetDescription(spec.description.substr(0, MAX_PATH - 1).c_str());
    link->SetIconLocation(spec.icon.empty() ? spec.target.c_str() : spec.icon.c_str(), spec.iconIndex);
    if (!spec.appUserModelId.empty() || !spec.toastActivatorClsid.empty()) {
        ComPtr<IPropertyStore> store;
        hr = link->QueryInterface(IID_IPropertyStore, reinterpret_cast<void**>(store.put()));
        if (FAILED(hr)) throw Failure(ErrorCode::IoError, L"IPropertyStore unavailable", static_cast<DWORD>(hr));
        if (!spec.appUserModelId.empty()) SetStringProperty(store.p, PKEY_AppUserModel_ID, spec.appUserModelId);
        if (!spec.toastActivatorClsid.empty()) {
            CLSID clsid{};
            if (FAILED(::CLSIDFromString(spec.toastActivatorClsid.c_str(), &clsid))) throw Failure(ErrorCode::InvalidArguments, L"Invalid toast activator CLSID");
            PROPVARIANT v;
            if (FAILED(::InitPropVariantFromCLSID(clsid, &v))) throw Failure(ErrorCode::IoError, L"Cannot set the toast activator");
            hr = store->SetValue(PKEY_AppUserModel_ToastActivatorCLSID, v);
            ::PropVariantClear(&v);
            if (FAILED(hr)) throw Failure(ErrorCode::IoError, L"Cannot set the toast activator", static_cast<DWORD>(hr));
        }
        hr = store->Commit();
        if (FAILED(hr)) throw Failure(ErrorCode::IoError, L"Cannot save the shortcut properties", static_cast<DWORD>(hr));
    }
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
