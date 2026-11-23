// Update feeds (update.feed) and update channels. A feed is a JSON object whose top-level fields describe the stable
// release, { "version", "url", "sha256", "size", "notes" }; other channels are entries of the same shape under
// "channels", e.g. { ..., "channels": { "beta": { "version": "2.0.0-beta.1", ... } } }. Feeds without "channels" are the
// format of FluentSetup 1.5 and earlier, which ignore the object. Pure functions: no network, no registry.
#pragma once

#include "Common/hash.h"
#include "Common/json.h"

#include <string>
#include <string_view>

namespace fsetup::boot {

inline constexpr wchar_t kStableChannel[] = L"stable";

// https:// URL without spaces, quotes, angle brackets, backslashes or control characters, at most 2000 characters.
bool IsSafeHttpsUrl(std::string_view url);
// 64 hex digits.
bool ParseDigest(std::string_view hex, Sha256Digest& out);

struct FeedRelease {
    std::wstring version, notesUrl;
    std::string url;
    Sha256Digest sha256{};
    uint64_t size = 0;      // 0 = not announced
    std::wstring channel;   // the entry it came from: "stable" (top-level fields or channels.stable) or the channel's name
};

enum class FeedLookup { Found, None, Invalid };

// The release `channel` offers: for "stable" the stable entry; for another channel the newer of its own entry and the
// stable one (a beta user gets the stable release once it is ahead), just the stable one when the feed has no entry of
// that name. None: the feed has no release for the channel (only other channels). Invalid: the document is not a feed,
// or an entry it needs is malformed (`error` says why).
FeedLookup PickFeedRelease(const json::Value& root, std::wstring_view channel, FeedRelease& out, std::wstring& error);

// The entry of exactly `version` in any channel (repair: the setup of the installed version).
FeedLookup FindFeedVersion(const json::Value& root, std::wstring_view version, FeedRelease& out, std::wstring& error);

// The channel an update check follows: /channel, else the one recorded at install time, else update.channel of the
// package, else stable. Names that are not channel names are skipped.
std::wstring EffectiveChannel(std::wstring_view requested, std::wstring_view recorded, std::wstring_view packageDefault);

}  // namespace fsetup::boot
