// Win32 fallback front end, laid out like the WinUI one (Windows App Installer / APK-installer style):
// a question as the title, publisher/version lines, the app icon large at the top right, "Launch when ready" at the
// bottom left and progress inline in the bottom bar. System controls only (Common Controls v6, UxTheme, DWM).
#include "Setup.Win32/fallback_ui.h"

#include "Bootstrapper/resource.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Setup.Win32/theme.h"

#include <commctrl.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <uxtheme.h>
#include <vssym32.h>
#include <windowsx.h>

#include <memory>
#include <vector>

namespace fsetup::win32ui {

namespace {

using boot::Controller;
using json::Value;

constexpr UINT WM_APP_PROGRESS = WM_APP + 1;
constexpr UINT WM_APP_DONE = WM_APP + 2;
constexpr UINT_PTR kPathTimer = 1;
constexpr int kClientWidth = 640;   // DIP (same window size as the WinUI front end)
constexpr int kClientHeight = 398;  // DIP
constexpr int kMargin = 32;
constexpr int kIconSize = 72;

enum Id : int {
    kStatus = 100, kTitle, kDescription, kLblPublisher, kPublisher, kLblVersion, kVersion, kLblLocation, kLocation, kChange,
    kLblSize, kSize, kInfo, kDirError,
    kResultBody, kResultCode, kResultDetail, kOpenFolder, kViewLog,
    kOHeading = 300, kOScopeLabel, kOScopeUser, kOScopeMachine, kOLocLabel, kOLoc, kOBrowse, kOSpace, kOError,
    kOShortLabel, kODesktop, kOStartMenu,
    kLHeading = 400, kLSub, kLText, kLAccept,
    kLaunch = 500, kPStage, kPPercent, kPBar, kPDetail,
    kSecondary = 700,
};

enum class Page { Welcome, Options, License, Progress, Result };
enum class ResultKind { None, Success, Failed, Cancelled };

class Window : public boot::IListener {
public:
    Window(Controller& controller, Value hello) : c_(controller), hello_(std::move(hello)) {}
    void Run();

    void OnProgress(const install::Progress& p) override {
        if (hwnd_) ::PostMessageW(hwnd_, WM_APP_PROGRESS, 0, reinterpret_cast<LPARAM>(new install::Progress(p)));
    }
    void OnDone(const install::Outcome& o) override {
        if (hwnd_) ::PostMessageW(hwnd_, WM_APP_DONE, 0, reinterpret_cast<LPARAM>(new Value(c_.DoneMessage(o))));
    }

private:
    static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l);
    LRESULT Handle(UINT m, WPARAM w, LPARAM l);

    std::wstring S(std::string_view key) const { return c_.strings().Get(key); }
    std::wstring F(std::string_view key, std::initializer_list<std::wstring_view> a) const { return c_.strings().Fmt(key, a); }
    int Px(int dip) const { return MulDiv(dip, static_cast<int>(dpi_), 96); }
    HWND Ctl(int id) const { return ::GetDlgItem(hwnd_, id); }
    HWND Make(const wchar_t* cls, int id, DWORD style, DWORD exStyle = 0);
    void SetText(int id, const std::wstring& t) { ::SetWindowTextW(Ctl(id), t.c_str()); }
    std::wstring GetText(int id) const;
    void Show(int id, bool visible) { ::ShowWindow(Ctl(id), visible ? SW_SHOWNA : SW_HIDE); }
    bool Visible(int id) const { return (::GetWindowLongW(Ctl(id), GWL_STYLE) & WS_VISIBLE) != 0; }
    bool Checked(int id) const { return Button_GetCheck(Ctl(id)) == BST_CHECKED; }
    int TextHeight(int id, int width) const;

    void Create();
    void ApplyFonts();
    void ApplyTheme();
    void ApplyHello();
    void UpdateInfoLines();
    void ShowPage(Page p);
    void Layout();
    void UpdateFooter();
    void Paint(HDC dc);
    LRESULT CustomDrawButton(NMCUSTOMDRAW* cd);
    void DrawStatus(const DRAWITEMSTRUCT* di);

    bool HasOptions() const;
    bool ShowsLicense() const { return !IsUninstall() && !hello_["ui"]["licenseText"].asString().empty(); }
    bool LaunchOffered() const { return !IsUninstall() && hello_["options"]["launchAfter"]["visible"].asBool(); }
    bool NeedsElevation() const { return scope_ == L"machine" && !hello_["elevated"].asBool(); }
    bool IsUninstall() const { return hello_["mode"].asString() == "uninstall"; }
    std::wstring AppName() const { return hello_["app"]["name"].asWide(); }
    std::wstring PrimaryVerb() const;

    void OnPrimary();
    void OnSecondary();
    void RequestClose();
    void Finish(bool launch);
    void StartInstall();
    void OnProgressMsg(const install::Progress& p);
    void OnDoneMsg(const Value& m);
    void CheckPathNow();
    void OnScopeChanged();
    void Browse();
    void UpdateSpace();

    Controller& c_;
    Value hello_;
    Theme theme_;
    HWND hwnd_ = nullptr;
    UINT dpi_ = 96;
    HICON iconLarge_ = nullptr;
    HICON iconSmall_ = nullptr;
    Page page_ = Page::Welcome;
    ResultKind result_ = ResultKind::None;
    std::wstring scope_, installDir_, logPath_;
    uint64_t requiredBytes_ = 0, freeBytes_ = 0;
    bool pathOk_ = true, pathDirty_ = false, userEditedPath_ = false, suppress_ = false;
    bool cancellable_ = true, cancelRequested_ = false, retryable_ = true, canLaunch_ = false;
    std::wstring statusGlyph_;
    COLORREF statusColor_ = 0;
    RECT infoRect_{};
};

HWND Window::Make(const wchar_t* cls, int id, DWORD style, DWORD exStyle) {
    return ::CreateWindowExW(exStyle, cls, L"", WS_CHILD | style, 0, 0, 10, 10, hwnd_,
                             reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), ::GetModuleHandleW(nullptr), nullptr);
}

std::wstring Window::GetText(int id) const {
    std::wstring t(static_cast<size_t>(::GetWindowTextLengthW(Ctl(id))) + 1, L'\0');
    ::GetWindowTextW(Ctl(id), t.data(), static_cast<int>(t.size()));
    t.resize(wcslen(t.c_str()));
    return t;
}

int Window::TextHeight(int id, int width) const {
    HWND h = Ctl(id);
    std::wstring text = GetText(id);
    if (text.empty()) text = L" ";
    HDC dc = ::GetDC(h);
    HGDIOBJ old = ::SelectObject(dc, reinterpret_cast<HFONT>(::SendMessageW(h, WM_GETFONT, 0, 0)));
    RECT r{0, 0, width, 0};
    ::DrawTextW(dc, text.c_str(), -1, &r, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX | DT_EDITCONTROL);
    ::SelectObject(dc, old);
    ::ReleaseDC(h, dc);
    return r.bottom - r.top;
}

