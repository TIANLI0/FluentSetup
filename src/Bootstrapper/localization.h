// String tables for both front ends. The tables (shared/localization/*.json) are embedded in the stub as RCDATA,
// selected by language and merged with per-project overrides from the package header ("ui.strings.<lang>").
#pragma once

#include "Common/json.h"

#include <initializer_list>
#include <string>
#include <string_view>

namespace fsetup::boot {

class Strings {
public:
    // requested: "/lang" value (may be empty). packageDefault: ui.defaultLanguage ("auto" or a tag).
    void Load(const std::wstring& requested, const std::wstring& packageDefault, const json::Value& overrides);

    [[nodiscard]] const std::wstring& Language() const noexcept { return language_; }
    [[nodiscard]] const json::Value& Table() const noexcept { return table_; }
    [[nodiscard]] std::wstring Get(std::string_view key) const;
    [[nodiscard]] std::wstring Fmt(std::string_view key, std::initializer_list<std::wstring_view> args) const;

private:
    std::wstring language_ = L"en-US";
    json::Value table_;
};

}  // namespace fsetup::boot
