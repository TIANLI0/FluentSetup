// Branch filters for executable code (header "filter" of a payload file; see docs/package-format.md, section 3.3).
//
// Calls and jumps in machine code hold relative targets: the same function called from many places has a different
// operand at each call site. Turning those operands into absolute positions makes them repeat, which compresses better.
// Both filters only change operand bits, never the opcode bits that decide where a filter applies, and the change is a
// bijection, so decoding is the exact inverse whatever the bytes are (data that merely looks like code included).
//
//   x86 (x86 and x64): at every byte E8 (call rel32) or E9 (jmp rel32) whose 4-byte operand lies in the file, an operand
//     v in [-2^24, 2^24) becomes the 25-bit sign extension of (v + p) mod 2^25, p = offset of the next instruction; the
//     scan continues after the operand. Other operands are left alone.
//   arm64: every 4-byte aligned word 100101xx... (BL imm26) gets imm26 + offset/4 (mod 2^26).
//
// The same code runs in the builder (PeFilter.cs, encode) and in setup (decode); positions are file offsets, so a file
// can be processed in chunks of any size.
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace fsetup::archive {

enum class BranchFilter : uint8_t { None, X86, Arm64 };

class BranchFilterState {
public:
    BranchFilterState(BranchFilter kind, bool encode) noexcept : kind_(kind), encode_(encode) {}

    // Filters `data[0, size)`, the bytes of the file from the current position on, in place. Returns how many leading
    // bytes are final; the caller keeps the rest (at most 4 bytes) and passes them again, followed by more of the file.
    // With `last` (no more bytes follow) everything is final.
    size_t Process(uint8_t* data, size_t size, bool last) noexcept {
        size_t done = size;
        switch (kind_) {
            case BranchFilter::None:
                break;
            case BranchFilter::X86:
                done = X86(data, size, last);
                break;
            case BranchFilter::Arm64:
                done = Arm64(data, size, last);
                break;
        }
        position_ += done;
        return done;
    }

private:
    static uint32_t Load(const uint8_t* p) noexcept {
        uint32_t v;
        memcpy(&v, p, 4);
        return v;
    }
    static void Store(uint8_t* p, uint32_t v) noexcept { memcpy(p, &v, 4); }

    size_t X86(uint8_t* data, size_t size, bool last) const noexcept {
        size_t i = 0;
        while (i < size) {
            if (data[i] != 0xE8 && data[i] != 0xE9) {
                ++i;
                continue;
            }
            if (i + 5 > size) return last ? size : i;  // operand not here yet (or past the end of the file)
            const uint32_t v = Load(data + i + 1);
            if (v + 0x01000000u < 0x02000000u) {  // in [-2^24, 2^24)
                const uint32_t next = static_cast<uint32_t>(position_ + i + 5);
                uint32_t w = (encode_ ? v + next : v - next) & 0x01FFFFFFu;
                if (w & 0x01000000u) w |= 0xFE000000u;
                Store(data + i + 1, w);
            }
            i += 5;
        }
        return size;
    }

    size_t Arm64(uint8_t* data, size_t size, bool last) const noexcept {
        const size_t words = size & ~static_cast<size_t>(3);
        for (size_t i = 0; i < words; i += 4) {
            const uint32_t w = Load(data + i);
            if ((w & 0xFC000000u) != 0x94000000u) continue;
            const uint32_t at = static_cast<uint32_t>((position_ + i) >> 2);
            Store(data + i, 0x94000000u | ((encode_ ? w + at : w - at) & 0x03FFFFFFu));
        }
        return last ? size : words;
    }

    BranchFilter kind_;
    bool encode_;
    uint64_t position_ = 0;  // file offset of data[0]
};

}  // namespace fsetup::archive
