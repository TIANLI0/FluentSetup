// Security-relevant unit tests: hostile package headers, hostile container layouts, install records, journal rollback.
#include "test.h"

#include "Archive/container.h"
#include "Common/error.h"
#include "Common/fs.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Install.Core/installed.h"
#include "Install.Core/journal.h"
#include "Install.Core/package.h"

#include <fstream>

using namespace fsetup;

namespace {

const std::string kZeroHash(64, '0');

std::string Header(const std::string& files, const std::string& streams = R"([{"offset":0,"size":16,"rawSize":3,"sha256":")" + kZeroHash + R"("}])",
                   const std::string& extraApp = "") {
    return R"({"format":1,"kind":"setup","app":{"id":"Test.App","name":"Test","version":"1.0.0")" + extraApp +
           R"(},"install":{"scope":"user"},"payload":{"streams":)" + streams + R"(,"files":)" + files + "}}";
}

std::string File(const std::string& path, int size = 3, int stream = 0, int offset = 0) {
    std::string escaped;
    for (const char c : path) {
        if (c == '\\' || c == '"') escaped.push_back('\\');
        escaped.push_back(c);
    }
    return R"({"path":")" + escaped + R"(","size":)" + std::to_string(size) + R"(,"sha256":")" + kZeroHash +
           R"(","stream":)" + std::to_string(stream) + R"(,"offset":)" + std::to_string(offset) + "}";
}

install::Package Parse(const std::string& h, uint64_t limit = 1 << 20) {
    return install::ParsePackage(h, [limit](uint64_t o, uint64_t s) { return o <= limit && s <= limit - o; });
}

std::wstring TempDir(const wchar_t* name) {
    const std::wstring d = path::Join(path::TempRoot(), std::wstring(L"fsetup-test-") + name + L"-" + RandomToken().substr(0, 8));
    fs::CreateDirectories(d);
    return d;
}

void WriteText(const std::wstring& p, const std::string& text) {
    fs::CreateDirectories(path::Parent(p));
    std::ofstream(p, std::ios::binary) << text;
}

std::string ReadText(const std::wstring& p) {
    std::ifstream f(p, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(f), {});
}

}  // namespace

TEST(package_header_valid_minimal) {
    const auto p = Parse(Header("[" + File("bin\\a.txt") + "]"));
    CHECK(p.appId == L"Test.App" && p.payload.files.size() == 1 && p.payload.files[0].path == L"bin\\a.txt");
}

TEST(package_header_rejects_path_traversal) {
    for (const char* bad : {"..\\..\\Windows\\evil.dll", "C:\\Windows\\evil.dll", "\\\\host\\share\\x", "a\\..\\..\\x",
                            "a/../../x", "evil.dll:ads", "CON", "dir\\NUL.txt"}) {
        CHECK_THROWS_CODE(Parse(Header("[" + File(bad) + "]")), ErrorCode::PackageCorrupt);
    }
}

TEST(package_header_rejects_installer_reserved_names) {
    for (const char* bad : {"uninstall.exe", "UNINSTALL.JSON", ".fsetup\\journal.jsonl"}) {
        CHECK_THROWS_CODE(Parse(Header("[" + File(bad) + "]")), ErrorCode::PackageCorrupt);
    }
}

TEST(package_header_rejects_duplicates_and_file_dir_clash) {
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt", 1) + "," + File("A.TXT", 2, 0, 1) + "]")), ErrorCode::PackageCorrupt);
    CHECK_THROWS_CODE(Parse(Header("[" + File("a", 1) + "," + File("a\\b", 2, 0, 1) + "]")), ErrorCode::PackageCorrupt);
}

TEST(package_header_rejects_bad_stream_accounting) {
    // file larger than its stream
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt", 4) + "]")), ErrorCode::PackageCorrupt);
    // non-contiguous offset
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt", 1, 0, 1) + "," + File("b.txt", 2, 0, 3) + "]")), ErrorCode::PackageCorrupt);
    // stream outside the container
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt") + "]"), 10), ErrorCode::PackageCorrupt);
    // missing stream
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt", 3, 1) + "]")), ErrorCode::PackageCorrupt);
    // overlapping streams
    const std::string two = R"([{"offset":0,"size":16,"rawSize":1,"sha256":")" + kZeroHash + R"("},{"offset":8,"size":16,"rawSize":1,"sha256":")" + kZeroHash + R"("}])";
    CHECK_THROWS_CODE(Parse(Header("[" + File("a", 1) + "," + File("b", 1, 1) + "]", two)), ErrorCode::PackageCorrupt);
    // negative / bogus sizes
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt", -1) + "]")), ErrorCode::PackageCorrupt);
}

