#include "Common/path.h"

#include "Common/text.h"

#include <shlobj.h>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

namespace fsetup::path {

const char* ProblemKey(Problem p) noexcept {
    switch (p) {
        case Problem::None: return "";
        case Problem::Empty: return "path.empty";
        case Problem::NotAbsolute: return "path.notAbsolute";
        case Problem::Network: return "path.network";
        case Problem::InvalidChars: return "path.invalidChars";
        case Problem::ReservedName: return "path.reservedName";
        case Problem::TooLong: return "path.tooLong";
        case Problem::Root: return "path.root";
        case Problem::Protected: return "path.protected";
        case Problem::NotEmpty: return "path.notEmpty";
        case Problem::OtherApp: return "path.otherApp";
        case Problem::Reparse: return "path.reparse";
        case Problem::NoDrive: return "path.noDrive";
        case Problem::NotWritable: return "path.notWritable";
        case Problem::Space: return "path.space";
        case Problem::Locked: return "path.locked";
    }
    return "";
}

bool IsReservedDeviceName(std::wstring_view component) noexcept {
    // The base name (before the first dot) is what Windows matches, also with trailing spaces: "nul .txt".
    std::wstring_view base = component.substr(0, component.find(L'.'));
    while (!base.empty() && base.back() == L' ') base.remove_suffix(1);
    static constexpr const wchar_t* kNames[] = {L"CON", L"PRN", L"AUX", L"NUL", L"CONIN$", L"CONOUT$"};
    for (const wchar_t* n : kNames) {
        if (EqualsIgnoreCase(base, n)) return true;
    }
    if (base.size() == 4 && (StartsWithIgnoreCase(base, L"COM") || StartsWithIgnoreCase(base, L"LPT"))) {
        const wchar_t d = base[3];
        // COM0..9, LPT0..9 plus the superscript digits Windows also treats as device names.
        if ((d >= L'0' && d <= L'9') || d == L'\u00B9' || d == L'\u00B2' || d == L'\u00B3') return true;
    }
    return false;
}

bool IsValidComponent(std::wstring_view c) noexcept {
    if (c.empty() || c.size() > kMaxComponent) return false;
    if (c == L"." || c == L"..") return false;
    for (const wchar_t ch : c) {
        if (ch < 0x20) return false;
        switch (ch) {
            case L'<': case L'>': case L':': case L'"': case L'/': case L'\\': case L'|': case L'?': case L'*':
                return false;
            default: break;
        }
    }
    if (c.back() == L'.' || c.back() == L' ') return false;
    if (c.front() == L' ') return false;
    if (IsReservedDeviceName(c)) return false;
    return true;
}

bool ValidateRelative(std::wstring_view rel, std::wstring& normalized, std::wstring* reason) {
    const auto fail = [&](const wchar_t* why) {
        if (reason) *reason = why;
        return false;
    };
    normalized.clear();
    if (rel.empty()) return fail(L"empty path");
    if (rel.size() > kMaxRelativePath) return fail(L"path too long");
    std::wstring work(rel);
    for (auto& ch : work) {
        if (ch == L'/') ch = L'\\';
    }
    if (work.front() == L'\\') return fail(L"absolute or UNC path");
    if (work.size() >= 2 && work[1] == L':') return fail(L"drive-qualified path");
    size_t start = 0;
    int depth = 0;
    while (start <= work.size()) {
        size_t end = work.find(L'\\', start);
        if (end == std::wstring::npos) end = work.size();
        const std::wstring_view comp(work.data() + start, end - start);
        if (comp.empty()) return fail(L"empty path component");
        if (comp == L"..") return fail(L"parent-directory component");
        if (comp == L".") return fail(L"current-directory component");
        if (!IsValidComponent(comp)) return fail(L"invalid file name component");
        if (++depth > 64) return fail(L"path nested too deeply");
        if (!normalized.empty()) normalized.push_back(L'\\');
        normalized.append(comp);
        start = end + 1;
        if (end == work.size()) break;
    }
    return true;
}

Problem ValidateAbsoluteSyntax(std::wstring_view input, std::wstring& normalized) {
    std::wstring_view s = Trim(input);
    // Accept a quoted path pasted from Explorer's "Copy as path".
    if (s.size() >= 2 && s.front() == L'"' && s.back() == L'"') s = s.substr(1, s.size() - 2);
    if (s.empty()) return Problem::Empty;
    std::wstring work(s);
    for (auto& ch : work) {
        if (ch == L'/') ch = L'\\';
    }
    if (StartsWithIgnoreCase(work, L"\\\\?\\") || StartsWithIgnoreCase(work, L"\\\\.\\")) return Problem::InvalidChars;
    if (work.size() >= 2 && work[0] == L'\\' && work[1] == L'\\') return Problem::Network;
    const bool drive = work.size() >= 3 && ((work[0] >= L'A' && work[0] <= L'Z') || (work[0] >= L'a' && work[0] <= L'z')) &&
                       work[1] == L':' && work[2] == L'\\';
    if (!drive) return Problem::NotAbsolute;
    while (work.size() > 3 && work.back() == L'\\') work.pop_back();
    if (work.size() == 3) return Problem::Root;
    work[0] = static_cast<wchar_t>(::towupper(work[0]));

    std::wstring rel;
    std::wstring why;
    if (!ValidateRelative(std::wstring_view(work).substr(3), rel, &why)) {
        for (const auto& c : Components(std::wstring_view(work).substr(3))) {
            if (IsReservedDeviceName(c)) return Problem::ReservedName;
        }
        if (work.size() > kMaxInstallDir) return Problem::TooLong;
        return Problem::InvalidChars;
    }
    normalized = work.substr(0, 3) + rel;
    if (normalized.size() > kMaxInstallDir) return Problem::TooLong;

    const UINT type = ::GetDriveTypeW(normalized.substr(0, 3).c_str());
    if (type == DRIVE_NO_ROOT_DIR || type == DRIVE_UNKNOWN) return Problem::NoDrive;
    if (type == DRIVE_REMOTE) return Problem::Network;
    return Problem::None;
}

std::wstring Join(std::wstring_view base, std::wstring_view rel) {
    std::wstring out(base);
    if (!out.empty() && out.back() != L'\\') out.push_back(L'\\');
    out.append(rel);
    return out;
}

std::wstring Parent(std::wstring_view p) {
    const size_t pos = p.find_last_of(L'\\');
    if (pos == std::wstring_view::npos) return {};
    if (pos == 2 && p.size() > 1 && p[1] == L':') return std::wstring(p.substr(0, 3));
    return std::wstring(p.substr(0, pos));
}

std::wstring FileName(std::wstring_view p) {
    const size_t pos = p.find_last_of(L'\\');
    return std::wstring(pos == std::wstring_view::npos ? p : p.substr(pos + 1));
}

std::wstring Long(std::wstring_view absolute) {
    if (StartsWithIgnoreCase(absolute, L"\\\\?\\")) return std::wstring(absolute);
    if (absolute.size() >= 2 && absolute[0] == L'\\' && absolute[1] == L'\\') {
        return L"\\\\?\\UNC\\" + std::wstring(absolute.substr(2));
    }
    return L"\\\\?\\" + std::wstring(absolute);
}

bool IsSameOrUnder(std::wstring_view child, std::wstring_view parent) noexcept {
    while (!parent.empty() && parent.back() == L'\\') parent.remove_suffix(1);
    while (!child.empty() && child.back() == L'\\') child.remove_suffix(1);
    if (parent.empty()) return false;
    if (child.size() < parent.size()) return false;
    if (!EqualsIgnoreCase(child.substr(0, parent.size()), parent)) return false;
    return child.size() == parent.size() || child[parent.size()] == L'\\';
}

bool IsStrictlyUnder(std::wstring_view child, std::wstring_view parent) noexcept {
    while (!parent.empty() && parent.back() == L'\\') parent.remove_suffix(1);
    while (!child.empty() && child.back() == L'\\') child.remove_suffix(1);
    return IsSameOrUnder(child, parent) && child.size() > parent.size();
}

std::wstring KnownFolder(REFKNOWNFOLDERID id) {
    PWSTR raw = nullptr;
    std::wstring out;
    if (SUCCEEDED(::SHGetKnownFolderPath(id, KF_FLAG_DONT_VERIFY, nullptr, &raw)) && raw) out = raw;
    ::CoTaskMemFree(raw);
    return out;
}

std::wstring ModulePath() {
    std::wstring buffer(MAX_PATH, L'\0');
    for (;;) {
        const DWORD n = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (n == 0) return {};
        if (n < buffer.size()) {
            buffer.resize(n);
            return buffer;
        }
        buffer.resize(buffer.size() * 2);
    }
}

std::wstring TempRoot() {
    wchar_t buffer[MAX_PATH + 2] = {};
    // GetTempPath2W returns C:\Windows\SystemTemp for SYSTEM processes (not the shared, user-writable C:\Windows\Temp).
    using GetTempPath2Fn = DWORD(WINAPI*)(DWORD, LPWSTR);
    static const auto getTempPath2 = reinterpret_cast<GetTempPath2Fn>(
        ::GetProcAddress(::GetModuleHandleW(L"kernel32.dll"), "GetTempPath2W"));
    const DWORD n = getTempPath2 ? getTempPath2(MAX_PATH + 1, buffer) : ::GetTempPathW(MAX_PATH + 1, buffer);
    std::wstring out(buffer, n);
    while (!out.empty() && out.back() == L'\\') out.pop_back();
    return out;
}

std::wstring SanitizeName(std::wstring_view name) {
    std::wstring out;
    for (const wchar_t ch : name) {
        if (ch < 0x20 || wcschr(L"<>:\"/\\|?*", ch)) out.push_back(L'_');
        else out.push_back(ch);
    }
    std::wstring_view t = Trim(out);
    while (!t.empty() && (t.back() == L'.' || t.back() == L' ')) t.remove_suffix(1);
    std::wstring result(t);
    if (result.empty() || IsReservedDeviceName(result)) result = L"App" + result;
    if (result.size() > 64) result.resize(64);
    return result;
}

std::vector<std::wstring> Components(std::wstring_view rel) {
    std::vector<std::wstring> out;
    size_t start = 0;
    while (start <= rel.size()) {
        size_t end = rel.find_first_of(L"\\/", start);
        if (end == std::wstring_view::npos) end = rel.size();
        if (end > start) out.emplace_back(rel.substr(start, end - start));
        start = end + 1;
    }
    return out;
}

}  // namespace fsetup::path
