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
constexpr int kClientWidth = 720;   // DIP, same as the WinUI window
constexpr int kClientHeight = 500;  // DIP
constexpr int kMargin = 32;
constexpr int kHeaderHeight = 88;

enum Id : int {
    kHeaderName = 100, kHeaderMeta,
    kWHeading = 200, kWBody, kWInfo, kWError, kWLocLabel, kWLoc, kWSpace,
    kOHeading = 300, kOScopeLabel, kOScopeUser, kOScopeMachine, kOLocLabel, kOLoc, kOBrowse, kOSpace, kOError,
    kOShortLabel, kODesktop, kOStartMenu,
    kLHeading = 400, kLSub, kLText, kLAccept,
    kPHeading = 500, kPStage, kPBar, kPDetail, kPPercent,
    kRIcon = 600, kRHeading, kRBody, kRLaunch, kRCode, kRDetail, kRFolder, kRLog,
    kSecondary = 700,
};

enum class Page { Welcome, Options, License, Progress, Result };
enum class ResultKind { None, Success, Failed, Cancelled };

class Window : public boot::IListener {
public:
    Window(Controller& controller, Value hello) : c_(controller), hello_(std::move(hello)) {}

    void Run(bool resume);

    // IListener (engine thread): marshal to the UI thread.
    void OnProgress(const install::Progress& p) override {
        if (hwnd_) ::PostMessageW(hwnd_, WM_APP_PROGRESS, 0, reinterpret_cast<LPARAM>(new install::Progress(p)));
    }
    void OnDone(const install::Outcome& o) override {
        if (hwnd_) ::PostMessageW(hwnd_, WM_APP_DONE, 0, reinterpret_cast<LPARAM>(new Value(c_.DoneMessage(o))));
    }

private:
    static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l);
    LRESULT Handle(UINT m, WPARAM w, LPARAM l);

    // helpers
    std::wstring S(std::string_view key) const { return c_.strings().Get(key); }
    std::wstring F(std::string_view key, std::initializer_list<std::wstring_view> a) const { return c_.strings().Fmt(key, a); }
    int Px(int dip) const { return MulDiv(dip, static_cast<int>(dpi_), 96); }
    HWND Ctl(int id) const { return ::GetDlgItem(hwnd_, id); }
    HWND Make(const wchar_t* cls, int id, DWORD style, const std::wstring& text = {}, DWORD exStyle = 0);
    void SetText(int id, const std::wstring& t) { ::SetWindowTextW(Ctl(id), t.c_str()); }
    void Show(int id, bool visible) { ::ShowWindow(Ctl(id), visible ? SW_SHOWNA : SW_HIDE); }
    bool Visible(int id) const { return (::GetWindowLongW(Ctl(id), GWL_STYLE) & WS_VISIBLE) != 0; }
    int TextHeight(int id, int width) const;

    void Create();
    void ApplyFonts();
    void ApplyTheme();
    void ApplyHello();
    void ShowPage(Page p);
    void Layout();
    void UpdateFooter();
    void Paint(HDC dc);
    LRESULT CustomDrawButton(NMCUSTOMDRAW* cd);
    void DrawResultIcon(const DRAWITEMSTRUCT* di);

    bool HasOptions() const;
    bool ShowsLicense() const;
    bool NeedsElevation() const { return scope_ == L"machine" && !hello_["elevated"].asBool(); }
    bool IsUninstall() const { return hello_["mode"].asString() == "uninstall"; }
    std::wstring AppName() const { return hello_["app"]["name"].asWide(); }
    std::wstring PrimaryVerb() const;

    void OnPrimary();
    void OnSecondary();
    void RequestClose();
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
    std::wstring scope_;
    std::wstring installDir_;
    std::wstring logPath_;
    std::wstring resultInstallDir_;
    uint64_t requiredBytes_ = 0, freeBytes_ = 0;
    bool pathOk_ = true;
    bool pathDirty_ = false;
    bool userEditedPath_ = false;
    bool suppress_ = false;
    bool cancellable_ = true;
    bool cancelRequested_ = false;
    bool retryable_ = true;
    bool done_ = false;
    std::wstring resultGlyph_;
    COLORREF resultColor_ = 0;
    RECT infoRect_{};
};

// ---------------------------------------------------------------------------------------------------------------

HWND Window::Make(const wchar_t* cls, int id, DWORD style, const std::wstring& text, DWORD exStyle) {
    return ::CreateWindowExW(exStyle, cls, text.c_str(), WS_CHILD | style, 0, 0, 10, 10, hwnd_,
                             reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), ::GetModuleHandleW(nullptr), nullptr);
}

