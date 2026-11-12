# Project file reference (`fluentsetup.json`)

A FluentSetup project is one JSON file, usually named `fluentsetup.json`, that describes the app, the files to
install and how the generated `Setup.exe` behaves. The builder (GUI and `fluentsetup` CLI) reads it with these rules:

- Property names are camelCase. They are matched case-insensitively.
- `//` and `/* */` comments and trailing commas are accepted.
- Unknown properties are not an error. At the root and inside `app`, `files`, `install`, `ui`, `output` and `signing` they are kept when the builder saves the file. Everywhere else they are ignored.
- Every property is optional as far as the parser is concerned. The validator then requires `app.id`, `app.name` and `files.source`.
- Relative file paths (`files.source`, `app.icon`, `ui.license`, …) are resolved against the folder that contains the project file. Absolute paths are allowed.
- `schemaVersion` must be `1` or lower. A higher value is rejected.

JSON Schema: [`shared/schemas/fluentsetup.project.schema.json`](../shared/schemas/fluentsetup.project.schema.json)
(draft 2020-12). To get completion and validation in editors, add this line to the file:
`"$schema": "../../shared/schemas/fluentsetup.project.schema.json"`. The builder ignores it.

## Minimal example

```json
{
  "$schema": "../../shared/schemas/fluentsetup.project.schema.json",
  "schemaVersion": 1,
  "app": {
    "id": "Contoso.Notes",
    "name": "Contoso Notes",
    "publisher": "Contoso",
    "version": "1.2.0",
    "mainExecutable": "Notes.exe"
  },
  "files": {
    "source": "bin/Release/publish"
  }
}
```

Everything else takes the defaults below. With these defaults the app is installed per user to
`%LocalAppData%\Programs\Contoso Notes`. You get a Start menu shortcut and an optional, unchecked desktop shortcut.
The installer offers to launch the app at the end, and the output is `dist\Contoso Notes-1.2.0-Setup.exe`.

`fluentsetup init` writes a complete file with every property set to its default value.

## Root

