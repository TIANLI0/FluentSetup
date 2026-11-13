# FluentSetup security model

This document describes the threats FluentSetup's installer runtime is designed against and how each mitigation is
implemented, with references to the code. It also lists what is **not** protected and the known gaps found while
writing this document. Line numbers refer to the current revision of the files.

## 1. Assets, actors and trust boundaries

**What is being protected**

* The integrity of what gets installed: the files, shortcuts and Apps & features (ARP) entry must be exactly what the
  package author built, or nothing at all.
* The rest of the machine: an installer, especially one running as administrator, must not write, delete or execute
  anything outside the places it owns.
* The administrator's privileges: the elevated worker must not become a tool that a standard user can steer.

**Actors**

| Actor | Trusted? | Notes |
|---|---|---|
| Package author (whoever ran the builder) | Yes, for the content | The package decides what is installed and which payload programs run as actions. FluentSetup confines *where* this happens; it does not judge *what* the author ships. |
| Party that modifies `Setup.exe` in transit or on disk | No | Detected by hashes (accidental damage, unsigned) and Authenticode (signed). |
| Other processes of the same user at medium integrity | Partly | They can already do anything the user can; they must not be able to use the elevated worker or the installer's temp files to gain more. |
| Other users on the machine | No | Must not read or tamper with temp directories, pipes or per-machine install locations. |
| Network | Not applicable | The runtime makes no network requests (revocation checks are offline, see 3.4). |

**Boundaries:** the unelevated `Setup.exe` process, the elevated worker (`Setup.exe --worker`), the WinUI front-end child
process, the per-user temp directory, the install directory, and the registry keys `HKCU|HKLM\...\Uninstall\<appId>`
and `HKCU|HKLM\Software\FluentSetup\Pending\<appId>`.

## 2. Threats and mitigations

### 2.1 DLL search-order hijacking

`Setup.exe` usually runs from the Downloads folder, where a malicious `version.dll`, `uxtheme.dll` etc. could be planted.

* The first statement of `wWinMain` restricts the process DLL search to System32:
  `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)` (`src/Bootstrapper/main.cpp:113`).
* Imports that are not KnownDLLs are **delay-loaded** so they are resolved only after that call:
  `comctl32.dll; uxtheme.dll; dwmapi.dll; wintrust.dll; bcrypt.dll; shcore.dll; oleaut32.dll`
  (`src/Bootstrapper/FluentSetup.Stub.vcxproj:24-26`). Statically imported DLLs (kernel32, user32, gdi32, advapi32,
  shell32, ole32) are KnownDLLs.
* Every native project linked through `build/native.props` gets `/DEPENDENTLOADFLAG:0x800`
  (`LOAD_LIBRARY_SEARCH_SYSTEM32`) for load-time dependencies (`build/native.props:91`).
* The fallback UI loads `shcore.dll` explicitly from System32 (`src/Setup.Win32/fallback_ui.cpp:184`).
* The binaries use the hybrid CRT (static VC runtime + system UCRT, `build/native.props:68-70, 94-99`), so there is no
  `vcruntime140.dll`/`msvcp140.dll` to plant next to `Setup.exe`.
* The WinUI front end runs from a freshly created private temp directory whose files are hash-verified and locked
  (2.2); it loads `Microsoft.WindowsAppRuntime.Bootstrap.dll` by absolute path from that directory
  (`src/Setup.WinUI/main.cpp:63-66`). `FluentSetup.UI.vcxproj` does not import `native.props`, so the UI does not get
  `/DEPENDENTLOADFLAG` or `SetDefaultDllDirectories`; it relies on its directory being private.

Additional hardening: heap termination on corruption (`main.cpp:114`), `/sdl`, Control Flow Guard and `/GS`
(`build/native.props:63, 71, 83`), `asInvoker` manifests for both executables (`src/Bootstrapper/stub.manifest:9`,
`src/Setup.WinUI/app.manifest`).

### 2.2 Temporary-directory hijacking

Three things are written to temp: the WinUI front-end bundle, the phase-2 copy of `uninstall.exe`, and the log.

* `path::TempRoot()` prefers `GetTempPath2W`, which returns `C:\Windows\SystemTemp` for SYSTEM instead of the shared,
  user-writable `C:\Windows\Temp` (`src/Common/path.cpp:199-209`).
* Bundle and phase-2 directories are named `FluentSetup-<16 hex>` / `FluentSetup-u-<16 hex>` from `BCryptGenRandom`
  (`src/Bootstrapper/winui_host.cpp:26`, `src/Bootstrapper/main.cpp:57`, `src/Common/hash.cpp:56-66`) and are created
  with `security::CreatePrivateDirectory` (`src/Common/security.cpp:45-58`): a **protected** DACL
  `D:P(A;OICI;FA;;;<user SID>)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)`. `CreateDirectoryW` fails if the name already exists,
  so a pre-created (squatted) directory is never reused; the caller then aborts.
