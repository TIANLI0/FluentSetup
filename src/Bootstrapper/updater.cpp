// Built-in updates: /check-update and /install-update on the installed uninstall.exe (or Setup.exe).
//
// The update source comes from the package header ("update", written from the project and kept in uninstall.exe):
// the latest release of a GitHub repository, or a JSON feed. Nothing runs before every check passed: https only, the
// published SHA-256 (GitHub's own asset digest and/or a checksums file, or the feed's), the file's integrity again
// through a handle that keeps it from changing, a valid FluentSetup package of the same app id and architecture, the
// advertised version (which must be newer than the installed one), and the signer when the project pins one. The new
// Setup.exe then runs with /passive (progress only) or /quiet and does the actual update, journaled as always.
//
// Differential downloads (update.differential, off unless the project turns it on): the downloaded Setup.exe is kept
// per user (%LocalAppData%\FluentSetup\updates\<app id>, the last one only), and the next update rebuilds the new
// Setup.exe from it (or from the Setup.exe that installed the app, if it is still there), byte for byte, fetching only
// what it does not have with range requests (install::RebuildFromBase). Without it, downloads go to a private
// temporary folder and are removed after use.
//
// Channels (update.channel, /channel): a feed may offer other releases than the stable one under "channels" (feed.h);
// on GitHub any channel but stable includes pre-releases. A check follows /channel, else the channel recorded at
// install time (Setup.exe /channel, which /install-update passes on), else update.channel.
//
// With administrator rights (an elevated prompt, a deployment tool running as SYSTEM) the verified Setup.exe runs from
// a private copy in a folder only administrators can change, never from the user's download folders (StartVerified).
#include "Bootstrapper/updater.h"

#include "Archive/container.h"
#include "Bootstrapper/feed.h"
#include "Common/error.h"
#include "Common/fs.h"
#include "Common/hash.h"
#include "Common/json.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/security.h"
#include "Common/text.h"
#include "Common/version.h"
#include "Install.Core/differential.h"
#include "Install.Core/extensions.h"
#include "Install.Core/installed.h"

#include <commctrl.h>
#include <shellapi.h>
#include <softpub.h>
#include <wincrypt.h>
#include <wintrust.h>

#include <algorithm>
#include <atomic>
#include <functional>
#include <list>
#include <map>
#include <memory>
#include <mutex>
#include <thread>

