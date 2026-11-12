#include "Common/version.h"

#include <cwctype>
#include <vector>

namespace fsetup {

bool Version::Parse(std::wstring_view text, Version& out) noexcept {
    out = Version{};
    if (text.empty() || text.size() > 64) return false;
    const size_t dash = text.find(L'-');
    std::wstring_view numbers = text.substr(0, dash);
    if (dash != std::wstring_view::npos) {
        const std::wstring_view tag = text.substr(dash + 1);
        if (tag.empty()) return false;
        for (const wchar_t c : tag) {
            if (!(std::iswalnum(c) || c == L'.' || c == L'-') || c > 0x7F) return false;
        }
        out.prerelease.assign(tag);
    }
    size_t index = 0;
    size_t i = 0;
    while (i <= numbers.size()) {
        if (index >= 4) return false;
        uint64_t value = 0;
        size_t digits = 0;
        while (i < numbers.size() && numbers[i] >= L'0' && numbers[i] <= L'9') {
            value = value * 10 + static_cast<uint64_t>(numbers[i] - L'0');
            if (value > 0xFFFFFFFFull || ++digits > 10) return false;
            ++i;
        }
        if (digits == 0) return false;
        out.parts[index++] = static_cast<uint32_t>(value);
        if (i == numbers.size()) break;
        if (numbers[i] != L'.') return false;
        ++i;
        if (i == numbers.size()) return false;  // trailing dot
    }
    return index >= 1;
}

std::wstring Version::ToString() const {
    std::wstring s = std::to_wstring(parts[0]) + L"." + std::to_wstring(parts[1]) + L"." + std::to_wstring(parts[2]);
    if (parts[3] != 0) s += L"." + std::to_wstring(parts[3]);
    if (!prerelease.empty()) s += L"-" + prerelease;
    return s;
}

namespace {

std::vector<std::wstring_view> SplitDots(std::wstring_view s) {
    std::vector<std::wstring_view> out;
    size_t start = 0;
    while (start <= s.size()) {
        size_t end = s.find(L'.', start);
        if (end == std::wstring_view::npos) end = s.size();
        out.push_back(s.substr(start, end - start));
        start = end + 1;
    }
    return out;
}

bool IsNumeric(std::wstring_view s) {
    if (s.empty()) return false;
    for (const wchar_t c : s) {
        if (c < L'0' || c > L'9') return false;
    }
    return true;
}

}  // namespace

int Compare(const Version& a, const Version& b) noexcept {
    for (size_t i = 0; i < 4; ++i) {
        if (a.parts[i] != b.parts[i]) return a.parts[i] < b.parts[i] ? -1 : 1;
    }
    if (a.prerelease.empty() || b.prerelease.empty()) {
        if (a.prerelease.empty() && b.prerelease.empty()) return 0;
        return a.prerelease.empty() ? 1 : -1;  // release > pre-release
    }
    const auto pa = SplitDots(a.prerelease);
    const auto pb = SplitDots(b.prerelease);
    for (size_t i = 0; i < pa.size() && i < pb.size(); ++i) {
        const bool na = IsNumeric(pa[i]);
        const bool nb = IsNumeric(pb[i]);
        if (na && nb) {
            const auto va = std::wcstoull(std::wstring(pa[i]).c_str(), nullptr, 10);
            const auto vb = std::wcstoull(std::wstring(pb[i]).c_str(), nullptr, 10);
            if (va != vb) return va < vb ? -1 : 1;
        } else if (na != nb) {
            return na ? -1 : 1;  // numeric identifiers sort before alphanumeric
        } else {
            const int c = pa[i].compare(pb[i]);
            if (c != 0) return c < 0 ? -1 : 1;
        }
    }
    if (pa.size() != pb.size()) return pa.size() < pb.size() ? -1 : 1;
    return 0;
}

}  // namespace fsetup