* Front-end files are created with `CREATE_NEW`, then reopened `GENERIC_READ` with `FILE_SHARE_READ` only (no write or
  delete sharing), re-hashed from disk and compared with the header's size and SHA-256; the handles stay open until the
  session ends, so the bytes that were verified are the bytes that get loaded (`winui_host.cpp:41-77`). The directory is
  deleted when the session ends (`winui_host.cpp:21-24`).
* `fs::WriteAllAtomic` writes to a randomly named `CREATE_NEW` sibling and renames it over the target
  (`src/Common/fs.cpp:101-126`).

The log file is opened with `FILE_FLAG_OPEN_REPARSE_POINT` and must be a regular disk file with a single hard link. In an elevated process (the worker), any link in the log path, or a final path that differs from the requested one, makes `log::Open` skip the log (and delete the file if it had just created it) instead of writing through the link (`src/Common/log.cpp`; gap G1, fixed).

### 2.3 Path traversal, absolute, UNC and device paths in packages

All untrusted path text (package header, install record, journal, command line, UI input) goes through
`src/Common/path.cpp`:

* `ValidateRelative` (`path.cpp:67-98`) accepts only relative paths made of valid components. It rejects empty paths,
  paths over 1024 characters, a leading `\` or `/` (absolute or UNC), drive-qualified paths (`X:`), empty components
  (`a\\b`), `.` and `..`, more than 64 levels, and any component that `IsValidComponent` (`path.cpp:50-65`) rejects:
  control characters, `< > : " / \ | ? *` (so `:` alternate data streams are impossible), leading spaces, trailing dots
  or spaces, components over 255 characters, and DOS device names including `CON`, `NUL`, `CONIN$`, `COM0-9`, `LPT0-9`
  and the superscript-digit variants, also with an extension or trailing spaces (`path.cpp:34-48`).