int Window::TextHeight(int id, int width) const {
    HWND h = Ctl(id);
    const int len = ::GetWindowTextLengthW(h);
    std::wstring text(static_cast<size_t>(len) + 1, L'\0');
    ::GetWindowTextW(h, text.data(), len + 1);
    text.resize(static_cast<size_t>(len));
    if (text.empty()) text = L" ";
    HDC dc = ::GetDC(h);
    HGDIOBJ old = ::SelectObject(dc, reinterpret_cast<HFONT>(::SendMessageW(h, WM_GETFONT, 0, 0)));
    RECT r{0, 0, width, 0};
    ::DrawTextW(dc, text.c_str(), -1, &r, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX | DT_EDITCONTROL);
    ::SelectObject(dc, old);
    ::ReleaseDC(h, dc);
    return r.bottom - r.top;
}

void Window::Run(bool resume) {
    INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_STANDARD_CLASSES | ICC_PROGRESS_CLASS | ICC_LINK_CLASS};
    ::InitCommonControlsEx(&icc);
    // hello_ outlives theme_, so the setting string stays valid.
    const std::string& themeSetting = hello_["ui"]["theme"].asString();
    theme_.Init(themeSetting.empty() ? "system" : themeSetting.c_str());

    WNDCLASSEXW wc{sizeof(wc)};
    wc.lpfnWndProc = Proc;
    wc.hInstance = ::GetModuleHandleW(nullptr);
    wc.hCursor = ::LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FluentSetupFallbackWindow";
    wc.hIcon = ::LoadIconW(wc.hInstance, MAKEINTRESOURCEW(IDI_APP));
    ::RegisterClassExW(&wc);

    // Place on the monitor under the cursor, sized for its DPI.
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
    const int x = mi.rcWork.left + (mi.rcWork.right - mi.rcWork.left - w) / 2;
    const int y = mi.rcWork.top + (mi.rcWork.bottom - mi.rcWork.top - h) / 2;

    std::wstring title = hello_["ui"]["title"].asWide();
    if (title.empty()) title = F(IsUninstall() ? "window.title.uninstall" : "window.title.install", {AppName()});
    hwnd_ = ::CreateWindowExW(WS_EX_CONTROLPARENT, wc.lpszClassName, title.c_str(), style, x, y, w, h, nullptr, nullptr,
                              wc.hInstance, this);
    if (!hwnd_) return;
    dpi_ = ::GetDpiForWindow(hwnd_);

    Create();
    ApplyHello();
    ApplyTheme();

    // Resume: show whatever the session is doing right now.
    install::Progress last;
    std::optional<install::Outcome> outcome;
    bool started = false;
    c_.Attach(this, &last, &outcome, &started);
    if (outcome) {
        OnDoneMsg(c_.DoneMessage(*outcome));
    } else if (started) {
        ShowPage(Page::Progress);
        SetText(kPHeading, F(IsUninstall() ? "progress.title.uninstall" : "progress.title.install", {AppName()}));
        OnProgressMsg(last);
    } else {
        ShowPage(Page::Welcome);
    }
    (void)resume;

    // Hide focus rectangles and mnemonic underlines until the user actually uses the keyboard (standard dialog behaviour).
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
    // Drain anything posted between the last message and detaching.
    while (::PeekMessageW(&msg, nullptr, WM_APP_PROGRESS, WM_APP_DONE, PM_REMOVE)) {
        if (msg.message == WM_APP_PROGRESS) delete reinterpret_cast<install::Progress*>(msg.lParam);
        else delete reinterpret_cast<Value*>(msg.lParam);
    }
    if (iconLarge_) ::DestroyIcon(iconLarge_);
    if (iconSmall_) ::DestroyIcon(iconSmall_);
}

