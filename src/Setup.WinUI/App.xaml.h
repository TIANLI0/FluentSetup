#pragma once

#include "App.xaml.g.h"

namespace winrt::FluentSetupUI::implementation {

struct App : AppT<App> {
    App();

    void OnLaunched(Microsoft::UI::Xaml::LaunchActivatedEventArgs const&);

    static int ExitCode() noexcept { return s_exitCode; }
    static void SetExitCode(int code) noexcept { s_exitCode = code; }

private:
    void ApplyAccentOverride();

    winrt::Microsoft::UI::Xaml::Window window_{nullptr};
    static inline int s_exitCode = 0;
};

}  // namespace winrt::FluentSetupUI::implementation
