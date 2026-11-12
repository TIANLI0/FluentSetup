// Entry point of FluentSetup.UI.exe.
//
// The XAML-generated main is disabled (DISABLE_XAML_GENERATED_MAIN) and the Windows App SDK auto-initializers are off.
// Order of operations, each step reporting failures to the bootstrapper before exiting with a classified exit code:
//   1. parse + validate arguments
//   2. connect to the bootstrapper pipe and verify the server PID
//   3. load Microsoft.WindowsAppRuntime.Bootstrap.dll dynamically from our own directory (a missing DLL must not turn into a
//      loader error before wWinMain) and call MddBootstrapInitialize2 for the exact major.minor + minimum version we were
//      compiled against
//   4. only then touch any Microsoft.UI.Xaml type: Application::Start
#include "pch.h"

#include "App.xaml.h"
#include "Common/text.h"
#include "IPC/protocol.h"
#include "Strings.h"
#include "UiChannel.h"

#include <MddBootstrap.h>
#include <WindowsAppSDK-VersionInfo.h>

#include <fstream>
#include <sstream>

using namespace fsetup;

namespace fsui {
std::wstring g_simulate;  // test hook: runtime-missing | crash-before-ready | crash-after-ready | hang
}

namespace {

void ReportInitFailure(const char* stage, HRESULT hr) {
    json::Value m;
    m.set("t", "init_failed");
    m.set("stage", stage);
    m.set("hr", static_cast<int64_t>(static_cast<uint32_t>(hr)));
    fsui::UiChannel::Get().Send(m);
}

std::wstring ModuleDirectory() {
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        const DWORD n = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (n == 0) return {};
        if (n < path.size()) {
            path.resize(n);
            break;
        }
        path.resize(path.size() * 2);
    }
    return path.substr(0, path.find_last_of(L'\\') + 1);
}

using MddInit2Fn = HRESULT(STDAPICALLTYPE*)(UINT32, PCWSTR, PACKAGE_VERSION, MddBootstrapInitializeOptions);
using MddShutdownFn = void(STDAPICALLTYPE*)();

struct RuntimeBinding {
    HMODULE module = nullptr;
    MddShutdownFn shutdown = nullptr;
};

HRESULT BindRuntime(RuntimeBinding& binding) {
    const std::wstring dll = ModuleDirectory() + L"Microsoft.WindowsAppRuntime.Bootstrap.dll";
    binding.module = ::LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!binding.module) return HRESULT_FROM_WIN32(::GetLastError());
    auto init = reinterpret_cast<MddInit2Fn>(::GetProcAddress(binding.module, "MddBootstrapInitialize2"));
    binding.shutdown = reinterpret_cast<MddShutdownFn>(::GetProcAddress(binding.module, "MddBootstrapShutdown"));
    if (!init || !binding.shutdown) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);

    PACKAGE_VERSION minVersion{};
    minVersion.Version = WINDOWSAPPSDK_RUNTIME_VERSION_UINT64;
    if (fsui::g_simulate == L"runtime-missing") {
        // Ask for a version that cannot exist, exercising the real "no compatible runtime" failure path of the API.
        minVersion.Version = 0x7FFF000000000000ull;
    }
    // No OnNoMatch_ShowUI: never pop the "install the runtime" dialog; the bootstrapper falls back to Win32 instead.
    return init(WINDOWSAPPSDK_RELEASE_MAJORMINOR, WINDOWSAPPSDK_RELEASE_VERSION_TAG_W, minVersion,
                MddBootstrapInitializeOptions_None);
}

std::string ReadFileUtf8(const std::wstring& path) {
    std::ifstream f(path, std::ios::binary);
    std::ostringstream ss;
    ss << f.rdbuf();
    return ss.str();
}

}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0;
    LPWSTR* argv = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    std::wstring pipe, preview;
    DWORD serverPid = 0;
    for (int i = 1; argv && i < argc; ++i) {
        const std::wstring_view a = argv[i];
        const bool hasValue = i + 1 < argc;
        if (a == L"--pipe" && hasValue) pipe = argv[++i];
        else if (a == L"--server-pid" && hasValue) serverPid = static_cast<DWORD>(_wtoi(argv[++i]));
        else if (a == L"--preview" && hasValue) preview = argv[++i];
        else if (a == L"--simulate" && hasValue) fsui::g_simulate = argv[++i];
        else {
            ::LocalFree(argv);
            return ipc::kUiExitBadArguments;
        }
    }
    if (argv) ::LocalFree(argv);

    auto& channel = fsui::UiChannel::Get();
    if (!preview.empty()) {
        json::Value hello;
        if (!json::Parse(ReadFileUtf8(preview), hello) || !hello.isObject()) return ipc::kUiExitBadArguments;
        channel.SetPreview(std::move(hello));
    } else {
        if (pipe.empty() || serverPid == 0) return ipc::kUiExitBadArguments;
        if (!channel.Connect(pipe, serverPid)) return ipc::kUiExitPipeConnectFailed;
    }

    RuntimeBinding runtime;
    const HRESULT hr = BindRuntime(runtime);
    if (FAILED(hr)) {
        ReportInitFailure("bootstrap", hr);
        channel.Shutdown();
        return hr == HRESULT_FROM_WIN32(ERROR_NO_MATCH) ? ipc::kUiExitRuntimeNotFound : ipc::kUiExitRuntimeInitFailed;
    }

    if (!channel.preview() && !channel.ReceiveHello(ipc::kConnectTimeoutMs)) {
        runtime.shutdown();
        channel.Shutdown();
        return ipc::kUiExitProtocolError;
    }
    fsui::SetStringTable(channel.hello()["strings"]);

    int exitCode = ipc::kUiExitOk;
    try {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        winrt::Microsoft::UI::Xaml::Application::Start([](auto&&) {
            winrt::make<winrt::FluentSetupUI::implementation::App>();
        });
        exitCode = winrt::FluentSetupUI::implementation::App::ExitCode();
    } catch (const winrt::hresult_error& e) {
        ReportInitFailure("xaml", e.code());
        exitCode = ipc::kUiExitXamlStartFailed;
    } catch (...) {
        ReportInitFailure("xaml", E_UNEXPECTED);
        exitCode = ipc::kUiExitXamlStartFailed;
    }

    channel.Shutdown();
    runtime.shutdown();
    return exitCode;
}