void Window::Create() {
    const DWORD text = WS_VISIBLE | SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL;
    Make(WC_STATICW, kHeaderName, text | SS_ENDELLIPSIS);
    Make(WC_STATICW, kHeaderMeta, text);

    Make(WC_STATICW, kWHeading, text);
    Make(WC_STATICW, kWBody, text);
    Make(WC_STATICW, kWInfo, text);
    Make(WC_STATICW, kWError, text);
    Make(WC_STATICW, kWLocLabel, text);
    Make(WC_STATICW, kWLoc, text | SS_PATHELLIPSIS);
    Make(WC_STATICW, kWSpace, text);

    Make(WC_STATICW, kOHeading, text);
    Make(WC_STATICW, kOScopeLabel, text);
    Make(WC_BUTTONW, kOScopeUser, BS_AUTORADIOBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kOScopeMachine, BS_AUTORADIOBUTTON);
    Make(WC_STATICW, kOLocLabel, text);
    Make(WC_EDITW, kOLoc, WS_TABSTOP | WS_GROUP | ES_AUTOHSCROLL, {}, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kOBrowse, BS_PUSHBUTTON | WS_TABSTOP);
    Make(WC_STATICW, kOSpace, text);
    Make(WC_STATICW, kOError, text);
    Make(WC_STATICW, kOShortLabel, text);
    Make(WC_BUTTONW, kODesktop, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kOStartMenu, BS_AUTOCHECKBOX | WS_TABSTOP);

    Make(WC_STATICW, kLHeading, text);
    Make(WC_STATICW, kLSub, text);
    Make(WC_EDITW, kLText, WS_TABSTOP | WS_GROUP | ES_MULTILINE | ES_READONLY | WS_VSCROLL | ES_AUTOVSCROLL, {}, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kLAccept, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);

    Make(WC_STATICW, kPHeading, text);
    Make(WC_STATICW, kPStage, text);
    Make(PROGRESS_CLASSW, kPBar, PBS_SMOOTH);
    Make(WC_STATICW, kPDetail, text | SS_PATHELLIPSIS);
    Make(WC_STATICW, kPPercent, text | SS_RIGHT);

    Make(WC_STATICW, kRIcon, SS_OWNERDRAW);
    Make(WC_STATICW, kRHeading, text);
    Make(WC_STATICW, kRBody, text);
    Make(WC_BUTTONW, kRLaunch, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);
    Make(WC_STATICW, kRCode, text);
    Make(WC_EDITW, kRDetail, WS_TABSTOP | ES_MULTILINE | ES_READONLY | WS_VSCROLL, {}, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kRFolder, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kRLog, BS_PUSHBUTTON | WS_TABSTOP);

    Make(WC_BUTTONW, kSecondary, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, IDOK, BS_DEFPUSHBUTTON | WS_TABSTOP);
    Make(WC_BUTTONW, IDCANCEL, BS_PUSHBUTTON | WS_TABSTOP);

    ::SendMessageW(Ctl(kPBar), PBM_SETRANGE32, 0, 1000);
    ::SendMessageW(Ctl(kOLoc), EM_SETLIMITTEXT, 240, 0);

    // Static texts that never change.
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
    SetText(kRFolder, S("result.openFolder"));
    SetText(kRLog, S("fallback.viewLog"));
}

