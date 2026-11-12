#include "pch.h"

#include "MainWindow.xaml.h"
#if __has_include("MainWindow.g.cpp")
#include "MainWindow.g.cpp"
#endif

#include "App.xaml.h"
#include "Common/text.h"
#include "IPC/protocol.h"
#include "Strings.h"
#include "UiChannel.h"

#include <dwmapi.h>

using namespace winrt;
using namespace winrt::Microsoft::UI;
using namespace winrt::Microsoft::UI::Windowing;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;
using namespace winrt::Microsoft::UI::Xaml::Media;
using fsetup::json::Value;
using fsui::Fmt;
using fsui::Str;

namespace fsui {
extern std::wstring g_simulate;
}

namespace winrt::FluentSetupUI::implementation {

namespace {

const Value& Hello() { return fsui::UiChannel::Get().hello(); }

void SetVisible(UIElement const& e, bool visible) { e.Visibility(visible ? Visibility::Visible : Visibility::Collapsed); }

Brush ThemeBrush(const wchar_t* key) {
    return Application::Current().Resources().Lookup(box_value(key)).as<Brush>();
}


[[noreturn]] void SimulatedCrash() {
    ::TerminateProcess(::GetCurrentProcess(), static_cast<UINT>(0xC0000005));
    for (;;) ::Sleep(INFINITE);
}

}  // namespace

winrt::fire_and_forget MainWindow::LoadIconFrame(std::wstring path, int pixels, std::function<void(ImageSource const&)> apply) {
    using namespace winrt::Windows::Graphics::Imaging;
    try {
        auto stream = co_await winrt::Windows::Storage::Streams::FileRandomAccessStream::OpenAsync(
            path, winrt::Windows::Storage::FileAccessMode::Read);
        auto decoder = co_await BitmapDecoder::CreateAsync(stream);
        // Smallest frame that is at least as large as needed; otherwise the largest one.
        uint32_t best = 0, bestWidth = 0;
        bool bestBigEnough = false;
        for (uint32_t i = 0; i < decoder.FrameCount(); ++i) {
            auto frame = co_await decoder.GetFrameAsync(i);
            const uint32_t w = frame.PixelWidth();
            const bool bigEnough = w >= static_cast<uint32_t>(pixels);
            if ((bigEnough && (!bestBigEnough || w < bestWidth)) || (!bigEnough && !bestBigEnough && w > bestWidth)) {
                best = i;
                bestWidth = w;
                bestBigEnough = bigEnough;
            }
        }
        auto frame = co_await decoder.GetFrameAsync(best);
        BitmapTransform transform;
        if (frame.PixelWidth() != static_cast<uint32_t>(pixels)) {
            transform.ScaledWidth(static_cast<uint32_t>(pixels));
            transform.ScaledHeight(static_cast<uint32_t>(pixels) * frame.PixelHeight() / std::max<uint32_t>(frame.PixelWidth(), 1));
            transform.InterpolationMode(BitmapInterpolationMode::Fant);
        }
        auto bitmap = co_await frame.GetSoftwareBitmapAsync(BitmapPixelFormat::Bgra8, BitmapAlphaMode::Premultiplied, transform,
                                                            ExifOrientationMode::IgnoreExifOrientation,
                                                            ColorManagementMode::DoNotColorManage);
        Imaging::SoftwareBitmapSource source;  // continuation runs back on the UI thread (C++/WinRT apartment resume)
        co_await source.SetBitmapAsync(bitmap);
        apply(source);
    } catch (...) {
        // An unreadable icon is cosmetic: the header simply shows no image.
    }
}

MainWindow::MainWindow() {
    InitializeComponent();
    dispatcher_ = Microsoft::UI::Dispatching::DispatcherQueue::GetForCurrentThread();
    if (fsui::g_simulate == L"crash-before-ready") SimulatedCrash();

    InitWindow();
    ApplyStaticText();
    ApplyHello();
    ShowPage(Page::Welcome);

    pathTimer_ = DispatcherTimer();
    pathTimer_.Interval(std::chrono::milliseconds(350));
    pathTimer_.Tick([weak = get_weak()](auto&&, auto&&) {
        if (auto self = weak.get()) {
            self->pathTimer_.Stop();
            self->RequestPathCheck();
        }
    });

    RootGrid().Loaded([weak = get_weak()](auto&&, auto&&) {
        auto self = weak.get();
        if (!self) return;
        self->PrimaryButton().Focus(FocusState::Programmatic);
        // "ready" = the XAML tree is live and laid out. Only now may the bootstrapper commit to this front end.
        self->dispatcher_.TryEnqueue(Microsoft::UI::Dispatching::DispatcherQueuePriority::Low, [weak] {
            auto self = weak.get();
            if (!self || self->readySent_) return;
            self->readySent_ = true;
            if (fsui::g_simulate == L"hang") return;
            auto& channel = fsui::UiChannel::Get();
            channel.SendType("ready");
            channel.StartReader(
                [weak](Value m) {
                    if (auto s = weak.get()) {
                        s->dispatcher_.TryEnqueue([weak, m = std::move(m)]() mutable {
                            if (auto s2 = weak.get()) s2->HandleMessage(std::move(m));
                        });
                    }
                },
                [weak] {
                    if (auto s = weak.get()) {
                        s->dispatcher_.TryEnqueue([weak] {
                            if (auto s2 = weak.get()) s2->OnPeerLost();
                        });
                    }
                });
        });
    });
}

void MainWindow::InitWindow() {
    auto native = this->try_as<::IWindowNative>();
    if (native) native->get_WindowHandle(&hwnd_);

    ExtendsContentIntoTitleBar(true);
    SetTitleBar(AppTitleBar());

    auto appWindow = AppWindow();
    if (auto presenter = appWindow.Presenter().try_as<OverlappedPresenter>()) {
        presenter.IsMaximizable(false);
        presenter.IsResizable(false);
    }

    const double scale = hwnd_ ? ::GetDpiForWindow(hwnd_) / 96.0 : 1.0;
    const int width = static_cast<int>(720 * scale);
    const int height = static_cast<int>(540 * scale);
    appWindow.Resize({width, height});
    const auto area = DisplayArea::GetFromWindowId(appWindow.Id(), DisplayAreaFallback::Nearest);
    const auto work = area.WorkArea();
    appWindow.Move({work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2});

    const auto& ui = Hello()["ui"];
    const std::string& theme = ui["theme"].asString();
    if (theme == "light") {
        RootGrid().RequestedTheme(ElementTheme::Light);
        appWindow.TitleBar().PreferredTheme(TitleBarTheme::Light);
    } else if (theme == "dark") {
        RootGrid().RequestedTheme(ElementTheme::Dark);
        appWindow.TitleBar().PreferredTheme(TitleBarTheme::Dark);
    } else {
        appWindow.TitleBar().PreferredTheme(TitleBarTheme::UseDefaultAppMode);
    }

    const std::string& backdrop = ui["backdrop"].asString();
    if (backdrop == "none") {
        RootGrid().Background(ThemeBrush(L"SolidBackgroundFillColorBaseBrush"));
    } else {
        Microsoft::UI::Xaml::Media::MicaBackdrop mica;
        if (backdrop == "micaAlt") mica.Kind(Microsoft::UI::Composition::SystemBackdrops::MicaKind::BaseAlt);
        SystemBackdrop(mica);
    }

    const std::wstring icon = Hello()["app"]["iconPath"].asWide();
    if (!icon.empty() && ::GetFileAttributesW(icon.c_str()) != INVALID_FILE_ATTRIBUTES) {
        appWindow.SetIcon(icon);
        // Decode the .ico with WIC and pick the frame that fits the current DPI; BitmapImage would take the first
        // (often 16 px) frame and scale it up, which looks blurry.
        LoadIconFrame(icon, static_cast<int>(48 * scale), [weak = get_weak()](ImageSource const& src) {
            if (auto self = weak.get()) self->AppIcon().Source(src);
        });
        LoadIconFrame(icon, static_cast<int>(16 * scale), [weak = get_weak()](ImageSource const& src) {
            if (auto self = weak.get()) {
                Controls::ImageIconSource iconSource;
                iconSource.ImageSource(src);
                self->AppTitleBar().IconSource(iconSource);
            }
        });
    } else {
        SetVisible(AppIcon(), false);
    }

    // Built-in control text (context menus, dialogs) and CJK glyph selection follow the installer's language.
    const std::wstring language = Hello()["language"].asWide();
    if (!language.empty()) RootGrid().Language(language);
    appWindow.Closing([weak = get_weak()](auto&&, AppWindowClosingEventArgs const& args) {
        auto self = weak.get();
        if (!self || self->closing_) return;
        args.Cancel(true);
        self->RequestClose();
    });
}

std::wstring MainWindow::AppName() const { return Hello()["app"]["name"].asWide(); }

bool MainWindow::IsUninstall() const { return Hello()["mode"].asString() == "uninstall"; }

void MainWindow::ApplyStaticText() {
    BrowseButton().Content(box_value(Str("button.browse")));
    ScopeLabel().Text(Str("options.scope"));
    ScopeUserRadio().Content(box_value(Str("options.scope.user")));
    ScopeMachineRadio().Content(box_value(Str("options.scope.machine")));
    LocationLabel().Text(Str("options.location"));
    ShortcutLabel().Text(Str("options.shortcuts"));
    DesktopCheck().Content(box_value(Str("options.desktop")));
    StartMenuCheck().Content(box_value(Str("options.startMenu")));
    OptionsHeading().Text(Str("options.title"));
    LicenseHeading().Text(Str("license.title"));
    LicenseSubtitle().Text(Str("license.subtitle"));
    LicenseAccept().Content(box_value(Str("license.accept")));
    ViewLogLink().Content(box_value(Str("result.viewLog")));
    OpenFolderLink().Content(box_value(Str("result.openFolder")));
    ResultDetails().Header(box_value(Str("result.details")));

    Automation::AutomationProperties::SetName(LocationBox(), Str("options.location"));
    Automation::AutomationProperties::SetName(LicenseText(), Str("license.title"));
    Automation::AutomationProperties::SetName(ProgressBarControl(), Str("progress.accessibleName"));
    Automation::AutomationProperties::SetName(ScopeRadios(), Str("options.scope"));
}

void MainWindow::ApplyHello() {
    const Value& h = Hello();
    const Value& app = h["app"];
    const Value& opts = h["options"];
    const Value& existing = h["existing"];
    const std::wstring name = AppName();
    const bool uninstall = IsUninstall();

    std::wstring title = h["ui"]["title"].asWide();
    if (title.empty()) title = Fmt(uninstall ? "window.title.uninstall" : "window.title.install", {name});
    if (fsui::UiChannel::Get().preview()) title += L" – " + Str("preview.suffix");
    Title(title);
    AppTitleBar().Title(title);

    AppNameText().Text(name);
    std::wstring meta = Fmt("header.version", {app["version"].asWide()});
    if (!app["publisher"].asWide().empty()) meta = app["publisher"].asWide() + L"  ·  " + meta;
    AppMetaText().Text(meta);

    // Welcome
    if (uninstall) {
        WelcomeHeading().Text(Fmt("uninstall.title", {name}));
        WelcomeBody().Text(Fmt("uninstall.body", {name}));
        WelcomeLocationLabel().Text(Str("uninstall.location"));
    } else {
        WelcomeHeading().Text(Fmt(existing["relation"].asString() == "upgrade" ? "welcome.title.update" : "welcome.title", {name}));
        std::wstring welcome = h["ui"]["welcomeText"].asWide();
        WelcomeBody().Text(welcome.empty() ? Fmt("welcome.body", {name}) : welcome);
        WelcomeLocationLabel().Text(Str("welcome.location"));
    }

    const std::string relation = existing["relation"].asString();
    if (!uninstall && relation != "none" && !relation.empty()) {
        const std::wstring oldV = existing["version"].asWide();
        const std::wstring newV = app["version"].asWide();
        InfoBarSeverity severity = InfoBarSeverity::Informational;
        std::wstring text;
        if (relation == "upgrade") text = Fmt("welcome.upgrade", {oldV, newV});
        else if (relation == "same") text = Fmt("welcome.reinstall", {oldV});
        else {
            const bool blocked = existing["blocked"].asBool();
            severity = blocked ? InfoBarSeverity::Error : InfoBarSeverity::Warning;
            text = Fmt(blocked ? "welcome.downgradeBlocked" : "welcome.downgrade", {oldV, newV});
        }
        WelcomeInfo().Severity(severity);
        WelcomeInfo().Message(text);
        WelcomeInfo().IsOpen(true);
    }

    installDir_ = opts["installDir"].asWide();
    scope_ = opts["scope"].asWide();
    requiredBytes_ = h["requiredBytes"].asUint();
    freeBytes_ = h["freeBytes"].asUint();
    WelcomeLocationText().Text(installDir_);
    UpdateSpaceText();
    pathOk_ = opts["dirOk"].asBool(true);
    if (!pathOk_) {
        WelcomeDirError().Message(opts["dirMessage"].asWide());
        WelcomeDirError().IsOpen(true);
        LocationError().Message(opts["dirMessage"].asWide());
        LocationError().IsOpen(true);
    }

    // Options page
    suppressPathEvents_ = true;
    LocationBox().Text(installDir_);
    suppressPathEvents_ = false;
    SetVisible(LocationPanel(), opts["canChangeDir"].asBool());
    SetVisible(ScopePanel(), opts["canChangeScope"].asBool());
    ScopeRadios().SelectedIndex(scope_ == L"machine" ? 1 : 0);
    const Value& desktop = opts["desktopShortcut"];
    const Value& startMenu = opts["startMenuShortcut"];
    SetVisible(DesktopCheck(), desktop["visible"].asBool());
    SetVisible(StartMenuCheck(), startMenu["visible"].asBool());
    SetVisible(ShortcutPanel(), desktop["visible"].asBool() || startMenu["visible"].asBool());
    DesktopCheck().IsChecked(desktop["value"].asBool());
    StartMenuCheck().IsChecked(startMenu["value"].asBool());

    const Value& launch = opts["launchAfter"];
    LaunchCheck().Content(box_value(Fmt("result.launch", {name})));
    LaunchCheck().IsChecked(launch["value"].asBool());

    LicenseText().Text(h["ui"]["licenseText"].asWide());
}

void MainWindow::UpdateSpaceText() {
    std::wstring text;
    if (requiredBytes_ > 0) text = Fmt("welcome.space", {fsetup::FormatBytes(requiredBytes_)});
    WelcomeSpaceText().Text(text);
    SetVisible(WelcomeSpaceText(), !text.empty() && !IsUninstall());
    if (freeBytes_ > 0) {
        LocationSpaceText().Text(
            Fmt("options.space", {fsetup::FormatBytes(requiredBytes_), fsetup::FormatBytes(freeBytes_)}));
    } else {
        LocationSpaceText().Text(Fmt("welcome.space", {fsetup::FormatBytes(requiredBytes_)}));
    }
}

bool MainWindow::HasOptions() const {
    if (IsUninstall()) return false;
    const Value& o = Hello()["options"];
    return o["canChangeDir"].asBool() || o["canChangeScope"].asBool() || o["desktopShortcut"]["visible"].asBool() ||
           o["startMenuShortcut"]["visible"].asBool();
}

bool MainWindow::ShowsLicense() const { return !IsUninstall() && !Hello()["ui"]["licenseText"].asString().empty(); }

bool MainWindow::NeedsElevation() const { return scope_ == L"machine" && !Hello()["elevated"].asBool(); }

std::wstring MainWindow::PrimaryVerb() const {
    if (IsUninstall()) return Str("button.uninstall");
    const std::string relation = Hello()["existing"]["relation"].asString();
    if (relation == "upgrade") return Str("button.update");
    if (relation == "same") return Str("button.reinstall");
    return Str("button.install");
}

void MainWindow::ShowPage(Page page) {
    page_ = page;
    SetVisible(WelcomePage(), page == Page::Welcome);
    SetVisible(OptionsPage(), page == Page::Options);
    SetVisible(LicensePage(), page == Page::License);
    SetVisible(ProgressPage(), page == Page::Progress);
    SetVisible(ResultPage(), page == Page::Result);
    if (page == Page::Welcome) WelcomeLocationText().Text(installDir_);
    UpdateFooter();
    if (PrimaryButton().Visibility() == Visibility::Visible && PrimaryButton().IsEnabled()) {
        PrimaryButton().Focus(FocusState::Programmatic);
    } else if (CancelButton().Visibility() == Visibility::Visible) {
        CancelButton().Focus(FocusState::Programmatic);
    }
}

void MainWindow::UpdateFooter() {
    const bool blocked = Hello()["existing"]["blocked"].asBool();
    bool secondaryVisible = false, primaryVisible = true, cancelVisible = true, primaryEnabled = true;
    std::wstring secondary, primary, cancel = Str("button.cancel");
    bool shield = false;

    switch (page_) {
        case Page::Welcome:
            secondaryVisible = HasOptions() && !blocked;
            secondary = Str("button.options");
            primary = ShowsLicense() ? Str("button.next") : PrimaryVerb();
            shield = !ShowsLicense() && NeedsElevation();
            primaryEnabled = !blocked && pathOk_ && !pathDirty_;
            break;
        case Page::Options:
            secondaryVisible = true;
            secondary = Str("button.back");
            primary = ShowsLicense() ? Str("button.next") : PrimaryVerb();
            shield = !ShowsLicense() && NeedsElevation();
            primaryEnabled = pathOk_ && !pathDirty_;
            break;
        case Page::License:
            secondaryVisible = true;
            secondary = Str("button.back");
            primary = PrimaryVerb();
            shield = NeedsElevation();
            primaryEnabled = LicenseAccept().IsChecked().Value() && pathOk_;
            break;
        case Page::Progress:
            primaryVisible = false;
            CancelButton().IsEnabled(cancellable_ && !cancelRequested_);
            break;
        case Page::Result:
            if (result_ == ResultKind::Success) {
                primary = Str("button.finish");
                cancelVisible = false;
            } else if (result_ == ResultKind::Failed) {
                primary = Str("button.retry");
                cancel = Str("button.close");
            } else {
                primary = Str("button.close");
                cancelVisible = false;
            }
            break;
    }
    if (page_ != Page::Progress) CancelButton().IsEnabled(true);

    SetVisible(SecondaryButton(), secondaryVisible);
    SecondaryButton().Content(box_value(secondary));
    SetVisible(PrimaryButton(), primaryVisible);
    PrimaryText().Text(primary);
    Automation::AutomationProperties::SetName(PrimaryButton(), primary);
    PrimaryButton().IsEnabled(primaryEnabled);
    SetVisible(PrimaryShield(), shield);
    SetVisible(CancelButton(), cancelVisible);
    CancelButton().Content(box_value(cancel));
}

void MainWindow::OnPrimaryClick(IInspectable const&, RoutedEventArgs const&) {
    switch (page_) {
        case Page::Welcome:
        case Page::Options:
            if (ShowsLicense()) ShowPage(Page::License);
            else StartInstall();
            break;
        case Page::License: StartInstall(); break;
        case Page::Progress: break;
        case Page::Result:
            if (result_ == ResultKind::Failed) {
                result_ = ResultKind::None;
                ShowPage(Page::Welcome);
            } else {
                Value m;
                m.set("t", "finish");
                m.set("launch", result_ == ResultKind::Success && LaunchCheck().Visibility() == Visibility::Visible &&
                                    LaunchCheck().IsChecked().Value());
                fsui::UiChannel::Get().Send(m);
                closing_ = true;
                Close();
            }
            break;
    }
}

void MainWindow::OnSecondaryClick(IInspectable const&, RoutedEventArgs const&) {
    if (page_ == Page::Welcome) ShowPage(Page::Options);
    else if (page_ == Page::License) ShowPage(HasOptions() ? Page::Options : Page::Welcome);
    else ShowPage(Page::Welcome);
}

void MainWindow::OnCancelClick(IInspectable const&, RoutedEventArgs const&) { RequestClose(); }

void MainWindow::RequestClose() {
    if (page_ == Page::Progress) {
        if (cancellable_ && !cancelRequested_) ConfirmCancel();
        return;
    }
    if (page_ == Page::Result && result_ == ResultKind::Success) {
        Value m;
        m.set("t", "finish");
        m.set("launch", false);
        fsui::UiChannel::Get().Send(m);
    } else {
        fsui::UiChannel::Get().SendType("close");
    }
    closing_ = true;
    Close();
}

winrt::fire_and_forget MainWindow::ConfirmCancel() {
    if (dialogOpen_) co_return;
    dialogOpen_ = true;
    auto weak = get_weak();
    ContentDialog dialog;
    dialog.XamlRoot(Content().XamlRoot());
    dialog.RequestedTheme(RootGrid().ActualTheme());
    dialog.Title(box_value(Str(IsUninstall() ? "cancel.title.uninstall" : "cancel.title")));
    dialog.Content(box_value(Str("cancel.body")));
    dialog.PrimaryButtonText(Str("cancel.confirm"));
    dialog.CloseButtonText(Str("cancel.continue"));
    dialog.DefaultButton(ContentDialogButton::Close);
    const auto choice = co_await dialog.ShowAsync();
    auto self = weak.get();
    if (!self) co_return;
    self->dialogOpen_ = false;
    if (choice == ContentDialogResult::Primary && self->page_ == Page::Progress && self->cancellable_) {
        self->cancelRequested_ = true;
        self->ProgressStage().Text(Str("state.Cancelling"));
        self->UpdateFooter();
        fsui::UiChannel::Get().SendType("cancel");
    }
}

void MainWindow::StartInstall() {
    installing_ = true;
    cancelRequested_ = false;
    cancellable_ = true;
    result_ = ResultKind::None;
    ProgressHeading().Text(Fmt(IsUninstall() ? "progress.title.uninstall" : "progress.title.install", {AppName()}));
    ProgressStage().Text(Str("state.Validating"));
    ProgressBarControl().IsIndeterminate(true);
    ProgressBarControl().Value(0);
    ProgressDetail().Text(L"");
    ProgressPercent().Text(L"");
    ShowPage(Page::Progress);

    if (fsui::UiChannel::Get().preview()) {
        RunPreviewProgress();
        return;
    }
    Value options;
    options.set("installDir", installDir_);
    options.set("scope", scope_);
    options.set("desktopShortcut", DesktopCheck().IsChecked().Value());
    options.set("startMenuShortcut", StartMenuCheck().IsChecked().Value());
    Value m;
    m.set("t", "start");
    m.set("options", options);
    fsui::UiChannel::Get().Send(m);
}

winrt::fire_and_forget MainWindow::RunPreviewProgress() {
    // Builder preview only: there is no engine, so the progress page is shown at a fixed point for a moment and then
    // the completion page appears. Real installs are driven exclusively by engine "progress" messages.
    auto weak = get_weak();
    Value p;
    p.set("state", "Extracting");
    p.set("fraction", 0.45);
    p.set("detail", "bin\\" + fsetup::WideToUtf8(AppName()) + ".exe");
    p.set("cancellable", true);
    OnProgress(p);
    DispatcherTimer timer;
    timer.Interval(std::chrono::milliseconds(1800));
    timer.Tick([weak, timer](auto&&, auto&&) {
        timer.Stop();
        auto self = weak.get();
        if (!self || self->page_ != Page::Progress) return;
        Value done;
        done.set("result", self->cancelRequested_ ? "cancelled" : "success");
        done.set("canLaunch", true);
        self->OnDone(done);
    });
    timer.Start();
    co_return;
}

void MainWindow::HandleMessage(Value message) {
    const std::string& type = message["t"].asString();
    if (type == "progress") OnProgress(message);
    else if (type == "done") OnDone(message);
    else if (type == "pathCheck") OnPathCheck(message);
}

void MainWindow::OnProgress(const Value& m) {
    if (fsui::g_simulate == L"crash-after-ready") SimulatedCrash();
    if (page_ != Page::Progress) ShowPage(Page::Progress);
    const std::string state = m["state"].asString();
    if (!cancelRequested_ || state == "RollingBack") ProgressStage().Text(Str("state." + state));
    const double fraction = m["fraction"].asDouble(-1);
    if (fraction < 0) {
        ProgressBarControl().IsIndeterminate(true);
        ProgressPercent().Text(L"");
    } else {
        ProgressBarControl().IsIndeterminate(false);
        ProgressBarControl().Value(fraction * 100.0);
        ProgressPercent().Text(fsetup::Printf(L"%d%%", static_cast<int>(fraction * 100.0)));
    }
    ProgressDetail().Text(m["detail"].asWide());
    const bool cancellable = m["cancellable"].asBool(true);
    if (cancellable != cancellable_) {
        cancellable_ = cancellable;
        UpdateFooter();
    }
}

void MainWindow::OnDone(const Value& m) {
    installing_ = false;
    const std::string result = m["result"].asString();
    const std::wstring name = AppName();
    const bool uninstall = IsUninstall();
    logPath_ = m["log"].asWide();
    if (m.has("installDir")) installDir_ = m["installDir"].asWide();

    SetVisible(LaunchCheck(), false);
    SetVisible(ResultCode(), false);
    SetVisible(ResultDetails(), false);
    SetVisible(OpenFolderLink(), false);
    SetVisible(ViewLogLink(), !logPath_.empty());

    if (result == "success") {
        result_ = ResultKind::Success;
        ResultIcon().Glyph(L"");
        ResultIcon().Foreground(ThemeBrush(L"SystemFillColorSuccessBrush"));
        ResultHeading().Text(Fmt(uninstall ? "result.success.title.uninstall" : "result.success.title", {name}));
        std::wstring body = uninstall ? std::wstring() : Hello()["ui"]["finishText"].asWide();
        if (body.empty()) body = Fmt(uninstall ? "result.success.body.uninstall" : "result.success.body", {name});
        if (uninstall && m["leftovers"].asBool()) body += L"\n\n" + Fmt("result.leftovers", {installDir_});
        ResultBody().Text(body);
        SetVisible(LaunchCheck(), !uninstall && m["canLaunch"].asBool());
        SetVisible(OpenFolderLink(), !uninstall && !installDir_.empty());
        SetVisible(ViewLogLink(), false);
    } else if (result == "cancelled") {
        result_ = ResultKind::Cancelled;
        ResultIcon().Glyph(L"");
        ResultIcon().Foreground(ThemeBrush(L"SystemFillColorCautionBrush"));
        ResultHeading().Text(Str(uninstall ? "result.cancelled.title.uninstall" : "result.cancelled.title"));
        ResultBody().Text(Str(m["restored"].asBool() ? "result.restored" : "result.noChanges"));
    } else {
        result_ = ResultKind::Failed;
        ResultIcon().Glyph(L"");
        ResultIcon().Foreground(ThemeBrush(L"SystemFillColorCriticalBrush"));
        ResultHeading().Text(Str(uninstall ? "result.failed.title.uninstall" : "result.failed.title"));
        std::wstring body = m["message"].asWide();
        if (m["restored"].asBool()) body += L"\n\n" + Str("result.restored");
        ResultBody().Text(body);
        ResultCode().Text(Fmt("result.code", {std::to_wstring(m["code"].asInt()), m["codeName"].asWide()}));
        SetVisible(ResultCode(), true);
        const std::wstring detail = m["detail"].asWide();
        ResultDetailText().Text(detail);
        SetVisible(ResultDetails(), !detail.empty());
    }
    ShowPage(Page::Result);
    // The retry button only makes sense if the failure was not about the package itself.
    if (result_ == ResultKind::Failed && !m["retryable"].asBool(true)) SetVisible(PrimaryButton(), false);
}

void MainWindow::OnPathCheck(const Value& m) {
    if (m["path"].asWide() != std::wstring(LocationBox().Text())) return;  // stale reply
    pathDirty_ = false;
    pathOk_ = m["ok"].asBool();
    installDir_ = m["normalized"].asWide().empty() ? std::wstring(LocationBox().Text()) : m["normalized"].asWide();
    requiredBytes_ = m["requiredBytes"].asUint(requiredBytes_);
    freeBytes_ = m["freeBytes"].asUint();
    LocationError().Message(m["message"].asWide());
    LocationError().IsOpen(!pathOk_);
    WelcomeDirError().Message(m["message"].asWide());
    WelcomeDirError().IsOpen(!pathOk_);
    UpdateSpaceText();
    UpdateFooter();
}

void MainWindow::RequestPathCheck() {
    if (fsui::UiChannel::Get().preview()) {
        pathDirty_ = false;
        pathOk_ = true;
        installDir_ = LocationBox().Text();
        UpdateFooter();
        return;
    }
    Value m;
    m.set("t", "checkPath");
    m.set("path", std::wstring(LocationBox().Text()));
    m.set("scope", scope_);
    fsui::UiChannel::Get().Send(m);
}

void MainWindow::OnPeerLost() {
    if (closing_) return;
    // The bootstrapper is gone; nothing can be installed or cancelled from here any more.
    Value done;
    done.set("result", "failed");
    done.set("code", static_cast<int>(14));
    done.set("codeName", "Internal");
    done.set("message", Str("error.peerLost"));
    done.set("retryable", false);
    OnDone(done);
}

void MainWindow::OnScopeChanged(IInspectable const&, SelectionChangedEventArgs const&) {
    const int index = ScopeRadios().SelectedIndex();
    if (index < 0) return;
    const std::wstring scope = index == 1 ? L"machine" : L"user";
    if (scope == scope_) return;
    scope_ = scope;
    if (!userEditedPath_) {
        const std::wstring dir = Hello()["options"][scope == L"machine" ? "machineDir" : "userDir"].asWide();
        suppressPathEvents_ = true;
        LocationBox().Text(dir);
        suppressPathEvents_ = false;
        installDir_ = dir;
    }
    pathDirty_ = true;
    UpdateFooter();
    RequestPathCheck();
}

void MainWindow::OnLocationChanged(IInspectable const&, TextChangedEventArgs const&) {
    if (suppressPathEvents_) return;
    userEditedPath_ = true;
    pathDirty_ = true;
    UpdateFooter();
    pathTimer_.Stop();
    pathTimer_.Start();
}

void MainWindow::OnLicenseChanged(IInspectable const&, RoutedEventArgs const&) { UpdateFooter(); }

void MainWindow::OnBrowseClick(IInspectable const&, RoutedEventArgs const&) {
    com_ptr<IFileOpenDialog> dialog;
    if (FAILED(::CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(dialog.put())))) return;
    DWORD flags = 0;
    dialog->GetOptions(&flags);
    dialog->SetOptions(flags | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
    const std::wstring title = Str("options.browseTitle");
    dialog->SetTitle(title.c_str());
    if (FAILED(dialog->Show(hwnd_))) return;
    com_ptr<IShellItem> item;
    if (FAILED(dialog->GetResult(item.put()))) return;
    PWSTR path = nullptr;
    if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) return;
    std::wstring chosen = path;
    ::CoTaskMemFree(path);
    // Picking a parent folder installs into "<parent>\<App name>", like every other Windows installer.
    const std::wstring leaf = Hello()["options"]["dirLeaf"].asWide();
    if (!leaf.empty()) {
        if (!chosen.empty() && chosen.back() != L'\\') chosen.push_back(L'\\');
        chosen += leaf;
    }
    LocationBox().Text(chosen);  // triggers OnLocationChanged -> validation
}

void MainWindow::OnOpenFolderClick(IInspectable const&, RoutedEventArgs const&) {
    if (!installDir_.empty()) ::ShellExecuteW(hwnd_, L"open", installDir_.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

void MainWindow::OnViewLogClick(IInspectable const&, RoutedEventArgs const&) {
    if (!logPath_.empty()) ::ShellExecuteW(hwnd_, L"open", L"notepad.exe", (L"\"" + logPath_ + L"\"").c_str(), nullptr, SW_SHOWNORMAL);
}

}  // namespace winrt::FluentSetupUI::implementation