namespace fsetup::boot {
namespace {

constexpr size_t kMaxApiAnswer = 8u * 1024 * 1024;   // GitHub releases JSON
constexpr size_t kMaxSmallFile = 1u * 1024 * 1024;   // feed, checksums file
constexpr uint64_t kMaxSetupSize = 4ull * 1024 * 1024 * 1024;
constexpr wchar_t kDownloadPrefix[] = L"FluentSetup-upd-";
constexpr wchar_t kRunCopyPrefix[] = L"FluentSetup-n-";  // elevated updates: private copies of the new Setup.exe

struct Source {
    std::wstring github, feed, asset, checksums, signer;
    std::wstring channel = kStableChannel;  // the channel the check follows (EffectiveChannel)
    bool prerelease = false;
    bool differential = false;  // keep the download and rebuild the next one from it (update.differential)
};

struct Release {
    std::wstring version, fileName, notesUrl;
    std::string url;
    Sha256Digest sha256{};
    uint64_t size = 0;  // 0 = not announced (feed without "size")
};

enum class Lookup { Found, None, Failed };

std::wstring ReplaceAll(std::wstring s, std::wstring_view from, std::wstring_view to) {
    for (size_t at = s.find(from); at != std::wstring::npos; at = s.find(from, at + to.size())) s.replace(at, from.size(), to);
    return s;
}

bool IsRepositoryName(const std::wstring& s) {
    const size_t slash = s.find(L'/');
    if (slash == std::wstring::npos || slash == 0 || slash + 1 >= s.size() || s.find(L'/', slash + 1) != std::wstring::npos || s.size() > 140) {
        return false;
    }
    return std::all_of(s.begin(), s.end(), [](wchar_t c) { return (c < 128 && iswalnum(c)) || c == L'-' || c == L'.' || c == L'_' || c == L'/'; });
}

std::optional<Source> ReadSource(const install::Package& pkg, std::wstring& why) {
    const json::Value& u = pkg.raw["update"];
    if (!u.isObject()) {
        why = L"This package has no update source (project: update.github or update.feed)";
        return std::nullopt;
    }
    Source s;
    s.github = u["github"].asWide();
    s.feed = u["feed"].asWide();
    s.asset = u["asset"].asWide();
    s.checksums = u["checksums"].asWide();
    s.signer = u["signer"].asWide();
    s.prerelease = u["prerelease"].asBool();
    s.differential = u["differential"].asBool(false);
    if (const std::wstring channel = u["channel"].asWide(); !channel.empty()) s.channel = channel;
    if (s.github.empty() == s.feed.empty()) why = L"update needs either github or feed";
    else if (!install::IsUpdateChannelName(s.channel)) why = L"update.channel is not a channel name";
    else if (!s.github.empty() && !IsRepositoryName(s.github)) why = L"update.github is not owner/name";
    else if (!s.github.empty() && (s.asset.empty() || !path::IsValidComponent(ReplaceAll(s.asset, L"{Version}", L"1.0.0")))) why = L"update.asset is not a file name";
    else if (!s.checksums.empty() && !path::IsValidComponent(s.checksums)) why = L"update.checksums is not a file name";
    else if (!s.feed.empty() && !IsSafeHttpsUrl(WideToUtf8(s.feed))) why = L"update.feed is not an https URL";
    if (!why.empty()) return std::nullopt;
    return s;
}

std::wstring VersionFromTag(const std::wstring& tag) {
    return !tag.empty() && (tag[0] == L'v' || tag[0] == L'V') ? tag.substr(1) : tag;
}

// "<64 hex>  <name>" or "<64 hex> *<name>" (sha256sum text and binary mode).
bool FindChecksum(const std::string& text, const std::wstring& fileName, Sha256Digest& out) {
    size_t start = 0;
    while (start < text.size()) {
        size_t end = text.find('\n', start);
        if (end == std::string::npos) end = text.size();
        std::string line = text.substr(start, end - start);
        start = end + 1;
        while (!line.empty() && (line.back() == '\r' || line.back() == ' ' || line.back() == '\t')) line.pop_back();
        if (line.size() < 66 || (line[64] != ' ' && line[64] != '\t')) continue;
        size_t name = 64;
        while (name < line.size() && (line[name] == ' ' || line[name] == '\t')) ++name;
        if (name < line.size() && line[name] == '*') ++name;
        Sha256Digest digest{};
        if (EqualsIgnoreCase(Utf8ToWide(line.substr(name)), fileName) && ParseDigest(std::string_view(line).substr(0, 64), digest)) {
            out = digest;
            return true;
        }
    }
    return false;
}

// The release's Setup.exe asset (update.asset with {Version}); `githubDigest` tells whether GitHub published its SHA-256.
bool AssetFromRelease(const Source& s, const json::Value& release, const std::wstring& version, Release& out, bool& githubDigest) {
    const std::wstring fileName = ReplaceAll(s.asset, L"{Version}", version);
    for (const auto& a : release["assets"].array()) {
        if (!EqualsIgnoreCase(a["name"].asWide(), fileName)) continue;
        const std::string& url = a["browser_download_url"].asString();
        if (!IsSafeHttpsUrl(url)) return false;
        Release rel;
        rel.version = version;
        rel.fileName = fileName;
        rel.url = url;
        rel.size = a["size"].asUint();
        if (IsSafeHttpsUrl(release["html_url"].asString())) rel.notesUrl = release["html_url"].asWide();
        const std::string& digest = a["digest"].asString();  // "sha256:<hex>" on assets uploaded since mid-2025
        githubDigest = digest.size() == 71 && digest.rfind("sha256:", 0) == 0 && ParseDigest(std::string_view(digest).substr(7), rel.sha256);
        out = std::move(rel);
        return true;
    }
    return false;
}

// The SHA-256 of the chosen asset: from the checksums file (which must agree with GitHub's digest, if any) or GitHub's.
Lookup ResolveChecksum(const Source& s, const json::Value& release, bool githubDigest, Release& out, std::wstring& error) {
    unsigned long status = 0;
    if (!s.checksums.empty()) {
        std::string listUrl;
        for (const auto& a : release["assets"].array()) {
            if (EqualsIgnoreCase(a["name"].asWide(), s.checksums)) listUrl = a["browser_download_url"].asString();
        }
        if (!IsSafeHttpsUrl(listUrl)) {
            error = Printf(L"Release %s has no %s", out.version.c_str(), s.checksums.c_str());
            return Lookup::Failed;
        }
        std::string list;
        if (!install::HttpGetText(listUrl, nullptr, kMaxSmallFile, list, status, error)) {
            error = s.checksums + L": " + error;
            return Lookup::Failed;
        }
        Sha256Digest listed{};
        if (!FindChecksum(list, out.fileName, listed)) {
            error = Printf(L"%s has no SHA-256 for %s", s.checksums.c_str(), out.fileName.c_str());
            return Lookup::Failed;
        }
        if (githubDigest && listed != out.sha256) {
            error = Printf(L"%s and GitHub disagree about the SHA-256 of %s", s.checksums.c_str(), out.fileName.c_str());
            return Lookup::Failed;
        }
        out.sha256 = listed;
    } else if (!githubDigest) {
        error = Printf(L"GitHub publishes no SHA-256 for %s; add a checksums file (update.checksums)", out.fileName.c_str());
        return Lookup::Failed;
    }
    if (out.size == 0 || out.size > kMaxSetupSize) {
        error = Printf(L"Unexpected size of %s", out.fileName.c_str());
        return Lookup::Failed;
    }
    return Lookup::Found;
}

Lookup FromGitHub(const Source& s, Release& out, std::wstring& error) {
    const bool prerelease = s.prerelease || s.channel != kStableChannel;  // any channel but stable: pre-releases too
    const std::string api = "https://api.github.com/repos/" + WideToUtf8(s.github) + (prerelease ? "/releases?per_page=30" : "/releases/latest");
    const wchar_t* headers = L"Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n";
    std::string body;
    unsigned long status = 0;
    log::Info(L"Update source: GitHub %s (%s, channel %s)", s.github.c_str(), prerelease ? L"pre-releases included" : L"latest release", s.channel.c_str());
    if (!install::HttpGetText(api, headers, kMaxApiAnswer, body, status, error)) {
        if (status == 404 && !prerelease) {
            log::Info(L"GitHub: no published release (or no such repository)");
            return Lookup::None;
        }
        error = L"GitHub: " + error + (status == 403 || status == 429 ? L" (rate limit; try again later)" : L"");
        return Lookup::Failed;
    }
    json::Value root;
    if (!json::Parse(body, root) || !(root.isArray() || root.isObject())) {
        error = L"GitHub sent an answer that is not a release list";
        return Lookup::Failed;
    }
    std::vector<const json::Value*> releases;
    if (root.isArray()) {
        for (const auto& r : root.array()) releases.push_back(&r);
    } else {
        releases.push_back(&root);
    }

    const json::Value* chosen = nullptr;
    Version best{};
    bool githubDigest = false;
    for (const json::Value* r : releases) {
        if ((*r)["draft"].asBool() || ((*r)["prerelease"].asBool() && !prerelease)) continue;
        const std::wstring version = VersionFromTag((*r)["tag_name"].asWide());
        Version v;
        if (!Version::Parse(version, v) || (chosen && Compare(v, best) <= 0)) continue;
        Release rel;
        bool digest = false;
        if (!AssetFromRelease(s, *r, version, rel, digest)) {
            log::Info(L"GitHub release %s has no %s", version.c_str(), ReplaceAll(s.asset, L"{Version}", version).c_str());
            continue;
        }
        out = std::move(rel);
        githubDigest = digest;
        chosen = r;
        best = v;
    }
    if (!chosen) return Lookup::None;
    return ResolveChecksum(s, *chosen, githubDigest, out, error);
}

// The release of exactly `version` (repair): tag "v<version>", else "<version>"; drafts are skipped, pre-releases
// count (the installed version may be one).
Lookup FromGitHubTag(const Source& s, const std::wstring& version, Release& out, std::wstring& error) {
    if (version.empty() || version.size() > 64 ||
        !std::all_of(version.begin(), version.end(), [](wchar_t c) { return (c < 128 && iswalnum(c)) || c == L'.' || c == L'-' || c == L'+'; })) {
        error = L"The installed version cannot be looked up: " + version;
        return Lookup::Failed;
    }
    const wchar_t* headers = L"Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n";
    for (const std::wstring& tag : {L"v" + version, version}) {
        std::string body;
        unsigned long status = 0;
        const std::string api = "https://api.github.com/repos/" + WideToUtf8(s.github) + "/releases/tags/" + WideToUtf8(tag);
        log::Info(L"Repair source: GitHub %s, release %s", s.github.c_str(), tag.c_str());
        if (!install::HttpGetText(api, headers, kMaxApiAnswer, body, status, error)) {
            if (status == 404) {
                error.clear();
                continue;
            }
            error = L"GitHub: " + error + (status == 403 || status == 429 ? L" (rate limit; try again later)" : L"");
            return Lookup::Failed;
        }
        json::Value release;
        if (!json::Parse(body, release) || !release.isObject()) {
            error = L"GitHub sent an answer that is not a release";
            return Lookup::Failed;
        }
        if (release["draft"].asBool()) continue;
        bool githubDigest = false;
        if (!AssetFromRelease(s, release, version, out, githubDigest)) {
            error = Printf(L"GitHub release %s has no %s", tag.c_str(), ReplaceAll(s.asset, L"{Version}", version).c_str());
            return Lookup::None;
        }
        return ResolveChecksum(s, release, githubDigest, out, error);
    }
    error = L"GitHub has no release of version " + version;
    return Lookup::None;
}

// The feed document; Failed when it cannot be read or is not JSON.
Lookup ReadFeed(const Source& s, json::Value& root, std::wstring& error) {
    std::string body;
    unsigned long status = 0;
    log::Info(L"Update source: feed %s (channel %s)", s.feed.c_str(), s.channel.c_str());
    if (!install::HttpGetText(WideToUtf8(s.feed), L"Accept: application/json\r\n", kMaxSmallFile, body, status, error)) {
        error = L"Update feed: " + error;
        return Lookup::Failed;
    }
    if (!json::Parse(body, root) || !root.isObject()) {
        error = L"The update feed is not a JSON object";
        return Lookup::Failed;
    }
    return Lookup::Found;
}

Lookup FromFeedRelease(FeedLookup found, FeedRelease&& r, const std::wstring& appId, Release& out) {
    if (found != FeedLookup::Found) return found == FeedLookup::None ? Lookup::None : Lookup::Failed;
    if (r.channel != kStableChannel) log::Info(L"Feed channel %s: version %s", r.channel.c_str(), r.version.c_str());
    out.version = std::move(r.version);
    out.url = std::move(r.url);
    out.sha256 = r.sha256;
    out.size = r.size;
    out.notesUrl = std::move(r.notesUrl);
    out.fileName = path::SanitizeName(appId + L"-" + out.version + L"-Setup.exe");
    return Lookup::Found;
}

Lookup FromFeed(const Source& s, const std::wstring& appId, Release& out, std::wstring& error) {
    json::Value root;
    if (const Lookup read = ReadFeed(s, root, error); read != Lookup::Found) return read;
    FeedRelease r;
    const FeedLookup found = PickFeedRelease(root, s.channel, r, error);
    if (found == FeedLookup::None) log::Info(L"The update feed has no release for channel %s", s.channel.c_str());
    return FromFeedRelease(found, std::move(r), appId, out);
}

// The newest published version, if it is newer than `installed`.
Lookup FindUpdate(const Source& s, const std::wstring& appId, const std::wstring& installed, Release& out, std::wstring& error) {
    const Lookup found = s.github.empty() ? FromFeed(s, appId, out, error) : FromGitHub(s, out, error);
    if (found != Lookup::Found) return found;
    Version latest, current;
    if (!Version::Parse(installed, current)) {
        error = L"The installed version is not a valid version: " + installed;
        return Lookup::Failed;
    }
    Version::Parse(out.version, latest);
    log::Info(L"Published version %s, installed %s", out.version.c_str(), installed.c_str());
    return Compare(latest, current) > 0 ? Lookup::Found : Lookup::None;
}

// The release of exactly `version` from the update source (repair).
Lookup FindExactRelease(const Source& s, const std::wstring& appId, const std::wstring& version, Release& out, std::wstring& error) {
    if (!s.github.empty()) return FromGitHubTag(s, version, out, error);
    json::Value root;
    if (const Lookup read = ReadFeed(s, root, error); read != Lookup::Found) return read;
    FeedRelease r;
    const FeedLookup found = FindFeedVersion(root, version, r, error);  // in any channel: the installed one may be a beta
    return FromFeedRelease(found, std::move(r), appId, out);
}

// Old download folders: a Setup.exe still running from one simply keeps it until next time.
void SweepOldDownloads() {
    const std::wstring tempRoot = path::TempRoot();
    WIN32_FIND_DATAW fd{};
    UniqueFind find(::FindFirstFileExW(path::Join(tempRoot, std::wstring(kDownloadPrefix) + L"*").c_str(), FindExInfoBasic, &fd,
                                       FindExSearchLimitToDirectories, nullptr, 0));
    for (bool more = find.valid(); more; more = ::FindNextFileW(find.get(), &fd)) {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) continue;
        const std::wstring dir = path::Join(tempRoot, fd.cFileName);
        {
            WIN32_FIND_DATAW f{};
            UniqueFind files(::FindFirstFileExW(path::Join(dir, L"*").c_str(), FindExInfoBasic, &f, FindExSearchNameMatch, nullptr, 0));
            for (bool m = files.valid(); m; m = ::FindNextFileW(files.get(), &f)) {
                if (!(f.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) ::DeleteFileW(path::Join(dir, f.cFileName).c_str());
            }
        }  // search handle closed before the folder is removed
        ::RemoveDirectoryW(dir.c_str());
    }
}

struct Downloaded {
    std::wstring path;
    UniqueHandle lock;  // read-only, shared for reading only: the file cannot change until Setup has started from it
    bool kept = false;  // in the app's update folder, kept for the next update (and as the repair source)
};

// ---- differential downloads (install::RebuildFromBase) ---------------------------------------------------------

// %LocalAppData%\FluentSetup\updates\<app id>: the last downloaded Setup.exe of the app.
std::wstring KeptFolder(const std::wstring& appId) {
    const std::wstring local = path::KnownFolder(FOLDERID_LocalAppData);
    return local.empty() ? std::wstring() : path::Join(local, L"FluentSetup\\updates\\" + appId);
}

// Earlier Setup.exe files of the app to take streams from, best first: the one kept by the last update, then the one
// that installed the current version, if it is still where it was.
std::vector<std::wstring> BasePackages(const std::wstring& appId) {
    std::vector<std::pair<FILETIME, std::wstring>> kept;
    if (const std::wstring dir = KeptFolder(appId); !dir.empty()) {
        WIN32_FIND_DATAW fd{};
        UniqueFind find(::FindFirstFileExW(path::Join(dir, L"*.exe").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, 0));
        for (bool more = find.valid(); more; more = ::FindNextFileW(find.get(), &fd)) {
            if (!(fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) kept.emplace_back(fd.ftLastWriteTime, path::Join(dir, fd.cFileName));
        }
    }
    std::sort(kept.begin(), kept.end(), [](const auto& a, const auto& b) { return ::CompareFileTime(&a.first, &b.first) > 0; });
    std::vector<std::wstring> out;
    for (auto& k : kept) out.push_back(std::move(k.second));
    if (const auto product = install::FindInstalled(appId)) {
        try {
            const install::InstallRecord record = install::LoadVerifiedRecord(*product);
            if (!record.sourcePackage.empty() && std::none_of(out.begin(), out.end(), [&](const std::wstring& o) { return EqualsIgnoreCase(o, record.sourcePackage); })) {
                out.push_back(record.sourcePackage);
            }
        } catch (const Failure&) {
            // no usable record: only the kept download
        }
    }
    return out;
}

bool DownloadDifferential(const Release& r, const std::wstring& basePath, const std::wstring& dest, const std::atomic<bool>& cancel,
                          const std::function<void(uint64_t, uint64_t)>& progress, std::wstring& why) {
    const install::RangeGetter get = [&](uint64_t first, uint64_t end, uint64_t& total, const std::function<bool(const uint8_t*, size_t)>& sink,
                                         std::wstring& error) { return install::HttpGetRange(r.url, first, end - 1, total, sink, cancel, error); };
    return install::RebuildFromBase(get, r.size, basePath, dest, cancel, progress, why);
}

// Opens the finished download read-only (no other writers from now on) and hashes it once more through that handle.
bool LockAndCheck(const Release& r, Downloaded& out, std::wstring& error) {
    out.lock.reset(::CreateFileW(path::Long(out.path).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!out.lock) {
        error = L"Cannot open the download: " + Win32Message(::GetLastError());
        return false;
    }
    Sha256 hash;
    std::vector<uint8_t> buffer(256 * 1024);
    uint64_t size = 0;
    DWORD got = 0;
    while (::ReadFile(out.lock.get(), buffer.data(), static_cast<DWORD>(buffer.size()), &got, nullptr) && got > 0) {
        hash.Update(buffer.data(), got);
        size += got;
    }
    if (hash.Finish() != r.sha256 || (r.size != 0 && size != r.size)) {
        error = L"The downloaded file does not match the published SHA-256";
        out.lock.reset();
        return false;
    }
    return true;
}

bool DownloadRelease(const Release& r, const Source& s, const std::wstring& appId, const std::atomic<bool>& cancel,
                     const std::function<void(uint64_t, uint64_t)>& progress, Downloaded& out, std::wstring& error) {
    SweepOldDownloads();
    std::wstring name = r.fileName;
    if (!path::IsValidComponent(name) || name.size() < 5 || !EqualsIgnoreCase(std::wstring_view(name).substr(name.size() - 4), L".exe")) name = L"Setup.exe";

    // update.differential: the download goes to the app's update folder and stays; otherwise a private temporary folder.
    std::wstring dir;
    if (s.differential) {
        if (const std::wstring kept = KeptFolder(appId); !kept.empty()) {
            try {
                fs::CreateDirectories(kept);
                dir = kept;
                out.kept = true;
            } catch (const Failure& f) {
                log::Warn(L"Cannot create %s (%s); the download is not kept", kept.c_str(), f.detail().c_str());
            }
        }
    }
    if (!out.kept) {
        dir = path::Join(path::TempRoot(), std::wstring(kDownloadPrefix) + RandomToken().substr(0, 16));
        if (const DWORD e = security::CreatePrivateDirectory(dir)) {
            error = L"Cannot create a download folder: " + Win32Message(e);
            return false;
        }
    }
    const std::wstring final = path::Join(dir, name);
    const std::wstring partial = out.kept ? path::Join(dir, L"." + RandomToken().substr(0, 12) + L".partial") : final;
    const auto discard = [&] {
        out.lock.reset();
        ::DeleteFileW(path::Long(partial).c_str());
        if (!out.kept) ::RemoveDirectoryW(dir.c_str());
    };

    bool differential = false;
    if (out.kept) {
        for (const auto& base : BasePackages(appId)) {
            std::wstring why;
            if (DownloadDifferential(r, base, partial, cancel, progress, why)) {
                differential = true;
                break;
            }
            ::DeleteFileW(path::Long(partial).c_str());
            if (cancel) {
                error = L"Cancelled";
                discard();
                return false;
            }
            log::Info(L"No differential download from %s: %s", base.c_str(), why.c_str());
        }
    }
    out.path = partial;
    if (differential && !LockAndCheck(r, out, error)) {
        log::Warn(L"The rebuilt download does not match (%s); downloading the whole file", error.c_str());
        ::DeleteFileW(path::Long(partial).c_str());
        differential = false;
        error.clear();
    }
    if (!differential) {
        log::Info(L"Downloading %S to %s", r.url.c_str(), partial.c_str());
        if (!install::Download(r.url, partial, r.sha256, r.size, cancel, progress, error) || !LockAndCheck(r, out, error)) {
            discard();
            return false;
        }
    }
    if (!out.kept) return true;

    // Kept: the download under its own name, and the earlier one goes. The file is checked once more after the move.
    out.lock.reset();
    if (::MoveFileExW(path::Long(partial).c_str(), path::Long(final).c_str(), MOVEFILE_REPLACE_EXISTING)) out.path = final;
    if (!LockAndCheck(r, out, error)) {
        discard();
        ::DeleteFileW(path::Long(out.path).c_str());
        return false;
    }
    WIN32_FIND_DATAW fd{};
    UniqueFind find(::FindFirstFileExW(path::Join(dir, L"*").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, 0));
    for (bool more = find.valid(); more; more = ::FindNextFileW(find.get(), &fd)) {
        if (fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) continue;
        const std::wstring other = path::Join(dir, fd.cFileName);
        if (!EqualsIgnoreCase(other, out.path)) ::DeleteFileW(path::Long(other).c_str());  // one still running stays until next time
    }
    return true;
}

// A download that will not be run: release the lock, delete it and its private folder.
void Discard(Downloaded& d) {
    d.lock.reset();
    if (d.path.empty()) return;
    ::DeleteFileW(d.path.c_str());
    ::RemoveDirectoryW(path::Parent(d.path).c_str());
    d.path.clear();
}

// `appId`, `architecture`: of the installed package.
bool VerifyPackage(const std::wstring& file, const std::wstring& appId, const std::wstring& architecture, const Release& r, const Source& s,
                   std::wstring& error) {
    try {
        const auto container = archive::Container::Open(file);
        long status = 0;
        if (const auto state = archive::CheckSignature(file, &status); state == archive::SignatureState::Tampered || state == archive::SignatureState::Distrusted) {
            error = Printf(L"The digital signature of the download does not match the file (0x%08lX)", status);
            return false;
        }
        const install::Package next = install::ParsePackage(
            container->HeaderJson(), [&](uint64_t offset, uint64_t size) { return container->StreamRangeValid(offset, size); });
        Version a, b;
        if (next.IsUninstaller()) error = L"The download is an uninstaller, not a setup";
        else if (!EqualsIgnoreCase(next.appId, appId)) error = L"The download is the setup of another app: " + next.appId;
        else if (!next.architecture.empty() && !architecture.empty() && !EqualsIgnoreCase(next.architecture, architecture)) {
            error = Printf(L"The download is for %s, not %s", next.architecture.c_str(), architecture.c_str());
        } else if (!Version::Parse(next.version, a) || !Version::Parse(r.version, b) || Compare(a, b) != 0) {
            error = Printf(L"The download contains version %s, not %s", next.version.c_str(), r.version.c_str());
        }
        if (!error.empty()) return false;
    } catch (const Failure& f) {
        error = L"The download is not a valid setup package: " + f.detail();
        return false;
    }
    if (!s.signer.empty()) {
        const std::wstring signer = archive::TrustedSignerName(file);
        if (signer.empty()) {
            error = L"The download does not carry a valid, trusted signature (update.signer is set)";
            return false;
        }
        if (!EqualsIgnoreCase(signer, s.signer)) {
            error = Printf(L"The download is signed by \"%s\", not \"%s\"", signer.c_str(), s.signer.c_str());
            return false;
        }
        log::Info(L"Signed by %s", signer.c_str());
    }
    return true;
}

// Copies the file open as `source` (read through that handle, which keeps it from changing) into a new folder only
// administrators can change (security::CreateRunDirectory) and opens the copy read-only, shared for reading only; it
// must hash to `expected`. On failure nothing is left behind.
bool PrivateCopy(HANDLE source, const std::wstring& sourcePath, const Sha256Digest& expected, const wchar_t* prefix, std::wstring& copy,
                 UniqueHandle& copyLock, std::wstring& why) {
    DWORD e = 0;
    const std::wstring dir = security::CreateRunDirectory(prefix, e);
    if (dir.empty()) return why = L"cannot create a private folder for the setup: " + Win32Message(e), false;
    const std::wstring file = path::Join(dir, path::FileName(sourcePath));
    const auto fail = [&](const std::wstring& what) {
        ::DeleteFileW(path::Long(file).c_str());
        ::RemoveDirectoryW(path::Long(dir).c_str());
        why = what;
        return false;
    };
    {
        UniqueHandle dest(::CreateFileW(path::Long(file).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (!dest) return fail(L"cannot create " + file + L": " + Win32Message(::GetLastError()));
        std::vector<uint8_t> buffer(4u << 20);
        for (uint64_t offset = 0;;) {
            OVERLAPPED ov{};
            ov.Offset = static_cast<DWORD>(offset);
            ov.OffsetHigh = static_cast<DWORD>(offset >> 32);
            DWORD read = 0, written = 0;
            if (!::ReadFile(source, buffer.data(), static_cast<DWORD>(buffer.size()), &read, &ov) && ::GetLastError() != ERROR_HANDLE_EOF) {
                return fail(L"cannot read " + sourcePath + L": " + Win32Message(::GetLastError()));
            }
            if (read == 0) break;
            if (!::WriteFile(dest.get(), buffer.data(), read, &written, nullptr) || written != read) {
                return fail(L"cannot write " + file + L": " + Win32Message(::GetLastError()));
            }
            offset += read;
        }
    }
    UniqueHandle lock(::CreateFileW(path::Long(file).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!lock) return fail(L"cannot open " + file + L": " + Win32Message(::GetLastError()));
    try {
        if (fs::HashFile(lock.get()) != expected) return fail(L"the copy of " + sourcePath + L" does not match");
    } catch (const Failure& f) {
        return fail(f.detail());
    }
    copy = file;
    copyLock = std::move(lock);
    return true;
}

// With administrator rights, the setup for a repair runs from a copy in a folder only administrators can change
// (security::CreateRunDirectory): its own folder (Downloads, %TEMP%, the kept update folder) belongs to the user, who
// could plant DLLs next to it. The copy is made from the locked original and must hash to `expected`.
bool CopyToRunDirectory(RepairPackage& out, const Sha256Digest& expected, std::wstring& why) {
    std::wstring copy;
    UniqueHandle lock;
    if (!PrivateCopy(out.lock.get(), out.path, expected, L"FluentSetup-r-", copy, lock, why)) return false;
    // The original is released and stays where it was; the copy and its folder go with `out`.
    out.lock = std::move(lock);
    out.path = copy;
    out.downloaded = true;
    log::Info(L"Repair: running a private copy of the setup: %s", copy.c_str());
    return true;
}

// Private copies of earlier elevated updates (kRunCopyPrefix in %SystemRoot%\Temp): a setup cannot delete the file it
// runs from, so each copy is also scheduled for deletion at the next restart; this removes the ones whose setup has
// ended in the meantime. Only folders that administrators control, never through a link.
void SweepRunCopies() {
    wchar_t windows[MAX_PATH + 1] = {};
    const UINT n = ::GetSystemWindowsDirectoryW(windows, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return;
    const std::wstring temp = path::Join(std::wstring(windows, n), L"Temp");
    WIN32_FIND_DATAW fd{};
    UniqueFind find(::FindFirstFileExW(path::Join(temp, std::wstring(kRunCopyPrefix) + L"*").c_str(), FindExInfoBasic, &fd,
                                       FindExSearchLimitToDirectories, nullptr, 0));
    for (bool more = find.valid(); more; more = ::FindNextFileW(find.get(), &fd)) {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) continue;
        const std::wstring dir = path::Join(temp, fd.cFileName);
        if (!security::IsAdministratorControlled(dir)) continue;
        {
            WIN32_FIND_DATAW f{};
            UniqueFind files(::FindFirstFileExW(path::Join(dir, L"*").c_str(), FindExInfoBasic, &f, FindExSearchNameMatch, nullptr, 0));
            for (bool m = files.valid(); m; m = ::FindNextFileW(files.get(), &f)) {
                if (!(f.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) ::DeleteFileW(path::Join(dir, f.cFileName).c_str());
            }
        }
        ::RemoveDirectoryW(dir.c_str());
    }
}

// The Setup.exe recorded at install time, if it is still there and unchanged: the whole file must hash to what the
// verified install record says (the record is checked against the hash in the Apps & features entry), read through a
// handle that keeps it from being changed, replaced or renamed until the setup has started from it. A record without
// that hash (written by FluentSetup 1.5.0 or older, which kept only the header digest, not covering the code in front
// of the package) never runs the file: the repair downloads the setup instead, as when the file is gone.
bool RecordedPackage(const install::Package& pkg, const std::wstring& version, const install::InstallRecord& record, RepairPackage& out,
                     std::wstring& why) {
    const std::wstring& file = record.sourcePackage;
    if (file.empty()) return why = L"the install record names no original setup", false;
    Sha256Digest expected{};
    if (!ParseDigest(WideToUtf8(record.sourcePackageFileSha256), expected)) {
        return why = L"the install record has no checksum of the whole original setup (installed by an older FluentSetup)", false;
    }
    UniqueHandle lock(::CreateFileW(path::Long(file).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!lock) return why = file + L" is no longer available", false;
    BY_HANDLE_FILE_INFORMATION info{};
    if (!::GetFileInformationByHandle(lock.get(), &info) || (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) {
        return why = file + L" is not a plain file", false;
    }
    try {
        if (fs::HashFile(lock.get()) != expected) return why = file + L" is not the setup that installed this version", false;
    } catch (const Failure& f) {
        return why = file + L": " + f.detail(), false;
    }
    // The publisher: update.signer when the project pins one, and the signer of the installed uninstall.exe when it
    // carries a trusted signature (a signed app is repaired only with a setup of the same publisher).
    std::wstring ignored;
    const std::optional<Source> source = ReadSource(pkg, ignored);
    std::vector<std::wstring> signers;
    if (source && !source->signer.empty()) signers.push_back(source->signer);
    if (const std::wstring installed = archive::TrustedSignerName(path::Join(record.installDir, install::kUninstallerName)); !installed.empty()) {
        signers.push_back(installed);
    }
    for (const auto& signer : signers) {
        std::wstring actual;
        if (!archive::SignedBy(file, signer, &actual, lock.get())) {
            return why = file + (actual.empty() ? std::wstring(L" carries no trusted signature") : L" is signed by \"" + actual + L"\"") +
                         L", not by \"" + signer + L"\"",
                   false;
        }
    }
    Release r;
    r.version = version;
    if (!VerifyPackage(file, pkg.appId, pkg.architecture, r, Source{}, why)) return false;
    out.path = file;
    out.lock = std::move(lock);
    out.downloaded = false;
    if (security::IsElevated()) {
        if (!CopyToRunDirectory(out, expected, why)) {
            out.lock.reset();
            out.path.clear();
            return false;
        }
        out.original = file;  // recorded again by the setup (it checks that the file is still identical)
    }
    return true;
}

// `recordAs`: when `file` is a private copy, the original it was made from (--source-package), may be empty.
bool StartSetup(const std::wstring& file, const Args& args, bool quiet, const std::wstring& recordAs, std::wstring& error) {
    std::wstring cmd = QuoteArg(file) + (quiet ? L" /quiet" : L" /passive");
    if (args.relaunch) cmd += L" /relaunch";
    if (!args.language.empty()) cmd += L" /lang " + QuoteArg(args.language);
    if (!args.channel.empty()) cmd += L" /channel " + args.channel;  // recorded: later checks follow it
    if (!recordAs.empty()) cmd += L" --source-package " + QuoteArg(recordAs);
    log::Info(L"Starting %s", cmd.c_str());
    ::AllowSetForegroundWindow(ASFW_ANY);
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    const std::wstring dir = path::Parent(file);
    if (!::CreateProcessW(file.c_str(), cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, dir.c_str(), &si, &pi)) {
        error = L"Cannot start the new setup: " + Win32Message(::GetLastError());
        return false;
    }
    ::CloseHandle(pi.hThread);
    ::CloseHandle(pi.hProcess);
    return true;
}

// Starts the verified download `d` of release `r`. With administrator rights it does not run from where it was
// downloaded (the user's %TEMP% or the kept update folder, where the user's own programs could plant DLLs or swap
// files): it is copied through its deny-write handle into a folder only administrators can change, hashed again there
// and started from the copy, as for a repair. The copy cannot be deleted while it runs: it goes at the next restart, or
// with the next elevated update. A kept download stays the file the setup records (--source-package); a temporary one is
// deleted at once.
bool StartVerified(Downloaded& d, const Release& r, const Args& args, bool quiet, std::wstring& error) {
    if (!security::IsElevated()) return StartSetup(d.path, args, quiet, {}, error);
    SweepRunCopies();
    std::wstring copy;
    UniqueHandle lock;
    if (!PrivateCopy(d.lock.get(), d.path, r.sha256, kRunCopyPrefix, copy, lock, error)) return false;
    log::Info(L"Running a private copy of the new setup: %s", copy.c_str());
    const std::wstring recordAs = d.kept ? d.path : std::wstring();
    if (d.kept) d.lock.reset();
    else Discard(d);
    const std::wstring dir = path::Parent(copy);
    if (!StartSetup(copy, args, quiet, recordAs, error)) {
        lock.reset();
        ::DeleteFileW(path::Long(copy).c_str());
        ::RemoveDirectoryW(path::Long(dir).c_str());
        return false;
    }
    ::MoveFileExW(path::Long(copy).c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
    ::MoveFileExW(path::Long(dir).c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
    return true;
}

// ---- /check-update ------------------------------------------------------------------------------------------------

// One JSON line on standard output, for apps that start uninstall.exe with a redirected stdout.
void WriteResult(const json::Value& v) {
    const HANDLE out = ::GetStdHandle(STD_OUTPUT_HANDLE);
    if (!out || out == INVALID_HANDLE_VALUE) return;
    const std::string text = json::Serialize(v) + "\n";
    DWORD written = 0;
    ::WriteFile(out, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
}

int CheckOnly(const install::Package& pkg, const Source& s, const std::wstring& installed) {
    Release r;
    std::wstring error;
    const Lookup found = FindUpdate(s, pkg.appId, installed, r, error);
    json::Value result = json::Value::MakeObject();
    result.set("installedVersion", installed);
    result.set("channel", s.channel);
    if (found == Lookup::Failed) {
        log::Error(L"Update check failed: %s", error.c_str());
        result.set("error", error);
        WriteResult(result);
        return static_cast<int>(ErrorCode::UpdateFailed);
    }
    result.set("updateAvailable", found == Lookup::Found);
    if (found == Lookup::Found) {
        result.set("latestVersion", r.version);
        if (!r.notesUrl.empty()) result.set("notes", r.notesUrl);
    }
    WriteResult(result);
    log::Info(found == Lookup::Found ? L"Update available: %s" : L"Up to date%s", found == Lookup::Found ? r.version.c_str() : L"");
    return static_cast<int>(found == Lookup::Found ? ErrorCode::UpdateAvailable : ErrorCode::Ok);
}

// ---- /install-update /quiet ---------------------------------------------------------------------------------------

int InstallQuietly(const install::Package& pkg, const Source& s, const std::wstring& installed, const Args& args) {
    Release r;
    std::wstring error;
    const Lookup found = FindUpdate(s, pkg.appId, installed, r, error);
    if (found == Lookup::None) {
        log::Info(L"Up to date");
        return 0;
    }
    std::atomic<bool> cancel{false};
    Downloaded d;
    if (found == Lookup::Failed || !DownloadRelease(r, s, pkg.appId, cancel, nullptr, d, error)) {
        log::Error(L"Update failed: %s", error.c_str());
        return static_cast<int>(ErrorCode::UpdateFailed);
    }
    if (!VerifyPackage(d.path, pkg.appId, pkg.architecture, r, s, error) || !StartVerified(d, r, args, true, error)) {
        log::Error(L"Update refused: %s", error.c_str());
        Discard(d);
        return static_cast<int>(ErrorCode::UpdateFailed);
    }
    return 0;
}

// ---- /install-update with a window -----------------------------------------------------------------------------------

enum class Phase { Checking, UpToDate, Available, Downloading, Verifying, Ready, CheckFailed, Failed };

struct Shared {
    std::mutex lock;
    Phase phase = Phase::Checking;
    Release release;
    std::wstring error;
    Downloaded download;
    std::atomic<uint64_t> done{0}, total{0};
    std::atomic<bool> cancel{false};
    std::atomic<int> running{0};  // worker threads not finished yet

    void Set(Phase p, std::wstring e = {}) {
        std::lock_guard g(lock);
        phase = p;
        if (!e.empty()) error = std::move(e);
    }
    Phase Get() {
        std::lock_guard g(lock);
        return phase;
    }
};

class UpdateWindow {
public:
    UpdateWindow(const install::Package& pkg, const Source& s, const Strings& strings, std::wstring installed, bool passive, HICON icon)
        : pkg_(pkg), source_(s), strings_(strings), installed_(std::move(installed)), passive_(passive), icon_(icon),
          shared_(std::make_shared<Shared>()) {}

    // Returns the exit code; on success the new Setup.exe has been started.
    int Run(const Args& args) {
        // Worker threads get copies only, and a reference to the shared state: a cancelled check may still be waiting
        // for the network after the window closed.
        auto shared = shared_;
        const Source source = source_;
        const std::wstring appId = pkg_.appId, installed = installed_;
        ++shared->running;
        std::thread([shared, source, appId, installed] {
            Release r;
            std::wstring error;
            const Lookup found = FindUpdate(source, appId, installed, r, error);
            if (found == Lookup::Failed) log::Error(L"Update check failed: %s", error.c_str());
            {
                std::lock_guard g(shared->lock);
                shared->release = r;
            }
            shared->Set(found == Lookup::Found ? Phase::Available : found == Lookup::None ? Phase::UpToDate : Phase::CheckFailed, error);
            --shared->running;
        }).detach();

        TASKDIALOGCONFIG config = Page(Phase::Checking);
        int button = 0;
        if (FAILED(::TaskDialogIndirect(&config, &button, nullptr, nullptr))) {
            log::Error(L"The update window could not be shown");
            shared_->cancel = true;
            return static_cast<int>(ErrorCode::Internal);
        }
        const Phase end = shared_->Get();
        int code = 0;
        if (end == Phase::Ready && !shared_->cancel) {
            std::wstring error;
            if (!StartVerified(shared_->download, shared_->release, args, false, error)) {
                log::Error(L"%s", error.c_str());
                Discard(shared_->download);
                code = static_cast<int>(ErrorCode::UpdateFailed);
            }
        } else {
            shared_->cancel = true;
            if (end == Phase::CheckFailed || end == Phase::Failed) {
                code = static_cast<int>(ErrorCode::UpdateFailed);
            } else if (end != Phase::UpToDate) {
                log::Info(L"Update declined or cancelled by the user");
                code = static_cast<int>(ErrorCode::Cancelled);
            }
        }
        // A cancelled download stops within a moment; a check can wait for a network timeout. Do not wait for that:
        // end the process here, before the objects the worker might still touch go away.
        for (int i = 0; i < 40 && shared_->running > 0; ++i) ::Sleep(50);
        if (shared_->running > 0) {
            log::Info(L"Exit code %d (a network request is still pending)", code);
            log::Close();
            ::ExitProcess(static_cast<UINT>(code));
        }
        return code;
    }

private:
    static constexpr int kUpdateButton = 1001;

    TASKDIALOGCONFIG Page(Phase phase) {
        Release r;
        std::wstring error;
        {
            std::lock_guard g(shared_->lock);
            r = shared_->release;
            error = shared_->error;
        }
        shown_ = phase;
        // Every page keeps its own strings until the window is gone: the dialog may still refer to them.
        PageText& t = texts_.emplace_back();
        t.title = strings_.Fmt("update.title", {pkg_.name});
        TASKDIALOGCONFIG c{};
        c.cbSize = sizeof(c);
        c.hInstance = ::GetModuleHandleW(nullptr);
        c.dwFlags = TDF_CALLBACK_TIMER | TDF_SIZE_TO_CONTENT | TDF_POSITION_RELATIVE_TO_WINDOW;
        c.pszWindowTitle = t.title.c_str();
        c.pfCallback = &UpdateWindow::Callback;
        c.lpCallbackData = reinterpret_cast<LONG_PTR>(this);
        switch (phase) {
            case Phase::Checking:
                t.main = strings_.Get("update.checking");
                c.dwFlags |= TDF_SHOW_MARQUEE_PROGRESS_BAR;
                c.dwCommonButtons = TDCBF_CANCEL_BUTTON;
                break;
            case Phase::UpToDate:
                t.main = strings_.Fmt("update.upToDate", {pkg_.name});
                t.content = strings_.Fmt("update.upToDate.body", {installed_});
                c.pszMainIcon = TD_INFORMATION_ICON;
                c.dwCommonButtons = TDCBF_CLOSE_BUTTON;
                break;
            case Phase::Available:
                t.main = strings_.Fmt("update.available", {pkg_.name, r.version});
                t.content = strings_.Fmt("update.available.body", {installed_, pkg_.name});
                if (!r.notesUrl.empty()) {
                    t.content += L"\n\n<a href=\"" + r.notesUrl + L"\">" + strings_.Get("update.notes") + L"</a>";
                    c.dwFlags |= TDF_ENABLE_HYPERLINKS;
                }
                t.updateText = strings_.Get("button.update");
                t.laterText = strings_.Get("update.later");
                buttons_[0] = {kUpdateButton, t.updateText.c_str()};
                buttons_[1] = {IDCANCEL, t.laterText.c_str()};
                c.pButtons = buttons_;
                c.cButtons = 2;
                c.nDefaultButton = kUpdateButton;
                c.dwFlags |= TDF_ALLOW_DIALOG_CANCELLATION;
                break;
            case Phase::Downloading:
            case Phase::Verifying:
            case Phase::Ready:
                t.main = phase == Phase::Downloading ? strings_.Fmt("update.downloading", {pkg_.name, r.version}) : strings_.Get("update.verifying");
                c.dwFlags |= TDF_SHOW_PROGRESS_BAR;
                c.dwCommonButtons = TDCBF_CANCEL_BUTTON;
                break;
            case Phase::CheckFailed:
            case Phase::Failed:
                t.main = strings_.Get(phase == Phase::CheckFailed ? "update.checkFailed" : "update.failed");
                t.content = strings_.Get("error.UpdateFailed");
                t.expanded = error;
                c.pszMainIcon = TD_ERROR_ICON;
                c.dwCommonButtons = TDCBF_CLOSE_BUTTON;
                break;
        }
        c.pszMainInstruction = t.main.c_str();
        if (!t.content.empty()) c.pszContent = t.content.c_str();
        if (!t.expanded.empty()) c.pszExpandedInformation = t.expanded.c_str();
        return c;
    }

    void Navigate(HWND hwnd, Phase phase) {
        page_ = Page(phase);
        ::SendMessageW(hwnd, TDM_NAVIGATE_PAGE, 0, reinterpret_cast<LPARAM>(&page_));
    }

    void StartDownload(HWND hwnd) {
        shared_->Set(Phase::Downloading);
        Navigate(hwnd, Phase::Downloading);
        auto shared = shared_;
        const Source source = source_;
        const std::wstring appId = pkg_.appId, architecture = pkg_.architecture;
        ++shared->running;
        std::thread([shared, source, appId, architecture] {
            struct Done {
                Shared& s;
                ~Done() { --s.running; }
            } done{*shared};
            Release r;
            {
                std::lock_guard g(shared->lock);
                r = shared->release;
            }
            shared->total = r.size;
            std::wstring error;
            Downloaded d;
            const auto progress = [&](uint64_t done, uint64_t) { shared->done = done; };
            if (!DownloadRelease(r, source, appId, shared->cancel, progress, d, error)) {
                if (!shared->cancel) log::Error(L"Download failed: %s", error.c_str());
                shared->Set(Phase::Failed, error);
                return;
            }
            shared->Set(Phase::Verifying);
            if (!VerifyPackage(d.path, appId, architecture, r, source, error)) {
                log::Error(L"Update refused: %s", error.c_str());
                Discard(d);
                shared->Set(Phase::Failed, error);
                return;
            }
            {
                std::lock_guard g(shared->lock);
                shared->download = std::move(d);
            }
            shared->Set(Phase::Ready);
        }).detach();
    }

    HRESULT OnTimer(HWND hwnd) {
        const Phase now = shared_->Get();
        if (now != shown_) {
            if (now == Phase::Ready) {
                ready_ = true;
                ::SendMessageW(hwnd, TDM_CLICK_BUTTON, IDCANCEL, 0);
                return S_OK;
            }
            if (passive_ && now == Phase::UpToDate) {
                ::SendMessageW(hwnd, TDM_CLICK_BUTTON, IDCANCEL, 0);
                return S_OK;
            }
            if (passive_ && now == Phase::Available) {
                StartDownload(hwnd);
                return S_OK;
            }
            if (!(now == Phase::Verifying && shown_ == Phase::Downloading)) Navigate(hwnd, now);
        }
        if (now == Phase::Downloading || now == Phase::Verifying) {
            const uint64_t total = shared_->total, done = shared_->done;
            const int pos = now == Phase::Verifying ? 100 : total ? static_cast<int>(done * 100 / total) : 0;
            ::SendMessageW(hwnd, TDM_SET_PROGRESS_BAR_POS, pos, 0);
            if (now == Phase::Verifying && !verifyingShown_) {
                verifyingShown_ = true;
                verifyingText_ = strings_.Get("update.verifying");
                ::SendMessageW(hwnd, TDM_SET_ELEMENT_TEXT, TDE_MAIN_INSTRUCTION, reinterpret_cast<LPARAM>(verifyingText_.c_str()));
            }
        }
        return S_OK;
    }

    static HRESULT CALLBACK Callback(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam, LONG_PTR data) {
        auto* self = reinterpret_cast<UpdateWindow*>(data);
        switch (msg) {
            case TDN_CREATED:
            case TDN_NAVIGATED:
                if (self->icon_) {
                    ::SendMessageW(hwnd, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(self->icon_));
                    ::SendMessageW(hwnd, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(self->icon_));
                }
                if (self->shown_ == Phase::Checking) ::SendMessageW(hwnd, TDM_SET_PROGRESS_BAR_MARQUEE, TRUE, 0);
                return S_OK;
            case TDN_TIMER:
                return self->OnTimer(hwnd);
            case TDN_BUTTON_CLICKED:
                if (static_cast<int>(wParam) == kUpdateButton) {
                    self->StartDownload(hwnd);
                    return S_FALSE;  // keep the window open
                }
                if (!self->ready_) self->shared_->cancel = true;
                return S_OK;
            case TDN_HYPERLINK_CLICKED: {
                const auto* url = reinterpret_cast<const wchar_t*>(lParam);
                if (url && IsSafeHttpsUrl(WideToUtf8(url))) ::ShellExecuteW(hwnd, L"open", url, nullptr, nullptr, SW_SHOWNORMAL);
                return S_OK;
            }
            default:
                return S_OK;
        }
    }

    const install::Package& pkg_;
    const Source source_;
    const Strings& strings_;
    const std::wstring installed_;
    const bool passive_;
    HICON icon_ = nullptr;
    std::shared_ptr<Shared> shared_;
    Phase shown_ = Phase::Checking;
    bool ready_ = false, verifyingShown_ = false;
    TASKDIALOGCONFIG page_{};
    TASKDIALOG_BUTTON buttons_[2]{};
    struct PageText {
        std::wstring title, main, content, expanded, updateText, laterText;
    };
    std::list<PageText> texts_;  // stable addresses
    std::wstring verifyingText_;
};

}  // namespace

RepairPackage::~RepairPackage() {
    lock.reset();
    if (downloaded && !path.empty()) {
        ::DeleteFileW(path.c_str());
        ::RemoveDirectoryW(path::Parent(path).c_str());
    }
}

RepairLookup FindRepairPackage(const install::Package& pkg, const std::wstring& version, const install::InstallRecord* record,
                               const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress,
                               RepairPackage& out, std::wstring& error) {
    std::wstring why;
    if (record && RecordedPackage(pkg, version, *record, out, why)) {
        log::Info(L"Repair source: the original setup %s", out.path.c_str());
        return RepairLookup::Found;
    }
    if (record) log::Info(L"Repair: not using the original setup: %s", why.c_str());
    std::wstring noSource;
    const std::optional<Source> source = ReadSource(pkg, noSource);
    if (!source) {
        error = why.empty() ? noSource : why;
        return RepairLookup::None;
    }
    Release r;
    const Lookup found = FindExactRelease(*source, pkg.appId, version, r, error);
    if (found == Lookup::None) return RepairLookup::None;
    Downloaded d;
    if (found == Lookup::Failed || !DownloadRelease(r, *source, pkg.appId, cancel, progress, d, error)) return RepairLookup::Failed;
    if (!VerifyPackage(d.path, pkg.appId, pkg.architecture, r, *source, error)) {
        Discard(d);
        return RepairLookup::Failed;
    }
    out.path = d.path;
    out.lock = std::move(d.lock);
    out.downloaded = !d.kept;  // a kept download stays: the setup of the installed version, for the next repair or update
    if (security::IsElevated()) {
        // Downloads and the kept update folder belong to the user: with administrator rights, run a private copy.
        const std::wstring original = out.path;
        const bool temporary = out.downloaded;
        if (!CopyToRunDirectory(out, r.sha256, error)) return RepairLookup::Failed;  // out deletes a temporary download
        if (temporary) {
            ::DeleteFileW(path::Long(original).c_str());
            ::RemoveDirectoryW(path::Long(path::Parent(original)).c_str());
        }
    }
    return RepairLookup::Found;
}

bool RunSetup(const std::wstring& file, bool quiet, const std::wstring& language, const std::wstring& recordAs, DWORD& exitCode,
              std::wstring& error) {
    std::wstring cmd = QuoteArg(file) + (quiet ? L" /quiet" : L" /passive");
    if (!language.empty()) cmd += L" /lang " + QuoteArg(language);
    if (!recordAs.empty()) cmd += L" --source-package " + QuoteArg(recordAs);
    log::Info(L"Starting %s", cmd.c_str());
    ::AllowSetForegroundWindow(ASFW_ANY);
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    const std::wstring dir = path::Parent(file);
    if (!::CreateProcessW(file.c_str(), cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, dir.c_str(), &si, &pi)) {
        error = L"Cannot start the setup: " + Win32Message(::GetLastError());
        return false;
    }
    UniqueHandle process(pi.hProcess), thread(pi.hThread);
    ::WaitForSingleObject(process.get(), INFINITE);
    exitCode = static_cast<DWORD>(ErrorCode::Internal);
    ::GetExitCodeProcess(process.get(), &exitCode);
    return true;
}

// The update channel recorded at install time (Setup.exe /channel), if the install record can be read.
std::wstring RecordedChannel(const install::InstalledProduct& product) {
    try {
        return install::LoadVerifiedRecord(product).updateChannel;
    } catch (const Failure& f) {
        log::Warn(L"Install record unusable (%s); the update channel is the package's", f.detail().c_str());
        return {};
    }
}

std::function<UpdateCheckResult()> MakeUpdateCheck(const install::Package& pkg, const std::wstring& installed) {
    std::wstring why;
    std::optional<Source> source = ReadSource(pkg, why);
    if (!source) return {};
    if (const auto product = install::FindInstalled(pkg.appId)) source->channel = EffectiveChannel({}, RecordedChannel(*product), source->channel);
    return [s = *source, appId = pkg.appId, installed] {
        UpdateCheckResult r;
        Release release;
        const Lookup found = FindUpdate(s, appId, installed, release, r.error);
        r.ok = found != Lookup::Failed;
        r.available = found == Lookup::Found;
        if (r.available) {
            r.version = release.version;
            r.notesUrl = release.notesUrl;
        }
        if (!r.ok) log::Warn(L"Update check failed: %s", r.error.c_str());
        return r;
    };
}

int RunUpdate(const install::Package& pkg, const Args& args, const Strings& strings) {
    std::wstring why;
    std::optional<Source> source = ReadSource(pkg, why);
    if (!source) {
        log::Error(L"%s", why.c_str());
        return static_cast<int>(ErrorCode::InvalidArguments);
    }
    const auto product = install::FindInstalled(pkg.appId);
    if (!product) {
        log::Error(L"%s is not installed", pkg.appId.c_str());
        return static_cast<int>(ErrorCode::NotInstalled);
    }
    // /channel, else the one recorded at install time, else update.channel.
    source->channel = EffectiveChannel(args.channel, args.channel.empty() ? RecordedChannel(*product) : std::wstring(), source->channel);
    if (args.checkUpdate) return CheckOnly(pkg, *source, product->version);

    // One update at a time per app (a second click while the window is open does nothing).
    UniqueHandle mutex(::CreateMutexW(nullptr, FALSE, (L"Local\\FluentSetup-update-" + pkg.appId).c_str()));
    if (mutex && ::WaitForSingleObject(mutex.get(), 0) == WAIT_TIMEOUT) {
        log::Info(L"Another update of %s is running", pkg.appId.c_str());
        return static_cast<int>(ErrorCode::AlreadyRunning);
    }
    if (args.quiet) return InstallQuietly(pkg, *source, product->version, args);

    HICON icon = nullptr;
    if (!pkg.mainExecutable.empty()) ::ExtractIconExW(path::Join(product->installDir, pkg.mainExecutable).c_str(), 0, &icon, nullptr, 1);
    UpdateWindow window(pkg, *source, strings, product->version, args.passive, icon);
    const int code = window.Run(args);
    if (icon) ::DestroyIcon(icon);
    return code;
}

}  // namespace fsetup::boot
