# Diagnostics and troubleshooting

This page explains what a FluentSetup `Setup.exe` / `uninstall.exe` reports when something goes wrong: exit codes,
log files, the recovery of interrupted operations, front-end selection and the most common problems.

Related reference pages: `docs/package-format.md` (container and integrity checks) and
`shared/protocols/ui-protocol.md` (IPC between the bootstrapper, the WinUI front end and the elevated worker).

## 1. Exit codes

Setup.exe and uninstall.exe exit with a value of `fsetup::ErrorCode` (`src/Common/error.h`). The values are a public
contract and are never renumbered. The same code is shown in the UI as "Error code N (Name)", and the user-facing
sentence comes from the localization key `error.<Name>` in `shared/localization/en-US.json` (`{0}` = app name,
`{1}` = technical detail).

| Code | Name | Meaning | Typical causes (from the code) | Message key |
|---:|---|---|---|---|
| 0 | `Ok` | Success. | Also returned for `/help`. | `error.Ok` |
| 1 | `Failed` | Unclassified failure. | A package action with `onFailure` = fail did not succeed (non-success exit code, timeout or could not start). | `error.Failed` |
| 2 | `Cancelled` | The user cancelled; changes were rolled back. | Cancel during the operation; also the result when the window is closed before anything was started. | `error.Cancelled` |
| 3 | `InvalidArguments` | Bad command line or option value. | Unknown switch, missing/invalid value (the usage text is shown); options sent by a front end that the package does not allow; requested scope not allowed by the package; app already installed in the other scope ("uninstall it first to change the installation scope"); installing with an uninstaller package; malformed options in the elevated worker. | `error.InvalidArguments` |
| 4 | `PackageCorrupt` | The container, header or payload failed validation. | Missing/invalid footer, header or stream checksum mismatch, header rule violation, file checksum mismatch during installation, zstd frame needing a window > 128 MiB, Authenticode signature does not match the file. Any failure to open or validate the running package at start-up is reported with this code. | `error.PackageCorrupt` |
| 5 | `UnsupportedSystem` | OS too old. | Windows build lower than the header's `app.minWindowsBuild`. | `error.UnsupportedSystem` |
| 6 | `ElevationRequired` | Administrator rights needed but not granted. | The UAC prompt was declined or could not be shown; `/quiet` per-machine operation started from a non-elevated process (silent mode never shows UAC); `ERROR_ELEVATION_REQUIRED`. | `error.ElevationRequired` |
| 7 | `DiskFull` | Not enough space. | Path check `path.space` (free space < required size + 4 MiB); `ERROR_DISK_FULL` / `ERROR_HANDLE_DISK_FULL`. | `error.DiskFull` |
| 8 | `FileInUse` | A file is locked. | `ERROR_SHARING_VIOLATION`, `ERROR_LOCK_VIOLATION`, `ERROR_USER_MAPPED_FILE` while replacing or removing files. | `error.FileInUse` |
| 9 | `AccessDenied` | No permission. | Per-user target not writable (`path.notWritable`); `ERROR_ACCESS_DENIED`, `ERROR_PRIVILEGE_NOT_HELD`, `ERROR_WRITE_PROTECT`; access denied writing the Apps & features entry. | `error.AccessDenied` |
| 10 | `DowngradeBlocked` | A newer version is installed. | Installed version is newer and the package has `allowDowngrade: false`. | `error.DowngradeBlocked` |
| 11 | `NotInstalled` | Nothing to uninstall. | `/uninstall` (or uninstall.exe) when no installation of the app id is registered, or none matches the uninstaller's directory. | `error.NotInstalled` |
| 12 | `IoError` | A file could not be read or written. | Win32 errors without a more specific mapping; the uninstaller could not copy itself to `%TEMP%`; shortcut creation failures. | `error.IoError` |
| 13 | `RegistryError` | Registration with Windows failed. | Writing registry values (Apps & features entry, pending marker); another product already uses the uninstall key named after the app id. | `error.RegistryError` |
| 14 | `Internal` | IPC, protocol or unexpected state. | The elevated worker did not connect / stopped unexpectedly / failed its own checks; the worker pipe could not be created; an unexpected exception; the uninstaller could not start its relocated copy. | `error.Internal` |
| 15 | `UnsafePath` | The location is not safe to use. | Install location rejected for any `path.*` reason other than space or writability (see section 6.3); a link or junction inside the install directory; the install directory resolves to a different path; a directory or link where a file must go; an action target outside the install directory. | `error.UnsafePath` |
| 16 | `AlreadyRunning` | Another setup of the same app is running. | The mutex `Global\FluentSetup-<AppId>` is held (across sessions). | `error.AlreadyRunning` |
| 17 | `PrerequisiteMissing` | A required component is missing. | A `requirements` entry (.NET Framework release, WebView2) is not satisfied and the package does not bundle an installer for it. The detail is the requirement's name. | `error.PrerequisiteMissing` |
| 18 | `UpdateAvailable` | Not a failure. | `/check-update` found a published version newer than the installed one. | `error.UpdateAvailable` |
| 19 | `UpdateFailed` | The update could not be checked, downloaded or used. | `/check-update`, `/install-update`: the GitHub API or the feed did not answer or answered nonsense (rate limit: HTTP 403/429); no SHA-256 is published; the download does not match it, is not a FluentSetup package, is another app or architecture, contains another version, or lacks the signature `update.signer` requires. The log (`%TEMP%\FluentSetup\<AppId>-update-<time>.log`) has the reason. | `error.UpdateFailed` |

