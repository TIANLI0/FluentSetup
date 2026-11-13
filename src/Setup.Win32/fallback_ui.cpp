// Win32 fallback front end, laid out like the WinUI one (Windows App Installer / APK-installer style):
// a question as the title, publisher/version lines, the app icon large at the top right, "Launch when ready" at the
// bottom left and progress inline in the bottom bar. System controls only (Common Controls v6, UxTheme, DWM, WIC for
// the banner). Pages: first page (install / uninstall / "Change <App>"), Settings (package inputs), license, progress,
// result.
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
#include <wincodec.h>
#include <windowsx.h>
#include <wrl/client.h>

#include <algorithm>
#include <memory>
#include <vector>

namespace fsetup::win32ui {

namespace {

using boot::Controller;
using json::Value;

constexpr UINT WM_APP_PROGRESS = WM_APP + 1;
constexpr UINT WM_APP_DONE = WM_APP + 2;
constexpr int kClientWidth = 720;   // DIP (same window size as the WinUI front end)
constexpr int kClientHeight = 508;  // DIP
constexpr int kMargin = 32;
constexpr int kIconSize = 72;
constexpr int kBannerMaxHeight = 96;  // DIP; the window does not scroll, so a tall banner is shown smaller
constexpr int kInputWidth = 440;      // DIP, width of an input field

// WIC identifiers, spelled out so that the stub needs no extra import library.
constexpr CLSID kClsidWicImagingFactory = {0xcacaf262, 0x9370, 0x4615, {0xa1, 0x3b, 0x9f, 0x55, 0x39, 0xda, 0x4c, 0x0a}};
constexpr GUID kWicPixelFormat32bppPBGRA = {0x6fddc324, 0x4e03, 0x4bfe, {0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x10}};

enum Id : int {
    kStatus = 100, kTitle, kSignature, kDescription, kLblPublisher, kPublisher, kLblVersion, kVersion, kLblLocation, kLocation, kChange,
    kLblSize, kSize, kInfo, kDirError, kRepairNote,
    kResultBody, kResultCode, kResultDetail, kOpenFolder, kViewLog,
    kOScopeLabel = 300, kOScopeUser, kOScopeMachine, kODesktop, kOStartMenu, kOVariantLabel, kOVariantDesc, kRemoveData, kUserDataPaths,
    kLHeading = 400, kLSub, kLText, kLAccept,
    kLaunch = 500, kPStage, kPPercent, kPBar, kPDetail,
    kIHeading = 600, kISub, kIPanel,
    kSecondary = 700,
    kChoiceBase = 900,   // package-defined option checkboxes: kChoiceBase + index
    kVariantBase = 950,  // package variant radio buttons: kVariantBase + index
    kInputBase = 1000,   // package inputs, children of the Settings panel: kInputBase + 4 * index + InputPart
};
enum InputPart : int { kInputLabel, kInputControl, kInputDescription, kInputError };

enum class Page { Welcome, Inputs, License, Progress, Result };
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
    static LRESULT CALLBACK PanelProc(HWND h, UINT m, WPARAM w, LPARAM l);
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
    int TextHeight(int id, int width) const { return TextHeightOf(Ctl(id), width); }
    int TextWidth(int id) const;
    static int TextHeightOf(HWND h, int width);

    void Create();
    void ApplyFonts();
    void ApplyTheme();
    void ApplyHello();
    void UpdateInfoLines();
    void UpdateNotice();
    void LoadBanner();
    void RenderBanner();
    int BannerSpace() const { return banner_ && page_ == Page::Welcome ? bannerH_ + Px(16) : 0; }
    void CreateInputs();
    void LayoutPanel();
    void ScrollPanelTo(int pos);
    void OnPanelScroll(int code);
    void EnsureInputVisible(size_t index);
    void OnInputChanged(size_t index, bool touched);
    std::wstring InputValue(size_t index) const;
    HWND InputCtl(size_t index, InputPart part) const { return ::GetDlgItem(panel_, kInputBase + static_cast<int>(index) * 4 + part); }
    const std::vector<Value>& Inputs() const { return hello_["inputs"].array(); }
    bool HasInputsPage() const { return hello_["inputsPage"].asBool() && !Inputs().empty(); }
    bool InputsValid() const { return std::all_of(inputOk_.begin(), inputOk_.end(), [](bool ok) { return ok; }); }
    void OnVariantChanged(size_t index);
    size_t VariantCount() const { return hello_["variants"].array().size(); }
    void ShowPage(Page p);
    void Layout();
    void UpdateFooter();
    void Paint(HDC dc);
    LRESULT CustomDrawButton(NMCUSTOMDRAW* cd);
    void DrawStatus(const DRAWITEMSTRUCT* di);

    bool HasOptions() const;
    bool ShowsLicense() const { return IsInstall() && !hello_["ui"]["licenseText"].asString().empty(); }
    bool LaunchOffered() const { return IsInstall() && hello_["options"]["launchAfter"]["visible"].asBool(); }
    bool NeedsElevation() const { return scope_ == L"machine" && !hello_["elevated"].asBool(); }
    bool IsUninstall() const { return hello_["mode"].asString() == "uninstall"; }
    bool IsModify() const { return hello_["mode"].asString() == "modify"; }
    bool IsInstall() const { return !IsUninstall() && !IsModify(); }
    bool CanRepair() const { return IsModify() && hello_["modify"]["canRepair"].asBool(); }
    std::wstring AppName() const { return hello_["app"]["name"].asWide(); }
    std::wstring PrimaryVerb() const;

    void OnPrimary();
    void OnSecondary();
    void RequestClose();
    void Finish(bool launch);
    void StartInstall();
    void StartRepair();
    void BeginProgress();
    void OnProgressMsg(const install::Progress& p);
    void OnDoneMsg(const Value& m);
    void CheckPath(const std::wstring& path);
    void OnScopeChanged();
    void Browse();

