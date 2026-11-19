#include "Install.Core/differential.h"

#include "Archive/container.h"
#include "Common/error.h"
#include "Common/log.h"
#include "Common/path.h"
#include "Common/text.h"
#include "Install.Core/package.h"

#include <algorithm>
#include <map>
#include <memory>
#include <utility>
#include <vector>

namespace fsetup::install {
namespace {

constexpr uint64_t kHeadBytes = 64 * 1024;          // PE headers (they locate the certificate table)
constexpr uint64_t kTailBytes = 64 * 1024;          // before the end of the container data: footer and padding
constexpr uint64_t kMergeGap = 256 * 1024;          // a smaller piece between two downloads is downloaded along
constexpr uint64_t kMinReuse = 1024 * 1024;         // below this a differential download is not worth its requests
constexpr uint64_t kMaxFileSize = 4ull * 1024 * 1024 * 1024;

struct Piece {
    Sha256Digest sha256{};
    uint64_t offset = 0, size = 0;  // absolute in the file
};

// Every stream of a package (payload, front end, bundled installers) with its place in the file.
std::vector<Piece> StreamPieces(const archive::Container& c) {
    const Package p = ParsePackage(c.HeaderJson(), [&](uint64_t offset, uint64_t size) { return c.StreamRangeValid(offset, size); });
    std::vector<Piece> out;
    for (const FileSet* set : {&p.payload, &p.frontend, &p.redist}) {
        for (const auto& st : set->streams) out.push_back({st.sha256, c.OverlayStart() + st.offset, st.size});
    }
    return out;
}

bool WriteAt(HANDLE h, uint64_t offset, const void* data, size_t size) {
    OVERLAPPED ov{};
    ov.Offset = static_cast<DWORD>(offset);
    ov.OffsetHigh = static_cast<DWORD>(offset >> 32);
    DWORD written = 0;
    return ::WriteFile(h, data, static_cast<DWORD>(size), &written, &ov) && written == size;
}

bool ReadAt(HANDLE h, uint64_t offset, void* data, size_t size) {
    OVERLAPPED ov{};
    ov.Offset = static_cast<DWORD>(offset);
    ov.OffsetHigh = static_cast<DWORD>(offset >> 32);
    DWORD got = 0;
    return ::ReadFile(h, data, static_cast<DWORD>(size), &got, &ov) && got == size;
}

}  // namespace

bool RebuildFromBase(const RangeGetter& get, uint64_t expectedSize, const std::wstring& basePath, const std::wstring& dest,
                     const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress, std::wstring& why,
                     RebuildStats* stats) {
    RebuildStats local;
    RebuildStats& st = stats ? *stats : local;
    st = {};
    std::unique_ptr<archive::Container> base;
    std::map<std::pair<Sha256Digest, uint64_t>, uint64_t> old;  // (sha256, size) -> offset in the base
    try {
        base = archive::Container::Open(basePath);
        for (const Piece& p : StreamPieces(*base)) old.emplace(std::make_pair(p.sha256, p.size), p.offset);
    } catch (const Failure& f) {
        why = f.detail();
        return false;
    }
    UniqueHandle out(::CreateFileW(path::Long(dest).c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!out) {
        why = L"Cannot create the download file: " + Win32Message(::GetLastError());
        return false;
    }
    uint64_t done = 0;
    const auto report = [&](uint64_t n) {
        done += n;
        if (progress) progress(done, st.total);
    };
    const auto fetch = [&](uint64_t first, uint64_t end) {  // [first, end)
        if (end <= first) return true;
        if (cancel) return why = L"Cancelled", false;
        uint64_t at = first, size = 0;
        ++st.requests;
        if (!get(first, end, size, [&](const uint8_t* data, size_t n) {
                if (at + n > end || !WriteAt(out.get(), at, data, n)) return false;
                at += n;
                st.downloaded += n;
                report(n);
                return !cancel;
            }, why)) {
            if (why.empty()) why = cancel ? L"Cancelled" : L"Cannot store the download";
            return false;
        }
        if (at != end) return why = L"A range ended early", false;
        if (st.total != 0 && size != st.total) return why = L"The file changed on the server", false;
        st.total = size;
        return true;
    };

    // 1. The PE headers, which say where the certificate table and so the container's footer are.
    if (!fetch(0, kHeadBytes)) return false;
    if ((expectedSize != 0 && st.total != expectedSize) || st.total > kMaxFileSize || st.total < 2 * kHeadBytes) {
        why = L"Unexpected file size";
        return false;
    }
    LARGE_INTEGER end{};
    end.QuadPart = static_cast<LONGLONG>(st.total);
    if (!::SetFilePointerEx(out.get(), end, nullptr, FILE_BEGIN) || !::SetEndOfFile(out.get())) {
        why = L"Cannot size the download file: " + Win32Message(::GetLastError());
        return false;
    }
    uint8_t head[4096];
    if (!ReadAt(out.get(), 0, head, sizeof(head))) return why = L"Cannot read back the download", false;
    uint32_t peOffset = 0;
    memcpy(&peOffset, head + 0x3C, sizeof(peOffset));
    if (head[0] != 'M' || head[1] != 'Z' || peOffset < 0x40 || peOffset > sizeof(head) - 24 - 2 - 160 || memcmp(head + peOffset, "PE\0\0", 4) != 0) {
        why = L"Not a PE file";
        return false;
    }
    const uint32_t optional = peOffset + 24;
    uint16_t magic = 0;
    memcpy(&magic, head + optional, sizeof(magic));
    const uint32_t security = optional + (magic == 0x20B ? 112u : 96u) + 4 * 8;
    uint32_t certificates[2] = {};
    memcpy(certificates, head + security, sizeof(certificates));
    const uint64_t dataEnd = certificates[0] != 0 ? certificates[0] : st.total;
    if (dataEnd > st.total || dataEnd < 2 * kHeadBytes) return why = L"Bad certificate table offset", false;

    // 2. The end (footer, padding, certificate table), then the header the footer points at.
    const uint64_t tailStart = std::max(kHeadBytes, dataEnd - std::min(dataEnd, kTailBytes));
    if (!fetch(tailStart, st.total)) return false;
    archive::Footer footer{};
    bool found = false;
    for (uint64_t pad = 0; pad < 16 && !found; ++pad) {
        if (!ReadAt(out.get(), dataEnd - pad - archive::kFooterSize, &footer, sizeof(footer))) return why = L"Cannot read back the download", false;
        found = memcmp(footer.magic, archive::kMagic, sizeof(archive::kMagic)) == 0;
    }
    const uint64_t headerStart = footer.overlayStart + footer.headerOffset;
    if (!found || footer.headerSize > archive::kMaxHeaderBytes || headerStart < kHeadBytes || footer.headerOffset > dataEnd ||
        headerStart + footer.headerSize > dataEnd) {
        why = L"No valid container footer";
        return false;
    }
    if (!fetch(headerStart, std::min(headerStart + footer.headerSize, tailStart))) return false;
    out.reset();

    // 3. The new package's streams, from its header (checked against the footer by Container::Open).
    std::vector<Piece> fresh;
    try {
        fresh = StreamPieces(*archive::Container::Open(dest));
    } catch (const Failure& f) {
        why = f.detail();
        return false;
    }

    // 4. Plan: copy the streams the base has, download every other byte not fetched yet. Two downloads with only a
    // small copy between them become one.
    struct Copy {
        uint64_t to, from, size;
    };
    std::vector<Copy> copies;
    for (const Piece& p : fresh) {
        if (p.offset < kHeadBytes || p.offset + p.size > tailStart) continue;  // overlaps what is here already
        if (const auto it = old.find({p.sha256, p.size}); it != old.end()) copies.push_back({p.offset, it->second, p.size});
    }
    std::vector<std::pair<uint64_t, uint64_t>> have = {{0, kHeadBytes}, {tailStart, st.total}, {headerStart, headerStart + footer.headerSize}};
    for (const Copy& c : copies) have.emplace_back(c.to, c.to + c.size);
    std::sort(have.begin(), have.end());
    std::vector<std::pair<uint64_t, uint64_t>> need;
    uint64_t at = 0;
    for (const auto& [a, b] : have) {
        if (a > at) need.emplace_back(at, a);
        at = std::max(at, b);
    }
    if (at < st.total) need.emplace_back(at, st.total);
    std::vector<std::pair<uint64_t, uint64_t>> merged;
    for (const auto& n : need) {
        if (!merged.empty() && n.first - merged.back().second < kMergeGap) merged.back().second = n.second;
        else merged.push_back(n);
    }
    std::erase_if(copies, [&](const Copy& c) {
        return std::any_of(merged.begin(), merged.end(), [&](const auto& m) { return c.to >= m.first && c.to + c.size <= m.second; });
    });
    uint64_t reuse = 0;
    for (const Copy& c : copies) reuse += c.size;
    if (reuse < kMinReuse || reuse < st.total / 8) {
        why = Printf(L"only %llu of %llu bytes are in %s", static_cast<unsigned long long>(reuse), static_cast<unsigned long long>(st.total), basePath.c_str());
        return false;
    }
    log::Info(L"Differential download: %llu of %llu bytes from %s, the rest in %zu range request(s)", static_cast<unsigned long long>(reuse),
              static_cast<unsigned long long>(st.total), basePath.c_str(), merged.size());

    // 5. Copy, then download.
    out.reset(::CreateFileW(path::Long(dest).c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!out) return why = L"Cannot reopen the download file: " + Win32Message(::GetLastError()), false;
    std::vector<uint8_t> buffer(1u << 20);
    for (const Copy& c : copies) {
        for (uint64_t moved = 0; moved < c.size;) {
            if (cancel) return why = L"Cancelled", false;
            const size_t n = static_cast<size_t>(std::min<uint64_t>(buffer.size(), c.size - moved));
            try {
                base->ReadAt(c.from + moved, buffer.data(), n);
            } catch (const Failure& f) {
                return why = f.detail(), false;
            }
            if (!WriteAt(out.get(), c.to + moved, buffer.data(), n)) return why = L"Cannot write the download: " + Win32Message(::GetLastError()), false;
            moved += n;
            st.reused += n;
            report(n);
        }
    }
    for (const auto& [a, b] : merged) {
        if (!fetch(a, b)) return false;
    }
    return true;
}

}  // namespace fsetup::install
