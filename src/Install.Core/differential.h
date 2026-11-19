// Differential downloads: rebuilding a new Setup.exe from an earlier one plus range requests.
//
// A new Setup.exe mostly repeats the streams of the last one: the builder ends its blocks at the same files every time,
// so unchanged files come out as byte-identical streams. RebuildFromBase writes the new file byte for byte: the stub,
// the header, the footer, the certificate table and every new stream come through `get` (range requests); streams the
// base package already has, found by their SHA-256 and size, are copied from it. The caller checks the finished file
// against the published SHA-256 (and signer) exactly like a full download, so nothing about what may run changes.
#pragma once

#include "Common/win.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <string>

namespace fsetup::install {

// Delivers bytes [first, end) of the new file to `sink`, in order, and sets `total` to the whole file's size. False with
// `why` when it cannot (for https: no range support, another answer, a transport error).
using RangeGetter = std::function<bool(uint64_t first, uint64_t end, uint64_t& total,
                                       const std::function<bool(const uint8_t*, size_t)>& sink, std::wstring& why)>;

struct RebuildStats {
    uint64_t total = 0;       // size of the new file
    uint64_t reused = 0;      // bytes copied from the base
    uint64_t downloaded = 0;  // bytes delivered by `get`
    size_t requests = 0;      // calls of `get`
};

// Writes the new file to `dest` (must not exist). `expectedSize`: the announced size, 0 if unknown. True when the file
// is complete (still to be checked by the caller); false with `why` when the base cannot help (nothing or too little in
// common, a malformed file) or a range fails. The caller deletes `dest` after a failure.
bool RebuildFromBase(const RangeGetter& get, uint64_t expectedSize, const std::wstring& basePath, const std::wstring& dest,
                     const std::atomic<bool>& cancel, const std::function<void(uint64_t, uint64_t)>& progress, std::wstring& why,
                     RebuildStats* stats = nullptr);

}  // namespace fsetup::install