* `ValidateAbsoluteSyntax` (`path.cpp:100-134`) accepts only `X:\...` on an existing local drive: it rejects `\\?\` and
  `\\.\` prefixes, UNC paths, relative paths, the drive root, paths over 200 characters, invalid or reserved
  components, unknown drives and `DRIVE_REMOTE` (mapped network drives).
* The header parser applies `ValidateRelative` to every payload/frontend file and directory, the main executable, the
  frontend entry and icon, action targets, the WebView2 installer and `removeData` sub-paths
  (`src/Install.Core/package.cpp:105-106, 142, 181, 231-234`; `src/Install.Core/extensions.cpp:120, 154, 369`). It also
  rejects the reserved names `uninstall.exe`, `uninstall.json` and the `.fsetup` folder at top level
  (`package.cpp:63-67, 108, 143`), case-insensitive duplicates, a path that is both a file and a directory
  (`package.cpp:109, 132-138`), and a main executable or action that is not a payload `.exe` (`package.cpp:179-183,
  211-215`; `extensions.cpp:155`).
* Install records and journals are re-validated when read back: record paths with `ValidateRelative`, the install
  directory with `ValidateAbsoluteSyntax`, shortcuts with `IsAllowedShortcutPath` (`src/Install.Core/installed.cpp:142-181`);
  rollback only touches paths inside the install directory, inside `.fsetup`, or allowed shortcut paths
  (`src/Install.Core/journal.cpp:121-124, 139-156`).
* The builder applies the same rules before a package is ever written (`src/Builder.Core/Packaging/PathRules.cs`), so a
  package that the runtime would reject is normally not produced; the runtime does not rely on that.

### 2.4 Junctions, symbolic links and other reparse points

* `fs::IsLinkLike` treats a path as a link if it is a reparse point with tag `IO_REPARSE_TAG_SYMLINK` or
  `IO_REPARSE_TAG_MOUNT_POINT` (junctions); `fs::HasLinkInPath` checks every existing prefix of a path
  (`src/Common/fs.cpp:19-38`).
* **Install directory:** rejected with `path.reparse` if any existing component is a link
  (`src/Install.Core/engine.cpp:314`); after it has been created, it is opened and its `GetFinalPathNameByHandle` must
  equal the requested path, which also catches other redirections such as `subst` drives (`engine.cpp:648-654`). The
  `.fsetup` work directory must not be a link (`engine.cpp:656-657, 887-888`).
* **Extraction:** each parent directory is checked with `HasLinkInPath` (results cached per directory)
  (`engine.cpp:378-385, 398`); an existing target that is a directory or link fails the install (`engine.cpp:406-408`);
  new files are created with `CREATE_NEW | FILE_FLAG_OPEN_REPARSE_POINT` (`engine.cpp:419-420`).
* **Uninstall:** the install directory may not go through a link (`engine.cpp:880`) and the record path is checked too
  (`src/Install.Core/installed.cpp:186`); recorded files that are directories, links, or whose parent path contains a link
  are skipped, not deleted (`engine.cpp:917-920`). Files are moved, not opened, so a link would be moved as a link.
* **Recursive deletion** (`fs::DeleteTree`, `fs.cpp:163-189`) unlinks a reparse-point directory with
  `RemoveDirectoryW` and never recurses into it. `removeData` targets that are links are skipped (`engine.cpp:954`).
* **Rollback:** only moves between `.fsetup` and the install directory/shortcut folders (`journal.cpp:141-157`).
* **Action executables** must not have a link in their parent path (`engine.cpp:549`).
* The builder does not follow junctions when collecting files (`src/Builder.Core/Packaging/FileCollector.cs:85`).

These are path-based checks followed by path-based operations; see gap G3 for the race this leaves.

### 2.5 Refusing unsafe install locations

`Engine::CheckInstallDir` (`engine.cpp:305-341`) rejects, with a precise `path.*` reason:

* invalid syntax, network and unknown drives, drive roots (2.3);
* **protected locations** (`IsProtectedLocation`, `engine.cpp:211-232`): the Windows directory and anything below it;
  exactly Program Files (x64/x86/Common), ProgramData, the Users folder, the profile root, Desktop, Public Desktop,
  Documents, Downloads, Pictures, Music, Videos, LocalAppData, LocalLow, Roaming AppData, UserProgramFiles, Start menu and
  Programs folders (user and common), Startup and Public; and any **ancestor** of those (for example `C:\Users`);
* a path containing a link (`Reparse`);
* a different directory than the one an existing installation uses (`Locked`);
* a file instead of a directory, or a non-empty directory that holds another app's `uninstall.json`/`uninstall.exe`
  (`OtherApp`) or any file this package would overwrite that is not marked `preserve` (`NotEmpty`);
* for per-user scope, a location the user cannot create a directory in (`NotWritable`), and insufficient free space.

The checks run in the controller for UI feedback and again in `Engine::Install` immediately before anything is changed
(`engine.cpp:609-616`), in whichever process (user or elevated worker) performs the install. The e2e test covers the
Windows folder, the profile root and a junction (`tests/Integration/e2e.ps1:215-228`).

For per-machine installs, a newly created top-level directory outside Program Files receives a protected DACL
`D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;AC)` (SYSTEM and Administrators full
control; Users and ALL APPLICATION PACKAGES read & execute) (`src/Common/security.cpp:60-77`, `engine.cpp:640-647`).
An existing but empty directory chosen outside Program Files gets the same DACL. Directories under Program Files keep
the inherited (already protected) ACL. A pre-existing, non-empty directory (an upgrade in place, possibly of an app
another installer put there) keeps its ACL, because the app may rely on writing into it; the log records a warning.
See gap G2.

### 2.6 Elevated worker authentication

Per-machine work happens in a separately elevated copy of the same executable (`asInvoker` front end, no
auto-elevation). The design goal is that the worker only installs or removes **its own embedded package** and can only
be driven by the process that launched it.

* **Launch:** `ShellExecuteExW` with verb `runas` on `path::ModulePath()` (the running, already validated file)
  (`src/Bootstrapper/elevation.cpp:69-83`, `src/Bootstrapper/controller.cpp:311-322`). The worker re-opens and re-validates
  its own package and signature like any start (`main.cpp:129-139`), and executes the engine on that package only
  (`src/Install.Worker/worker.cpp:87-88`); nothing in the IPC protocol can name another package, file or command.
* **Pipe name:** `FluentSetup-` + 128 random bits (`controller.cpp:303`, `hash.cpp:62-66`).
* **Pipe object** (`src/IPC/pipe.cpp:65-88`): `FILE_FLAG_FIRST_PIPE_INSTANCE` and `nMaxInstances = 1` (creation fails if
  someone already owns the name), `PIPE_REJECT_REMOTE_CLIENTS`, and a protected DACL
  `D:P(A;;GA;;;<current user>)(A;;GA;;;BA)(A;;GA;;;SY)` (`pipe.cpp:16-36`). Administrators are included because an
  over-the-shoulder elevated worker runs as a different account.
* **Server checks client:** after `ConnectNamedPipe`, the client PID (`GetNamedPipeClientProcessId`) must equal the PID
  of the process returned by `ShellExecuteEx`; otherwise the worker is terminated (`controller.cpp:331-337`).
* **Client checks server:** the worker opens the parent process by PID before connecting (so the PID cannot be recycled
  while it runs), connects with `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION` (the server can never impersonate the
  admin) (`pipe.cpp:108-110`), and requires `GetNamedPipeServerProcessId == --parent-pid` (`worker.cpp:41-57`).
* **What the worker refuses** (`worker.cpp:36-74`): running unelevated; a parent that does not exist; a server that is
  not the parent; no `run` message within 60 s; an `op` other than `install`/`uninstall`; an `op` that does not match its
  own command line (`/uninstall`); `install` on an uninstaller package; malformed options (`OptionsFromJson`: `scope`
  must be `user|machine`, `installDir` at most 1024 characters, at most 16 choices with ids up to 32 characters and
  boolean values, `engine.cpp:74-89`). The engine then re-checks scope, elevation and the install directory
  (`engine.cpp:583-616`).
* **Messages:** at most 16 MiB, JSON depth at most 32, must be an object (`pipe.cpp:178-189`, `pipe.h:21`). Progress
  coming back from the worker is clamped (`engine.cpp:50-60`); outcome codes outside the known range become `Internal`
  (`engine.cpp:109-122`).
* **Lifetime:** if the controller goes away, the worker's reader thread sets cancel and the operation rolls back
  (`worker.cpp:77-84`).
* **Launching the app** after a per-machine install never happens elevated: the main executable must be strictly inside
  the install directory (`controller.cpp:404-411`) and an elevated process starts it through the desktop shell
  (Explorer) or not at all (`elevation.cpp:85-101`).

The same channel protects the WinUI front end: the UI checks the server PID it was given (`src/Setup.WinUI/UiChannel.cpp:14-23`)
and the bootstrapper checks the UI's PID (`winui_host.cpp:200`); options coming from the UI are re-validated by
`Controller::SanitizeOptions` (`controller.cpp:61-79`) before use. The package rules (allowed scope, custom folder,
visible shortcut offers, declared option ids) live in `Engine::SanitizeOptions`, which the controller and the elevated
worker both apply, so a modified unelevated parent cannot bypass them (gap G6, fixed).

### 2.7 Single instance

A `Global\FluentSetup-<appId>` mutex allows one setup/uninstall of an app at a time across sessions
(`InstanceLock` in `main.cpp`); a second instance exits with `AlreadyRunning` (16). The mutex has an explicit security
descriptor that lets every authenticated or interactive user wait on it, and a mutex that cannot be created or opened
counts as held (fail closed; gap G7, fixed). A repair releases it just before it starts the Setup.exe of the installed
version, which takes it again.
Individual pipes are single-instance (`FILE_FLAG_FIRST_PIPE_INSTANCE`, one instance, 2.6).

### 2.8 Integrity of the package (SHA-256)

* **Container:** the footer is located from the PE security directory or end of file; all offsets and sizes are
  bounds-checked (PE header sanity, section count <= 96, certificate table inside the file and after the image,
  overlay after the image and 8-byte aligned, header <= 64 MiB inside the overlay, no unknown flags)
  (`src/Archive/container.cpp:63-138`). The header's SHA-256 must match the footer (`container.cpp:140-151`).
* **Streams:** every stream must lie between the overlay start and the header and streams must be ascending and
  non-overlapping (`container.cpp:153-156`, `package.cpp:80-95`); the frontend streams may not overlap the payload
  streams (`package.cpp:235-240`). The SHA-256 of the stored bytes is verified once the stream is consumed, together with
  an exact decoded size, a complete final frame, and no trailing data (`container.cpp:219-248`,
  `engine.cpp:456-457`).
* **Files:** files must be contiguous inside their stream and add up to its `rawSize` (`package.cpp:97-130`). Each file's
  SHA-256 is computed while it is written and compared with the header (`engine.cpp:428-451`); a mismatch raises
  `PackageCorrupt` and the install is rolled back. Front-end files are verified from disk under a deny-write lock
  before use (2.2).
* **The package file** is held open with `FILE_SHARE_READ` only from validation to the end of the process
  (`container.cpp:29-31`), so it cannot be modified between the signature check and extraction.
* **Install record:** `uninstall.json`'s SHA-256 is stored in the ARP key as `FluentSetupRecordSha256`
  (`engine.cpp:786`); upgrade and uninstall refuse a record that does not match it, belongs to another app, scope or
  directory (`installed.cpp:183-207`).

Without a signature these hashes detect accidental damage only: the header hash is stored in the footer of the same
file, so anyone who can rewrite the file can recompute it.

### 2.9 Authenticode tamper detection

`archive::CheckSignature` (`container.cpp:252-282`) calls `WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)` on the
running `Setup.exe` with no UI, no revocation checks and cache-only URL retrieval (offline). Results map to:

| Result | State | Effect |
|---|---|---|
| `ERROR_SUCCESS` | `Valid` | proceed |
| no signature / unknown subject form or provider | `Unsigned` | proceed |
| `TRUST_E_BAD_DIGEST`, `CRYPT_E_BAD_MSG`, any unlisted error | `Tampered` | refused as `PackageCorrupt` (exit 4) before the header is parsed (`main.cpp:131-134`) |
| untrusted root, chaining, expired, explicit distrust, test root, bad cert signature, revoked, revocation offline | `Intact` | proceed (logged) |

Because the overlay lies before the certificate table, Authenticode's hash covers the stub, all streams, the header and
the footer; any change to a signed package becomes `Tampered`. The signature state is written to the log
(`main.cpp:161-162`) but is not shown in either UI. `uninstall.exe` is written without the certificate table
(`container.cpp:158-179`), so it is always unsigned.

### 2.10 Bounded parsing

* JSON (`src/Common/json.h:86-92`, `json.cpp`): recursive descent with a depth limit and a byte limit
  (`json.cpp:410`), strict grammar (no comments, trailing commas or control characters in strings, surrogates validated,
  non-finite numbers rejected), never throws on bad input. Limits used: package header depth 16 and 64 MiB
  (`package.cpp:155-157`, `container.h:24`); IPC depth 32 and 16 MiB (`pipe.cpp:185-188`); install record 256 MiB file
  (`installed.cpp:189`); journal 512 MiB (`journal.cpp:94`). Duplicate keys are accepted and the first one wins.
* Header semantics: at most 1,000,000 streams and 2,000,000 files, total 16 TiB sanity bound
  (`package.cpp:15-17, 77-78, 125`); string fields have maximum lengths and no control characters
  (`package.cpp:21-32`); app ids are `[A-Za-z0-9._-]{1,64}` (`package.cpp:44-52`); website must be http(s); at most
  32 processes, 16 options, 32 actions with at most 32 arguments of 1024 characters each (`extensions.cpp:40-50, 104,
  145, 181`).
* Command line: unknown switches and malformed values are errors; values at most 2048 characters; pipe names
  alphanumeric/`-` up to 80; PIDs and handle values are range-checked decimal (`src/Bootstrapper/args.cpp:35-179`).

### 2.11 zstd limits and decompression bombs

* Only the decoder is compiled, without legacy formats or assembly (`src/Archive/FluentSetup.Archive.vcxproj:13, 22-29`).
* Streaming decoders refuse frames needing a window above 2^27 bytes (128 MiB), bounding decoder memory
  (`container.cpp:200-201`); the builder never produces larger windows (`PackageBuilder.cs:314-318`).
* Output is bounded by the declared size: decoding more than `rawSize` is `PackageCorrupt` immediately
  (`container.cpp:241`); the header is decoded into a buffer of exactly `headerRawSize` <= 64 MiB and the frame's declared
  content size must match (`container.cpp:183-191`).
* Declared sizes must add up (`package.cpp:118-130`), and the declared total plus uninstaller must fit on the target
  drive with a 4 MiB margin before anything is written (`engine.cpp:298-303, 338-339`). A "bomb" therefore either
  declares its real size and is refused for lack of space, or lies and is stopped as soon as it exceeds the declared
  size.

### 2.12 Deletion boundaries (rollback and uninstall)

Uninstall only removes:

* files listed in the **verified** install record (2.8), relative to the recorded install directory, that are regular
  files not reached through a link and not `preserve` (`engine.cpp:910-927`);
* shortcuts listed in the record, which must be `.lnk` files directly in the scope's Start menu Programs or Desktop
  folder (`installed.cpp:175-179, 209-225`);
* the record, `uninstall.exe`, the `.fsetup` work directory, recorded directories that are empty, and the install
  directory only if empty (`engine.cpp:958-972`); anything else is kept and reported as leftovers;
* declared cleanup: `removeData` folders that must be `{CommonAppData}|{LocalAppData}|{AppData}\<valid relative path>`
  (`extensions.cpp:357-374`) and are not links (`engine.cpp:952-956`), scheduled tasks in the root folder and `Run`
  values by validated name (`extensions.cpp:52-58, 376-408`).

Files are first moved into `.fsetup\t-<random>` under journal control, so a failure before commit restores them.
Rollback is bounded the same way (2.3, 2.4). A foreign ARP key with the package's id is never overwritten unless the
package explicitly adopts it (`engine.cpp:598-600`).

### 2.13 Extension actions

* Only `.exe` files that are part of the payload can be run (`extensions.cpp:154-155`), from inside the install
  directory, existing, not through a link (`engine.cpp:548-551`).
* `CreateProcessW` with an explicit application name, an argument vector quoted with `CommandLineToArgvW` rules, no shell,
  no `PATH` lookup, no handle inheritance, `CREATE_NO_WINDOW`, working directory = the executable's directory
  (`extensions.cpp:60-79, 321-341`).
* The process starts suspended and is assigned to a job object before it runs; on timeout (1-3600 s) the whole job is
  terminated (`extensions.cpp:326-347`). Exit codes are checked against `successExitCodes`; `onFailure: fail` fails
  the operation (and rolls back), `warn`/`ignore` continue (`engine.cpp:553-564`).
* Actions run with the installer's rights: as the user for per-user installs, and **as administrator** inside the
  elevated worker for per-machine installs.

## 3. What is not protected

* **A malicious or compromised package author.** The author chooses the payload, the actions (which may run elevated
  for per-machine packages), what processes are closed, what tasks/Run values/data folders are removed on uninstall, and
  the texts shown. FluentSetup confines these to the documented locations; it cannot make a malicious program safe.
* **Unsigned packages.** Without Authenticode, anyone who can modify `Setup.exe` can produce a consistent package with
  valid hashes. Users see "Unknown publisher" in UAC and SmartScreen.
* **Publisher trust.** A signature that is intact but not trusted, expired, revoked or explicitly distrusted is accepted
  (`Intact`, 2.9). Deciding whether to trust a publisher is left to the user, UAC and SmartScreen; the installer only
  enforces integrity. Revocation is not checked online.
* **Same-user malware.** Code running as the same user can already modify `Setup.exe` before it starts, attach to the
  unelevated process, or replace the copy of the executable that is about to be elevated (for example the phase-2
  `uninstall.exe` in the user's private temp directory). UAC consent prompts are not a security boundary against it.
* **Machine-wide integrity of the uninstaller.** `uninstall.exe` is unsigned and only self-hashed; its protection is the
  ACL of the install directory.
* **Power loss.** The journal flushes file entries every 64 operations; a power failure can lose the last entries and
  leave files that rollback does not know about.

## 4. Recommendations

1. **Sign every package.** Enable the `signing` section of the project (`SigningSettings` in
   `src/Builder.Core/Model/ProjectModel.cs`): `enabled`, a `provider` and its settings: `signtool` with
   `certificateThumbprint` (certificate store; preferred, works with hardware tokens) or `certificateFile` (.pfx) with
   `passwordEnvironmentVariable` (the password is never stored in the project and never put on a command line),
   `trustedSigning` (Azure Artifact Signing, keys never leave Azure), or `command` (any signing tool, e.g. against a
   cloud HSM; also on Linux and macOS). Set `timestampUrl` (default `http://timestamp.digicert.com`; always timestamp so
   signatures outlive the certificate). The builder signs with a SHA-256 file digest
   (`src/Builder.Core/Packaging/Signer.cs`), fails if the file has no signature afterwards and, with output
   verification on, re-inspects the written package. Check the result with `fluentsetup inspect --verify` and
   `signtool verify /pa`.