TEST(package_header_rejects_bad_identity_and_main_exe) {
    CHECK_THROWS_CODE(install::ParsePackage(R"({"format":1,"kind":"setup","app":{"id":"..\\x","name":"T","version":"1"}})",
                                            [](uint64_t, uint64_t) { return true; }),
                      ErrorCode::PackageCorrupt);
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt") + "]", R"([{"offset":0,"size":16,"rawSize":3,"sha256":")" + kZeroHash + R"("}])",
                                   R"(,"mainExecutable":"cmd.exe")")),
                      ErrorCode::PackageCorrupt);
    CHECK_THROWS_CODE(Parse(Header("[" + File("a.txt") + "]", R"([{"offset":0,"size":16,"rawSize":3,"sha256":")" + kZeroHash + R"("}])",
                                   R"(,"mainExecutable":"a.txt")")),
                      ErrorCode::PackageCorrupt);
    CHECK_THROWS_CODE(Parse("{\"format\":2}"), ErrorCode::PackageCorrupt);
    CHECK_THROWS_CODE(Parse("not json"), ErrorCode::PackageCorrupt);
}

TEST(install_record_rejects_hostile_entries) {
    const auto rec = [](const std::string& files, const std::string& shortcuts = "[]") {
        json::Value v;
        json::Parse(R"({"format":1,"appId":"T","scope":"user","installDir":"C:\\Apps\\T","files":)" + files +
                        R"(,"directories":[],"shortcuts":)" + shortcuts + "}",
                    v);
        return v;
    };
    CHECK(install::RecordFromJson(rec(R"([{"path":"a.txt","size":1,"sha256":")" + kZeroHash + R"("}])")).files.size() == 1);
    CHECK_THROWS_CODE(install::RecordFromJson(rec(R"([{"path":"..\\..\\Windows\\system32\\x.dll","size":1,"sha256":")" + kZeroHash + R"("}])")),
                      ErrorCode::NotInstalled);
    CHECK_THROWS_CODE(install::RecordFromJson(rec("[]", R"(["C:\\Windows\\notepad.lnk"])")), ErrorCode::NotInstalled);
    CHECK_THROWS_CODE(install::RecordFromJson(rec("[]", R"(["C:\\Users\\Public\\Desktop\\x.exe"])")), ErrorCode::NotInstalled);
}

TEST(shortcut_paths_are_confined_to_scope_folders) {
    const std::wstring programs = path::KnownFolder(FOLDERID_Programs);
    CHECK(install::IsAllowedShortcutPath(path::Join(programs, L"App.lnk"), install::Scope::User));
    CHECK(!install::IsAllowedShortcutPath(path::Join(programs, L"Sub\\App.lnk"), install::Scope::User));
    CHECK(!install::IsAllowedShortcutPath(path::Join(programs, L"App.exe"), install::Scope::User));
    CHECK(!install::IsAllowedShortcutPath(path::Join(programs, L"App.lnk"), install::Scope::Machine));
}

TEST(journal_rollback_restores_previous_state) {
    const std::wstring dir = TempDir(L"journal");
    const std::wstring work = path::Join(dir, L".fsetup");
    fs::CreateDirectories(work);
    WriteText(path::Join(dir, L"keep.txt"), "original");
    {
        install::Journal j(path::Join(work, L"journal.jsonl"));
        j.Begin(L"install", dir, install::Scope::User, L"Test.App");
        // replace keep.txt
        const std::wstring saved = path::Join(work, L"b\\keep.txt");
        fs::CreateDirectories(path::Parent(saved));
        j.Backup(path::Join(dir, L"keep.txt"), saved);
        CHECK(fs::Move(path::Join(dir, L"keep.txt"), saved) == 0);
        j.Place(path::Join(dir, L"keep.txt"));
        WriteText(path::Join(dir, L"keep.txt"), "new");
        // add a new file in a new directory
        j.Mkdir(path::Join(dir, L"sub"));
        j.Place(path::Join(dir, L"sub\\new.txt"));
        WriteText(path::Join(dir, L"sub\\new.txt"), "new");
        // a hostile entry pointing outside the install directory must be ignored
        WriteText(path::Join(path::Parent(dir), L"fsetup-outside.txt"), "victim");
        j.Place(path::Join(path::Parent(dir), L"fsetup-outside.txt"));
        j.Flush();
    }
    const auto entries = install::ReadJournal(path::Join(work, L"journal.jsonl"));
    CHECK(entries.size() == 6 && !install::JournalCommitted(entries));
    const auto stats = install::Rollback(entries, dir, install::Scope::User, L"Test.App");
    CHECK(stats.failed == 0);
    CHECK(ReadText(path::Join(dir, L"keep.txt")) == "original");
    CHECK(!fs::Exists(path::Join(dir, L"sub")));
    CHECK(ReadText(path::Join(path::Parent(dir), L"fsetup-outside.txt")) == "victim");
    ::DeleteFileW(path::Join(path::Parent(dir), L"fsetup-outside.txt").c_str());
    fs::DeleteTree(dir);
}

TEST(journal_tolerates_torn_last_line) {
    const std::wstring dir = TempDir(L"torn");
    WriteText(path::Join(dir, L"j.jsonl"), "{\"op\":\"begin\"}\n{\"op\":\"place\",\"pa");
    CHECK(install::ReadJournal(path::Join(dir, L"j.jsonl")).size() == 1);
    fs::DeleteTree(dir);
}

