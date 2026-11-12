// SHA-256 through the system CNG provider (bcrypt.dll). No bundled crypto code.
#pragma once

#include "Common/win.h"

#include <array>
#include <span>
#include <string>

namespace fsetup {

using Sha256Digest = std::array<uint8_t, 32>;

class Sha256 {
public:
    Sha256();
    ~Sha256();
    Sha256(const Sha256&) = delete;
    Sha256& operator=(const Sha256&) = delete;

    void Update(std::span<const uint8_t> data);
    void Update(const void* data, size_t size) { Update({static_cast<const uint8_t*>(data), size}); }
    Sha256Digest Finish();

    static Sha256Digest Of(std::span<const uint8_t> data);

private:
    void* alg_ = nullptr;   // BCRYPT_ALG_HANDLE
    void* hash_ = nullptr;  // BCRYPT_HASH_HANDLE
};

// Fills the buffer with cryptographically secure random bytes. Throws Failure on provider failure.
void RandomBytes(std::span<uint8_t> out);
// 32 hex characters of secure randomness, for pipe names and temp directory names.
std::wstring RandomToken();

}  // namespace fsetup