2. Prefer per-user packages unless the app really needs machine-wide installation; per-machine packages run their
   actions as administrator.
3. For per-machine packages keep the default `{ProgramFiles}\{AppName}` location and consider `allowCustomDir: false`
   (see gap G2).
4. Keep actions minimal and make them idempotent: their effects are not rolled back.
5. Distribute over HTTPS and publish the SHA-256 printed in the build report.

## Prerequisite downloads

A package can let Setup.exe download a missing prerequisite (`requirements.*.download`). This is the only network
access FluentSetup ever makes, and only when that prerequisite is missing on the PC.

* https only: the builder and the installer engine both reject other schemes, TLS 1.2 or 1.3 is required, and
  redirects from https to http are refused (`WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP`).
* The package must state the file's SHA-256 (and may state its exact size). The download is streamed into a new file
  (`CREATE_NEW`) in a private directory (`security::CreatePrivateDirectory`), hashed while it is written, capped at the
  declared size (512 MiB if unknown), and run only if size and hash match. Otherwise setup stops before anything is
  installed (exit code 17). The folder is deleted afterwards.
* The download happens before the install is started, so a network failure never leaves a half-installed app.
* The downloaded installer runs like an action: `CreateProcess` without a shell, in a job object, with a timeout. For
  per-machine installs it runs elevated, so the SHA-256 in the (ideally signed) package is what authorises it.
