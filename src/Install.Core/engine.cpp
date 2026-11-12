#include "Install.Core/engine.h"

#include "Common/fs.h"
#include "Common/log.h"
#include "Common/security.h"
#include "Common/text.h"
#include "Common/version.h"
#include "Install.Core/journal.h"
#include "Install.Core/shortcuts.h"

#include <objbase.h>
#include <shlobj.h>

#include <algorithm>
#include <unordered_set>

namespace fsetup::install {

// ---------------------------------------------------------------------------------------------------------------
// Serialisation helpers

const char* StateName(State s) noexcept {
    switch (s) {
        case State::Idle: return "Idle";
        case State::Validating: return "Validating";
        case State::WaitingForElevation: return "WaitingForElevation";
        case State::Preparing: return "Preparing";
        case State::Extracting: return "Extracting";
        case State::Installing: return "Installing";
        case State::Registering: return "Registering";
        case State::Finalizing: return "Finalizing";
        case State::Removing: return "Removing";
        case State::Completed: return "Completed";
        case State::Cancelling: return "Cancelling";
        case State::RollingBack: return "RollingBack";
        case State::Failed: return "Failed";
        case State::Downloading: return "Downloading";
        case State::InstallingPrerequisites: return "InstallingPrerequisites";
    }
    return "Idle";
}

json::Value ProgressToJson(const Progress& p) {
    json::Value v;
    v.set("state", StateName(p.state));
    v.set("fraction", p.fraction);
    v.set("detail", p.detail);
    v.set("cancellable", p.cancellable);
    return v;
}

Progress ProgressFromJson(const json::Value& v) {
    Progress p;
    const std::string& s = v["state"].asString();
    for (int i = 0; i <= static_cast<int>(State::Failed); ++i) {
        if (s == StateName(static_cast<State>(i))) p.state = static_cast<State>(i);
    }
    p.fraction = std::clamp(v["fraction"].asDouble(-1), -1.0, 1.0);
    p.detail = v["detail"].asWide().substr(0, 1024);
    p.cancellable = v["cancellable"].asBool(true);
    return p;
}

json::Value OptionsToJson(const InstallOptions& o) {
    json::Value v;
    v.set("installDir", o.installDir);
    v.set("scope", ScopeName(o.scope));
    v.set("desktopShortcut", o.desktopShortcut);
    v.set("startMenuShortcut", o.startMenuShortcut);
    json::Value choices = json::Value::MakeObject();
    for (const auto& [id, on] : o.choices) choices.set(id, on);
    v.set("choices", choices);
    return v;
}

bool OptionsFromJson(const json::Value& v, InstallOptions& o) {
    if (!v.isObject() || !v["installDir"].isString()) return false;
    const std::string& scope = v["scope"].asString();
    if (scope != "user" && scope != "machine") return false;
    o.installDir = v["installDir"].asWide();
    if (o.installDir.size() > 1024) return false;
    o.scope = scope == "machine" ? Scope::Machine : Scope::User;
    o.desktopShortcut = v["desktopShortcut"].asBool(false);
    o.startMenuShortcut = v["startMenuShortcut"].asBool(true);
    if (v["choices"].object().size() > 16) return false;
    for (const auto& [id, on] : v["choices"].object()) {
        if (id.size() > 32 || !on.isBool()) return false;
        o.choices[id] = on.asBool();
    }
    return true;
}

json::Value OutcomeToJson(const Outcome& o) {
    json::Value v;
    v.set("code", static_cast<int64_t>(o.code));
    v.set("detail", o.detail);
    v.set("rolledBack", o.rolledBack);
    v.set("upgrade", o.upgrade);
    v.set("leftovers", o.leftovers);
    v.set("installDir", o.installDir);
    v.set("mainExecutable", o.mainExecutable);
    json::Value d = json::Value::MakeArray();
    for (const auto& p : o.deferredDeletes) d.push(p);
    v.set("deferred", d);
    json::Value w = json::Value::MakeArray();
    for (const auto& s : o.warnings) w.push(s);
    v.set("warnings", w);
    return v;
}

Outcome OutcomeFromJson(const json::Value& v) {
    Outcome o;
    const int64_t code = v["code"].asInt(static_cast<int64_t>(ErrorCode::Internal));
    o.code = (code >= 0 && code <= static_cast<int64_t>(kLastErrorCode)) ? static_cast<ErrorCode>(code) : ErrorCode::Internal;
    o.detail = v["detail"].asWide();
    o.rolledBack = v["rolledBack"].asBool();
    o.upgrade = v["upgrade"].asBool();
    o.leftovers = v["leftovers"].asBool();
    o.installDir = v["installDir"].asWide();
    o.mainExecutable = v["mainExecutable"].asWide();
    for (const auto& d : v["deferred"].array()) o.deferredDeletes.push_back(d.asWide());
    for (const auto& w : v["warnings"].array()) o.warnings.push_back(w.asWide().substr(0, 512));
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Progress reporting with throttling (never more often than every 40 ms unless the state changes).

class Reporter {
public:
    explicit Reporter(IProgressSink& sink) : sink_(sink) {}

    void Set(State s, double fraction, std::wstring detail = {}, bool cancellable = true) {
        current_.state = s;
        current_.fraction = fraction;
        current_.detail = std::move(detail);
        current_.cancellable = cancellable;
        Emit(true);
        if (s != State::Completed && s != State::Failed) log::Info(L"State -> %S", StateName(s));
    }
    void Update(double fraction, const std::wstring& detail) {
        current_.fraction = fraction;
        current_.detail = detail;
        Emit(false);
    }
    [[nodiscard]] State state() const noexcept { return current_.state; }

private:
    void Emit(bool force) {
        const ULONGLONG now = ::GetTickCount64();
        if (!force && now - last_ < 40) return;
        last_ = now;
        sink_.OnProgress(current_);
    }
    IProgressSink& sink_;
    Progress current_;
    ULONGLONG last_ = 0;
};

namespace {

struct ComScope {
    HRESULT hr = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    ~ComScope() {
        if (SUCCEEDED(hr)) ::CoUninitialize();
    }
};

void ThrowIfCancelled(const std::atomic<bool>& cancel) {
    if (cancel.load()) throw Failure(ErrorCode::Cancelled, L"Cancelled by the user");
}

std::wstring Describe(const Failure& f) {
    std::wstring s = f.detail();
    if (f.systemError() != 0) s += Printf(L" (system error %lu: %s)", f.systemError(), Win32Message(f.systemError()).c_str());
    return s;
}

void ThrowMove(DWORD e, const std::wstring& what) {
    throw Failure(ClassifyWin32(e), what, e);
}

std::wstring ShortcutName(const Package& pkg) { return path::SanitizeName(pkg.name) + L".lnk"; }

// Every directory of a set of relative files (and extra directories), deepest first.
std::vector<std::wstring> ImpliedDirectories(const std::vector<std::wstring>& files, const std::vector<std::wstring>& extra) {
    std::unordered_set<std::wstring> seen;
    std::vector<std::wstring> out;
    const auto add = [&](const std::wstring& d) {
        if (seen.insert(ToLower(d)).second) out.push_back(d);
    };
    for (const auto& f : files) {
        for (size_t pos = f.find(L'\\'); pos != std::wstring::npos; pos = f.find(L'\\', pos + 1)) add(f.substr(0, pos));
    }
    for (const auto& d : extra) {
        add(d);
        for (size_t pos = d.find(L'\\'); pos != std::wstring::npos; pos = d.find(L'\\', pos + 1)) add(d.substr(0, pos));
    }
    std::sort(out.begin(), out.end(), [](const std::wstring& a, const std::wstring& b) {
        const auto depth = [](const std::wstring& s) { return std::count(s.begin(), s.end(), L'\\'); };
        return depth(a) > depth(b);
    });
    return out;
}

void RemoveEmptyDirectories(const std::wstring& root, const std::vector<std::wstring>& relDirsDeepestFirst) {
    for (const auto& rel : relDirsDeepestFirst) {
        const std::wstring d = path::Join(root, rel);
        if (fs::IsDirectory(d) && !fs::IsLinkLike(d) && fs::IsDirectoryEmpty(d)) ::RemoveDirectoryW(path::Long(d).c_str());
    }
}

bool IsProtectedLocation(const std::wstring& dir) {
    static const KNOWNFOLDERID* const kExact[] = {
        &FOLDERID_ProgramFiles,   &FOLDERID_ProgramFilesX86,   &FOLDERID_ProgramFilesCommon, &FOLDERID_ProgramData,
        &FOLDERID_UserProfiles,   &FOLDERID_Profile,           &FOLDERID_Desktop,            &FOLDERID_PublicDesktop,
        &FOLDERID_Documents,      &FOLDERID_Downloads,         &FOLDERID_Pictures,           &FOLDERID_Music,
        &FOLDERID_Videos,         &FOLDERID_LocalAppData,      &FOLDERID_RoamingAppData,     &FOLDERID_UserProgramFiles,
        &FOLDERID_Programs,       &FOLDERID_CommonPrograms,    &FOLDERID_Startup,            &FOLDERID_Public,
        &FOLDERID_LocalAppDataLow, &FOLDERID_StartMenu,        &FOLDERID_CommonStartMenu,
    };
    for (const auto* id : kExact) {
        const std::wstring f = path::KnownFolder(*id);
        if (!f.empty() && EqualsIgnoreCase(f, dir)) return true;
    }
    const std::wstring windows = path::KnownFolder(FOLDERID_Windows);
    if (!windows.empty() && path::IsSameOrUnder(dir, windows)) return true;
    // An ancestor of any of the above (e.g. "C:\Users") is just as bad.
    for (const auto* id : kExact) {
        const std::wstring f = path::KnownFolder(*id);
        if (!f.empty() && path::IsStrictlyUnder(f, dir)) return true;
    }
    return false;
}

bool ProbeWritable(const std::wstring& dir) {
    std::wstring probe = dir;
    while (!probe.empty() && !fs::IsDirectory(probe)) {
        const std::wstring parent = path::Parent(probe);
        if (parent == probe || parent.empty()) return false;
        probe = parent;
    }
    const std::wstring test = path::Join(probe, L"fsetup-probe-" + RandomToken().substr(0, 12));
    if (!::CreateDirectoryW(path::Long(test).c_str(), nullptr)) return ::GetLastError() != ERROR_ACCESS_DENIED;
    ::RemoveDirectoryW(path::Long(test).c_str());
    return true;
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------

Engine::Engine(const Package& pkg, const archive::Container& container) : pkg_(pkg), container_(container) {}

bool Engine::ScopeAllowed(Scope s) const noexcept {
    switch (pkg_.scopeMode) {
        case ScopeMode::UserOnly: return s == Scope::User;
        case ScopeMode::MachineOnly: return s == Scope::Machine;
        case ScopeMode::UserOrMachine: return true;
    }
    return false;
}

std::optional<InstalledProduct> Engine::FindExisting(bool* foreign) const {
    if (foreign) *foreign = false;
    if (auto p = FindInstalled(pkg_.appId)) return p;
    std::vector<std::wstring> keys;
    if (!pkg_.ext.adoptUninstallKey.empty()) keys.push_back(pkg_.appId);
    keys.insert(keys.end(), pkg_.ext.legacyUninstallKeys.begin(), pkg_.ext.legacyUninstallKeys.end());
    for (const auto& k : keys) {
        for (const Scope s : {Scope::Machine, Scope::User}) {
            if (!ScopeAllowed(s)) continue;
            if (auto p = QueryForeign(k, s); p && !p->installDir.empty()) {
                if (foreign) *foreign = true;
                return p;
            }
        }
    }
    return std::nullopt;
}

Existing Engine::DetectExisting() const {
    Existing e;
    e.product = FindExisting(&e.foreign);
    if (!e.product) return e;
    Version current, ours;
    if (!Version::Parse(e.product->version, current) || !Version::Parse(pkg_.version, ours)) {
        e.relation = Relation::Upgrade;  // unknown installed version: treat as an update
        return e;
    }
    const int c = Compare(ours, current);
    e.relation = c > 0 ? Relation::Upgrade : c == 0 ? Relation::Same : Relation::Downgrade;
    e.blocked = e.relation == Relation::Downgrade && !pkg_.allowDowngrade;
    return e;
}

bool Engine::SanitizeOptions(InstallOptions& o) const {
    if (!ScopeAllowed(o.scope)) return false;
    const auto existing = FindExisting();
    if (existing && existing->scope != o.scope) return false;
    if ((!pkg_.allowCustomDir || existing) && !EqualsIgnoreCase(o.installDir, DefaultInstallDir(o.scope))) return false;
    if (!OfferVisible(pkg_.desktopShortcut)) o.desktopShortcut = OfferDefault(pkg_.desktopShortcut);
    if (!OfferVisible(pkg_.startMenuShortcut)) o.startMenuShortcut = OfferDefault(pkg_.startMenuShortcut);
    std::map<std::string, bool> choices;
    for (const auto& opt : pkg_.ext.options) choices[opt.id] = opt.defaultValue;
    for (const auto& [id, on] : o.choices) {
        if (!choices.count(id)) return false;  // only options the package defines
        choices[id] = on;
    }
    for (const auto& opt : pkg_.ext.options) {
        if (opt.required) choices[opt.id] = true;  // locked on, whatever the UI or /option says
    }
    o.choices = std::move(choices);
    return true;
}

std::wstring Engine::DefaultInstallDir(Scope scope) const {
    if (auto p = FindExisting(); p && p->scope == scope && !p->installDir.empty()) return p->installDir;
    const std::wstring expanded = ExpandDirTemplate(scope == Scope::Machine ? pkg_.machineDirTemplate : pkg_.userDirTemplate, pkg_);
    std::wstring normalized;
    return path::ValidateAbsoluteSyntax(expanded, normalized) == path::Problem::None ? normalized : expanded;
}

uint64_t Engine::RequiredBytes() const {
    // What ends up on disk: the app's files plus the uninstaller (stub + WinUI front end + header).
    uint64_t ui = 0;
    for (const auto& s : pkg_.frontend.streams) ui += s.size;
    return pkg_.payload.totalRaw + container_.OverlayStart() + ui;
}

DirCheck Engine::CheckInstallDir(const std::wstring& dir, Scope scope, bool probeWritable) const {
    DirCheck r;
    r.requiredBytes = RequiredBytes();
    r.problem = path::ValidateAbsoluteSyntax(dir, r.normalized);
    if (r.problem != path::Problem::None) return r;
    const std::wstring& d = r.normalized;
    r.freeBytes = fs::FreeSpace(d);

    if (IsProtectedLocation(d)) return r.problem = path::Problem::Protected, r;
    if (fs::HasLinkInPath(d)) return r.problem = path::Problem::Reparse, r;

    const auto existing = FindExisting();
    if (existing && !existing->installDir.empty() && !EqualsIgnoreCase(existing->installDir, d)) {
        return r.problem = path::Problem::Locked, r;
    }
    const bool ourInstall = existing && EqualsIgnoreCase(existing->installDir, d);

    const DWORD attrs = fs::Attributes(d);
    if (attrs != INVALID_FILE_ATTRIBUTES) {
        if (!(attrs & FILE_ATTRIBUTE_DIRECTORY)) return r.problem = path::Problem::NotEmpty, r;
        if (!ourInstall && !fs::IsDirectoryEmpty(d)) {
            // Another installation lives here: never mix two apps in one folder.
            if (fs::Exists(path::Join(d, kRecordName)) || fs::Exists(path::Join(d, kUninstallerName))) {
                return r.problem = path::Problem::OtherApp, r;
            }
            // Otherwise allow it (e.g. user data kept by a previous uninstall) as long as setup would not overwrite a
            // single file it does not own. Preserved files are expected to be there already.
            for (const auto& f : pkg_.payload.files) {
                if (!f.preserve && fs::Exists(path::Join(d, f.path))) return r.problem = path::Problem::NotEmpty, r;
            }
        }
    }
    if (scope == Scope::User && probeWritable && !ProbeWritable(d)) return r.problem = path::Problem::NotWritable, r;
    constexpr uint64_t kMargin = 4u << 20;  // staging metadata, journal, file-system slack
    if (r.freeBytes != 0 && r.freeBytes < r.requiredBytes + kMargin) return r.problem = path::Problem::Space, r;
    return r;
}

void Engine::RecoverPending(Scope scope) const {
    const auto pending = GetPending(pkg_.appId, scope);
    if (!pending) return;
    const std::wstring& dir = *pending;
    const std::wstring workDir = path::Join(dir, kWorkDirName);
    const auto entries = ReadJournal(path::Join(workDir, L"journal.jsonl"));
    log::Warn(L"Found an unfinished operation in %s (%zu journal entries); recovering", dir.c_str(), entries.size());
    bool clean = true;
    if (!entries.empty()) {
        const json::Value& begin = entries.front();
        const bool matches = begin["op"].asString() == "begin" && EqualsIgnoreCase(begin["appId"].asWide(), pkg_.appId) &&
                             EqualsIgnoreCase(begin["dir"].asWide(), dir);
        if (matches && !JournalCommitted(entries)) {
            const auto stats = Rollback(entries, dir, scope, pkg_.appId);
            log::Info(L"Recovery rollback: %u undone, %u failed", stats.undone, stats.failed);
            clean = stats.failed == 0;
        }
    }
    if (clean && fs::IsDirectory(workDir) && !fs::IsLinkLike(workDir)) fs::DeleteTree(workDir);
    if (fs::IsDirectory(dir) && fs::IsDirectoryEmpty(dir)) ::RemoveDirectoryW(path::Long(dir).c_str());
    ClearPending(pkg_.appId, scope);
}

void Engine::InstallPayload(const std::wstring& dir, const std::wstring& backup, Journal& journal, Reporter& rep,
                            const std::atomic<bool>& cancel, std::vector<RecordFile>& recordFiles) {
    // Files are decoded straight to their final location (renames are expensive on Windows because file-system filters
    // inspect each one). Safety comes from the write-ahead journal: an existing file is first moved to the backup
    // folder (journal "backup"), a new file is announced before it is created (journal "place"), so a failure, cancel or
    // crash at any point rolls back to exactly the previous state.
    const auto& files = pkg_.payload.files;
    const uint64_t total = std::max<uint64_t>(pkg_.payload.totalRaw, 1);
    uint64_t done = 0;
    unsigned placed = 0;
    constexpr size_t kChunk = 1u << 20;
    auto buffer = std::make_unique<uint8_t[]>(kChunk);
    std::unordered_set<std::wstring> safeDirs;  // directories already verified to contain no links
    const auto dirIsSafe = [&](const std::wstring& d) {
        std::wstring key = ToLower(d);
        if (safeDirs.count(key)) return true;
        if (fs::HasLinkInPath(d)) return false;
        safeDirs.insert(std::move(key));
        return true;
    };

    size_t index = 0;
    for (uint32_t s = 0; s < pkg_.payload.streams.size(); ++s) {
        const StreamInfo& si = pkg_.payload.streams[s];
        archive::StreamReader reader(container_, si.offset, si.size, si.rawSize, si.sha256);
        for (; index < files.size() && files[index].stream == s; ++index) {
            ThrowIfCancelled(cancel);
            const FileInfo& f = files[index];
            const std::wstring target = path::Join(dir, f.path);
            std::vector<std::wstring> parents;
            fs::CreateDirectories(path::Parent(target), &parents);
            for (const auto& p : parents) journal.Mkdir(p);
            if (!dirIsSafe(path::Parent(target))) throw Failure(ErrorCode::UnsafePath, L"Link inside the install directory: " + f.path);

            bool discard = false;  // preserved user data that already exists: decode (to verify) but keep the user's copy
            const DWORD attrs = fs::Attributes(target);
            if (attrs != INVALID_FILE_ATTRIBUTES) {
                if (f.preserve) {
                    discard = true;
                } else {
                    if ((attrs & FILE_ATTRIBUTE_DIRECTORY) || fs::IsLinkLike(target)) {
                        throw Failure(ErrorCode::UnsafePath, L"Target is a directory or link: " + f.path);
                    }
                    const std::wstring saved = path::Join(backup, f.path);
                    fs::CreateDirectories(path::Parent(saved));
                    journal.Backup(target, saved);
                    if (attrs & FILE_ATTRIBUTE_READONLY) ::SetFileAttributesW(path::Long(target).c_str(), attrs & ~FILE_ATTRIBUTE_READONLY);
                    if (const DWORD e = fs::Move(target, saved)) ThrowMove(e, L"Cannot replace " + target);
                }
            }
            UniqueHandle h;
            if (!discard) {
                journal.Place(target);
                h.reset(::CreateFileW(path::Long(target).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                      FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
                if (!h) ThrowWin32(L"Cannot create " + target);
                if (f.size > (8u << 20)) {
                    FILE_ALLOCATION_INFO alloc{};
                    alloc.AllocationSize.QuadPart = static_cast<LONGLONG>(f.size);
                    ::SetFileInformationByHandle(h.get(), FileAllocationInfo, &alloc, sizeof(alloc));
                }
            }
            Sha256 hash;
            uint64_t remaining = f.size;
            while (remaining > 0) {
                ThrowIfCancelled(cancel);
                const size_t want = static_cast<size_t>(std::min<uint64_t>(remaining, kChunk));
                size_t got = 0;
                while (got < want) {
                    const size_t n = reader.Read(buffer.get() + got, want - got);
                    if (n == 0) throw Failure(ErrorCode::PackageCorrupt, L"Stream ended early in " + f.path);
                    got += n;
                }
                hash.Update(buffer.get(), got);
                if (h) {
                    DWORD written = 0;
                    if (!::WriteFile(h.get(), buffer.get(), static_cast<DWORD>(got), &written, nullptr) || written != got) {
                        ThrowWin32(L"Write failed for " + target);
                    }
                }
                remaining -= got;
                done += got;
                rep.Update(0.02 + 0.93 * (static_cast<double>(done) / static_cast<double>(total)), f.path);
            }
            h.reset();
            if (hash.Finish() != f.sha256) throw Failure(ErrorCode::PackageCorrupt, L"Checksum mismatch for " + f.path);
            if (!discard && f.attributes) ::SetFileAttributesW(path::Long(target).c_str(), f.attributes);
            recordFiles.push_back({f.path, f.size, f.sha256, f.preserve});
            if (!discard && faultAfter_ != 0 && ++placed >= faultAfter_) throw Failure(ErrorCode::IoError, L"Injected fault (test hook)");
        }
        uint8_t extra = 0;
        if (reader.Read(&extra, 1) != 0) throw Failure(ErrorCode::PackageCorrupt, L"Stream carries unexpected data");
    }
}
void Engine::WriteUninstaller(const std::wstring& dest) const {
    UniqueHandle h(::CreateFileW(path::Long(dest).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                 FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!h) ThrowWin32(L"Cannot create uninstaller");
    uint64_t pos = 0;
    const auto write = [&](const void* data, size_t size) {
        DWORD written = 0;
        if (!::WriteFile(h.get(), data, static_cast<DWORD>(size), &written, nullptr) || written != size) {
            ThrowWin32(L"Cannot write uninstaller");
        }
        pos += size;
    };
    const auto pad8 = [&] {
        static constexpr uint8_t zeros[8] = {};
        if (pos % 8) write(zeros, 8 - static_cast<size_t>(pos % 8));
    };

    container_.CopyStubTo(h.get());
    pos = container_.OverlayStart();
    pad8();
    const uint64_t overlayStart = pos;

    // Copy the WinUI front-end streams verbatim (they are already verified by hash whenever they are used).
    json::Value header = pkg_.raw;
    header.set("kind", "uninstaller");
    json::Value emptySet;
    emptySet.set("streams", json::Value::MakeArray());
    emptySet.set("files", json::Value::MakeArray());
    emptySet.set("directories", json::Value::MakeArray());
    header.set("payload", emptySet);
    if (pkg_.raw["frontend"].isObject()) {
        json::Value frontend = pkg_.raw["frontend"];
        json::Value streams = json::Value::MakeArray();
        std::vector<uint8_t> buffer(1u << 20);
        for (size_t i = 0; i < pkg_.frontend.streams.size(); ++i) {
            const StreamInfo& s = pkg_.frontend.streams[i];
            json::Value entry = pkg_.raw["frontend"]["streams"].array()[i];
            entry.set("offset", pos - overlayStart);
            streams.push(entry);
            uint64_t left = s.size, at = container_.OverlayStart() + s.offset;
            while (left > 0) {
                const size_t n = static_cast<size_t>(std::min<uint64_t>(left, buffer.size()));
                container_.ReadAt(at, buffer.data(), n);
                write(buffer.data(), n);
                at += n;
                left -= n;
            }
            pad8();
        }
        frontend.set("streams", streams);
        header.set("frontend", frontend);
    }

    const std::string json = json::Serialize(header);
    archive::Footer footer{};
    memcpy(footer.magic, archive::kMagic, sizeof(footer.magic));
    footer.version = archive::kFormatVersion;
    footer.flags = 0;  // the stub carries no compressor; the header is small and stored as is
    footer.overlayStart = overlayStart;
    footer.headerOffset = pos - overlayStart;
    footer.headerSize = json.size();
    footer.headerRawSize = json.size();
    const Sha256Digest digest = Sha256::Of({reinterpret_cast<const uint8_t*>(json.data()), json.size()});
    memcpy(footer.headerSha256, digest.data(), digest.size());
    write(json.data(), json.size());
    pad8();
    write(&footer, sizeof(footer));
    ::FlushFileBuffers(h.get());
}

// ---------------------------------------------------------------------------------------------------------------
// Actions

void Engine::RunActions(Action::When when, const std::wstring& dir, const std::map<std::string, bool>& choices, Outcome& out) const {
    for (const Action& a : pkg_.ext.actions) {
        if (a.when != when) continue;
        if (!a.ifOption.empty()) {
            bool selected = false, required = false;
            for (const auto& o : pkg_.ext.options) {
                if (o.id == a.ifOption) selected = o.defaultValue, required = o.required;
            }
            if (auto it = choices.find(a.ifOption); it != choices.end() && !required) selected = it->second;
            if (!selected) continue;
        }
        if (a.skipIfInstalled && RegistryVersionAtLeast(*a.skipIfInstalled)) {
            log::Info(L"Skipping %s: the required version is already installed", a.run.c_str());
            continue;
        }
        const std::wstring exe = path::Join(dir, a.run);
        if (!path::IsStrictlyUnder(exe, dir) || !fs::Exists(exe) || fs::HasLinkInPath(path::Parent(exe))) {
            throw Failure(ErrorCode::UnsafePath, L"Action target is not a file inside the install directory: " + a.run);
        }
        log::Info(L"Running %s (%zu argument(s))", a.run.c_str(), a.args.size());
        const auto succeeded = [&](const ActionResult& res) {
            return res.ok && std::find(a.successExitCodes.begin(), a.successExitCodes.end(), res.exitCode) != a.successExitCodes.end();
        };
        ActionResult r = RunProgram(exe, a.args, a.timeoutSeconds);
        if (!succeeded(r) && a.fallbackArgs) {
            // e.g. a driver installer whose silent mode failed: give the user its own (visible) UI.
            log::Warn(L"%s did not succeed (%s); retrying with its fallback arguments", a.run.c_str(),
                      r.ok ? Printf(L"exit code %lld", static_cast<long long>(r.exitCode)).c_str() : r.detail.c_str());
            r = RunProgram(exe, *a.fallbackArgs, 1800, true);
        }
        if (!succeeded(r) && !a.fallbackRunValue.empty()) {
            if (WriteRunValue(a.fallbackRunValue, exe, a.fallbackRunArgs)) {
                log::Warn(L"%s did not succeed; registered Run value %s instead", a.run.c_str(), a.fallbackRunValue.c_str());
                continue;
            }
        }
        const bool ok = succeeded(r);
        if (ok) {
            log::Info(L"%s finished (exit code %lld)", a.run.c_str(), static_cast<long long>(r.exitCode));
            continue;
        }
        const std::wstring what = a.name.Get(L"en-US", path::FileName(a.run)) +
                                  (r.ok ? Printf(L" returned exit code %lld", static_cast<long long>(r.exitCode)) : L": " + r.detail);
        if (a.onFailure == Action::OnFailure::Fail) throw Failure(ErrorCode::Failed, what);
        log::Warn(L"%s", what.c_str());
        if (a.onFailure == Action::OnFailure::Warn) out.warnings.push_back(what);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Install
Outcome Engine::Install(const InstallOptions& options, IProgressSink& sink, const std::atomic<bool>& cancel) {
    Outcome out;
    Reporter rep(sink);
    ComScope com;
    const Scope scope = options.scope;
    std::unique_ptr<Journal> journal;
    std::wstring dir, workDir;
    bool pendingSet = false;
    Existing existing;

    try {
        rep.Set(State::Validating, -1);
        log::Info(L"Install %s %s (%s scope) to '%s'", pkg_.appId.c_str(), pkg_.version.c_str(), ScopeName(scope),
                  options.installDir.c_str());
        if (pkg_.IsUninstaller()) throw Failure(ErrorCode::InvalidArguments, L"This executable is an uninstaller");
        if (!ScopeAllowed(scope)) throw Failure(ErrorCode::InvalidArguments, L"Requested scope is not allowed by the package");
        if (scope == Scope::Machine && !security::IsElevated()) {
            throw Failure(ErrorCode::ElevationRequired, L"Per-machine installation requires administrator rights");
        }
        existing = DetectExisting();
        out.upgrade = existing.product.has_value();
        if (existing.product && existing.product->scope != scope) {
            throw Failure(ErrorCode::InvalidArguments, std::wstring(L"The app is already installed for ") +
                                                           (existing.product->scope == Scope::Machine ? L"all users" : L"the current user") +
                                                           L"; uninstall it first to change the installation scope");
        }
        if (existing.blocked) {
            throw Failure(ErrorCode::DowngradeBlocked, L"Installed version " + existing.product->version + L" is newer than " + pkg_.version);
        }
        if (ForeignArpEntryExists(pkg_.appId, scope) && !(existing.foreign && existing.product->scope == scope)) {
            throw Failure(ErrorCode::RegistryError, L"Another product already uses the uninstall key '" + pkg_.appId + L"'");
        }
        RecoverPending(scope);
        std::vector<const Requirement*> installLater;  // missing prerequisites with a bundled or downloadable installer
        for (const auto& req : pkg_.ext.requirements) {
            const std::wstring found = InstalledRequirementVersion(req);
            if (RequirementSatisfied(req)) {
                log::Info(L"Prerequisite %s present%s%s", req.name.c_str(), found.empty() ? L"" : L": ", found.c_str());
                continue;
            }
            log::Info(L"Prerequisite %s missing%s%s", req.name.c_str(), found.empty() ? L"" : L" (found ", found.empty() ? L"" : (found + L", too old)").c_str());
            if (!req.Installable()) {
                throw Failure(ErrorCode::PrerequisiteMissing, req.name + (req.helpUrl.empty() ? std::wstring() : L"\n" + Utf8ToWide(req.helpUrl)));
            }
            installLater.push_back(&req);
        }

        const DirCheck check = CheckInstallDir(options.installDir, scope, scope == Scope::User);
        if (check.problem != path::Problem::None) {
            const ErrorCode code = check.problem == path::Problem::Space         ? ErrorCode::DiskFull
                                   : check.problem == path::Problem::NotWritable ? ErrorCode::AccessDenied
                                                                                 : ErrorCode::UnsafePath;
            throw Failure(code, L"Install location rejected (" + Utf8ToWide(path::ProblemKey(check.problem)) + L"): " +
                                    options.installDir);
        }
        dir = check.normalized;
        out.installDir = dir;

        // Prerequisites without a bundled installer are downloaded now, before anything is changed: a network problem
        // must not leave a half-installed app. Each file is checked against the size and SHA-256 in the package.
        std::map<const Requirement*, std::wstring> downloaded;
        std::wstring downloadDir;
        ScopeExit removeDownloads([&] {
            if (!downloadDir.empty()) fs::DeleteTree(downloadDir);
        });
        for (const Requirement* req : installLater) {
            if (!req->installer.empty()) continue;
            if (downloadDir.empty()) {
                downloadDir = path::Join(path::TempRoot(), L"FluentSetup-d-" + RandomToken().substr(0, 16));
                if (const DWORD e = security::CreatePrivateDirectory(downloadDir); e != 0) {
                    downloadDir.clear();
                    throw Failure(ErrorCode::IoError, L"Cannot create a download folder", e);
                }
            }
            rep.Set(State::Downloading, 0.0, req->name);
            const std::wstring dest = path::Join(downloadDir, Printf(L"prerequisite-%zu.exe", downloaded.size() + 1));
            std::wstring error;
            log::Info(L"Downloading %s from %S", req->name.c_str(), req->downloadUrl.c_str());
            const bool ok = Download(req->downloadUrl, dest, req->downloadSha256, req->downloadSize, cancel,
                                     [&](uint64_t done, uint64_t total) {
                                         rep.Update(total ? static_cast<double>(done) / static_cast<double>(total) : -1.0,
                                                    req->name + L" (" + FormatBytes(done) + L")");
                                     }, error);
            ThrowIfCancelled(cancel);
            if (!ok) throw Failure(ErrorCode::PrerequisiteMissing, req->name + L": " + error);
            downloaded[req] = dest;
        }

        std::optional<InstallRecord> oldRecord;
        if (existing.foreign) {
            log::Info(L"Adopting the existing installation of %s %s in %s (installed by another installer)", pkg_.appId.c_str(),
                      existing.product->version.c_str(), existing.product->installDir.c_str());
        } else if (existing.product) {
            try {
                oldRecord = LoadVerifiedRecord(*existing.product);
            } catch (const Failure& f) {
                log::Warn(L"Previous install record unusable, files of the old version may remain: %s", f.detail().c_str());
            }
        }
        ThrowIfCancelled(cancel);

        // ---- Preparing --------------------------------------------------------------------------------------
        rep.Set(State::Preparing, -1);
        for (const auto& still : CloseProcesses(pkg_.ext.closeProcesses)) log::Warn(L"%s could not be closed", still.c_str());
        if (existing.product && security::IsElevated()) {
            // The previous build's native images no longer match the assemblies about to be replaced.
            for (const auto& a : pkg_.ext.ngen) RunNGen(path::Join(dir, a), false);
        }
        SetPending(pkg_.appId, scope, dir);
        pendingSet = true;
        std::vector<std::wstring> created;
        const bool preexistingEmpty = fs::IsDirectory(dir) && fs::IsDirectoryEmpty(dir);
        fs::CreateDirectories(dir, &created);
        if (scope == Scope::Machine && !path::IsSameOrUnder(dir, path::KnownFolder(FOLDERID_ProgramFiles))) {
            // Outside Program Files the folder inherits whatever its parent allows (often write access for all users),
            // which would let a standard user replace binaries that admins and elevated actions run. Protect folders we
            // created and empty folders the user picked; an existing app folder is left alone, because the app may rely
            // on writing into it (upgrades of apps installed by other installers).
            const std::wstring target = !created.empty() ? created.front() : preexistingEmpty ? dir : std::wstring();
            if (!target.empty()) {
                if (const DWORD e = security::ApplyMachineInstallAcl(target); e != 0) {
                    log::Warn(L"Could not apply the protected ACL to %s (error %lu)", target.c_str(), e);
                }
            } else {
                log::Warn(L"Per-machine install into the existing folder %s keeps that folder's permissions", dir.c_str());
            }
        }
        {
            UniqueHandle dh(::CreateFileW(path::Long(dir).c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                          nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr));
            if (!dh || !EqualsIgnoreCase(fs::FinalPath(dh.get()), dir)) {
                throw Failure(ErrorCode::UnsafePath, L"Install directory resolves to a different location: " + dir);
            }
        }
        workDir = path::Join(dir, kWorkDirName);
        if (fs::Exists(workDir)) {
            if (fs::IsLinkLike(workDir)) throw Failure(ErrorCode::UnsafePath, L"Work directory is a link");
            fs::DeleteTree(workDir);
        }
        fs::CreateDirectories(workDir);
        ::SetFileAttributesW(path::Long(workDir).c_str(), FILE_ATTRIBUTE_HIDDEN);
        journal = std::make_unique<Journal>(path::Join(workDir, L"journal.jsonl"));
        journal->Begin(L"install", dir, scope, pkg_.appId);
        for (const auto& d : created) journal->Mkdir(d);
        const std::wstring backup = path::Join(workDir, L"b-" + RandomToken().substr(0, 10));
        fs::CreateDirectories(backup);

        // ---- Installing (decode + verify + place, journaled) ------------------------------------------------
        rep.Set(State::Installing, 0.02);
        std::vector<RecordFile> recordFiles;
        InstallPayload(dir, backup, *journal, rep, cancel, recordFiles);
        std::unordered_set<std::wstring> newSet;
        for (const auto& f : pkg_.payload.files) newSet.insert(ToLower(f.path));
        for (const auto& d : pkg_.payload.directories) {
            std::vector<std::wstring> made;
            fs::CreateDirectories(path::Join(dir, d), &made);
            for (const auto& p : made) journal->Mkdir(p);
        }
        // Files of the previous version that this version no longer ships.
        if (oldRecord) {
            for (const auto& f : oldRecord->files) {
                if (newSet.count(ToLower(f.path))) continue;
                const std::wstring target = path::Join(dir, f.path);
                if (f.preserve) {
                    if (fs::Exists(target)) recordFiles.push_back(f);
                    continue;
                }
                if (!fs::Exists(target) || fs::IsLinkLike(target) || fs::IsDirectory(target)) continue;
                const std::wstring saved = path::Join(backup, f.path);
                fs::CreateDirectories(path::Parent(saved));
                journal->Backup(target, saved);
                if (const DWORD e = fs::Move(target, saved)) ThrowMove(e, L"Cannot remove obsolete file " + target);
            }
        }
        ThrowIfCancelled(cancel);

        // ---- Registering ------------------------------------------------------------------------------------
        rep.Set(State::Registering, 0.96, {}, false);
        journal->Flush();
        const auto replaceInDir = [&](const std::wstring& target) {
            if (!fs::Exists(target)) return;
            const std::wstring saved = path::Join(backup, path::FileName(target));
            journal->Backup(target, saved);
            if (const DWORD e = fs::Move(target, saved)) ThrowMove(e, L"Cannot replace " + target);
        };
        const std::wstring uninstaller = path::Join(dir, kUninstallerName);
        const std::wstring uninstallerNew = path::Join(workDir, L"uninstall.new");
        WriteUninstaller(uninstallerNew);
        replaceInDir(uninstaller);
        journal->Place(uninstaller);
        if (const DWORD e = fs::Move(uninstallerNew, uninstaller)) ThrowMove(e, L"Cannot place the uninstaller");

        // Shortcuts.
        std::vector<std::wstring> shortcuts;
        const std::wstring mainExe = pkg_.mainExecutable.empty() ? std::wstring() : path::Join(dir, pkg_.mainExecutable);
        out.mainExecutable = mainExe;
        const auto folders = ShortcutFolders(scope);  // [0] = Start menu programs, [1] = desktop
        const auto wantShortcut = [&](Offer offer, bool chosen) {
            return offer == Offer::On || (OfferVisible(offer) && chosen);
        };
        const bool wantStart = !mainExe.empty() && wantShortcut(pkg_.startMenuShortcut, options.startMenuShortcut);
        const bool wantDesktop = !mainExe.empty() && wantShortcut(pkg_.desktopShortcut, options.desktopShortcut);
        const std::wstring linkName = ShortcutName(pkg_);
        const std::wstring startLink = folders[0].empty() ? std::wstring() : path::Join(folders[0], linkName);
        const std::wstring desktopLink = folders[1].empty() ? std::wstring() : path::Join(folders[1], linkName);
        std::vector<std::wstring> desired;
        if (wantStart && !startLink.empty()) desired.push_back(startLink);
        if (wantDesktop && !desktopLink.empty()) desired.push_back(desktopLink);
        int linkIndex = 0;
        for (const auto& link : desired) {
            std::wstring saved;
            if (fs::Exists(link)) {
                saved = path::Join(backup, L"link" + std::to_wstring(linkIndex++) + L".lnk");
                journal->Shortcut(link, saved);
                fs::Move(link, saved);
            } else {
                journal->Shortcut(link, L"");
            }
            CreateShortcut(link, mainExe, dir, pkg_.description);
            shortcuts.push_back(link);
        }
        if (oldRecord) {
            for (const auto& old : oldRecord->shortcuts) {
                const bool kept = std::any_of(desired.begin(), desired.end(), [&](const std::wstring& d) { return EqualsIgnoreCase(d, old); });
                if (kept || !fs::Exists(old)) continue;
                const std::wstring saved = path::Join(backup, L"link" + std::to_wstring(linkIndex++) + L".lnk");
                journal->Backup(old, saved);
                fs::Move(old, saved);
            }
        }

        // Install record.
        InstallRecord record;
        record.appId = pkg_.appId;
        record.name = pkg_.name;
        record.version = pkg_.version;
        record.publisher = pkg_.publisher;
        record.mainExecutable = pkg_.mainExecutable;
        record.scope = scope;
        record.installDir = dir;
        record.files = recordFiles;
        std::vector<std::wstring> relFiles;
        for (const auto& f : recordFiles) relFiles.push_back(f.path);
        record.directories = ImpliedDirectories(relFiles, pkg_.payload.directories);
        record.shortcuts = shortcuts;
        record.choices = options.choices;
        record.createdRoot = std::any_of(created.begin(), created.end(), [&](const std::wstring& c) { return EqualsIgnoreCase(c, dir); }) ||
                             (oldRecord && oldRecord->createdRoot);
        const std::string recordJson = json::Serialize(RecordToJson(record), 2);
        const std::wstring recordPath = path::Join(dir, kRecordName);
        replaceInDir(recordPath);
        journal->Place(recordPath);
        fs::WriteAllAtomic(recordPath, {reinterpret_cast<const uint8_t*>(recordJson.data()), recordJson.size()});

        // Apps & features entry.
        journal->Registry(SnapshotArp(pkg_.appId, scope));
        ArpEntry arp;
        arp.displayName = pkg_.name;
        arp.displayVersion = pkg_.version;
        arp.publisher = pkg_.publisher;
        arp.displayIcon = (mainExe.empty() ? uninstaller : mainExe) + L",0";
        arp.installLocation = dir;
        arp.uninstallString = L"\"" + uninstaller + L"\" /uninstall";
        arp.quietUninstallString = L"\"" + uninstaller + L"\" /uninstall /quiet";
        arp.urlInfoAbout = pkg_.website;
        arp.comments = pkg_.description;
        arp.recordSha256 = Utf8ToWide(ToHex(Sha256::Of({reinterpret_cast<const uint8_t*>(recordJson.data()), recordJson.size()})));
        arp.estimatedSizeKb = static_cast<uint32_t>(std::min<uint64_t>((pkg_.payload.totalRaw + 1023) / 1024, 0xFFFFFFFFu));
        Version v;
        Version::Parse(pkg_.version, v);
        arp.versionMajor = v.Major();
        arp.versionMinor = v.Minor();
        WriteArp(pkg_.appId, scope, arp);

        // Bundled prerequisite installers, then the package's own post-install steps (payload executables only).
        if (!installLater.empty()) rep.Set(State::InstallingPrerequisites, -1, {}, false);
        for (const Requirement* req : installLater) {
            const std::wstring exe = !req->installer.empty() ? path::Join(dir, req->installer) : downloaded[req];
            rep.Update(-1, req->name);
            log::Info(L"Installing prerequisite %s from %s", req->name.c_str(), exe.c_str());
            const ActionResult r = RunProgram(exe, req->installerArgs, 1800);
            if (!r.ok || !RequirementSatisfied(*req)) {
                const std::wstring found = InstalledRequirementVersion(*req);
                const std::wstring why = req->name + (found.empty() ? std::wstring() : L" " + found + L" < " + req->minVersionText) + L": " +
                                         (r.ok ? Printf(L"installer exit code %lld", static_cast<long long>(r.exitCode)) : r.detail);
                // A required minimum the app cannot run without: undo the installation rather than leave a broken app.
                if (req->mustSucceed) {
                    throw Failure(ErrorCode::PrerequisiteMissing, why + (req->helpUrl.empty() ? std::wstring() : L"\n" + Utf8ToWide(req->helpUrl)));
                }
                out.warnings.push_back(why);  // the app's files are in place; the user can install the runtime later
            }
        }
        // Leftovers of earlier versions that would otherwise run alongside this one (tasks, Run values, shortcuts),
        // removed before the actions so an action can register its own replacement. Not undone by a rollback.
        const Cleanup& ic = pkg_.ext.installCleanup;
        for (const auto& t : ic.removeScheduledTasks) {
            if (DeleteScheduledTask(t)) log::Info(L"Removed scheduled task %s", t.c_str());
        }
        for (const auto& r : ic.removeRunValues) DeleteRunValue(r);
        for (const auto& s : ic.removeShortcuts) DeleteShortcutsNamed(s);
        if (security::IsElevated()) {
            for (const auto& a : pkg_.ext.ngen) RunNGen(path::Join(dir, a), true);  // queued: compiled when the PC is idle
        }
        RunActions(Action::When::AfterInstall, dir, options.choices, out);

        // ---- Finalizing: point of no return -----------------------------------------------------------------
        rep.Set(State::Finalizing, 0.99, {}, false);
        journal->Commit();
        // Uninstall entries of older product names now point at files this install replaced.
        for (const auto& k : ic.removeUninstallKeys) {
            if (!EqualsIgnoreCase(k, pkg_.appId) && DeleteForeignArpEntry(k, scope)) log::Info(L"Removed uninstall entry %s", k.c_str());
        }
        for (const auto& k : pkg_.ext.legacyUninstallKeys) {
            if (!EqualsIgnoreCase(k, pkg_.appId) && DeleteForeignArpEntry(k, scope)) log::Info(L"Removed legacy uninstall entry %s", k.c_str());
        }
        if (oldRecord) {
            std::vector<std::wstring> oldFiles;
            for (const auto& f : oldRecord->files) oldFiles.push_back(f.path);
            RemoveEmptyDirectories(dir, ImpliedDirectories(oldFiles, oldRecord->directories));
        }
        journal.reset();
        if (!fs::DeleteTree(workDir)) log::Warn(L"Could not fully remove %s", workDir.c_str());
        ClearPending(pkg_.appId, scope);
        ::SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, dir.c_str(), nullptr);

        rep.Set(State::Completed, 1.0, {}, false);
        log::Info(L"Install completed: %zu files", pkg_.payload.files.size());
        return out;
    } catch (const Failure& f) {
        out.code = f.code();
        out.detail = Describe(f);
    } catch (const std::exception&) {
        out.code = ErrorCode::Internal;
        out.detail = L"Unexpected exception";
    }

    // ---- failure / cancel path ----------------------------------------------------------------------------------
    log::Error(L"Install failed: %s [%s]", out.detail.c_str(), ErrorCodeName(out.code));
    if (journal) {
        rep.Set(State::RollingBack, -1, {}, false);
        const auto entries = journal->Entries();
        journal.reset();
        if (!JournalCommitted(entries)) {
            const auto stats = Rollback(entries, dir, scope, pkg_.appId);
            log::Info(L"Rollback: %u undone, %u failed", stats.undone, stats.failed);
            out.rolledBack = stats.failed == 0;
            if (stats.failed == 0) fs::DeleteTree(workDir);
            if (fs::IsDirectory(dir) && fs::IsDirectoryEmpty(dir)) ::RemoveDirectoryW(path::Long(dir).c_str());
        }
    } else {
        out.rolledBack = true;  // nothing was changed yet
    }
    if (pendingSet && out.rolledBack) ClearPending(pkg_.appId, scope);
    rep.Set(State::Failed, -1, {}, false);
    return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Uninstall

Outcome Engine::Uninstall(const std::wstring& originDir, IProgressSink& sink, const std::atomic<bool>& cancel) {
    Outcome out;
    Reporter rep(sink);
    ComScope com;
    std::unique_ptr<Journal> journal;
    std::wstring dir, workDir;
    Scope scope = Scope::User;
    bool pendingSet = false;

    try {
        rep.Set(State::Validating, -1);
        std::optional<InstalledProduct> product;
        for (const Scope s : {Scope::User, Scope::Machine}) {
            auto p = QueryInstalled(pkg_.appId, s);
            if (p && (originDir.empty() || EqualsIgnoreCase(p->installDir, originDir))) {
                product = p;
                break;
            }
        }
        if (!product) throw Failure(ErrorCode::NotInstalled, L"No matching installation of " + pkg_.appId + L" was found");
        scope = product->scope;
        out.upgrade = true;  // "restored" wording on rollback
        log::Info(L"Uninstall %s %s (%s scope) from '%s'", pkg_.appId.c_str(), product->version.c_str(), ScopeName(scope),
                  product->installDir.c_str());
        if (scope == Scope::Machine && !security::IsElevated()) {
            throw Failure(ErrorCode::ElevationRequired, L"Removing a per-machine installation requires administrator rights");
        }
        RecoverPending(scope);
        const InstallRecord record = LoadVerifiedRecord(*product);
        dir = record.installDir;
        out.installDir = dir;
        if (fs::HasLinkInPath(dir)) throw Failure(ErrorCode::UnsafePath, L"Install directory goes through a link");
        ThrowIfCancelled(cancel);

        rep.Set(State::Preparing, -1);
        SetPending(pkg_.appId, scope, dir);
        pendingSet = true;
        workDir = path::Join(dir, kWorkDirName);
        if (fs::Exists(workDir)) {
            if (fs::IsLinkLike(workDir)) throw Failure(ErrorCode::UnsafePath, L"Work directory is a link");
            fs::DeleteTree(workDir);
        }
        fs::CreateDirectories(workDir);
        ::SetFileAttributesW(path::Long(workDir).c_str(), FILE_ATTRIBUTE_HIDDEN);
        journal = std::make_unique<Journal>(path::Join(workDir, L"journal.jsonl"));
        journal->Begin(L"uninstall", dir, scope, pkg_.appId);
        const std::wstring trash = path::Join(workDir, L"t-" + RandomToken().substr(0, 10));
        fs::CreateDirectories(trash);

        for (const auto& still : CloseProcesses(pkg_.ext.closeProcesses)) log::Warn(L"%s could not be closed", still.c_str());
        RunActions(Action::When::BeforeUninstall, dir, record.choices, out);
        if (security::IsElevated()) {
            for (const auto& a : pkg_.ext.ngen) RunNGen(path::Join(dir, a), false);  // drop native images before the files go
        }
        rep.Set(State::Removing, 0.0);
        size_t i = 0;
        std::unordered_set<std::wstring> safeDirs;
        const auto dirIsSafe = [&](const std::wstring& d) {
            std::wstring key = ToLower(d);
            if (safeDirs.count(key)) return true;
            if (fs::HasLinkInPath(d)) return false;
            safeDirs.insert(std::move(key));
            return true;
        };
        for (const auto& f : record.files) {
            ++i;
            ThrowIfCancelled(cancel);
            if (f.preserve) continue;
            const std::wstring target = path::Join(dir, f.path);
            const DWORD attrs = fs::Attributes(target);
            if (attrs == INVALID_FILE_ATTRIBUTES) continue;
            if ((attrs & FILE_ATTRIBUTE_DIRECTORY) || fs::IsLinkLike(target) || !dirIsSafe(path::Parent(target))) {
                log::Warn(L"Skipping %s: not a regular file inside the install directory", target.c_str());
                continue;
            }
            const std::wstring saved = path::Join(trash, f.path);
            fs::CreateDirectories(path::Parent(saved));
            journal->Backup(target, saved);
            if (attrs & FILE_ATTRIBUTE_READONLY) ::SetFileAttributesW(path::Long(target).c_str(), attrs & ~FILE_ATTRIBUTE_READONLY);
            if (const DWORD e = fs::Move(target, saved)) ThrowMove(e, L"Cannot remove " + target);
            rep.Update(0.9 * static_cast<double>(i) / static_cast<double>(std::max<size_t>(record.files.size(), 1)), f.path);
        }
        int linkIndex = 0;
        for (const auto& link : record.shortcuts) {
            if (!fs::Exists(link)) continue;
            const std::wstring saved = path::Join(trash, L"link" + std::to_wstring(linkIndex++) + L".lnk");
            journal->Backup(link, saved);
            fs::Move(link, saved);
        }
        const std::wstring recordPath = path::Join(dir, kRecordName);
        journal->Backup(recordPath, path::Join(trash, kRecordName));
        if (const DWORD e = fs::Move(recordPath, path::Join(trash, kRecordName))) ThrowMove(e, L"Cannot remove the install record");
        ThrowIfCancelled(cancel);

        rep.Set(State::Registering, 0.95, {}, false);
        journal->Registry(SnapshotArp(pkg_.appId, scope));
        DeleteArp(pkg_.appId, scope);

        rep.Set(State::Finalizing, 0.98, {}, false);
        journal->Commit();
        journal.reset();
        // Declared cleanup of things the app itself created (scheduled tasks, Run values, caches under AppData).
        for (const auto& t : pkg_.ext.cleanup.removeScheduledTasks) {
            if (DeleteScheduledTask(t)) log::Info(L"Removed scheduled task %s", t.c_str());
        }
        for (const auto& v : pkg_.ext.cleanup.removeRunValues) DeleteRunValue(v);
        for (const auto& s : pkg_.ext.cleanup.removeShortcuts) DeleteShortcutsNamed(s);
        for (const auto& tpl : pkg_.ext.cleanup.removeData) {
            const std::wstring target = ExpandDataTemplate(tpl);
            if (!target.empty() && fs::IsDirectory(target) && !fs::IsLinkLike(target)) {
                if (!fs::DeleteTree(target)) log::Warn(L"Could not fully remove %s", target.c_str());
            }
        }
        std::vector<std::wstring> relFiles;
        for (const auto& f : record.files) relFiles.push_back(f.path);
        RemoveEmptyDirectories(dir, ImpliedDirectories(relFiles, record.directories));
        if (!fs::DeleteTree(workDir)) log::Warn(L"Could not fully remove %s", workDir.c_str());
        const std::wstring uninstaller = path::Join(dir, kUninstallerName);
        if (fs::Exists(uninstaller) && fs::DeleteFileForce(uninstaller) != 0) {
            out.deferredDeletes.push_back(uninstaller);  // still running: removed after it exits
        }
        ClearPending(pkg_.appId, scope);
        if (fs::IsDirectory(dir)) {
            if (fs::IsDirectoryEmpty(dir)) {
                ::RemoveDirectoryW(path::Long(dir).c_str());
            } else if (!out.deferredDeletes.empty()) {
                out.deferredDeletes.push_back(dir);  // removed afterwards if it is empty by then
            }
            // Anything else in the directory was not installed by us (logs, user data): keep it and say so.
            WIN32_FIND_DATAW fd{};
            UniqueFind f(::FindFirstFileExW(path::Long(path::Join(dir, L"*")).c_str(), FindExInfoBasic, &fd,
                                            FindExSearchNameMatch, nullptr, 0));
            if (f.valid()) {
                do {
                    if (wcscmp(fd.cFileName, L".") && wcscmp(fd.cFileName, L"..") && !EqualsIgnoreCase(fd.cFileName, kUninstallerName)) {
                        out.leftovers = true;
                    }
                } while (!out.leftovers && ::FindNextFileW(f.get(), &fd));
            }
        }
        ::SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, path::Parent(dir).c_str(), nullptr);
        rep.Set(State::Completed, 1.0, {}, false);
        log::Info(L"Uninstall completed");
        return out;
    } catch (const Failure& f) {
        out.code = f.code();
        out.detail = Describe(f);
    } catch (const std::exception&) {
        out.code = ErrorCode::Internal;
        out.detail = L"Unexpected exception";
    }
    log::Error(L"Uninstall failed: %s [%s]", out.detail.c_str(), ErrorCodeName(out.code));
    if (journal) {
        rep.Set(State::RollingBack, -1, {}, false);
        const auto entries = journal->Entries();
        journal.reset();
        if (!JournalCommitted(entries)) {
            const auto stats = Rollback(entries, dir, scope, pkg_.appId);
            out.rolledBack = stats.failed == 0;
            if (stats.failed == 0) fs::DeleteTree(workDir);
        }
    } else {
        out.rolledBack = true;
    }
    if (pendingSet && out.rolledBack) ClearPending(pkg_.appId, scope);
    rep.Set(State::Failed, -1, {}, false);
    return out;
}

}  // namespace fsetup::install
