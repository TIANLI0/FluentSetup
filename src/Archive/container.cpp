#include "Archive/container.h"

#include "Common/error.h"
#include "Common/path.h"
#include "Common/text.h"

#include <softpub.h>
#include <wintrust.h>
#include <zstd.h>

#include <algorithm>
#include <vector>

#pragma comment(lib, "wintrust.lib")

#pragma comment(lib, "crypt32.lib")

namespace fsetup::archive {

namespace {

[[noreturn]] void Corrupt(const std::wstring& why) { throw Failure(ErrorCode::PackageCorrupt, why); }

constexpr size_t kInputChunk = 1u << 20;

}  // namespace

std::unique_ptr<Container> Container::Open(const std::wstring& path) {
    std::unique_ptr<Container> c(new Container());
    c->path_ = path;
    // FILE_SHARE_READ only: nobody may modify the package while we validate and extract it.
    c->file_.reset(::CreateFileW(path::Long(path).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                 FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    if (!c->file_) {
        const DWORD e = ::GetLastError();
        throw Failure(e == ERROR_SHARING_VIOLATION ? ErrorCode::FileInUse : ErrorCode::IoError, L"Cannot open package " + path, e);
    }
    LARGE_INTEGER size{};
    if (!::GetFileSizeEx(c->file_.get(), &size)) ThrowWin32(L"GetFileSizeEx");
    c->fileSize_ = static_cast<uint64_t>(size.QuadPart);
    c->LocateFooter();
    c->LoadHeader();
    return c;
}

void Container::ReadAt(uint64_t offset, void* buffer, size_t size) const {
    auto* p = static_cast<uint8_t*>(buffer);
    while (size > 0) {
        OVERLAPPED ov{};
        ov.Offset = static_cast<DWORD>(offset);
        ov.OffsetHigh = static_cast<DWORD>(offset >> 32);
        DWORD read = 0;
        const DWORD chunk = static_cast<DWORD>(std::min<size_t>(size, 1u << 24));
        if (!::ReadFile(file_.get(), p, chunk, &read, &ov)) {
            const DWORD e = ::GetLastError();
            if (e != ERROR_HANDLE_EOF) ThrowWin32(L"Read failed: " + path_, e);
        }
        if (read == 0) Corrupt(L"Unexpected end of package file");
        p += read;
        offset += read;
        size -= read;
    }
}

void Container::LocateFooter() {
    // --- PE headers ---------------------------------------------------------------------------------------------
    IMAGE_DOS_HEADER dos{};
    if (fileSize_ < sizeof(dos) + kFooterSize) Corrupt(L"File too small");
    ReadAt(0, &dos, sizeof(dos));
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew <= 0 || static_cast<uint64_t>(dos.e_lfanew) > 4096) {
        Corrupt(L"Not a PE file");
    }
    const uint64_t ntOffset = static_cast<uint64_t>(dos.e_lfanew);
    uint8_t ntBuffer[sizeof(IMAGE_NT_HEADERS64)] = {};
    ReadAt(ntOffset, ntBuffer, sizeof(ntBuffer));
    DWORD signature = 0;
    memcpy(&signature, ntBuffer, sizeof(signature));
    if (signature != IMAGE_NT_SIGNATURE) Corrupt(L"Bad PE signature");
    IMAGE_FILE_HEADER fileHeader{};
    memcpy(&fileHeader, ntBuffer + 4, sizeof(fileHeader));
    const uint64_t optOffset = ntOffset + 4 + sizeof(IMAGE_FILE_HEADER);
    WORD magic = 0;
    memcpy(&magic, ntBuffer + 4 + sizeof(IMAGE_FILE_HEADER), sizeof(magic));
    uint64_t dirOffset = 0;
    if (magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        dirOffset = optOffset + offsetof(IMAGE_OPTIONAL_HEADER64, DataDirectory);
        checksumFileOffset_ = static_cast<uint32_t>(optOffset + offsetof(IMAGE_OPTIONAL_HEADER64, CheckSum));
    } else if (magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC) {
        dirOffset = optOffset + offsetof(IMAGE_OPTIONAL_HEADER32, DataDirectory);
        checksumFileOffset_ = static_cast<uint32_t>(optOffset + offsetof(IMAGE_OPTIONAL_HEADER32, CheckSum));
    } else {
        Corrupt(L"Unknown optional header");
    }
    securityDirFileOffset_ = static_cast<uint32_t>(dirOffset + IMAGE_DIRECTORY_ENTRY_SECURITY * sizeof(IMAGE_DATA_DIRECTORY));
    IMAGE_DATA_DIRECTORY security{};
    ReadAt(securityDirFileOffset_, &security, sizeof(security));

    // End of section raw data = where the original stub image ends.
    const uint64_t sectionsOffset = optOffset + fileHeader.SizeOfOptionalHeader;
    if (fileHeader.NumberOfSections == 0 || fileHeader.NumberOfSections > 96) Corrupt(L"Bad section count");
    std::vector<IMAGE_SECTION_HEADER> sections(fileHeader.NumberOfSections);
    ReadAt(sectionsOffset, sections.data(), sections.size() * sizeof(IMAGE_SECTION_HEADER));
    for (const auto& s : sections) {
        imageEnd_ = std::max<uint64_t>(imageEnd_, static_cast<uint64_t>(s.PointerToRawData) + s.SizeOfRawData);
    }

    dataEnd_ = fileSize_;
    if (security.VirtualAddress != 0 && security.Size != 0) {
        const uint64_t certStart = security.VirtualAddress;  // for the security directory this is a file offset
        if (certStart + security.Size > fileSize_ || certStart < imageEnd_) Corrupt(L"Bad certificate table");
        dataEnd_ = certStart;
        signatureOffset_ = certStart;
    }

    // --- footer (tolerate up to 15 bytes of alignment padding added by signing tools) --------------------------
    bool found = false;
    for (uint64_t pad = 0; pad < 16 && dataEnd_ >= kFooterSize + pad; ++pad) {
        Footer f{};
        ReadAt(dataEnd_ - pad - kFooterSize, &f, sizeof(f));
        if (memcmp(f.magic, kMagic, sizeof(kMagic)) == 0) {
            footer_ = f;
            dataEnd_ -= pad;
            found = true;
            break;
        }
    }
    if (!found) Corrupt(L"No FluentSetup package data found (missing footer)");
    if (footer_.version != kFormatVersion) Corrupt(L"Unsupported package format version " + std::to_wstring(footer_.version));
    const uint64_t footerStart = dataEnd_ - kFooterSize;
    if (footer_.overlayStart < imageEnd_ || footer_.overlayStart > footerStart || footer_.overlayStart % 8 != 0) {
        Corrupt(L"Bad overlay offset");
    }
    const uint64_t overlaySize = footerStart - footer_.overlayStart;
    if (footer_.headerSize == 0 || footer_.headerSize > kMaxHeaderBytes || footer_.headerRawSize == 0 ||
        footer_.headerRawSize > kMaxHeaderBytes || footer_.headerOffset > overlaySize ||
        footer_.headerSize > overlaySize - footer_.headerOffset) {
        Corrupt(L"Bad header bounds");
    }
    if ((footer_.flags & ~kFlagHeaderCompressed) != 0) Corrupt(L"Unknown footer flags");
}

void Container::LoadHeader() {
    std::vector<uint8_t> raw(static_cast<size_t>(footer_.headerSize));
    ReadAt(footer_.overlayStart + footer_.headerOffset, raw.data(), raw.size());
    const Sha256Digest digest = Sha256::Of(raw);
    if (memcmp(digest.data(), footer_.headerSha256, digest.size()) != 0) Corrupt(L"Header checksum mismatch");
    if (footer_.flags & kFlagHeaderCompressed) {
        headerJson_ = DecodeAll(raw.data(), raw.size(), footer_.headerRawSize);
    } else {
        if (raw.size() != footer_.headerRawSize) Corrupt(L"Header size mismatch");
        headerJson_.assign(reinterpret_cast<const char*>(raw.data()), raw.size());
    }
}

bool Container::StreamRangeValid(uint64_t offset, uint64_t size) const noexcept {
    // Streams live between the overlay start and the header.
    return offset <= footer_.headerOffset && size <= footer_.headerOffset - offset;
}

void Container::CopyStubTo(HANDLE dest) const {
    std::vector<uint8_t> buffer(static_cast<size_t>(std::min<uint64_t>(footer_.overlayStart, 64ull << 20)));
    uint64_t done = 0;
    bool first = true;
    while (done < footer_.overlayStart) {
        const size_t chunk = static_cast<size_t>(std::min<uint64_t>(buffer.size(), footer_.overlayStart - done));
        ReadAt(done, buffer.data(), chunk);
        if (first) {
            // Drop the Authenticode directory and the checksum: the copy is a different (unsigned) file.
            if (securityDirFileOffset_ + sizeof(IMAGE_DATA_DIRECTORY) <= chunk) {
                memset(buffer.data() + securityDirFileOffset_, 0, sizeof(IMAGE_DATA_DIRECTORY));
            }
            if (checksumFileOffset_ + sizeof(DWORD) <= chunk) memset(buffer.data() + checksumFileOffset_, 0, sizeof(DWORD));
            first = false;
        }
        DWORD written = 0;
        if (!::WriteFile(dest, buffer.data(), static_cast<DWORD>(chunk), &written, nullptr) || written != chunk) {
            ThrowWin32(L"Writing uninstaller stub failed");
        }
        done += chunk;
    }
}

// ---------------------------------------------------------------------------------------------------------------

std::string DecodeAll(const uint8_t* data, size_t size, uint64_t rawSize) {
    if (rawSize > kMaxHeaderBytes) Corrupt(L"Decoded block too large");
    const unsigned long long declared = ZSTD_getFrameContentSize(data, size);
    if (declared != ZSTD_CONTENTSIZE_UNKNOWN && declared != rawSize) Corrupt(L"Frame size mismatch");
    std::string out(static_cast<size_t>(rawSize), '\0');
    const size_t n = ZSTD_decompress(out.data(), out.size(), data, size);
    if (ZSTD_isError(n) || n != rawSize) Corrupt(L"Header decompression failed");
    return out;
}

StreamReader::StreamReader(const Container& container, uint64_t offset, uint64_t size, uint64_t rawSize,
                           const Sha256Digest& sha256)
    : container_(container), base_(container.OverlayStart() + offset), size_(size), rawSize_(rawSize), expected_(sha256) {
    if (!container.StreamRangeValid(offset, size)) Corrupt(L"Stream outside package bounds");
    ZSTD_DStream* ds = ZSTD_createDStream();
    if (!ds) throw Failure(ErrorCode::Internal, L"ZSTD_createDStream failed");
    dstream_ = ds;
    // Bound decoder memory: refuse frames that need a window larger than 128 MiB.
    ZSTD_DCtx_setParameter(ds, ZSTD_d_windowLogMax, 27);
    inCapacity_ = kInputChunk;
    in_ = std::make_unique<uint8_t[]>(inCapacity_);
}

StreamReader::~StreamReader() {
    if (dstream_) ZSTD_freeDStream(static_cast<ZSTD_DStream*>(dstream_));
}

void StreamReader::Refill() {
    const size_t chunk = static_cast<size_t>(std::min<uint64_t>(inCapacity_, size_ - inputDone_));
    container_.ReadAt(base_ + inputDone_, in_.get(), chunk);
    hasher_.Update(in_.get(), chunk);
    inputDone_ += chunk;
    inPos_ = 0;
    inSize_ = chunk;
}

size_t StreamReader::Read(uint8_t* out, size_t capacity) {
    if (finished_) return 0;
    auto* ds = static_cast<ZSTD_DStream*>(dstream_);
    ZSTD_outBuffer output{out, capacity, 0};
    while (output.pos == 0) {
        if (inPos_ == inSize_) {
            if (inputDone_ == size_) {
                // All input consumed: the last frame must be complete and the totals must match exactly.
                if (lastRet_ != 0) Corrupt(L"Truncated compressed stream");
                if (produced_ != rawSize_) Corrupt(L"Decoded size mismatch");
                const Sha256Digest digest = hasher_.Finish();
                if (digest != expected_) Corrupt(L"Stream checksum mismatch");
                finished_ = true;
                return 0;
            }
            Refill();
        }
        ZSTD_inBuffer input{in_.get(), inSize_, inPos_};
        const size_t ret = ZSTD_decompressStream(ds, &output, &input);
        if (ZSTD_isError(ret)) Corrupt(L"Decompression failed: " + Utf8ToWide(ZSTD_getErrorName(ret)));
        inPos_ = input.pos;
        lastRet_ = ret;
        if (produced_ + output.pos > rawSize_) Corrupt(L"Stream decodes to more data than declared");
        if (output.pos == 0 && input.pos == input.size && inputDone_ == size_ && ret != 0) {
            Corrupt(L"Truncated compressed stream");
        }
    }
    produced_ += output.pos;
    return output.pos;
}

// ---------------------------------------------------------------------------------------------------------------

SignatureState CheckSignature(const std::wstring& path, long* status) {
    WINTRUST_FILE_INFO file{};
    file.cbStruct = sizeof(file);
    file.pcwszFilePath = path.c_str();
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    WINTRUST_DATA data{};
    data.cbStruct = sizeof(data);
    data.dwUIChoice = WTD_UI_NONE;
    data.fdwRevocationChecks = WTD_REVOKE_NONE;
    data.dwUnionChoice = WTD_CHOICE_FILE;
    data.pFile = &file;
    data.dwStateAction = WTD_STATEACTION_VERIFY;
    data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
    const LONG result = ::WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data);
    data.dwStateAction = WTD_STATEACTION_CLOSE;
    ::WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data);
    if (status) *status = result;
    if (result == ERROR_SUCCESS) return SignatureState::Valid;
    if (result == TRUST_E_NOSIGNATURE || result == TRUST_E_SUBJECT_FORM_UNKNOWN || result == TRUST_E_PROVIDER_UNKNOWN) {
        return SignatureState::Unsigned;
    }
    if (result == TRUST_E_BAD_DIGEST || result == CRYPT_E_BAD_MSG) return SignatureState::Tampered;
    // Untrusted root, expired certificate, explicit distrust, ...: the bytes are intact but the publisher is not
    // trusted on this machine. Integrity is what the installer has to enforce; trust is the user's/SmartScreen's call.
    // A certificate the PC knows to be revoked, or one the user/administrator explicitly distrusted, is refused.
    if (result == CERT_E_REVOKED || result == TRUST_E_EXPLICIT_DISTRUST) return SignatureState::Distrusted;
    if (result == CERT_E_UNTRUSTEDROOT || result == CERT_E_CHAINING || result == CERT_E_EXPIRED || result == CERT_E_UNTRUSTEDTESTROOT ||
        result == TRUST_E_CERT_SIGNATURE || result == CRYPT_E_REVOCATION_OFFLINE) {
        return SignatureState::Intact;
    }
    return SignatureState::Tampered;
}

