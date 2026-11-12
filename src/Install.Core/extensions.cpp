#include "Install.Core/extensions.h"

#include "Common/error.h"
#include "Common/fs.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/text.h"

#include <comdef.h>
#include <taskschd.h>
#include <tlhelp32.h>

#include <algorithm>
#include <unordered_set>

#pragma comment(lib, "taskschd.lib")

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

    for (const auto& r : root["requirements"].array()) {
        Requirement req;
        const std::string& type = r["type"].asString();
        req.name = r["name"].asWide().substr(0, 128);
        if (type == "netfx") {
            req.kind = Requirement::Kind::NetFramework;
            req.minRelease = static_cast<uint32_t>(r["minRelease"].asUint(0));
            if (req.minRelease == 0) Bad(L"netfx requirement without minRelease");
        } else if (type == "webview2") {
            req.kind = Requirement::Kind::WebView2;
            if (r["installer"].isString()) {
                std::wstring why;
                if (!path::ValidateRelative(r["installer"].asWide(), req.installer, &why) || !payloadHas(req.installer)) {
                    Bad(L"webview2 installer must be a payload file");
                }
                req.installerArgs = ParseArgs(r["args"]);
            }
        } else {
            Bad(L"unknown requirement type");
        }
        if (req.name.empty()) req.name = req.kind == Requirement::Kind::WebView2 ? L"Microsoft Edge WebView2 Runtime" : L".NET Framework";
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
        opt.defaultValue = o["default"].asBool(false);
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

bool RequirementSatisfied(const Requirement& r) {
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
    // WebView2 Evergreen runtime: machine-wide (32-bit view) or per-user registration with a non-empty, non-zero "pv".
    static constexpr wchar_t kClient[] = L"SOFTWARE\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    for (const auto& [hive, view] : {std::pair<HKEY, DWORD>{HKEY_LOCAL_MACHINE, RRF_SUBKEY_WOW6432KEY}, std::pair<HKEY, DWORD>{HKEY_CURRENT_USER, 0}}) {
        const std::wstring pv = ReadRegString(hive, kClient, L"pv", view);
        if (!pv.empty() && pv != L"0.0.0.0") return true;
    }
    return false;
}

bool RegistryVersionAtLeast(const RegistryVersionCondition& c) {
    for (const DWORD view : {static_cast<DWORD>(RRF_SUBKEY_WOW6464KEY), static_cast<DWORD>(RRF_SUBKEY_WOW6432KEY)}) {
        Version v;
        if (Version::Parse(ReadRegString(c.hive, c.key, c.value, view), v) && Compare(v, c.atLeast) >= 0) return true;
    }
    return false;
}

// ---- actions -------------------------------------------------------------------------------------------------------

ActionResult RunProgram(const std::wstring& exe, const std::vector<std::wstring>& args, uint32_t timeoutSeconds) {
    ActionResult r;
    std::wstring cmd = Quote(exe);
    for (const auto& a : args) cmd += L" " + Quote(a);
    // Job object so a timeout can end the whole process tree.
    UniqueHandle job(::CreateJobObjectW(nullptr, nullptr));
    STARTUPINFOW si{sizeof(si)};
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi{};
    const std::wstring dir = path::Parent(exe);
    if (!::CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr,
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

}  // namespace fsetup::install