    Controller& c_;
    Value hello_;
    Theme theme_;
    HWND hwnd_ = nullptr;
    UINT dpi_ = 96;
    HICON iconLarge_ = nullptr;
    HICON iconSmall_ = nullptr;
    Page page_ = Page::Welcome;
    ResultKind result_ = ResultKind::None;
    std::wstring scope_, installDir_, logPath_, baseInfo_;
    std::string variant_;
    uint64_t requiredBytes_ = 0, freeBytes_ = 0;
    bool pathOk_ = true, userPickedPath_ = false;
    bool cancellable_ = true, cancelRequested_ = false, retryable_ = true, canLaunch_ = false;
    std::wstring statusGlyph_;
    COLORREF statusColor_ = 0;
    RECT infoRect_{};
    bool signatureVerified_ = false;
    bool applyingHello_ = false;  // values set by the program are not the user's edits
    bool repairing_ = false;  // the progress/result pages are about a repair (modify mode)
    // Settings page: a scrollable panel holding one label/control/description/error group per input.
    HWND panel_ = nullptr;
    int panelScroll_ = 0, panelContent_ = 0;
    std::vector<bool> inputOk_, inputTouched_;
    // Banner (ui.banner): decoded once (premultiplied BGRA), scaled and put on the page colour per DPI and theme.
    std::vector<uint8_t> bannerPixels_;
    UINT bannerSrcW_ = 0, bannerSrcH_ = 0;
    HBITMAP banner_ = nullptr;
    int bannerW_ = 0, bannerH_ = 0;
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

int Window::TextHeightOf(HWND h, int width) {
    std::wstring text(static_cast<size_t>(::GetWindowTextLengthW(h)) + 1, L'\0');
    ::GetWindowTextW(h, text.data(), static_cast<int>(text.size()));
    text.resize(wcslen(text.c_str()));
    if (text.empty()) text = L" ";
    HDC dc = ::GetDC(h);
    HGDIOBJ old = ::SelectObject(dc, reinterpret_cast<HFONT>(::SendMessageW(h, WM_GETFONT, 0, 0)));
    RECT r{0, 0, width, 0};
    ::DrawTextW(dc, text.c_str(), -1, &r, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX | DT_EDITCONTROL);
    ::SelectObject(dc, old);
    ::ReleaseDC(h, dc);
    return r.bottom - r.top;
}

int Window::TextWidth(int id) const {
    HWND h = Ctl(id);
    const std::wstring text = GetText(id);
    HDC dc = ::GetDC(h);
    HGDIOBJ old = ::SelectObject(dc, reinterpret_cast<HFONT>(::SendMessageW(h, WM_GETFONT, 0, 0)));
    RECT r{};
    ::DrawTextW(dc, text.c_str(), -1, &r, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
    ::SelectObject(dc, old);
    ::ReleaseDC(h, dc);
    return r.right - r.left;
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
    WNDCLASSEXW pc{sizeof(pc)};
    pc.lpfnWndProc = PanelProc;
    pc.hInstance = wc.hInstance;
    pc.hCursor = wc.hCursor;
    pc.lpszClassName = L"FluentSetupFallbackPanel";
    ::RegisterClassExW(&pc);

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
    if (title.empty()) title = F(IsUninstall() ? "window.title.uninstall" : IsModify() ? "modify.title" : "window.title.install", {AppName()});
    hwnd_ = ::CreateWindowExW(WS_EX_CONTROLPARENT, wc.lpszClassName, title.c_str(), style,
                              mi.rcWork.left + (mi.rcWork.right - mi.rcWork.left - w) / 2,
                              mi.rcWork.top + (mi.rcWork.bottom - mi.rcWork.top - h) / 2, w, h, nullptr, nullptr, wc.hInstance, this);
    if (!hwnd_) return;
    dpi_ = ::GetDpiForWindow(hwnd_);

    LoadBanner();
    Create();
    ApplyHello();
    ApplyTheme();

    // Resume: show whatever the session is doing right now (e.g. after the WinUI front end stopped).
    install::Progress last;
    std::optional<install::Outcome> outcome;
    bool started = false;
    c_.Attach(this, &last, &outcome, &started);
    repairing_ = c_.operation() == boot::Operation::Repair;
    if (outcome) {
        OnDoneMsg(c_.DoneMessage(*outcome));
    } else if (started) {
        ShowPage(Page::Progress);
        OnProgressMsg(last);
    } else if (hello_["modify"]["startRepair"].asBool()) {
        StartRepair();  // uninstall.exe /repair
    } else if (hello_["passive"].asBool()) {
        StartInstall();  // passive mode (/passive, Inno /SILENT): no questions
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
    if (banner_) ::DeleteObject(banner_);
}

void Window::Create() {
    const DWORD text = SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL;
    // Creation order = tab order.
    Make(WC_STATICW, kStatus, SS_OWNERDRAW);
    Make(WC_STATICW, kTitle, text);
    Make(WC_STATICW, kSignature, text);
    Make(WC_STATICW, kDescription, text);
    for (int id : {kLblPublisher, kPublisher, kLblVersion, kVersion, kLblLocation}) Make(WC_STATICW, id, text);
    Make(WC_STATICW, kLocation, text | SS_PATHELLIPSIS);
    Make(WC_BUTTONW, kChange, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    for (int id : {kLblSize, kSize, kInfo, kDirError, kRepairNote, kResultBody, kResultCode}) Make(WC_STATICW, id, text);
    Make(WC_EDITW, kResultDetail, WS_TABSTOP | WS_GROUP | ES_MULTILINE | ES_READONLY | WS_VSCROLL, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kOpenFolder, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kViewLog, BS_PUSHBUTTON | WS_TABSTOP);

    // Install options sit inline on the first page; "Change" next to the location only picks a folder.
    Make(WC_STATICW, kOScopeLabel, text);
    Make(WC_BUTTONW, kOScopeUser, BS_AUTORADIOBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kOScopeMachine, BS_AUTORADIOBUTTON);
    Make(WC_BUTTONW, kRemoveData, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);
    Make(WC_STATICW, kUserDataPaths, text);
    Make(WC_STATICW, kOVariantLabel, text);
    for (size_t i = 0; i < VariantCount(); ++i) {
        Make(WC_BUTTONW, kVariantBase + static_cast<int>(i), BS_AUTORADIOBUTTON | WS_TABSTOP | (i == 0 ? WS_GROUP : 0));
    }
    Make(WC_STATICW, kOVariantDesc, text);
    Make(WC_BUTTONW, kODesktop, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, kOStartMenu, BS_AUTOCHECKBOX | WS_TABSTOP);
    for (size_t i = 0; i < hello_["options"]["choices"].array().size(); ++i) {
        Make(WC_BUTTONW, kChoiceBase + static_cast<int>(i), BS_AUTOCHECKBOX | WS_TABSTOP | (i == 0 ? WS_GROUP : 0));
    }

    Make(WC_STATICW, kLHeading, text);
    Make(WC_STATICW, kLSub, text);
    Make(WC_EDITW, kLText, WS_TABSTOP | WS_GROUP | ES_MULTILINE | ES_READONLY | WS_VSCROLL, WS_EX_CLIENTEDGE);
    Make(WC_BUTTONW, kLAccept, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);

    Make(WC_STATICW, kIHeading, text);
    Make(WC_STATICW, kISub, text);
    CreateInputs();

    Make(WC_BUTTONW, kLaunch, BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP);
    Make(WC_STATICW, kPStage, text | SS_ENDELLIPSIS);
    Make(WC_STATICW, kPPercent, text | SS_RIGHT);
    Make(PROGRESS_CLASSW, kPBar, PBS_SMOOTH);
    Make(WC_STATICW, kPDetail, text | SS_PATHELLIPSIS);

    Make(WC_BUTTONW, kSecondary, BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP);
    Make(WC_BUTTONW, IDOK, BS_DEFPUSHBUTTON | WS_TABSTOP);
    Make(WC_BUTTONW, IDCANCEL, BS_PUSHBUTTON | WS_TABSTOP);

    ::SendMessageW(Ctl(kPBar), PBM_SETRANGE32, 0, 1000);
    SetText(kOScopeLabel, S("options.scope"));
    SetText(kOScopeUser, S("options.scope.userShort"));
    SetText(kOScopeMachine, S("options.scope.machineShort"));
    SetText(kOVariantLabel, S("options.variant"));
    SetText(kRemoveData, S("fallback.removeData"));
    for (size_t i = 0; i < VariantCount(); ++i) {
        const Value& v = hello_["variants"].array()[i];
        SetText(kVariantBase + static_cast<int>(i), v["label"].asWide() + L" (" + FormatBytes(v["requiredBytes"].asUint()) + L")");
    }
    SetText(kODesktop, S("fallback.desktop"));
    SetText(kOStartMenu, S("fallback.startMenu"));
    SetText(kLHeading, S("license.title"));
    SetText(kLSub, S("license.subtitle"));
    SetText(kLAccept, S("fallback.accept"));
    SetText(kIHeading, S("inputs.title"));
    SetText(kISub, F(IsModify() ? "inputs.subtitle.modify" : "inputs.subtitle", {AppName()}));
    SetText(kRepairNote, S("modify.repairBody"));
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
    set(kLHeading, theme_.subtitle);
    set(kIHeading, theme_.subtitle);
    set(kISub, theme_.body);
    set(kSignature, theme_.body);
    set(kRepairNote, theme_.caption);
    for (size_t i = 0; i < Inputs().size(); ++i) {
        for (const InputPart part : {kInputLabel, kInputControl}) ::SendMessageW(InputCtl(i, part), WM_SETFONT, reinterpret_cast<WPARAM>(theme_.body), FALSE);
        for (const InputPart part : {kInputDescription, kInputError}) ::SendMessageW(InputCtl(i, part), WM_SETFONT, reinterpret_cast<WPARAM>(theme_.caption), FALSE);
    }
    for (size_t i = 0; i < hello_["options"]["choices"].array().size(); ++i) set(kChoiceBase + static_cast<int>(i), theme_.body);
    for (int id : std::initializer_list<int>{kPDetail, kResultCode, kOVariantDesc}) set(id, theme_.caption);
    set(kOVariantLabel, theme_.body);
    set(kRemoveData, theme_.body);
    set(kUserDataPaths, theme_.caption);
    for (size_t i = 0; i < VariantCount(); ++i) set(kVariantBase + static_cast<int>(i), theme_.body);
    for (int id : std::initializer_list<int>{kLblPublisher, kPublisher, kLblVersion, kVersion, kLblLocation, kLocation, kChange,
                                             kLblSize, kSize, kDescription, kInfo, kDirError,
                                             kResultBody, kResultDetail, kOpenFolder, kViewLog, kOScopeLabel, kOScopeUser,
                                             kOScopeMachine, kODesktop, kOStartMenu, kLSub, kLText, kLAccept, kLaunch, kPStage,
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
    for (HWND parent : {hwnd_, panel_}) {
        for (HWND child = parent ? ::GetWindow(parent, GW_CHILD) : nullptr; child; child = ::GetWindow(child, GW_HWNDNEXT)) {
            wchar_t cls[32] = {};
            ::GetClassNameW(child, cls, 32);
            if (_wcsicmp(cls, WC_BUTTONW) == 0) theme_.ApplyControlTheme(child, false);
            else if (_wcsicmp(cls, WC_EDITW) == 0 || _wcsicmp(cls, WC_COMBOBOXW) == 0) theme_.ApplyControlTheme(child, true);
        }
    }
    if (panel_) theme_.ApplyControlTheme(panel_, false);  // its scroll bar
    RenderBanner();
    Layout();
    ::InvalidateRect(hwnd_, nullptr, TRUE);
}

void Window::ApplyHello() {
    const Value& opts = hello_["options"];
    const Value& existing = hello_["existing"];
    const std::wstring name = AppName();
    std::wstring description = hello_["ui"]["welcomeText"].asWide();
    if (IsUninstall()) description = F("uninstall.body", {name});
    if (IsModify()) description = F("modify.subtitle", {name, hello_["app"]["version"].asWide()});
    SetText(kDescription, description);
    const Value& signature = hello_["signature"];
    signatureVerified_ = signature["state"].asString() == "valid" && !signature["publisher"].asWide().empty();
    SetText(kSignature, signatureVerified_ ? F("welcome.verifiedPublisher", {signature["publisher"].asWide()}) : S("welcome.unknownPublisher"));

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
    Button_SetCheck(Ctl(scope_ == L"machine" ? kOScopeMachine : kOScopeUser), BST_CHECKED);
    Button_SetCheck(Ctl(kODesktop), opts["desktopShortcut"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    Button_SetCheck(Ctl(kOStartMenu), opts["startMenuShortcut"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    Button_SetCheck(Ctl(kLaunch), opts["launchAfter"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    const auto& choices = opts["choices"].array();
    for (size_t i = 0; i < choices.size(); ++i) {
        SetText(kChoiceBase + static_cast<int>(i), choices[i]["label"].asWide());
        Button_SetCheck(Ctl(kChoiceBase + static_cast<int>(i)), choices[i]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
        ::EnableWindow(Ctl(kChoiceBase + static_cast<int>(i)), !choices[i]["required"].asBool());  // required: locked on
    }
    baseInfo_ = info;
    Button_SetCheck(Ctl(kRemoveData), hello_["userData"]["value"].asBool() ? BST_CHECKED : BST_UNCHECKED);
    {
        std::wstring paths = hello_["userData"]["paths"].asWide();
        for (size_t p = 0; (p = paths.find(L'\n', p)) != std::wstring::npos; p += 2) paths.replace(p, 1, L"\r\n");
        SetText(kUserDataPaths, paths);
    }
    variant_ = hello_["variant"].asString();
    for (size_t i = 0; i < VariantCount(); ++i) {
        const bool on = hello_["variants"].array()[i]["id"].asString() == variant_;
        Button_SetCheck(Ctl(kVariantBase + static_cast<int>(i)), on ? BST_CHECKED : BST_UNCHECKED);
        if (on) requiredBytes_ = hello_["variants"].array()[i]["requiredBytes"].asUint(requiredBytes_);
    }
    UpdateNotice();
    applyingHello_ = true;
    for (size_t i = 0; i < Inputs().size(); ++i) {
        const Value& input = Inputs()[i];
        const std::wstring value = input["value"].asWide();
        if (input["type"].asString() == "choice") {
            const auto& list = input["choices"].array();
            int selected = -1;
            for (size_t k = 0; k < list.size(); ++k) {
                if (list[k]["value"].asWide() == value) selected = static_cast<int>(k);
            }
            ::SendMessageW(InputCtl(i, kInputControl), CB_SETCURSEL, static_cast<WPARAM>(selected), 0);
        } else {
            ::SetWindowTextW(InputCtl(i, kInputControl), value.c_str());
        }
        inputOk_[i] = input["ok"].asBool(true);
    }
    applyingHello_ = false;
    std::wstring license = hello_["ui"]["licenseText"].asWide(), crlf;
    for (size_t i = 0; i < license.size(); ++i) {
        if (license[i] == L'\n' && (i == 0 || license[i - 1] != L'\r')) crlf.push_back(L'\r');
        crlf.push_back(license[i]);
    }
    SetText(kLText, crlf);
    UpdateInfoLines();
}

// Running copies of the app and missing prerequisites of the chosen variant, in the info box on the first page.
void Window::UpdateNotice() {
    std::wstring notice = baseInfo_;
    const auto add = [&](const std::wstring& line) {
        if (!line.empty()) notice += (notice.empty() ? L"" : L"\r\n") + line;
    };
    if (!hello_["runningApps"].asWide().empty()) add(F("welcome.running", {hello_["runningApps"].asWide()}));
    if (!hello_["lockingApps"].asWide().empty()) add(F("welcome.lockingApps", {AppName(), hello_["lockingApps"].asWide()}));
    if (!IsInstall()) {
        SetText(kInfo, notice);
        return;
    }
    std::wstring prereq = hello_["prerequisites"].asWide(), description;
    for (const auto& v : hello_["variants"].array()) {
        if (v["id"].asString() == variant_) prereq = v["prerequisites"].asWide(), description = v["description"].asWide();
    }
    for (size_t p = 0; (p = prereq.find(L'\n', p)) != std::wstring::npos; p += 2) prereq.replace(p, 1, L"\r\n");
    add(prereq);
    SetText(kInfo, notice);
    SetText(kOVariantDesc, description);
}

void Window::OnVariantChanged(size_t index) {
    const auto& variants = hello_["variants"].array();
    if (index >= variants.size() || variants[index]["id"].asString() == variant_) return;
    variant_ = variants[index]["id"].asString();
    requiredBytes_ = variants[index]["requiredBytes"].asUint(requiredBytes_);
    UpdateNotice();
    UpdateInfoLines();
    ShowPage(page_);  // the info box may appear or disappear
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
    SetText(kVersion, versionText != version ? versionText : std::wstring());  // the title already shows the plain version
    SetText(kLocation, installDir_);
    SetText(kSize, !IsInstall() || requiredBytes_ == 0 || page_ == Page::Result ? std::wstring() : FormatBytes(requiredBytes_));
}

bool Window::HasOptions() const {
    if (IsUninstall()) return false;
    const Value& o = hello_["options"];
    return VariantCount() > 1 || o["canChangeScope"].asBool() || o["desktopShortcut"]["visible"].asBool() ||
           o["startMenuShortcut"]["visible"].asBool() || !o["choices"].array().empty();
}

std::wstring Window::PrimaryVerb() const {
    if (IsUninstall()) return S("button.uninstall");
    if (IsModify()) return S("modify.change");
    const std::string relation = hello_["existing"]["relation"].asString();
    if (relation == "upgrade") return S("button.update");
    if (relation == "same") return S("button.reinstall");
    return S("button.install");
}

void Window::ShowPage(Page p) {
    page_ = p;
    const Value& o = hello_["options"];
    const bool summary = p == Page::Welcome || p == Page::Progress || p == Page::Result;
    const bool welcome = p == Page::Welcome, license = p == Page::License, result = p == Page::Result, inputs = p == Page::Inputs;

    if (welcome) {
        const std::string relation = hello_["existing"]["relation"].asString();
        const char* key = IsUninstall() ? "ask.uninstall" : relation == "upgrade" ? "ask.update" : relation == "same" ? "ask.reinstall" : "ask.install";
        SetText(kTitle, IsModify() ? F("modify.title", {AppName()}) : F(key, {AppName(), hello_["app"]["version"].asWide()}));
    } else if (p == Page::Progress) {
        if (IsModify()) SetText(kTitle, F(repairing_ ? "modify.repairing" : "progress.title.modify", {AppName()}));
        else SetText(kTitle, F(IsUninstall() ? "progress.uninstalling" : "progress.installing", {AppName(), hello_["app"]["version"].asWide()}));
    }
    UpdateInfoLines();
    Show(kTitle, summary);
    Show(kSignature, welcome);
    Show(kStatus, result);
    for (const auto [label, value] : {std::pair{kLblPublisher, kPublisher}, std::pair{kLblVersion, kVersion},
                                      std::pair{kLblLocation, kLocation}, std::pair{kLblSize, kSize}}) {
        const bool has = summary && ::GetWindowTextLengthW(Ctl(value)) > 0;
        Show(label, has);
        Show(value, has);
    }
    const bool editable = welcome && !IsUninstall() && !hello_["existing"]["blocked"].asBool();
    Show(kChange, editable && o["canChangeDir"].asBool());
    Show(kDescription, welcome && ::GetWindowTextLengthW(Ctl(kDescription)) > 0);
    Show(kInfo, welcome && ::GetWindowTextLengthW(Ctl(kInfo)) > 0);
    Show(kDirError, welcome && !pathOk_);
    Show(kRepairNote, welcome && CanRepair());
    if (!result) {
        for (int id : {kResultBody, kResultCode, kResultDetail, kOpenFolder, kViewLog}) Show(id, false);
    }

    for (int id : {kOScopeLabel, kOScopeUser, kOScopeMachine}) Show(id, editable && o["canChangeScope"].asBool());
    const bool userData = welcome && IsUninstall() && hello_["userData"]["visible"].asBool();
    Show(kRemoveData, userData);
    Show(kUserDataPaths, userData);
    const bool variants = editable && VariantCount() > 1;
    Show(kOVariantLabel, variants);
    for (size_t i = 0; i < VariantCount(); ++i) Show(kVariantBase + static_cast<int>(i), variants);
    Show(kOVariantDesc, variants && ::GetWindowTextLengthW(Ctl(kOVariantDesc)) > 0);
    Show(kODesktop, editable && o["desktopShortcut"]["visible"].asBool());
    Show(kOStartMenu, editable && o["startMenuShortcut"]["visible"].asBool());
    for (size_t i = 0; i < o["choices"].array().size(); ++i) Show(kChoiceBase + static_cast<int>(i), editable);
    for (int id : {kLHeading, kLSub, kLText, kLAccept}) Show(id, license);
    for (int id : {kIHeading, kISub, kIPanel}) Show(id, inputs);

    UpdateFooter();
    ::InvalidateRect(hwnd_, nullptr, TRUE);
    if (inputs && !Inputs().empty()) ::SetFocus(InputCtl(0, kInputControl));
    else ::SetFocus(Visible(IDOK) && ::IsWindowEnabled(Ctl(IDOK)) ? Ctl(IDOK) : Ctl(IDCANCEL));
}

void Window::UpdateFooter() {
    const bool blocked = hello_["existing"]["blocked"].asBool();
    const bool beforeStart = page_ == Page::Welcome || page_ == Page::Inputs || page_ == Page::License;
    bool secondary = false, primary = true, primaryEnabled = true, shield = false;
    std::wstring secondaryText, primaryText, cancelText = S("button.cancel");
    switch (page_) {
        case Page::Welcome: {
            // "Change <App>": Repair on the left of the primary button; Change applies the toggles (after Settings).
            const bool more = HasInputsPage() || ShowsLicense();
            secondary = CanRepair();
            secondaryText = S("modify.repair");
            primaryText = more ? S("button.next") : PrimaryVerb();
            shield = !more && NeedsElevation();
            primaryEnabled = !blocked && pathOk_;
            break;
        }
        case Page::Inputs:
            secondary = true;
            secondaryText = S("button.back");
            primaryText = ShowsLicense() ? S("button.next") : PrimaryVerb();
            shield = !ShowsLicense() && NeedsElevation();
            primaryEnabled = InputsValid() && pathOk_;
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
            y += BannerSpace();
            int titleLeft = left;
            if (Visible(kStatus)) {
                place(kStatus, left, y + Px(6), Px(32), Px(32));
                titleLeft = left + Px(44);
            }
            stack(kTitle, Px(4), textRight - titleLeft, -1, titleLeft);
            stack(kSignature, Px(8), textWidth);
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
                int valueW = textRight - valueX;
                if (value == kLocation && Visible(kChange)) {
                    // "Change" stays on the path's line: the path gives way (SS_PATHELLIPSIS: C:\Users\...\App).
                    SIZE ideal{};
                    Button_GetIdealSize(Ctl(kChange), &ideal);
                    const int bw = std::max(Px(64), static_cast<int>(ideal.cx) + Px(12)), bh = Px(26);
                    const int avail = right - valueX - bw - Px(8);
                    valueW = std::min(avail, TextWidth(kLocation) + Px(2));
                    place(kChange, valueX + valueW + Px(8), y + (h - bh) / 2, bw, bh);
                }
                place(value, valueX, y, valueW, h);
                y += h + Px(8);
            }
            // Install options, inline (no separate page).
            if (Visible(kOScopeUser)) {
                y += Px(6);
                const int lw = TextWidth(kOScopeLabel), rowH = Px(24);
                place(kOScopeLabel, left, y + (rowH - TextHeight(kOScopeLabel, lw + Px(4))) / 2, lw + Px(4), TextHeight(kOScopeLabel, lw + Px(4)));
                int rx = left + std::max(lw + Px(16), valueX - left);
                for (int id : {static_cast<int>(kOScopeUser), static_cast<int>(kOScopeMachine)}) {
                    const int w = TextWidth(id) + Px(32);
                    place(id, rx, y, w, rowH);
                    rx += w + Px(16);
                }
                y += rowH + Px(4);
            }
            if (Visible(kOVariantLabel)) {
                y += Px(6);
                const int lw = TextWidth(kOVariantLabel), rowH = Px(24);
                place(kOVariantLabel, left, y + (rowH - TextHeight(kOVariantLabel, lw + Px(4))) / 2, lw + Px(4), TextHeight(kOVariantLabel, lw + Px(4)));
                int rx = left + std::max(lw + Px(16), valueX - left);
                for (size_t i = 0; i < VariantCount(); ++i) {
                    const int id = kVariantBase + static_cast<int>(i);
                    const int w = std::min(right - rx, TextWidth(id) + Px(32));
                    if (rx + w > right && rx > left + Px(100)) {  // wrap long labels onto the next row
                        y += rowH + Px(2);
                        rx = left + std::max(lw + Px(16), valueX - left);
                    }
                    place(id, rx, y, w, rowH);
                    rx += w + Px(16);
                }
                y += rowH + Px(2);
                if (Visible(kOVariantDesc)) {
                    const int dx = left + std::max(lw + Px(16), valueX - left), dw = right - dx;
                    const int dh = TextHeight(kOVariantDesc, dw);
                    place(kOVariantDesc, dx, y, dw, dh);
                    y += dh + Px(4);
                }
            }
            if (Visible(kODesktop) || Visible(kOStartMenu) || Visible(kChoiceBase)) y += Px(6);
            const auto checkRow = [&](int id) {
                if (!Visible(id)) return;
                place(id, left, y, std::min(right - left, TextWidth(id) + Px(32)), Px(24));
                y += Px(24) + Px(2);
            };
            checkRow(kODesktop);
            checkRow(kOStartMenu);
            for (size_t i = 0; i < hello_["options"]["choices"].array().size(); ++i) checkRow(kChoiceBase + static_cast<int>(i));
            y += Px(8);
            if (Visible(kInfo)) {
                const int pad = Px(14);
                const int width = right - left;
                const int h = TextHeight(kInfo, width - 2 * pad);
                infoRect_ = {left, y, right, y + h + 2 * pad};
                place(kInfo, left + pad, y + pad, width - 2 * pad, h);
                y += h + 2 * pad + Px(12);
            }
            if (Visible(kRemoveData)) {
                y += Px(4);
                place(kRemoveData, left, y, std::min(right - left, TextWidth(kRemoveData) + Px(32)), Px(24));
                y += Px(26);
                const int ph = TextHeight(kUserDataPaths, right - left - Px(26));
                place(kUserDataPaths, left + Px(26), y, right - left - Px(26), ph);
                y += ph + Px(8);
            }
            stack(kDirError, Px(12), right - left);
            stack(kRepairNote, Px(8), right - left);
            stack(kResultBody, Px(8), right - left);
            stack(kResultCode, Px(8), right - left);
            stack(kResultDetail, Px(8), right - left, Px(56));

            break;
        }
        case Page::License: {
            const int width = right - left;
            stack(kLHeading, Px(8), width);
            stack(kLSub, Px(12), width);
            // The license takes all the height left above the accept checkbox and the bottom bar.
            const int footerTop = client.bottom - Px(24) - Px(32) - Px(16);
            stack(kLText, Px(12), width, std::max(Px(120), footerTop - y - Px(12) - Px(22)));
            stack(kLAccept, 0, width, Px(22));
            break;
        }
        case Page::Inputs: {
            const int width = right - left;
            stack(kIHeading, Px(8), width);
            stack(kISub, Px(12), width);
            // The fields scroll inside the panel; it ends above the bottom bar. A few pixels of room on the left keep
            // the edit borders and focus rectangles visible.
            const int footerTop = client.bottom - Px(24) - Px(32) - Px(16);
            place(kIPanel, left - Px(4), y, right - left + Px(4) + Px(kMargin) / 2, std::max(Px(80), footerTop - y));
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
    if (page_ == Page::Inputs) LayoutPanel();
}

void Window::Paint(HDC dc) {
    RECT client{};
    ::GetClientRect(hwnd_, &client);
    ::FillRect(dc, &client, theme_.baseBrush());
    if (banner_ && page_ == Page::Welcome) {
        // Decorative: already composed onto the page colour (RenderBanner), so a plain copy is enough.
        HDC mem = ::CreateCompatibleDC(dc);
        HGDIOBJ old = ::SelectObject(mem, banner_);
        ::BitBlt(dc, Px(kMargin), Px(16), bannerW_, bannerH_, mem, 0, 0, SRCCOPY);
        ::SelectObject(mem, old);
        ::DeleteDC(mem);
    }
    if (iconLarge_ && page_ != Page::License && page_ != Page::Inputs) {
        ::DrawIconEx(dc, client.right - Px(kMargin) - Px(kIconSize), Px(20) + BannerSpace(), iconLarge_, Px(kIconSize), Px(kIconSize), 0, nullptr,
                     DI_NORMAL);
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
            if (HasInputsPage()) ShowPage(Page::Inputs);
            else if (ShowsLicense()) ShowPage(Page::License);
            else StartInstall();
            break;
        case Page::Inputs:
            if (!InputsValid()) break;
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
    switch (page_) {
        case Page::Welcome:
            if (CanRepair()) StartRepair();
            break;
        case Page::Inputs: ShowPage(Page::Welcome); break;
        case Page::License: ShowPage(HasInputsPage() ? Page::Inputs : Page::Welcome); break;
        default: break;
    }
}

void Window::Finish(bool launch) {
    if (launch && canLaunch_) c_.LaunchApp();
    ::DestroyWindow(hwnd_);
}

void Window::RequestClose() {
    if (page_ == Page::Progress) {
        if (!cancellable_ || cancelRequested_) return;
        const std::wstring title = S(IsUninstall() ? "cancel.title.uninstall" : IsModify() ? "cancel.title.modify" : "cancel.title");
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
    if (IsUninstall()) o.removeUserData = Checked(kRemoveData);
    if (!IsUninstall()) {
        Value v;
        v.set("installDir", installDir_);
        v.set("scope", scope_);
        v.set("desktopShortcut", Checked(kODesktop));
        v.set("startMenuShortcut", Checked(kOStartMenu));
        Value choices = Value::MakeObject();
        const auto& defined = hello_["options"]["choices"].array();
        for (size_t i = 0; i < defined.size(); ++i) choices.set(defined[i]["id"].asString(), Checked(kChoiceBase + static_cast<int>(i)));
        v.set("choices", choices);
        v.set("variant", variant_);
        Value inputs = Value::MakeObject();
        for (size_t i = 0; i < Inputs().size(); ++i) inputs.set(Inputs()[i]["id"].asString(), InputValue(i));
        v.set("inputs", inputs);
        if (!c_.SanitizeOptions(v, o)) {
            pathOk_ = false;
            SetText(kDirError, S("error.InvalidArguments"));
            ShowPage(Page::Welcome);
            return;
        }
    }
    repairing_ = false;
    BeginProgress();
    c_.Start(o, hwnd_);
}

void Window::StartRepair() {
    repairing_ = true;
    BeginProgress();
    c_.Repair(hwnd_);
}

void Window::BeginProgress() {
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
    canLaunch_ = IsInstall() && r == "success" && m["canLaunch"].asBool();
    const bool modify = IsModify(), repair = m["operation"].asString() == "repair";
    const std::string repairResult = m["repair"].asString();
    if (r == "success" && hello_["passive"].asBool()) {  // passive mode closes itself after a success
        Finish(false);
        return;
    }
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
        SetText(kTitle, F(uninstall ? "done.uninstalled" : updated ? "done.updated" : "done.installed", {name, hello_["app"]["version"].asWide()}));
        body = uninstall ? std::wstring() : hello_["ui"]["finishText"].asWide();
        if (uninstall && m["leftovers"].asBool()) body = F("result.leftovers", {installDir_});
        if (repair && repairResult == "noProblems") {
            SetText(kTitle, S("result.noProblems.title"));
            body = F("modify.noProblems", {name});
        } else if (repair) {
            SetText(kTitle, F("result.success.title.repair", {name}));
            body = F("result.success.body.repair", {name});
        } else if (modify) {
            SetText(kTitle, F("result.success.title.modify", {name}));
            body = F("result.success.body.modify", {name});
        }
        if (m["rebootRequired"].asBool()) body += (body.empty() ? L"" : L"\r\n\r\n") + S("result.rebootRequired");
    } else {
        const bool cancelled = r == "cancelled";
        result_ = cancelled ? ResultKind::Cancelled : ResultKind::Failed;
        statusGlyph_ = cancelled ? L"" : L"";
        statusColor_ = cancelled ? c.caution : c.critical;
        SetText(kTitle, S(cancelled ? (uninstall ? "result.cancelled.title.uninstall" : modify ? "result.cancelled.title.modify" : "result.cancelled.title")
                                    : (uninstall ? "result.failed.title.uninstall"
                                       : repair  ? "result.failed.title.repair"
                                       : modify  ? "result.failed.title.modify"
                                                 : "result.failed.title")));
        body = cancelled ? std::wstring() : m["message"].asWide();
        const std::wstring extra = m["restored"].asBool() ? S("result.restored") : m["noChanges"].asBool() || cancelled ? S("result.noChanges") : std::wstring();
        if (!extra.empty()) body += (body.empty() ? L"" : L"\r\n") + extra;
        SetText(kResultCode, F("result.code", {std::to_wstring(m["code"].asInt()), m["codeName"].asWide()}));
        SetText(kResultDetail, m["detail"].asWide());
    }
    std::wstring warnings;
    for (const auto& w : m["warnings"].array()) warnings += L"\r\n  " + w.asWide();
    if (!warnings.empty()) body += (body.empty() ? L"" : L"\r\n\r\n") + S("result.warnings") + warnings;
    SetText(kResultBody, body);
    ShowPage(Page::Result);
    const bool failed = result_ == ResultKind::Failed;
    Show(kResultBody, !body.empty());
    Show(kResultCode, failed);
    Show(kResultDetail, failed && !m["detail"].asWide().empty());
    Show(kOpenFolder, result_ == ResultKind::Success && IsInstall() && !installDir_.empty());
    Show(kViewLog, failed && !logPath_.empty());
    UpdateFooter();
    ::InvalidateRect(Ctl(kStatus), nullptr, TRUE);
}

void Window::CheckPath(const std::wstring& path) {
    const Value r = c_.CheckPath(path, scope_ == L"machine" ? install::Scope::Machine : install::Scope::User);
    pathOk_ = r["ok"].asBool();
    installDir_ = r["normalized"].asWide().empty() ? path : r["normalized"].asWide();
    requiredBytes_ = r["requiredBytes"].asUint(requiredBytes_);
    for (const auto& v : hello_["variants"].array()) {
        if (v["id"].asString() == variant_) requiredBytes_ = v["requiredBytes"].asUint(requiredBytes_);
    }
    freeBytes_ = r["freeBytes"].asUint();
    SetText(kDirError, r["message"].asWide());
    UpdateInfoLines();
    Show(kDirError, page_ == Page::Welcome && !pathOk_);
    UpdateFooter();
    ::InvalidateRect(hwnd_, nullptr, TRUE);
}

void Window::OnScopeChanged() {
    const std::wstring scope = Checked(kOScopeMachine) ? L"machine" : L"user";
    if (scope == scope_) return;
    scope_ = scope;
    // Follow the scope's default folder unless the user picked one explicitly.
    CheckPath(userPickedPath_ ? installDir_ : hello_["options"][scope == L"machine" ? "machineDir" : "userDir"].asWide());
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
        ::CoTaskMemFree(chosen);
        userPickedPath_ = true;
        CheckPath(dir);
    }
    if (item) item->Release();
    dialog->Release();
}

// ---- Settings page (package inputs) -------------------------------------------------------------------------------

void Window::CreateInputs() {
    const auto& inputs = Inputs();
    inputOk_.assign(inputs.size(), true);
    inputTouched_.assign(inputs.size(), false);
    panel_ = ::CreateWindowExW(WS_EX_CONTROLPARENT, L"FluentSetupFallbackPanel", L"", WS_CHILD | WS_VSCROLL | WS_CLIPCHILDREN, 0, 0, 10, 10,
                               hwnd_, reinterpret_cast<HMENU>(static_cast<INT_PTR>(kIPanel)), ::GetModuleHandleW(nullptr), this);
    if (!panel_) return;
    const auto make = [&](const wchar_t* cls, size_t index, InputPart part, DWORD style, DWORD exStyle = 0) {
        return ::CreateWindowExW(exStyle, cls, L"", WS_CHILD | style, 0, 0, 10, 10, panel_,
                                 reinterpret_cast<HMENU>(static_cast<INT_PTR>(kInputBase + static_cast<int>(index) * 4 + part)),
                                 ::GetModuleHandleW(nullptr), nullptr);
    };
    for (size_t i = 0; i < inputs.size(); ++i) {
        const Value& input = inputs[i];
        const std::string type = input["type"].asString();
        // The label is created right before its control: screen readers take the control's name from it.
        std::wstring label = input["label"].asWide();
        HWND labelCtl = make(WC_STATICW, i, kInputLabel, WS_VISIBLE | SS_LEFT | SS_NOPREFIX);
        ::SetWindowTextW(labelCtl, label.c_str());
        if (type == "choice") {
            HWND combo = make(WC_COMBOBOXW, i, kInputControl, WS_VISIBLE | WS_TABSTOP | WS_GROUP | WS_VSCROLL | CBS_DROPDOWNLIST);
            for (const auto& choice : input["choices"].array()) {
                const std::wstring text = choice["label"].asWide();
                ::SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(text.c_str()));
            }
        } else {
            HWND edit = make(WC_EDITW, i, kInputControl, WS_VISIBLE | WS_TABSTOP | WS_GROUP | ES_AUTOHSCROLL | (type == "password" ? ES_PASSWORD : 0),
                             WS_EX_CLIENTEDGE);
            ::SendMessageW(edit, EM_LIMITTEXT, 4096, 0);
        }
        HWND description = make(WC_STATICW, i, kInputDescription, WS_VISIBLE | SS_LEFT | SS_NOPREFIX);
        ::SetWindowTextW(description, input["description"].asWide().c_str());
        make(WC_STATICW, i, kInputError, SS_LEFT | SS_NOPREFIX);
    }
}

std::wstring Window::InputValue(size_t index) const {
    const Value& input = Inputs()[index];
    HWND control = InputCtl(index, kInputControl);
    if (input["type"].asString() == "choice") {
        const LRESULT selected = ::SendMessageW(control, CB_GETCURSEL, 0, 0);
        const auto& choices = input["choices"].array();
        return selected >= 0 && static_cast<size_t>(selected) < choices.size() ? choices[static_cast<size_t>(selected)]["value"].asWide() : std::wstring();
    }
    std::wstring text(static_cast<size_t>(::GetWindowTextLengthW(control)) + 1, L'\0');
    ::GetWindowTextW(control, text.data(), static_cast<int>(text.size()));
    text.resize(wcslen(text.c_str()));
    return text;
}

void Window::OnInputChanged(size_t index, bool touched) {
    if (index >= Inputs().size()) return;
    // The bootstrapper's rules (required, choices, pattern) decide, exactly as for the WinUI front end and /input.
    const Value reply = c_.CheckInput(Inputs()[index]["id"].asString(), InputValue(index));
    inputOk_[index] = reply["ok"].asBool();
    if (touched) inputTouched_[index] = true;
    HWND error = InputCtl(index, kInputError);
    const bool showError = inputTouched_[index] && !inputOk_[index];
    ::SetWindowTextW(error, showError ? reply["message"].asWide().c_str() : L"");
    const bool wasShown = (::GetWindowLongW(error, GWL_STYLE) & WS_VISIBLE) != 0;
    ::ShowWindow(error, showError ? SW_SHOWNA : SW_HIDE);
    if (wasShown != showError) LayoutPanel();
    if (page_ == Page::Inputs) UpdateFooter();
}

void Window::LayoutPanel() {
    if (!panel_) return;
    RECT wr{};
    ::GetWindowRect(panel_, &wr);
    const int panelH = wr.bottom - wr.top;
    // The scroll bar's room is always kept, so showing it never re-flows the fields.
    const int width = std::max(Px(80), static_cast<int>(wr.right - wr.left) - ::GetSystemMetricsForDpi(SM_CXVSCROLL, dpi_) - Px(8));
    const int x = Px(4);
    const auto measure = [&](int y0) {
        int y = y0;
        HDWP dwp = ::BeginDeferWindowPos(static_cast<int>(Inputs().size()) * 4 + 1);
        const auto place = [&](HWND h, int top, int w, int hgt) {
            dwp = ::DeferWindowPos(dwp, h, nullptr, x, top, w, hgt, SWP_NOZORDER | SWP_NOACTIVATE);
        };
        for (size_t i = 0; i < Inputs().size(); ++i) {
            HWND label = InputCtl(i, kInputLabel), control = InputCtl(i, kInputControl);
            HWND description = InputCtl(i, kInputDescription), error = InputCtl(i, kInputError);
            const int lh = TextHeightOf(label, width);
            place(label, y, width, lh);
            y += lh + Px(6);
            const int cw = std::min(width, Px(kInputWidth));
            if (Inputs()[i]["type"].asString() == "choice") {
                // The height given to a drop-down list is the height of its open list; the closed box sizes itself.
                const int boxH = static_cast<int>(::SendMessageW(control, CB_GETITEMHEIGHT, static_cast<WPARAM>(-1), 0)) + Px(8);
                place(control, y, cw, Px(240));
                y += boxH + Px(4);
            } else {
                place(control, y, cw, Px(30));
                y += Px(30) + Px(4);
            }
            if (::GetWindowTextLengthW(description) > 0) {
                const int dh = TextHeightOf(description, width);
                place(description, y, width, dh);
                y += dh + Px(2);
            }
            if (::GetWindowLongW(error, GWL_STYLE) & WS_VISIBLE) {
                const int eh = TextHeightOf(error, width);
                place(error, y, width, eh);
                y += eh;
            }
            y += Px(16);
        }
        ::EndDeferWindowPos(dwp);
        return y;
    };
    panelContent_ = measure(Px(4) - panelScroll_) + panelScroll_;
    const int maxScroll = std::max(0, panelContent_ - panelH);
    if (panelScroll_ > maxScroll) {
        panelScroll_ = maxScroll;
        measure(Px(4) - panelScroll_);
    }
    SCROLLINFO si{sizeof(si)};
    si.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
    si.nMin = 0;
    si.nMax = std::max(0, panelContent_ - 1);
    si.nPage = static_cast<UINT>(std::max(0, panelH));
    si.nPos = panelScroll_;
    ::SetScrollInfo(panel_, SB_VERT, &si, TRUE);
    ::InvalidateRect(panel_, nullptr, TRUE);
}

void Window::ScrollPanelTo(int pos) {
    RECT wr{};
    ::GetWindowRect(panel_, &wr);
    pos = std::clamp(pos, 0, std::max(0, panelContent_ - static_cast<int>(wr.bottom - wr.top)));
    if (pos == panelScroll_) return;
    panelScroll_ = pos;
    LayoutPanel();
}

void Window::OnPanelScroll(int code) {
    SCROLLINFO si{sizeof(si)};
    si.fMask = SIF_ALL;
    ::GetScrollInfo(panel_, SB_VERT, &si);
    int pos = panelScroll_;
    switch (code) {
        case SB_LINEUP: pos -= Px(24); break;
        case SB_LINEDOWN: pos += Px(24); break;
        case SB_PAGEUP: pos -= static_cast<int>(si.nPage); break;
        case SB_PAGEDOWN: pos += static_cast<int>(si.nPage); break;
        case SB_THUMBTRACK:
        case SB_THUMBPOSITION: pos = si.nTrackPos; break;
        case SB_TOP: pos = 0; break;
        case SB_BOTTOM: pos = panelContent_; break;
        default: return;
    }
    ScrollPanelTo(pos);
}

// Keyboard focus moved to a field: scroll it (with its label) into view.
void Window::EnsureInputVisible(size_t index) {
    RECT panel{}, label{}, control{};
    ::GetClientRect(panel_, &panel);
    ::GetWindowRect(InputCtl(index, kInputLabel), &label);
    ::GetWindowRect(InputCtl(index, kInputControl), &control);
    ::MapWindowPoints(nullptr, panel_, reinterpret_cast<POINT*>(&label), 2);
    ::MapWindowPoints(nullptr, panel_, reinterpret_cast<POINT*>(&control), 2);
    if (label.top < 0) ScrollPanelTo(panelScroll_ + label.top - Px(4));
    else if (control.bottom > panel.bottom) ScrollPanelTo(panelScroll_ + control.bottom - panel.bottom + Px(24));
}

LRESULT CALLBACK Window::PanelProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_NCCREATE) {
        ::SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams));
    }
    auto* self = reinterpret_cast<Window*>(::GetWindowLongPtrW(h, GWLP_USERDATA));
    if (self && self->panel_ == h) {
        switch (m) {
            case WM_COMMAND:
            case WM_CTLCOLORSTATIC:
            case WM_CTLCOLOREDIT:
            case WM_CTLCOLORLISTBOX:
                return self->Handle(m, w, l);  // the window's handler knows every control and colour
            case WM_ERASEBKGND: {
                RECT r{};
                ::GetClientRect(h, &r);
                ::FillRect(reinterpret_cast<HDC>(w), &r, self->theme_.baseBrush());
                return 1;
            }
            case WM_VSCROLL:
                self->OnPanelScroll(LOWORD(w));
                return 0;
            case WM_MOUSEWHEEL:
                self->ScrollPanelTo(self->panelScroll_ - GET_WHEEL_DELTA_WPARAM(w) * self->Px(48) / WHEEL_DELTA);
                return 0;
            default: break;
        }
    }
    return ::DefWindowProcW(h, m, w, l);
}

// ---- banner (ui.banner) ---------------------------------------------------------------------------------------

void Window::LoadBanner() {
    const std::wstring& file = c_.package().bannerFile;
    if (file.empty()) return;
    std::vector<uint8_t> bytes;
    if (!c_.ReadFrontendFile(file, bytes, 4u << 20)) {
        log::Warn(L"The banner %s could not be read", file.c_str());
        return;
    }
    using Microsoft::WRL::ComPtr;
    ComPtr<IWICImagingFactory> factory;
    ComPtr<IWICStream> stream;
    ComPtr<IWICBitmapDecoder> decoder;
    ComPtr<IWICBitmapFrameDecode> frame;
    ComPtr<IWICFormatConverter> converter;
    UINT w = 0, h = 0;
    if (FAILED(::CoCreateInstance(kClsidWicImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))) ||
        FAILED(factory->CreateStream(&stream)) || FAILED(stream->InitializeFromMemory(bytes.data(), static_cast<DWORD>(bytes.size()))) ||
        FAILED(factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder)) ||
        FAILED(decoder->GetFrame(0, &frame)) || FAILED(factory->CreateFormatConverter(&converter)) ||
        FAILED(converter->Initialize(frame.Get(), kWicPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)) ||
        FAILED(converter->GetSize(&w, &h)) || w == 0 || h == 0 || w > 8192 || h > 8192) {
        log::Warn(L"The banner %s is not an image Windows can decode", file.c_str());
        return;
    }
    bannerPixels_.resize(static_cast<size_t>(w) * h * 4);
    if (FAILED(converter->CopyPixels(nullptr, w * 4, static_cast<UINT>(bannerPixels_.size()), bannerPixels_.data()))) {
        bannerPixels_.clear();
        return;
    }
    bannerSrcW_ = w;
    bannerSrcH_ = h;
}

void Window::RenderBanner() {
    if (banner_) ::DeleteObject(banner_);
    banner_ = nullptr;
    if (bannerPixels_.empty() || !hwnd_) return;
    RECT client{};
    ::GetClientRect(hwnd_, &client);
    // Scaled to the content width; a banner that would be taller than the cap is shown smaller instead.
    int w = client.right - 2 * Px(kMargin);
    int h = MulDiv(w, static_cast<int>(bannerSrcH_), static_cast<int>(bannerSrcW_));
    if (h > Px(kBannerMaxHeight)) {
        h = Px(kBannerMaxHeight);
        w = MulDiv(h, static_cast<int>(bannerSrcW_), static_cast<int>(bannerSrcH_));
    }
    if (w <= 0 || h <= 0) return;
    using Microsoft::WRL::ComPtr;
    ComPtr<IWICImagingFactory> factory;
    ComPtr<IWICBitmap> source;
    ComPtr<IWICBitmapScaler> scaler;
    if (FAILED(::CoCreateInstance(kClsidWicImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))) ||
        FAILED(factory->CreateBitmapFromMemory(bannerSrcW_, bannerSrcH_, kWicPixelFormat32bppPBGRA, bannerSrcW_ * 4,
                                               static_cast<UINT>(bannerPixels_.size()), bannerPixels_.data(), &source)) ||
        FAILED(factory->CreateBitmapScaler(&scaler)) ||
        FAILED(scaler->Initialize(source.Get(), static_cast<UINT>(w), static_cast<UINT>(h), WICBitmapInterpolationModeFant))) {
        return;
    }
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;  // top-down
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP bitmap = ::CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bitmap) return;
    if (FAILED(scaler->CopyPixels(nullptr, static_cast<UINT>(w) * 4, static_cast<UINT>(w) * 4 * static_cast<UINT>(h), static_cast<BYTE*>(bits)))) {
        ::DeleteObject(bitmap);
        return;
    }
    // Premultiplied pixels over the page colour: out = src + background * (1 - alpha).
    const COLORREF bg = theme_.colors().base;
    const unsigned back[3] = {GetBValue(bg), GetGValue(bg), GetRValue(bg)};
    auto* p = static_cast<uint8_t*>(bits);
    for (size_t i = 0, n = static_cast<size_t>(w) * h; i < n; ++i, p += 4) {
        const unsigned inverse = 255u - p[3];
        for (int c = 0; c < 3; ++c) p[c] = static_cast<uint8_t>(std::min(255u, p[c] + (back[c] * inverse + 127u) / 255u));
        p[3] = 255;
    }
    banner_ = bitmap;
    bannerW_ = w;
    bannerH_ = h;
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
        case WM_CTLCOLORLISTBOX:
        case WM_CTLCOLORBTN: {
            HDC dc = reinterpret_cast<HDC>(w);
            const int id = ::GetDlgCtrlID(reinterpret_cast<HWND>(l));
            const int inputPart = id >= kInputBase ? (id - kInputBase) % 4 : -1;
            const auto& c = theme_.colors();
            HBRUSH brush = theme_.baseBrush();
            COLORREF bg = c.base, fg = c.text;
            if (id == kInfo) brush = theme_.infoBrush(), bg = c.infoFill;
            if (id == kLText || id == kResultDetail || inputPart == kInputControl || m == WM_CTLCOLORLISTBOX) brush = theme_.editBrush(), bg = c.editFill;
            if (id == kLblPublisher || id == kLblVersion || id == kLblLocation || id == kLblSize || id == kOScopeLabel || id == kOVariantLabel || id == kOVariantDesc || id == kUserDataPaths || id == kDescription || id == kPDetail || id == kPPercent || id == kResultCode || id == kRepairNote || inputPart == kInputDescription) fg = c.textSecondary;
            if (id == kSignature) fg = signatureVerified_ ? c.textSecondary : c.caution;
            if (id == kDirError || inputPart == kInputError) fg = c.critical;
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
            else if (id == kSecondary) OnSecondary();
            else if (id == kChange) Browse();
            else if ((id == kOScopeUser || id == kOScopeMachine) && code == BN_CLICKED) OnScopeChanged();
            else if (id >= kVariantBase && id < kVariantBase + static_cast<int>(VariantCount()) && code == BN_CLICKED) OnVariantChanged(static_cast<size_t>(id - kVariantBase));
            else if (id == kLAccept && code == BN_CLICKED) UpdateFooter();
            else if (id >= kInputBase && static_cast<size_t>((id - kInputBase) / 4) < Inputs().size() && (id - kInputBase) % 4 == kInputControl) {
                const size_t index = static_cast<size_t>((id - kInputBase) / 4);
                if (code == EN_CHANGE || code == CBN_SELCHANGE) OnInputChanged(index, !applyingHello_);
                else if (code == EN_SETFOCUS || code == CBN_SETFOCUS) EnsureInputVisible(index);
                else if (code == EN_KILLFOCUS || code == CBN_KILLFOCUS) OnInputChanged(index, true);
            }
            else if (id == kViewLog && !logPath_.empty()) {
                ::ShellExecuteW(hwnd_, L"open", L"notepad.exe", (L"\"" + logPath_ + L"\"").c_str(), nullptr, SW_SHOWNORMAL);
            } else if (id == kOpenFolder && !installDir_.empty()) {
                ::ShellExecuteW(hwnd_, L"open", installDir_.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            }
            return 0;
        }
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
            RenderBanner();
            Layout();
            ::InvalidateRect(hwnd_, nullptr, TRUE);
            return 0;
        }
        case WM_SETTINGCHANGE:
            if (l && wcscmp(reinterpret_cast<const wchar_t*>(l), L"ImmersiveColorSet") == 0) ApplyTheme();
            break;
        case WM_MOUSEWHEEL:
            if (page_ == Page::Inputs && panel_) {
                ScrollPanelTo(panelScroll_ - GET_WHEEL_DELTA_WPARAM(w) * Px(48) / WHEEL_DELTA);
                return 0;
            }
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
