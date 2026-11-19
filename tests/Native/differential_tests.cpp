// Differential downloads: a new package rebuilt from an earlier one and range requests (install::RebuildFromBase).
#include "test.h"
#include "Archive/container.h"
#include "Common/error.h"
#include "Common/fs.h"
#include "Common/hash.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Archive/pefilter.h"
#include "Install.Core/differential.h"

#include <algorithm>
#include <atomic>

using namespace fsetup;

namespace {

std::vector<uint8_t> Noise(size_t n, uint32_t seed) {
    std::vector<uint8_t> out(n);
    uint32_t x = seed * 2654435761u + 1;
    for (auto& b : out) {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        b = static_cast<uint8_t>(x);
    }
    return out;
}

std::string Hex(const Sha256Digest& d) {
    static constexpr char kHex[] = "0123456789abcdef";
    std::string s;
    for (const uint8_t b : d) s += kHex[b >> 4], s += kHex[b & 15];
    return s;
}

// The installer stub + the streams (any bytes: they are not decoded here) + an uncompressed header + the footer.
std::vector<uint8_t> Package(const std::vector<std::vector<uint8_t>>& streams) {
    auto bytes = fs::ReadAll(path::Join(path::Parent(path::ModulePath()), L"FluentSetup.Stub.exe"), 64 << 20);
    while (bytes.size() % 8) bytes.push_back(0);
    const uint64_t overlay = bytes.size();
    std::string list, files;
    for (size_t i = 0; i < streams.size(); ++i) {
        const uint64_t offset = bytes.size() - overlay;
        bytes.insert(bytes.end(), streams[i].begin(), streams[i].end());
        while (bytes.size() % 8) bytes.push_back(0);
        const std::string size = std::to_string(streams[i].size());
        list += std::string(i ? "," : "") + R"({"offset":)" + std::to_string(offset) + R"(,"size":)" + size + R"(,"rawSize":)" + size +
                R"(,"sha256":")" + Hex(Sha256::Of(streams[i])) + R"("})";
        files += std::string(i ? "," : "") + R"({"path":"f)" + std::to_string(i) + R"(.bin","size":)" + size + R"(,"sha256":")" +
                 std::string(64, '0') + R"(","stream":)" + std::to_string(i) + R"(,"offset":0})";
    }
    const std::string header = R"({"format":1,"kind":"setup","app":{"id":"Test.Diff","name":"Diff","version":"1.0.0"},"install":{"scope":"user"},)"
                               R"("payload":{"streams":[)" + list + R"(],"files":[)" + files + R"(],"directories":[]}})";
    const uint64_t headerOffset = bytes.size() - overlay;
    bytes.insert(bytes.end(), header.begin(), header.end());
    while (bytes.size() % 8) bytes.push_back(0);
    archive::Footer f{};
    memcpy(f.magic, archive::kMagic, 8);
    f.version = archive::kFormatVersion;
    f.overlayStart = overlay;
    f.headerOffset = headerOffset;
    f.headerSize = header.size();
    f.headerRawSize = header.size();
    const auto digest = Sha256::Of({reinterpret_cast<const uint8_t*>(header.data()), header.size()});
    memcpy(f.headerSha256, digest.data(), 32);
    const size_t at = bytes.size();
    bytes.resize(at + sizeof(f));
    memcpy(bytes.data() + at, &f, sizeof(f));
    return bytes;
}

std::wstring Save(const std::vector<uint8_t>& bytes) {
    const std::wstring file = path::Join(path::TempRoot(), L"fsetup-diff-" + RandomToken().substr(0, 8) + L".exe");
    fs::WriteAllAtomic(file, bytes);
    return file;
}

// A "server" that answers range requests from memory.
install::RangeGetter Server(const std::vector<uint8_t>& file) {
    return [&file](uint64_t first, uint64_t end, uint64_t& total, const std::function<bool(const uint8_t*, size_t)>& sink, std::wstring& why) {
        total = file.size();
        if (end > file.size()) return why = L"416", false;
        for (uint64_t at = first; at < end;) {
            const size_t n = static_cast<size_t>(std::min<uint64_t>(100000, end - at));
            if (!sink(file.data() + at, n)) return false;
            at += n;
        }
        return true;
    };
}

}  // namespace

TEST(differential_download_rebuilds_the_new_package_byte_for_byte) {
    const auto a = Noise(700 * 1024, 1), b = Noise(700 * 1024, 2), c = Noise(700 * 1024, 3), d = Noise(700 * 1024, 4);
    const auto oldBytes = Package({a, b, c, d});
    const auto newBytes = Package({a, Noise(700 * 1024, 5), c, Noise(500 * 1024, 6)});  // a and c are unchanged
    const std::wstring base = Save(oldBytes);
    const std::wstring dest = path::Join(path::TempRoot(), L"fsetup-diff-out-" + RandomToken().substr(0, 8) + L".exe");
    std::atomic<bool> cancel{false};
    uint64_t lastDone = 0;
    bool monotonic = true;
    install::RebuildStats stats;
    std::wstring why;
    const bool ok = install::RebuildFromBase(Server(newBytes), newBytes.size(), base, dest, cancel,
                                             [&](uint64_t done, uint64_t) { monotonic = monotonic && done >= lastDone; lastDone = done; }, why, &stats);
    CHECK(ok);
    CHECK(fs::ReadAll(dest, 64 << 20) == newBytes);
    CHECK(stats.total == newBytes.size());
    CHECK(stats.reused == a.size() + c.size());               // both unchanged streams came from the old package
    CHECK(stats.reused + stats.downloaded == newBytes.size());  // every other byte was downloaded exactly once
    CHECK(monotonic && lastDone == newBytes.size());
    ::DeleteFileW(dest.c_str());

    // The announced size must match.
    CHECK(!install::RebuildFromBase(Server(newBytes), newBytes.size() + 1, base, dest, cancel, nullptr, why));
    ::DeleteFileW(dest.c_str());
    ::DeleteFileW(base.c_str());
}

