// DemoApp: a tiny native app used to exercise FluentSetup end to end (install, upgrade, uninstall).
// "DemoApp.exe --exit-code N" exits with N; "DemoApp.exe --version" prints the version to a file next to the exe so tests can check which build is installed.
#include <windows.h>

#include <cstdio>
#include <string>

#ifndef DEMO_VERSION
#define DEMO_VERSION L"0.0.0"
#endif

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR cmd, int) {
    // "--exit-code N": exit with N at once (tests use it to make an install action fail).
    if (const wchar_t* e = cmd ? wcsstr(cmd, L"--exit-code ") : nullptr) return _wtoi(e + 12);
    if (cmd && wcsstr(cmd, L"--version")) {
        wchar_t path[MAX_PATH];
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        std::wstring out(path);
        out = out.substr(0, out.find_last_of(L'\\') + 1) + L"version-check.txt";
        FILE* f = nullptr;
        if (_wfopen_s(&f, out.c_str(), L"w, ccs=UTF-8") == 0 && f) {
            fwprintf(f, L"%s", DEMO_VERSION);
            fclose(f);
        }
        return 0;
    }
    std::wstring text = L"FluentSetup DemoApp\n\nVersion ";
    text += DEMO_VERSION;
    MessageBoxW(nullptr, text.c_str(), L"DemoApp", MB_OK | MB_ICONINFORMATION);
    return 0;
}