void Window::ApplyFonts() {
    theme_.CreateFonts(dpi_);
    const auto set = [&](int id, HFONT f) { ::SendMessageW(Ctl(id), WM_SETFONT, reinterpret_cast<WPARAM>(f), FALSE); };
    for (int id : {kHeaderMeta, kWSpace, kOSpace, kPDetail, kPPercent, kRCode}) set(id, theme_.caption);
    set(kHeaderName, theme_.subtitle);
    for (int id : {kWHeading, kOHeading, kLHeading, kPHeading, kRHeading}) set(id, theme_.heading);
    for (int id : {kWLocLabel, kOScopeLabel, kOLocLabel, kOShortLabel}) set(id, theme_.bodyStrong);
    for (int id : std::initializer_list<int>{kWBody, kWInfo, kWError, kWLoc, kOScopeUser, kOScopeMachine, kOLoc, kOBrowse, kOError, kODesktop, kOStartMenu,
                   kLSub, kLText, kLAccept, kPStage, kRBody, kRLaunch, kRDetail, kRFolder, kRLog, kSecondary, static_cast<int>(IDOK), static_cast<int>(IDCANCEL)}) {
        set(id, theme_.body);
    }
    if (iconLarge_) ::DestroyIcon(iconLarge_);
    if (iconSmall_) ::DestroyIcon(iconSmall_);
    ::LoadIconWithScaleDown(::GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_APP), Px(48), Px(48), &iconLarge_);
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
    const Value& app = hello_["app"];
    const Value& opts = hello_["options"];
    const Value& existing = hello_["existing"];
    const std::wstring name = AppName();
    SetText(kHeaderName, name);
    std::wstring meta = F("header.version", {app["version"].asWide()});
    if (!app["publisher"].asWide().empty()) meta = app["publisher"].asWide() + L"  ·  " + meta;
    SetText(kHeaderMeta, meta);

    if (IsUninstall()) {
        SetText(kWHeading, F("uninstall.title", {name}));
        SetText(kWBody, F("uninstall.body", {name}));
        SetText(kWLocLabel, S("uninstall.location"));
    } else {
        const std::string relation = existing["relation"].asString();
        SetText(kWHeading, F(relation == "upgrade" ? "welcome.title.update" : "welcome.title", {name}));
        const std::wstring welcome = hello_["ui"]["welcomeText"].asWide();
        SetText(kWBody, welcome.empty() ? F("welcome.body", {name}) : welcome);
        SetText(kWLocLabel, S("welcome.location"));
        std::wstring info;
        if (relation == "upgrade") info = F("welcome.upgrade", {existing["version"].asWide(), app["version"].asWide()});
        else if (relation == "same") info = F("welcome.reinstall", {existing["version"].asWide()});
        else if (relation == "downgrade") {
            info = F(existing["blocked"].asBool() ? "welcome.downgradeBlocked" : "welcome.downgrade",
                     {existing["version"].asWide(), app["version"].asWide()});
        }
        SetText(kWInfo, info);
    }
    installDir_ = opts["installDir"].asWide();
    scope_ = opts["scope"].asWide();
    requiredBytes_ = hello_["requiredBytes"].asUint();
    freeBytes_ = hello_["freeBytes"].asUint();
    pathOk_ = opts["dirOk"].asBool(true);
    SetText(kWError, opts["dirMessage"].asWide());
    SetText(kOError, opts["dirMessage"].asWide());
    suppress_ = true;
    SetText(kOLoc, installDir_);
    suppress_ = false;
    Button_SetCheck(Ctl(scope_ == L"machine" ? kOScopeMachine : kOScopeUser), BST_CHECKED);
    Button_SetCheck(Ctl(kODesktop), opts["desktopShortcut"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    Button_SetCheck(Ctl(kOStartMenu), opts["startMenuShortcut"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    SetText(kRLaunch, F("fallback.launch", {name}));
    Button_SetCheck(Ctl(kRLaunch), opts["launchAfter"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    // Normalise line breaks for the multi-line edit control.
    std::wstring license = hello_["ui"]["licenseText"].asWide(), crlf;
    for (size_t i = 0; i < license.size(); ++i) {
        if (license[i] == L'\n' && (i == 0 || license[i - 1] != L'\r')) crlf.push_back(L'\r');
        crlf.push_back(license[i]);
    }
    SetText(kLText, crlf);
    UpdateSpace();
}

void Window::UpdateSpace() {
    SetText(kWLoc, installDir_);
    SetText(kWSpace, requiredBytes_ ? F("welcome.space", {FormatBytes(requiredBytes_)}) : std::wstring());
    SetText(kOSpace, freeBytes_ ? F("options.space", {FormatBytes(requiredBytes_), FormatBytes(freeBytes_)})
                                : F("welcome.space", {FormatBytes(requiredBytes_)}));
}

bool Window::HasOptions() const {
    if (IsUninstall()) return false;
    const Value& o = hello_["options"];
    return o["canChangeDir"].asBool() || o["canChangeScope"].asBool() || o["desktopShortcut"]["visible"].asBool() ||
           o["startMenuShortcut"]["visible"].asBool();
}

bool Window::ShowsLicense() const { return !IsUninstall() && !hello_["ui"]["licenseText"].asString().empty(); }

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
    const bool welcome = p == Page::Welcome, options = p == Page::Options, license = p == Page::License,
               progress = p == Page::Progress, result = p == Page::Result;
    for (int id : {kWHeading, kWBody, kWLocLabel, kWLoc}) Show(id, welcome);
    Show(kWSpace, welcome && !IsUninstall());
    Show(kWInfo, welcome && ::GetWindowTextLengthW(Ctl(kWInfo)) > 0);
    Show(kWError, welcome && !pathOk_);
    Show(kOHeading, options);
    for (int id : {kOScopeLabel, kOScopeUser, kOScopeMachine}) Show(id, options && o["canChangeScope"].asBool());
    for (int id : {kOLocLabel, kOLoc, kOBrowse, kOSpace}) Show(id, options && o["canChangeDir"].asBool());
    Show(kOError, options && !pathOk_);
    const bool desktop = o["desktopShortcut"]["visible"].asBool(), start = o["startMenuShortcut"]["visible"].asBool();
    Show(kOShortLabel, options && (desktop || start));
    Show(kODesktop, options && desktop);
    Show(kOStartMenu, options && start);
    for (int id : {kLHeading, kLSub, kLText, kLAccept}) Show(id, license);
    for (int id : {kPHeading, kPStage, kPBar, kPDetail, kPPercent}) Show(id, progress);
    for (int id : {kRIcon, kRHeading, kRBody}) Show(id, result);
    if (!result) {
        for (int id : {kRLaunch, kRCode, kRDetail, kRFolder, kRLog}) Show(id, false);
    }
    UpdateFooter();
    Layout();
    ::InvalidateRect(hwnd_, nullptr, TRUE);
    HWND focus = Visible(IDOK) && ::IsWindowEnabled(Ctl(IDOK)) ? Ctl(IDOK) : Ctl(IDCANCEL);
    if (p == Page::Options && o["canChangeDir"].asBool()) focus = Ctl(kOLoc);
    ::SetFocus(focus);
}

void Window::UpdateFooter() {
    const bool blocked = hello_["existing"]["blocked"].asBool();
    bool secondary = false, primary = true, cancel = true, primaryEnabled = true, shield = false;
    std::wstring secondaryText, primaryText, cancelText = S("button.cancel");
    switch (page_) {
        case Page::Welcome:
            secondary = HasOptions() && !blocked;
            secondaryText = S("button.options");
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
            primaryEnabled = Button_GetCheck(Ctl(kLAccept)) == BST_CHECKED && pathOk_;
            break;
        case Page::Progress:
            primary = false;
            break;
        case Page::Result:
            if (result_ == ResultKind::Success) {
                primaryText = S("button.finish");
                cancel = false;
            } else if (result_ == ResultKind::Failed) {
                primaryText = S("button.retry");
                primary = retryable_;
                cancelText = S("button.close");
            } else {
                primaryText = S("button.close");
                cancel = false;
            }
            break;
    }
    Show(kSecondary, secondary);
    SetText(kSecondary, secondaryText);
    Show(IDOK, primary);
    SetText(IDOK, primaryText);
    ::EnableWindow(Ctl(IDOK), primaryEnabled);
    Button_SetElevationRequiredState(Ctl(IDOK), shield);
    Show(IDCANCEL, cancel);
    SetText(IDCANCEL, cancelText);
    ::EnableWindow(Ctl(IDCANCEL), page_ != Page::Progress || (cancellable_ && !cancelRequested_));
    Layout();
}

void Window::Layout() {
    if (!hwnd_) return;
    RECT client{};
    ::GetClientRect(hwnd_, &client);
    const int width = client.right;
    const int left = Px(kMargin), right = width - Px(kMargin), contentWidth = right - left;
    HDWP dwp = ::BeginDeferWindowPos(48);
    const auto place = [&](int id, int x, int y, int w, int h) {
        dwp = ::DeferWindowPos(dwp, Ctl(id), nullptr, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    };

    // Header.
    const int textLeft = left + Px(48 + 16);
    const int nameH = TextHeight(kHeaderName, right - textLeft), metaH = TextHeight(kHeaderMeta, right - textLeft);
    const int headerTop = (Px(kHeaderHeight) - nameH - metaH - Px(2)) / 2;
    place(kHeaderName, textLeft, headerTop, right - textLeft, nameH);
    place(kHeaderMeta, textLeft, headerTop + nameH + Px(2), right - textLeft, metaH);

    // Content stacks.
    int y = Px(kHeaderHeight) + Px(24);
    const auto stack = [&](int id, int gapAfter, int h = -1, int indent = 0) {
        if (!Visible(id)) return;
        const int height = h >= 0 ? h : TextHeight(id, contentWidth - indent);
        place(id, left + indent, y, contentWidth - indent, height);
        y += height + gapAfter;
    };
    infoRect_ = {};
    switch (page_) {
        case Page::Welcome:
            stack(kWHeading, Px(12));
            stack(kWBody, Px(16));
            if (Visible(kWInfo)) {
                const int pad = Px(12);
                const int h = TextHeight(kWInfo, contentWidth - 2 * pad);
                infoRect_ = {left, y, right, y + h + 2 * pad};
                place(kWInfo, left + pad, y + pad, contentWidth - 2 * pad, h);
                y += h + 2 * pad + Px(16);
            }
            stack(kWError, Px(16));
            stack(kWLocLabel, Px(4));
            stack(kWLoc, Px(4));
            stack(kWSpace, 0);
            break;
        case Page::Options: {
            stack(kOHeading, Px(16));
            stack(kOScopeLabel, Px(6));
            stack(kOScopeUser, Px(4), Px(22));
            stack(kOScopeMachine, Px(16), Px(22));
            stack(kOLocLabel, Px(6));
            if (Visible(kOLoc)) {
                const int browseW = Px(104), h = Px(32);
                place(kOLoc, left, y, contentWidth - browseW - Px(8), h);
                place(kOBrowse, right - browseW, y, browseW, h);
                y += h + Px(6);
            }
            stack(kOSpace, Px(6));
            stack(kOError, Px(10));
            if (Visible(kOShortLabel)) y += Px(6);
            stack(kOShortLabel, Px(6));
            stack(kODesktop, Px(4), Px(22));
            stack(kOStartMenu, 0, Px(22));
            break;
        }
        case Page::License:
            stack(kLHeading, Px(8));
            stack(kLSub, Px(12));
            stack(kLText, Px(12), Px(150));
            stack(kLAccept, 0, Px(22));
            break;
        case Page::Progress: {
            stack(kPHeading, Px(12));
            stack(kPStage, Px(12));
            stack(kPBar, Px(10), Px(4));
            const int h = TextHeight(kPDetail, contentWidth);
            place(kPDetail, left, y, contentWidth - Px(56), h);
            place(kPPercent, right - Px(48), y, Px(48), h);
            break;
        }
        case Page::Result:
            place(kRIcon, left, y, Px(48), Px(48));
            y += Px(52);
            stack(kRHeading, Px(12));
            stack(kRBody, Px(12));
            stack(kRLaunch, Px(8), Px(22));
            stack(kRCode, Px(8));
            stack(kRDetail, Px(8), Px(64));
            if (Visible(kRFolder) || Visible(kRLog)) {
                int x = left;
                for (int id : {kRFolder, kRLog}) {
                    if (!Visible(id)) continue;
                    SIZE ideal{};
                    Button_GetIdealSize(Ctl(id), &ideal);
                    const int w = std::max(Px(120), static_cast<int>(ideal.cx) + Px(24));
                    place(id, x, y, w, Px(32));
                    x += w + Px(8);
                }
            }
            break;
    }

    // Footer.
    const int buttonH = Px(32), footerY = client.bottom - Px(24) - buttonH;
    const auto buttonWidth = [&](int id) {
        HWND h = Ctl(id);
        SIZE ideal{};
        Button_GetIdealSize(h, &ideal);
        return std::max(Px(96), static_cast<int>(ideal.cx) + Px(24));
    };
    int x = right;
    for (int id : {IDCANCEL, IDOK}) {
        if (!Visible(id)) continue;
        const int w = buttonWidth(id);
        x -= w;
        place(id, x, footerY, w, buttonH);
        x -= Px(8);
    }
    if (Visible(kSecondary)) place(kSecondary, left, footerY, buttonWidth(kSecondary), buttonH);
    ::EndDeferWindowPos(dwp);
}

void Window::Paint(HDC dc) {
    RECT client{};
    ::GetClientRect(hwnd_, &client);
    RECT header{0, 0, client.right, Px(kHeaderHeight)};
    ::FillRect(dc, &header, theme_.baseBrush());
    RECT layer{0, Px(kHeaderHeight), client.right, client.bottom};
    ::FillRect(dc, &layer, theme_.layerBrush());
    HPEN pen = ::CreatePen(PS_SOLID, 1, theme_.colors().stroke);
    HGDIOBJ oldPen = ::SelectObject(dc, pen);
    ::MoveToEx(dc, 0, Px(kHeaderHeight), nullptr);
    ::LineTo(dc, client.right, Px(kHeaderHeight));
    if (iconLarge_) {
        ::DrawIconEx(dc, Px(kMargin), (Px(kHeaderHeight) - Px(48)) / 2, iconLarge_, Px(48), Px(48), 0, nullptr, DI_NORMAL);
    }
    if (page_ == Page::Welcome && Visible(kWInfo)) {
        HPEN border = ::CreatePen(PS_SOLID, 1, theme_.colors().infoStroke);
        ::SelectObject(dc, border);
        HGDIOBJ oldBrush = ::SelectObject(dc, theme_.infoBrush());
        ::RoundRect(dc, infoRect_.left, infoRect_.top, infoRect_.right, infoRect_.bottom, Px(8), Px(8));
        ::SelectObject(dc, oldBrush);
        ::SelectObject(dc, pen);
        ::DeleteObject(border);
    }
    ::SelectObject(dc, oldPen);
    ::DeleteObject(pen);
}

LRESULT Window::CustomDrawButton(NMCUSTOMDRAW* cd) {
    const LONG style = ::GetWindowLongW(cd->hdr.hwndFrom, GWL_STYLE) & BS_TYPEMASK;
    const bool check = style == BS_AUTOCHECKBOX || style == BS_CHECKBOX;
    const bool radio = style == BS_AUTORADIOBUTTON || style == BS_RADIOBUTTON;
    if ((!check && !radio) || theme_.highContrast()) return CDRF_DODEFAULT;
    if (cd->dwDrawStage != CDDS_PREPAINT) return CDRF_DODEFAULT;

    // Draw the themed glyph ourselves so the label can follow the light/dark palette.
    HWND h = cd->hdr.hwndFrom;
    HDC dc = cd->hdc;
    RECT rc = cd->rc;
    ::FillRect(dc, &rc, theme_.layerBrush());
    const LRESULT state = ::SendMessageW(h, BM_GETSTATE, 0, 0);
    const bool checked = (state & BST_CHECKED) != 0, hot = (state & BST_HOT) != 0, pushed = (state & BST_PUSHED) != 0;
    const bool disabled = !::IsWindowEnabled(h);
    HTHEME th = ::OpenThemeDataForDpi(h, theme_.dark() ? L"DarkMode_Explorer::Button" : L"Button", dpi_);
    if (!th) th = ::OpenThemeDataForDpi(h, L"Button", dpi_);
    int part = check ? BP_CHECKBOX : BP_RADIOBUTTON;
    int s = 1;
    const int base = checked ? (check ? CBS_CHECKEDNORMAL : RBS_CHECKEDNORMAL) : (check ? CBS_UNCHECKEDNORMAL : RBS_UNCHECKEDNORMAL);
    s = base + (disabled ? 3 : pushed ? 2 : hot ? 1 : 0);
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
        RECT f{t.left - Px(2), t.top + (t.bottom - t.top - (measure.bottom - measure.top)) / 2 - Px(1), t.left + (measure.right - measure.left) + Px(2), 0};
        f.bottom = f.top + (measure.bottom - measure.top) + Px(2);
        ::SetTextColor(dc, theme_.colors().text);
        ::DrawFocusRect(dc, &f);
    }
    ::SelectObject(dc, old);
    return CDRF_SKIPDEFAULT;
}

void Window::DrawResultIcon(const DRAWITEMSTRUCT* di) {
    ::FillRect(di->hDC, &di->rcItem, theme_.layerBrush());
    if (resultGlyph_.empty()) return;
    HGDIOBJ old = ::SelectObject(di->hDC, theme_.icon);
    ::SetBkMode(di->hDC, TRANSPARENT);
    ::SetTextColor(di->hDC, resultColor_);
    RECT r = di->rcItem;
    ::DrawTextW(di->hDC, resultGlyph_.c_str(), -1, &r, DT_SINGLELINE | DT_LEFT | DT_VCENTER | DT_NOPREFIX);
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
            } else {
                if (result_ == ResultKind::Success && Visible(kRLaunch) && Button_GetCheck(Ctl(kRLaunch)) == BST_CHECKED) c_.LaunchApp();
                done_ = true;
                ::DestroyWindow(hwnd_);
            }
            break;
    }
}

void Window::OnSecondary() {
    if (page_ == Page::Welcome) ShowPage(Page::Options);
    else if (page_ == Page::License) ShowPage(HasOptions() ? Page::Options : Page::Welcome);
    else ShowPage(Page::Welcome);
}

void Window::RequestClose() {
    if (page_ == Page::Progress) {
        if (!cancellable_ || cancelRequested_) return;
        TASKDIALOGCONFIG tdc{sizeof(tdc)};
        tdc.hwndParent = hwnd_;
        tdc.dwFlags = TDF_POSITION_RELATIVE_TO_WINDOW | TDF_ALLOW_DIALOG_CANCELLATION;
        const std::wstring title = S(IsUninstall() ? "cancel.title.uninstall" : "cancel.title");
        const std::wstring body = S("cancel.body");
        const std::wstring yes = S("cancel.confirm"), no = S("cancel.continue");
        std::wstring caption;
        caption.resize(static_cast<size_t>(::GetWindowTextLengthW(hwnd_)) + 1);
        ::GetWindowTextW(hwnd_, caption.data(), static_cast<int>(caption.size()));
        TASKDIALOG_BUTTON buttons[] = {{IDYES, yes.c_str()}, {IDNO, no.c_str()}};
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
    done_ = true;
    ::DestroyWindow(hwnd_);
}

void Window::StartInstall() {
    install::InstallOptions o = c_.DefaultOptions();
    if (!IsUninstall()) {
        Value v;
        v.set("installDir", installDir_);
        v.set("scope", scope_);
        v.set("desktopShortcut", Button_GetCheck(Ctl(kODesktop)) == BST_CHECKED);
        v.set("startMenuShortcut", Button_GetCheck(Ctl(kOStartMenu)) == BST_CHECKED);
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
    SetText(kPHeading, F(IsUninstall() ? "progress.title.uninstall" : "progress.title.install", {AppName()}));
    SetText(kPStage, S("state.Validating"));
    SetText(kPDetail, L"");
    SetText(kPPercent, L"");
    ::SetWindowLongW(Ctl(kPBar), GWL_STYLE, ::GetWindowLongW(Ctl(kPBar), GWL_STYLE) | PBS_MARQUEE);
    ::SendMessageW(Ctl(kPBar), PBM_SETMARQUEE, TRUE, 30);
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
    resultInstallDir_ = m["installDir"].asWide();
    retryable_ = m["retryable"].asBool(true);
    const auto& c = theme_.colors();
    Show(kRLaunch, false);
    Show(kRCode, false);
    Show(kRDetail, false);
    Show(kRFolder, false);
    Show(kRLog, false);
    if (r == "success") {
        result_ = ResultKind::Success;
        resultGlyph_ = L"";
        resultColor_ = c.success;
        SetText(kRHeading, F(uninstall ? "result.success.title.uninstall" : "result.success.title", {name}));
        std::wstring body = uninstall ? std::wstring() : hello_["ui"]["finishText"].asWide();
        if (body.empty()) body = F(uninstall ? "result.success.body.uninstall" : "result.success.body", {name});
        if (uninstall && m["leftovers"].asBool()) body += L"\r\n\r\n" + F("result.leftovers", {resultInstallDir_});
        SetText(kRBody, body);
        ShowPage(Page::Result);
        Show(kRLaunch, !uninstall && m["canLaunch"].asBool());
        Show(kRFolder, !uninstall && !resultInstallDir_.empty());
    } else {
        const bool cancelled = r == "cancelled";
        result_ = cancelled ? ResultKind::Cancelled : ResultKind::Failed;
        resultGlyph_ = cancelled ? L"" : L"";
        resultColor_ = cancelled ? c.caution : c.critical;
        if (cancelled) SetText(kRHeading, S(uninstall ? "result.cancelled.title.uninstall" : "result.cancelled.title"));
        else SetText(kRHeading, S(uninstall ? "result.failed.title.uninstall" : "result.failed.title"));
        std::wstring body = cancelled ? std::wstring() : m["message"].asWide();
        if (m["restored"].asBool()) body += (body.empty() ? L"" : L"\r\n\r\n") + S("result.restored");
        else if (m["noChanges"].asBool()) body += (body.empty() ? L"" : L"\r\n\r\n") + S("result.noChanges");
        SetText(kRBody, body);
        SetText(kRCode, F("result.code", {std::to_wstring(m["code"].asInt()), m["codeName"].asWide()}));
        SetText(kRDetail, m["detail"].asWide());
        ShowPage(Page::Result);
        Show(kRCode, !cancelled);
        Show(kRDetail, !cancelled && !m["detail"].asWide().empty());
        Show(kRLog, !logPath_.empty());
    }
    UpdateFooter();
    Layout();
    ::InvalidateRect(Ctl(kRIcon), nullptr, TRUE);
}

void Window::CheckPathNow() {
    std::wstring text(static_cast<size_t>(::GetWindowTextLengthW(Ctl(kOLoc))) + 1, L'\0');
    ::GetWindowTextW(Ctl(kOLoc), text.data(), static_cast<int>(text.size()));
    text.resize(wcslen(text.c_str()));
    const Value r = c_.CheckPath(text, scope_ == L"machine" ? install::Scope::Machine : install::Scope::User);
    pathDirty_ = false;
    pathOk_ = r["ok"].asBool();
    installDir_ = r["normalized"].asWide().empty() ? text : r["normalized"].asWide();
    requiredBytes_ = r["requiredBytes"].asUint(requiredBytes_);
    freeBytes_ = r["freeBytes"].asUint();
    SetText(kOError, r["message"].asWide());
    SetText(kWError, r["message"].asWide());
    Show(kOError, page_ == Page::Options && !pathOk_);
    UpdateSpace();
    UpdateFooter();
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void Window::OnScopeChanged() {
    const std::wstring scope = Button_GetCheck(Ctl(kOScopeMachine)) == BST_CHECKED ? L"machine" : L"user";
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
    if (SUCCEEDED(dialog->Show(hwnd_)) && SUCCEEDED(dialog->GetResult(&item)) &&
        SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &chosen))) {
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
            HWND ctl = reinterpret_cast<HWND>(l);
            const int id = ::GetDlgCtrlID(ctl);
            const auto& c = theme_.colors();
            HBRUSH brush = theme_.layerBrush();
            COLORREF bg = c.layer, fg = c.text;
            if (id == kHeaderName || id == kHeaderMeta) brush = theme_.baseBrush(), bg = c.base;
            if (id == kWInfo) brush = theme_.infoBrush(), bg = c.infoFill;
            if (id == kOLoc || id == kLText || id == kRDetail) brush = theme_.editBrush(), bg = c.editFill;
            if (id == kHeaderMeta || id == kWSpace || id == kOSpace || id == kPDetail || id == kPPercent || id == kRCode) fg = c.textSecondary;
            if (id == kOError || id == kWError) fg = c.critical;
            ::SetTextColor(dc, fg);
            ::SetBkColor(dc, bg);
            return reinterpret_cast<LRESULT>(brush);
        }
        case WM_NOTIFY: {
            auto* hdr = reinterpret_cast<NMHDR*>(l);
            if (hdr->code == NM_CUSTOMDRAW) return CustomDrawButton(reinterpret_cast<NMCUSTOMDRAW*>(l));
            break;
        }
        case WM_DRAWITEM:
            if (w == kRIcon) {
                DrawResultIcon(reinterpret_cast<DRAWITEMSTRUCT*>(l));
                return TRUE;
            }
            break;
        case DM_GETDEFID:
            return MAKELRESULT(IDOK, DC_HASDEFID);
        case WM_COMMAND: {
            const int id = LOWORD(w), code = HIWORD(w);
            if (id == IDOK && Visible(IDOK) && ::IsWindowEnabled(Ctl(IDOK))) OnPrimary();
            else if (id == IDCANCEL) {
                if (page_ == Page::Result && !Visible(IDCANCEL)) OnPrimary();
                else RequestClose();
            } else if (id == kSecondary) OnSecondary();
            else if (id == kOBrowse) Browse();
            else if ((id == kOScopeUser || id == kOScopeMachine) && code == BN_CLICKED) OnScopeChanged();
            else if (id == kLAccept && code == BN_CLICKED) UpdateFooter();
            else if (id == kOLoc && code == EN_CHANGE && !suppress_) {
                userEditedPath_ = true;
                pathDirty_ = true;
                UpdateFooter();
                ::SetTimer(hwnd_, kPathTimer, 350, nullptr);
            } else if (id == kRLog && !logPath_.empty()) {
                ::ShellExecuteW(hwnd_, L"open", L"notepad.exe", (L"\"" + logPath_ + L"\"").c_str(), nullptr, SW_SHOWNORMAL);
            } else if (id == kRFolder && !resultInstallDir_.empty()) {
                ::ShellExecuteW(hwnd_, L"open", resultInstallDir_.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
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
            RequestClose();
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
            if (l && (wcscmp(reinterpret_cast<const wchar_t*>(l), L"ImmersiveColorSet") == 0)) ApplyTheme();
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

void Run(boot::Controller& controller, bool resume) {
    const HRESULT com = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    {
        Window window(controller, controller.Hello(L""));
        window.Run(resume);
    }
    if (SUCCEEDED(com)) ::CoUninitialize();
}

}  // namespace fsetup::win32ui