void Window::Run() {
    INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_STANDARD_CLASSES | ICC_PROGRESS_CLASS};
    ::InitCommonControlsEx(&icc);
    const std::string& themeSetting = hello_["ui"]["theme"].asString();  // hello_ outlives theme_
    theme_.Init(themeSetting.empty() ? "system" : themeSetting.c_str());

    WNDCLASSEXW wc{sizeof(wc)};
    wc.lpfnWndProc = Proc;
    wc.hInstance = ::GetModuleHandleW(nullptr);
    wc.hCursor = ::LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FluentSetupFallbackWindow";
    wc.hIcon = ::LoadIconW(wc.hInstance, MAKEINTRESOURCEW(IDI_APP));
    ::RegisterClassExW(&wc);

    POINT cursor{};
    ::GetCursorPos(&cursor);
    HMONITOR monitor = ::MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY);
    UINT dpiX = 96, dpiY = 96;
    using GetDpiForMonitorFn = HRESULT(WINAPI*)(HMONITOR, int, UINT*, UINT*);
    if (HMODULE shcore = ::LoadLibraryExW(L"shcore.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32)) {
        if (auto fn = reinterpret_cast<GetDpiForMonitorFn>(::GetProcAddress(shcore, "GetDpiForMonitor"))) fn(monitor, 0, &dpiX, &dpiY);
    }
    dpi_ = dpiX;
    const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
    RECT r{0, 0, Px(kClientWidth), Px(kClientHeight)};
    ::AdjustWindowRectExForDpi(&r, style, FALSE, WS_EX_CONTROLPARENT, dpi_);
    MONITORINFO mi{sizeof(mi)};
    ::GetMonitorInfoW(monitor, &mi);
    const int w = r.right - r.left, h = r.bottom - r.top;
    std::wstring title = hello_["ui"]["title"].asWide();
    if (title.empty()) title = F(IsUninstall() ? "window.title.uninstall" : "window.title.install", {AppName()});
    hwnd_ = ::CreateWindowExW(WS_EX_CONTROLPARENT, wc.lpszClassName, title.c_str(), style,
                              mi.rcWork.left + (mi.rcWork.right - mi.rcWork.left - w) / 2,
                              mi.rcWork.top + (mi.rcWork.bottom - mi.rcWork.top - h) / 2, w, h, nullptr, nullptr, wc.hInstance, this);
    if (!hwnd_) return;
    dpi_ = ::GetDpiForWindow(hwnd_);

    Create();
    ApplyHello();
    ApplyTheme();

    // Resume: show whatever the session is doing right now (e.g. after the WinUI front end stopped).
    install::Progress last;
    std::optional<install::Outcome> outcome;
    bool started = false;
    c_.Attach(this, &last, &outcome, &started);
    if (outcome) {
        OnDoneMsg(c_.DoneMessage(*outcome));
    } else if (started) {
        ShowPage(Page::Progress);
        OnProgressMsg(last);
    } else {
        ShowPage(Page::Welcome);
    }

    // Hide focus rectangles and mnemonic underlines until the keyboard is used (standard dialog behaviour).
    ::SendMessageW(hwnd_, WM_CHANGEUISTATE, MAKEWPARAM(UIS_INITIALIZE, 0), 0);
    ::ShowWindow(hwnd_, SW_SHOWNORMAL);
    ::SetForegroundWindow(hwnd_);
    MSG msg;
    while (::GetMessageW(&msg, nullptr, 0, 0) > 0) {
        if (!::IsDialogMessageW(hwnd_, &msg)) {
            ::TranslateMessage(&msg);
            ::DispatchMessageW(&msg);
        }
    }
    c_.Attach(nullptr, nullptr, nullptr, nullptr);
    while (::PeekMessageW(&msg, nullptr, WM_APP_PROGRESS, WM_APP_DONE, PM_REMOVE)) {
        if (msg.message == WM_APP_PROGRESS) delete reinterpret_cast<install::Progress*>(msg.lParam);
        else delete reinterpret_cast<Value*>(msg.lParam);
    }
    if (iconLarge_) ::DestroyIcon(iconLarge_);
    if (iconSmall_) ::DestroyIcon(iconSmall_);
}

