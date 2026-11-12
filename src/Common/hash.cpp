#include "Common/hash.h"

#include "Common/error.h"
#include "Common/text.h"

#include <bcrypt.h>

#ifndef NT_SUCCESS
#define NT_SUCCESS(status) (((NTSTATUS)(status)) >= 0)
#endif

namespace fsetup {

Sha256::Sha256() {
    BCRYPT_ALG_HANDLE alg = nullptr;
    NTSTATUS st = ::BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
    if (!NT_SUCCESS(st)) throw Failure(ErrorCode::Internal, L"BCryptOpenAlgorithmProvider(SHA256) failed", static_cast<DWORD>(st));
    BCRYPT_HASH_HANDLE hash = nullptr;
    st = ::BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0);
    if (!NT_SUCCESS(st)) {
        ::BCryptCloseAlgorithmProvider(alg, 0);
        throw Failure(ErrorCode::Internal, L"BCryptCreateHash failed", static_cast<DWORD>(st));
    }
    alg_ = alg;
    hash_ = hash;
}

Sha256::~Sha256() {
    if (hash_) ::BCryptDestroyHash(hash_);
    if (alg_) ::BCryptCloseAlgorithmProvider(alg_, 0);
}

void Sha256::Update(std::span<const uint8_t> data) {
    size_t offset = 0;
    while (offset < data.size()) {
        const ULONG chunk = static_cast<ULONG>(std::min<size_t>(data.size() - offset, 1u << 30));
        const NTSTATUS st = ::BCryptHashData(hash_, const_cast<PUCHAR>(data.data() + offset), chunk, 0);
        if (!NT_SUCCESS(st)) throw Failure(ErrorCode::Internal, L"BCryptHashData failed", static_cast<DWORD>(st));
        offset += chunk;
    }
}

Sha256Digest Sha256::Finish() {
    Sha256Digest digest{};
    const NTSTATUS st = ::BCryptFinishHash(hash_, digest.data(), static_cast<ULONG>(digest.size()), 0);
    if (!NT_SUCCESS(st)) throw Failure(ErrorCode::Internal, L"BCryptFinishHash failed", static_cast<DWORD>(st));
    return digest;
}

Sha256Digest Sha256::Of(std::span<const uint8_t> data) {
    Sha256 h;
    h.Update(data);
    return h.Finish();
}

void RandomBytes(std::span<uint8_t> out) {
    const NTSTATUS st =
        ::BCryptGenRandom(nullptr, out.data(), static_cast<ULONG>(out.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (!NT_SUCCESS(st)) throw Failure(ErrorCode::Internal, L"BCryptGenRandom failed", static_cast<DWORD>(st));
}

std::wstring RandomToken() {
    uint8_t bytes[16];
    RandomBytes(bytes);
    return Utf8ToWide(ToHex(bytes));
}

}  // namespace fsetup
