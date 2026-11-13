# Setup.exe container format (v1)

This page documents the binary layout of a FluentSetup `Setup.exe` (and of the `uninstall.exe` it writes into the
install directory), the JSON header it carries, and the checks the runtime applies before trusting any of it.

Source of truth (keep this page in sync with them):

| Concern | Native (C++) | Builder (C#) |
|---|---|---|
| Footer, PE parsing, stream decoding, signature check | `src/Archive/container.h`, `container.cpp` | `src/Builder.Core/Packaging/PackageFormat.cs` |
| Writing the container and the header JSON | – | `PackageBuilder.cs` (`Build`, `CompressFiles`, `HeaderWriter`) |
| Reading back / verification | `container.cpp` | `PackageInspector.cs` (`fluentsetup inspect`) |
| Header parsing and validation | `src/Install.Core/package.cpp`, `extensions.cpp`, `src/Common/json.cpp` | – |
| Uninstaller generation | `src/Install.Core/engine.cpp` (`Engine::WriteUninstaller`) | – |
| Authenticode signing | – | `Signer.cs` |

All integers are little-endian. "Overlay" means the bytes appended after the PE image of the stub.

## 1. Byte layout

```
offset 0
┌──────────────────────────────────────────────────────────────┐
│ PE image of the stub (headers + sections, incl. resources)   │  icon / version info patched by the builder
├──────────────────────────────────────────────────────────────┤  ← overlayStart (absolute, multiple of 8)
│ stream 0          (zstd)   + zero padding to 8 bytes         │
│ stream 1          (zstd)   + zero padding to 8 bytes         │
│ ...                                                          │
├──────────────────────────────────────────────────────────────┤  ← overlayStart + headerOffset
│ header            (zstd-compressed UTF-8 JSON, or raw JSON)  │
│ zero padding to 8 bytes                                      │
├──────────────────────────────────────────────────────────────┤
│ footer, 80 bytes, magic "FLSETUP\x1A"                        │
├──────────────────────────────────────────────────────────────┤  ← end of file, or PE security directory offset
│ Authenticode certificate table (optional, added by signtool) │
└──────────────────────────────────────────────────────────────┘
```

* The builder pads the stub to a multiple of 8 before `overlayStart`, pads after every stream and after the header,
  so every piece (and the footer) starts 8-byte aligned. Padding bytes are zero and are not covered by any hash.
* Streams occupy `[overlayStart, overlayStart + headerOffset)`. Nothing after the header except padding and the footer
  belongs to the container.
* The overlay itself carries no type tags. Which stream belongs to which file set is defined only by the header
  (section 4): the builder writes the WinUI front-end stream first (if the package includes it), then the payload
  streams in order.

### Worked example

`out\uitest\FluentSetup Demo-1.0.0-Setup.exe` (1,013,752 bytes, unsigned):

| Piece | Absolute offset | Overlay-relative offset | Size |
|---|---|---|---|
| Stub PE image (+ alignment) | 0 | – | 649,728 |
| `frontend` stream 0 | 649,728 | 0 | 339,697 (+7 padding) |
| `payload` stream 0 | 989,432 | 339,704 | 14,718 (+2 padding) |
| Header (zstd) | 1,004,152 | 354,424 | 9,517 stored / 31,745 raw (+3 padding) |
| Footer | 1,013,672 | 363,944 | 80 |

## 2. Footer

Defined as `fsetup::archive::Footer` (`#pragma pack(1)`, `static_assert(sizeof(Footer) == 80)`) and mirrored by
`PackageFormat.EncodeFooter` / `DecodeFooter`.

| Offset | Size | Type | Field | Meaning |
|---:|---:|---|---|---|
| 0 | 8 | `char[8]` | `magic` | `46 4C 53 45 54 55 50 1A` = `"FLSETUP\x1A"` |
| 8 | 4 | `uint32` | `version` | Container format version. Must be `1`. |
| 12 | 4 | `uint32` | `flags` | Bit 0 `kFlagHeaderCompressed`: header is a zstd frame. All other bits must be 0. |
| 16 | 8 | `uint64` | `overlayStart` | Absolute file offset of the overlay. |
| 24 | 8 | `uint64` | `headerOffset` | Header offset, relative to `overlayStart`. Also the end of the stream area. |
| 32 | 8 | `uint64` | `headerSize` | Stored header size in bytes (compressed size if bit 0 is set). |
| 40 | 8 | `uint64` | `headerRawSize` | Size of the decoded header JSON. |
| 48 | 32 | `uint8[32]` | `headerSha256` | SHA-256 of the **stored** header bytes (`headerSize` bytes at `overlayStart + headerOffset`). |

The builder always writes `version = 1` and `flags = 1`. The uninstaller writer (section 7) writes `flags = 0`.

### 2.1 Locating the footer

`Container::LocateFooter` (native) and `PackageInspector.Inspect` (builder) do the following:

1. Parse the PE headers: `MZ` signature, `e_lfanew` in `(0, 4096]`, `PE\0\0` signature, PE32 or PE32+ optional
   header, 1–96 section headers. `imageEnd` = the maximum of `PointerToRawData + SizeOfRawData` over all sections.
2. Read `IMAGE_DIRECTORY_ENTRY_SECURITY`. If both its `VirtualAddress` and `Size` are non-zero the file is signed; for
   this directory `VirtualAddress` is a **file offset**. The certificate table must lie inside the file and must not
   start before `imageEnd`. The container data then ends at that offset (`dataEnd`); otherwise `dataEnd` is the end of
   the file.
3. Look for the magic at `dataEnd - pad - 80` for `pad = 0..15`. This tolerates up to 15 bytes of alignment padding
   that signing tools may insert between the footer and the certificate table.

Then (native reader) the footer is rejected with `PackageCorrupt` unless all of these hold:

* `version == 1`;
* `imageEnd <= overlayStart <= footerStart` and `overlayStart % 8 == 0`;
* `1 <= headerSize <= 64 MiB` and `1 <= headerRawSize <= 64 MiB` (`kMaxHeaderBytes`);
* `headerOffset <= overlaySize` and `headerSize <= overlaySize - headerOffset`, where
  `overlaySize = footerStart - overlayStart`;
* no flag bits other than bit 0.

`PackageInspector` applies a subset of these checks (see "Known differences" in section 9).

## 3. Streams and compression

A stream is a zstd-compressed concatenation of the bytes of one or more files. The header lists, per stream, its
overlay-relative `offset`, stored `size`, decoded `rawSize` and the SHA-256 of the stored bytes; per file, the stream
index and the file's offset inside the **decoded** stream.

### 3.1 How the builder forms streams (`PackageBuilder.Build`)

* Payload files are sorted by extension, then by relative path (ordinal, case-insensitive), so similar files compress
  together.
* A file of 16 MiB or more (`OwnStreamBytes`) gets a stream of its own.
* Smaller files are accumulated into a solid block; a block is closed as soon as its raw size reaches 32 MiB
  (`SolidBlockBytes`). The last partial block becomes the final stream.
* Blocks are compressed in parallel; each stream is a single zstd frame with the pledged source size set.
* The WinUI front end (the `*.exe`, `*.dll`, `*.pri` and `*.xbf` files at the top level of `runtime\<arch>\ui`, plus
  `app.ico` if an icon is available) is one stream, written before the payload streams.

### 3.2 zstd parameters

| Data | Level | Other parameters |
|---|---:|---|
| Payload, `output.compression = "fast"` | 3 | library defaults for the level |
| Payload, `"normal"` (default) | 12 | library defaults for the level |
| Payload, `"max"` | 19 | `windowLog = 27`, long-distance matching enabled |
| WinUI front-end stream | 19 | `windowLog = 27`, long-distance matching enabled |
| Header | 19 | one-shot `Compressor(19).Wrap` |
| Any payload stream that holds a single file ≥ 16 MiB | as above | `nbWorkers = min(4, processor count)` |

The native decoder for streams (`StreamReader`) is a streaming `ZSTD_DStream` with `ZSTD_d_windowLogMax = 27`: frames
that need a window larger than 128 MiB are refused (`PackageCorrupt`). The builder never sets a window log above 27
for exactly this reason. The header is decoded in one shot (`DecodeAll`) into a buffer of exactly `headerRawSize`
bytes (at most 64 MiB); if the frame declares a content size it must equal `headerRawSize`.

### 3.3 Reading a stream (`StreamReader`)

* The range `[offset, offset + size)` must lie entirely in the stream area (`offset <= headerOffset` and
  `size <= headerOffset - offset`), otherwise `PackageCorrupt`.
* Input is read in 1 MiB chunks and fed both to the SHA-256 and to the decoder.
* Decoding more bytes than `rawSize` is an error. When all `size` input bytes are consumed, the last frame must be
  complete, the decoded total must equal `rawSize` exactly, and the SHA-256 of the stored bytes must match. Only then
  does `Read` return 0.
* Consumers (payload installation, WinUI bundle extraction) read one extra byte after the last file of each stream to
  force this final verification; a non-zero result is reported as "Stream carries unexpected data" / "UI stream has
  trailing data".

## 4. Header JSON

UTF-8 JSON, written by `HeaderWriter.Write` with `Utf8JsonWriter` (relaxed escaping) and compressed at level 19. Paths
inside the header use `\` as separator. `fluentsetup inspect <Setup.exe> --json` prints the decoded header under the
`header` key; `--verify` additionally checks every stream hash.

### 4.1 Example (trimmed)

Generated from the demo package with
`src\CLI\bin\Release\net10.0-windows10.0.19041.0\fluentsetup.exe inspect "out\uitest\FluentSetup Demo-1.0.0-Setup.exe" --json`;
long arrays and the license text are shortened.

```json
{
  "format": 1,
  "kind": "setup",
  "app": {
    "id": "FluentSetup.DemoApp",
    "name": "FluentSetup Demo",
    "publisher": "FluentSetup Samples",
    "version": "1.0.0",
    "description": "Sample app used to test FluentSetup end to end.",
    "website": "https://example.com/fluentsetup-demo",
    "mainExecutable": "DemoApp.exe",
    "architecture": "x64",
    "minWindowsBuild": 17763
  },
  "install": {
    "scope": "userOrMachine",
    "defaultScope": "user",
    "userDir": "{LocalAppData}\\Programs\\{AppName}",
    "machineDir": "{ProgramFiles}\\{AppName}",
    "allowCustomDir": true,
    "desktopShortcut": "optionalOff",
    "startMenuShortcut": "on",
    "launchAfterInstall": "optionalOn",
    "allowDowngrade": false,
    "closeProcesses": []
  },
  "requirements": [],
  "options": [],
  "actions": [],
  "uninstall": { "removeData": [], "removeScheduledTasks": [], "removeRunValues": [] },
  "ui": {
    "title": "", "welcomeText": "", "finishText": "",
    "theme": "system", "backdrop": "mica", "useSystemAccent": true, "accentColor": "",
    "license": "FluentSetup Demo - Sample License Agreement\n\n...",
    "licenses": {},
    "defaultLanguage": "auto",
    "strings": {}
  },
  "payload": {
    "streams": [
      { "offset": 339704, "size": 14718, "rawSize": 38301,
        "sha256": "a8777a3eabd87f19551c3ae00f0d17981e0988f6edebe925cc2b2291760372cc" }
    ],
    "files": [
      { "path": "data\\legacy.dat", "size": 8,
        "sha256": "822d4e98cd0a439fc1f99843fdea9ce2e2ad79a24c2c7eed0f6efcd0e9ad0f2a", "stream": 0, "offset": 0 },
      { "path": "DemoApp.exe", "size": 36352,
        "sha256": "e95c81bf9af94d73deb52a10812f3d82e39bc4eab3b0b6a3e34848ac8f89c1d5", "stream": 0, "offset": 8 }
    ],
    "directories": [ "logs" ]
  },
  "frontend": {
    "streams": [
      { "offset": 0, "size": 339697, "rawSize": 1064328,
        "sha256": "ee0e50c30e29213babe10da25f3986ea26208db132a1873b2e2d6d3bddb1cb60" }
    ],
    "files": [
      { "path": "App.xbf", "size": 852, "sha256": "04dedb41…", "stream": 0, "offset": 0 },
      { "path": "FluentSetup.UI.exe", "size": 643584, "sha256": "d7ef4f3f…", "stream": 0, "offset": 852 }
    ],
    "entry": "FluentSetup.UI.exe",
    "icon": "app.ico"
  }
}
```

(The two front-end hashes are abbreviated here; real headers always carry 64 hex digits.)

### 4.2 Top level

| Field | Type | Rules (native parser) |
|---|---|---|
| `format` | number | Must be `1`. |
| `kind` | string | `"setup"` (written by the builder) or `"uninstaller"` (written by the engine, section 7). |
| `app` | object | Section 4.3. |
| `install` | object | Section 4.4. |
| `requirements`, `options`, `actions`, `uninstall` | array / object | Section 4.5 (declarative extensions). |
| `ui` | object | Must be an object if present. Passed through to the front ends; not interpreted by the engine. |
| `payload` | object | File set (section 4.6). Must be empty for `kind = "uninstaller"`. |
| `frontend` | object, optional | File set plus `entry` and optional `icon` (section 4.7). |

Unknown keys are ignored: the parser only looks up the keys it knows, and accessors are lenient (a value of the wrong
type reads as its default) except where a rule below says otherwise.

### 4.3 `app`

| Field | Required | Rules |
|---|---|---|
| `id` | yes | ≤ 64 chars, `[A-Za-z0-9._-]`, must not start or end with `.`. Used for the ARP key, the pending marker and the setup mutex. |
| `name` | yes | ≤ 128 chars. |
| `publisher` | no | ≤ 128 chars. |
| `version` | yes | ≤ 64 chars; must parse as a version: up to four numeric parts plus optional pre-release tag (`1.2.3`, `1.2.3.4`, `2.0.0-beta.2`). |
| `description` | no | ≤ 1024 chars. |
| `website` | no | ≤ 512 chars; if non-empty must start with `http://` or `https://`. |
| `architecture` | no | ≤ 16 chars. Informational for the runtime. |
| `minWindowsBuild` | no | Unsigned number; Setup.exe exits with `UnsupportedSystem` (5) if the OS build is lower. |
| `mainExecutable` | no | Relative path, must end in `.exe`; for `kind = "setup"` it must be one of the payload files. If empty, desktop/Start-menu shortcuts and "launch after install" are forced off. |

Every string checked by `RequireString` must be a string (if present) and must not contain control characters other
than CR, LF and TAB.

### 4.4 `install`

| Field | Default if absent | Values |
|---|---|---|
| `scope` | `user` | `user`, `machine`, `userOrMachine` (empty string = `user`); anything else is rejected. |
| `defaultScope` | `user` | Only used for `userOrMachine`: `machine` or anything else (= user). |
| `userDir` | `{LocalAppData}\Programs\{AppName}` | Directory template. |
| `machineDir` | `{ProgramFiles}\{AppName}` | Directory template. |
| `allowCustomDir` | `true` | |
| `desktopShortcut` | `optionalOff` | Offer: `off`, `on`, `optionalOff`, `optionalOn`. Other strings are rejected. |
| `startMenuShortcut` | `on` | Offer. |
| `launchAfterInstall` | `optionalOn` | Offer. |
| `allowDowngrade` | `false` | |
| `closeProcesses` | `[]` | ≤ 32 file names ending in `.exe`. |
| `adoptUninstallKey` | absent | If present must equal `app.id` (case-insensitive). Written by the builder only when `install.adoptExistingInstall` is set. |

Directory templates expand `{LocalAppData}`, `{UserProgramFiles}`, `{AppData}`, `{ProgramFiles}`, `{ProgramData}`,
`{AppName}` (sanitized name), `{Publisher}` (sanitized publisher, or the name if empty) and `{AppId}`.

### 4.5 Declarative extensions (`extensions.cpp`)

| Field | Rules |
|---|---|
| `requirements[]` | `type`: `netfx` (needs `minRelease > 0`) or `webview2` (optional `installer`: a payload file, except in an uninstaller header, optional `args`). `name` is truncated to 128 chars. Unknown types are rejected. |
| `options[]` | ≤ 16 entries; `id` 1–32 chars `[A-Za-z0-9_-]`, unique; `label` (string or `{lang: text}`, 256 chars max per text); `default` bool. |
| `actions[]` | ≤ 32 entries; `when`: `afterInstall` or `beforeUninstall`; `run`: relative path of an `.exe` that is part of the payload (in an uninstaller header, which has no payload, any safe relative path; it is checked at run time to be an existing file inside the install directory); `args`: ≤ 32 strings of ≤ 1024 bytes; `timeoutSeconds` clamped to 1–3600 (default 300); `successExitCodes` (array of numbers); `onFailure`: `warn`, `ignore`, anything else = fail; optional `ifOption` (must name an option); optional `skipIfInstalled {hive: HKLM|HKCU, key ≤ 255 chars not starting with "\", value ≤ 128 chars, version}`; `name` (localized text). |
| `uninstall.removeData[]` | Each entry must expand to `{CommonAppData}`, `{LocalAppData}` or `{AppData}` plus a sub-folder. |
| `uninstall.removeScheduledTasks[]`, `uninstall.removeRunValues[]` | 1–128 chars, no control characters and none of `\ / : * ? " < > |`. |

### 4.6 File sets (`payload`, `frontend`)

| Field | Rules |
|---|---|
| `streams[]` | ≤ 1,000,000 entries. Each: `offset` ≥ 0, `size` > 0, `rawSize` ≥ 0 (all numbers), `sha256` = 64 hex digits (SHA-256 of the stored bytes). Streams must be in ascending offset order and must not overlap, and must lie in the stream area. |
| `files[]` | ≤ 2,000,000 entries. Each: `path` (safe relative path, see below), `size`, `sha256` (of the decoded file), `stream` (index), `offset` (inside the decoded stream), optional `attributes` (masked to `READONLY \| HIDDEN`), optional `preserve` (user data: never overwritten if present, never removed). |
| `directories[]` | Extra directories to create (may be empty). Same path rules; must not collide with a file. |

Consistency rules:

* Files are ordered by stream index (non-decreasing); inside a stream, files are contiguous: each `offset` equals the
  sum of the sizes of the earlier files in the same stream, and the sum of all file sizes in a stream equals its
  `rawSize` exactly.
* Paths are unique case-insensitively, and no file path may also be a parent directory of another file.
* The total decoded size of a set is limited to 2^44 bytes (16 TiB).
* Relative paths (`path::ValidateRelative`): components separated by `\` (`/` is normalized), each a valid Windows file
  name; rejected are empty, absolute, drive-relative and UNC paths, `.`/`..`, `:`, control characters, `<>"|?*`,
  trailing dots/spaces, DOS device names (CON, NUL, COM1, …) and paths longer than 1024 characters.
* Payload only: the reserved names `uninstall.exe` and `uninstall.json` (top level) and the top-level directory
  `.fsetup` may not be used.

### 4.7 `frontend`

In addition to the file-set fields: `entry` (relative path of the executable to start, the builder writes
`FluentSetup.UI.exe`) and optional `icon` (relative path inside the set, the builder writes `app.ico` when an icon was
included). Front-end streams must not overlap any payload stream. The bootstrapper only uses the WinUI front end if
`entry` is non-empty and the set has at least one file.

### 4.7a 1.2 additions

All optional; an older stub never reads a 1.2 header (every Setup.exe reads only its own), and `format` stays 1.

| Key | Content |
|---|---|
| `app.appUserModelId`, `app.toastActivatorClsid` | Set on the app's own shortcuts. |
| `install.appPaths`, `install.restartManager` (`auto`\|`off`), `install.lockedFiles` (`fail`\|`reboot`) | |
| `shortcuts`, `associations`, `protocols`, `registry`, `environment`, `services`, `firewall`, `inputs`, `configFiles` | The project sections with defaults resolved by the builder (targets, Start menu folders, `args`), paths with `\`. Parsed and validated again by `ParseExtensions`. |
| `requirements[]` | New `type`s `vcredist` (`architecture`, `minVersion`) and `custom` (`id`, `detect.registry` or `detect.file`); any requirement may have `bundled` (a file of the `redist` set) and `download.signer` (then `download.sha256` is optional). |
| `ui.banner` | File name in the `frontend` set (`banner.png` …). A `frontend` set without `entry` carries only the icon and banner (no WinUI front end). |
| `redist` | File set (like `frontend`) of bundled official prerequisite installers, one stream each; never installed, extracted into a private temporary folder only when the prerequisite is missing. Not copied into the uninstaller. |
| `uninstallerImage` | `{header: base64, certificates: base64, checksum, sha256}`: the exact header of the signed `uninstall.exe`, its Authenticode certificate table and PE checksum, and the SHA-256 of the finished file (section 7). Not copied into the uninstaller. |
| `testHooks` | `true` only in packages built with `fluentsetup build --test-hooks` (FluentSetup's own tests): enables `FLUENTSETUP_TEST_FAULT_AFTER` and `--ui-simulate`. |

### 4.8 JSON parser limits

The header is parsed by the project's own JSON reader (`src/Common/json.cpp`) with:

* maximum nesting depth 16 (header-specific; the reader's default is 64);
* maximum size: bounded by the footer (`headerRawSize ≤ 64 MiB`);
* strict JSON: no comments, no trailing commas, no trailing data; a leading UTF-8 BOM is tolerated; control characters
  inside strings, invalid escapes and unpaired surrogates are errors;
* integers that fit in `int64` are kept exactly; other numbers become `double` (non-finite values are rejected);
* duplicate keys are accepted and the **first** occurrence wins.

Any violation of the rules in section 4 throws `Failure(PackageCorrupt, "Invalid package header: …")`.

## 5. Integrity checks

| What | Hash covers | When it is verified | On failure |
|---|---|---|---|
| Header | Stored header bytes (`headerSha256` in the footer) | `Container::Open`, before decompression or parsing, at every start of Setup.exe/uninstall.exe | Exit code 4 (`PackageCorrupt`) |
| Stream | Stored (compressed) bytes, `streams[].sha256` | Incrementally while decoding; checked when the stream has been fully consumed, together with the exact decoded size | `PackageCorrupt`; during installation the operation is rolled back |
| Payload file | Decoded file content, `files[].sha256` | Right after each file has been written (`Engine::InstallPayload`) | `PackageCorrupt`, rollback |
| Front-end file | Decoded file content | After extraction the file is re-opened read-only with write/delete sharing denied, read back from disk and its size and hash are checked; the handle stays open while the UI runs | WinUI is not used (fallback to Win32) |
| Build output | Header hash and every stream hash | `PackageBuilder` runs `PackageInspector.Inspect(verifyStreams: true)` after writing (and after signing) | Build fails, the partial file is deleted |

Notes:

* Payload data is decoded straight to its final location; corruption is detected per file (file hash) and per stream
  (stream hash at the end). Every change is journaled, so a detected corruption rolls the installation back.
* Setup.exe opens its own file with `FILE_SHARE_READ` only, so the package cannot be modified while it runs.
* When copying front-end streams into `uninstall.exe`, the stored bytes are copied without re-hashing; the hashes in the
  header are carried over and are verified whenever the uninstaller extracts its UI.

## 6. Authenticode and tamper detection

Signing (`Signer.Sign`) runs `signtool sign /fd SHA256 /d <app name>` with either `/sha1 <thumbprint>` or
`/f <file> [/p <password from an environment variable>]`, plus `/tr <url> /td SHA256` if a timestamp URL is configured.
It happens after the footer has been written, so the certificate table ends up after the footer and the PE security
directory points at it; this is why readers locate the footer through the security directory (section 2.1).

At start-up, after `Container::Open` succeeded, Setup.exe calls `CheckSignature` → `WinVerifyTrust` with
`WINTRUST_ACTION_GENERIC_VERIFY_V2`, no UI, `WTD_REVOKE_NONE` and `WTD_CACHE_ONLY_URL_RETRIEVAL` (no network access):

| `WinVerifyTrust` result | `SignatureState` (log value) | Effect |
|---|---|---|
| `ERROR_SUCCESS` | `Valid` (1) | Continue. |
| `TRUST_E_NOSIGNATURE`, `TRUST_E_SUBJECT_FORM_UNKNOWN`, `TRUST_E_PROVIDER_UNKNOWN` | `Unsigned` (0) | Continue; integrity rests on the SHA-256 checks. |
| `CERT_E_UNTRUSTEDROOT`, `CERT_E_CHAINING`, `CERT_E_EXPIRED`, `TRUST_E_EXPLICIT_DISTRUST`, `CERT_E_UNTRUSTEDTESTROOT`, `TRUST_E_CERT_SIGNATURE`, `CERT_E_REVOKED`, `CRYPT_E_REVOCATION_OFFLINE` | `Intact` (2) | Continue: the bytes match the signature, only trust in the publisher is missing (that decision is left to the user and SmartScreen). |
| `TRUST_E_BAD_DIGEST`, `CRYPT_E_BAD_MSG`, and any other failure | `Tampered` (3) | Abort with exit code 4 and the detail "The digital signature does not match the file (0x…)". |

The log line `Windows build …, elevated=…, signature=<state> (0x<WinVerifyTrust result>)` records the outcome.

## 7. The uninstaller (`uninstall.exe`)

`Engine::WriteUninstaller` builds it from the running package during installation:

1. `Container::CopyStubTo` copies bytes `[0, overlayStart)` of Setup.exe (the patched stub, including the alignment
   padding) and zeroes the `IMAGE_DIRECTORY_ENTRY_SECURITY` entry and the PE `CheckSum`. The certificate table lies
   after the footer and is not copied, so the result is a valid, **unsigned** PE that keeps the icon and version
   resources.
2. The new overlay starts at the same `overlayStart`. Each front-end stream is copied verbatim (stored bytes), followed
   by zero padding to 8 bytes; only its `offset` changes in the header.
3. The header is the original header DOM with `kind` set to `"uninstaller"`, `payload` replaced by
   `{"streams": [], "files": [], "directories": []}`, and `frontend.streams[].offset` rewritten. It is serialized as
   compact JSON by the native serializer and stored **uncompressed**: footer `flags = 0`,
   `headerSize = headerRawSize`.
4. Zero padding to 8 bytes, then the 80-byte footer.

The file is written to `<InstallDir>\.fsetup\uninstall.new` and then moved to `<InstallDir>\uninstall.exe`; both steps
are journaled. When `uninstall.exe` starts, `kind = "uninstaller"` puts it into uninstall mode; it first copies itself
to a private temporary directory and runs the copy (see `docs/diagnostics.md`).

### 7.1 Signed uninstaller (`uninstallerImage`)

With `signing.signUninstaller` the builder writes the uninstaller exactly as the engine will (stub with the
Authenticode directory and checksum cleared, the front-end streams, an uncompressed header derived from the setup
header with `kind: "uninstaller"`, an empty payload and no `redist`, footer with flags 0), signs it, and checks that
the signing tool only set the checksum and the security directory and appended the certificate table. The engine
writes the same bytes using the stored header, sets the checksum and the security directory (offset = file size,
size = table size), appends the table and keeps the file only if its SHA-256 equals `uninstallerImage.sha256`;
otherwise it writes the unsigned uninstaller.

## 8. Compatibility and versioning

* The container version is the footer `version` field; both readers refuse anything other than `1`. The header has its
  own `format` field; the native parser refuses anything other than `1`.
* The native reader refuses unknown footer flag bits.
* Readers ignore unknown header keys, so a header with additional keys is still accepted by an existing stub. A change
  that existing readers would misinterpret (new meaning of an existing field, new mandatory field, new stream layout)
  needs a new `format` or container `version`, because old stubs cannot detect it otherwise.
* Every Setup.exe reads only its own overlay, and `uninstall.exe` is produced by the same stub that installed the app,
  so the stub and the container it reads always come from the same build. The constants that must stay identical are
  in `container.h` and `PackageFormat.cs` (magic, version, footer size and layout, flag values, 64 MiB header limit).
* Post-processing Setup.exe with anything other than Authenticode signing is not supported: `overlayStart` is an
  absolute offset and the stub refuses a footer whose overlay starts before the end of the PE sections, so tools that
  rewrite or grow the PE image break the package.

## 9. Known differences between the readers

`PackageInspector` (used by `fluentsetup inspect` and the build's verification step) is more lenient than the native
reader. A file it accepts can still be rejected by Setup.exe:

* it does not check `overlayStart` alignment or that `overlayStart` lies after the PE sections;
* it does not reject `headerSize = 0` or unknown footer flag bits;
* it compares stream hashes as lowercase hex strings (the native parser accepts upper- and lowercase hex);
* it reads the PE headers only from the first 4096 bytes and accepts `e_lfanew <= 3800`;
* it validates neither the header JSON rules of section 4 nor per-file hashes (`--verify` checks stream hashes only).
