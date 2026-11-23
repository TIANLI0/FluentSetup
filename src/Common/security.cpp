#include "Common/security.h"

#include "Common/hash.h"
#include "Common/path.h"

#include <aclapi.h>
#include <memory>
#include <sddl.h>
#include <wtsapi32.h>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "wtsapi32.lib")

namespace fsetup::security {

bool IsElevated() {
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    UniqueHandle t(token);
    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    return ::GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size) && elevation.TokenIsElevated;
}

bool HasLinkedToken() {
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) return true;  // unknown: assume split token
    UniqueHandle t(token);
    TOKEN_ELEVATION_TYPE type = TokenElevationTypeDefault;
    DWORD size = 0;
    if (!::GetTokenInformation(token, TokenElevationType, &type, sizeof(type), &size)) return true;
    return type != TokenElevationTypeDefault;
}

bool IsSessionUser() {
    auto query = [](WTS_INFO_CLASS cls) {
        LPWSTR buffer = nullptr;
        DWORD bytes = 0;
        std::wstring value;
        if (::WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, cls, &buffer, &bytes)) {
            if (buffer) value = buffer;
            ::WTSFreeMemory(buffer);
        }
        return value;
    };
    const std::wstring sessionUser = query(WTSUserName), sessionDomain = query(WTSDomainName);
    if (sessionUser.empty()) return false;
    wchar_t name[256 + 1]{}, domain[256 + 1]{};
    DWORD nameLen = ARRAYSIZE(name), domainLen = ARRAYSIZE(domain);
    SID_NAME_USE use{};
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    UniqueHandle t(token);
    DWORD size = 0;
    ::GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    if (size == 0) return false;
    auto buffer = std::make_unique<uint8_t[]>(size);
    if (!::GetTokenInformation(token, TokenUser, buffer.get(), size, &size)) return false;
    if (!::LookupAccountSidW(nullptr, reinterpret_cast<TOKEN_USER*>(buffer.get())->User.Sid, name, &nameLen, domain, &domainLen, &use))
        return false;
    return ::CompareStringOrdinal(name, -1, sessionUser.c_str(), -1, TRUE) == CSTR_EQUAL &&
           ::CompareStringOrdinal(domain, -1, sessionDomain.c_str(), -1, TRUE) == CSTR_EQUAL;
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

std::wstring CreateRunDirectory(const std::wstring& prefix, DWORD& error) {
    error = 0;
    const std::wstring name = prefix + RandomToken().substr(0, 16);
    if (!IsElevated()) {
        const std::wstring dir = path::Join(path::TempRoot(), name);
        error = CreatePrivateDirectory(dir);
        return error ? std::wstring() : dir;
    }
    wchar_t windows[MAX_PATH + 1] = {};
    const UINT n = ::GetSystemWindowsDirectoryW(windows, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        error = n ? ERROR_BUFFER_OVERFLOW : ::GetLastError();
        return {};
    }
    const std::wstring dir = path::Join(path::Join(std::wstring(windows, n), L"Temp"), name);
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!::ConvertStringSecurityDescriptorToSecurityDescriptorW(L"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", SDDL_REVISION_1, &sd, nullptr)) {
        error = ::GetLastError();
        return {};
    }
    SECURITY_ATTRIBUTES sa{sizeof(sa), sd, FALSE};
    if (!::CreateDirectoryW(path::Long(dir).c_str(), &sa)) error = ::GetLastError();
    ::LocalFree(sd);
    return error ? std::wstring() : dir;
}

namespace {

// Owner Administrators; protected DACL: SYSTEM + Administrators full control, Users + ALL APPLICATION PACKAGES read &
// execute.
constexpr wchar_t kMachineInstallSddl[] =
    L"O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;AC)";

constexpr wchar_t kTrustedInstallerSid[] = L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

bool IsAdministrativeSid(PSID sid) {
    if (!sid || !::IsValidSid(sid)) return false;
    if (::IsWellKnownSid(sid, WinBuiltinAdministratorsSid) || ::IsWellKnownSid(sid, WinLocalSystemSid)) return true;
    PSID ti = nullptr;
    if (!::ConvertStringSidToSidW(kTrustedInstallerSid, &ti)) return false;
    const bool same = ::EqualSid(sid, ti) != FALSE;
    ::LocalFree(ti);
    return same;
}

}  // namespace

