#include "Bootstrapper/args.h"

#include "Common/path.h"
#include "Common/text.h"
#include "Common/win.h"

#include <shellapi.h>

#include <vector>

namespace fsetup::boot {

namespace {

bool ParseYesNo(std::wstring_view v, bool& out) {
    if (EqualsIgnoreCase(v, L"yes") || EqualsIgnoreCase(v, L"true") || v == L"1") return out = true, true;
    if (EqualsIgnoreCase(v, L"no") || EqualsIgnoreCase(v, L"false") || v == L"0") return out = false, true;
    return false;
}

bool ParseUnsigned(std::wstring_view v, unsigned long long& out) {
    if (v.empty() || v.size() > 20) return false;
    out = 0;
    for (const wchar_t c : v) {
        if (c < L'0' || c > L'9') return false;
        const unsigned long long next = out * 10 + static_cast<unsigned>(c - L'0');
        if (next < out) return false;
        out = next;
    }
    return true;
}

}  // namespace

bool ParseArgs(const wchar_t* commandLine, Args& out, std::wstring& error) {
    int argc = 0;
    LPWSTR* argv = ::CommandLineToArgvW(commandLine, &argc);
    if (!argv) {
        error = L"Cannot parse the command line";
        return false;
    }
    std::vector<std::wstring> args(argv + 1, argv + argc);
    ::LocalFree(argv);

    for (size_t i = 0; i < args.size(); ++i) {
        std::wstring token = args[i];
        if (token.size() < 2 || (token[0] != L'/' && token[0] != L'-')) {
            error = L"Unexpected argument: " + token;
            return false;
        }
        std::wstring name = token.substr(token[0] == L'-' && token.size() > 2 && token[1] == L'-' ? 2 : 1);
        std::optional<std::wstring> inlineValue;
        if (const size_t eq = name.find(L'='); eq != std::wstring::npos) {
            inlineValue = name.substr(eq + 1);
            name = name.substr(0, eq);
        }
        name = ToLower(name);
        const auto value = [&](std::wstring& v) -> bool {
            if (inlineValue) {
                v = *inlineValue;
            } else if (i + 1 < args.size()) {
                v = args[++i];
            } else {
                error = L"Missing value for /" + name;
                return false;
            }
            if (v.empty() || v.size() > 2048) {
                error = L"Invalid value for /" + name;
                return false;
            }
            return true;
        };
        const auto flag = [&]() -> bool {
            if (inlineValue) {
                error = L"/" + name + L" does not take a value";
                return false;
            }
            return true;
        };
        std::wstring v;
        unsigned long long n = 0;

        if (name == L"quiet" || name == L"silent" || name == L"q" || name == L"s") {
            if (!flag()) return false;
            out.quiet = true;
        } else if (name == L"uninstall" || name == L"u") {
            if (!flag()) return false;
            out.uninstall = true;
        } else if (name == L"help" || name == L"?" || name == L"h") {
            if (!flag()) return false;
            out.help = true;
        } else if (name == L"log") {
            if (!value(v)) return false;
            std::wstring normalized;
            const std::wstring parent = path::Parent(v);
            if (path::ValidateAbsoluteSyntax(parent, normalized) != path::Problem::None || !path::IsValidComponent(path::FileName(v))) {
                error = L"/log needs an absolute file path on a local drive";
                return false;
            }
            out.logPath = path::Join(normalized, path::FileName(v));
        } else if (name == L"dir" || name == L"installdir") {
            if (!value(v)) return false;
            out.installDir = v;  // validated by the engine (and reported with a precise reason)
        } else if (name == L"scope") {
            if (!value(v)) return false;
            if (EqualsIgnoreCase(v, L"user")) out.machineScope = false;
            else if (EqualsIgnoreCase(v, L"machine")) out.machineScope = true;
            else return error = L"/scope must be 'user' or 'machine'", false;
        } else if (name == L"desktop-shortcut") {
            bool b = false;
            if (!value(v) || !ParseYesNo(v, b)) return error = L"/desktop-shortcut must be yes or no", false;
            out.desktopShortcut = b;
        } else if (name == L"start-menu-shortcut") {
            bool b = false;
            if (!value(v) || !ParseYesNo(v, b)) return error = L"/start-menu-shortcut must be yes or no", false;
            out.startMenuShortcut = b;
        } else if (name == L"option") {
            if (!value(v)) return false;
            const size_t eq = v.find(L'=');
            bool on = false;
            const std::wstring id = eq == std::wstring::npos ? std::wstring() : v.substr(0, eq);
            if (id.empty() || id.size() > 32 || !ParseYesNo(std::wstring_view(v).substr(eq + 1), on)) {
                return error = L"/option must look like name=yes or name=no", false;
            }
            for (const wchar_t c : id) {
                if (!(iswalnum(c) || c == L'_' || c == L'-')) return error = L"invalid /option name", false;
            }
            out.choices[WideToUtf8(id)] = on;
        } else if (name == L"lang") {
            if (!value(v)) return false;
            if (!EqualsIgnoreCase(v, L"en-US") && !EqualsIgnoreCase(v, L"zh-CN")) return error = L"/lang must be en-US or zh-CN", false;
            out.language = v;
        } else if (name == L"ui") {
            if (!value(v)) return false;
            if (EqualsIgnoreCase(v, L"auto")) out.ui = UiChoice::Auto;
            else if (EqualsIgnoreCase(v, L"winui")) out.ui = UiChoice::WinUI;
            else if (EqualsIgnoreCase(v, L"win32")) out.ui = UiChoice::Win32;
            else return error = L"/ui must be auto, winui or win32", false;
        } else if (name == L"ui-simulate") {
            if (!value(v)) return false;
            if (v != L"runtime-missing" && v != L"crash-before-ready" && v != L"crash-after-ready" && v != L"hang") {
                return error = L"unknown --ui-simulate mode", false;
            }
            out.uiSimulate = v;
        } else if (name == L"worker") {
            if (!flag()) return false;
            out.worker = true;
        } else if (name == L"pipe") {
            if (!value(v)) return false;
            if (v.size() > 80) return error = L"bad pipe name", false;
            for (const wchar_t c : v) {
                if (!(iswalnum(c) || c == L'-')) return error = L"bad pipe name", false;
            }
            out.pipe = v;
        } else if (name == L"parent-pid" || name == L"wait-pid") {
            if (!value(v) || !ParseUnsigned(v, n) || n == 0 || n > 0xFFFFFFFFull) return error = L"bad process id", false;
            (name == L"parent-pid" ? out.parentPid : out.waitPid) = static_cast<unsigned long>(n);
        } else if (name == L"origin-dir") {
            if (!value(v)) return false;
            std::wstring normalized;
            if (path::ValidateAbsoluteSyntax(v, normalized) != path::Problem::None) return error = L"bad origin directory", false;
            out.originDir = normalized;
        } else if (name == L"phase2") {
            if (!flag()) return false;
            out.phase2 = true;
        } else if (name == L"origin-process" || name == L"result-pipe") {
            if (!value(v) || !ParseUnsigned(v, n) || n == 0) return error = L"bad handle value", false;
            (name == L"origin-process" ? out.originProcess : out.resultPipe) = n;
        } else {
            error = L"Unknown option: " + token;
            return false;
        }
    }
    if (out.worker && (out.pipe.empty() || out.parentPid == 0)) return error = L"--worker requires --pipe and --parent-pid", false;
    if (out.phase2 && (out.originDir.empty() || out.originProcess == 0 || out.resultPipe == 0)) {
        return error = L"--phase2 requires --origin-dir, --origin-process and --result-pipe", false;
    }
    return true;
}

std::wstring QuoteArg(const std::wstring& arg) {
    // CommandLineToArgvW-compatible quoting.
    if (!arg.empty() && arg.find_first_of(L" \t\"") == std::wstring::npos) return arg;
    std::wstring out = L"\"";
    for (size_t i = 0; i < arg.size(); ++i) {
        size_t backslashes = 0;
        while (i < arg.size() && arg[i] == L'\\') {
            ++backslashes;
            ++i;
        }
        if (i == arg.size()) {
            out.append(backslashes * 2, L'\\');  // they precede the closing quote
            break;
        }
        if (arg[i] == L'"') {
            out.append(backslashes * 2 + 1, L'\\');
        } else {
            out.append(backslashes, L'\\');
        }
        out.push_back(arg[i]);
    }
    out.push_back(L'"');
    return out;
}

}  // namespace fsetup::boot
