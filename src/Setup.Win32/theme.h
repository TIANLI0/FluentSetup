// Visual resources for the Win32 fallback front end: colours approximating the Windows 11 Fluent palette for light and
// dark mode, system colours in high-contrast mode, DPI-scaled fonts (Segoe UI Variable on Windows 11, Segoe UI on
// Windows 10) and the DWM window attributes that are available natively.
#pragma once

#include "Common/win.h"

namespace fsetup::win32ui {

struct Palette {
    COLORREF base;       // header / title bar
    COLORREF layer;      // content area
    COLORREF stroke;     // separators
    COLORREF text;
    COLORREF textSecondary;
    COLORREF critical;
    COLORREF success;
    COLORREF caution;
    COLORREF infoFill;
    COLORREF infoStroke;
    COLORREF editFill;
};

class Theme {
public:
    ~Theme();
    // themeSetting: "light" | "dark" | "system".
    void Init(const char* themeSetting);
    void Refresh();  // re-read system state (WM_SETTINGCHANGE / WM_THEMECHANGED)
    void CreateFonts(UINT dpi);

    [[nodiscard]] bool dark() const noexcept { return dark_; }
    [[nodiscard]] bool highContrast() const noexcept { return highContrast_; }
    [[nodiscard]] const Palette& colors() const noexcept { return palette_; }
    [[nodiscard]] HBRUSH baseBrush() const noexcept { return baseBrush_; }
    [[nodiscard]] HBRUSH layerBrush() const noexcept { return layerBrush_; }
    [[nodiscard]] HBRUSH infoBrush() const noexcept { return infoBrush_; }
    [[nodiscard]] HBRUSH editBrush() const noexcept { return editBrush_; }

    HFONT body = nullptr;
    HFONT bodyStrong = nullptr;
    HFONT heading = nullptr;
    HFONT subtitle = nullptr;
    HFONT caption = nullptr;
    HFONT icon = nullptr;

    // Applies title-bar colour, dark caption and rounded corners where the OS supports them.
    void ApplyWindowAttributes(HWND hwnd) const;
    // Dark-mode control themes ("DarkMode_Explorer" etc.) or the default theme.
    void ApplyControlTheme(HWND control, bool isEdit) const;

private:
    void BuildPalette();
    void DestroyBrushes();
    void DestroyFonts();

    const char* setting_ = "system";
    bool dark_ = false;
    bool highContrast_ = false;
    Palette palette_{};
    HBRUSH baseBrush_ = nullptr, layerBrush_ = nullptr, infoBrush_ = nullptr, editBrush_ = nullptr;
};

}  // namespace fsetup::win32ui
