// UTF-8 / UTF-16 conversion and small string helpers.
#pragma once

#include "Common/win.h"

#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace fsetup {

std::wstring Utf8ToWide(std::string_view s);
std::string WideToUtf8(std::wstring_view s);

std::wstring ToLower(std::wstring_view s);
bool EqualsIgnoreCase(std::wstring_view a, std::wstring_view b) noexcept;
bool StartsWithIgnoreCase(std::wstring_view s, std::wstring_view prefix) noexcept;
std::wstring_view Trim(std::wstring_view s) noexcept;

std::string ToHex(std::span<const uint8_t> bytes);
bool FromHex(std::string_view hex, std::vector<uint8_t>& out);
bool FromBase64(std::string_view text, std::vector<uint8_t>& out);  // standard alphabet with '=' padding

// Replaces every "{0}", "{1}" ... with the matching argument.
std::wstring FormatIndexed(std::wstring_view pattern, std::initializer_list<std::wstring_view> args);

// printf-style formatting into a std::wstring.
std::wstring Printf(const wchar_t* fmt, ...);

// "12.3 MB" style, binary units, locale-neutral.
std::wstring FormatBytes(uint64_t bytes);

// System text for a Win32 error code ("Access is denied.") without trailing newline.
std::wstring Win32Message(DWORD error);

}  // namespace fsetup
