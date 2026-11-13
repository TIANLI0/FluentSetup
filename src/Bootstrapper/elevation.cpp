#include "Bootstrapper/elevation.h"

#include "Common/log.h"
#include "Common/security.h"

#include <ole2.h>
#include <exdisp.h>
#include <shellapi.h>
#include <shldisp.h>
#include <shlobj.h>
#include <shobjidl.h>

namespace fsetup::boot {

namespace {
template <typename T>
struct Com {
    T* p = nullptr;
    ~Com() {
        if (p) p->Release();
    }
    T** put() {
        if (p) p->Release();
        p = nullptr;
        return &p;
    }
    T* operator->() const { return p; }
    explicit operator bool() const { return p != nullptr; }
};

// Raymond Chen, "How can I launch an unelevated process from my elevated process?" - via the desktop's shell view.
bool ShellExecuteViaExplorer(const std::wstring& file, const std::wstring& dir, const std::wstring& parameters) {
    Com<IShellWindows> windows;
    if (FAILED(::CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(windows.put())))) return false;
    VARIANT empty{};
    long hwnd = 0;
    Com<IDispatch> dispatch;
    if (windows->FindWindowSW(&empty, &empty, SWC_DESKTOP, &hwnd, SWFO_NEEDDISPATCH, dispatch.put()) != S_OK) return false;
    Com<IServiceProvider> provider;
    if (FAILED(dispatch->QueryInterface(IID_PPV_ARGS(provider.put())))) return false;
    Com<IShellBrowser> browser;
    if (FAILED(provider->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(browser.put())))) return false;
    Com<IShellView> view;
    if (FAILED(browser->QueryActiveShellView(view.put()))) return false;
    Com<IDispatch> background;
    if (FAILED(view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(background.put())))) return false;
    Com<IShellFolderViewDual> folderView;
    if (FAILED(background->QueryInterface(IID_PPV_ARGS(folderView.put())))) return false;
    Com<IDispatch> app;
    if (FAILED(folderView->get_Application(app.put()))) return false;
    Com<IShellDispatch2> shell;
    if (FAILED(app->QueryInterface(IID_PPV_ARGS(shell.put())))) return false;
    BSTR bFile = ::SysAllocString(file.c_str());
    VARIANT vArgs{}, vDir{}, vOp{}, vShow{};
    if (!parameters.empty()) {
        vArgs.vt = VT_BSTR;
        vArgs.bstrVal = ::SysAllocString(parameters.c_str());
    }
    vDir.vt = VT_BSTR;
    vDir.bstrVal = ::SysAllocString(dir.c_str());
    vOp.vt = VT_BSTR;
    vOp.bstrVal = ::SysAllocString(L"open");
    vShow.vt = VT_I4;
    vShow.lVal = SW_SHOWNORMAL;
    const HRESULT hr = shell->ShellExecute(bFile, vArgs, vDir, vOp, vShow);
    ::SysFreeString(bFile);
    ::VariantClear(&vArgs);
    ::VariantClear(&vDir);
    ::VariantClear(&vOp);
    return SUCCEEDED(hr);
}
}  // namespace

UniqueHandle LaunchElevated(const std::wstring& exe, const std::wstring& parameters, HWND owner, DWORD& error) {
    SHELLEXECUTEINFOW sei{sizeof(sei)};
    sei.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    sei.hwnd = owner;
    sei.lpVerb = L"runas";
    sei.lpFile = exe.c_str();
    sei.lpParameters = parameters.c_str();
    sei.nShow = SW_HIDE;
    if (!::ShellExecuteExW(&sei)) {
        error = ::GetLastError();
        return {};
    }
    error = 0;
    return UniqueHandle(sei.hProcess);
}

bool ShellOpenAsUser(const std::wstring& file, const std::wstring& workingDir, const std::wstring& parameters) {
    const HRESULT init = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    auto direct = [&] {
        SHELLEXECUTEINFOW sei{sizeof(sei)};
        sei.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        sei.lpVerb = L"open";
        sei.lpFile = file.c_str();
        sei.lpParameters = parameters.empty() ? nullptr : parameters.c_str();
        sei.lpDirectory = workingDir.empty() ? nullptr : workingDir.c_str();
        sei.nShow = SW_SHOWNORMAL;
        return ::ShellExecuteExW(&sei) != FALSE;
    };
    bool ok = false;
    if (security::IsElevated()) {
        ok = ShellExecuteViaExplorer(file, workingDir, parameters);
        if (!ok && !security::HasLinkedToken() && security::IsSessionUser()) {
            // UAC is off (or this is the built-in Administrator) and this is the signed-in user: there is no
            // unelevated token to start the app with, the user's own programs get this same token, so starting it
            // directly gives it nothing it would not otherwise have. Happens without a desktop shell (Explorer).
            log::Info(L"No desktop shell; UAC is off for this account, starting %s directly", file.c_str());
            ok = direct();
        }
        if (!ok) log::Warn(L"Could not start %s as the desktop user; not starting it elevated", file.c_str());
    } else {
        ok = direct();
    }
    if (SUCCEEDED(init)) ::CoUninitialize();
    return ok;
}

}  // namespace fsetup::boot