Front ends offer "Try again" only for retryable codes; codes 3, 4, 5, 10, 11 and 16 are not retryable.

Notes:

* When the Win32 or WinUI front end shows a failure, the "Technical details" area contains the English `detail` text
  that is also in the log (including "system error N: …" when a Win32 error was involved).
* For `uninstall.exe`, phase 1 (the copy in the install directory) returns the exit code that phase 2 (the relocated
  copy) reports through an inherited pipe, or phase 2's process exit code if nothing was reported.
* The elevated worker's own process exit code is the outcome code, or `Internal` (14) / `InvalidArguments` (3) when it
  refuses to run (see `shared/protocols/ui-protocol.md`, section 3.2).
* `FluentSetup.UI.exe` has its own exit codes (`0x20`–`0x26`); they only appear in the log, never as Setup.exe exit
  codes (protocol document, section 2.5).
* Builder diagnostics (`FS1001` … `FS2002`, reported by `fluentsetup validate` / `build`) are a separate set of codes
  and are not covered here.

## 2. Logs

### 2.1 Location

| Case | Log file |
|---|---|
| Default (install) | `%TEMP%\FluentSetup\<AppId>-install-YYYYMMDD-HHMMSS.log` |
| Default (uninstall, including uninstall.exe) | `%TEMP%\FluentSetup\<AppId>-uninstall-YYYYMMDD-HHMMSS.log` |
| Header could not be read | `<AppId>` is replaced by `package` |
| `/log <file>` | That file. The path must be absolute on a local drive: its parent directory must pass the same syntax check as an install directory (drive-letter form, not a drive root, at most 200 characters, existing non-network drive) and the file name must be a valid name. Otherwise Setup.exe exits with 3. |

`%TEMP%` is the result of `GetTempPath2W` (falls back to `GetTempPathW`), which is `C:\Windows\SystemTemp` for SYSTEM
processes. The timestamp is local time at start-up. Missing parent directories are created.

The file is opened in append mode, so several processes of one session can write to it:

* the elevated worker receives `/log <same file>` and writes with the tag `worker`;
* `uninstall.exe` phase 1 logs the relocation and closes the log; phase 2 receives the user's original switches
  (including `/log`, if given) and otherwise computes its own default name (tag `uninstall`).

The WinUI front end does not write a log; its start-up failures reach the bootstrapper's log through `init_failed` and
its exit code. The "View log" link of both front ends opens the bootstrapper's log file.

### 2.2 Format

UTF-8 with BOM, CRLF line endings, one line per entry:

```
2026-11-12 10:31:02.417 [INFO ] [setup:12345] FluentSetup 1.0.0 (x64) started: "C:\Users\me\Downloads\Demo-Setup.exe"
```

`[LEVEL]` is `DEBUG`, `INFO `, `WARN ` or `ERROR`; the tag is `setup`, `worker` or `uninstall`, followed by the PID.

### 2.3 Useful lines