std::wstring TrustedSignerName(const std::wstring& path) {
    WINTRUST_FILE_INFO info{};
    info.cbStruct = sizeof(info);
    info.pcwszFilePath = path.c_str();
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    WINTRUST_DATA data{};
    data.cbStruct = sizeof(data);
    data.dwUIChoice = WTD_UI_NONE;
    data.fdwRevocationChecks = WTD_REVOKE_NONE;
    data.dwUnionChoice = WTD_CHOICE_FILE;
    data.pFile = &info;
    data.dwStateAction = WTD_STATEACTION_VERIFY;
    data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
    std::wstring name;
    if (::WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data) == ERROR_SUCCESS) {
        if (CRYPT_PROVIDER_DATA* provider = ::WTHelperProvDataFromStateData(data.hWVTStateData)) {
            CRYPT_PROVIDER_SGNR* signer = ::WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0);
            if (signer && signer->csCertChain > 0 && signer->pasCertChain && signer->pasCertChain[0].pCert) {
                wchar_t buffer[512] = {};
                if (::CertGetNameStringW(signer->pasCertChain[0].pCert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, buffer, ARRAYSIZE(buffer)) > 1) {
                    name = buffer;
                }
            }
        }
    }
    data.dwStateAction = WTD_STATEACTION_CLOSE;
    ::WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &action, &data);
    return name;
}

SignerInfo GetSignerInfo(const std::wstring& path) {
    SignerInfo info;
    info.state = CheckSignature(path);
    if (info.state == SignatureState::Valid) {
        info.publisher = TrustedSignerName(path);
        if (info.publisher.empty()) info.state = SignatureState::Intact;
    }
    return info;
}

}  // namespace fsetup::archive
