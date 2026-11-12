// Product version handling: "1.2.3", "1.2.3.4", "2.0.0-beta.2". Up to four numeric parts plus an optional
// pre-release tag (SemVer precedence for the tag; a release sorts after any of its pre-releases).
#pragma once

#include <array>
#include <cstdint>
#include <string>
#include <string_view>

namespace fsetup {

struct Version {
    std::array<uint32_t, 4> parts{};
    std::wstring prerelease;

    static bool Parse(std::wstring_view text, Version& out) noexcept;
    [[nodiscard]] std::wstring ToString() const;
    // ARP "VersionMajor"/"VersionMinor" values.
    [[nodiscard]] uint32_t Major() const noexcept { return parts[0]; }
    [[nodiscard]] uint32_t Minor() const noexcept { return parts[1]; }
};

// <0 if a<b, 0 if equal, >0 if a>b.
int Compare(const Version& a, const Version& b) noexcept;

}  // namespace fsetup