| Line (prefix) | Meaning |
|---|---|
| `Windows build …, elevated=…, signature=N (0x…)` | Signature state: 0 unsigned, 1 valid, 2 intact but untrusted, 3 tampered; plus the `WinVerifyTrust` result. |
| `Package validation failed: …` | Exit code 4, with the exact reason (e.g. "Header checksum mismatch", "Invalid package header: …"). |
| `Runtime probe: …` | Result of the Windows App Runtime check (section 4). |
| `WinUI front end ready after N ms` | WinUI handshake completed. |
| `Front end: WinUI` / `Front end: Win32 (requested)` / `Front end: falling back to Win32 (fresh\|resuming session): <reason>` | Which UI was used and why. Reasons include the UI's exit reason, e.g. "no compatible Windows App Runtime (MddBootstrapInitialize2: no match)". |
| `Requesting elevation for the worker` | Per-machine operation is handed to the elevated worker. |
| `State -> <State>` | Engine state transitions. |
| `Install failed: <detail> [<Code>]` / `Uninstall failed: …` | The failure and its code. |
| `Rollback: X undone, Y failed` | Result of rolling back. Y > 0 leaves the pending marker in place (section 3). |
| `Found an unfinished operation in … (N journal entries); recovering` / `Recovery rollback: …` | Recovery of an earlier interrupted operation. |
| `Exit code N` | Final exit code. |

## 3. Interrupted operations: journal and pending marker

Every install and uninstall is journaled so that a failure, a cancel, a crash or a power loss can be undone.

### 3.1 Files and registry

