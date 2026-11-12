#include "Install.Core/package.h"

#include "Common/error.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Common/version.h"

#include <algorithm>
#include <unordered_set>

namespace fsetup::install {

namespace {

constexpr size_t kMaxFiles = 2'000'000;
constexpr size_t kMaxStreams = 1'000'000;
constexpr uint64_t kMaxTotalRaw = 1ull << 44;  // 16 TiB: a sanity bound, the real bound is disk space

[[noreturn]] void Bad(const std::wstring& why) { throw Failure(ErrorCode::PackageCorrupt, L"Invalid package header: " + why); }

std::wstring RequireString(const json::Value& v, const char* key, size_t maxLen, bool required = true) {
    const json::Value& f = v[key];
    if (f.isNull() && !required) return {};
    if (!f.isString()) Bad(Utf8ToWide(key) + L" must be a string");
    std::wstring s = f.asWide();
    if (required && s.empty()) Bad(Utf8ToWide(key) + L" is empty");
    if (s.size() > maxLen) Bad(Utf8ToWide(key) + L" is too long");
    for (const wchar_t c : s) {
        if (c < 0x20 && c != L'\n' && c != L'\r' && c != L'\t') Bad(Utf8ToWide(key) + L" contains control characters");
    }
    return s;
}

Offer ParseOffer(const json::Value& v, Offer fallback) {
    if (v.isNull()) return fallback;
    const std::string& s = v.asString();
    if (s == "off") return Offer::Off;
    if (s == "on") return Offer::On;
    if (s == "optionalOff") return Offer::OptionalOff;
    if (s == "optionalOn") return Offer::OptionalOn;
    Bad(L"unknown offer value " + Utf8ToWide(s));
}

bool ValidAppId(const std::wstring& id) {
    if (id.empty() || id.size() > 64) return false;
    for (const wchar_t c : id) {
        const bool ok = (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9') || c == L'.' ||
                        c == L'-' || c == L'_';
        if (!ok) return false;
    }
    return id.front() != L'.' && id.back() != L'.';
}

bool EndsWithExe(const std::wstring& p) { return p.size() > 4 && EqualsIgnoreCase(std::wstring_view(p).substr(p.size() - 4), L".exe"); }

bool ParseDigest(const json::Value& v, Sha256Digest& out) {
    std::vector<uint8_t> bytes;
    if (!v.isString() || !FromHex(v.asString(), bytes) || bytes.size() != out.size()) return false;
    std::copy(bytes.begin(), bytes.end(), out.begin());
    return true;
}

bool IsReservedTopLevel(const std::wstring& rel) {
    const std::wstring first = rel.substr(0, rel.find(L'\\'));
    return EqualsIgnoreCase(rel, kUninstallerName) || EqualsIgnoreCase(rel, kRecordName) ||
           EqualsIgnoreCase(first, kWorkDirName);
}

FileSet ParseFileSet(const json::Value& v, const wchar_t* what, const std::function<bool(uint64_t, uint64_t)>& rangeCheck,
                     bool checkReserved) {
    FileSet set;
    if (v.isNull()) return set;
    if (!v.isObject()) Bad(std::wstring(what) + L" must be an object");

    const auto& streams = v["streams"].array();
    const auto& files = v["files"].array();
    if (streams.size() > kMaxStreams) Bad(L"too many streams");
    if (files.size() > kMaxFiles) Bad(L"too many files");

    // Streams: in bounds, ascending, non-overlapping.
    uint64_t previousEnd = 0;
    for (const auto& s : streams) {
        StreamInfo info;
        if (!s["offset"].isNumber() || !s["size"].isNumber() || !s["rawSize"].isNumber()) Bad(L"stream fields missing");
        const int64_t off = s["offset"].asInt(-1), sz = s["size"].asInt(-1), raw = s["rawSize"].asInt(-1);
        if (off < 0 || sz <= 0 || raw < 0) Bad(L"negative stream fields");
        info.offset = static_cast<uint64_t>(off);
        info.size = static_cast<uint64_t>(sz);
        info.rawSize = static_cast<uint64_t>(raw);
        if (!ParseDigest(s["sha256"], info.sha256)) Bad(L"bad stream sha256");
        if (info.offset < previousEnd) Bad(L"streams overlap or are out of order");
        if (!rangeCheck(info.offset, info.size)) Bad(L"stream outside the package");
        previousEnd = info.offset + info.size;
        set.streams.push_back(info);
    }

    // Files: valid unique paths, contiguous inside their stream, sizes add up.
    std::vector<uint64_t> streamFill(set.streams.size(), 0);
    std::unordered_set<std::wstring> seen;
    seen.reserve(files.size() * 2);
    int64_t lastStream = -1;
    for (const auto& f : files) {
        FileInfo info;
        std::wstring why;
        if (!path::ValidateRelative(f["path"].asWide(), info.path, &why)) {
            Bad(L"unsafe file path '" + f["path"].asWide() + L"': " + why);
        }
        if (checkReserved && IsReservedTopLevel(info.path)) Bad(L"file path uses a reserved name: " + info.path);
        info.variant = f["variant"].asString();
        if (info.variant.size() > 32) Bad(L"invalid variant id for " + info.path);
        // The same path may appear once per variant (different builds of one file), but not also as a shared file.
        if (!seen.insert(Utf8ToWide(info.variant) + L"|" + ToLower(info.path)).second) Bad(L"duplicate file path: " + info.path);
        const int64_t size = f["size"].asInt(-1), stream = f["stream"].asInt(-1), offset = f["offset"].asInt(-1);
        if (size < 0 || stream < 0 || offset < 0) Bad(L"bad numeric fields for " + info.path);
        if (static_cast<size_t>(stream) >= set.streams.size()) Bad(L"file refers to a missing stream: " + info.path);
        if (stream < lastStream) Bad(L"files are not ordered by stream");
        lastStream = stream;
        info.size = static_cast<uint64_t>(size);
        info.stream = static_cast<uint32_t>(stream);
        info.offset = static_cast<uint64_t>(offset);
        if (info.offset != streamFill[info.stream]) Bad(L"file data is not contiguous: " + info.path);
        streamFill[info.stream] += info.size;
        if (streamFill[info.stream] > set.streams[info.stream].rawSize) Bad(L"stream overflow at " + info.path);
        if (!ParseDigest(f["sha256"], info.sha256)) Bad(L"bad sha256 for " + info.path);
        info.attributes = static_cast<uint32_t>(f["attributes"].asUint(0)) & (FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_HIDDEN);
        info.preserve = f["preserve"].asBool(false);
        set.totalRaw += info.size;
        if (set.totalRaw > kMaxTotalRaw) Bad(L"payload too large");
        set.files.push_back(std::move(info));
    }
    for (size_t i = 0; i < set.streams.size(); ++i) {
        if (streamFill[i] != set.streams[i].rawSize) Bad(L"stream size does not match its files");
    }

    // A file may not also be a directory of another file ("a" and "a\b"), whatever variants the two belong to.
    std::unordered_set<std::wstring> paths;
    for (const auto& file : set.files) paths.insert(ToLower(file.path));
    for (const auto& file : set.files) {
        std::wstring lower = ToLower(file.path);
        for (size_t pos = lower.find(L'\\'); pos != std::wstring::npos; pos = lower.find(L'\\', pos + 1)) {
            if (paths.count(lower.substr(0, pos))) Bad(L"path is both a file and a directory: " + file.path.substr(0, pos));
        }
    }

    for (const auto& d : v["directories"].array()) {
        std::wstring rel, why;
        if (!path::ValidateRelative(d.asWide(), rel, &why)) Bad(L"unsafe directory '" + d.asWide() + L"': " + why);
        if (checkReserved && IsReservedTopLevel(rel)) Bad(L"directory uses a reserved name: " + rel);
        if (seen.count(ToLower(rel))) Bad(L"directory collides with a file: " + rel);
        set.directories.push_back(rel);
    }
    return set;
}

}  // namespace

Package ParsePackage(const std::string& headerJson, const std::function<bool(uint64_t, uint64_t)>& rangeCheck) {
    Package p;
    std::string error;
    json::ParseLimits limits;
    limits.maxDepth = 16;
    if (!json::Parse(headerJson, p.raw, &error, limits) || !p.raw.isObject()) Bad(L"malformed JSON: " + Utf8ToWide(error));
    const json::Value& root = p.raw;
    if (root["format"].asInt() != 1) Bad(L"unsupported header format");
    p.kind = root["kind"].asWide();
    if (p.kind != L"setup" && p.kind != L"uninstaller") Bad(L"unknown package kind");

    const json::Value& app = root["app"];
    p.appId = RequireString(app, "id", 64);
    if (!ValidAppId(p.appId)) Bad(L"invalid app id (allowed: letters, digits, '.', '-', '_')");
    p.name = RequireString(app, "name", 128);
    p.publisher = RequireString(app, "publisher", 128, false);
    p.version = RequireString(app, "version", 64);
    Version v;
    if (!Version::Parse(p.version, v)) Bad(L"invalid version");
    p.description = RequireString(app, "description", 1024, false);
    p.website = RequireString(app, "website", 512, false);
    if (!p.website.empty() && !StartsWithIgnoreCase(p.website, L"https://") && !StartsWithIgnoreCase(p.website, L"http://")) {
        Bad(L"website must be an http(s) URL");
    }
    p.architecture = RequireString(app, "architecture", 16, false);
    p.minWindowsBuild = static_cast<uint32_t>(app["minWindowsBuild"].asUint(0));
    const std::wstring mainExe = RequireString(app, "mainExecutable", path::kMaxRelativePath, false);
    if (!mainExe.empty()) {
        std::wstring why;
        if (!path::ValidateRelative(mainExe, p.mainExecutable, &why)) Bad(L"unsafe main executable path: " + why);
        if (!EndsWithExe(p.mainExecutable)) Bad(L"main executable must be an .exe");
    }

    const json::Value& inst = root["install"];
    const std::string& scope = inst["scope"].asString();
    if (scope == "user" || scope.empty()) p.scopeMode = ScopeMode::UserOnly;
    else if (scope == "machine") p.scopeMode = ScopeMode::MachineOnly;
    else if (scope == "userOrMachine") p.scopeMode = ScopeMode::UserOrMachine;
    else Bad(L"unknown scope");
    p.defaultScope = p.scopeMode == ScopeMode::MachineOnly ? Scope::Machine
                     : p.scopeMode == ScopeMode::UserOnly ? Scope::User
                     : (inst["defaultScope"].asString() == "machine" ? Scope::Machine : Scope::User);
    p.userDirTemplate = inst["userDir"].isString() ? inst["userDir"].asWide() : L"{LocalAppData}\\Programs\\{AppName}";
    p.machineDirTemplate = inst["machineDir"].isString() ? inst["machineDir"].asWide() : L"{ProgramFiles}\\{AppName}";
    p.allowCustomDir = inst["allowCustomDir"].asBool(true);
    p.desktopShortcut = ParseOffer(inst["desktopShortcut"], Offer::OptionalOff);
    p.startMenuShortcut = ParseOffer(inst["startMenuShortcut"], Offer::On);
    p.launchAfterInstall = ParseOffer(inst["launchAfterInstall"], Offer::OptionalOn);
    p.allowDowngrade = inst["allowDowngrade"].asBool(false);
    if (p.mainExecutable.empty()) {
        p.desktopShortcut = Offer::Off;
        p.startMenuShortcut = Offer::Off;
        p.launchAfterInstall = Offer::Off;
    }

    p.ui = root["ui"];
    if (!p.ui.isNull() && !p.ui.isObject()) Bad(L"ui must be an object");

    p.payload = ParseFileSet(root["payload"], L"payload", rangeCheck, true);
    {
        // Variants: declared ids, a default, and every variant-specific file belongs to a declared variant and does not
        // shadow a shared file.
        const auto& list = root["variants"].array();
        if (list.size() > 8) Bad(L"too many variants");
        for (const auto& entry : list) {
            Variant variant;
            variant.id = entry["id"].asString();
            if (variant.id.empty() || variant.id.size() > 32 ||
                !std::all_of(variant.id.begin(), variant.id.end(), [](char c) { return isalnum(static_cast<unsigned char>(c)) || c == '_' || c == '-'; })) {
                Bad(L"invalid variant id");
            }
            if (p.FindVariant(variant.id)) Bad(L"duplicate variant id");
            variant.label = ParseLocalizedText(entry["label"]);
            variant.description = ParseLocalizedText(entry["description"]);
            variant.isDefault = entry["default"].asBool(false);
            p.variants.push_back(std::move(variant));
        }
        std::unordered_set<std::wstring> shared;
        for (const auto& f : p.payload.files) {
            if (f.variant.empty()) shared.insert(ToLower(f.path));
        }
        for (auto& each : p.variants) each.bytes = 0;
        for (const auto& f : p.payload.files) {
            if (f.variant.empty()) {
                for (auto& each : p.variants) each.bytes += f.size;
                continue;
            }
            Variant* owner = const_cast<Variant*>(p.FindVariant(f.variant));
            if (!owner) Bad(L"file belongs to an undeclared variant: " + f.path);
            if (shared.count(ToLower(f.path))) Bad(L"variant file shadows a shared file: " + f.path);
            owner->bytes += f.size;
        }
    }
    if (!p.IsUninstaller() && !p.mainExecutable.empty()) {
        const bool found = std::any_of(p.payload.files.begin(), p.payload.files.end(),
                                       [&](const FileInfo& f) { return EqualsIgnoreCase(f.path, p.mainExecutable); });
        if (!found) Bad(L"main executable is not part of the payload");
    }
    if (p.IsUninstaller() && !p.payload.files.empty()) Bad(L"uninstaller must not carry a payload");

    {
        std::unordered_set<std::wstring> payloadSet;
        for (const auto& f : p.payload.files) payloadSet.insert(ToLower(f.path));
        // The uninstaller carries no payload: its action targets refer to the installed files, which RunActions checks
        // at run time (an existing file strictly inside the install directory, no links on the way).
        const bool uninstaller = p.IsUninstaller();
        p.ext = ParseExtensions(root, [&](const std::wstring& rel) { return uninstaller || payloadSet.count(ToLower(rel)) != 0; });
        if (!p.ext.adoptUninstallKey.empty() && !EqualsIgnoreCase(p.ext.adoptUninstallKey, p.appId)) {
            Bad(L"adoptUninstallKey must equal the app id");
        }
    }

    const json::Value& fe = root["frontend"];
    if (fe.isObject()) {
        p.frontend = ParseFileSet(fe, L"frontend", rangeCheck, false);
        std::wstring why;
        if (!fe["entry"].asWide().empty() && !path::ValidateRelative(fe["entry"].asWide(), p.frontendEntry, &why)) {
            Bad(L"unsafe frontend entry");
        }
        if (fe["icon"].isString() && !path::ValidateRelative(fe["icon"].asWide(), p.iconFile, &why)) Bad(L"unsafe icon path");
        // The UI streams must not overlap the payload streams.
        for (const auto& a : p.frontend.streams) {
            for (const auto& b : p.payload.streams) {
                if (a.offset < b.offset + b.size && b.offset < a.offset + a.size) Bad(L"frontend overlaps payload");
            }
        }
    }
    return p;
}

std::wstring ExpandDirTemplate(const std::wstring& tpl, const Package& pkg) {
    struct Token {
        const wchar_t* name;
        std::wstring value;
    };
    const Token tokens[] = {
        {L"{LocalAppData}", path::KnownFolder(FOLDERID_LocalAppData)},
        {L"{UserProgramFiles}", path::KnownFolder(FOLDERID_UserProgramFiles)},
        {L"{AppData}", path::KnownFolder(FOLDERID_RoamingAppData)},
        {L"{ProgramFiles}", path::KnownFolder(FOLDERID_ProgramFiles)},
        {L"{ProgramData}", path::KnownFolder(FOLDERID_ProgramData)},
        {L"{AppName}", path::SanitizeName(pkg.name)},
        {L"{Publisher}", path::SanitizeName(pkg.publisher.empty() ? pkg.name : pkg.publisher)},
        {L"{AppId}", pkg.appId},
    };
    std::wstring out;
    for (size_t i = 0; i < tpl.size();) {
        bool matched = false;
        if (tpl[i] == L'{') {
            for (const auto& t : tokens) {
                const size_t len = wcslen(t.name);
                if (tpl.compare(i, len, t.name) == 0) {
                    out += t.value;
                    i += len;
                    matched = true;
                    break;
                }
            }
        }
        if (!matched) out.push_back(tpl[i++]);
    }
    return out;
}

}  // namespace fsetup::install