## 5. Known gaps and weaknesses

Found while reading the code for this document. G1, G5, G6, G7, G8, G10, G11 and G14 have been fixed since, and G2 partly; the rows say so.

| Id | Severity (estimate) | Location | Description |
|---|---|---|---|
| G1 | Fixed | `src/Common/log.cpp` | *Was:* the elevated worker opened its log at a path supplied by the unelevated parent (default: the user's `%TEMP%\FluentSetup`) with `OPEN_ALWAYS` and without `FILE_FLAG_OPEN_REPARSE_POINT`, so a planted junction/symlink/hard link could make the administrator create or append to an arbitrary file. *Now:* reparse points and multiply-linked files are refused, and an elevated process also refuses links anywhere in the path and verifies the final path of the opened handle. |
| G2 | Medium (partly fixed) | `src/Install.Core/engine.cpp` (Preparing) | For per-machine installs outside Program Files the protected ACL is applied to directories setup creates and to an existing *empty* directory the user picked. A pre-existing non-empty directory (upgrade in place, adopted install) keeps its ACL; if a standard user can write there, they can replace binaries installed by the administrator, including `uninstall.exe` and the executables that `afterInstall` actions run elevated, and they control the `.fsetup` journal (see G4). The engine logs a warning in that case. |
| G3 | Medium (only with G2-type locations or user-created `ProgramData` folders) | `src/Install.Core/engine.cpp:378-385, 398, 903-920, 952-956`; `src/Common/fs.cpp:163-189` | Link checks are path-based and done before the operation (and cached per directory during extraction); the subsequent create/move/delete is again path-based. In a directory a standard user can modify, a directory can be swapped for a junction between check and use (time-of-check/time-of-use), redirecting elevated writes, moves or the recursive `DeleteTree` of `removeData` (for example a folder under `C:\ProgramData` that a user pre-created). |
| G4 | Low | `src/Install.Core/engine.cpp:343-364`; `src/Install.Core/journal.cpp:161-163` | Recovery runs elevated over `<dir>\.fsetup\journal.jsonl`. If the install directory is user-writable (G2) and a machine-scope pending marker exists (after a crash), a user can edit the journal: rollback stays inside the install directory and shortcut folders, but `reg` entries restore arbitrary values into the app's HKLM ARP key. |
| G5 | Fixed | `src/Archive/container.cpp` (`CheckSignature`) | *Was:* `CERT_E_REVOKED` and `TRUST_E_EXPLICIT_DISTRUST` were accepted and the signature state was not shown. *Now:* a certificate the PC knows to be revoked or that is explicitly distrusted is refused (`PackageCorrupt`, also for updates and Repair packages), and the first page shows "Verified publisher: <name>" for a trusted signature or "Unknown publisher" otherwise. Revocation is still not checked online. |
| G6 | Fixed | `src/Install.Worker/worker.cpp`; `Engine::SanitizeOptions` | *Was:* the worker did not re-apply the package's option policy. *Now:* the worker rejects options that `Engine::SanitizeOptions` refuses (scope, custom folder when `allowCustomDir` is false, undeclared option ids) and forces hidden shortcut offers to their defaults. |
| G7 | Fixed | `src/Bootstrapper/main.cpp` (`InstanceLock`) | *Was:* the `Global\FluentSetup-<appId>` mutex was created with default security, and if an existing object denied access, `CreateMutexW` failed and the single-instance check was silently skipped. *Now:* it is created with an explicit security descriptor (`D:P(A;;0x001F0001;;;SY)(A;;0x001F0001;;;BA)(A;;0x00100001;;;AU)(A;;0x00100001;;;IU)(A;;0x00100001;;;OW)`: every authenticated or interactive user may wait on it and release it, so setups of different users and sessions see each other; the owner-rights ACE keeps its creator from rewriting that security) and opened with `SYNCHRONIZE \| MUTEX_MODIFY_STATE`. Any failure to create or open it, including access denied or a same-named object of another type, is treated as "another setup is running" (exit code 16, fail closed). A user who creates the mutex first can still make other users' setups of that app wait while their own runs; that is the purpose of the lock. |
| G8 | Fixed | `src/Install.Core/extensions.cpp` (`RunProgram`); `engine.cpp` | *Was:* bundled prerequisite installers skipped the location checks of actions, and processes an action started outlived it. *Now:* an installer among the installed files must be strictly inside the install folder and not reached through a link, and the job object of an action ends every process the action left running when it exits (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`). Vendor prerequisite installers are exempt: their updaters keep running on purpose. |
| G9 | Fixed | `src/Builder.Core/Packaging/Signer.cs` (`TemporaryCertificateStore`) | *Was:* the PFX password was passed to `signtool.exe` on its command line (`/p`), visible to other processes on the build machine and to process-creation auditing; it was only masked in the log. *Now:* a password-protected .pfx is imported (password from the environment variable) into a certificate store of its own under the current user (`FluentSetup-Signing-<pid>-<random>`), signtool signs with `/s <store> /sha1 <thumbprint>`, and the store and the persisted private key are deleted afterwards, also when signing fails. A store left by a build that was killed while signing (its process is gone) is removed with its key by the next signing run. The command provider never receives secrets from FluentSetup; tools read them from the environment. |
| G10 | Fixed | `src/Bootstrapper/main.cpp` (`RunUninstallPhase1`) | A running copy cannot delete itself, so each uninstall removes the `%TEMP%\FluentSetup-u-<random>` copies earlier uninstalls left behind (skipping links and copies still in use). The copy of the most recent uninstall remains until the next one. |
| G11 | Fixed | `src/Bootstrapper/controller.cpp` (`Run`); `src/Bootstrapper/main.cpp` | *Was:* test hooks were active in every release package: the `FLUENTSETUP_TEST_FAULT_AFTER` environment variable (forces a failure and rollback after N files, in-process installs only) and `--ui-simulate`. *Now:* both are honoured only by packages built with `fluentsetup build --test-hooks` (header `testHooks: true`, used by the e2e and UI tests); other packages ignore them and log a warning. The parser still accepts `--ui-simulate`, so such a command line is not an error. |
| G12 | Informational | `src/Bootstrapper/winui_host.cpp:177`; `src/Setup.WinUI/app.manifest` | The UI manifest comment says the UI "never runs elevated", but if `Setup.exe` itself is started elevated the WinUI child inherits the elevated token. |
| G13 | Informational | `src/Install.Core/extensions.cpp:399-408`; `src/Install.Core/engine.cpp:952-956` | `removeRunValues` deletes from both HKCU and HKLM regardless of scope, and in an over-the-shoulder elevated uninstall HKCU, `{LocalAppData}` and `{AppData}` refer to the administrator's profile, not the user who installed the app. |
| G14 | Fixed | `src/Common/json.cpp` | *Was:* duplicate JSON object keys were accepted (first one wins). *Now:* they are a parse error (package headers, install records, journals, IPC messages, update feeds). |

## 6. System integration (1.2)

* **Registry.** Everything setup writes into the registry (file types, URL schemes, App Paths, Default apps
  capabilities, `registry[]` values) goes through one writer that journals the previous state (whole trees for keys
  setup owns, single values elsewhere) and is confined to: `Software\Classes\<ProgID|scheme|.ext>` (ProgIDs need a
  dot and are never `Microsoft.*`/`Windows.*`/`AppX*`; program and shell extensions such as `.exe`, `.lnk`, `.ps1`,
  `.url` and schemes such as `http`, `file`, `ms-*`, `exefile`, `folder` are refused), `App Paths\<x.exe>`,
  `Software\FluentSetup\Capabilities\<appId>`, values of `Software\RegisteredApplications`, and
  `Software\<Vendor>\...` outside the shared trees. Rollback and uninstall re-check every key from the journal and the
  install record against these rules, so a tampered journal or record cannot make an elevated process touch other
  keys. Only the scope's own hive is used (HKCU for per-user installs, HKLM for per-machine installs). An existing key
  that is not a URL scheme is never replaced by a protocol registration, and an extension's default handler is only
  taken where no other app holds it and only removed while it is still this app's.
* **Services and firewall rules** are per-machine only, run built-in accounts only (no passwords in packages), point
  at programs among the installed files, and are removed by name (services) or by the app's own rule group (firewall;
  a rule name another program also uses is never removed).
* **Bundled and downloaded prerequisite installers.** Official installers are bundled after the builder checked their
  Microsoft signature, and verified by SHA-256 like every stream when setup extracts them into a private temporary
  folder. A download without a pinned SHA-256 (`"auto": "download"`, or `download.signer`) only runs if it carries a
  valid Authenticode signature with a trusted chain issued to the pinned name.
* **Pre-signed uninstaller.** Setup writes the signed `uninstall.exe` only if the reproduced file matches the SHA-256
  the (signed) package recorded; otherwise it writes the unsigned one, as before.
* **Rollback is never repeated.** If some steps of a rollback fail, the journal is rewritten to hold only those steps,
  so the next run retries them without undoing again what was already restored.
