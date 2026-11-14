#include "Install.Core/extensions.h"

#include "Common/error.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Install.Core/package.h"

#include <comdef.h>
#include <taskschd.h>
#include <tlhelp32.h>
#include <winhttp.h>
#include <appmodel.h>
#include <shellapi.h>
#include <evntrace.h>
#include <sddl.h>
#include <restartmanager.h>

#include <algorithm>
#include <cerrno>
#include <regex>
#include <unordered_set>

#pragma comment(lib, "taskschd.lib")
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "rstrtmgr.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "version.lib")

namespace fsetup::install {

namespace {

[[noreturn]] void Bad(const std::wstring& why) { throw Failure(ErrorCode::PackageCorrupt, L"Invalid package header: " + why); }

bool IsExeName(const std::wstring& n) {
    return path::IsValidComponent(n) && n.size() > 4 && EqualsIgnoreCase(std::wstring_view(n).substr(n.size() - 4), L".exe");
}

LocalizedText ParseText(const json::Value& v) {
    LocalizedText t;
    if (v.isString()) {
        t.byLanguage["*"] = v.asWide().substr(0, 256);
    } else {
        for (const auto& [k, s] : v.object()) {
            if (s.isString() && k.size() <= 16) t.byLanguage[k] = s.asWide().substr(0, 256);
        }
    }
    return t;
}

std::vector<std::wstring> ParseArgs(const json::Value& v) {
    std::vector<std::wstring> out;
    if (v.array().size() > 32) Bad(L"too many action arguments");
    for (const auto& a : v.array()) {
        if (!a.isString() || a.asString().size() > 1024) Bad(L"invalid action argument");
        std::wstring s = a.asWide();
        if (s.find(L'\0') != std::wstring::npos) Bad(L"invalid action argument");
        out.push_back(std::move(s));
    }
    return out;
}

bool ValidTaskPath(const std::wstring& n);

bool ValidTaskOrValueName(const std::wstring& n) {
    if (n.empty() || n.size() > 128) return false;
    for (const wchar_t c : n) {
        if (c < 0x20 || wcschr(L"\\/:*?\"<>|", c)) return false;
    }
    return true;
}

// CommandLineToArgvW-compatible quoting (same rules as the bootstrapper's QuoteArg).
std::wstring Quote(const std::wstring& arg) {
    if (!arg.empty() && arg.find_first_of(L" \t\"") == std::wstring::npos) return arg;
    std::wstring out = L"\"";
    for (size_t i = 0; i < arg.size(); ++i) {
        size_t backslashes = 0;
        while (i < arg.size() && arg[i] == L'\\') {
            ++backslashes;
            ++i;
        }
        if (i == arg.size()) {
            out.append(backslashes * 2, L'\\');
            break;
        }
        out.append(arg[i] == L'"' ? backslashes * 2 + 1 : backslashes, L'\\');
        out.push_back(arg[i]);
    }
    out.push_back(L'"');
    return out;
}

bool ValidTaskPath(const std::wstring& n) {
    if (n.empty() || n.size() > 255 || n.front() == L'\\' || n.back() == L'\\') return false;
    size_t start = 0;
    for (size_t pos; (pos = n.find(L'\\', start)) != std::wstring::npos; start = pos + 1) {
        if (!ValidTaskOrValueName(n.substr(start, pos - start))) return false;
    }
    return ValidTaskOrValueName(n.substr(start));
}

bool IsDataTemplate(const std::wstring& t) { return !ExpandDataTemplate(t, L"C:\\InstallDir").empty(); }
// The elevated cleanup may run after the install record is gone (per-user uninstall + UAC): no {InstallDir} there.
bool IsElevatedDataTemplate(const std::wstring& t) { return !ExpandDataTemplate(t).empty(); }

bool IsLnkName(const std::wstring& n) {
    return path::IsValidComponent(n) && n.size() > 4 && EqualsIgnoreCase(std::wstring_view(n).substr(n.size() - 4), L".lnk");
}

void ParseNames(const json::Value& list, std::vector<std::wstring>& out, bool (*valid)(const std::wstring&), const wchar_t* what) {
    if (list.array().size() > 32) Bad(std::wstring(L"too many ") + what + L" entries");
    for (const auto& v : list.array()) {
        if (!v.isString() || !valid(v.asWide())) Bad(std::wstring(L"invalid ") + what + L": " + v.asWide());
        out.push_back(v.asWide());
    }
}

HKEY ParseHive(const std::string& h) {
    if (h == "HKLM") return HKEY_LOCAL_MACHINE;
    if (h == "HKCU") return HKEY_CURRENT_USER;
    return nullptr;
}

bool IsAsciiAlnum(wchar_t c) { return (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9'); }

// AppUserModelID: up to 128 characters, no spaces (Company.Product[.SubProduct.Version]).
bool IsAppUserModelId(const std::wstring& id) {
    return !id.empty() && id.size() <= 128 && std::all_of(id.begin(), id.end(), [](wchar_t c) { return IsAsciiAlnum(c) || c == L'.' || c == L'-' || c == L'_'; });
}

// Extensions that would hijack how Windows runs programs or opens shell objects are never registered.
bool IsAssociableExtension(const std::wstring& ext) {
    if (ext.size() < 2 || ext.size() > 33 || ext.front() != L'.') return false;
    if (!std::all_of(ext.begin() + 1, ext.end(), [](wchar_t c) { return IsAsciiAlnum(c) || c == L'_' || c == L'-'; })) return false;
    static const wchar_t* const kReserved[] = {
        L".exe", L".com", L".bat", L".cmd", L".lnk", L".dll", L".sys", L".msi", L".msp", L".msu", L".ps1", L".psm1", L".psd1",
        L".vbs", L".vbe", L".js", L".jse", L".wsf", L".wsh", L".scr", L".pif", L".cpl", L".reg", L".url", L".hta", L".inf",
        L".msc", L".ocx", L".drv", L".appx", L".msix", L".appxbundle", L".msixbundle", L".appinstaller", L".library-ms",
        L".search-ms", L".settingcontent-ms", L".application", L".jar", L".scf", L".theme", L".themepack", L".diagcab"};
    return std::none_of(std::begin(kReserved), std::end(kReserved), [&](const wchar_t* r) { return ext == r; });
}

// "Vendor.App.Type": letters/digits in dot-separated parts, at least one dot, max 39 characters, not Microsoft's.
bool IsProgId(const std::wstring& id) {
    if (id.size() < 3 || id.size() > 39 || !((id[0] >= L'a' && id[0] <= L'z') || (id[0] >= L'A' && id[0] <= L'Z'))) return false;
    bool dot = false, partEmpty = false;
    for (const wchar_t c : id) {
        if (c == L'.') {
            if (partEmpty) return false;
            dot = partEmpty = true;
        } else if (IsAsciiAlnum(c)) {
            partEmpty = false;
        } else {
            return false;
        }
    }
    return dot && !partEmpty && !StartsWithIgnoreCase(id, L"Microsoft.") && !StartsWithIgnoreCase(id, L"Windows.") &&
           !StartsWithIgnoreCase(id, L"AppX");
}

// A scheme of the app's own: lowercase, 2-32 characters, none of the schemes Windows and browsers rely on.
bool IsCustomScheme(const std::wstring& s) {
    if (s.size() < 2 || s.size() > 32 || !(s[0] >= L'a' && s[0] <= L'z')) return false;
    if (!std::all_of(s.begin(), s.end(), [](wchar_t c) { return (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'+' || c == L'-' || c == L'.'; })) return false;
    static const wchar_t* const kReserved[] = {L"http", L"https", L"file", L"ftp", L"ftps", L"mailto", L"news", L"nntp", L"telnet", L"ldap",
                                                L"tel", L"sms", L"callto", L"javascript", L"vbscript", L"data", L"about", L"res", L"shell",
                                                L"search", L"its", L"mk", L"hcp", L"read", L"onenote", L"skype", L"zoommtg", L"steam"};
    if (std::any_of(std::begin(kReserved), std::end(kReserved), [&](const wchar_t* r) { return s == r; })) return false;
    // Shell classes live in the same registry tree (HKCU\Software\Classes overrides HKLM): never one of those.
    static const wchar_t* const kShellClasses[] = {
        L"exefile", L"batfile", L"cmdfile", L"comfile", L"lnkfile", L"piffile", L"scrfile", L"regfile", L"htafile", L"vbsfile", L"vbefile",
        L"jsfile", L"jsefile", L"wsffile", L"wshfile", L"cplfile", L"dllfile", L"sysfile", L"inffile", L"mscfile", L"folder", L"directory",
        L"drive", L"allfilesystemobjects", L"clsid", L"interface", L"typelib", L"appid", L"systemfileassociations", L"applications",
        L"unknown", L"protocols", L"mime", L"network", L"printers", L"installer", L"msi.package", L"msi.patch", L"desktopbackground",
        L"librarylocation", L"launchinghost", L"txtfile", L"inifile", L"jpegfile", L"pngfile", L"htmlfile", L"urlfile", L"xmlfile"};
    if (std::any_of(std::begin(kShellClasses), std::end(kShellClasses), [&](const wchar_t* c) { return s == c; })) return false;
    return s.rfind(L"ms-", 0) != 0 && s.rfind(L"windows", 0) != 0 && s.rfind(L"microsoft", 0) != 0 && s.rfind(L"search-ms", 0) != 0;
}

// "80", "8000-8010,9000": ports 1-65535.
bool IsPortList(const std::wstring& list) {
    if (list.size() > 256) return false;
    size_t start = 0;
    while (start <= list.size()) {
        size_t end = list.find(L',', start);
        if (end == std::wstring::npos) end = list.size();
        const std::wstring part = list.substr(start, end - start);
        const size_t dash = part.find(L'-');
        for (const std::wstring& n : {part.substr(0, dash), dash == std::wstring::npos ? std::wstring(L"1") : part.substr(dash + 1)}) {
            if (n.empty() || n.size() > 5 || n.find_first_not_of(L"0123456789") != std::wstring::npos) return false;
            const unsigned long v = wcstoul(n.c_str(), nullptr, 10);
            if (v < 1 || v > 65535) return false;
        }
        start = end + 1;
    }
    return true;
}

}  // namespace

bool ParseRegistryNumber(const std::wstring& text, bool qword, uint64_t& out) {
    if (text.empty() || text.size() > 20) return false;
    const bool hex = text.size() > 2 && text[0] == L'0' && (text[1] == L'x' || text[1] == L'X');
    const std::wstring digits = hex ? text.substr(2) : text;
    if (digits.empty() || digits.find_first_not_of(hex ? L"0123456789abcdefABCDEF" : L"0123456789") != std::wstring::npos) return false;
    errno = 0;
    out = wcstoull(digits.c_str(), nullptr, hex ? 16 : 10);
    if (errno == ERANGE) return false;
    return qword || out <= 0xFFFFFFFFull;
}

std::wstring ExpandSystemTemplate(const std::wstring& tpl) {
    struct Root {
        const wchar_t* token;
        REFKNOWNFOLDERID id;
    };
    static const Root kRoots[] = {{L"{ProgramFiles}\\", FOLDERID_ProgramFiles},     {L"{ProgramFilesX86}\\", FOLDERID_ProgramFilesX86},
                                  {L"{System}\\", FOLDERID_System},                 {L"{CommonAppData}\\", FOLDERID_ProgramData},
                                  {L"{LocalAppData}\\", FOLDERID_LocalAppData},     {L"{AppData}\\", FOLDERID_RoamingAppData}};
    for (const auto& r : kRoots) {
        const size_t len = wcslen(r.token);
        if (tpl.compare(0, len, r.token) != 0) continue;
        std::wstring rel;
        if (!path::ValidateRelative(tpl.substr(len), rel)) return {};
        const std::wstring base = path::KnownFolder(r.id);
        return base.empty() ? std::wstring() : path::Join(base, rel);
    }
    return {};
}

bool OptionSelected(const Extensions& e, const std::string& ifOption, const std::map<std::string, bool>& choices) {
    if (ifOption.empty()) return true;
    bool selected = false, required = false;
    for (const auto& o : e.options) {
        if (o.id == ifOption) selected = o.defaultValue, required = o.required;
    }
    if (auto it = choices.find(ifOption); it != choices.end() && !required) selected = it->second;
    return selected;
}

std::wstring CheckInputValue(const InputDef& input, const std::wstring& value) {
    if (value.size() > 4096) return L"too long";
    if (value.empty()) return input.required ? L"required" : std::wstring();
    if (input.type == InputDef::Type::Choice &&
        std::none_of(input.choices.begin(), input.choices.end(), [&](const InputChoice& c) { return c.value == value; })) {
        return L"not one of the choices";
    }
    if (!input.pattern.empty()) {
        try {
            if (!std::regex_search(value, std::wregex(input.pattern, std::regex_constants::ECMAScript))) return L"does not match the expected format";
        } catch (const std::regex_error&) {
            return L"does not match the expected format";
        }
    }
    return {};
}

std::wstring ExpandPlaceholders(const std::wstring& text, const PlaceholderContext& ctx) {
    std::wstring out;
    out.reserve(text.size());
    for (size_t i = 0; i < text.size();) {
        if (text[i] == L'{') {
            const size_t close = text.find(L'}', i);
            if (close != std::wstring::npos) {
                const std::wstring token = text.substr(i + 1, close - i - 1);
                const std::wstring* value = nullptr;
                std::wstring input;
                if (token == L"InstallDir") value = &ctx.installDir;
                else if (token == L"MainExecutable") value = &ctx.mainExecutable;
                else if (token == L"AppVersion") value = &ctx.appVersion;
                else if (token == L"AppId") value = &ctx.appId;
                else if (token.rfind(L"Input:", 0) == 0 && ctx.inputs) {
                    if (auto it = ctx.inputs->find(WideToUtf8(token.substr(6))); it != ctx.inputs->end()) input = it->second;
                    value = &input;
                }
                if (value) {
                    out += *value;
                    i = close + 1;
                    continue;
                }
            }
        }
        out.push_back(text[i++]);
    }
    return out;
}

bool IsEnvironmentName(const std::wstring& name) {
    if (name.empty() || name.size() > 128 || !((name[0] >= L'A' && name[0] <= L'Z') || (name[0] >= L'a' && name[0] <= L'z') || name[0] == L'_')) return false;
    if (!std::all_of(name.begin(), name.end(), [](wchar_t c) { return IsAsciiAlnum(c) || c == L'_' || c == L'.' || c == L'-' || c == L'(' || c == L')'; })) return false;
    static const wchar_t* const kReserved[] = {
        L"Path", L"PATHEXT", L"SystemRoot", L"windir", L"TEMP", L"TMP", L"ComSpec", L"USERPROFILE", L"APPDATA", L"LOCALAPPDATA",
        L"ProgramData", L"ProgramFiles", L"ProgramFiles(x86)", L"ProgramW6432", L"OS", L"NUMBER_OF_PROCESSORS", L"PSModulePath",
        L"USERNAME", L"USERDOMAIN", L"HOMEDRIVE", L"HOMEPATH", L"PUBLIC", L"ALLUSERSPROFILE", L"SystemDrive", L"CommonProgramFiles",
        L"CommonProgramFiles(x86)", L"CommonProgramW6432", L"DriverData", L"COMPUTERNAME", L"LOGONSERVER", L"SESSIONNAME"};
    if (std::any_of(std::begin(kReserved), std::end(kReserved), [&](const wchar_t* r) { return EqualsIgnoreCase(name, r); })) return false;
    return !StartsWithIgnoreCase(name, L"PROCESSOR_");
}

bool IsServiceName(const std::wstring& name) {
    return !name.empty() && name.size() <= 80 &&
           std::all_of(name.begin(), name.end(), [](wchar_t c) { return IsAsciiAlnum(c) || c == L'_' || c == L'.' || c == L'-'; });
}

bool IsOwnedRegistryKey(const std::wstring& key, bool tree) {
    if (key.empty() || key.size() > 255 || key.front() == L'\\' || key.back() == L'\\') return false;
    std::vector<std::wstring> parts;
    for (size_t start = 0; start <= key.size();) {
        size_t end = key.find(L'\\', start);
        if (end == std::wstring::npos) end = key.size();
        parts.push_back(key.substr(start, end - start));
        start = end + 1;
    }
    for (const auto& p : parts) {
        if (p.empty() || p.size() > 128 || std::any_of(p.begin(), p.end(), [](wchar_t c) { return c < 0x20; })) return false;
    }
    if (parts.size() < 2 || !EqualsIgnoreCase(parts[0], L"Software")) return false;
    if (EqualsIgnoreCase(parts[1], L"Classes")) {
        if (parts.size() < 3) return false;
        const std::wstring& name = parts[2];
        if (name.front() == L'.') return !tree && IsAssociableExtension(ToLower(name));  // shared: values only
        if (!IsProgId(name) && !IsCustomScheme(name)) return false;
        return !tree || parts.size() == 3;
    }
    if (EqualsIgnoreCase(parts[1], L"RegisteredApplications")) return parts.size() == 2 && !tree;
    if (EqualsIgnoreCase(parts[1], L"FluentSetup")) {
        return parts.size() >= 4 && EqualsIgnoreCase(parts[2], L"Capabilities") && (!tree || parts.size() == 4);
    }
    if (EqualsIgnoreCase(parts[1], L"Microsoft")) {
        // Software\Microsoft\Windows\CurrentVersion\App Paths\<app>.exe
        return parts.size() == 6 && EqualsIgnoreCase(parts[2], L"Windows") && EqualsIgnoreCase(parts[3], L"CurrentVersion") &&
               EqualsIgnoreCase(parts[4], L"App Paths") && IsExeName(parts[5]);
    }
    return IsRemovableRegistryKey(L"HKCU\\" + key);
}

bool IsCreatableRegistryKey(const std::wstring& key) {
    if (IsOwnedRegistryKey(key, true)) return true;
    static constexpr wchar_t kClasses[] = L"Software\\Classes\\";
    if (!StartsWithIgnoreCase(key, kClasses)) return false;
    const std::wstring name = key.substr(wcslen(kClasses));
    return name.find(L'\\') == std::wstring::npos && IsAssociableExtension(ToLower(name));
}

std::vector<std::wstring> LockingApps(const std::vector<std::wstring>& files) {
    std::vector<std::wstring> names;
    if (files.empty()) return names;
    DWORD session = 0;
    WCHAR sessionKey[CCH_RM_SESSION_KEY + 1] = {};
    if (::RmStartSession(&session, 0, sessionKey) != ERROR_SUCCESS) return names;
    std::vector<LPCWSTR> list;
    for (const auto& f : files) list.push_back(f.c_str());
    for (size_t i = 0; i < list.size(); i += 1000) {
        const UINT n = static_cast<UINT>(std::min<size_t>(1000, list.size() - i));
        ::RmRegisterResources(session, n, list.data() + i, 0, nullptr, 0, nullptr);
    }
    UINT needed = 0, count = 0;
    DWORD reasons = 0;
    std::vector<RM_PROCESS_INFO> info;
    DWORD rc = ERROR_MORE_DATA;
    for (int attempt = 0; attempt < 3 && rc == ERROR_MORE_DATA; ++attempt) {
        info.resize(needed + 8);
        count = static_cast<UINT>(info.size());
        rc = ::RmGetList(session, &needed, &count, info.data(), &reasons);
    }
    if (rc == ERROR_SUCCESS) {
        for (UINT i = 0; i < count; ++i) {
            if (info[i].Process.dwProcessId == ::GetCurrentProcessId()) continue;
            std::wstring name = info[i].strAppName;
            if (name.empty()) continue;
            if (std::none_of(names.begin(), names.end(), [&](const std::wstring& n) { return EqualsIgnoreCase(n, name); })) names.push_back(std::move(name));
        }
    }
    ::RmEndSession(session);
    return names;
}

LocalizedText ParseLocalizedText(const json::Value& v) { return ParseText(v); }

std::string ClosestLanguage(const std::string& language, const std::vector<std::string>& available) {
    const auto lower = [](std::string s) {
        for (auto& c : s) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c);
        return s;
    };
    const std::string want = lower(language);
    for (const auto& a : available) {
        if (lower(a) == want) return a;
    }
    const auto primary = [](const std::string& s) { return s.substr(0, s.find('-')); };
    const auto traditional = [](const std::string& s) {
        return s == "zh-tw" || s == "zh-hk" || s == "zh-mo" || s.rfind("zh-hant", 0) == 0;
    };
    std::string sameLanguage;
    for (const auto& a : available) {
        const std::string tag = lower(a);
        if (primary(tag) != primary(want)) continue;
        if (primary(want) == "zh" && traditional(tag) == traditional(want)) return a;  // same script first
        if (sameLanguage.empty()) sameLanguage = a;
    }
    return sameLanguage;
}

std::wstring LocalizedText::Get(const std::wstring& language, const std::wstring& fallback) const {
    std::vector<std::string> tags;
    for (const auto& [tag, text] : byLanguage) {
        if (tag != "*" && !text.empty()) tags.push_back(tag);
    }
    if (const std::string tag = ClosestLanguage(WideToUtf8(language), tags); !tag.empty()) return byLanguage.at(tag);
    if (auto it = byLanguage.find("*"); it != byLanguage.end()) return it->second;
    if (auto it = byLanguage.find("en-US"); it != byLanguage.end()) return it->second;
    return fallback;
}

Extensions ParseExtensions(const json::Value& root, const std::function<bool(const std::wstring&)>& payloadHas) {
    Extensions e;
    const json::Value& inst = root["install"];
    for (const auto& p : inst["closeProcesses"].array()) {
        const std::wstring n = p.asWide();
        if (!IsExeName(n)) Bad(L"closeProcesses entries must be file names ending in .exe: " + n);
        e.closeProcesses.push_back(n);
    }
    if (e.closeProcesses.size() > 32) Bad(L"too many closeProcesses entries");
    e.adoptUninstallKey = inst["adoptUninstallKey"].asWide();
    if (!e.adoptUninstallKey.empty() && !ValidTaskOrValueName(e.adoptUninstallKey)) Bad(L"invalid adoptUninstallKey");
    ParseNames(inst["legacyUninstallKeys"], e.legacyUninstallKeys, ValidTaskOrValueName, L"legacyUninstallKeys");
    if (const json::Value& cc = inst["closeCommand"]; cc.isObject()) {
        // Runs a file of the *installed* app (checked at run time like an action).
        if (!path::ValidateRelative(cc["run"].asWide(), e.closeCommand) || !IsExeName(path::FileName(e.closeCommand))) Bad(L"invalid closeCommand");
        e.closeCommandArgs = ParseArgs(cc["args"]);
    }
    for (const auto& n : inst["ngen"].array()) {
        std::wstring rel;
        if (!path::ValidateRelative(n.asWide(), rel) || !payloadHas(rel)) Bad(L"ngen entries must be payload files: " + n.asWide());
        e.ngen.push_back(rel);
    }
    if (e.ngen.size() > 16) Bad(L"too many ngen entries");
    const json::Value& ic = inst["cleanup"];
    ParseNames(ic["removeScheduledTasks"], e.installCleanup.removeScheduledTasks, ValidTaskOrValueName, L"scheduled task name");
    ParseNames(ic["removeRunValues"], e.installCleanup.removeRunValues, ValidTaskOrValueName, L"Run value name");
    ParseNames(ic["removeShortcuts"], e.installCleanup.removeShortcuts, IsShortcutName, L"shortcut name");
    ParseNames(ic["removeUninstallKeys"], e.installCleanup.removeUninstallKeys, ValidTaskOrValueName, L"uninstall key");
    ParseNames(ic["removeFiles"], e.installCleanup.removeFiles, IsFilePattern, L"removeFiles pattern");
    e.startMenuFolder = inst["startMenuFolder"].asWide();
    if (!e.startMenuFolder.empty() && (!path::IsValidComponent(e.startMenuFolder) || e.startMenuFolder.size() > 64)) Bad(L"invalid startMenuFolder");

    for (const auto& r : root["requirements"].array()) {
        Requirement req;
        const std::string& type = r["type"].asString();
        req.name = r["name"].asWide().substr(0, 128);
        if (type == "netfx") {
            req.kind = Requirement::Kind::NetFramework;
            req.minRelease = static_cast<uint32_t>(r["minRelease"].asUint(0));
            if (req.minRelease == 0) Bad(L"netfx requirement without minRelease");
        } else if (type == "dotnet") {
            req.kind = Requirement::Kind::DotNet;
            req.runtime = r["runtime"].asWide();
            if (req.runtime != L"Microsoft.NETCore.App" && req.runtime != L"Microsoft.WindowsDesktop.App" && req.runtime != L"Microsoft.AspNetCore.App") {
                Bad(L"unknown .NET runtime: " + req.runtime);
            }
            const std::string& rf = r["rollForward"].asString();
            req.rollForward = rf == "patch" ? Requirement::RollForward::Patch : rf == "major" ? Requirement::RollForward::Major : Requirement::RollForward::Minor;
        } else if (type == "webview2") {
            req.kind = Requirement::Kind::WebView2;
        } else if (type == "windowsAppRuntime") {
            // Framework package of the Windows App SDK: 2.x is one family per major version ("2"), 1.x per minor ("1.6").
            req.kind = Requirement::Kind::WindowsAppRuntime;
            const std::wstring channel = r["channel"].asWide();
            if (channel.empty() || channel.size() > 8 || channel.find_first_not_of(L"0123456789.") != std::wstring::npos) Bad(L"invalid Windows App Runtime channel");
            req.runtime = L"Microsoft.WindowsAppRuntime." + channel + L"_8wekyb3d8bbwe";
        } else if (type == "vcredist") {
            req.kind = Requirement::Kind::VcRedist;
            req.architecture = r["architecture"].asWide();
            if (req.architecture != L"x64" && req.architecture != L"x86" && req.architecture != L"arm64") Bad(L"invalid VC++ redistributable architecture");
        } else if (type == "custom") {
            req.kind = Requirement::Kind::Custom;
            const json::Value& detect = r["detect"];
            if (const json::Value& reg = detect["registry"]; reg.isObject()) {
                RegistryVersionCondition c;
                c.hive = ParseHive(reg["hive"].asString());
                c.key = reg["key"].asWide();
                c.value = reg["value"].asWide();
                c.anyVersion = reg["version"].asString().empty();
                if (!c.hive || c.key.empty() || c.key.size() > 255 || c.key.front() == L'\\' || c.value.size() > 128 ||
                    (!c.anyVersion && !Version::Parse(reg["version"].asWide(), c.atLeast))) {
                    Bad(L"invalid custom requirement registry detection");
                }
                req.detectRegistry = c;
            } else if (const json::Value& file = detect["file"]; file.isObject()) {
                req.detectFile = file["path"].asWide();
                if (ExpandSystemTemplate(req.detectFile).empty()) Bad(L"invalid custom requirement file detection: " + req.detectFile);
                if (!file["version"].asString().empty()) {
                    Version v;
                    if (!Version::Parse(file["version"].asWide(), v)) Bad(L"invalid custom requirement file version");
                    req.detectFileVersion = v;
                }
            } else {
                Bad(L"custom requirement without detection");
            }
            if (req.name.empty()) Bad(L"custom requirement without a name");
        } else {
            Bad(L"unknown requirement type");
        }
        if (r["minVersion"].isString()) {
            Version v;
            if (!Version::Parse(r["minVersion"].asWide(), v)) Bad(L"invalid requirement minVersion");
            req.minVersion = v;
            req.minVersionText = r["minVersion"].asWide().substr(0, 64);
        }
        if (req.kind == Requirement::Kind::DotNet && !req.minVersion) Bad(L".NET requirement without minVersion");
        if (req.kind == Requirement::Kind::WindowsAppRuntime && req.name.empty()) req.name = L"Windows App Runtime " + r["channel"].asWide();
        if (r["installer"].isString()) {
            std::wstring why;
            if (!path::ValidateRelative(r["installer"].asWide(), req.installer, &why) || !payloadHas(req.installer)) {
                Bad(L"requirement installer must be a payload file");
            }
        }
        if (r["bundled"].isString()) {
            // Microsoft's installer stored in the package's redist set (checked against that set by ParsePackage).
            if (!path::ValidateRelative(r["bundled"].asWide(), req.bundled) || !IsExeName(path::FileName(req.bundled))) Bad(L"invalid bundled prerequisite installer");
        }
        req.installerArgs = ParseArgs(r["args"]);
        if (const json::Value& d = r["download"]; d.isObject()) {
            req.downloadUrl = d["url"].asString();
            std::vector<uint8_t> digest;
            if (req.downloadUrl.size() > 2048 || req.downloadUrl.rfind("https://", 0) != 0) Bad(L"requirement downloads must use https");
            req.downloadSigner = d["signer"].asWide();
            if (req.downloadSigner.size() > 256) Bad(L"invalid download signer");
            if (!d["sha256"].asString().empty() || req.downloadSigner.empty()) {
                // Without a pinned signer the file itself is pinned.
                if (!FromHex(d["sha256"].asString(), digest) || digest.size() != req.downloadSha256.size()) Bad(L"requirement download needs a sha256 or a signer");
                std::copy(digest.begin(), digest.end(), req.downloadSha256.begin());
                req.hasDownloadSha256 = true;
            }
            req.downloadSize = d["size"].asUint(0);
        }
        req.mustSucceed = r["onFailure"].asString() == "fail";
        for (const auto& v : r["variants"].array()) {
            if (!v.isString() || v.asString().empty() || v.asString().size() > 32 || req.variants.size() >= 8) Bad(L"invalid requirement variants");
            req.variants.push_back(v.asString());
        }
        req.helpUrl = r["helpUrl"].asString();
        if (req.helpUrl.size() > 2048 || (!req.helpUrl.empty() && req.helpUrl.rfind("https://", 0) != 0)) Bad(L"helpUrl must be https");
        if (req.name.empty()) {
            req.name = req.kind == Requirement::Kind::WebView2 ? L"Microsoft Edge WebView2 Runtime"
                     : req.kind == Requirement::Kind::VcRedist ? L"Microsoft Visual C++ Redistributable (" + req.architecture + L")"
                     : req.kind == Requirement::Kind::DotNet ? (req.runtime == L"Microsoft.WindowsDesktop.App" ? L".NET Desktop Runtime"
                                                                : req.runtime == L"Microsoft.AspNetCore.App" ? L"ASP.NET Core Runtime" : L".NET Runtime")
                                                             : L".NET Framework";
        }
        e.requirements.push_back(std::move(req));
    }

    std::unordered_set<std::string> optionIds;
    for (const auto& o : root["options"].array()) {
        UserOption opt;
        opt.id = o["id"].asString();
        if (opt.id.empty() || opt.id.size() > 32 ||
            !std::all_of(opt.id.begin(), opt.id.end(), [](char c) { return isalnum(static_cast<unsigned char>(c)) || c == '_' || c == '-'; })) {
            Bad(L"invalid option id");
        }
        if (!optionIds.insert(opt.id).second) Bad(L"duplicate option id");
        opt.label = ParseText(o["label"]);
        opt.required = o["required"].asBool(false);
        if (const json::Value& s = o["startup"]; s.isObject()) {
            opt.startupName = s["name"].asWide();
            if (!ValidTaskOrValueName(opt.startupName)) Bad(L"invalid option startup name");
            opt.startupArgs = ParseArgs(s["args"]);
        }
        if (const json::Value& d = o["detect"]; d.isObject()) {
            opt.detectRunValue = d["runValue"].asWide();
            opt.detectScheduledTask = d["scheduledTask"].asWide();
            if ((!opt.detectRunValue.empty() && !ValidTaskOrValueName(opt.detectRunValue)) ||
                (!opt.detectScheduledTask.empty() && !ValidTaskPath(opt.detectScheduledTask))) {
                Bad(L"invalid option detect");
            }
        }
        if (o["quietDefault"].isBool()) opt.quietDefault = o["quietDefault"].asBool();
        opt.defaultValue = opt.required || o["default"].asBool(false);
        e.options.push_back(std::move(opt));
    }
    if (e.options.size() > 16) Bad(L"too many options");

    for (const auto& a : root["actions"].array()) {
        Action act;
        const std::string& when = a["when"].asString();
        if (when == "afterInstall") act.when = Action::When::AfterInstall;
        else if (when == "beforeUninstall") act.when = Action::When::BeforeUninstall;
        else Bad(L"unknown action time");
        std::wstring why;
        if (!path::ValidateRelative(a["run"].asWide(), act.run, &why)) Bad(L"unsafe action path: " + why);
        if (!IsExeName(path::FileName(act.run)) || !payloadHas(act.run)) Bad(L"actions may only run .exe files from the payload: " + act.run);
        act.args = ParseArgs(a["args"]);
        act.timeoutSeconds = static_cast<uint32_t>(std::clamp<uint64_t>(a["timeoutSeconds"].asUint(300), 1, 3600));
        if (a["successExitCodes"].isArray()) {
            act.successExitCodes.clear();
            for (const auto& c : a["successExitCodes"].array()) act.successExitCodes.push_back(c.asInt());
        }
        const std::string& onFailure = a["onFailure"].asString();
        act.onFailure = onFailure == "warn" ? Action::OnFailure::Warn : onFailure == "ignore" ? Action::OnFailure::Ignore : Action::OnFailure::Fail;
        act.ifOption = a["ifOption"].asString();
        if (!act.ifOption.empty() && !optionIds.count(act.ifOption)) Bad(L"action refers to an unknown option");
        const json::Value& cond = a["skipIfInstalled"];
        if (cond.isObject()) {
            RegistryVersionCondition c;
            c.hive = ParseHive(cond["hive"].asString());
            c.key = cond["key"].asWide();
            c.value = cond["value"].asWide();
            if (!c.hive || c.key.empty() || c.key.size() > 255 || c.key.front() == L'\\' || c.value.size() > 128 ||
                !Version::Parse(cond["version"].asWide(), c.atLeast)) {
                Bad(L"invalid skipIfInstalled condition");
            }
            act.skipIfInstalled = c;
        }
        act.name = ParseText(a["name"]);
        if (a["fallbackArgs"].isArray()) act.fallbackArgs = ParseArgs(a["fallbackArgs"]);
        if (const json::Value& rv = a["fallbackRunValue"]; rv.isObject()) {
            act.fallbackRunValue = rv["name"].asWide();
            if (!ValidTaskOrValueName(act.fallbackRunValue)) Bad(L"invalid fallbackRunValue name");
            act.fallbackRunArgs = ParseArgs(rv["args"]);
        }
        e.actions.push_back(std::move(act));
    }
    if (e.actions.size() > 32) Bad(L"too many actions");

    // ---- 1.2 sections ------------------------------------------------------------------------------------------
    const auto checkOption = [&](const json::Value& v, const wchar_t* what) {
        std::string id = v["ifOption"].asString();
        if (!id.empty() && !optionIds.count(id)) Bad(std::wstring(what) + L" refers to an unknown option");
        return id;
    };
    const auto payloadFile = [&](const json::Value& v, const char* key, bool required, const wchar_t* what) {
        std::wstring rel;
        const std::wstring raw = v[key].asWide();
        if (raw.empty() && !required) return rel;
        if (!path::ValidateRelative(raw, rel) || !payloadHas(rel)) Bad(std::wstring(what) + L" must be a file among the installed files: " + raw);
        return rel;
    };
    const auto text = [&](const json::Value& v, const char* key, size_t max, const wchar_t* what) {
        std::wstring s = v[key].asWide();
        if (s.size() > max || std::any_of(s.begin(), s.end(), [](wchar_t c) { return c < 0x20; })) Bad(std::wstring(L"invalid ") + what);
        return s;
    };
    const json::Value& app = root["app"];
    e.appUserModelId = app["appUserModelId"].asWide();
    if (!e.appUserModelId.empty() && !IsAppUserModelId(e.appUserModelId)) Bad(L"invalid appUserModelId");
    e.toastActivatorClsid = app["toastActivatorClsid"].asWide();
    if (!e.toastActivatorClsid.empty()) {
        CLSID clsid{};
        if (e.toastActivatorClsid.size() != 38 || FAILED(::CLSIDFromString(e.toastActivatorClsid.c_str(), &clsid))) Bad(L"invalid toastActivatorClsid");
    }
    e.appPaths = inst["appPaths"].asBool(false);
    e.restartManager = inst["restartManager"].asString() != "off";
    e.rebootForLockedFiles = inst["lockedFiles"].asString() == "reboot";

    if (root["shortcuts"].array().size() > 16) Bad(L"too many shortcuts");
    for (const auto& s : root["shortcuts"].array()) {
        ShortcutDef d;
        d.name = s["name"].asWide();
        if (d.name.empty() || d.name.size() > 128 || !path::IsValidComponent(d.name + L".lnk")) Bad(L"invalid shortcut name: " + d.name);
        d.target = payloadFile(s, "target", true, L"shortcut target");
        d.args = ParseArgs(s["args"]);
        d.description = text(s, "description", 259, L"shortcut description");
        d.icon = payloadFile(s, "icon", false, L"shortcut icon");
        d.iconIndex = static_cast<int>(std::clamp<int64_t>(s["iconIndex"].asInt(0), -65535, 65535));
        if (!s["workingDir"].asWide().empty() && !path::ValidateRelative(s["workingDir"].asWide(), d.workingDir)) Bad(L"invalid shortcut working folder");
        const std::string& location = s["location"].asString();
        d.location = location == "desktop" ? ShortcutDef::Location::Desktop : location == "startup" ? ShortcutDef::Location::Startup : ShortcutDef::Location::StartMenu;
        if (location != "desktop" && location != "startup" && location != "startMenu" && !location.empty()) Bad(L"invalid shortcut location");
        d.folder = s["folder"].asWide();
        if (!d.folder.empty() && (!path::IsValidComponent(d.folder) || d.folder.size() > 64)) Bad(L"invalid shortcut folder");
        d.ifOption = checkOption(s, L"shortcut");
        d.appUserModelId = s["appUserModelId"].asWide();
        if (!d.appUserModelId.empty() && !IsAppUserModelId(d.appUserModelId)) Bad(L"invalid shortcut appUserModelId");
        e.shortcuts.push_back(std::move(d));
    }

    if (root["associations"].array().size() > 16) Bad(L"too many file associations");
    for (const auto& a : root["associations"].array()) {
        AssociationDef d;
        if (a["extensions"].array().empty() || a["extensions"].array().size() > 16) Bad(L"file association without extensions");
        for (const auto& x : a["extensions"].array()) {
            const std::wstring ext = ToLower(x.asWide());
            if (!IsAssociableExtension(ext)) Bad(L"invalid or reserved file extension: " + ext);
            d.extensions.push_back(ext);
        }
        d.progId = a["progId"].asWide();
        if (!IsProgId(d.progId)) Bad(L"invalid ProgID: " + d.progId);
        d.description = text(a, "description", 256, L"file type description");
        d.icon = payloadFile(a, "icon", false, L"file type icon");
        d.iconIndex = static_cast<int>(std::clamp<int64_t>(a["iconIndex"].asInt(0), -65535, 65535));
        d.target = payloadFile(a, "target", true, L"file association target");
        if (!IsExeName(path::FileName(d.target))) Bad(L"file association target must be an .exe");
        d.args = ParseArgs(a["args"]);
        d.contentType = a["contentType"].asWide();
        if (d.contentType.size() > 128 || d.contentType.find_first_not_of(L"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$&^_.+-/") != std::wstring::npos) Bad(L"invalid content type");
        d.ifOption = checkOption(a, L"file association");
        e.associations.push_back(std::move(d));
    }

    if (root["protocols"].array().size() > 16) Bad(L"too many protocols");
    for (const auto& p : root["protocols"].array()) {
        ProtocolDef d;
        d.scheme = p["scheme"].asWide();
        if (!IsCustomScheme(d.scheme)) Bad(L"invalid or reserved URL scheme: " + d.scheme);
        d.description = text(p, "description", 256, L"protocol description");
        d.icon = payloadFile(p, "icon", false, L"protocol icon");
        d.iconIndex = static_cast<int>(std::clamp<int64_t>(p["iconIndex"].asInt(0), -65535, 65535));
        d.target = payloadFile(p, "target", true, L"protocol target");
        if (!IsExeName(path::FileName(d.target))) Bad(L"protocol target must be an .exe");
        d.args = ParseArgs(p["args"]);
        d.ifOption = checkOption(p, L"protocol");
        e.protocols.push_back(std::move(d));
    }

    if (root["registry"].array().size() > 32) Bad(L"too many registry keys");
    for (const auto& r : root["registry"].array()) {
        RegistryEntryDef d;
        d.key = r["key"].asWide();
        if (!IsRemovableRegistryKey(L"HKCU\\" + d.key) || EqualsIgnoreCase(d.key.substr(0, 21), L"Software\\FluentSetup\\") ||
            EqualsIgnoreCase(d.key, L"Software\\FluentSetup")) {
            Bad(L"registry keys must be Software\\<Vendor>\\... : " + d.key);
        }
        const std::string& uninstall = r["uninstall"].asString();
        d.uninstall = uninstall == "key" ? RegistryEntryDef::Uninstall::Key : uninstall == "keep" ? RegistryEntryDef::Uninstall::Keep : RegistryEntryDef::Uninstall::Values;
        if (r["values"].array().size() > 32) Bad(L"too many registry values");
        for (const auto& v : r["values"].array()) {
            RegistryValueDef val;
            val.name = text(v, "name", 255, L"registry value name");
            const std::string& t = v["type"].asString();
            val.type = t == "expandString" ? REG_EXPAND_SZ : t == "dword" ? REG_DWORD : t == "qword" ? REG_QWORD : t == "multiString" ? REG_MULTI_SZ : REG_SZ;
            if (!t.empty() && t != "string" && val.type == REG_SZ) Bad(L"invalid registry value type");
            val.data = v["data"].asWide();
            if (val.data.size() > 16384) Bad(L"registry value too long");
            if ((val.type == REG_DWORD || val.type == REG_QWORD) && val.data.find(L'{') == std::wstring::npos) {
                uint64_t n = 0;
                if (!ParseRegistryNumber(val.data, val.type == REG_QWORD, n)) Bad(L"invalid registry number: " + val.data);
            }
            d.values.push_back(std::move(val));
        }
        d.ifOption = checkOption(r, L"registry key");
        e.registry.push_back(std::move(d));
    }

    if (root["environment"].array().size() > 16) Bad(L"too many environment variables");
    for (const auto& v : root["environment"].array()) {
        EnvironmentDef d;
        d.name = v["name"].asWide();
        if (!IsEnvironmentName(d.name)) Bad(L"invalid or reserved environment variable: " + d.name);
        d.value = v["value"].asWide();
        if (d.value.size() > 8192 || d.value.find(L'\0') != std::wstring::npos) Bad(L"environment value too long");
        d.expand = v["expand"].asBool(false);
        d.ifOption = checkOption(v, L"environment variable");
        e.environment.push_back(std::move(d));
    }

    if (root["services"].array().size() > 8) Bad(L"too many services");
    for (const auto& s : root["services"].array()) {
        ServiceDef d;
        d.name = s["name"].asWide();
        if (!IsServiceName(d.name)) Bad(L"invalid service name: " + d.name);
        d.displayName = text(s, "displayName", 256, L"service display name");
        if (d.displayName.empty()) d.displayName = d.name;
        d.description = text(s, "description", 1024, L"service description");
        d.run = payloadFile(s, "run", true, L"service program");
        if (!IsExeName(path::FileName(d.run))) Bad(L"service program must be an .exe");
        d.args = ParseArgs(s["args"]);
        const std::string& st = s["startType"].asString();
        d.startType = st == "manual" ? SERVICE_DEMAND_START : st == "disabled" ? SERVICE_DISABLED : SERVICE_AUTO_START;
        d.delayed = st == "delayedAuto";
        const std::string& account = s["account"].asString();
        d.account = account == "localService" ? ServiceDef::Account::LocalService : account == "networkService" ? ServiceDef::Account::NetworkService : ServiceDef::Account::LocalSystem;
        d.start = s["start"].asBool(true);
        d.restartOnFailure = s["restartOnFailure"].asBool(true);
        if (s["dependencies"].array().size() > 16) Bad(L"too many service dependencies");
        for (const auto& dep : s["dependencies"].array()) {
            if (!IsServiceName(dep.asWide())) Bad(L"invalid service dependency");
            d.dependencies.push_back(dep.asWide());
        }
        e.services.push_back(std::move(d));
    }

    if (root["firewall"].array().size() > 16) Bad(L"too many firewall rules");
    for (const auto& f : root["firewall"].array()) {
        FirewallRuleDef d;
        d.name = text(f, "name", 128, L"firewall rule name");
        if (d.name.empty() || d.name.find(L'|') != std::wstring::npos) Bad(L"invalid firewall rule name");
        d.program = payloadFile(f, "program", true, L"firewall program");
        if (!IsExeName(path::FileName(d.program))) Bad(L"firewall program must be an .exe");
        d.inbound = f["direction"].asString() != "out";
        const std::string& protocol = f["protocol"].asString();
        d.protocol = protocol == "tcp" ? 6 : protocol == "udp" ? 17 : 256;
        d.localPorts = f["localPorts"].asWide();
        if (!d.localPorts.empty() && (d.protocol == 256 || !IsPortList(d.localPorts))) Bad(L"invalid firewall ports (tcp or udp only): " + d.localPorts);
        if (f["profiles"].isArray() && !f["profiles"].array().empty()) {
            d.profiles = 0;
            for (const auto& p : f["profiles"].array()) {
                const std::string& name = p.asString();
                if (name == "domain") d.profiles |= 1;
                else if (name == "private") d.profiles |= 2;
                else if (name == "public") d.profiles |= 4;
                else Bad(L"invalid firewall profile");
            }
        }
        d.ifOption = checkOption(f, L"firewall rule");
        e.firewall.push_back(std::move(d));
    }

    std::unordered_set<std::string> inputIds;
    if (root["inputs"].array().size() > 8) Bad(L"too many inputs");
    for (const auto& i : root["inputs"].array()) {
        InputDef d;
        d.id = i["id"].asString();
        if (d.id.empty() || d.id.size() > 32 ||
            !std::all_of(d.id.begin(), d.id.end(), [](char c) { return isalnum(static_cast<unsigned char>(c)) || c == '_' || c == '-'; })) {
            Bad(L"invalid input id");
        }
        if (!inputIds.insert(d.id).second) Bad(L"duplicate input id");
        d.label = ParseText(i["label"]);
        d.description = ParseText(i["description"]);
        const std::string& type = i["type"].asString();
        d.type = type == "password" ? InputDef::Type::Password : type == "choice" ? InputDef::Type::Choice : InputDef::Type::Text;
        d.defaultValue = i["default"].asWide().substr(0, 4096);
        d.required = i["required"].asBool(false);
        d.pattern = i["pattern"].asWide();
        if (d.pattern.size() > 512) Bad(L"input pattern too long");
        if (!d.pattern.empty()) {
            try {
                std::wregex compiled(d.pattern, std::regex_constants::ECMAScript);
            } catch (const std::regex_error&) {
                Bad(L"invalid input pattern");
            }
        }
        d.remember = i["remember"].asBool(true) && d.type != InputDef::Type::Password;
        if (i["choices"].array().size() > 32) Bad(L"too many input choices");
        for (const auto& c : i["choices"].array()) {
            InputChoice choice{c["value"].asWide().substr(0, 1024), ParseText(c["label"])};
            d.choices.push_back(std::move(choice));
        }
        if (d.type == InputDef::Type::Choice && d.choices.empty()) Bad(L"choice input without choices");
        e.inputs.push_back(std::move(d));
    }

    if (root["configFiles"].array().size() > 16) Bad(L"too many config files");
    for (const auto& c : root["configFiles"].array()) {
        ConfigFileDef d;
        const std::wstring raw = c["path"].asWide();
        if (raw.rfind(L'{', 0) == 0) {
            if (raw.rfind(L"{InstallDir}", 0) == 0 || ExpandDataTemplate(raw).empty()) Bad(L"invalid config file path: " + raw);
            d.path = raw;
        } else {
            if (!path::ValidateRelative(raw, d.path) || EqualsIgnoreCase(d.path.substr(0, d.path.find(L'\\')), kWorkDirName) ||
                EqualsIgnoreCase(d.path, L"uninstall.exe") || EqualsIgnoreCase(d.path, L"uninstall.json")) {
                Bad(L"invalid config file path: " + raw);
            }
        }
        d.ini = c["format"].asString() == "ini";
        if (c["values"].array().empty() || c["values"].array().size() > 64) Bad(L"config file without values");
        for (const auto& v : c["values"].array()) {
            ConfigValueDef val;
            val.key = text(v, "key", 256, L"config key");
            val.section = text(v, "section", 256, L"config section");
            if (val.key.empty() || (d.ini && (val.section.empty() || val.key.find(L'=') != std::wstring::npos)) ||
                (!d.ini && (val.key.front() == L'.' || val.key.back() == L'.' || val.key.find(L"..") != std::wstring::npos))) {
                Bad(L"invalid config key: " + val.key);
            }
            val.value = v["value"].asWide();
            if (val.value.size() > 4096) Bad(L"config value too long");
            const std::string& t = v["type"].asString();
            val.type = t == "number" ? ConfigValueDef::Type::Number : t == "bool" ? ConfigValueDef::Type::Bool : ConfigValueDef::Type::String;
            d.values.push_back(std::move(val));
        }
        d.ifOption = checkOption(c, L"config file");
        e.configFiles.push_back(std::move(d));
    }

    const json::Value& u = root["uninstall"];
    for (const auto& d : u["removeData"].array()) {
        if (!IsDataTemplate(d.asWide())) Bad(L"removeData entries must be {CommonAppData}|{LocalAppData}|{AppData}|{InstallDir}\\<folder>: " + d.asWide());
        e.cleanup.removeData.push_back(d.asWide());
    }
    for (const auto& t : u["removeScheduledTasks"].array()) {
        if (!ValidTaskPath(t.asWide())) Bad(L"invalid scheduled task name");
        e.cleanup.removeScheduledTasks.push_back(t.asWide());
    }
    for (const auto& r : u["removeRunValues"].array()) {
        if (!ValidTaskOrValueName(r.asWide())) Bad(L"invalid Run value name");
        e.cleanup.removeRunValues.push_back(r.asWide());
    }
    ParseNames(u["removeShortcuts"], e.cleanup.removeShortcuts, IsShortcutName, L"shortcut name");
    ParseNames(u["removeRegistryKeys"], e.cleanup.removeRegistryKeys, IsRemovableRegistryKey, L"registry key");
    for (const auto& k : e.cleanup.removeRegistryKeys) {
        if (k.rfind(L"HKCU\\", 0) != 0) Bad(L"uninstall.removeRegistryKeys are per-user (HKCU) keys; use elevated.removeRegistryKeys for HKLM");
    }
    const json::Value& ec = u["elevated"];
    ParseNames(ec["ifExists"], e.elevatedCleanup.ifExists, IsElevatedDataTemplate, L"elevated.ifExists");
    ParseNames(ec["traceSessions"], e.elevatedCleanup.traceSessions, ValidTaskOrValueName, L"trace session name");
    ParseNames(ec["scheduledTasks"], e.elevatedCleanup.scheduledTasks, ValidTaskPath, L"scheduled task");
    ParseNames(ec["removeData"], e.elevatedCleanup.removeData, IsElevatedDataTemplate, L"elevated.removeData");
    ParseNames(ec["removeRegistryKeys"], e.elevatedCleanup.removeRegistryKeys, IsRemovableRegistryKey, L"elevated.removeRegistryKeys");
    for (const auto& s : ec["serviceAccess"].array()) {
        ServiceAccess access;
        access.service = s["service"].asWide();
        if (!ValidTaskOrValueName(access.service) || access.service.find(L' ') != std::wstring::npos) Bad(L"invalid service name");
        for (const auto& r : s["rights"].array()) {
            const std::wstring right = r.asWide();
            if (right.empty() || right.size() > 32 || right.find_first_not_of(L"ABCDEFGHIJKLMNOPQRSTUVWXYZ") != std::wstring::npos) Bad(L"invalid service right");
            access.rights.push_back(right);
        }
        if (access.rights.empty() || access.rights.size() > 8) Bad(L"serviceAccess needs rights");
        e.elevatedCleanup.serviceAccess.push_back(std::move(access));
    }
    if (e.elevatedCleanup.serviceAccess.size() > 8) Bad(L"too many serviceAccess entries");
    const json::Value& ud = u["userData"];
    ParseNames(ud["paths"], e.userData.paths, IsDataTemplate, L"userData path");
    e.userData.removeByDefault = ud["default"].asBool(false);
    e.relaunchArgs = ParseArgs(root["install"]["relaunchArgs"]);
    for (const auto& d : root["install"]["addToPath"].array()) {
        std::wstring rel;
        const std::wstring raw = d.asWide();
        if (!raw.empty() && raw != L"." && !path::ValidateRelative(raw, rel)) Bad(L"invalid addToPath entry: " + raw);
        e.addToPath.push_back(rel);
    }
    if (e.addToPath.size() > 8) Bad(L"too many addToPath entries");
    return e;
}

// ---- processes ---------------------------------------------------------------------------------------------------

namespace {

template <typename F>
void ForEachProcess(F&& f) {
    UniqueHandle snap(::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (!snap) return;
    PROCESSENTRY32W pe{sizeof(pe)};
    if (!::Process32FirstW(snap.get(), &pe)) return;
    do {
        f(pe);
    } while (::Process32NextW(snap.get(), &pe));
}

bool Matches(const std::vector<std::wstring>& names, const wchar_t* exe) {
    return std::any_of(names.begin(), names.end(), [&](const std::wstring& n) { return EqualsIgnoreCase(n, exe); });
}

}  // namespace

std::vector<std::wstring> RunningProcesses(const std::vector<std::wstring>& imageNames) {
    std::vector<std::wstring> found;
    if (imageNames.empty()) return found;
    ForEachProcess([&](const PROCESSENTRY32W& pe) {
        if (pe.th32ProcessID != ::GetCurrentProcessId() && Matches(imageNames, pe.szExeFile) &&
            std::none_of(found.begin(), found.end(), [&](const std::wstring& f) { return EqualsIgnoreCase(f, pe.szExeFile); })) {
            found.emplace_back(pe.szExeFile);
        }
    });
    return found;
}

std::vector<std::wstring> CloseProcesses(const std::vector<std::wstring>& imageNames, unsigned graceMs) {
    std::vector<std::wstring> failed;
    if (imageNames.empty()) return failed;
    std::vector<DWORD> pids;
    ForEachProcess([&](const PROCESSENTRY32W& pe) {
        if (pe.th32ProcessID != ::GetCurrentProcessId() && Matches(imageNames, pe.szExeFile)) pids.push_back(pe.th32ProcessID);
    });
    if (pids.empty()) return failed;
    log::Info(L"Closing %zu running process(es) of the app", pids.size());
    // 1. polite: WM_CLOSE to every top-level window they own
    ::EnumWindows(
        [](HWND h, LPARAM lp) -> BOOL {
            DWORD pid = 0;
            ::GetWindowThreadProcessId(h, &pid);
            const auto* list = reinterpret_cast<const std::vector<DWORD>*>(lp);
            if (std::find(list->begin(), list->end(), pid) != list->end()) ::PostMessageW(h, WM_CLOSE, 0, 0);
            return TRUE;
        },
        reinterpret_cast<LPARAM>(&pids));
    std::vector<UniqueHandle> handles;
    for (const DWORD pid : pids) {
        HANDLE h = ::OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (h) handles.emplace_back(h);
    }
    const ULONGLONG deadline = ::GetTickCount64() + graceMs;
    for (auto& h : handles) {
        const ULONGLONG now = ::GetTickCount64();
        ::WaitForSingleObject(h.get(), now < deadline ? static_cast<DWORD>(deadline - now) : 0);
    }
    // 2. force whatever is still running
    for (auto& h : handles) {
        if (::WaitForSingleObject(h.get(), 0) == WAIT_TIMEOUT) {
            ::TerminateProcess(h.get(), 1);
            ::WaitForSingleObject(h.get(), 5000);
        }
    }
    // Anything we could not even open (other user, protected) is reported.
    ForEachProcess([&](const PROCESSENTRY32W& pe) {
        if (std::find(pids.begin(), pids.end(), pe.th32ProcessID) != pids.end() && Matches(imageNames, pe.szExeFile)) {
            failed.emplace_back(pe.szExeFile);
        }
    });
    return failed;
}

// ---- requirements --------------------------------------------------------------------------------------------------

namespace {

std::wstring ReadRegString(HKEY hive, const std::wstring& key, const std::wstring& value, DWORD view) {
    wchar_t buffer[256] = {};
    DWORD size = sizeof(buffer);
    if (::RegGetValueW(hive, key.c_str(), value.c_str(), RRF_RT_REG_SZ | view, nullptr, buffer, &size) != ERROR_SUCCESS) return {};
    return buffer;
}

}  // namespace

namespace {

constexpr wchar_t kWebView2Client[] = L"SOFTWARE\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

std::wstring WebView2Version() {
    // Evergreen runtime: machine-wide (32-bit view) or per-user registration with a non-empty, non-zero "pv".
    for (const auto& [hive, view] : {std::pair<HKEY, DWORD>{HKEY_LOCAL_MACHINE, RRF_SUBKEY_WOW6432KEY},
                                     std::pair<HKEY, DWORD>{HKEY_LOCAL_MACHINE, RRF_SUBKEY_WOW6464KEY},
                                     std::pair<HKEY, DWORD>{HKEY_CURRENT_USER, 0}}) {
        const std::wstring pv = ReadRegString(hive, kWebView2Client, L"pv", view);
        if (!pv.empty() && pv != L"0.0.0.0") return pv;
    }
    return {};
}

// Highest installed version of a shared .NET runtime that satisfies the requirement ("" if none).
std::wstring DotNetVersion(const Requirement& r) {
#if defined(_M_ARM64)
    constexpr wchar_t kArch[] = L"arm64";
#else
    constexpr wchar_t kArch[] = L"x64";
#endif
    std::wstring root = ReadRegString(HKEY_LOCAL_MACHINE, std::wstring(L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\") + kArch,
                                      L"InstallLocation", RRF_SUBKEY_WOW6432KEY);
    if (root.empty()) root = path::Join(path::KnownFolder(FOLDERID_ProgramFiles), L"dotnet");
    const std::wstring dir = path::Join(path::Join(root, L"shared"), r.runtime);
    WIN32_FIND_DATAW fd{};
    UniqueFind find(::FindFirstFileExW(path::Join(dir, L"*").c_str(), FindExInfoBasic, &fd, FindExSearchLimitToDirectories, nullptr, 0));
    std::wstring best;
    Version bestV;
    for (bool more = find.valid(); more; more = ::FindNextFileW(find.get(), &fd)) {
        Version v;
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || !Version::Parse(fd.cFileName, v) || !r.minVersion) continue;
        const Version& min = *r.minVersion;
        if (Compare(v, min) < 0) continue;
        if (r.rollForward != Requirement::RollForward::Major && v.Major() != min.Major()) continue;
        if (r.rollForward == Requirement::RollForward::Patch && v.Minor() != min.Minor()) continue;
        if (best.empty() || Compare(v, bestV) > 0) best = fd.cFileName, bestV = v;
    }
    return best;
}

}  // namespace

// Highest registered version of a Windows App Runtime framework package for this architecture that satisfies the
// requirement ("" if none). Framework packages are registered per user; the installer registers them for the PC.
std::wstring WindowsAppRuntimeVersion(const Requirement& r) {
#if defined(_M_ARM64)
    constexpr UINT32 kArch = PROCESSOR_ARCHITECTURE_ARM64;
#else
    constexpr UINT32 kArch = PROCESSOR_ARCHITECTURE_AMD64;
#endif
    UINT32 count = 0, length = 0;
    if (::GetPackagesByPackageFamily(r.runtime.c_str(), &count, nullptr, &length, nullptr) != ERROR_INSUFFICIENT_BUFFER || count == 0) return {};
    std::vector<PWSTR> names(count);
    std::vector<wchar_t> buffer(length);
    if (::GetPackagesByPackageFamily(r.runtime.c_str(), &count, names.data(), &length, buffer.data()) != ERROR_SUCCESS) return {};
    std::wstring best;
    Version bestV;
    for (UINT32 i = 0; i < count; ++i) {
        UINT32 idLength = 0;
        if (::PackageIdFromFullName(names[i], PACKAGE_INFORMATION_BASIC, &idLength, nullptr) != ERROR_INSUFFICIENT_BUFFER) continue;
        auto idBuffer = std::make_unique<BYTE[]>(idLength);
        if (::PackageIdFromFullName(names[i], PACKAGE_INFORMATION_BASIC, &idLength, idBuffer.get()) != ERROR_SUCCESS) continue;
        const auto* id = reinterpret_cast<const PACKAGE_ID*>(idBuffer.get());
        if (id->processorArchitecture != kArch) continue;
        const std::wstring text = Printf(L"%u.%u.%u.%u", id->version.Major, id->version.Minor, id->version.Build, id->version.Revision);
        Version v;
        if (!Version::Parse(text, v) || (r.minVersion && Compare(v, *r.minVersion) < 0)) continue;
        if (best.empty() || Compare(v, bestV) > 0) best = text, bestV = v;
    }
    return best;
}

namespace {

// Visual C++ 2015-2022 (v14) redistributable: VisualStudio\14.0\VC\Runtimes\<arch> with Installed = 1 and
// Version = "v14.40.33810.00" (registered in the 32-bit view by the x86/x64 installers, native view on ARM64).
std::wstring VcRedistVersion(const Requirement& r) {
    const std::wstring key = L"SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\" + r.architecture;
    for (const DWORD view : {static_cast<DWORD>(RRF_SUBKEY_WOW6432KEY), static_cast<DWORD>(RRF_SUBKEY_WOW6464KEY)}) {
        DWORD installed = 0, size = sizeof(installed);
        if (::RegGetValueW(HKEY_LOCAL_MACHINE, key.c_str(), L"Installed", RRF_RT_REG_DWORD | view, nullptr, &installed, &size) != ERROR_SUCCESS ||
            installed != 1) {
            continue;
        }
        std::wstring v = ReadRegString(HKEY_LOCAL_MACHINE, key, L"Version", view);
        if (!v.empty() && (v[0] == L'v' || v[0] == L'V')) v.erase(0, 1);
        return v.empty() ? L"14.0" : v;
    }
    return {};
}

std::wstring FileVersionOf(const std::wstring& file) {
    DWORD handle = 0;
    const DWORD size = ::GetFileVersionInfoSizeW(file.c_str(), &handle);
    if (size == 0) return {};
    std::vector<uint8_t> data(size);
    VS_FIXEDFILEINFO* info = nullptr;
    UINT len = 0;
    if (!::GetFileVersionInfoW(file.c_str(), 0, size, data.data()) || !::VerQueryValueW(data.data(), L"\\", reinterpret_cast<void**>(&info), &len) ||
        !info || len < sizeof(VS_FIXEDFILEINFO)) {
        return {};
    }
    return Printf(L"%u.%u.%u.%u", HIWORD(info->dwFileVersionMS), LOWORD(info->dwFileVersionMS), HIWORD(info->dwFileVersionLS), LOWORD(info->dwFileVersionLS));
}

bool CustomSatisfied(const Requirement& r) {
    if (r.detectRegistry) {
        const RegistryVersionCondition& c = *r.detectRegistry;
        if (!c.anyVersion) return RegistryVersionAtLeast(c);
        for (const REGSAM view : {static_cast<REGSAM>(KEY_WOW64_64KEY), static_cast<REGSAM>(KEY_WOW64_32KEY)}) {
            HKEY key = nullptr;
            if (::RegOpenKeyExW(c.hive, c.key.c_str(), 0, KEY_QUERY_VALUE | view, &key) != ERROR_SUCCESS) continue;
            const bool found = c.value.empty() || ::RegQueryValueExW(key, c.value.c_str(), nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS;
            ::RegCloseKey(key);
            if (found) return true;
        }
        return false;
    }
    const std::wstring file = ExpandSystemTemplate(r.detectFile);
    if (file.empty() || !fs::Exists(file)) return false;
    if (!r.detectFileVersion) return true;
    Version v;
    return Version::Parse(FileVersionOf(file), v) && Compare(v, *r.detectFileVersion) >= 0;
}

}  // namespace

std::wstring InstalledRequirementVersion(const Requirement& r) {
    if (r.kind == Requirement::Kind::WindowsAppRuntime) return WindowsAppRuntimeVersion(r);
    if (r.kind == Requirement::Kind::DotNet) return DotNetVersion(r);
    if (r.kind == Requirement::Kind::WebView2) return WebView2Version();
    if (r.kind == Requirement::Kind::VcRedist) return VcRedistVersion(r);
    if (r.kind == Requirement::Kind::Custom && !r.detectFile.empty() && r.detectFileVersion) return FileVersionOf(ExpandSystemTemplate(r.detectFile));
    return {};
}

bool RequirementSatisfied(const Requirement& r) {
    if (r.kind == Requirement::Kind::DotNet) return !DotNetVersion(r).empty();
    if (r.kind == Requirement::Kind::WindowsAppRuntime) return !WindowsAppRuntimeVersion(r).empty();
    if (r.kind == Requirement::Kind::Custom) return CustomSatisfied(r);
    if (r.kind == Requirement::Kind::VcRedist) {
        const std::wstring found = VcRedistVersion(r);
        Version v;
        return !found.empty() && (!r.minVersion || (Version::Parse(found, v) && Compare(v, *r.minVersion) >= 0));
    }
    if (r.kind == Requirement::Kind::NetFramework) {
        for (const DWORD view : {static_cast<DWORD>(RRF_SUBKEY_WOW6464KEY), static_cast<DWORD>(RRF_SUBKEY_WOW6432KEY)}) {
            DWORD release = 0, size = sizeof(release);
            if (::RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full", L"Release",
                               RRF_RT_REG_DWORD | view, nullptr, &release, &size) == ERROR_SUCCESS &&
                release >= r.minRelease) {
                return true;
            }
        }
        return false;
    }
    const std::wstring pv = WebView2Version();
    if (pv.empty()) return false;
    Version v;
    return !r.minVersion || (Version::Parse(pv, v) && Compare(v, *r.minVersion) >= 0);
}

// ---- downloads -----------------------------------------------------------------------------------------------------

namespace {

struct InetHandle {
    HINTERNET h = nullptr;
    InetHandle() = default;
    InetHandle(const InetHandle&) = delete;
    InetHandle& operator=(const InetHandle&) = delete;
    ~InetHandle() {
        if (h) ::WinHttpCloseHandle(h);
    }
};

// One https GET. Members close in reverse order (request, connection, session).
struct HttpsGet {
    InetHandle session, connect, request;
    DWORD status = 0;
};

// Sends an https GET (TLS 1.2+, redirects only to https) and reads the status code. False with `error` when the server
// cannot be reached; any status is returned to the caller.
bool StartHttpsGet(const std::string& url, const wchar_t* headers, HttpsGet& r, std::wstring& error) {
    const std::wstring wurl = Utf8ToWide(url);
    URL_COMPONENTS uc{sizeof(uc)};
    wchar_t host[256] = {}, pathBuf[2048] = {}, extra[2048] = {};
    uc.lpszHostName = host;
    uc.dwHostNameLength = ARRAYSIZE(host);
    uc.lpszUrlPath = pathBuf;
    uc.dwUrlPathLength = ARRAYSIZE(pathBuf);
    uc.lpszExtraInfo = extra;  // "?query"
    uc.dwExtraInfoLength = ARRAYSIZE(extra);
    if (!::WinHttpCrackUrl(wurl.c_str(), 0, 0, &uc) || uc.nScheme != INTERNET_SCHEME_HTTPS) {
        error = L"Only https downloads are allowed";
        return false;
    }
    const std::wstring object = std::wstring(pathBuf) + extra;
    const auto fail = [&](const wchar_t* what) {
        error = std::wstring(what) + L": " + Win32Message(::GetLastError());
        return false;
    };
    r.session.h = ::WinHttpOpen(L"FluentSetup/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!r.session.h) r.session.h = ::WinHttpOpen(L"FluentSetup/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!r.session.h) return fail(L"WinHttpOpen");
    DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
    if (!::WinHttpSetOption(r.session.h, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols))) {
        protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;  // TLS 1.3 needs Windows 11
        ::WinHttpSetOption(r.session.h, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols));
    }
    DWORD redirect = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
    ::WinHttpSetOption(r.session.h, WINHTTP_OPTION_REDIRECT_POLICY, &redirect, sizeof(redirect));
    ::WinHttpSetTimeouts(r.session.h, 15000, 15000, 30000, 60000);
    r.connect.h = ::WinHttpConnect(r.session.h, host, uc.nPort, 0);
    if (!r.connect.h) return fail(L"Cannot connect");
    r.request.h = ::WinHttpOpenRequest(r.connect.h, L"GET", object.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (!r.request.h) return fail(L"Cannot open the request");
    if (!::WinHttpSendRequest(r.request.h, headers ? headers : WINHTTP_NO_ADDITIONAL_HEADERS, headers ? static_cast<DWORD>(-1L) : 0,
                              WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !::WinHttpReceiveResponse(r.request.h, nullptr)) {
        return fail(L"Download failed");
    }
    DWORD size = sizeof(r.status);
    ::WinHttpQueryHeaders(r.request.h, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &r.status, &size,
                          WINHTTP_NO_HEADER_INDEX);
    return true;
}

}  // namespace

bool Download(const std::string& url, const std::wstring& dest, const Sha256Digest& sha256, uint64_t expectedSize,
              const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress, std::wstring& error) {
    return DownloadFile(url, dest, &sha256, expectedSize, cancel, progress, error);
}

bool DownloadFile(const std::string& url, const std::wstring& dest, const Sha256Digest* sha256, uint64_t expectedSize,
                  const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress, std::wstring& error) {
    const auto fail = [&](const wchar_t* what) {
        error = std::wstring(what) + L": " + Win32Message(::GetLastError());
        return false;
    };
    HttpsGet r;
    if (!StartHttpsGet(url, nullptr, r, error)) return false;
    if (r.status != 200) {
        error = Printf(L"The server answered HTTP %lu", r.status);
        return false;
    }
    const uint64_t limit = expectedSize ? expectedSize : 512ull * 1024 * 1024;
    UniqueHandle file(::CreateFileW(path::Long(dest).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file) return fail(L"Cannot create the download file");
    Sha256 hash;
    std::vector<uint8_t> buffer(256 * 1024);
    uint64_t total = 0;
    for (;;) {
        if (cancel) {
            error = L"Cancelled";
            return false;
        }
        DWORD got = 0;
        if (!::WinHttpReadData(r.request.h, buffer.data(), static_cast<DWORD>(buffer.size()), &got)) return fail(L"Download interrupted");
        if (got == 0) break;
        total += got;
        if (total > limit) {
            error = L"The download is larger than expected";
            return false;
        }
        hash.Update(buffer.data(), got);
        DWORD written = 0;
        if (!::WriteFile(file.get(), buffer.data(), got, &written, nullptr) || written != got) return fail(L"Cannot write the download");
        if (progress) progress(total, expectedSize);
    }
    if ((expectedSize && total != expectedSize) || (sha256 && hash.Finish() != *sha256)) {
        error = L"The downloaded file does not match the expected SHA-256";
        return false;
    }
    return true;
}

bool HttpGetText(const std::string& url, const wchar_t* headers, size_t maxBytes, std::string& body, unsigned long& status,
                 std::wstring& error) {
    body.clear();
    status = 0;
    HttpsGet r;
    if (!StartHttpsGet(url, headers, r, error)) return false;
    status = r.status;
    if (r.status != 200) {
        error = Printf(L"The server answered HTTP %lu", r.status);
        return false;
    }
    char buffer[16384];
    for (;;) {
        DWORD got = 0;
        if (!::WinHttpReadData(r.request.h, buffer, sizeof(buffer), &got)) {
            error = L"Download interrupted: " + Win32Message(::GetLastError());
            return false;
        }
        if (got == 0) break;
        if (body.size() + got > maxBytes) {
            error = L"The server sent more data than expected";
            return false;
        }
        body.append(buffer, got);
    }
    return true;
}

bool RegistryVersionAtLeast(const RegistryVersionCondition& c) {
    for (const DWORD view : {static_cast<DWORD>(RRF_SUBKEY_WOW6464KEY), static_cast<DWORD>(RRF_SUBKEY_WOW6432KEY)}) {
        Version v;
        if (Version::Parse(ReadRegString(c.hive, c.key, c.value, view), v) && Compare(v, c.atLeast) >= 0) return true;
    }
    return false;
}

// ---- actions -------------------------------------------------------------------------------------------------------

ActionResult RunProgram(const std::wstring& exe, const std::vector<std::wstring>& args, uint32_t timeoutSeconds, bool visible,
                        bool endChildren) {
    ActionResult r;
    std::wstring cmd = Quote(exe);
    for (const auto& a : args) cmd += L" " + Quote(a);
    // Job object so a timeout can end the whole process tree.
    UniqueHandle job(::CreateJobObjectW(nullptr, nullptr));
    if (job && endChildren) {
        // Nothing an action started keeps running (with administrator rights, for per-machine installs) after it.
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        ::SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits));
    }
    STARTUPINFOW si{sizeof(si)};
    if (!visible) {
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_HIDE;
    }
    PROCESS_INFORMATION pi{};
    const std::wstring dir = path::Parent(exe);
    if (!::CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED | (visible ? 0 : CREATE_NO_WINDOW), nullptr,
                          dir.c_str(), &si, &pi)) {
        const DWORD error = ::GetLastError();
        if (error == ERROR_ELEVATION_REQUIRED) {
            // The program's manifest asks for administrator rights (e.g. a machine-wide runtime installer started by a
            // per-user setup): start it through UAC, still without a shell command line.
            std::wstring params;
            for (const auto& a : args) params += (params.empty() ? L"" : L" ") + Quote(a);
            SHELLEXECUTEINFOW sei{sizeof(sei)};
            sei.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
            sei.lpVerb = L"runas";
            sei.lpFile = exe.c_str();
            sei.lpParameters = params.c_str();
            sei.lpDirectory = dir.c_str();
            sei.nShow = visible ? SW_SHOWNORMAL : SW_HIDE;
            if (!::ShellExecuteExW(&sei) || !sei.hProcess) {
                r.ok = false;
                r.detail = L"Could not start " + exe + L" as administrator: " + Win32Message(::GetLastError());
                return r;
            }
            UniqueHandle elevated(sei.hProcess);
            r.ran = true;
            if (::WaitForSingleObject(elevated.get(), timeoutSeconds * 1000u) == WAIT_TIMEOUT) {
                ::TerminateProcess(elevated.get(), static_cast<UINT>(ERROR_TIMEOUT));
                r.ok = false;
                r.detail = Printf(L"%s did not finish within %u s", path::FileName(exe).c_str(), timeoutSeconds);
                return r;
            }
            DWORD code = 0;
            ::GetExitCodeProcess(elevated.get(), &code);
            r.exitCode = static_cast<int32_t>(code);
            return r;
        }
        r.ok = false;
        r.detail = L"Could not start " + exe + L": " + Win32Message(error);
        return r;
    }
    UniqueHandle process(pi.hProcess), thread(pi.hThread);
    if (job) ::AssignProcessToJobObject(job.get(), process.get());
    ::ResumeThread(thread.get());
    r.ran = true;
    if (::WaitForSingleObject(process.get(), timeoutSeconds * 1000u) == WAIT_TIMEOUT) {
        if (job) ::TerminateJobObject(job.get(), static_cast<UINT>(ERROR_TIMEOUT));
        else ::TerminateProcess(process.get(), static_cast<UINT>(ERROR_TIMEOUT));
        r.ok = false;
        r.detail = Printf(L"%s did not finish within %u s", path::FileName(exe).c_str(), timeoutSeconds);
        return r;
    }
    DWORD code = 0;
    ::GetExitCodeProcess(process.get(), &code);
    r.exitCode = static_cast<int32_t>(code);
    return r;
}

// ---- uninstall cleanup -------------------------------------------------------------------------------------------

std::wstring ExpandDataTemplate(const std::wstring& tpl, const std::wstring& installDir) {
    static constexpr wchar_t kInstallDir[] = L"{InstallDir}\\";
    if (tpl.compare(0, wcslen(kInstallDir), kInstallDir) == 0) {
        // Something the app writes into its own folder (logs, caches): a relative path that never reaches the
        // installer's work folder.
        std::wstring rel;
        if (installDir.empty() || !path::ValidateRelative(tpl.substr(wcslen(kInstallDir)), rel)) return {};
        if (EqualsIgnoreCase(rel.substr(0, rel.find(L'\\')), kWorkDirName)) return {};
        return path::Join(installDir, rel);
    }
    struct Root {
        const wchar_t* token;
        REFKNOWNFOLDERID id;
    };
    static const Root kRoots[] = {{L"{CommonAppData}\\", FOLDERID_ProgramData},
                                  {L"{LocalAppData}\\", FOLDERID_LocalAppData},
                                  {L"{AppData}\\", FOLDERID_RoamingAppData}};
    for (const auto& r : kRoots) {
        const size_t len = wcslen(r.token);
        if (tpl.compare(0, len, r.token) != 0) continue;
        std::wstring rel;
        if (!path::ValidateRelative(tpl.substr(len), rel)) return {};
        const std::wstring base = path::KnownFolder(r.id);
        return base.empty() ? std::wstring() : path::Join(base, rel);
    }
    return {};
}

bool DeleteScheduledTask(const std::wstring& name) {
    const HRESULT init = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool deleted = false;
    const size_t slash = name.rfind(L'\\');
    const std::wstring folderPath = L"\\" + (slash == std::wstring::npos ? std::wstring() : name.substr(0, slash));
    const std::wstring taskName = slash == std::wstring::npos ? name : name.substr(slash + 1);
    ITaskService* service = nullptr;
    if (SUCCEEDED(::CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER, IID_ITaskService, reinterpret_cast<void**>(&service)))) {
        VARIANT empty{};
        if (SUCCEEDED(service->Connect(empty, empty, empty, empty))) {
            ITaskFolder* folder = nullptr;
            BSTR folderName = ::SysAllocString(folderPath.c_str());
            if (SUCCEEDED(service->GetFolder(folderName, &folder))) {
                BSTR task = ::SysAllocString(taskName.c_str());
                deleted = SUCCEEDED(folder->DeleteTask(task, 0));
                ::SysFreeString(task);
                // Remove the app's own folder once it is empty (never the root).
                IRegisteredTaskCollection* tasks = nullptr;
                ITaskFolderCollection* subfolders = nullptr;
                LONG taskCount = 1, folderCount = 1;
                if (folderPath != L"\\" && SUCCEEDED(folder->GetTasks(TASK_ENUM_HIDDEN, &tasks)) && SUCCEEDED(tasks->get_Count(&taskCount)) &&
                    SUCCEEDED(folder->GetFolders(0, &subfolders)) && SUCCEEDED(subfolders->get_Count(&folderCount)) && taskCount == 0 && folderCount == 0) {
                    ITaskFolder* root = nullptr;
                    BSTR rootName = ::SysAllocString(L"\\");
                    BSTR leaf = ::SysAllocString(folderPath.substr(1).c_str());
                    if (SUCCEEDED(service->GetFolder(rootName, &root))) {
                        root->DeleteFolder(leaf, 0);
                        root->Release();
                    }
                    ::SysFreeString(leaf);
                    ::SysFreeString(rootName);
                }
                if (tasks) tasks->Release();
                if (subfolders) subfolders->Release();
                folder->Release();
            }
            ::SysFreeString(folderName);
        }
        service->Release();
    }
    if (SUCCEEDED(init)) ::CoUninitialize();
    if (deleted) log::Info(L"Removed scheduled task %s", name.c_str());
    return deleted;
}

bool RemoveDataPath(const std::wstring& tpl, const std::wstring& installDir) {
    const std::wstring target = ExpandDataTemplate(tpl, installDir);
    if (target.empty() || !fs::Exists(target) || fs::IsLinkLike(target) || fs::HasLinkInPath(path::Parent(target))) return false;
    bool ok = false;
    if (fs::IsDirectory(target)) {
        ok = fs::DeleteTree(target);
    } else {
        ::SetFileAttributesW(path::Long(target).c_str(), FILE_ATTRIBUTE_NORMAL);
        ok = ::DeleteFileW(path::Long(target).c_str()) != FALSE;
    }
    if (ok) log::Info(L"Removed %s", target.c_str());
    else log::Warn(L"Could not fully remove %s", target.c_str());
    return ok;
}

bool AnyExists(const std::vector<std::wstring>& templates, const std::wstring& installDir) {
    if (templates.empty()) return true;
    return std::any_of(templates.begin(), templates.end(), [&](const std::wstring& t) {
        const std::wstring p = ExpandDataTemplate(t, installDir);
        return !p.empty() && fs::Exists(p);
    });
}

bool StopTraceSession(const std::wstring& name) {
    // EVENT_TRACE_PROPERTIES followed by room for the logger and log file names.
    const size_t size = sizeof(EVENT_TRACE_PROPERTIES) + 2 * 1024 * sizeof(wchar_t);
    auto buffer = std::make_unique<uint8_t[]>(size);
    auto* props = reinterpret_cast<EVENT_TRACE_PROPERTIES*>(buffer.get());
    ZeroMemory(props, size);
    props->Wnode.BufferSize = static_cast<ULONG>(size);
    props->LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES);
    props->LogFileNameOffset = sizeof(EVENT_TRACE_PROPERTIES) + 1024 * sizeof(wchar_t);
    const ULONG rc = ::ControlTraceW(0, name.c_str(), props, EVENT_TRACE_CONTROL_STOP);
    // The boot-time session ("autosession\<name>" in logman) lives in the WMI Autologger key.
    const std::wstring key = L"SYSTEM\\CurrentControlSet\\Control\\WMI\\Autologger\\" + name;
    const LSTATUS del = ::RegDeleteTreeW(HKEY_LOCAL_MACHINE, key.c_str());
    if (rc == ERROR_SUCCESS || del == ERROR_SUCCESS) log::Info(L"Stopped trace session %s (stop %lu, autologger %ld)", name.c_str(), rc, del);
    return (rc == ERROR_SUCCESS || rc == ERROR_WMI_INSTANCE_NOT_FOUND) && (del == ERROR_SUCCESS || del == ERROR_FILE_NOT_FOUND);
}

