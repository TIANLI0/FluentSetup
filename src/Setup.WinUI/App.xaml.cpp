#include "pch.h"

#include "App.xaml.h"

#include "IPC/protocol.h"
#include "MainWindow.xaml.h"
#include "UiChannel.h"

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;

namespace winrt::FluentSetupUI::implementation {

App::App() {
    UnhandledException([](IInspectable const&, UnhandledExceptionEventArgs const& e) {
        // Tell the bootstrapper exactly what happened, then exit with a classified code. The bootstrapper keeps the
        // install engine running and continues in the Win32 front end; it never restarts this process.
        fsetup::json::Value m;
        m.set("t", "init_failed");
        m.set("stage", "unhandled");
        m.set("hr", static_cast<int64_t>(static_cast<uint32_t>(e.Exception().value)));
        fsui::UiChannel::Get().Send(m);
        e.Handled(true);
        SetExitCode(fsetup::ipc::kUiExitUnhandledException);
        Application::Current().Exit();
    });
}

void App::ApplyAccentOverride() {
    const auto& ui = fsui::UiChannel::Get().hello()["ui"];
    const std::string& accent = ui["accentColor"].asString();
    if (ui["useSystemAccent"].asBool(true) || accent.size() != 7 || accent[0] != '#') return;
    const auto hex = [&](size_t i) { return static_cast<uint8_t>(std::stoi(accent.substr(i, 2), nullptr, 16)); };
    const Windows::UI::Color base{255, hex(1), hex(3), hex(5)};
    const auto shade = [&](double f) {
        const auto mix = [&](uint8_t c) {
            return static_cast<uint8_t>(f >= 0 ? c + (255 - c) * f : c * (1 + f));
        };
        return Windows::UI::Color{255, mix(base.R), mix(base.G), mix(base.B)};
    };
    auto res = Resources();
    res.Insert(box_value(L"SystemAccentColor"), box_value(base));
    res.Insert(box_value(L"SystemAccentColorLight1"), box_value(shade(0.2)));
    res.Insert(box_value(L"SystemAccentColorLight2"), box_value(shade(0.4)));
    res.Insert(box_value(L"SystemAccentColorLight3"), box_value(shade(0.6)));
    res.Insert(box_value(L"SystemAccentColorDark1"), box_value(shade(-0.2)));
    res.Insert(box_value(L"SystemAccentColorDark2"), box_value(shade(-0.4)));
    res.Insert(box_value(L"SystemAccentColorDark3"), box_value(shade(-0.6)));
}

void App::OnLaunched(LaunchActivatedEventArgs const&) {
    try {
        // Same language as setup chose (and as the Win32 front end would use) for framework-provided strings.
        const std::wstring language = fsui::UiChannel::Get().hello()["language"].asWide();
        if (!language.empty()) winrt::Microsoft::Windows::Globalization::ApplicationLanguages::PrimaryLanguageOverride(language);
    } catch (...) {
    }
    try {
        ApplyAccentOverride();
    } catch (...) {
        // A malformed colour is cosmetic; keep the system accent.
    }
    window_ = make<MainWindow>();
    window_.Activate();
}

}  // namespace winrt::FluentSetupUI::implementation
