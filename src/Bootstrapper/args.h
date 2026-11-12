// Strict command-line parsing for Setup.exe / uninstall.exe. Unknown switches, missing values and malformed values are
// errors (exit code InvalidArguments), never silently ignored.
//
// Public switches (both "/x" and "--x" spellings, case-insensitive; values follow as the next argument or after '='):
//   /quiet | /verysilent | /s no UI at all
//   /passive | /silent        progress window only (no questions); Inno Setup's /SILENT
//   /uninstall                remove the installed app
//   /log <file>               write the log to this file
//   /dir <path>               install location
//   /scope user|machine       installation scope
//   /desktop-shortcut yes|no  /start-menu-shortcut yes|no
//   /lang en-US|zh-CN         UI language
//   /option <id>=yes|no       package-defined option (repeatable), e.g. /option autostart=no
//   /ui auto|winui|win32      front-end selection (diagnostics)
//   /help | /?
// Internal switches (used between FluentSetup processes, documented in docs/architecture.md):
//   --worker --pipe <name> --parent-pid <n> [--origin-dir <dir>] [--wait-pid <n>]
//   --phase2 --origin-dir <dir> --origin-process <handle> --result-pipe <handle>
//   --ui-simulate <mode>
#pragma once

#include <map>
#include <optional>
#include <string>
#include <vector>

namespace fsetup::boot {

enum class UiChoice { Auto, WinUI, Win32 };

struct Args {
    bool quiet = false;
    bool passive = false;  // /passive, Inno /SILENT: show progress, ask nothing, close when done (quiet wins)
    bool uninstall = false;
    bool help = false;
    std::wstring logPath;
    std::wstring installDir;
    std::optional<bool> machineScope;
    std::optional<bool> desktopShortcut;
    std::optional<bool> startMenuShortcut;
    std::wstring language;
    UiChoice ui = UiChoice::Auto;
    std::map<std::string, bool> choices;  // /option id=yes|no (package-defined options)
    std::string variant;                  // /variant id (package variant, e.g. shared or selfContained)
    bool removeUserData = false;          // /remove-user-data (uninstall)
    bool relaunch = false;                // /relaunch, Inno /RELAUNCH=1, NSIS-style /UPDATE: start the app after a quiet or passive install
    std::optional<std::vector<std::wstring>> tasks;  // Inno /TASKS (exact) or /MERGETASKS (added): option ids, desktopicon
    bool tasksMerge = false;
    std::wstring uiSimulate;

    // internal
    bool worker = false;
    std::wstring pipe;
    unsigned long parentPid = 0;
    std::wstring originDir;
    unsigned long waitPid = 0;
    bool phase2 = false;
    unsigned long long originProcess = 0;  // inherited handle value
    unsigned long long resultPipe = 0;     // inherited handle value
};

// Returns false and sets `error` on any invalid input.
bool ParseArgs(const wchar_t* commandLine, Args& out, std::wstring& error);

std::wstring QuoteArg(const std::wstring& arg);

}  // namespace fsetup::boot
