#include "Common/text.h"

#include <cstdarg>
#include <cwchar>

namespace fsetup {

std::wstring Utf8ToWide(std::string_view s) {
    if (s.empty()) return {};
    const int n = ::MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    if (n <= 0) return {};
    std::wstring out(static_cast<size_t>(n), L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n);
    return out;
}

std::string WideToUtf8(std::wstring_view s) {
    if (s.empty()) return {};
    const int n = ::WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(static_cast<size_t>(n), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n, nullptr, nullptr);
    return out;
}

std::wstring ToLower(std::wstring_view s) {
    std::wstring out(s);
    if (!out.empty()) ::CharLowerBuffW(out.data(), static_cast<DWORD>(out.size()));
    return out;
}

bool EqualsIgnoreCase(std::wstring_view a, std::wstring_view b) noexcept {
    if (a.size() != b.size()) return false;
    return ::CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) ==
           CSTR_EQUAL;
}

bool StartsWithIgnoreCase(std::wstring_view s, std::wstring_view prefix) noexcept {
    return s.size() >= prefix.size() && EqualsIgnoreCase(s.substr(0, prefix.size()), prefix);
}

std::wstring_view Trim(std::wstring_view s) noexcept {
    const auto isSpace = [](wchar_t c) { return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n'; };
    while (!s.empty() && isSpace(s.front())) s.remove_prefix(1);
    while (!s.empty() && isSpace(s.back())) s.remove_suffix(1);
    return s;
}

std::string ToHex(std::span<const uint8_t> bytes) {
    static constexpr char kDigits[] = "0123456789abcdef";
    std::string out;
    out.resize(bytes.size() * 2);
    for (size_t i = 0; i < bytes.size(); ++i) {
        out[i * 2] = kDigits[bytes[i] >> 4];
        out[i * 2 + 1] = kDigits[bytes[i] & 0xF];
    }
    return out;
}

bool FromHex(std::string_view hex, std::vector<uint8_t>& out) {
    if (hex.size() % 2 != 0) return false;
    const auto nibble = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    out.clear();
    out.reserve(hex.size() / 2);
    for (size_t i = 0; i < hex.size(); i += 2) {
        const int hi = nibble(hex[i]);
        const int lo = nibble(hex[i + 1]);
        if (hi < 0 || lo < 0) return false;
        out.push_back(static_cast<uint8_t>((hi << 4) | lo));
    }
    return true;
}

bool FromBase64(std::string_view text, std::vector<uint8_t>& out) {
    const auto sextet = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    out.clear();
    if (text.size() % 4 != 0) return false;
    out.reserve(text.size() / 4 * 3);
    for (size_t i = 0; i < text.size(); i += 4) {
        const bool last = i + 4 == text.size();
        int v[4];
        int pad = 0;
        for (int k = 0; k < 4; ++k) {
            const char c = text[i + k];
            if (c == '=' && last && k >= 2) {
                v[k] = 0;
                ++pad;
            } else {
                if (pad) return false;  // data after padding
                v[k] = sextet(c);
                if (v[k] < 0) return false;
            }
        }
        const uint32_t n = (static_cast<uint32_t>(v[0]) << 18) | (static_cast<uint32_t>(v[1]) << 12) | (static_cast<uint32_t>(v[2]) << 6) | static_cast<uint32_t>(v[3]);
        out.push_back(static_cast<uint8_t>(n >> 16));
        if (pad < 2) out.push_back(static_cast<uint8_t>(n >> 8));
        if (pad < 1) out.push_back(static_cast<uint8_t>(n));
    }
    return true;
}

std::wstring FormatIndexed(std::wstring_view pattern, std::initializer_list<std::wstring_view> args) {
    std::wstring out;
    out.reserve(pattern.size() + 32);
    for (size_t i = 0; i < pattern.size(); ++i) {
        if (pattern[i] == L'{' && i + 2 < pattern.size() && pattern[i + 2] == L'}' && pattern[i + 1] >= L'0' &&
            pattern[i + 1] <= L'9') {
            const size_t index = static_cast<size_t>(pattern[i + 1] - L'0');
            if (index < args.size()) out.append(*(args.begin() + index));
            i += 2;
        } else {
            out.push_back(pattern[i]);
        }
    }
    return out;
}

std::wstring Printf(const wchar_t* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    va_list copy;
    va_copy(copy, args);
    const int n = _vscwprintf(fmt, copy);
    va_end(copy);
    std::wstring out;
    if (n > 0) {
        out.resize(static_cast<size_t>(n));
        vswprintf_s(out.data(), out.size() + 1, fmt, args);
    }
    va_end(args);
    return out;
}

std::wstring FormatBytes(uint64_t bytes) {
    static constexpr const wchar_t* kUnits[] = {L"B", L"KB", L"MB", L"GB", L"TB"};
    double value = static_cast<double>(bytes);
    size_t unit = 0;
    while (value >= 1024.0 && unit + 1 < std::size(kUnits)) {
        value /= 1024.0;
        ++unit;
    }
    if (unit == 0) return Printf(L"%llu B", bytes);
    return Printf(value >= 100.0 ? L"%.0f %s" : L"%.1f %s", value, kUnits[unit]);
}

std::wstring Win32Message(DWORD error) {
    wchar_t* buffer = nullptr;
    const DWORD n = ::FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, error, 0,
        reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    std::wstring out;
    if (n != 0 && buffer != nullptr) {
        out.assign(buffer, n);
        ::LocalFree(buffer);
    }
    return std::wstring(Trim(out));
}

}  // namespace fsetup
