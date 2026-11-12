#include "Setup.Win32/theme.h"

#include <dwmapi.h>
#include <uxtheme.h>

#include <cstring>

namespace fsetup::win32ui {

namespace {

bool SystemUsesDarkApps() {
    DWORD value = 1, size = sizeof(value);
    if (::RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                       L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &value, &size) != ERROR_SUCCESS) {
        return false;
    }
    return value == 0;
}

bool FontExists(const wchar_t* face) {
    HDC dc = ::GetDC(nullptr);
    LOGFONTW lf{};
    lf.lfCharSet = DEFAULT_CHARSET;
    wcsncpy_s(lf.lfFaceName, face, _TRUNCATE);
    bool found = false;
    ::EnumFontFamiliesExW(
        dc, &lf, [](const LOGFONTW*, const TEXTMETRICW*, DWORD, LPARAM p) -> int {
            *reinterpret_cast<bool*>(p) = true;
            return 0;
        },
        reinterpret_cast<LPARAM>(&found), 0);
    ::ReleaseDC(nullptr, dc);
    return found;
}

HFONT MakeFont(const wchar_t* face, int dip, int weight, UINT dpi) {
    LOGFONTW lf{};
    lf.lfHeight = -MulDiv(dip, static_cast<int>(dpi), 96);
    lf.lfWeight = weight;
    lf.lfCharSet = DEFAULT_CHARSET;
    lf.lfQuality = CLEARTYPE_QUALITY;
    wcsncpy_s(lf.lfFaceName, face, _TRUNCATE);
    return ::CreateFontIndirectW(&lf);
}

}  // namespace

Theme::~Theme() {
    DestroyBrushes();
    DestroyFonts();
}

void Theme::Init(const char* themeSetting) {
    setting_ = themeSetting;
    Refresh();
}

void Theme::Refresh() {
    HIGHCONTRASTW hc{sizeof(hc)};
    highContrast_ = ::SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(hc), &hc, 0) && (hc.dwFlags & HCF_HIGHCONTRASTON);
    if (strcmp(setting_, "dark") == 0) dark_ = true;
    else if (strcmp(setting_, "light") == 0) dark_ = false;
    else dark_ = SystemUsesDarkApps();
    if (highContrast_) dark_ = false;
    BuildPalette();
}

void Theme::BuildPalette() {
    DestroyBrushes();
    if (highContrast_) {
        const COLORREF window = ::GetSysColor(COLOR_WINDOW), text = ::GetSysColor(COLOR_WINDOWTEXT);
        palette_ = {window, window, text, text, ::GetSysColor(COLOR_GRAYTEXT) == window ? text : text, text, text, text,
                    window, text, window};
    } else if (dark_) {
        palette_ = {RGB(32, 32, 32),    RGB(39, 39, 39),    RGB(29, 29, 29),    RGB(255, 255, 255),
                    RGB(206, 206, 206), RGB(255, 153, 164), RGB(108, 203, 95),  RGB(252, 225, 0),
                    RGB(45, 45, 45),    RGB(58, 58, 58),    RGB(45, 45, 45)};
    } else {
        palette_ = {RGB(243, 243, 243), RGB(251, 251, 251), RGB(229, 229, 229), RGB(27, 27, 27),
                    RGB(96, 96, 96),    RGB(196, 43, 28),   RGB(15, 123, 15),   RGB(157, 93, 0),
                    RGB(246, 246, 246), RGB(229, 229, 229), RGB(255, 255, 255)};
    }
    baseBrush_ = ::CreateSolidBrush(palette_.base);
    layerBrush_ = ::CreateSolidBrush(palette_.layer);
    infoBrush_ = ::CreateSolidBrush(palette_.infoFill);
    editBrush_ = ::CreateSolidBrush(palette_.editFill);
}

void Theme::DestroyBrushes() {
    for (HBRUSH* b : {&baseBrush_, &layerBrush_, &infoBrush_, &editBrush_}) {
        if (*b) ::DeleteObject(*b);
        *b = nullptr;
    }
}

void Theme::DestroyFonts() {
    for (HFONT* f : {&body, &bodyStrong, &heading, &subtitle, &title, &statusIcon, &caption, &icon}) {
        if (*f) ::DeleteObject(*f);
        *f = nullptr;
    }
}

void Theme::CreateFonts(UINT dpi) {
    DestroyFonts();
    static const bool variable = FontExists(L"Segoe UI Variable Text");
    const wchar_t* text = variable ? L"Segoe UI Variable Text" : L"Segoe UI";
    const wchar_t* display = variable ? L"Segoe UI Variable Display" : L"Segoe UI";
    const wchar_t* smallFace = variable ? L"Segoe UI Variable Small" : L"Segoe UI";
    static const bool fluentIcons = FontExists(L"Segoe Fluent Icons");
    body = MakeFont(text, 14, FW_NORMAL, dpi);
    bodyStrong = MakeFont(text, 14, FW_SEMIBOLD, dpi);
    heading = MakeFont(display, 18, FW_SEMIBOLD, dpi);
    subtitle = MakeFont(display, 20, FW_SEMIBOLD, dpi);
    caption = MakeFont(smallFace, 12, FW_NORMAL, dpi);
    title = MakeFont(display, 28, FW_SEMIBOLD, dpi);
    icon = MakeFont(fluentIcons ? L"Segoe Fluent Icons" : L"Segoe MDL2 Assets", 40, FW_NORMAL, dpi);
    statusIcon = MakeFont(fluentIcons ? L"Segoe Fluent Icons" : L"Segoe MDL2 Assets", 28, FW_NORMAL, dpi);
}

void Theme::ApplyWindowAttributes(HWND hwnd) const {
    const BOOL darkCaption = dark_ ? TRUE : FALSE;
    // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+), 19 on older Windows 10 builds.
    if (FAILED(::DwmSetWindowAttribute(hwnd, 20, &darkCaption, sizeof(darkCaption)))) {
        ::DwmSetWindowAttribute(hwnd, 19, &darkCaption, sizeof(darkCaption));
    }
    // Windows 11: rounded corners and a caption that matches the header so both read as one surface.
    const int round = 2;  // DWMWCP_ROUND
    ::DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &round, sizeof(round));
    if (!highContrast_) {
        const COLORREF captionColor = palette_.base;
        ::DwmSetWindowAttribute(hwnd, 35 /*DWMWA_CAPTION_COLOR*/, &captionColor, sizeof(captionColor));
        const COLORREF textColor = palette_.text;
        ::DwmSetWindowAttribute(hwnd, 36 /*DWMWA_TEXT_COLOR*/, &textColor, sizeof(textColor));
    }
}

void Theme::ApplyControlTheme(HWND control, bool isEdit) const {
    if (highContrast_ || !dark_) {
        ::SetWindowTheme(control, L"Explorer", nullptr);
        if (isEdit) ::SetWindowTheme(control, nullptr, nullptr);
        return;
    }
    ::SetWindowTheme(control, isEdit ? L"DarkMode_CFD" : L"DarkMode_Explorer", nullptr);
}

}  // namespace fsetup::win32ui