| Property | Type | Default | Meaning |
|---|---|---|---|
| `$schema` | string | – | Editor hint. Ignored by the builder. |
| `schemaVersion` | integer | `1` | Format version. Values greater than 1 are rejected. |
| `app` | object | | [App identity](#app) |
| `files` | object | | [Files to install](#files) |
| `install` | object | | [Install location and behaviour](#install) |
| `requirements` | object | | [Required Windows components](#requirements) |
| `options` | array | `[]` | [User-selectable options](#options) |
| `actions` | array | `[]` | [Programs to run](#actions) |
| `uninstall` | object | | [Extra uninstall cleanup](#uninstall) |
| `ui` | object | | [Installer look and text](#ui) |
| `output` | object | | [Output file](#output) |
| `signing` | object | | [Code signing](#signing) |

## app

| Property | Type | Default | Meaning |
|---|---|---|---|
| `id` | string | `""` | **Required.** Stable identifier, e.g. `Contoso.Notes`. Allowed characters are ASCII letters, digits, `.`, `-` and `_` (max 64). It must not start or end with `.`. It is the name of the uninstall registry key, so keep it the same across versions. |
| `name` | string | `""` | **Required**, max 128. Display name. Also used for `{AppName}`, shortcut names and the Setup.exe file name. |
| `publisher` | string | `""` | Max 128. Shown in Windows Settings. Leaving it empty gives a warning. |
| `version` | string | `"1.0.0"` | 1–4 dot-separated numbers, optionally followed by `-tag`, e.g. `2.0.0-beta.1`. Max 64 characters. See [upgrades](#upgrades-and-downgrades). |
| `description` | string | `""` | Max 1024. Shown in Windows Settings and used as the shortcut comment. |
| `website` | string | `""` | Absolute `http://` or `https://` URL, or empty. |
| `icon` | string | `""` | `.ico` or `.png` file (up to 256×256) for Setup.exe and the installer window. Empty means the default FluentSetup icon is used. |
| `mainExecutable` | string | `""` | Relative path of the app's `.exe` inside the installed files. It must be one of those files. Shortcuts, "launch after install" and the Settings icon use it. When it is empty, no shortcuts are created and the app is not launched after install (you get a warning). |

## files

| Property | Type | Default | Meaning |
|---|---|---|---|
| `source` | string | `""` | **Required.** Folder whose contents are installed. |
| `include` | string[] | `["**/*"]` | Globs a file under `source` must match to be installed. |
| `exclude` | string[] | `["**/*.pdb", "**/*.ilk", "**/*.obj", "**/.git/**", "**/Thumbs.db", "**/.DS_Store"]` | Globs of files and folders to leave out. If you set this property, your list **replaces** the defaults. |
| `preserve` | string[] | `[]` | Globs of user-data files. See [preserve](#preserve). |
| `extra` | object[] | `[]` | Additional files or folders from elsewhere: `{ "source": "...", "target": "sub/folder" }`. |
| `extra[].source` | string | `""` | File or folder. A file is installed as `<target>\<file name>`. For a folder, its contents are installed under `<target>` **without** include/exclude filtering. |
| `extra[].target` | string | `""` | Relative target folder. Empty means the install folder itself. |
| `emptyDirectories` | string[] | `[]` | Relative folders to create even if they stay empty. They are removed on uninstall if they are still empty. |

### Glob syntax

The implementation is in `src/Builder.Core/Packaging/Glob.cs`.

- Matching is case-insensitive. `/` and `\` are both accepted as separators.
- `*` matches any characters within one folder level. `**` matches any number of levels. `?` matches one character other than a separator. All other characters match literally. There are no `[...]` classes or `{a,b}` alternatives.
- A pattern **without** a separator matches the file name in any folder: `*.pdb` behaves like `**/*.pdb`, and `THRM.exe` matches `THRM.exe` in every folder.
- A pattern **with** a separator is anchored at `files.source`: `bridge/**` matches everything below `source\bridge`.
- A pattern ending in `/` matches everything below that folder (`logs/` behaves like `logs/**`).
- Leading `./` is ignored.
- Patterns must not be blank, rooted (`/x`, `C:x`) or contain `..` (error FS1106).

How the rules are applied (`FileCollector.cs`):

1. The collector walks `files.source`. A folder is skipped entirely if an `exclude` glob matches its relative path followed by `/`. So `**/.git/**` or `cache/` excludes the whole folder, but a bare `cache` only excludes *files* named `cache`.
2. A file is installed when it matches at least one `include` glob and no `exclude` glob.
3. `extra` sources are added after that, unfiltered.
4. Symbolic links and junctions are never followed. They are skipped with warning FS1112.
5. If the output folder lies inside the source, it is skipped.
6. Errors:
   - Two sources map to the same target path (FS1109).
   - A path is both a file and a folder (FS1114).
   - A path is not a valid relative path (FS1107).
   - A path is reserved by the installer: `uninstall.exe`, `uninstall.json` or anything under `.fsetup` (FS1108).
   - No files are left to install (FS1115).
7. Read-only and hidden attributes are kept.

### preserve

`preserve` globs are matched against each file's final relative path, including files from `extra`. They use the same
syntax as above. A preserved file:

- is installed only if no file exists at that path. An existing copy is kept, though the packaged copy is still verified.
- is never overwritten on update. It stays in place even when a later version no longer ships it.
- is not removed on uninstall.

## install

| Property | Type | Default | Meaning |
|---|---|---|---|
| `scope` | `user` \| `machine` \| `userOrMachine` | `"user"` | See [scopes](#install-scopes). |
| `defaultScope` | `user` \| `machine` | `"user"` | Pre-selected scope. Used only when `scope` is `userOrMachine`. |
| `userDir` | string | `"{LocalAppData}\\Programs\\{AppName}"` | Folder template for per-user installs. Must not contain `{ProgramFiles}` when `scope` is `user` or `userOrMachine` (FS1203). |
| `machineDir` | string | `"{ProgramFiles}\\{AppName}"` | Folder template for per-machine installs. Must not contain `{LocalAppData}` or `{AppData}` when `scope` is `machine` or `userOrMachine` (FS1204). |
| `allowCustomDir` | bool | `true` | Lets the user (or `/dir`) choose another folder. This is never possible when updating an existing installation. |
| `desktopShortcut` | offer | `"optionalOff"` | Desktop shortcut to `app.mainExecutable`. |
| `startMenuShortcut` | offer | `"on"` | Start menu shortcut, named after `app.name`. |
| `launchAfterInstall` | `off` \| `optionalOff` \| `optionalOn` | `"optionalOn"` | "Launch" choice on the finish page. `on` is rejected (FS1206). The app is never launched in quiet mode. |
| `allowDowngrade` | bool | `false` | Allows installing over a newer installed version. |
| `minWindowsBuild` | integer | `17763` | 0–100000. On an older build Setup.exe exits with code 5. A value of 0 disables the check. Values from 1 to 17762 give a warning, because FluentSetup itself needs build 17763. |
| `closeProcesses` | string[] | `[]` | Up to 32 bare `.exe` names, e.g. `"MyApp.exe"`. Before install and before uninstall these processes get `WM_CLOSE` and are terminated if still running after a grace period. |
| `adoptExistingInstall` | bool | `false` | Takes over an installation made by a previous, non-FluentSetup installer that registered the uninstall key named `app.id`. Its folder and version count as the existing installation. Files of the old version that the new one does not ship are left in place, because no FluentSetup install record exists for them. |

The values `off`, `on`, `optionalOff` and `optionalOn` mean:

| Value | Meaning |
|---|---|
| `off` | Never. |
| `on` | Always, with no checkbox. |
| `optionalOff` | Shown as a checkbox, unchecked by default. |
| `optionalOn` | Shown as a checkbox, checked by default. |

Without `app.mainExecutable` all three are forced to `off`.

### Path templates

`userDir` and `machineDir` must start with a placeholder or be an absolute path such as `D:\Apps\X` (FS1212). These
placeholders are recognised. They are case-sensitive, and any other `{...}` is error FS1211.

| Placeholder | Expands to |
|---|---|
| `{LocalAppData}` | `%LocalAppData%` |
| `{UserProgramFiles}` | `%LocalAppData%\Programs` (FOLDERID_UserProgramFiles) |
| `{AppData}` | Roaming `%AppData%` |
| `{ProgramFiles}` | Native `Program Files` |
| `{ProgramData}` | `%ProgramData%` |
| `{AppName}` | `app.name`, made file-name safe |
| `{Publisher}` | `app.publisher` (or `app.name` if empty), made file-name safe |
| `{AppId}` | `app.id` |

Other fields use their own placeholder sets:

- `output.fileName`: `{AppName}`, `{AppId}`, `{Version}` (each with invalid file-name characters removed) and `{Architecture}` (`x64` or `arm64`).
- `uninstall.removeData`: `{CommonAppData}` (= ProgramData), `{LocalAppData}` and `{AppData}`, only as the first segment.

### Install scopes

| `scope` | Behaviour |
|---|---|
| `user` | Per-user install into `userDir` without administrator rights. Registered under HKCU. |
| `machine` | Install for all users into `machineDir`. Requires elevation and the UI asks for it. Registered under HKLM. A custom folder outside Program Files gets a protected ACL. |
| `userOrMachine` | The user picks. `defaultScope` is pre-selected, and `/scope` sets it from the command line. |

When the app is already installed, the existing scope and folder are always reused. Installing into the other scope
fails until the app is uninstalled. A per-machine install in `/quiet` mode must be started from an elevated process,
because quiet mode never shows the UAC prompt. Otherwise Setup.exe exits with code 6.

### Upgrades and downgrades

The installed version is found by `app.id`. Versions are compared numerically, part by part (`1.10 > 1.9`). Missing
parts count as 0. A release is newer than its pre-releases (`2.0.0 > 2.0.0-rc.1`), and pre-release tags are compared
by their dot-separated parts.

- **Newer package:** the installer updates in place. Files the new version no longer ships are removed, except preserved files. Shortcuts that are no longer wanted are removed.
- **Same version:** the installer offers a reinstall.
- **Older package:** blocked with exit code 10 (`DowngradeBlocked`) unless `allowDowngrade` is `true`.
- **Unparseable installed version:** treated as an update.

The whole operation is journaled. Any failure before the final commit rolls back to the previous state, and that
includes a failing `afterInstall` action with `onFailure: "fail"`.

## requirements

These are checked before anything is changed.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `netFramework` | string | `""` | Minimum .NET Framework 4.x: one of `4.5`, `4.5.1`, `4.5.2`, `4.6`, `4.6.1`, `4.6.2`, `4.7`, `4.7.1`, `4.7.2`, `4.8`, `4.8.1`. Empty means not required. If it is missing, setup stops with exit code 17 (`PrerequisiteMissing`). |
| `webView2.required` | bool | `false` | The app needs the Microsoft Edge WebView2 Evergreen runtime. |
| `webView2.installer` | string | `""` | Optional bundled runtime installer, given as a relative path of a file **among the installed files**, typically added through `files.extra`. If the runtime is missing, this installer runs after the files are placed (600 s limit). Its failure is only a warning. Without it, a missing runtime stops setup with exit code 17. |
| `webView2.arguments` | string[] | `["/silent", "/install"]` | Arguments for the bundled installer. |

## options

These are checkboxes on the installer's options page. Actions can depend on them. There can be at most 16.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `id` | string | `""` | **Required.** Unique id: letters, digits, `-` and `_` (max 32). |
| `label` | object | `{}` | **Required.** Text per language, e.g. `{ "en-US": "Start when I sign in", "zh-CN": "登录时启动" }`. At least one entry is needed and none may be blank. A plain string is not accepted. |
| `default` | bool | `false` | Initial state. Quiet installs use this value unless `/option id=yes\|no` is given. |

## actions

Actions run programs from the app's own files after install or before uninstall. There can be at most 32.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `when` | `afterInstall` \| `beforeUninstall` | `"afterInstall"` | `afterInstall` runs after files, shortcuts and registration, before the install is committed. `beforeUninstall` runs before any file is removed. |
| `run` | string | `""` | **Required.** Relative path of an `.exe` that is one of the installed files. |
| `arguments` | string[] | `[]` | At most 32 arguments, each up to 1024 characters. Each is passed as one quoted argument. |
| `timeoutSeconds` | integer | `300` | 1–3600. When it expires, the program and all its child processes are terminated and the step fails. |
| `successExitCodes` | integer[] | `[0]` | Exit codes that count as success. |
| `onFailure` | `fail` \| `warn` \| `ignore` | `"fail"` | `fail` aborts: after install the whole installation is rolled back, and before uninstall the uninstall is aborted. `warn` continues and lists the failure on the result page. `ignore` continues and only logs it. |
| `ifOption` | string | `""` | Runs only if the option with this id is selected. The id must exist in `options` (FS1625). |
| `skipIfInstalled` | object \| null | `null` | Skips the step if a registry value already holds at least this version. |
| `skipIfInstalled.hive` | `HKLM` \| `HKCU` | `"HKLM"` | Registry hive. |
| `skipIfInstalled.key` | string | `""` | Key below the hive, with no leading `\` (max 255). The 64-bit and 32-bit views are both checked. |
| `skipIfInstalled.value` | string | `"DisplayVersion"` | Name of a `REG_SZ` value. |
| `skipIfInstalled.version` | string | `""` | Required version, using the same format as `app.version`. |
| `name` | object | `{}` | Step name per language, used in the log and in failure messages. If empty, the file name is used. |

### Security rules

These rules are enforced by the builder and again by the installer engine, which re-validates the package header.

- Actions can **only** run an `.exe` that is part of the package's own payload. The path is relative, inside the install folder, and not reached through a link. System tools, scripts, `cmd.exe`, URLs and absolute paths are impossible.
- Processes are started directly with `CreateProcess`, with no shell. They run hidden, with the program's folder as the working directory, inside a job object so a timeout ends the whole process tree.
- A step that fails (failed to start, timed out, or exited with a code not in `successExitCodes`) is handled according to `onFailure`. With `fail` after install, setup rolls back its *own* changes. Anything the program itself did, such as installing a driver or registering a task, cannot be undone (info FS1627).
- `ifOption` is evaluated against the user's choice. In quiet mode it uses the option's `default` or `/option`.
- `skipIfInstalled` is checked right before the step runs.

## uninstall

These are extra cleanup steps for things the app itself created. They run after the uninstall is committed, and their
failures are only logged.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `removeData` | string[] | `[]` | Folders deleted recursively. Each must be `{CommonAppData}\<path>`, `{LocalAppData}\<path>` or `{AppData}\<path>`, where `<path>` is a valid relative path. Only existing directories that are not links are removed. |
| `removeScheduledTasks` | string[] | `[]` | Task names in the Task Scheduler root folder (max 128 characters, no `\/:*?"<>\|`). |
| `removeRunValues` | string[] | `[]` | Value names deleted from `HKCU` and `HKLM` `Software\Microsoft\Windows\CurrentVersion\Run`. Same name rules as for tasks. |

The installed files and shortcuts, the app's uninstall registration and the install folder (if it is empty) are always
removed. Preserved files and files the installer did not create are kept.

## ui

| Property | Type | Default | Meaning |
|---|---|---|---|
| `title` | string | `""` | Custom heading on the welcome page (max 128). |
| `welcomeText` | string | `""` | Custom text on the welcome page (max 2048). |
| `finishText` | string | `""` | Custom text on the finish page after installing (max 2048). |
| `theme` | `system` \| `light` \| `dark` | `"system"` | Colour theme. |
| `backdrop` | `mica` \| `micaAlt` \| `none` | `"mica"` | Window material of the WinUI front end. |
| `useSystemAccent` | bool | `true` | Use the Windows accent colour. |
| `accentColor` | string | `""` | `#RRGGBB`. Required when `useSystemAccent` is `false` (FS1303). |
| `license` | string | `""` | Plain-text license file (max 1 MB). If set, a **first-time** install shows it and the user must accept it. |
| `licenses` | object | `{}` | Per-language license files, e.g. `{ "zh-CN": "license_zh.txt" }`. Only `en-US` and `zh-CN` are used. Falls back to `license`. |
| `defaultLanguage` | `auto` \| `en-US` \| `zh-CN` | `"auto"` | `auto` follows the user's Windows display languages: `zh*` gives zh-CN, `en*` gives en-US, anything else gives en-US. `/lang` overrides this setting. |
| `strings` | object | `{}` | Per-language overrides of built-in strings, e.g. `{ "zh-CN": { "welcome.body": "…" } }`. Keys are listed in `shared/localization/en-US.json`. Only `en-US` and `zh-CN` are used (other languages give warning FS1307). Values longer than 4096 bytes are ignored. |

## output

| Property | Type | Default | Meaning |
|---|---|---|---|
| `directory` | string | `"dist"` | Output folder. `build -o` overrides it. |
| `fileName` | string | `"{AppName}-{Version}-Setup.exe"` | Must expand to a valid file name ending in `.exe`. Placeholders: `{AppName}`, `{AppId}`, `{Version}`, `{Architecture}`. |
| `architecture` | `x64` \| `arm64` | `"x64"` | Target architecture. `build --arch` overrides it. |
| `compression` | `fast` \| `normal` \| `max` | `"normal"` | Zstandard level: `fast` is 3, `normal` is 12, `max` is 19 with long-distance matching. |
| `includeWinUI` | bool | `true` | Embeds the WinUI 3 front end. Setup.exe falls back to the built-in Win32 UI where WinUI cannot run. `false` gives a smaller Win32-only installer. |

## signing

Signing uses `signtool.exe` with a SHA-256 file digest.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `false` | Sign Setup.exe. `build --sign` and `build --no-sign` override it. |
| `certificateThumbprint` | string | `""` | SHA-1 thumbprint of a certificate in the certificate store (spaces are removed). Takes precedence over `certificateFile`. |
| `certificateFile` | string | `""` | `.pfx` file. |
| `passwordEnvironmentVariable` | string | `""` | Name of an environment variable that holds the PFX password. The password is never stored in the project or written to the log. |
| `timestampUrl` | string | `"http://timestamp.digicert.com"` | RFC 3161 timestamp server. Empty means no timestamp. |
| `signToolPath` | string | `""` | Path of `signtool.exe`. Empty means the Windows 10/11 SDK and `PATH` are searched. |

When `enabled` is true, a thumbprint or a certificate file is required (FS1501).

## CLI: `fluentsetup`

```
fluentsetup build <project.json> [options]
    -o, --output <dir>      Output folder (default: output.directory)
    --arch x64|arm64        Target architecture (default: output.architecture)
    --sign | --no-sign      Override signing.enabled
    --report <file.json>    Also write a machine-readable build report
    --runtime <dir>         Use another installer runtime folder
    -v, --verbose           Detailed log (also prints info diagnostics)
    --json                  Print the build report as JSON instead of text
    --non-interactive       Accepted for CI scripts (the CLI never prompts)
fluentsetup validate <project.json> [--json]
fluentsetup inspect <Setup.exe> [--verify] [--json]
fluentsetup init [folder] --name <name> --id <app.id> --source <files folder> [--publisher <name>]
fluentsetup --version | --help
```

- `validate` runs all project checks and collects the files, the same steps `build` performs before compressing, and prints diagnostics as `severity CODE [field]: message`. With `--json` it prints `{ valid, files, bytes, diagnostics }`.
- `inspect` prints the header summary, sizes and signature state of a built Setup.exe. `--verify` also checks every stream's SHA-256.
- `init` creates `<folder>\fluentsetup.json` (default folder: the current one) and refuses to overwrite an existing file. `mainExecutable` is set to the first `*.exe` found directly in the source folder.
- Options accept `--name value` or `--name=value`. Unknown or repeated options are usage errors.
- Exit codes: `0` success, `1` validation or build failure, `2` usage error, `3` unexpected error, `4` cancelled.

## Setup.exe command line

Switches can be written `/x`, `-x` or `--x` and are case-insensitive. A value follows as the next argument or after
`=`, for example `/dir=C:\Apps\X`. Unknown switches and invalid values are errors (exit code 3) and are never ignored.
The installed `uninstall.exe` accepts the same switches.

| Switch | Meaning |
|---|---|
| `/quiet`, `/silent`, `/q`, `/s` | No UI at all. Uses the defaults plus the switches below. Per-machine operations need an already elevated process. |
| `/uninstall`, `/u` | Remove the installed app instead of installing it. |
| `/dir <path>`, `/installdir <path>` | Install folder. Honoured only if `allowCustomDir` is true and the app is not yet installed. The engine validates it. |
| `/scope user\|machine` | Scope. Honoured only if the package allows that scope and the app is not yet installed. |
| `/desktop-shortcut yes\|no` | Desktop shortcut choice. Honoured only for `optionalOff`/`optionalOn`. |
| `/start-menu-shortcut yes\|no` | Start menu shortcut choice. Honoured only for `optionalOff`/`optionalOn`. |
| `/option <id>=yes\|no` | Value of a package option. Can be repeated. Ids the package does not define are ignored. |
| `/lang en-US\|zh-CN` | UI language. Overrides `ui.defaultLanguage`. |
| `/log <file>` | Write the log to this absolute file path. Default: `%TEMP%\FluentSetup\<appId>-install-<timestamp>.log` (or `-uninstall-`). |
| `/ui auto\|winui\|win32` | Front-end selection, for diagnostics. `win32` skips the WinUI front end. |
| `/help`, `/h`, `/?` | Show usage. |

`yes|no` also accepts `true`/`false` and `1`/`0`. The switches `--worker`, `--pipe`, `--parent-pid`, `--wait-pid`,
`--origin-dir`, `--phase2`, `--origin-process`, `--result-pipe` and `--ui-simulate` are used internally between
FluentSetup processes.

Exit codes (from `src/Common/error.h`):

| Code | Name | Meaning |
|---|---|---|
| 0 | Ok | Success |
| 1 | Failed | Unclassified failure, including a failed action with `onFailure: "fail"` |
| 2 | Cancelled | The user cancelled and the changes were rolled back |
| 3 | InvalidArguments | Bad command line or option value |
| 4 | PackageCorrupt | The package failed validation |
| 5 | UnsupportedSystem | Windows is older than `install.minWindowsBuild` |
| 6 | ElevationRequired | Administrator rights are needed and were not granted |
| 7 | DiskFull | Not enough disk space |
| 8 | FileInUse | A file is in use |
| 9 | AccessDenied | Access was denied |
| 10 | DowngradeBlocked | A newer version is installed and `allowDowngrade` is false |
| 11 | NotInstalled | Uninstall was requested but the app is not installed |
| 12 | IoError | I/O error |
| 13 | RegistryError | Registry error |
| 14 | Internal | Internal error |
| 15 | UnsafePath | Unsafe path |
| 16 | AlreadyRunning | Another setup or uninstall of the same app is in progress |
| 17 | PrerequisiteMissing | A required Windows component is missing |
