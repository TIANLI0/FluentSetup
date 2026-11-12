#pragma once

#include "MainWindow.g.h"

#include "Common/json.h"

#include <functional>

namespace winrt::FluentSetupUI::implementation {

enum class Page { Welcome, License, Progress, Result };
enum class ResultKind { None, Success, Failed, Cancelled };

struct MainWindow : MainWindowT<MainWindow> {
    MainWindow();

    void OnPrimaryClick(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::RoutedEventArgs const&);
    void OnSecondaryClick(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::RoutedEventArgs const&);
    void OnCancelClick(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::RoutedEventArgs const&);
    void OnBrowseClick(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::RoutedEventArgs const&);
    void OnOpenFolderClick(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::RoutedEventArgs const&);
    void OnViewLogClick(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::RoutedEventArgs const&);
    void OnScopeChanged(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::Controls::SelectionChangedEventArgs const&);
    void OnLicenseChanged(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::RoutedEventArgs const&);

private:
    void InitWindow();
    winrt::fire_and_forget LoadIconFrame(std::wstring path, int pixels,
                                         std::function<void(winrt::Microsoft::UI::Xaml::Media::ImageSource const&)> apply);
    void ApplyHello();
    void ApplyStaticText();
    void UpdateInfoLines();

    void ShowPage(Page page);
    void UpdateFooter();
    [[nodiscard]] bool HasOptions() const;
    [[nodiscard]] bool ShowsLicense() const;
    [[nodiscard]] bool LaunchOffered() const;
    [[nodiscard]] std::wstring PrimaryVerb() const;
    [[nodiscard]] bool NeedsElevation() const;
    void StartInstall();
    void Finish(bool launch);
    void RequestClose();
    winrt::fire_and_forget ConfirmCancel();

    void HandleMessage(fsetup::json::Value message);
    void OnProgress(const fsetup::json::Value& m);
    void OnDone(const fsetup::json::Value& m);
    void OnPathCheck(const fsetup::json::Value& m);
    void OnPeerLost();
    void RequestPathCheck(const std::wstring& path);
    winrt::fire_and_forget RunPreviewProgress();

    [[nodiscard]] std::wstring AppName() const;
    [[nodiscard]] bool IsUninstall() const;
    void UpdateSpaceText();

    HWND hwnd_ = nullptr;
    Page page_ = Page::Welcome;
    ResultKind result_ = ResultKind::None;
    bool cancellable_ = true;
    bool cancelRequested_ = false;
    bool canLaunch_ = false;
    bool retryable_ = true;
    bool pathOk_ = true;
    bool pathDirty_ = false;
    bool userPickedPath_ = false;
    bool readySent_ = false;
    bool closing_ = false;
    bool dialogOpen_ = false;
    std::wstring scope_;
    std::wstring logPath_;
    std::wstring installDir_;
    std::wstring pendingPath_;  // folder whose check is in flight
    uint64_t requiredBytes_ = 0;
    uint64_t freeBytes_ = 0;
    winrt::Microsoft::UI::Dispatching::DispatcherQueue dispatcher_{nullptr};
};

}  // namespace winrt::FluentSetupUI::implementation

namespace winrt::FluentSetupUI::factory_implementation {
struct MainWindow : MainWindowT<MainWindow, implementation::MainWindow> {};
}  // namespace winrt::FluentSetupUI::factory_implementation