TEST(differential_download_needs_something_in_common) {
    const auto oldBytes = Package({Noise(900 * 1024, 11), Noise(900 * 1024, 12)});
    const auto newBytes = Package({Noise(900 * 1024, 13), Noise(900 * 1024, 14)});
    const std::wstring base = Save(oldBytes);
    const std::wstring dest = path::Join(path::TempRoot(), L"fsetup-diff-out-" + RandomToken().substr(0, 8) + L".exe");
    std::atomic<bool> cancel{false};
    std::wstring why;
    CHECK(!install::RebuildFromBase(Server(newBytes), 0, base, dest, cancel, nullptr, why));
    CHECK(why.find(L"only 0 of") == 0);
    ::DeleteFileW(dest.c_str());

    // A base that is no package at all is refused without touching the network.
    const std::wstring junk = Save(Noise(4096, 15));
    size_t calls = 0;
    const install::RangeGetter counting = [&](uint64_t, uint64_t, uint64_t&, const std::function<bool(const uint8_t*, size_t)>&, std::wstring&) {
        ++calls;
        return false;
    };
    CHECK(!install::RebuildFromBase(counting, 0, junk, dest, cancel, nullptr, why));
    CHECK(calls == 0);
    ::DeleteFileW(junk.c_str());
    ::DeleteFileW(base.c_str());
}

// ---- branch filters (Archive/pefilter.h) ---------------------------------------------------------------------------
// The same vectors and digests as tests/Builder.Core.Tests/BranchFilterTests.cs: the builder's encoder and setup's
// decoder are exact inverses.

namespace {

std::vector<uint8_t> FilterVector(archive::BranchFilter kind, size_t size) {
    std::vector<uint8_t> b(size);
    for (size_t k = 0; k < size; ++k) {
        b[k] = static_cast<uint8_t>((static_cast<uint32_t>(k) * 2654435761u) >> 24);
        if (kind == archive::BranchFilter::X86) {
            if (k % 11 == 0) b[k] = 0xE8;
            else if (k % 17 == 0) b[k] = 0xE9;
        }
    }
    if (kind == archive::BranchFilter::Arm64) {
        for (size_t j = 0; 4 * j + 3 < size; j += 3) b[4 * j + 3] = static_cast<uint8_t>(0x94 | (b[4 * j + 3] & 3));
    }
    return b;
}

std::vector<uint8_t> RunFilter(archive::BranchFilter kind, bool encode, const std::vector<uint8_t>& input, const std::vector<size_t>& chunks) {
    archive::BranchFilterState state(kind, encode);
    std::vector<uint8_t> out, buffer(input.size() + 8);
    size_t carry = 0, at = 0, c = 0;
    while (at < input.size() || carry > 0) {
        const size_t n = std::min(chunks[c++ % chunks.size()], input.size() - at);
        memcpy(buffer.data() + carry, input.data() + at, n);
        at += n;
        const size_t avail = carry + n;
        const size_t ready = state.Process(buffer.data(), avail, at == input.size());
        out.insert(out.end(), buffer.begin(), buffer.begin() + static_cast<ptrdiff_t>(ready));
        memmove(buffer.data(), buffer.data() + ready, avail - ready);
        carry = avail - ready;
    }
    return out;
}

}  // namespace

TEST(branch_filters_match_the_builder) {
    const struct {
        archive::BranchFilter kind;
        size_t size;
        const char* encoded;
    } cases[] = {
        {archive::BranchFilter::X86, 200003, "973ab2325b99d1c69eaae695978bb4780fcb37ad06f59fa013d6019988c32c66"},
        {archive::BranchFilter::Arm64, 200002, "af50bf804762dd7362203c8d99f49bbf5893b7d4f42650869178d314a4202d12"},
    };
    for (const auto& c : cases) {
        const auto input = FilterVector(c.kind, c.size);
        const auto encoded = RunFilter(c.kind, true, input, {SIZE_MAX});
        CHECK(Hex(Sha256::Of(encoded)) == c.encoded);  // what the builder writes
        CHECK(RunFilter(c.kind, false, encoded, {SIZE_MAX}) == input);
        CHECK(RunFilter(c.kind, false, encoded, {1, 2, 3, 5, 7, 4096, 1u << 20}) == input);  // setup's 1 MiB chunks, or any
        CHECK(RunFilter(c.kind, true, input, {3, 1, 4, 1, 5, 9, 2, 6}) == encoded);
    }
}