void Window::Create() {
    const DWORD text = SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL;
    // Creation order = tab order.
    Make(WC_STATICW, kStatus, SS_OWNERDRAW);
    Make(WC_STATICW, kTitle, text);
    Make(WC_STATICW, kDescription, text);
    for (int id : {kLblPublisher, kPublisher, kLblVersion, kVersion, kLblLocation}) Make(WC_STATICW, id, text);
    Make(WC_STATICW, kLocation, text | SS_PATHELLIPSIS);
    Make(WC_BUTTONW, kChange, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    for (int id : {kLblSize, kSize, kInfo, kDirError, kResultBody, kResultCode}) Make(WC_STATICW, id, text);
    Make(WC_EDITW, kResultDetail, WS_TABSTOP | WS_GROUP | ES_MULTILINE | ES_READONLY | WS_VSCROLL, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kOpenFolder, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kViewLog, BS_PUSHBUTTON | WS_TABSTOP);

    Make(WC_STATICW, kOHeading, text);
    Make(WC_STATICW, kOScopeLabel, text);
    Make(WC_BUTTONW, kOScopeUser, BS_AUTORADIOBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kOScopeMachine, BS_AUTORADIOBUTTON);
    Make(WC_STATICW, kOLocLabel, text);
    Make(WC_EDITW, kOLoc, WS_TABSTOP | WS_GROUP | ES_AUTOHSCROLL, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kOBrowse, BS_PUSHBUTTON | WS_TABSTOP);
    Make(WC_STATICW, kOSpace, text);
    Make(WC_STATICW, kOError, text);
    Make(WC_STATICW, kOShortLabel, text);
    Make(WC_BUTTONW, kODesktop, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kOStartMenu, BS_AUTOCHECKBOX | WS_TABSTOP);

    Make(WC_STATICW, kLHeading, text);
    Make(WC_STATICW, kLSub, text);
    Make(WC_EDITW, kLText, WS_TABSTOP | WS_GROUP | ES_MULTILINE | ES_READONLY | WS_VSCROLL, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kLAccept, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);

    Make(WC_BUTTONW, kLaunch, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);
    Make(WC_STATICW, kPStage, text | SS_ENDELLIPSIS);
    Make(WC_STATICW, kPPercent, text | SS_RIGHT);
    Make(PROGRESS_CLASSW, kPBar, PBS_SMOOTH);
    Make(WC_STATICW, kPDetail, text | SS_PATHELLIPSIS);

    Make(WC_BUTTONW, kSecondary, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, IDOK, BS_DEFPUSHBUTTON | WS_TABSTOP);
    Make(WC_BUTTONW, IDCANCEL, BS_PUSHBUTTON | WS_TABSTOP);

    ::SendMessageW(Ctl(kPBar), PBM_SETRANGE32, 0, 1000);
    ::SendMessageW(Ctl(kOLoc), EM_SETLIMITTEXT, 240, 0);
    SetText(kOHeading, S("options.title"));
    SetText(kOScopeLabel, S("options.scope"));
    SetText(kOScopeUser, S("options.scope.user"));
    SetText(kOScopeMachine, S("options.scope.machine"));
    SetText(kOLocLabel, S("options.location"));
    SetText(kOBrowse, S("fallback.browse"));
    SetText(kOShortLabel, S("options.shortcuts"));
    SetText(kODesktop, S("fallback.desktop"));
    SetText(kOStartMenu, S("fallback.startMenu"));
    SetText(kLHeading, S("license.title"));
    SetText(kLSub, S("license.subtitle"));
    SetText(kLAccept, S("fallback.accept"));
    SetText(kLaunch, S("launchWhenReady"));
    SetText(kLblPublisher, S("label.publisher"));
    SetText(kLblVersion, S("label.version"));
    SetText(kLblLocation, S("label.location"));
    SetText(kLblSize, S("label.size"));
    SetText(kChange, S("button.change"));
    SetText(kOpenFolder, S("result.openFolder"));
    SetText(kViewLog, S("fallback.viewLog"));
}

void Window::ApplyFonts() {
    theme_.CreateFonts(dpi_);
    const auto set = [&](int id, HFONT f) { ::SendMessageW(Ctl(id), WM_SETFONT, reinterpret_cast<WPARAM>(f), FALSE); };
    set(kTitle, theme_.title);
    for (int id : {kOHeading, kLHeading}) set(id, theme_.subtitle);
    for (int id : {kOScopeLabel, kOLocLabel, kOShortLabel}) set(id, theme_.bodyStrong);
    for (int id : {kOSpace, kPDetail, kResultCode}) set(id, theme_.caption);
    for (int id : std::initializer_list<int>{kLblPublisher, kPublisher, kLblVersion, kVersion, kLblLocation, kLocation, kChange,
                                             kLblSize, kSize, kDescription, kInfo, kDirError,
                                             kResultBody, kResultDetail, kOpenFolder, kViewLog, kOScopeUser, kOScopeMachine, kOLoc,
                                             kOBrowse, kOError, kODesktop, kOStartMenu, kLSub, kLText, kLAccept, kLaunch, kPStage,
                                             kPPercent, kSecondary, static_cast<int>(IDOK), static_cast<int>(IDCANCEL)}) {
        set(id, theme_.body);
    }
    if (iconLarge_) ::DestroyIcon(iconLarge_);
    if (iconSmall_) ::DestroyIcon(iconSmall_);
    ::LoadIconWithScaleDown(::GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_APP), Px(kIconSize), Px(kIconSize), &iconLarge_);
    ::LoadIconWithScaleDown(::GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_APP), ::GetSystemMetricsForDpi(SM_CXSMICON, dpi_),
                            ::GetSystemMetricsForDpi(SM_CYSMICON, dpi_), &iconSmall_);
    ::SendMessageW(hwnd_, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(iconSmall_));
    ::SendMessageW(hwnd_, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(iconLarge_));
}

void Window::ApplyTheme() {
    theme_.Refresh();
    ApplyFonts();
    theme_.ApplyWindowAttributes(hwnd_);
    for (HWND child = ::GetWindow(hwnd_, GW_CHILD); child; child = ::GetWindow(child, GW_HWNDNEXT)) {
        wchar_t cls[32] = {};
        ::GetClassNameW(child, cls, 32);
        if (_wcsicmp(cls, WC_BUTTONW) == 0) theme_.ApplyControlTheme(child, false);
        else if (_wcsicmp(cls, WC_EDITW) == 0) theme_.ApplyControlTheme(child, true);
    }
    Layout();
    ::InvalidateRect(hwnd_, nullptr, TRUE);
}

