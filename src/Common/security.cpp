#include "Common/security.h"

#include "Common/path.h"

#include <aclapi.h>
#include <memory>
#include <sddl.h>

#pragma comment(lib, "advapi32.lib")

namespace fsetup::security {

bool IsElevated() {
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    UniqueHandle t(token);
    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    return ::GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size) && elevation.TokenIsElevated;
}

namespace {

std::wstring CurrentUserSid() {
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    UniqueHandle t(token);
    DWORD size = 0;
    ::GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::wstring result;
    if (size == 0) return result;
    auto buffer = std::make_unique<uint8_t[]>(size);
    if (::GetTokenInformation(token, TokenUser, buffer.get(), size, &size)) {
        wchar_t* s = nullptr;
        if (::ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.get())->User.Sid, &s)) {
            result = s;
            ::LocalFree(s);
        }
    }
    return result;
}

}  // namespace

DWORD CreatePrivateDirectory(const std::wstring& dir) {
    const std::wstring user = CurrentUserSid();
    if (user.empty()) return ERROR_INVALID_SID;
    const std::wstring sddl = L"D:P(A;OICI;FA;;;" + user + L")(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!::ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) {
        return ::GetLastError();
    }
    SECURITY_ATTRIBUTES sa{sizeof(sa), sd, FALSE};
    const BOOL ok = ::CreateDirectoryW(path::Long(dir).c_str(), &sa);
    const DWORD e = ok ? 0 : ::GetLastError();
    ::LocalFree(sd);
    return e;
}

DWORD ApplyMachineInstallAcl(const std::wstring& dir) {
    // Protected DACL: SYSTEM + Administrators full control, Users + ALL APPLICATION PACKAGES read & execute.
    static constexpr wchar_t kSddl[] =
        L"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;AC)";
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!::ConvertStringSecurityDescriptorToSecurityDescriptorW(kSddl, SDDL_REVISION_1, &sd, nullptr)) {
        return ::GetLastError();
    }
    BOOL present = FALSE, defaulted = FALSE;
    PACL dacl = nullptr;
    ::GetSecurityDescriptorDacl(sd, &present, &dacl, &defaulted);
    std::wstring target = dir;
    const DWORD e = ::SetNamedSecurityInfoW(target.data(), SE_FILE_OBJECT,
                                            DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, nullptr,
                                            nullptr, dacl, nullptr);
    ::LocalFree(sd);
    return e;
}

std::wstring OwnerSid(const std::wstring& p) {
    PSID owner = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    std::wstring target = p;
    if (::GetNamedSecurityInfoW(target.data(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr,
                                nullptr, &sd) != ERROR_SUCCESS) {
        return {};
    }
    std::wstring result;
    wchar_t* s = nullptr;
    if (owner && ::ConvertSidToStringSidW(owner, &s)) {
        result = s;
        ::LocalFree(s);
    }
    ::LocalFree(sd);
    return result;
}

}  // namespace fsetup::security
