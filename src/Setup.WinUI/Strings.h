// Localized strings for the WinUI front end. The string table is not compiled into this executable: the bootstrapper
// resolves the user's language, merges per-project overrides and sends the final table in the "hello" message, so that
// the WinUI and the Win32 front end always show exactly the same text.
#pragma once

#include "Common/json.h"

#include <initializer_list>
#include <string>
#include <string_view>

namespace fsui {

void SetStringTable(const fsetup::json::Value& table);
std::wstring Str(std::string_view key);
std::wstring Fmt(std::string_view key, std::initializer_list<std::wstring_view> args);

}  // namespace fsui
