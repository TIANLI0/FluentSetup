#include "Bootstrapper/feed.h"

#include "Common/text.h"
#include "Common/version.h"
#include "Install.Core/installed.h"

#include <algorithm>
#include <vector>

namespace fsetup::boot {

namespace {

constexpr uint64_t kMaxSetupSize = 4ull * 1024 * 1024 * 1024;

// One release entry: { "version", "url", "sha256", "size", "notes" }.
bool ParseEntry(const json::Value& e, const std::wstring& channel, FeedRelease& out, std::wstring& error) {
    const std::wstring where = channel == kStableChannel ? std::wstring(L"The update feed") : L"Channel \"" + channel + L"\" of the update feed";
    if (!e.isObject()) return error = where + L" is not a JSON object", false;
    FeedRelease r;
    Version v;
    r.version = e["version"].asWide();
    r.url = e["url"].asString();
    r.size = e["size"].asUint();
    r.channel = channel;
    if (!Version::Parse(r.version, v)) error = where + L" has no valid \"version\"";
    else if (!IsSafeHttpsUrl(r.url)) error = where + L": \"url\" is not an https URL";
    else if (!ParseDigest(e["sha256"].asString(), r.sha256)) error = where + L" has no \"sha256\" (64 hex digits)";
    else if (r.size > kMaxSetupSize) error = where + L" announces an unexpected size";
    if (!error.empty()) return false;
    if (IsSafeHttpsUrl(e["notes"].asString())) r.notesUrl = e["notes"].asWide();
    out = std::move(r);
    return true;
}

// The stable entry: the top-level fields, or channels.stable in a feed that has no top-level release.
const json::Value* StableEntry(const json::Value& root) {
    if (root.has("version")) return &root;
    if (const json::Value& s = root["channels"]["stable"]; s.isObject()) return &s;
    return nullptr;
}

}  // namespace

bool IsSafeHttpsUrl(std::string_view url) {
    if (url.size() < 9 || url.size() > 2000 || url.rfind("https://", 0) != 0) return false;
    for (const char c : url) {
        const auto u = static_cast<unsigned char>(c);
        if (u <= 0x20 || u == 0x7F || c == '"' || c == '<' || c == '>' || c == '\\') return false;
    }
    return true;
}

bool ParseDigest(std::string_view hex, Sha256Digest& out) {
    std::vector<uint8_t> bytes;
    if (hex.size() != 64 || !FromHex(hex, bytes) || bytes.size() != out.size()) return false;
    std::copy(bytes.begin(), bytes.end(), out.begin());
    return true;
}

FeedLookup PickFeedRelease(const json::Value& root, std::wstring_view channel, FeedRelease& out, std::wstring& error) {
    error.clear();
    if (!root.isObject()) return error = L"The update feed is not a JSON object", FeedLookup::Invalid;
    const json::Value& channels = root["channels"];
    if (root.has("channels") && !channels.isObject()) return error = L"\"channels\" in the update feed is not a JSON object", FeedLookup::Invalid;
    const std::wstring name = channel.empty() ? std::wstring(kStableChannel) : std::wstring(channel);
    if (!install::IsUpdateChannelName(name)) return error = L"Not an update channel: " + name, FeedLookup::Invalid;

    FeedRelease stable, own;
    bool hasStable = false, hasOwn = false;
    if (const json::Value* s = StableEntry(root)) {
        if (!ParseEntry(*s, kStableChannel, stable, error)) return FeedLookup::Invalid;
        hasStable = true;
    } else if (!root.has("channels")) {
        return error = L"The update feed has no valid \"version\"", FeedLookup::Invalid;  // not a feed at all
    }
    if (name != kStableChannel && channels.has(WideToUtf8(name))) {
        if (!ParseEntry(channels[WideToUtf8(name)], name, own, error)) return FeedLookup::Invalid;
        hasOwn = true;
    }
    if (!hasStable && !hasOwn) return FeedLookup::None;
    if (hasStable && hasOwn) {
        Version a, b;
        Version::Parse(own.version, a);
        Version::Parse(stable.version, b);
        out = Compare(a, b) >= 0 ? std::move(own) : std::move(stable);
    } else {
        out = hasOwn ? std::move(own) : std::move(stable);
    }
    return FeedLookup::Found;
}

FeedLookup FindFeedVersion(const json::Value& root, std::wstring_view version, FeedRelease& out, std::wstring& error) {
    error.clear();
    Version wanted;
    if (!Version::Parse(version, wanted)) return error = L"Not a version: " + std::wstring(version), FeedLookup::Invalid;
    if (!root.isObject()) return error = L"The update feed is not a JSON object", FeedLookup::Invalid;
    std::vector<std::pair<const json::Value*, std::wstring>> entries;
    if (const json::Value* s = StableEntry(root)) entries.emplace_back(s, kStableChannel);
    for (const auto& [key, entry] : root["channels"].object()) {
        const std::wstring name = Utf8ToWide(key);
        if (name != kStableChannel && install::IsUpdateChannelName(name)) entries.emplace_back(&entry, name);
    }
    std::wstring advertised;
    for (const auto& [entry, name] : entries) {
        FeedRelease r;
        std::wstring why;
        Version v;
        if (!ParseEntry(*entry, name, r, why)) {
            if (error.empty()) error = why;
            continue;
        }
        Version::Parse(r.version, v);
        if (Compare(v, wanted) == 0) {
            out = std::move(r);
            error.clear();
            return FeedLookup::Found;
        }
        advertised += (advertised.empty() ? L"" : L", ") + r.version;
    }
    if (entries.empty()) return error = L"The update feed has no valid \"version\"", FeedLookup::Invalid;
    if (advertised.empty()) return FeedLookup::Invalid;  // every entry was malformed: `error` has the first reason
    error = L"The update feed offers version " + advertised + L", not the installed " + std::wstring(version);
    return FeedLookup::None;
}

std::wstring EffectiveChannel(std::wstring_view requested, std::wstring_view recorded, std::wstring_view packageDefault) {
    for (const std::wstring_view c : {requested, recorded, packageDefault}) {
        if (!c.empty() && install::IsUpdateChannelName(c)) return std::wstring(c);
    }
    return kStableChannel;
}

}  // namespace fsetup::boot
