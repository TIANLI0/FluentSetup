#include "Bootstrapper/runtime_probe.h"

#include "Common/text.h"
#include "Common/win.h"

#include <appmodel.h>
#include <WindowsAppSDK-VersionInfo.h>

#include <memory>
#include <vector>

namespace fsetup::boot {

namespace {

constexpr UINT32 ProcessArchitecture() {
#if defined(_M_ARM64)
    return PROCESSOR_ARCHITECTURE_ARM64;
#elif defined(_M_X64)
    return PROCESSOR_ARCHITECTURE_AMD64;
#else
    return PROCESSOR_ARCHITECTURE_INTEL;
#endif
}

DWORD OsBuild() {
    using RtlGetVersionFn = LONG(WINAPI*)(OSVERSIONINFOW*);
    const auto fn = reinterpret_cast<RtlGetVersionFn>(::GetProcAddress(::GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
    OSVERSIONINFOW info{sizeof(info)};
    if (fn && fn(&info) == 0) return info.dwBuildNumber;
    return 0;
}

}  // namespace

RuntimeProbe ProbeWindowsAppRuntime() {
    RuntimeProbe probe;
    const DWORD build = OsBuild();
    if (build < 17763) {
        probe.detail = Printf(L"Windows build %lu is older than 17763 (WinUI 3 minimum)", build);
        return probe;
    }
    UINT32 count = 0, bufferLength = 0;
    const PCWSTR family = WINDOWSAPPSDK_RUNTIME_PACKAGE_FRAMEWORK_PACKAGEFAMILYNAME_W;
    LONG rc = ::GetPackagesByPackageFamily(family, &count, nullptr, &bufferLength, nullptr);
    if (rc != ERROR_INSUFFICIENT_BUFFER || count == 0) {
        probe.detail = std::wstring(L"No package of family ") + family + L" is registered for this user";
        return probe;
    }
    std::vector<PWSTR> names(count);
    std::vector<wchar_t> buffer(bufferLength);
    rc = ::GetPackagesByPackageFamily(family, &count, names.data(), &bufferLength, buffer.data());
    if (rc != ERROR_SUCCESS) {
        probe.detail = Printf(L"GetPackagesByPackageFamily failed (%ld)", rc);
        return probe;
    }
    const UINT64 minimum = WINDOWSAPPSDK_RUNTIME_VERSION_UINT64;
    std::wstring seen;
    for (UINT32 i = 0; i < count; ++i) {
        UINT32 idLength = 0;
        if (::PackageIdFromFullName(names[i], PACKAGE_INFORMATION_BASIC, &idLength, nullptr) != ERROR_INSUFFICIENT_BUFFER) continue;
        auto idBuffer = std::make_unique<BYTE[]>(idLength);
        if (::PackageIdFromFullName(names[i], PACKAGE_INFORMATION_BASIC, &idLength, idBuffer.get()) != ERROR_SUCCESS) continue;
        const auto* id = reinterpret_cast<const PACKAGE_ID*>(idBuffer.get());
        seen += std::wstring(names[i]) + L"; ";
        if (id->processorArchitecture == ProcessArchitecture() && id->version.Version >= minimum) {
            probe.compatible = true;
            probe.detail = std::wstring(L"Found ") + names[i] + L" (needs >= " WINDOWSAPPSDK_RUNTIME_VERSION_DOTQUADSTRING_W L")";
            return probe;
        }
    }
    probe.detail = L"No compatible runtime (need " WINDOWSAPPSDK_RUNTIME_VERSION_DOTQUADSTRING_W L" for this architecture). Found: " + seen;
    return probe;
}

}  // namespace fsetup::boot