| Item | Location |
|---|---|
| Work directory (hidden) | `<InstallDir>\.fsetup\` |
| Journal | `<InstallDir>\.fsetup\journal.jsonl` – one compact JSON object per line |
| Backups of replaced files (install) | `<InstallDir>\.fsetup\b-<10 hex>\…` |
| Removed files (uninstall) | `<InstallDir>\.fsetup\t-<10 hex>\…` |
| New uninstaller before it is moved into place | `<InstallDir>\.fsetup\uninstall.new` |
| Pending marker | `HKCU\Software\FluentSetup\Pending\<AppId>` (per-user) or `HKLM\Software\FluentSetup\Pending\<AppId>` (per-machine), 64-bit registry view, value `InstallDir` (REG_SZ) |

Journal entries (`op`): `begin {mode, dir, scope, appId}`, `mkdir {path}`, `backup {from, to}`, `place {path}`,
`shortcut {path, backup?}`, `reg {snapshot}` (Apps & features entry before the change), `commit`. An entry is written
**before** the action it describes. `begin`, `shortcut`, `reg` and `commit` are flushed to disk immediately, other
entries at least every 64 entries and before the registration phase.

### 3.2 Lifecycle

1. In the *Preparing* phase, before the first change on disk, the engine writes the pending marker, recreates
   `.fsetup` and starts the journal.
2. Files are backed up / placed / removed with journal entries; then the uninstaller, shortcuts, install record and
   Apps & features entry are written (install), or the files, shortcuts, record and Apps & features entry are removed
   (uninstall).
3. `commit` is the point of no return. Afterwards `.fsetup` is deleted and the marker is cleared.
4. On failure or cancel before `commit`, the journal is rolled back newest-first. If every step was undone, `.fsetup`
   and the marker are removed (after a failed install, an install directory left empty is also deleted). If some steps
   could not be undone, the marker and the journal stay for the next run. A failure after `commit` is not rolled back.
5. If the process dies (crash, kill, power loss, a lost elevated worker), marker and journal stay as they are.

### 3.3 What happens on the next run

At the start of the next install or uninstall of the same app id in the same scope (after the initial checks such as
scope, elevation and downgrade, before any change), `Engine::RecoverPending` runs:

* It reads the directory from the marker and the journal from `<dir>\.fsetup\journal.jsonl` (up to 512 MiB; a torn
  last line and unparsable lines are skipped).
* If the first entry is a `begin` for this app id and this directory and there is no `commit`, the journal is rolled
  back. Rollback only acts on paths inside the install directory (or the scope's shortcut folders for shortcuts),
  restores backups only from inside `.fsetup`, and only restores the app's own Apps & features key; actions that never
  happened are skipped.
* If the rollback had no failures (or nothing needed rolling back), `.fsetup` is deleted; an empty install directory
  is removed.
* The marker is cleared in every case, so recovery is attempted once.

Then the requested operation continues normally.

To inspect a stuck state manually: check the marker value, read `journal.jsonl`, and look for the `Recovery rollback`
line in the next run's log.

## 4. Front-end selection (`/ui`) and the Windows App Runtime probe

### 4.1 Switch

| Command line | Behaviour |
|---|---|
| (none) or `/ui auto` | Try the WinUI front end; fall back to the Win32 front end if it cannot be used. |
| `/ui winui` | Currently identical to `auto` (the fallback still applies). |
| `/ui win32` | Use the Win32 front end directly. |
| `/quiet` (`/silent`, `/q`, `/s`) | No UI at all; `/ui` is irrelevant. |

The WinUI front end is only attempted if the package contains it (`frontend` in the header; the builder includes it
when `output.includeWinUI` is set and the WinUI runtime files exist for the architecture, otherwise warning `FS2002`).

### 4.2 How the decision is made

1. **Probe** (`runtime_probe.cpp`, in Setup.exe, no side effects):
   * Windows build must be ≥ 17763.
   * `GetPackagesByPackageFamily("Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe")` must return at least one package
     registered **for the current user**.
   * At least one of them must have the same processor architecture as Setup.exe (x64 build → AMD64, arm64 build →
     ARM64) and a version ≥ `2.5.1.0`.

   The family name and minimum version are compiled in from `WindowsAppSDK-VersionInfo.h` of the
   `Microsoft.WindowsAppSDK.Runtime` package the stub is built against (currently 2.5.1). If the probe fails, nothing
   is extracted and the Win32 front end is used ("fresh" fallback).
2. **Extraction**: the UI files are extracted to `%TEMP%\FluentSetup-<16 hex>` and verified; a failure falls back.
3. **Authoritative check** in `FluentSetup.UI.exe`: it loads `Microsoft.WindowsAppRuntime.Bootstrap.dll` from its own
   folder and calls `MddBootstrapInitialize2` for release 2.5 with the same minimum version, without the "install the
   runtime" dialog. "No match" → exit `0x22`, other failures → `0x23`; both fall back.
4. **Handshake**: the UI must connect within 10 s and report `ready` within 20 s, otherwise the bootstrapper falls
   back.
5. If the WinUI process stops later (crash, killed), the Win32 front end takes over the **same** session, showing the
   running operation or its result ("resuming session"). The WinUI front end is never restarted.

Test hook: `--ui-simulate runtime-missing|crash-before-ready|crash-after-ready|hang` exercises these fallback paths.

## 5. Uninstall specifics

* `uninstall.exe` cannot delete itself while running. It copies itself to a new private directory
  `%TEMP%\FluentSetup-u-<16 hex>\uninstall.exe`, runs the copy with `--phase2` and waits for its result. The copy
  deletes the original uninstaller (and the install directory if it ends up empty) after the original process exits.
  That temporary copy stays until the next uninstall, which removes stale copies first.
* Apps & features uses `"<InstallDir>\uninstall.exe" /uninstall` and, for silent removal,
  `"<InstallDir>\uninstall.exe" /uninstall /quiet`.
* Files that were not installed by setup stay in the install directory; the result page then says so
  (`result.leftovers`).

## 6. Common problems

### 6.1 The classic (Win32) window appears instead of the modern one

Not an error. The log line `Front end: falling back to Win32 (…)` gives the reason; typical ones:

| Reason in the log | Cause | What to do |
|---|---|---|
| `No package of family Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe is registered for this user` | Windows App Runtime 2.x not installed for this user. | Install the Windows App Runtime 2.5 (or later 2.x) for the machine's architecture, or ignore: the installation works the same in the Win32 window. |
| `No compatible runtime (need 2.5.1.0 for this architecture). Found: …` | Only older versions, or only other architectures, are installed. | Update the runtime. |
| `Windows build … is older than 17763` | Windows 10 before 1809. | – |
| `package has no WinUI front end` | The package was built without the WinUI front end. | Rebuild with `output.includeWinUI` and the WinUI runtime files present. |
| `UI exited before connecting: …` / `UI exited during start-up: …` / `UI initialization failed at stage '…' (HRESULT …)` | The UI process failed; the text after the colon is the decoded UI exit code. | Check the HRESULT; try `/ui win32` to confirm the installation itself works. |
| `UI did not become ready within the timeout` | XAML start took longer than 20 s (very slow or overloaded machine). | – |
| `UI stopped after start-up: …` | The WinUI window crashed after it was shown; the session continued in Win32. | Report with the log. |

### 6.2 SmartScreen, signatures and "damaged" packages

* SmartScreen and the "Unknown publisher" text in UAC prompts are decided by Windows before or outside Setup.exe;
  FluentSetup only influences them through Authenticode signing (`signing` settings of the project).
* A signature that is present but does not match the file (`TRUST_E_BAD_DIGEST`, `CRYPT_E_BAD_MSG`, other failures)
  makes Setup.exe refuse to run with exit code 4. A valid signature from an untrusted or expired certificate does not
  stop it (logged as `signature=2`).
* Any byte change of the header or of a stream is detected through SHA-256 even for unsigned packages (exit code 4,
  "This setup file is damaged or incomplete"). Typical causes: incomplete download, a modified file, or tools that
  rewrite the PE after the build. Download or build the file again; `fluentsetup inspect <Setup.exe> --verify` checks
  the header and stream hashes.
* `uninstall.exe` is generated on the target machine from the stub and is always unsigned, even when Setup.exe was
  signed. A per-machine uninstall therefore shows a UAC prompt for an unsigned program.

### 6.3 Install location rejected (`path.*`)

The front ends check the location while the user types (`checkPath`); the engine checks it again when the operation
starts. At start time `path.space` maps to exit code 7, `path.notWritable` to 9 and every other problem to 15, with the
detail "Install location rejected (path.<key>): <dir>".

| Key | Message (en-US) | When it is reported |
|---|---|---|
| `path.empty` | Enter an install location. | The field is empty (after trimming spaces and surrounding quotes). |
| `path.notAbsolute` | Enter a full path, for example C:\Apps\MyApp. | Not of the form `X:\…`. |
| `path.network` | Network locations aren't supported. Choose a folder on this PC. | UNC path (`\\server\…`) or a network drive. |
| `path.invalidChars` | The path contains characters that can't be used in folder names. | `\\?\` / `\\.\` prefixes or a component that is not a valid file name. |
| `path.reservedName` | The path contains a name that Windows reserves. | A component such as `CON`, `NUL`, `COM1`, `LPT1`. |
| `path.tooLong` | The path is too long. | More than 200 characters. |
| `path.root` | Choose a folder, not the root of a drive. | A drive root such as `D:\`. |
| `path.noDrive` | This drive isn't available. | The drive does not exist. |
| `path.protected` | This folder belongs to Windows or holds personal files. Choose a dedicated folder for the app. | Exactly a well-known folder (Program Files, Program Files (x86), Common Files, ProgramData, Users, the profile, Desktop, Public Desktop, Documents, Downloads, Pictures, Music, Videos, AppData\Local, AppData\Roaming, LocalLow, Programs, Start menu folders, Startup, Public), anything inside the Windows folder, or a parent of any of these. |
| `path.reparse` | The path goes through a link or junction. Choose a regular folder. | A symbolic link or junction somewhere in the path. |
| `path.locked` | The app is already installed. Its location can't be changed during an update. | The app is installed in a different directory. |
| `path.notEmpty` | This folder already contains files that setup would overwrite. … | The path is a file, or a non-empty folder that already contains one of the package's (non-preserved) files. |
| `path.otherApp` | Another app is already installed in this folder. | The folder contains an `uninstall.json` or `uninstall.exe` of another installation. |
| `path.notWritable` | You can't create files in this location without administrator permission. | Per-user installs only: creating a test folder fails with access denied. Choose a per-user location or install for everyone. |
| `path.space` | Not enough free space: {0} required, {1} available. | Free space is less than the required size (payload + uninstaller) plus 4 MiB. |

### 6.4 Other frequent situations

| Symptom | Explanation |
|---|---|
| "Setup for X is already running" (16) | Another Setup.exe or uninstall.exe of the same app id is running, possibly in another user session. |
| Silent per-machine install exits with 6 | `/quiet` never shows UAC; run it from an elevated process. |
| Elevation declined → 6 | The user answered "No" to the UAC prompt; nothing was changed. |
| "The elevated worker stopped unexpectedly …" (14) | The elevated process ended without reporting a result; its journal is rolled back on the next run (section 3). |
| Update refuses a different scope (3) | The app is installed for the other scope; uninstall it first. |
| A newer version is installed (10) | Downgrades are blocked unless the package sets `allowDowngrade`. |
| A `%TEMP%\FluentSetup-u-…` folder remains | The copy of uninstall.exe from the last uninstall (section 5). The next uninstall removes it; it is also safe to delete by hand when no uninstall is running. |