void Window::ApplyHello() {
    const Value& opts = hello_["options"];
    const Value& existing = hello_["existing"];
    const std::wstring name = AppName();
    std::wstring description = hello_["ui"]["welcomeText"].asWide();
    if (IsUninstall()) description = F("uninstall.body", {name});
    SetText(kDescription, description);

    const std::string relation = existing["relation"].asString();
    std::wstring info;
    if (!IsUninstall() && relation == "downgrade") {  // upgrades/reinstalls are explained inline in the version row
        const std::wstring oldV = existing["version"].asWide(), newV = hello_["app"]["version"].asWide();
        if (relation == "upgrade") info = F("welcome.upgrade", {oldV, newV});
        else if (relation == "same") info = F("welcome.reinstall", {oldV});
        else if (relation == "downgrade") info = F(existing["blocked"].asBool() ? "welcome.downgradeBlocked" : "welcome.downgrade", {oldV, newV});
    }
    SetText(kInfo, info);

    installDir_ = opts["installDir"].asWide();
    scope_ = opts["scope"].asWide();
    requiredBytes_ = hello_["requiredBytes"].asUint();
    freeBytes_ = hello_["freeBytes"].asUint();
    pathOk_ = opts["dirOk"].asBool(true);
    SetText(kDirError, opts["dirMessage"].asWide());
    SetText(kOError, opts["dirMessage"].asWide());
    suppress_ = true;
    SetText(kOLoc, installDir_);
    suppress_ = false;
    Button_SetCheck(Ctl(scope_ == L"machine" ? kOScopeMachine : kOScopeUser), BST_CHECKED);
    Button_SetCheck(Ctl(kODesktop), opts["desktopShortcut"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    Button_SetCheck(Ctl(kOStartMenu), opts["startMenuShortcut"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    Button_SetCheck(Ctl(kLaunch), opts["launchAfter"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    std::wstring license = hello_["ui"]["licenseText"].asWide(), crlf;
    for (size_t i = 0; i < license.size(); ++i) {
        if (license[i] == L'\n' && (i == 0 || license[i - 1] != L'\r')) crlf.push_back(L'\r');
        crlf.push_back(license[i]);
    }
    SetText(kLText, crlf);
    UpdateSpace();
}

void Window::UpdateInfoLines() {
    SetText(kPublisher, hello_["app"]["publisher"].asWide());
    const std::wstring version = hello_["app"]["version"].asWide(), installed = hello_["existing"]["version"].asWide();
    const std::string relation = hello_["existing"]["relation"].asString();
    std::wstring versionText = version;
    if (page_ == Page::Welcome && !IsUninstall()) {
        if (relation == "upgrade") versionText = F("version.upgrade", {installed, version});
        else if (relation == "same") versionText = F("version.same", {version});
        else if (relation == "downgrade") versionText = F("version.downgrade", {installed, version});
    }
    SetText(kVersion, versionText);
    SetText(kLocation, installDir_);
    SetText(kSize, IsUninstall() || requiredBytes_ == 0 || page_ == Page::Result ? std::wstring() : FormatBytes(requiredBytes_));
}
void Window::UpdateSpace() {
    SetText(kOSpace, freeBytes_ ? F("options.space", {FormatBytes(requiredBytes_), FormatBytes(freeBytes_)})
                                : F("welcome.space", {FormatBytes(requiredBytes_)}));
    UpdateInfoLines();
}

bool Window::HasOptions() const {
    if (IsUninstall()) return false;
    const Value& o = hello_["options"];
    return o["canChangeDir"].asBool() || o["canChangeScope"].asBool() || o["desktopShortcut"]["visible"].asBool() ||
           o["startMenuShortcut"]["visible"].asBool();
}

std::wstring Window::PrimaryVerb() const {
    if (IsUninstall()) return S("button.uninstall");
    const std::string relation = hello_["existing"]["relation"].asString();
    if (relation == "upgrade") return S("button.update");
    if (relation == "same") return S("button.reinstall");
    return S("button.install");
}

void Window::ShowPage(Page p) {
    page_ = p;
    const Value& o = hello_["options"];
    const bool summary = p == Page::Welcome || p == Page::Progress || p == Page::Result;
    const bool welcome = p == Page::Welcome, options = p == Page::Options, license = p == Page::License, result = p == Page::Result;

    if (welcome) {
        const std::string relation = hello_["existing"]["relation"].asString();
        const char* key = IsUninstall() ? "ask.uninstall" : relation == "upgrade" ? "ask.update" : relation == "same" ? "ask.reinstall" : "ask.install";
        SetText(kTitle, F(key, {AppName()}));
    } else if (p == Page::Progress) {
        SetText(kTitle, F(IsUninstall() ? "progress.uninstalling" : "progress.installing", {AppName()}));
    }
    UpdateInfoLines();
    Show(kTitle, summary);
    Show(kStatus, result);
    for (const auto [label, value] : {std::pair{kLblPublisher, kPublisher}, std::pair{kLblVersion, kVersion},
                                      std::pair{kLblLocation, kLocation}, std::pair{kLblSize, kSize}}) {
        const bool has = summary && ::GetWindowTextLengthW(Ctl(value)) > 0;
        Show(label, has);
        Show(value, has);
    }
    Show(kChange, welcome && HasOptions() && !hello_["existing"]["blocked"].asBool());
    Show(kDescription, welcome && ::GetWindowTextLengthW(Ctl(kDescription)) > 0);
    Show(kInfo, welcome && ::GetWindowTextLengthW(Ctl(kInfo)) > 0);
    Show(kDirError, welcome && !pathOk_);
    if (!result) {
        for (int id : {kResultBody, kResultCode, kResultDetail, kOpenFolder, kViewLog}) Show(id, false);
    }

    Show(kOHeading, options);
    for (int id : {kOScopeLabel, kOScopeUser, kOScopeMachine}) Show(id, options && o["canChangeScope"].asBool());
    for (int id : {kOLocLabel, kOLoc, kOBrowse, kOSpace}) Show(id, options && o["canChangeDir"].asBool());
    Show(kOError, options && !pathOk_);
    const bool desktop = o["desktopShortcut"]["visible"].asBool(), start = o["startMenuShortcut"]["visible"].asBool();
    Show(kOShortLabel, options && (desktop || start));
    Show(kODesktop, options && desktop);
    Show(kOStartMenu, options && start);
    for (int id : {kLHeading, kLSub, kLText, kLAccept}) Show(id, license);

    UpdateFooter();
    ::InvalidateRect(hwnd_, nullptr, TRUE);
    HWND focus = Visible(IDOK) && ::IsWindowEnabled(Ctl(IDOK)) ? Ctl(IDOK) : Ctl(IDCANCEL);
    if (options && o["canChangeDir"].asBool()) focus = Ctl(kOLoc);
    ::SetFocus(focus);
}

void Window::UpdateFooter() {
    const bool blocked = hello_["existing"]["blocked"].asBool();
    const bool beforeStart = page_ == Page::Welcome || page_ == Page::Options || page_ == Page::License;
    bool secondary = false, primary = true, primaryEnabled = true, shield = false;
    std::wstring secondaryText, primaryText, cancelText = S("button.cancel");
    switch (page_) {
        case Page::Welcome:  // options are reached through the "Change" button next to the location
            primaryText = ShowsLicense() ? S("button.next") : PrimaryVerb();
            shield = !ShowsLicense() && NeedsElevation();
            primaryEnabled = !blocked && pathOk_ && !pathDirty_;
            break;
        case Page::Options:
            secondary = true;
            secondaryText = S("button.back");
            primaryText = ShowsLicense() ? S("button.next") : PrimaryVerb();
            shield = !ShowsLicense() && NeedsElevation();
            primaryEnabled = pathOk_ && !pathDirty_;
            break;
        case Page::License:
            secondary = true;
            secondaryText = S("button.back");
            primaryText = PrimaryVerb();
            shield = NeedsElevation();
            primaryEnabled = Checked(kLAccept) && pathOk_;
            break;
        case Page::Progress:
            primary = false;
            break;
        case Page::Result:
            cancelText = S("button.close");
            primary = result_ == ResultKind::Success ? canLaunch_ : result_ == ResultKind::Failed && retryable_;
            primaryText = result_ == ResultKind::Success ? S("button.launch") : S("button.retry");
            break;
    }
    Show(kLaunch, beforeStart && LaunchOffered());
    for (int id : {kPStage, kPPercent, kPBar, kPDetail}) Show(id, page_ == Page::Progress);
    Show(kSecondary, secondary);
    SetText(kSecondary, secondaryText);
    Show(IDOK, primary);
    SetText(IDOK, primaryText);
    ::EnableWindow(Ctl(IDOK), primaryEnabled);
    Button_SetElevationRequiredState(Ctl(IDOK), shield);
    Show(IDCANCEL, true);
    SetText(IDCANCEL, cancelText);
    ::EnableWindow(Ctl(IDCANCEL), page_ != Page::Progress || (cancellable_ && !cancelRequested_));
    Layout();
}

void Window::Layout() {
    if (!hwnd_) return;
    RECT client{};
    ::GetClientRect(hwnd_, &client);
    const int left = Px(kMargin), right = client.right - Px(kMargin);
    const int iconLeft = right - Px(kIconSize);
    const int textRight = iconLeft - Px(24);
    HDWP dwp = ::BeginDeferWindowPos(48);
    const auto place = [&](int id, int x, int y, int w, int h) {
        dwp = ::DeferWindowPos(dwp, Ctl(id), nullptr, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    };
    int y = Px(16);
    const auto stack = [&](int id, int gapAfter, int width, int h = -1, int x = -1) {
        if (!Visible(id)) return;
        if (x < 0) x = left;
        const int height = h >= 0 ? h : TextHeight(id, width);
        place(id, x, y, width, height);
        y += height + gapAfter;
    };
    const int textWidth = textRight - left;
    infoRect_ = {};
    switch (page_) {
        case Page::Welcome:
        case Page::Progress:
        case Page::Result: {
            int titleLeft = left;
            if (Visible(kStatus)) {
                place(kStatus, left, y + Px(6), Px(32), Px(32));
                titleLeft = left + Px(44);
            }
            stack(kTitle, Px(8), textRight - titleLeft, -1, titleLeft);
            stack(kDescription, Px(8), textWidth);
            y += Px(12);
            // Aligned label / value rows.
            int labelW = 0;
            for (int id : {kLblPublisher, kLblVersion, kLblLocation, kLblSize}) {
                HDC dc = ::GetDC(hwnd_);
                HGDIOBJ old = ::SelectObject(dc, theme_.body);
                const std::wstring t = GetText(id);
                SIZE sz{};
                ::GetTextExtentPoint32W(dc, t.c_str(), static_cast<int>(t.size()), &sz);
                ::SelectObject(dc, old);
                ::ReleaseDC(hwnd_, dc);
                labelW = std::max(labelW, static_cast<int>(sz.cx));
            }
            const int valueX = left + labelW + Px(24);
            for (const auto [label, value] : {std::pair{kLblPublisher, kPublisher}, std::pair{kLblVersion, kVersion},
                                              std::pair{kLblLocation, kLocation}, std::pair{kLblSize, kSize}}) {
                if (!Visible(value)) continue;
                const int h = TextHeight(label, labelW + Px(4));
                place(label, left, y, labelW + Px(4), h);
                const int valueW = textRight - valueX;
                place(value, valueX, y, valueW, h);  // the location static uses SS_PATHELLIPSIS: C:\Users\...\App
                y += h + Px(8);
                if (value == kLocation && Visible(kChange)) {
                    SIZE ideal{};
                    Button_GetIdealSize(Ctl(kChange), &ideal);
                    const int bw = std::max(Px(72), static_cast<int>(ideal.cx) + Px(16));
                    place(kChange, valueX, y - Px(2), bw, Px(28));
                    y += Px(28) + Px(8);
                }
            }
            y += Px(4);
            if (Visible(kInfo)) {
                const int pad = Px(14);
                const int width = right - left;
                const int h = TextHeight(kInfo, width - 2 * pad);
                infoRect_ = {left, y, right, y + h + 2 * pad};
                place(kInfo, left + pad, y + pad, width - 2 * pad, h);
                y += h + 2 * pad + Px(12);
            }
            stack(kDirError, Px(12), right - left);
            stack(kResultBody, Px(8), right - left);
            stack(kResultCode, Px(8), right - left);
            stack(kResultDetail, Px(8), right - left, Px(56));

            break;
        }
        case Page::Options: {
            const int width = right - left;
            stack(kOHeading, Px(16), width);
            stack(kOScopeLabel, Px(6), width);
            stack(kOScopeUser, Px(4), width, Px(22));
            stack(kOScopeMachine, Px(16), width, Px(22));
            stack(kOLocLabel, Px(6), width);
            if (Visible(kOLoc)) {
                const int browseW = Px(104), h = Px(32);
                place(kOLoc, left, y, width - browseW - Px(8), h);
                place(kOBrowse, right - browseW, y, browseW, h);
                y += h + Px(6);
            }
            stack(kOSpace, Px(6), width);
            stack(kOError, Px(10), width);
            if (Visible(kOShortLabel)) y += Px(6);
            stack(kOShortLabel, Px(6), width);
            stack(kODesktop, Px(4), width, Px(22));
            stack(kOStartMenu, 0, width, Px(22));
            break;
        }
        case Page::License: {
            const int width = right - left;
            stack(kLHeading, Px(8), width);
            stack(kLSub, Px(12), width);
            stack(kLText, Px(12), width, Px(150));
            stack(kLAccept, 0, width, Px(22));
            break;
        }
    }

    // Bottom bar: actions on the right, launch checkbox or progress on the left.
    const int buttonH = Px(32), footerY = client.bottom - Px(24) - buttonH;
    const auto buttonWidth = [&](int id) {
        SIZE ideal{};
        Button_GetIdealSize(Ctl(id), &ideal);
        return std::max(Px(96), static_cast<int>(ideal.cx) + Px(24));
    };
    int x = right;
    for (int id : {static_cast<int>(IDCANCEL), static_cast<int>(IDOK), static_cast<int>(kSecondary)}) {
        if (!Visible(id)) continue;
        const int w = buttonWidth(id);
        x -= w;
        place(id, x, footerY, w, buttonH);
        x -= Px(8);
    }
    const int leftWidth = x - Px(16) - left;
    if (Visible(kLaunch)) place(kLaunch, left, footerY + (buttonH - Px(22)) / 2, leftWidth, Px(22));
    int linkX = left;
    for (int id : {static_cast<int>(kOpenFolder), static_cast<int>(kViewLog)}) {
        if (!Visible(id)) continue;
        const int w = buttonWidth(id);
        place(id, linkX, footerY, w, buttonH);
        linkX += w + Px(8);
    }
    if (Visible(kPBar)) {
        const int lineH = TextHeight(kPStage, leftWidth);
        const int captionH = TextHeight(kPDetail, leftWidth);
        int py = footerY + buttonH - (lineH + Px(6) + Px(4) + Px(4) + captionH);
        place(kPStage, left, py, leftWidth - Px(56), lineH);
        place(kPPercent, left + leftWidth - Px(52), py, Px(52), lineH);
        py += lineH + Px(6);
        place(kPBar, left, py, leftWidth, Px(4));
        py += Px(8);
        place(kPDetail, left, py, leftWidth, captionH);
    }
    ::EndDeferWindowPos(dwp);
}

void Window::Paint(HDC dc) {
    RECT client{};
    ::GetClientRect(hwnd_, &client);
    ::FillRect(dc, &client, theme_.baseBrush());
    if (iconLarge_ && page_ != Page::Options && page_ != Page::License) {
        ::DrawIconEx(dc, client.right - Px(kMargin) - Px(kIconSize), Px(20), iconLarge_, Px(kIconSize), Px(kIconSize), 0, nullptr, DI_NORMAL);
    }
    if (page_ == Page::Welcome && Visible(kInfo)) {
        HPEN border = ::CreatePen(PS_SOLID, 1, theme_.colors().infoStroke);
        HGDIOBJ oldPen = ::SelectObject(dc, border);
        HGDIOBJ oldBrush = ::SelectObject(dc, theme_.infoBrush());
        ::RoundRect(dc, infoRect_.left, infoRect_.top, infoRect_.right, infoRect_.bottom, Px(8), Px(8));
        ::SelectObject(dc, oldBrush);
        ::SelectObject(dc, oldPen);
        ::DeleteObject(border);
    }
}

LRESULT Window::CustomDrawButton(NMCUSTOMDRAW* cd) {
    const LONG style = ::GetWindowLongW(cd->hdr.hwndFrom, GWL_STYLE) & BS_TYPEMASK;
    const bool check = style == BS_AUTOCHECKBOX || style == BS_CHECKBOX;
    const bool radio = style == BS_AUTORADIOBUTTON || style == BS_RADIOBUTTON;
    if ((!check && !radio) || theme_.highContrast()) return CDRF_DODEFAULT;
    if (cd->dwDrawStage != CDDS_PREPAINT) return CDRF_DODEFAULT;

    // Draw the themed glyph ourselves so the label follows the light/dark palette.
    HWND h = cd->hdr.hwndFrom;
    HDC dc = cd->hdc;
    RECT rc = cd->rc;
    ::FillRect(dc, &rc, theme_.baseBrush());
    const LRESULT state = ::SendMessageW(h, BM_GETSTATE, 0, 0);
    const bool checked = (state & BST_CHECKED) != 0, hot = (state & BST_HOT) != 0, pushed = (state & BST_PUSHED) != 0;
    const bool disabled = !::IsWindowEnabled(h);
    HTHEME th = ::OpenThemeDataForDpi(h, theme_.dark() ? L"DarkMode_Explorer::Button" : L"Button", dpi_);
    if (!th) th = ::OpenThemeDataForDpi(h, L"Button", dpi_);
    const int part = check ? BP_CHECKBOX : BP_RADIOBUTTON;
    const int base = checked ? (check ? CBS_CHECKEDNORMAL : RBS_CHECKEDNORMAL) : (check ? CBS_UNCHECKEDNORMAL : RBS_UNCHECKEDNORMAL);
    const int s = base + (disabled ? 3 : pushed ? 2 : hot ? 1 : 0);
    SIZE glyph{Px(16), Px(16)};
    if (th) ::GetThemePartSize(th, dc, part, s, nullptr, TS_DRAW, &glyph);
    RECT g{rc.left, rc.top + (rc.bottom - rc.top - glyph.cy) / 2, rc.left + glyph.cx, 0};
    g.bottom = g.top + glyph.cy;
    if (th) {
        ::DrawThemeBackground(th, dc, part, s, &g, nullptr);
        ::CloseThemeData(th);
    }
    wchar_t text[512] = {};
    ::GetWindowTextW(h, text, 512);
    RECT t{g.right + Px(8), rc.top, rc.right, rc.bottom};
    ::SetBkMode(dc, TRANSPARENT);
    ::SetTextColor(dc, disabled ? theme_.colors().textSecondary : theme_.colors().text);
    HGDIOBJ old = ::SelectObject(dc, reinterpret_cast<HFONT>(::SendMessageW(h, WM_GETFONT, 0, 0)));
    const LRESULT ui = ::SendMessageW(h, WM_QUERYUISTATE, 0, 0);
    UINT flags = DT_SINGLELINE | DT_VCENTER | DT_LEFT;
    if (ui & UISF_HIDEACCEL) flags |= DT_HIDEPREFIX;
    RECT measure = t;
    ::DrawTextW(dc, text, -1, &measure, flags | DT_CALCRECT);
    ::DrawTextW(dc, text, -1, &t, flags);
    if (::GetFocus() == h && !(ui & UISF_HIDEFOCUS)) {
        const int th2 = measure.bottom - measure.top;
        RECT f{t.left - Px(2), t.top + (t.bottom - t.top - th2) / 2 - Px(1), t.left + (measure.right - measure.left) + Px(2), 0};
        f.bottom = f.top + th2 + Px(2);
        ::SetTextColor(dc, theme_.colors().text);
        ::DrawFocusRect(dc, &f);
    }
    ::SelectObject(dc, old);
    return CDRF_SKIPDEFAULT;
}

void Window::DrawStatus(const DRAWITEMSTRUCT* di) {
    ::FillRect(di->hDC, &di->rcItem, theme_.baseBrush());
    if (statusGlyph_.empty()) return;
    HGDIOBJ old = ::SelectObject(di->hDC, theme_.statusIcon);
    ::SetBkMode(di->hDC, TRANSPARENT);
    ::SetTextColor(di->hDC, statusColor_);
    RECT r = di->rcItem;
    ::DrawTextW(di->hDC, statusGlyph_.c_str(), -1, &r, DT_SINGLELINE | DT_LEFT | DT_TOP | DT_NOPREFIX);
    ::SelectObject(di->hDC, old);
}

void Window::OnPrimary() {
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
            } else if (result_ == ResultKind::Success) {
                Finish(true);
            }
            break;
    }
}

void Window::OnSecondary() {
    if (page_ == Page::Welcome) ShowPage(Page::Options);
    else if (page_ == Page::License) ShowPage(HasOptions() ? Page::Options : Page::Welcome);
    else ShowPage(Page::Welcome);
}

void Window::Finish(bool launch) {
    if (launch && canLaunch_) c_.LaunchApp();
    ::DestroyWindow(hwnd_);
}

void Window::RequestClose() {
    if (page_ == Page::Progress) {
        if (!cancellable_ || cancelRequested_) return;
        const std::wstring title = S(IsUninstall() ? "cancel.title.uninstall" : "cancel.title");
        const std::wstring body = S("cancel.body"), yes = S("cancel.confirm"), no = S("cancel.continue");
        const std::wstring caption = [this] {
            std::wstring t(static_cast<size_t>(::GetWindowTextLengthW(hwnd_)) + 1, L'\0');
            ::GetWindowTextW(hwnd_, t.data(), static_cast<int>(t.size()));
            return t;
        }();
        TASKDIALOG_BUTTON buttons[] = {{IDYES, yes.c_str()}, {IDNO, no.c_str()}};
        TASKDIALOGCONFIG tdc{sizeof(tdc)};
        tdc.hwndParent = hwnd_;
        tdc.dwFlags = TDF_POSITION_RELATIVE_TO_WINDOW | TDF_ALLOW_DIALOG_CANCELLATION;
        tdc.pszWindowTitle = caption.c_str();
        tdc.pszMainInstruction = title.c_str();
        tdc.pszContent = body.c_str();
        tdc.cButtons = 2;
        tdc.pButtons = buttons;
        tdc.nDefaultButton = IDNO;
        int choice = IDNO;
        if (SUCCEEDED(::TaskDialogIndirect(&tdc, &choice, nullptr, nullptr)) && choice == IDYES && page_ == Page::Progress && cancellable_) {
            cancelRequested_ = true;
            SetText(kPStage, S("state.Cancelling"));
            UpdateFooter();
            c_.Cancel();
        }
        return;
    }
    if (!c_.Started()) c_.MarkClosedByUser();
    ::DestroyWindow(hwnd_);
}

void Window::StartInstall() {
    install::InstallOptions o = c_.DefaultOptions();
    if (!IsUninstall()) {
        Value v;
        v.set("installDir", installDir_);
        v.set("scope", scope_);
        v.set("desktopShortcut", Checked(kODesktop));
        v.set("startMenuShortcut", Checked(kOStartMenu));
        if (!c_.SanitizeOptions(v, o)) {
            pathOk_ = false;
            SetText(kOError, S("error.InvalidArguments"));
            ShowPage(Page::Options);
            return;
        }
    }
    cancelRequested_ = false;
    cancellable_ = true;
    result_ = ResultKind::None;
    SetText(kPStage, S("state.Validating"));
    SetText(kPDetail, L"");
    SetText(kPPercent, L"");
    HWND bar = Ctl(kPBar);
    ::SetWindowLongW(bar, GWL_STYLE, ::GetWindowLongW(bar, GWL_STYLE) | PBS_MARQUEE);
    ::SendMessageW(bar, PBM_SETMARQUEE, TRUE, 30);
    ShowPage(Page::Progress);
    c_.Start(o, hwnd_);
}

void Window::OnProgressMsg(const install::Progress& p) {
    if (page_ != Page::Progress) ShowPage(Page::Progress);
    if (!cancelRequested_ || p.state == install::State::RollingBack) SetText(kPStage, S(std::string("state.") + install::StateName(p.state)));
    HWND bar = Ctl(kPBar);
    const LONG style = ::GetWindowLongW(bar, GWL_STYLE);
    if (p.fraction < 0) {
        if (!(style & PBS_MARQUEE)) {
            ::SetWindowLongW(bar, GWL_STYLE, style | PBS_MARQUEE);
            ::SendMessageW(bar, PBM_SETMARQUEE, TRUE, 30);
        }
        SetText(kPPercent, L"");
    } else {
        if (style & PBS_MARQUEE) {
            ::SendMessageW(bar, PBM_SETMARQUEE, FALSE, 0);
            ::SetWindowLongW(bar, GWL_STYLE, style & ~PBS_MARQUEE);
        }
        ::SendMessageW(bar, PBM_SETPOS, static_cast<WPARAM>(p.fraction * 1000.0), 0);
        SetText(kPPercent, Printf(L"%d%%", static_cast<int>(p.fraction * 100.0)));
    }
    SetText(kPDetail, p.detail);
    if (p.cancellable != cancellable_) {
        cancellable_ = p.cancellable;
        UpdateFooter();
    }
}

void Window::OnDoneMsg(const Value& m) {
    const std::string r = m["result"].asString();
    const std::wstring name = AppName();
    const bool uninstall = IsUninstall();
    logPath_ = m["log"].asWide();
    if (!m["installDir"].asWide().empty()) installDir_ = m["installDir"].asWide();
    retryable_ = m["retryable"].asBool(true);
    canLaunch_ = !uninstall && r == "success" && m["canLaunch"].asBool();
    if (canLaunch_ && LaunchOffered() && Checked(kLaunch)) {  // "Launch when ready"
        Finish(true);
        return;
    }
    const auto& c = theme_.colors();
    std::wstring body;
    if (r == "success") {
        result_ = ResultKind::Success;
        statusGlyph_ = L"";
        statusColor_ = c.success;
        const bool updated = hello_["existing"]["relation"].asString() == "upgrade";
        SetText(kTitle, F(uninstall ? "done.uninstalled" : updated ? "done.updated" : "done.installed", {name}));
        body = uninstall ? std::wstring() : hello_["ui"]["finishText"].asWide();
        if (uninstall && m["leftovers"].asBool()) body = F("result.leftovers", {installDir_});
    } else {
        const bool cancelled = r == "cancelled";
        result_ = cancelled ? ResultKind::Cancelled : ResultKind::Failed;
        statusGlyph_ = cancelled ? L"" : L"";
        statusColor_ = cancelled ? c.caution : c.critical;
        SetText(kTitle, S(cancelled ? (uninstall ? "result.cancelled.title.uninstall" : "result.cancelled.title")
                                    : (uninstall ? "result.failed.title.uninstall" : "result.failed.title")));
        body = cancelled ? std::wstring() : m["message"].asWide();
        const std::wstring extra = m["restored"].asBool() ? S("result.restored") : m["noChanges"].asBool() || cancelled ? S("result.noChanges") : std::wstring();
        if (!extra.empty()) body += (body.empty() ? L"" : L"\r\n") + extra;
        SetText(kResultCode, F("result.code", {std::to_wstring(m["code"].asInt()), m["codeName"].asWide()}));
        SetText(kResultDetail, m["detail"].asWide());
    }
    SetText(kResultBody, body);
    ShowPage(Page::Result);
    const bool failed = result_ == ResultKind::Failed;
    Show(kResultBody, !body.empty());
    Show(kResultCode, failed);
    Show(kResultDetail, failed && !m["detail"].asWide().empty());
    Show(kOpenFolder, result_ == ResultKind::Success && !uninstall && !installDir_.empty());
    Show(kViewLog, failed && !logPath_.empty());
    UpdateFooter();
    ::InvalidateRect(Ctl(kStatus), nullptr, TRUE);
}

void Window::CheckPathNow() {
    const std::wstring text = GetText(kOLoc);
    const Value r = c_.CheckPath(text, scope_ == L"machine" ? install::Scope::Machine : install::Scope::User);
    pathDirty_ = false;
    pathOk_ = r["ok"].asBool();
    installDir_ = r["normalized"].asWide().empty() ? text : r["normalized"].asWide();
    requiredBytes_ = r["requiredBytes"].asUint(requiredBytes_);
    freeBytes_ = r["freeBytes"].asUint();
    SetText(kOError, r["message"].asWide());
    SetText(kDirError, r["message"].asWide());
    Show(kOError, page_ == Page::Options && !pathOk_);
    UpdateSpace();
    UpdateFooter();
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void Window::OnScopeChanged() {
    const std::wstring scope = Checked(kOScopeMachine) ? L"machine" : L"user";
    if (scope == scope_) return;
    scope_ = scope;
    if (!userEditedPath_) {
        suppress_ = true;
        SetText(kOLoc, hello_["options"][scope == L"machine" ? "machineDir" : "userDir"].asWide());
        suppress_ = false;
    }
    CheckPathNow();
}

void Window::Browse() {
    IFileOpenDialog* dialog = nullptr;
    if (FAILED(::CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dialog)))) return;
    DWORD flags = 0;
    dialog->GetOptions(&flags);
    dialog->SetOptions(flags | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
    const std::wstring title = S("options.browseTitle");
    dialog->SetTitle(title.c_str());
    IShellItem* item = nullptr;
    PWSTR chosen = nullptr;
    if (SUCCEEDED(dialog->Show(hwnd_)) && SUCCEEDED(dialog->GetResult(&item)) && SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &chosen))) {
        std::wstring dir = chosen;
        const std::wstring leaf = hello_["options"]["dirLeaf"].asWide();
        if (!leaf.empty()) {
            if (dir.back() != L'\\') dir.push_back(L'\\');
            dir += leaf;
        }
        SetText(kOLoc, dir);  // EN_CHANGE -> validation
        ::CoTaskMemFree(chosen);
    }
    if (item) item->Release();
    dialog->Release();
}

LRESULT CALLBACK Window::Proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    Window* self = nullptr;
    if (m == WM_NCCREATE) {
        self = static_cast<Window*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);
        self->hwnd_ = h;
        ::SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    } else {
        self = reinterpret_cast<Window*>(::GetWindowLongPtrW(h, GWLP_USERDATA));
    }
    return self ? self->Handle(m, w, l) : ::DefWindowProcW(h, m, w, l);
}

LRESULT Window::Handle(UINT m, WPARAM w, LPARAM l) {
    switch (m) {
        case WM_ERASEBKGND: return 1;
        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = ::BeginPaint(hwnd_, &ps);
            Paint(dc);
            ::EndPaint(hwnd_, &ps);
            return 0;
        }
        case WM_CTLCOLORSTATIC:
        case WM_CTLCOLOREDIT:
        case WM_CTLCOLORBTN: {
            HDC dc = reinterpret_cast<HDC>(w);
            const int id = ::GetDlgCtrlID(reinterpret_cast<HWND>(l));
            const auto& c = theme_.colors();
            HBRUSH brush = theme_.baseBrush();
            COLORREF bg = c.base, fg = c.text;
            if (id == kInfo) brush = theme_.infoBrush(), bg = c.infoFill;
            if (id == kOLoc || id == kLText || id == kResultDetail) brush = theme_.editBrush(), bg = c.editFill;
            if (id == kLblPublisher || id == kLblVersion || id == kLblLocation || id == kLblSize || id == kDescription || id == kOSpace || id == kPDetail || id == kPPercent || id == kResultCode) fg = c.textSecondary;
            if (id == kOError || id == kDirError) fg = c.critical;
            ::SetTextColor(dc, fg);
            ::SetBkColor(dc, bg);
            return reinterpret_cast<LRESULT>(brush);
        }
        case WM_NOTIFY:
            if (reinterpret_cast<NMHDR*>(l)->code == NM_CUSTOMDRAW) return CustomDrawButton(reinterpret_cast<NMCUSTOMDRAW*>(l));
            break;
        case WM_DRAWITEM:
            if (w == kStatus) {
                DrawStatus(reinterpret_cast<DRAWITEMSTRUCT*>(l));
                return TRUE;
            }
            break;
        case DM_GETDEFID:
            return MAKELRESULT(IDOK, DC_HASDEFID);
        case WM_COMMAND: {
            const int id = LOWORD(w), code = HIWORD(w);
            if (id == IDOK && Visible(IDOK) && ::IsWindowEnabled(Ctl(IDOK))) OnPrimary();
            else if (id == IDCANCEL) RequestClose();
            else if (id == kSecondary || id == kChange) OnSecondary();
            else if (id == kOBrowse) Browse();
            else if ((id == kOScopeUser || id == kOScopeMachine) && code == BN_CLICKED) OnScopeChanged();
            else if (id == kLAccept && code == BN_CLICKED) UpdateFooter();
            else if (id == kOLoc && code == EN_CHANGE && !suppress_) {
                userEditedPath_ = true;
                pathDirty_ = true;
                UpdateFooter();
                ::SetTimer(hwnd_, kPathTimer, 350, nullptr);
            } else if (id == kViewLog && !logPath_.empty()) {
                ::ShellExecuteW(hwnd_, L"open", L"notepad.exe", (L"\"" + logPath_ + L"\"").c_str(), nullptr, SW_SHOWNORMAL);
            } else if (id == kOpenFolder && !installDir_.empty()) {
                ::ShellExecuteW(hwnd_, L"open", installDir_.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            }
            return 0;
        }
        case WM_TIMER:
            if (w == kPathTimer) {
                ::KillTimer(hwnd_, kPathTimer);
                CheckPathNow();
            }
            return 0;
        case WM_APP_PROGRESS: {
            std::unique_ptr<install::Progress> p(reinterpret_cast<install::Progress*>(l));
            if (page_ == Page::Progress || c_.Running()) OnProgressMsg(*p);
            return 0;
        }
        case WM_APP_DONE: {
            std::unique_ptr<Value> v(reinterpret_cast<Value*>(l));
            OnDoneMsg(*v);
            return 0;
        }
        case WM_CLOSE:
            if (page_ == Page::Result && result_ == ResultKind::Success) Finish(false);
            else RequestClose();
            return 0;
        case WM_DESTROY:
            ::PostQuitMessage(0);
            return 0;
        case WM_DPICHANGED: {
            dpi_ = HIWORD(w);
            const RECT* r = reinterpret_cast<const RECT*>(l);
            ApplyFonts();
            ::SetWindowPos(hwnd_, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top, SWP_NOZORDER | SWP_NOACTIVATE);
            Layout();
            ::InvalidateRect(hwnd_, nullptr, TRUE);
            return 0;
        }
        case WM_SETTINGCHANGE:
            if (l && wcscmp(reinterpret_cast<const wchar_t*>(l), L"ImmersiveColorSet") == 0) ApplyTheme();
            break;
        case WM_THEMECHANGED:
        case WM_SYSCOLORCHANGE:
            ApplyTheme();
            break;
        default: break;
    }
    return ::DefWindowProcW(hwnd_, m, w, l);
}

}  // namespace

void Run(boot::Controller& controller, bool /*resume*/) {
    const HRESULT com = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    {
        Window window(controller, controller.Hello(L""));
        window.Run();
    }
    if (SUCCEEDED(com)) ::CoUninitialize();
}

}  // namespace fsetup::win32ui
