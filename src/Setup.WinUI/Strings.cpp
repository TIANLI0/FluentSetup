#include "pch.h"

#include "Strings.h"

#include "Common/text.h"

namespace fsui {

namespace {
fsetup::json::Value& Table() {
    static fsetup::json::Value table;
    return table;
}
}  // namespace

void SetStringTable(const fsetup::json::Value& table) { Table() = table; }

std::wstring Str(std::string_view key) {
    const auto& v = Table()[key];
    if (v.isString()) return v.asWide();
    return fsetup::Utf8ToWide(key);  // visible but harmless fallback, makes missing keys easy to spot
}

std::wstring Fmt(std::string_view key, std::initializer_list<std::wstring_view> args) {
    return fsetup::FormatIndexed(Str(key), args);
}

}  // namespace fsui
