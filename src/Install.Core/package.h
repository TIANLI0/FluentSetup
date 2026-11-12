// In-memory model of a package header (manifest), produced by FluentSetup.CLI and validated here before use.
// Field names match shared/schemas/package-header.schema.json.
#pragma once

#include "Common/hash.h"
#include "Common/json.h"
#include "Install.Core/extensions.h"

#include <functional>
#include <string>
#include <vector>

namespace fsetup::install {

enum class Scope { User, Machine };
inline const wchar_t* ScopeName(Scope s) noexcept { return s == Scope::Machine ? L"machine" : L"user"; }

enum class ScopeMode { UserOnly, MachineOnly, UserOrMachine };

// How an optional feature is offered: never / forced / user can choose (with a default).
enum class Offer { Off, On, OptionalOff, OptionalOn };
inline bool OfferVisible(Offer o) noexcept { return o == Offer::OptionalOff || o == Offer::OptionalOn; }
inline bool OfferDefault(Offer o) noexcept { return o == Offer::On || o == Offer::OptionalOn; }

struct StreamInfo {
    uint64_t offset = 0;  // relative to the overlay start
    uint64_t size = 0;    // stored bytes
    uint64_t rawSize = 0; // decoded bytes
    Sha256Digest sha256{};
};

struct FileInfo {
    std::wstring path;  // validated relative path
    uint64_t size = 0;
    Sha256Digest sha256{};
    uint32_t stream = 0;
    uint64_t offset = 0;  // offset inside the decoded stream
    uint32_t attributes = 0;  // FILE_ATTRIBUTE_READONLY | HIDDEN only
    bool preserve = false;    // user data: never overwritten if present, never removed
    std::string variant;      // empty = every variant; else only installed when this variant is chosen
};

// Alternative builds of the app in one package (e.g. framework-dependent vs self-contained). Files without a variant
// are shared; the user (or /variant) picks one variant at install time.
struct Variant {
    std::string id;
    LocalizedText label;
    LocalizedText description;
    bool isDefault = false;
    uint64_t bytes = 0;  // shared + variant-specific payload bytes
};

struct FileSet {
    std::vector<StreamInfo> streams;
    std::vector<FileInfo> files;
    std::vector<std::wstring> directories;  // extra (possibly empty) directories to create
    uint64_t totalRaw = 0;
};

struct Package {
    std::wstring kind;  // "setup" or "uninstaller"

    // app
    std::wstring appId, name, publisher, version, description, website, mainExecutable, architecture;
    uint32_t minWindowsBuild = 0;

    // install
    ScopeMode scopeMode = ScopeMode::UserOnly;
    Scope defaultScope = Scope::User;
    std::wstring userDirTemplate, machineDirTemplate;
    bool allowCustomDir = true;
    Offer desktopShortcut = Offer::OptionalOff;
    Offer startMenuShortcut = Offer::On;
    Offer launchAfterInstall = Offer::OptionalOn;
    bool allowDowngrade = false;

    // ui (kept as JSON; only interpreted by the front ends)
    json::Value ui;

    FileSet payload;
    std::vector<Variant> variants;  // empty = a single build
    FileSet frontend;
    std::wstring frontendEntry;
    std::wstring iconFile;  // inside the frontend set

    Extensions ext;   // close processes, requirements, options, actions, uninstall cleanup
    json::Value raw;  // the full header, used to derive the uninstaller header

    [[nodiscard]] bool IsUninstaller() const noexcept { return kind == L"uninstaller"; }
    [[nodiscard]] const Variant* FindVariant(const std::string& id) const noexcept {
        for (const auto& v : variants) {
            if (v.id == id) return &v;
        }
        return nullptr;
    }
    [[nodiscard]] std::string DefaultVariant() const {
        for (const auto& v : variants) {
            if (v.isDefault) return v.id;
        }
        return variants.empty() ? std::string() : variants.front().id;
    }
    [[nodiscard]] bool HasFrontend() const noexcept { return !frontendEntry.empty() && !frontend.files.empty(); }
};

// Parses and validates a header. `rangeCheck(offset,size)` must return true for in-bounds stream ranges.
// Throws Failure(PackageCorrupt) with a precise diagnostic on any violation.
Package ParsePackage(const std::string& headerJson, const std::function<bool(uint64_t, uint64_t)>& rangeCheck);

// Expands {LocalAppData}, {ProgramFiles}, {AppName}, ... in an install directory template.
std::wstring ExpandDirTemplate(const std::wstring& tpl, const Package& pkg);

// Names the installer reserves inside the install directory; packages may not contain them.
inline constexpr const wchar_t* kUninstallerName = L"uninstall.exe";
inline constexpr const wchar_t* kRecordName = L"uninstall.json";
inline constexpr const wchar_t* kWorkDirName = L".fsetup";

}  // namespace fsetup::install
