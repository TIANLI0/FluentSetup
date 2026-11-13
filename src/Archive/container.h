// FluentSetup self-extracting container (format v1). See docs/package-format.md.
//
//   [ PE image of the stub (incl. resources) ]
//   [ overlay: stream 0 | stream 1 | ... | header | padding ]   <- every piece 8-byte aligned
//   [ footer, 80 bytes ]
//   [ Authenticode certificate table (optional, appended by signtool) ]
//
// The footer is located right before the certificate table (from the PE security directory) or at end of file.
// All offsets in the footer and header are bounds-checked before use; nothing in the overlay is executed or
// extracted unless its SHA-256 matches.
#pragma once

#include "Common/hash.h"
#include "Common/win.h"

#include <memory>
#include <string>

namespace fsetup::archive {

inline constexpr char kMagic[8] = {'F', 'L', 'S', 'E', 'T', 'U', 'P', '\x1A'};
inline constexpr uint32_t kFormatVersion = 1;
inline constexpr uint32_t kFooterSize = 80;
inline constexpr uint64_t kMaxHeaderBytes = 64ull * 1024 * 1024;

enum FooterFlags : uint32_t {
    kFlagHeaderCompressed = 1u << 0,
};

#pragma pack(push, 1)
struct Footer {
    char magic[8];
    uint32_t version;
    uint32_t flags;
    uint64_t overlayStart;   // absolute file offset of the overlay
    uint64_t headerOffset;   // relative to overlayStart
    uint64_t headerSize;     // stored (possibly compressed) size
    uint64_t headerRawSize;  // decoded JSON size
    uint8_t headerSha256[32];
};
#pragma pack(pop)
static_assert(sizeof(Footer) == kFooterSize);

class Container {
public:
    // Opens `path` for reading with write/delete sharing denied and validates footer + header.
    // Throws Failure(PackageCorrupt) on any inconsistency.
    static std::unique_ptr<Container> Open(const std::wstring& path);

    [[nodiscard]] const std::string& HeaderJson() const noexcept { return headerJson_; }
    [[nodiscard]] const Footer& footer() const noexcept { return footer_; }
    [[nodiscard]] const std::wstring& FilePath() const noexcept { return path_; }
    [[nodiscard]] uint64_t OverlayStart() const noexcept { return footer_.overlayStart; }
    // SHA-256 of the stored header (from the footer): identifies this exact package build.
    [[nodiscard]] std::wstring HeaderDigestHex() const {
        static constexpr wchar_t kHex[] = L"0123456789abcdef";
        std::wstring out;
        for (const uint8_t b : footer_.headerSha256) out += kHex[b >> 4], out += kHex[b & 15];
        return out;
    }
    [[nodiscard]] uint64_t FileSize() const noexcept { return fileSize_; }
    [[nodiscard]] bool HasSignature() const noexcept { return signatureOffset_ != 0; }

    // True if [offset, offset+size) relative to the overlay lies entirely in the stream area (before the header).
    [[nodiscard]] bool StreamRangeValid(uint64_t offset, uint64_t size) const noexcept;

    // Positional read relative to the start of the file. Thread-safe. Throws on short read.
    void ReadAt(uint64_t absoluteOffset, void* buffer, size_t size) const;

    // Copies the PE image part ([0, overlayStart)) to `dest` with the security directory and checksum cleared, so the
    // copy is a valid, unsigned stub. Used to create the uninstaller.
    void CopyStubTo(HANDLE dest) const;

private:
    Container() = default;
    void LocateFooter();
    void LoadHeader();

    std::wstring path_;
    UniqueHandle file_;
    uint64_t fileSize_ = 0;
    uint64_t dataEnd_ = 0;          // end of the overlay+footer area (start of the certificate table or EOF)
    uint64_t imageEnd_ = 0;         // end of the PE sections
    uint64_t signatureOffset_ = 0;  // 0 = unsigned
    uint32_t securityDirFileOffset_ = 0;  // file offset of IMAGE_DATA_DIRECTORY[SECURITY]
    uint32_t checksumFileOffset_ = 0;
    Footer footer_{};
    std::string headerJson_;
};

// Streaming zstd decoder over one stream of the container. Verifies the decoded length and, once the stream has been
// fully consumed, the SHA-256 of the stored bytes.
class StreamReader {
public:
    StreamReader(const Container& container, uint64_t offset, uint64_t size, uint64_t rawSize, const Sha256Digest& sha256);
    ~StreamReader();
    StreamReader(const StreamReader&) = delete;
    StreamReader& operator=(const StreamReader&) = delete;

    // Decodes up to `capacity` bytes. Returns 0 only when the whole stream has been decoded and verified.
    size_t Read(uint8_t* out, size_t capacity);

    [[nodiscard]] uint64_t Consumed() const noexcept { return inputDone_; }

private:
    void Refill();

    const Container& container_;
    uint64_t base_;
    uint64_t size_;
    uint64_t rawSize_;
    Sha256Digest expected_;
    Sha256 hasher_;
    void* dstream_ = nullptr;  // ZSTD_DStream*
    std::unique_ptr<uint8_t[]> in_;
    size_t inCapacity_ = 0;
    size_t inPos_ = 0;
    size_t inSize_ = 0;
    uint64_t inputDone_ = 0;
    uint64_t produced_ = 0;
    size_t lastRet_ = 1;
    bool finished_ = false;
};

// Decodes a complete zstd buffer of known decoded size (used for the header). Throws on mismatch.
std::string DecodeAll(const uint8_t* data, size_t size, uint64_t rawSize);

enum class SignatureState {
    Unsigned,
    Valid,        // chain trusted
    Intact,       // hash matches but chain not trusted (self-signed / test certificate, expired)
    Tampered,     // signature present but the file hash does not match
    Distrusted,   // the certificate is revoked (as far as the local cache knows) or explicitly distrusted: refused
};
// WinVerifyTrust on the file, offline (no revocation network fetch).
SignatureState CheckSignature(const std::wstring& path, long* status = nullptr);

// Subject name (simple display name) of the certificate that signed the file, if the signature is valid and its chain
// trusted; "" otherwise.
std::wstring TrustedSignerName(const std::wstring& path);

// For the first page: whether the file is signed by a trusted publisher, and that publisher's name.
struct SignerInfo {
    SignatureState state = SignatureState::Unsigned;
    std::wstring publisher;  // only when state == Valid
};
SignerInfo GetSignerInfo(const std::wstring& path);

}  // namespace fsetup::archive
