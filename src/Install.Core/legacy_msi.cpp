#include "Install.Core/legacy_msi.h"

#include "Common/log.h"
#include "Common/path.h"
#include "Common/security.h"
#include "Common/text.h"

#include <msi.h>
#include <shellapi.h>

#include <algorithm>

#pragma comment(lib, "msi.lib")

namespace fsetup::install {

namespace {

constexpr DWORD kTimeoutMs = 1800u * 1000;  // like a prerequisite installer

std::wstring ProductInfo(const std::wstring& code, MSIINSTALLCONTEXT context, const wchar_t* property) {
    wchar_t buffer[1024] = {};
    DWORD size = ARRAYSIZE(buffer);
    // szUserSid null: the current user (and it must be null for the machine context).
    if (::MsiGetProductInfoExW(code.c_str(), nullptr, context, property, buffer, &size) != ERROR_SUCCESS) return {};
    return std::wstring(buffer, std::min<DWORD>(size, ARRAYSIZE(buffer) - 1));
}

// Every instance of one product visible to this user: per machine and the current user's.
void AddInstances(const std::wstring& code, std::vector<MsiProduct>& out) {
    for (DWORD index = 0; index < 8; ++index) {
        wchar_t found[39] = {};
        MSIINSTALLCONTEXT context = MSIINSTALLCONTEXT_NONE;
        const UINT r = ::MsiEnumProductsExW(code.c_str(), nullptr, MSIINSTALLCONTEXT_ALL, index, found, &context, nullptr, nullptr);
        if (r != ERROR_SUCCESS) {
            if (r != ERROR_NO_MORE_ITEMS && r != ERROR_UNKNOWN_PRODUCT) log::Warn(L"Windows Installer: cannot look up %s (%u)", code.c_str(), r);
            return;
        }
        MsiProduct p;
        p.productCode = code;
        p.context = context == MSIINSTALLCONTEXT_MACHINE        ? MsiContext::Machine
                    : context == MSIINSTALLCONTEXT_USERMANAGED ? MsiContext::UserManaged
                                                               : MsiContext::UserUnmanaged;
        if (std::any_of(out.begin(), out.end(), [&](const MsiProduct& o) { return o.productCode == p.productCode && o.context == p.context; })) continue;
        p.name = ProductInfo(code, context, INSTALLPROPERTY_PRODUCTNAME);
        p.version = ProductInfo(code, context, INSTALLPROPERTY_VERSIONSTRING);
        p.installLocation = ProductInfo(code, context, INSTALLPROPERTY_INSTALLLOCATION);
        out.push_back(std::move(p));
    }
}

}  // namespace

bool NormalizeMsiCode(std::wstring_view code, std::wstring& out) {
    if (code.size() != 38 || code.front() != L'{' || code.back() != L'}') return false;
    std::wstring s(code);
    for (size_t i = 1; i < 37; ++i) {
        wchar_t& c = s[i];
        if (i == 9 || i == 14 || i == 19 || i == 24) {
            if (c != L'-') return false;
            continue;
        }
        if (c >= L'a' && c <= L'f') c = static_cast<wchar_t>(c - L'a' + L'A');
        if (!((c >= L'0' && c <= L'9') || (c >= L'A' && c <= L'F'))) return false;
    }
    out = std::move(s);
    return true;
}

const wchar_t* MsiContextName(MsiContext c) noexcept {
    switch (c) {
        case MsiContext::Machine: return L"per machine";
        case MsiContext::UserUnmanaged: return L"per user";
        case MsiContext::UserManaged: return L"per user, managed";
    }
    return L"?";
}

std::vector<MsiProduct> FindMsiProducts(const std::vector<std::wstring>& productCodes, const std::vector<std::wstring>& upgradeCodes) {
    std::vector<MsiProduct> out;
    if (productCodes.empty() && upgradeCodes.empty()) return out;
    std::vector<std::wstring> codes;
    const auto add = [&](const std::wstring& code) {
        std::wstring n;
        if (NormalizeMsiCode(code, n) && std::find(codes.begin(), codes.end(), n) == codes.end()) codes.push_back(std::move(n));
    };
    for (const auto& c : productCodes) add(c);
    for (const auto& u : upgradeCodes) {
        std::wstring upgrade;
        if (!NormalizeMsiCode(u, upgrade)) continue;
        for (DWORD index = 0; index < 64; ++index) {
            wchar_t product[39] = {};
            const UINT r = ::MsiEnumRelatedProductsW(upgrade.c_str(), 0, index, product);
            if (r != ERROR_SUCCESS) {
                if (r != ERROR_NO_MORE_ITEMS) log::Warn(L"Windows Installer: cannot enumerate the products of upgrade code %s (%u)", upgrade.c_str(), r);
                break;
            }
            add(product);
        }
    }
    for (const auto& code : codes) AddInstances(code, out);
    return out;
}

MsiOutcome ClassifyMsiResult(DWORD code) noexcept {
    switch (code) {
        case ERROR_SUCCESS: return MsiOutcome::Removed;
        case ERROR_UNKNOWN_PRODUCT: return MsiOutcome::NotInstalled;
        case ERROR_SUCCESS_REBOOT_REQUIRED:
        case ERROR_SUCCESS_REBOOT_INITIATED: return MsiOutcome::RebootRequired;
        case ERROR_INSTALL_ALREADY_RUNNING: return MsiOutcome::Busy;
        case ERROR_CANCELLED:
        case ERROR_INSTALL_USEREXIT: return MsiOutcome::Declined;
        default: return MsiOutcome::Failed;
    }
}

std::wstring MsiexecRemoveArguments(const std::wstring& productCode) {
    return L"/x " + productCode + L" /qn REBOOT=ReallySuppress";
}

MsiOutcome RemoveMsiProduct(const MsiProduct& product, DWORD& code) {
    std::wstring productCode;
    if (!NormalizeMsiCode(product.productCode, productCode)) {
        code = ERROR_INVALID_PARAMETER;
        return MsiOutcome::Failed;
    }
    if (!MsiNeedsElevation(product.context) || security::IsElevated()) {
        // In this process: no UI at all, no restart (Windows Installer reports 3010 instead).
        const INSTALLUILEVEL previous = ::MsiSetInternalUI(INSTALLUILEVEL_NONE, nullptr);
        code = ::MsiConfigureProductExW(productCode.c_str(), INSTALLLEVEL_DEFAULT, INSTALLSTATE_ABSENT, L"REBOOT=ReallySuppress");
        ::MsiSetInternalUI(previous, nullptr);
        return ClassifyMsiResult(code);
    }
    // A product installed for all users, from a setup without administrator rights: Windows' own msiexec, elevated.
    wchar_t system[MAX_PATH + 1] = {};
    const UINT n = ::GetSystemDirectoryW(system, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        code = ::GetLastError();
        return MsiOutcome::Failed;
    }
    const std::wstring msiexec = path::Join(std::wstring(system, n), L"msiexec.exe");
    const std::wstring args = MsiexecRemoveArguments(productCode);
    log::Info(L"Running %s %s with administrator rights", msiexec.c_str(), args.c_str());
    SHELLEXECUTEINFOW sei{sizeof(sei)};
    sei.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
    sei.lpVerb = L"runas";
    sei.lpFile = msiexec.c_str();
    sei.lpParameters = args.c_str();
    sei.lpDirectory = system;
    sei.nShow = SW_HIDE;
    if (!::ShellExecuteExW(&sei) || !sei.hProcess) {
        code = ::GetLastError();
        return code == ERROR_CANCELLED ? MsiOutcome::Declined : MsiOutcome::Failed;
    }
    UniqueHandle process(sei.hProcess);
    if (::WaitForSingleObject(process.get(), kTimeoutMs) != WAIT_OBJECT_0) {
        code = WAIT_TIMEOUT;
        return MsiOutcome::Failed;
    }
    DWORD exit = ERROR_INSTALL_FAILURE;
    ::GetExitCodeProcess(process.get(), &exit);
    code = exit;
    return ClassifyMsiResult(code);
}

}  // namespace fsetup::install