MachineInstallSecurity::MachineInstallSecurity() {
    if (::ConvertStringSecurityDescriptorToSecurityDescriptorW(kMachineInstallSddl, SDDL_REVISION_1, &sd_, nullptr)) {
        sa_.lpSecurityDescriptor = sd_;
    }
}

MachineInstallSecurity::~MachineInstallSecurity() {
    if (sd_) ::LocalFree(sd_);
}

DWORD ApplyMachineInstallAcl(const std::wstring& dir) {
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!::ConvertStringSecurityDescriptorToSecurityDescriptorW(kMachineInstallSddl, SDDL_REVISION_1, &sd, nullptr)) {
        return ::GetLastError();
    }
    BOOL present = FALSE, defaulted = FALSE;
    PACL dacl = nullptr;
    PSID owner = nullptr;
    ::GetSecurityDescriptorDacl(sd, &present, &dacl, &defaulted);
    ::GetSecurityDescriptorOwner(sd, &owner, &defaulted);
    // The owner too: whoever created the folder (a standard user who made it beforehand) could otherwise give
    // themselves access again, owners may always change the permissions.
    std::wstring target = dir;
    const DWORD e = ::SetNamedSecurityInfoW(target.data(), SE_FILE_OBJECT,
                                            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                                            owner, nullptr, dacl, nullptr);
    ::LocalFree(sd);
    return e;
}

bool IsAdministratorControlled(const std::wstring& dir) {
    PSID owner = nullptr;
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    std::wstring target = dir;
    if (::GetNamedSecurityInfoW(target.data(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, &owner, nullptr,
                                &dacl, nullptr, &sd) != ERROR_SUCCESS) {
        return false;
    }
    // Anything that lets its holder add, replace or delete files, or change the permissions. Inheritable entries count
    // too: the files and folders already inside carry them.
    constexpr ACCESS_MASK kWrite = FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_DELETE_CHILD | FILE_WRITE_ATTRIBUTES | DELETE |
                                   WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL | MAXIMUM_ALLOWED;
    bool controlled = IsAdministrativeSid(owner) && dacl != nullptr;  // no DACL at all: everyone has full control
    for (DWORD i = 0; controlled && i < dacl->AceCount; ++i) {
        void* ace = nullptr;
        if (!::GetAce(dacl, i, &ace)) {
            controlled = false;
            break;
        }
        const auto* header = static_cast<const ACE_HEADER*>(ace);
        if (header->AceType == ACCESS_DENIED_ACE_TYPE || header->AceType == ACCESS_DENIED_CALLBACK_ACE_TYPE ||
            header->AceType == ACCESS_DENIED_OBJECT_ACE_TYPE || header->AceType == ACCESS_DENIED_CALLBACK_OBJECT_ACE_TYPE) {
            continue;  // denying never grants
        }
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE && header->AceType != ACCESS_ALLOWED_CALLBACK_ACE_TYPE) {
            controlled = false;  // an allow entry of a kind not examined here: assume the worst
            break;
        }
        const auto* allowed = static_cast<const ACCESS_ALLOWED_ACE*>(ace);
        if (!(allowed->Mask & kWrite)) continue;
        PSID sid = const_cast<DWORD*>(&allowed->SidStart);
        // CREATOR OWNER only stands for whoever creates a child; OWNER RIGHTS for the owner, checked above.
        if (IsAdministrativeSid(sid) || ::IsWellKnownSid(sid, WinCreatorOwnerSid) || ::IsWellKnownSid(sid, WinCreatorOwnerRightsSid)) continue;
        controlled = false;
    }
    ::LocalFree(sd);
    return controlled;
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
