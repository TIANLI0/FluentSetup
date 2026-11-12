#include "Install.Core/extensions.h"

#include "Common/error.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/text.h"

#include <comdef.h>
#include <taskschd.h>
#include <tlhelp32.h>
#include <winhttp.h>

#include <algorithm>
#include <unordered_set>

#pragma comment(lib, "taskschd.lib")
#pragma comment(lib, "winhttp.lib")

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

}  // namespace

std::wstring LocalizedText::Get(const std::wstring& language, const std::wstring& fallback) const {
    if (auto it = byLanguage.find(WideToUtf8(language)); it != byLanguage.end() && !it->second.empty()) return it->second;
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
    for (const auto& n : inst["ngen"].array()) {
        std::wstring rel;
        if (!path::ValidateRelative(n.asWide(), rel) || !payloadHas(rel)) Bad(L"ngen entries must be payload files: " + n.asWide());
        e.ngen.push_back(rel);
    }
    if (e.ngen.size() > 16) Bad(L"too many ngen entries");
    const json::Value& ic = inst["cleanup"];
    ParseNames(ic["removeScheduledTasks"], e.installCleanup.removeScheduledTasks, ValidTaskOrValueName, L"scheduled task name");
    ParseNames(ic["removeRunValues"], e.installCleanup.removeRunValues, ValidTaskOrValueName, L"Run value name");
    ParseNames(ic["removeShortcuts"], e.installCleanup.removeShortcuts, IsLnkName, L"shortcut name");
    ParseNames(ic["removeUninstallKeys"], e.installCleanup.removeUninstallKeys, ValidTaskOrValueName, L"uninstall key");

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
        if (r["installer"].isString()) {
            std::wstring why;
            if (!path::ValidateRelative(r["installer"].asWide(), req.installer, &why) || !payloadHas(req.installer)) {
                Bad(L"requirement installer must be a payload file");
            }
        }
        req.installerArgs = ParseArgs(r["args"]);
        if (const json::Value& d = r["download"]; d.isObject()) {
            req.downloadUrl = d["url"].asString();
            std::vector<uint8_t> digest;
            if (req.downloadUrl.size() > 2048 || req.downloadUrl.rfind("https://", 0) != 0) Bad(L"requirement downloads must use https");
            if (!FromHex(d["sha256"].asString(), digest) || digest.size() != req.downloadSha256.size()) Bad(L"requirement download needs a sha256");
            std::copy(digest.begin(), digest.end(), req.downloadSha256.begin());
            req.downloadSize = d["size"].asUint(0);
        }
        req.mustSucceed = r["onFailure"].asString() == "fail";
        req.helpUrl = r["helpUrl"].asString();
        if (req.helpUrl.size() > 2048 || (!req.helpUrl.empty() && req.helpUrl.rfind("https://", 0) != 0)) Bad(L"helpUrl must be https");
        if (req.name.empty()) {
            req.name = req.kind == Requirement::Kind::WebView2 ? L"Microsoft Edge WebView2 Runtime"
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

    const json::Value& u = root["uninstall"];
    for (const auto& d : u["removeData"].array()) {
        if (ExpandDataTemplate(d.asWide()).empty()) Bad(L"removeData entries must be {CommonAppData}|{LocalAppData}|{AppData}\\<folder>: " + d.asWide());
        e.cleanup.removeData.push_back(d.asWide());
    }
    for (const auto& t : u["removeScheduledTasks"].array()) {
        if (!ValidTaskOrValueName(t.asWide())) Bad(L"invalid scheduled task name");
        e.cleanup.removeScheduledTasks.push_back(t.asWide());
    }
    for (const auto& r : u["removeRunValues"].array()) {
        if (!ValidTaskOrValueName(r.asWide())) Bad(L"invalid Run value name");
        e.cleanup.removeRunValues.push_back(r.asWide());
    }
    ParseNames(u["removeShortcuts"], e.cleanup.removeShortcuts, IsLnkName, L"shortcut name");
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

std::wstring InstalledRequirementVersion(const Requirement& r) {
    if (r.kind == Requirement::Kind::DotNet) return DotNetVersion(r);
    if (r.kind == Requirement::Kind::WebView2) return WebView2Version();
    return {};
}

bool RequirementSatisfied(const Requirement& r) {
    if (r.kind == Requirement::Kind::DotNet) return !DotNetVersion(r).empty();
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

bool Download(const std::string& url, const std::wstring& dest, const Sha256Digest& sha256, uint64_t expectedSize,
              const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress, std::wstring& error) {
    const std::wstring wurl = Utf8ToWide(url);
    URL_COMPONENTS uc{sizeof(uc)};
    wchar_t host[256] = {}, pathBuf[2048] = {};
    uc.lpszHostName = host;
    uc.dwHostNameLength = ARRAYSIZE(host);
    uc.lpszUrlPath = pathBuf;
    uc.dwUrlPathLength = ARRAYSIZE(pathBuf);
    if (!::WinHttpCrackUrl(wurl.c_str(), 0, 0, &uc) || uc.nScheme != INTERNET_SCHEME_HTTPS) {
        error = L"Only https downloads are allowed";
        return false;
    }
    const auto fail = [&](const wchar_t* what) {
        error = std::wstring(what) + L": " + Win32Message(::GetLastError());
        return false;
    };
    struct Inet {
        HINTERNET h;
        ~Inet() { if (h) ::WinHttpCloseHandle(h); }
    };
    Inet session{::WinHttpOpen(L"FluentSetup/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0)};
    if (!session.h) session.h = ::WinHttpOpen(L"FluentSetup/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session.h) return fail(L"WinHttpOpen");
    DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
    if (!::WinHttpSetOption(session.h, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols))) {
        protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;  // TLS 1.3 needs Windows 11
        ::WinHttpSetOption(session.h, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols));
    }
    DWORD redirect = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
    ::WinHttpSetOption(session.h, WINHTTP_OPTION_REDIRECT_POLICY, &redirect, sizeof(redirect));
    ::WinHttpSetTimeouts(session.h, 15000, 15000, 30000, 60000);
    Inet connect{::WinHttpConnect(session.h, host, uc.nPort, 0)};
    if (!connect.h) return fail(L"Cannot connect");
    Inet request{::WinHttpOpenRequest(connect.h, L"GET", pathBuf, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE)};
    if (!request.h) return fail(L"Cannot open the request");
    if (!::WinHttpSendRequest(request.h, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !::WinHttpReceiveResponse(request.h, nullptr)) {
        return fail(L"Download failed");
    }
    DWORD status = 0, size = sizeof(status);
    ::WinHttpQueryHeaders(request.h, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX);
    if (status != 200) {
        error = Printf(L"The server answered HTTP %lu", status);
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
        if (!::WinHttpReadData(request.h, buffer.data(), static_cast<DWORD>(buffer.size()), &got)) return fail(L"Download interrupted");
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
    if ((expectedSize && total != expectedSize) || hash.Finish() != sha256) {
        error = L"The downloaded file does not match the expected SHA-256";
        return false;
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

ActionResult RunProgram(const std::wstring& exe, const std::vector<std::wstring>& args, uint32_t timeoutSeconds, bool visible) {
    ActionResult r;
    std::wstring cmd = Quote(exe);
    for (const auto& a : args) cmd += L" " + Quote(a);
    // Job object so a timeout can end the whole process tree.
    UniqueHandle job(::CreateJobObjectW(nullptr, nullptr));
    STARTUPINFOW si{sizeof(si)};
    if (!visible) {
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_HIDE;
    }
    PROCESS_INFORMATION pi{};
    const std::wstring dir = path::Parent(exe);
    if (!::CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED | (visible ? 0 : CREATE_NO_WINDOW), nullptr,
                          dir.c_str(), &si, &pi)) {
        r.ok = false;
        r.detail = L"Could not start " + exe + L": " + Win32Message(::GetLastError());
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

std::wstring ExpandDataTemplate(const std::wstring& tpl) {
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
    ITaskService* service = nullptr;
    if (SUCCEEDED(::CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER, IID_ITaskService, reinterpret_cast<void**>(&service)))) {
        VARIANT empty{};
        if (SUCCEEDED(service->Connect(empty, empty, empty, empty))) {
            ITaskFolder* root = nullptr;
            BSTR rootName = ::SysAllocString(L"\\");
            if (SUCCEEDED(service->GetFolder(rootName, &root))) {
                BSTR taskName = ::SysAllocString(name.c_str());
                deleted = SUCCEEDED(root->DeleteTask(taskName, 0));
                ::SysFreeString(taskName);
                root->Release();
            }
            ::SysFreeString(rootName);
        }
        service->Release();
    }
    if (SUCCEEDED(init)) ::CoUninitialize();
    return deleted;
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
    std::wstring command = Quote(exe);
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

void DeleteShortcutsNamed(const std::wstring& name) {
    if (!IsLnkName(name)) return;
    for (REFKNOWNFOLDERID id : {FOLDERID_Programs, FOLDERID_CommonPrograms, FOLDERID_Desktop, FOLDERID_PublicDesktop,
                                FOLDERID_Startup, FOLDERID_CommonStartup}) {
        const std::wstring dir = path::KnownFolder(id);
        if (dir.empty()) continue;
        const std::wstring link = path::Join(dir, name);
        if (fs::Exists(link) && !fs::IsLinkLike(link) && ::DeleteFileW(path::Long(link).c_str())) log::Info(L"Removed shortcut %s", link.c_str());
    }
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

}  // namespace fsetup::install