TEST(link_detection) {
    const std::wstring dir = TempDir(L"links");
    const std::wstring target = path::Join(dir, L"target");
    const std::wstring junction = path::Join(dir, L"junction");
    fs::CreateDirectories(target);
    const std::wstring cmd = L"cmd.exe /c mklink /J \"" + junction + L"\" \"" + target + L"\" >nul";
    _wsystem(cmd.c_str());
    CHECK(fs::IsLinkLike(junction));
    CHECK(fs::HasLinkInPath(path::Join(junction, L"child")));
    CHECK(!fs::HasLinkInPath(path::Join(target, L"child")));
    CHECK(fs::DeleteTree(dir));  // removes the junction without following it
    CHECK(!fs::Exists(target));
}

// ---- container layout ------------------------------------------------------------------------------------------

namespace {

std::wstring StubPath() { return path::Join(path::Parent(path::ModulePath()), L"FluentSetup.Stub.exe"); }

// Builds stub + uncompressed header + footer, then lets `mutate` damage the bytes.
std::wstring MakeContainer(const std::string& header, const std::function<void(std::vector<uint8_t>&, size_t footerPos)>& mutate) {
    auto bytes = fs::ReadAll(StubPath(), 64 << 20);
    while (bytes.size() % 8) bytes.push_back(0);
    const uint64_t overlay = bytes.size();
    bytes.insert(bytes.end(), header.begin(), header.end());
    while (bytes.size() % 8) bytes.push_back(0);
    archive::Footer f{};
    memcpy(f.magic, archive::kMagic, 8);
    f.version = archive::kFormatVersion;
    f.overlayStart = overlay;
    f.headerOffset = 0;
    f.headerSize = header.size();
    f.headerRawSize = header.size();
    const auto digest = Sha256::Of({reinterpret_cast<const uint8_t*>(header.data()), header.size()});
    memcpy(f.headerSha256, digest.data(), 32);
    const size_t footerPos = bytes.size();
    bytes.resize(bytes.size() + sizeof(f));
    memcpy(bytes.data() + footerPos, &f, sizeof(f));
    if (mutate) mutate(bytes, footerPos);
    const std::wstring file = path::Join(path::TempRoot(), L"fsetup-container-" + RandomToken().substr(0, 8) + L".exe");
    fs::WriteAllAtomic(file, bytes);
    return file;
}

const std::string kUninstallerHeader =
    R"({"format":1,"kind":"uninstaller","app":{"id":"Test.App","name":"Test","version":"1.0.0"},"install":{"scope":"user"}})";

}  // namespace

TEST(container_valid_minimal) {
    const auto file = MakeContainer(kUninstallerHeader, nullptr);
    auto c = archive::Container::Open(file);
    CHECK(c->HeaderJson() == kUninstallerHeader);
    c.reset();
    ::DeleteFileW(file.c_str());
}

TEST(container_rejects_damage) {
    const std::vector<std::function<void(std::vector<uint8_t>&, size_t)>> damage = {
        [](std::vector<uint8_t>& b, size_t) { b[b.size() - 81 - 5] ^= 0xFF; },                       // header byte
        [](std::vector<uint8_t>& b, size_t pos) { b[pos] = 'X'; },                                    // magic
        [](std::vector<uint8_t>& b, size_t pos) { b[pos + 8] = 9; },                                  // version
        [](std::vector<uint8_t>& b, size_t pos) { *reinterpret_cast<uint64_t*>(&b[pos + 16]) = 4; }, // overlay inside PE image
        [](std::vector<uint8_t>& b, size_t pos) { *reinterpret_cast<uint64_t*>(&b[pos + 32]) = 1ull << 40; },  // header size
        [](std::vector<uint8_t>& b, size_t pos) { *reinterpret_cast<uint64_t*>(&b[pos + 24]) = ~0ull; },       // header offset
        [](std::vector<uint8_t>& b, size_t pos) { b[pos + 12] = 0x80; },                              // unknown flags
        [](std::vector<uint8_t>& b, size_t) { b.resize(b.size() - 3); },                              // truncated footer
    };
    for (const auto& d : damage) {
        const auto file = MakeContainer(kUninstallerHeader, d);
        CHECK_THROWS_CODE(archive::Container::Open(file), ErrorCode::PackageCorrupt);
        ::DeleteFileW(file.c_str());
    }
}

TEST(container_stream_ranges) {
    const auto file = MakeContainer(kUninstallerHeader, nullptr);
    auto c = archive::Container::Open(file);
    CHECK(c->StreamRangeValid(0, 0));
    CHECK(!c->StreamRangeValid(0, 1));               // header starts at offset 0: no room for streams
    CHECK(!c->StreamRangeValid(~0ull, 2));            // overflow-safe
    c.reset();
    ::DeleteFileW(file.c_str());
}