bool RemoveServiceAccess(const ServiceAccess& access) {
    SC_HANDLE scm = ::OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) return false;
    SC_HANDLE svc = ::OpenServiceW(scm, access.service.c_str(), READ_CONTROL | WRITE_DAC);
    bool changed = false, ok = false;
    if (svc) {
        DWORD needed = 0;
        ::QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, nullptr, 0, &needed);
        std::vector<uint8_t> sd(needed);
        LPWSTR sddl = nullptr;
        if (needed && ::QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd.data(), needed, &needed) &&
            ::ConvertSecurityDescriptorToStringSecurityDescriptorW(sd.data(), SDDL_REVISION_1, DACL_SECURITY_INFORMATION, &sddl, nullptr)) {
            std::wstring text = sddl;
            ::LocalFree(sddl);
            // Remove "(A;;<rights>;;;S-1-5-21-...)": allow ACEs with exactly these rights for user accounts only.
            for (const auto& right : access.rights) {
                const std::wstring prefix = L"(A;;" + right + L";;;S-1-5-21-";
                for (size_t pos; (pos = text.find(prefix)) != std::wstring::npos;) {
                    const size_t end = text.find(L')', pos);
                    if (end == std::wstring::npos) break;
                    text.erase(pos, end - pos + 1);
                    changed = true;
                }
            }
            ok = true;
            PSECURITY_DESCRIPTOR updated = nullptr;
            if (changed) {
                ok = ::ConvertStringSecurityDescriptorToSecurityDescriptorW(text.c_str(), SDDL_REVISION_1, &updated, nullptr) &&
                     ::SetServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, updated);
                if (updated) ::LocalFree(updated);
                if (ok) log::Info(L"Removed user access entries from service %s", access.service.c_str());
            }
        }
        ::CloseServiceHandle(svc);
    }
    ::CloseServiceHandle(scm);
    return ok;
}
void DeleteRunValue(const std::wstring& name) {
    static constexpr wchar_t kRun[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    for (HKEY hive : {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE}) {
        HKEY key = nullptr;
        if (::RegOpenKeyExW(hive, kRun, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS) {
            ::RegDeleteValueW(key, name.c_str());
            ::RegCloseKey(key);
        }
    }
}

bool WriteRunValue(const std::wstring& name, const std::wstring& exe, const std::vector<std::wstring>& args) {
    // Always quoted ("C:\App\App.exe" --background), the form Inno Setup, NSIS and most apps write themselves, so an
    // app that compares its own Run value finds the same text.
    std::wstring command = L"\"" + exe + L"\"";
    for (const auto& a : args) command += L" " + Quote(a);
    HKEY key = nullptr;
    if (::RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", 0, nullptr, 0, KEY_SET_VALUE,
                          nullptr, &key, nullptr) != ERROR_SUCCESS) {
        return false;
    }
    const bool ok = ::RegSetValueExW(key, name.c_str(), 0, REG_SZ, reinterpret_cast<const BYTE*>(command.c_str()),
                                     static_cast<DWORD>((command.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
    ::RegCloseKey(key);
    return ok;
}

bool IsShortcutName(const std::wstring& n) {
    const size_t slash = n.find(L'\\');
    if (slash == std::wstring::npos) return IsLnkName(n);
    return path::IsValidComponent(n.substr(0, slash)) && IsLnkName(n.substr(slash + 1));  // "Folder\App.lnk"
}

void DeleteShortcutsNamed(const std::wstring& name) {
    if (!IsShortcutName(name)) return;
    const bool inFolder = name.find(L'\\') != std::wstring::npos;
    for (REFKNOWNFOLDERID id : {FOLDERID_Programs, FOLDERID_CommonPrograms, FOLDERID_Desktop, FOLDERID_PublicDesktop,
                                FOLDERID_Startup, FOLDERID_CommonStartup}) {
        const std::wstring dir = path::KnownFolder(id);
        if (dir.empty()) continue;
        const std::wstring link = path::Join(dir, name);
        const std::wstring folder = path::Parent(link);
        if (inFolder && fs::HasLinkInPath(folder)) continue;
        if (fs::Exists(link) && !fs::IsLinkLike(link) && ::DeleteFileW(path::Long(link).c_str())) log::Info(L"Removed shortcut %s", link.c_str());
        // A Start menu folder of an older installer ("App\Uninstall App.lnk"): removed once it is empty.
        if (inFolder && fs::IsDirectory(folder) && fs::IsDirectoryEmpty(folder)) ::RemoveDirectoryW(path::Long(folder).c_str());
    }
}

namespace {

constexpr wchar_t kRunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";

// Case-insensitive * and ? matching of one file name.
bool WildcardMatch(std::wstring_view pattern, std::wstring_view name) {
    size_t p = 0, n = 0, star = std::wstring_view::npos, mark = 0;
    while (n < name.size()) {
        if (p < pattern.size() && (pattern[p] == L'?' || ::towlower(pattern[p]) == ::towlower(name[n]))) {
            ++p, ++n;
        } else if (p < pattern.size() && pattern[p] == L'*') {
            star = p++;
            mark = n;
        } else if (star != std::wstring_view::npos) {
            p = star + 1;
            n = ++mark;
        } else {
            return false;
        }
    }
    while (p < pattern.size() && pattern[p] == L'*') ++p;
    return p == pattern.size();
}

bool SplitRegistryKey(const std::wstring& key, HKEY& hive, std::wstring& sub) {
    if (key.rfind(L"HKCU\\", 0) == 0) hive = HKEY_CURRENT_USER;
    else if (key.rfind(L"HKLM\\", 0) == 0) hive = HKEY_LOCAL_MACHINE;
    else return false;
    sub = key.substr(5);
    return true;
}

}  // namespace

bool IsFilePattern(const std::wstring& pattern) {
    if (pattern.empty() || pattern.size() > 260) return false;
    const size_t slash = pattern.rfind(L'\\');
    const std::wstring folder = slash == std::wstring::npos ? std::wstring() : pattern.substr(0, slash);
    const std::wstring name = slash == std::wstring::npos ? pattern : pattern.substr(slash + 1);
    if (!folder.empty()) {
        std::wstring rel;
        if (folder.find_first_of(L"*?") != std::wstring::npos || !path::ValidateRelative(folder, rel)) return false;
        if (EqualsIgnoreCase(rel.substr(0, rel.find(L'\\')), kWorkDirName)) return false;
    }
    std::wstring probe = name;  // the file name with its wildcards replaced must be a valid name
    std::replace(probe.begin(), probe.end(), L'*', L'x');
    std::replace(probe.begin(), probe.end(), L'?', L'x');
    // At least one literal character: "*" or "*.*" would empty the folder.
    return path::IsValidComponent(probe) && name.find_first_not_of(L"*?.") != std::wstring::npos;
}

std::vector<std::wstring> MatchFiles(const std::wstring& dir, const std::wstring& pattern) {
    std::vector<std::wstring> out;
    if (!IsFilePattern(pattern)) return out;
    const size_t slash = pattern.rfind(L'\\');
    const std::wstring folder = slash == std::wstring::npos ? dir : path::Join(dir, pattern.substr(0, slash));
    const std::wstring name = slash == std::wstring::npos ? pattern : pattern.substr(slash + 1);
    if (!fs::IsDirectory(folder) || fs::HasLinkInPath(folder)) return out;
    WIN32_FIND_DATAW fd{};
    UniqueFind find(::FindFirstFileExW(path::Long(path::Join(folder, L"*")).c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, 0));
    for (bool more = find.valid(); more; more = ::FindNextFileW(find.get(), &fd)) {
        if (fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) continue;
        if (WildcardMatch(name, fd.cFileName)) out.push_back(path::Join(folder, fd.cFileName));
    }
    return out;
}

bool IsRemovableRegistryKey(const std::wstring& key) {
    HKEY hive = nullptr;
    std::wstring sub;
    if (key.size() > 255 || !SplitRegistryKey(key, hive, sub)) return false;
    std::vector<std::wstring> parts;
    for (size_t start = 0; start <= sub.size();) {
        size_t end = sub.find(L'\\', start);
        if (end == std::wstring::npos) end = sub.size();
        parts.push_back(sub.substr(start, end - start));
        start = end + 1;
    }
    if (parts.size() < 2 || !EqualsIgnoreCase(parts[0], L"Software")) return false;
    for (const auto& p : parts) {
        if (p.empty() || p.size() > 128 || std::any_of(p.begin(), p.end(), [](wchar_t c) { return c < 0x20; })) return false;
    }
    // Trees that other software shares are never deleted wholesale.
    for (const wchar_t* shared : {L"Microsoft", L"Classes", L"Policies", L"Wow6432Node", L"RegisteredApplications", L"Clients"}) {
        if (EqualsIgnoreCase(parts[1], shared)) return false;
    }
    return true;
}

bool DeleteRegistryKey(const std::wstring& key) {
    HKEY hive = nullptr;
    std::wstring sub;
    if (!IsRemovableRegistryKey(key) || !SplitRegistryKey(key, hive, sub)) return false;
    const size_t slash = sub.rfind(L'\\');
    HKEY parent = nullptr;
    if (::RegOpenKeyExW(hive, sub.substr(0, slash).c_str(), 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, &parent) != ERROR_SUCCESS) return false;
    const std::wstring leaf = sub.substr(slash + 1);
    const LSTATUS rc = ::RegDeleteTreeW(parent, leaf.c_str());  // with a subkey name it deletes that key too
    ::RegCloseKey(parent);
    if (rc == ERROR_SUCCESS) log::Info(L"Removed registry key %s", key.c_str());
    return rc == ERROR_SUCCESS;
}

bool RunValueExists(const std::wstring& name) {
    for (HKEY hive : {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE}) {
        HKEY key = nullptr;
        if (::RegOpenKeyExW(hive, kRunKey, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS) continue;
        const bool found = ::RegQueryValueExW(key, name.c_str(), nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS;
        ::RegCloseKey(key);
        if (found) return true;
    }
    return false;
}

bool ScheduledTaskExists(const std::wstring& name) {
    const HRESULT init = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool found = false;
    ITaskService* service = nullptr;
    if (SUCCEEDED(::CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER, IID_ITaskService, reinterpret_cast<void**>(&service)))) {
        VARIANT empty{};
        if (SUCCEEDED(service->Connect(empty, empty, empty, empty))) {
            ITaskFolder* folder = nullptr;
            BSTR root = ::SysAllocString(L"\\");
            if (SUCCEEDED(service->GetFolder(root, &folder))) {
                IRegisteredTask* task = nullptr;
                BSTR taskPath = ::SysAllocString(name.c_str());
                // A task this user may not read (registered elevated) reports access denied, not absence.
                const HRESULT hr = folder->GetTask(taskPath, &task);
                found = SUCCEEDED(hr) || hr == E_ACCESSDENIED;
                if (task) task->Release();
                ::SysFreeString(taskPath);
                folder->Release();
            }
            ::SysFreeString(root);
        }
        service->Release();
    }
    if (SUCCEEDED(init)) ::CoUninitialize();
    return found;
}

bool RunNGen(const std::wstring& assembly, bool install) {
    const std::wstring windows = path::KnownFolder(FOLDERID_Windows);
    if (windows.empty() || !fs::Exists(assembly)) return false;
    for (const wchar_t* fw : {L"Microsoft.NET\\Framework64\\v4.0.30319\\ngen.exe", L"Microsoft.NET\\Framework\\v4.0.30319\\ngen.exe"}) {
        const std::wstring ngen = path::Join(windows, fw);
        if (!fs::Exists(ngen)) continue;
        std::vector<std::wstring> args = install ? std::vector<std::wstring>{L"install", assembly, L"/queue", L"/nologo"}
                                                 : std::vector<std::wstring>{L"uninstall", assembly, L"/nologo"};
        const ActionResult r = RunProgram(ngen, args, 60);
        log::Info(L"ngen %s %s: %s", install ? L"install" : L"uninstall", assembly.c_str(),
                  r.ok ? Printf(L"exit code %lld", static_cast<long long>(r.exitCode)).c_str() : r.detail.c_str());
        return r.ok;
    }
    return false;
}

namespace {

constexpr wchar_t kMachineEnv[] = L"SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment";

std::vector<std::wstring> SplitPath(const std::wstring& value) {
    std::vector<std::wstring> parts;
    for (size_t start = 0; start <= value.size();) {
        size_t end = value.find(L';', start);
        if (end == std::wstring::npos) end = value.size();
        if (end > start) parts.push_back(value.substr(start, end - start));
        start = end + 1;
    }
    return parts;
}

bool SameDir(std::wstring a, std::wstring b) {
    while (!a.empty() && a.back() == L'\\') a.pop_back();
    while (!b.empty() && b.back() == L'\\') b.pop_back();
    return EqualsIgnoreCase(a, b);
}

// Rewrites PATH with `edit` applied to its entries; keeps the value type (REG_EXPAND_SZ for %VARS%).
bool EditPath(bool machine, const std::function<bool(std::vector<std::wstring>&)>& edit) {
    HKEY key = nullptr;
    if (::RegOpenKeyExW(machine ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER, machine ? kMachineEnv : L"Environment", 0,
                        KEY_QUERY_VALUE | KEY_SET_VALUE, &key) != ERROR_SUCCESS) {
        return false;
    }
    DWORD type = REG_EXPAND_SZ, size = 0;
    std::wstring value;
    if (::RegQueryValueExW(key, L"Path", nullptr, &type, nullptr, &size) == ERROR_SUCCESS && size > 0 && size < 1024 * 1024) {
        value.resize(size / sizeof(wchar_t) + 1);
        ::RegQueryValueExW(key, L"Path", nullptr, &type, reinterpret_cast<BYTE*>(value.data()), &size);
        value.resize(wcsnlen(value.c_str(), value.size()));
    }
    if (type != REG_SZ && type != REG_EXPAND_SZ) type = REG_EXPAND_SZ;
    std::vector<std::wstring> parts = SplitPath(value);
    bool ok = true;
    if (edit(parts)) {
        std::wstring joined;
        for (const auto& p : parts) joined += (joined.empty() ? L"" : L";") + p;
        ok = ::RegSetValueExW(key, L"Path", 0, type, reinterpret_cast<const BYTE*>(joined.c_str()),
                              static_cast<DWORD>((joined.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
        DWORD_PTR result = 0;
        ::SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, reinterpret_cast<LPARAM>(L"Environment"), SMTO_ABORTIFHUNG, 3000, &result);
    }
    ::RegCloseKey(key);
    return ok;
}

}  // namespace

bool AddToPath(const std::wstring& dir, bool machine) {
    const bool ok = EditPath(machine, [&](std::vector<std::wstring>& parts) {
        if (std::any_of(parts.begin(), parts.end(), [&](const std::wstring& p) { return SameDir(p, dir); })) return false;
        parts.push_back(dir);
        return true;
    });
    log::Info(L"%s %s to the %s PATH", ok ? L"Added" : L"Could not add", dir.c_str(), machine ? L"machine" : L"user");
    return ok;
}

void RemoveFromPath(const std::wstring& dir, bool machine) {
    EditPath(machine, [&](std::vector<std::wstring>& parts) {
        const size_t before = parts.size();
        parts.erase(std::remove_if(parts.begin(), parts.end(), [&](const std::wstring& p) { return SameDir(p, dir); }), parts.end());
        return parts.size() != before;
    });
}

}  // namespace fsetup::install