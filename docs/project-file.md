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
| `variables` | object | `{}` | [Values for `$(Name)` placeholders](#variables). |
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

## variables

Any string in the project can use `$(Name)` placeholders. Their values come from `variables` and, with priority, from
the command line: `fluentsetup build -D Version=1.4.2 -D Channel=stable` (also `validate`). A release pipeline can
stamp the version, or the version of a bundled driver, without rewriting the project file.

```json
"variables": { "Version": "1.4.1", "PawnIOVersion": "2.2.0.0" },
"app": { "version": "$(Version)" },
"actions": [{ "run": "drivers/PawnIO_setup.exe", "skipIfInstalled": { "key": "...", "version": "$(PawnIOVersion)" } }]
```

- Names: letters, digits, `_`, `.` and `-`, starting with a letter or `_` (max 64), matched case-insensitively.
- An unknown name is error FS1801. `$$(` writes a literal `$(`.
- The Builder app shows and saves the raw text and validates with the project's own `variables`.

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

### Variants (self-contained or shared runtimes)

`files.variants` puts several builds of the same app into one package, for example a WinUI 3 app published
framework-dependent (`shared`: needs .NET and the Windows App Runtime) and self-contained (`selfContained`: carries
both). Each variant names its own `source` folder; `files.include`, `exclude` and `preserve` apply to every variant
and `files.extra` is added to every variant. With variants, `files.source` is not used.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `id` | string | | **Required.** Letters, digits, `-`, `_` (max 32). |
| `label` | object | `{}` | Text per language (or in the localization folder). Shown as a radio button with the variant's size. |
| `description` | object | `{}` | One line shown below the choice. |
| `default` | bool | `false` | Pre-selected variant (else the first). |
| `source` | string | | **Required.** Folder with this build (e.g. `dotnet publish -o …`). |

How it works:

- **Build time.** The builder collects every variant. Files with the same path and content in all variants are stored
  once as shared files; the rest is stored per variant in separate streams, so installing one variant never
  decompresses the other's data. `output.variants` (or `fluentsetup build --variant id[,id]`) limits which variants go
  into a package: with one variant the package has no choice at all.
- **Install time.** The first page shows a *Runtime* choice when the package has more than one variant;
  `Setup.exe /variant <id>` selects one from the command line (also in quiet mode). An update keeps the variant that is
  installed unless another one is chosen. Switching variants removes the files the new choice does not need.
- **Prerequisites per variant.** `requirements.*.variants` limits a prerequisite to some variants, e.g. the .NET runtime
  and the Windows App Runtime only for `shared`. The notice on the first page follows the selected variant.

```json
"files": {
  "include": ["**/*"], "exclude": ["**/*.pdb"],
  "variants": [
    { "id": "selfContained", "label": { "en-US": "Self-contained (includes runtimes)" }, "default": true, "source": "publish/sc" },
    { "id": "shared", "label": { "en-US": "Shared runtimes (smaller)" }, "source": "publish/fd" }
  ]
},
"requirements": {
  "dotNet": [{ "runtime": "core", "version": "10.0.0", "variants": ["shared"], "download": { "url": "https://…", "sha256": "…" } }],
  "windowsAppRuntime": { "required": true, "version": "2.5.1", "variants": ["shared"], "download": { "url": "https://…", "sha256": "…" } }
}
```

### Glob syntax

The implementation is in `src/Builder.Core/Packaging/Glob.cs`.

- Matching is case-insensitive. `/` and `\` are both accepted as separators.
- `*` matches any characters within one folder level. `**` matches any number of levels. `?` matches one character other than a separator. All other characters match literally. There are no `[...]` classes or `{a,b}` alternatives.
- A pattern **without** a separator matches the file name in any folder: `*.pdb` behaves like `**/*.pdb`, and `THRM.exe` matches `THRM.exe` in every folder, including stray copies such as `old/THRM.exe`.
- A pattern **with** a separator is anchored at `files.source`: `bridge/**` matches everything below `source\bridge`.
- A pattern ending in `/` matches everything below that folder (`logs/` behaves like `logs/**`).
- A leading `./` anchors the pattern at `files.source`: `./THRM.exe` matches only the top-level `THRM.exe`. Use it in `include` for the app's main files, so copies in subfolders of the build output are not picked up.
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
| `startMenuFolder` | string | `""` | Start menu folder for that shortcut, e.g. `"Contoso"` gives `Programs\Contoso\<App>.lnk` (the "program group" many Inno Setup and NSIS scripts create). Empty means directly in Programs. The folder is removed on uninstall once it is empty. |
| `launchAfterInstall` | `off` \| `optionalOff` \| `optionalOn` | `"optionalOn"` | "Launch" choice on the finish page. `on` is rejected (FS1206). The app is never launched in quiet mode. |
| `allowDowngrade` | bool | `false` | Allows installing over a newer installed version. |
| `minWindowsBuild` | integer | `17763` | 0–100000. On an older build Setup.exe exits with code 5. A value of 0 disables the check. Values from 1 to 17762 give a warning, because FluentSetup itself needs build 17763. |
| `closeProcesses` | string[] | `[]` | Up to 32 bare `.exe` names, e.g. `"MyApp.exe"`. Before install and before uninstall these processes get `WM_CLOSE` and are terminated if still running after a grace period. |
| `adoptExistingInstall` | bool | `false` | Takes over an installation made by a previous, non-FluentSetup installer that registered the uninstall key named `app.id`. Its folder and version count as the existing installation. Files of the old version that the new one does not ship are left in place, because no FluentSetup install record exists for them. |
| `legacyUninstallKeys` | string[] | `[]` | Uninstall key names of **older product names** (e.g. before a rename). If the app is not installed under its own id, the first of these keys that points at an existing folder is adopted the same way (folder from `InstallLocation`, else from `UninstallString` or `DisplayIcon`). After a successful install these keys are deleted. Keys written by FluentSetup are never touched; the app's own id is not allowed here (FS1650). |
| `ngen` | string[] | `[]` | .NET Framework assemblies among the installed files (`.exe`/`.dll`, max 16). After install they are queued with `ngen install /queue` (compiled when the PC is idle); before an upgrade replaces them and before uninstall, `ngen uninstall` drops the old native images. Only in elevated (per-machine) installs; failures are only logged. |
| `addToPath` | string[] | `[]` | Folders of the app put on PATH (relative; `""` = the install folder): the user PATH for per-user installs, the machine PATH for per-machine. Added after the install is committed, removed on uninstall; new terminals see it. |
| `relaunchArguments` | string[] | `[]` | Arguments for the app when an in-app updater ran `Setup.exe /relaunch` (Inno `/RELAUNCH=1`) in quiet mode, e.g. `["--updated"]`. |
| `closeCommand` | object \| null | `null` | `{ "run": "App.exe", "arguments": ["--exit"] }`: asks a running copy of the **installed** app to exit cleanly (e.g. a tray app that only hides on WM_CLOSE) before files are replaced or removed; `closeProcesses` then handles anything still running. |
| `cleanup.removeScheduledTasks` | string[] | `[]` | Leftovers of earlier versions removed **during installation**, after the files are in place and before `afterInstall` actions run (so an action can register its replacement). Task names in the root folder. |
| `cleanup.removeRunValues` | string[] | `[]` | `...\CurrentVersion\Run` value names (HKCU and HKLM). |
| `cleanup.removeShortcuts` | string[] | `[]` | Shortcut file names (`.lnk`) deleted from the per-user and all-users Start menu Programs, Desktop and Startup folders. `"Folder\\Old.lnk"` reaches into a Start menu folder, which is removed once it is empty. Never list the app's own shortcut names here. |
| `cleanup.removeFiles` | string[] | `[]` | Files in the install folder that no install record knows about, typically the uninstaller of an adopted Inno Setup install (`"unins*.exe"`, `"unins*.dat"`, `"unins*.msg"`). Relative paths; `*` and `?` only in the file name, which needs at least one literal character. Removed after the new files are placed, journaled like every other change (a rollback restores them). Files of the package are never touched. |
| `cleanup.removeUninstallKeys` | string[] | `[]` | Uninstall entries of other product names, deleted after the install is committed (not this app's id). |

The `cleanup` steps are not undone by a rollback.

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

Prerequisites are checked before anything is changed. A missing (or too old) one is listed on the first page of the
installer together with what setup will do about it.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `netFramework` | string | `""` | Minimum .NET Framework 4.x: one of `4.5`, `4.5.1`, `4.5.2`, `4.6`, `4.6.1`, `4.6.2`, `4.7`, `4.7.1`, `4.7.2`, `4.8`, `4.8.1`. Empty means not required. |
| `netFrameworkSetup` | source | `null` | Where to get .NET Framework when it is missing (see *Prerequisite sources*). |
| `webView2.required` | bool | `false` | The app needs the Microsoft Edge WebView2 Evergreen runtime (machine-wide or per-user registration). |
| `webView2.minVersion` | string | `""` | Minimum runtime version, e.g. `"120.0.2210.91"`. An older runtime counts as missing: setup says it is too old and runs the installer to update it. Combine with `onFailure: "fail"` to refuse to install the app on an outdated runtime. |
| `webView2.*` | source | | `installer`, `arguments` (default `["/silent", "/install"]`), `download`, `helpUrl`, `onFailure`. |
| `windowsAppRuntime.required` | bool | `false` | The app needs the Windows App SDK runtime (framework-dependent WinUI 3 apps). Detected per user and architecture through its framework package. |
| `windowsAppRuntime.version` | string | `""` | Windows App SDK version: `2.5.1` means package family `Microsoft.WindowsAppRuntime.2` at version 2.5.1 or newer; `1.6` means any `Microsoft.WindowsAppRuntime.1.6`. |
| `windowsAppRuntime.*` | source | | `installer`, `arguments` (default `["--quiet"]`), `download`, `helpUrl`, `onFailure`, `variants`. Microsoft's installer is `WindowsAppRuntimeInstall-<arch>.exe`. |
| `*.variants` | string[] | `[]` | On any prerequisite: only needed for these variants. |
| `dotNet[]` | object[] | `[]` | .NET (Core) shared runtimes, max 8. |
| `dotNet[].runtime` | `desktop` \| `core` \| `aspnetcore` | `"desktop"` | `Microsoft.WindowsDesktop.App`, `Microsoft.NETCore.App` or `Microsoft.AspNetCore.App`, found under `%ProgramFiles%\dotnet\shared` (or the registered install location) for the package's architecture. |
| `dotNet[].version` | string | | **Required.** Minimum version, e.g. `"8.0.0"`. |
| `dotNet[].rollForward` | `patch` \| `minor` \| `major` | `"minor"` | Which newer versions count: same `major.minor`, same major (the .NET default), or any newer one. |
| `dotNet[].*` | source | | `installer`, `arguments` (default `["/install", "/quiet", "/norestart"]`), `download`, `helpUrl`, `onFailure`. |

### Prerequisite sources

| Property | Type | Default | Meaning |
|---|---|---|---|
| `installer` | string | `""` | Installer **among the installed files** (add it with `files.extra`). Runs after the app's files are placed, visible, up to 30 minutes. |
| `arguments` | string[] | see above | Installer arguments. |
| `download` | object | `null` | `{ "url": "https://…", "sha256": "<64 hex>", "size": <bytes or 0> }`. Used when no installer is bundled. Downloaded **before anything is changed** (progress is shown), over TLS 1.2+, https only (no redirect to http), into a private temporary folder, size-capped (exact `size`, else 512 MiB) and verified against `sha256` before it runs. A mismatch or network error stops setup with exit code 17 and nothing installed. |
| `helpUrl` | string | `""` | https link shown when the prerequisite is missing and setup cannot install it. |
| `onFailure` | `warn` \| `fail` | `"warn"` | After the installer ran, the prerequisite is checked again. `warn`: the app stays installed and the result page lists the problem. `fail`: the whole installation is rolled back (exit code 17). |

With neither `installer` nor `download`, a missing prerequisite stops setup before anything is changed (exit code 17,
`PrerequisiteMissing`, with `helpUrl` in the message).

## options

These are checkboxes on the installer's options page. Actions can depend on them. There can be at most 16.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `id` | string | `""` | **Required.** Unique id: letters, digits, `-` and `_` (max 32). |
| `label` | object | `{}` | **Required.** Text per language, e.g. `{ "en-US": "Start when I sign in", "zh-CN": "登录时启动" }`. At least one entry is needed and none may be blank. A plain string is not accepted. |
| `default` | bool | `false` | Initial state on a first install. Quiet and passive installs use it unless `quietDefault` or `/option id=yes\|no` says otherwise. |
| `quietDefault` | bool \| null | `null` | Value in quiet and passive **first** installs, e.g. `false` for an "Enable now" checkbox that is checked in the UI but must not act in scripted installs (Inno Setup `skipifsilent`). |
| `detect` | object \| null | `null` | `{ "runValue": "App", "scheduledTask": "App" }`: on an **update** the option starts selected only if that Run value (HKCU or HKLM) or scheduled task exists. Use it when the app itself turns the feature on and off (autostart in the app's settings), so an update keeps the user's current state. An option with `startup` detects its own Run value without this. `/option` and Inno `/TASKS` still win. |
| `startup` | object \| null | `null` | `{ "name": "App", "arguments": ["--background"] }`: while the option is selected, `app.mainExecutable` starts with Windows (`HKCU\...\Run\<name>`). The value is removed when the option is unselected on an update and on uninstall. |
| `required` | bool | `false` | A component that is always installed (e.g. a driver the app needs): shown checked and **locked**, so users see it is part of the installation. `/option id=no` cannot turn it off. |

Options are shown on the installer's first page, below the install location. Labels can also come from the
localization folder (`ui.localization`).

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
| `id` | string | `""` | Optional id (letters, digits, `-`, `_`), used to translate `name` in the localization folder. |
| `fallbackArguments` | string[] \| null | `null` | If the step does not succeed, run the same program again **visibly** with these arguments, e.g. an installer's interactive mode after its silent mode failed. Up to 30 minutes. |
| `fallbackRunValue` | object \| null | `null` | `{ "name": "...", "arguments": [...] }`. If the step still does not succeed, register `"<run>" <arguments>` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` instead and count the step as done (e.g. autostart when a scheduled task cannot be created). List the name in `uninstall.removeRunValues`. |

### Security rules

These rules are enforced by the builder and again by the installer engine, which re-validates the package header.

- Actions can **only** run an `.exe` that is part of the package's own payload. The path is relative, inside the install folder, and not reached through a link. System tools, scripts, `cmd.exe`, URLs and absolute paths are impossible.
- Processes are started directly with `CreateProcess`, with no shell. They run hidden, with the program's folder as the working directory, inside a job object so a timeout ends the whole process tree.
- A step that fails (failed to start, timed out, or exited with a code not in `successExitCodes`) is handled according to `onFailure`. With `fail` after install, setup rolls back its *own* changes. Anything the program itself did, such as installing a driver or registering a task, cannot be undone (info FS1627).
- `ifOption` is evaluated against the user's choice. In quiet mode it uses the option's `default` or `/option`. The choices are stored in the install record, so `beforeUninstall` actions see what was chosen at install time (records from older versions fall back to the defaults).
- `skipIfInstalled` is checked right before the step runs.

## uninstall

These are extra cleanup steps for things the app itself created. They run after the uninstall is committed, and their
failures are only logged.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `removeData` | string[] | `[]` | Files or folders deleted (folders recursively). Each must be `{CommonAppData}\<path>`, `{LocalAppData}\<path>`, `{AppData}\<path>` or `{InstallDir}\<path>` (something the app writes into its own folder, such as `logs`), where `<path>` is a valid relative path. Nothing reached through a link is removed. |
| `removeScheduledTasks` | string[] | `[]` | Task names in the Task Scheduler root folder (max 128 characters, no `\/:*?"<>\|`). |
| `removeRunValues` | string[] | `[]` | Value names deleted from `HKCU` and `HKLM` `Software\Microsoft\Windows\CurrentVersion\Run`. Same name rules as for tasks. |
| `removeShortcuts` | string[] | `[]` | Shortcut file names (`.lnk`) deleted from the per-user and all-users Start menu Programs, Desktop and Startup folders (for shortcuts the app or older versions created); `"Folder\\App.lnk"` for a Start menu folder. |
| `removeRegistryKeys` | string[] | `[]` | Per-user registry keys deleted with everything below them, e.g. `"HKCU\\Software\\Contoso\\App"`. `Software\<Vendor>` or deeper; `Microsoft`, `Classes`, `Policies`, `Wow6432Node`, `RegisteredApplications` and `Clients` are refused (FS1653). |
| `userData.paths` | string[] | `[]` | Settings and data the user decides about (`{LocalAppData}\X`, `{InstallDir}\X` etc., files or folders): a checkbox on the uninstall page. Quiet and passive uninstalls keep them unless `/remove-user-data`. |
| `userData.default` | bool | `false` | Checkbox pre-selected (delete). |
| `elevated.ifExists` | string[] | `[]` | Undo changes the app made with administrator rights only if one of these exists (checked before user data is removed; empty = always). |
| `elevated.traceSessions` | string[] | `[]` | ETW sessions to stop, including their boot-time autologger (`logman ... autosession\<name>`). |
| `elevated.scheduledTasks` | string[] | `[]` | Scheduled tasks, folders allowed (`"App\\Task"`); an emptied folder is removed. |
| `elevated.serviceAccess` | object[] | `[]` | `{ "service": "Name", "rights": ["LCRPWP"] }`: removes allow-entries for user accounts (`S-1-5-21-…`) with exactly these SDDL rights from the service's DACL. |
| `elevated.removeData` | string[] | `[]` | Files or folders removed with administrator rights (no `{InstallDir}`: this step can run after the install record is gone). |
| `elevated.removeRegistryKeys` | string[] | `[]` | Registry keys removed with administrator rights, e.g. `"HKLM\\Software\\Contoso\\App"` (same rules as `removeRegistryKeys`). |

`elevated` runs directly in a per-machine (elevated) uninstall. A per-user uninstall asks once for UAC after the
files are removed (interactive only; silent uninstalls skip it and log a warning).

The installed files and shortcuts, the app's uninstall registration and the install folder (if it is empty) are always
removed. Preserved files and files the installer did not create are kept.

## update

Built-in updates. With an update source, the installed `uninstall.exe` can look for a newer Setup.exe of the app and
install it, so an app needs no updater of its own: a "Check for updates" menu item starts
`uninstall.exe /install-update /relaunch`, a background check runs `uninstall.exe /check-update`.

```json
"update": { "github": "contoso/notes", "checksums": "SHA256SUMS.txt" }
```

| Property | Type | Default | Meaning |
|---|---|---|---|
| `github` | string | `""` | GitHub repository `owner/name`. The latest release (`/releases/latest`) is used; with `prerelease`, the newest of the recent releases. Its version is the tag without a leading `v`. |
| `feed` | string | `""` | Instead of `github`: an https URL of a JSON document `{ "version": "1.2.0", "url": "https://…/Setup.exe", "sha256": "<64 hex digits>", "size": 12345678, "notes": "https://…" }` (`size` and `notes` optional). For your own server or a CDN. |
| `asset` | string | `""` | `github`: the file name of Setup.exe in a release, with `{Version}` for the release's version, e.g. `"MyApp-{Version}-Setup.exe"`. Empty = `output.fileName` (its other placeholders are filled in at build time). A name that contains the version literally (from `$(Version)`) gets warning FS1705: the next release has another name. |
| `prerelease` | bool | `false` | `github`: also offer pre-releases. |
| `checksums` | string | `""` | `github`: a release file with `<sha256>  <file name>` lines (`sha256sum` format), e.g. `SHA256SUMS.txt`. Empty = GitHub's own SHA-256 digest of the asset. When both exist they must agree. |
| `signer` | string | `""` | If set, an update only runs when its Authenticode signature is valid with a trusted chain and the certificate is issued to this name (subject common name, e.g. `"Contoso Ltd"`). Use it when you sign (`signing.enabled`); FS1710 warns otherwise. |

Before anything runs, the download must: come over https (redirects only to https); match the published SHA-256
(checked while downloading and once more through a handle that keeps the file from changing); be a valid FluentSetup
package of the same `app.id` and architecture; contain exactly the advertised version, which must be newer than the
installed one; and carry the pinned signature if `signer` is set. The new Setup.exe then runs with `/passive` (or
`/quiet`) and does the update like any other upgrade: journaled, rolled back on failure, closing the running app
(`install.closeProcesses`) and, with `/relaunch`, starting it again.

| Command | What it does |
|---|---|
| `uninstall.exe /check-update` | No UI. Exit code 0 = up to date, 18 = a newer version is published, 19 = the source could not be read. Prints one JSON line on standard output if it is redirected: `{"installedVersion":"1.0.0","updateAvailable":true,"latestVersion":"1.1.0","notes":"https://…"}`. |
| `uninstall.exe /install-update` | A small window: checking, then "1.1.0 is available" with **Update** / **Not now** and a link to the release notes, then the download progress. Exit code 0 (up to date, or the new Setup.exe was started), 2 (Not now / cancelled) or 19. |
| `uninstall.exe /install-update /passive` | Progress only: no question; closes by itself when there is no update. |
| `uninstall.exe /install-update /quiet` | No UI: downloads, verifies and starts `Setup.exe /quiet`. Exit code 0 when there is no update or the new setup was started. |

Add `/relaunch` to start the app again after the update, and `/lang` to choose the language. GitHub allows 60 API
requests per hour per address without a token, enough for checks at start-up or once a day.

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
| `license` | string | `""` | Plain-text license file (max 1 MB). If set, the user must accept it before installing. |
| `licenseMode` | `firstInstall` \| `always` | `"firstInstall"` | `always` also shows the license before upgrades and reinstalls (e.g. a user agreement that must be accepted for every version). |
| `licenses` | object | `{}` | Per-language license files, e.g. `{ "zh-CN": "license_zh.txt" }`. Falls back to `license`. |
| `localization` | string | `""` | Folder with one `<language>.json` per language; see *Localization*. |
| `defaultLanguage` | `auto` \| `en-US` \| `zh-CN` \| `ja-JP` \| `ko-KR` | `"auto"` | `auto` follows the user's Windows display languages (first one the installer supports; else en-US). `/lang` overrides this setting. |
| `strings` | object | `{}` | Per-language overrides of built-in strings, e.g. `{ "zh-CN": { "welcome.body": "…" } }`. Keys are listed in `shared/localization/en-US.json`. Values longer than 4096 bytes are ignored. |

### Localization

The installer's own text ships in four languages: en-US, zh-CN, ja-JP and ko-KR (one file each in
`shared/localization`). **Any other language** can be added by a package: give its localization file (named with
any BCP-47 tag, e.g. `fr-FR.json`, `de.json`, `pt-BR.json`) a `strings` table with the installer's texts.
`fluentsetup locale add <tag> <project.json>` creates that file with every key (English values to translate) and the
package's option, action and variant ids. Language choice: `/lang`, else `ui.defaultLanguage`, else the first Windows
display language the installer has strings for (exact tag first, then the same primary language, e.g. `de-AT` →
`de-DE`), else English. Untranslated keys show in English; `validate` reports the coverage (FS1327). The package's own text (option labels, action names, license, string overrides) can be kept
out of `fluentsetup.json` in a folder named by `ui.localization`, one file per language:

```json
// locales/ja-JP.json
{
  "options": { "autostart": "Windows 起動時に開始" },
  "actions": { "pawnio": "PawnIO ドライバーのインストール" },
  "license": "license_ja-JP.txt",
  "strings": { "welcome.running": "{0} を終了してから続行します。" }
}
```

`options` and `actions` are keyed by id (`options[].id`, `actions[].id`), `license` is relative to the localization
folder. A file wins over inline texts for its language. Files whose name is not a language tag are reported (FS1321) and
ignored; unknown ids give warnings (FS1323, FS1324).

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
    -D, --define <n>=<v>    Value for $(n) in the project (repeatable, overrides "variables")
    -v, --verbose           Detailed log (also prints info diagnostics)
    --json                  Print the build report as JSON instead of text
    --non-interactive       Accepted for CI scripts (the CLI never prompts)
fluentsetup validate <project.json> [--json] [-D <name>=<value> ...]
fluentsetup inspect <Setup.exe> [--verify] [--json]
fluentsetup init [folder] --name <name> --id <app.id> --source <files folder> [--publisher <name>]
fluentsetup winget <Setup.exe> --url <https URL> | --github <owner/repo> [-o <dir>] [--identifier <Publisher.App>]
                   [--license <name>] [--license-url <url>] [--short-description <text>] [--publisher-url <url>]
                   [--tags <a,b>] [--release-notes-url <url>] [--json]
fluentsetup --version | --help
```

- `validate` runs all project checks and collects the files, the same steps `build` performs before compressing, and prints diagnostics as `severity CODE [field]: message`. With `--json` it prints `{ valid, files, bytes, diagnostics }`.
- `inspect` prints the header summary, sizes and signature state of a built Setup.exe. `--verify` also checks every stream's SHA-256.
- `init` creates `<folder>\fluentsetup.json` (default folder: the current one) and refuses to overwrite an existing file. `mainExecutable` is set to the first `*.exe` found directly in the source folder.
- `winget` writes winget manifests for a built Setup.exe into `<dir>\manifests\<letter>\<Publisher>\<App>\<version>\` (default `<dir>`: the current folder), the layout of the winget-pkgs repository. Everything else comes from the package. See [winget.md](winget.md); [msbuild.md](msbuild.md) covers the NuGet packages (MSBuild targets, .NET tool, library).
- Options accept `--name value` or `--name=value`. Unknown or repeated options are usage errors.
- Exit codes: `0` success, `1` validation or build failure, `2` usage error, `3` unexpected error, `4` cancelled.

## Setup.exe command line

Switches can be written `/x`, `-x` or `--x` and are case-insensitive. A value follows as the next argument or after
`=`, for example `/dir=C:\Apps\X`. Unknown switches and invalid values are errors (exit code 3) and are never ignored.
The installed `uninstall.exe` accepts the same switches.

| Switch | Meaning |
|---|---|
| `/quiet`, `/verysilent`, `/q`, `/s` | No UI at all. Uses the defaults plus the switches below. A per-machine operation started without administrator rights shows the UAC prompt, as Inno Setup and NSIS do; a declined prompt gives exit code 6. |
| `/passive`, `/silent` | Progress window only: no questions, starts at once with the defaults and closes itself after a success (a failure stays on screen). This is Inno Setup's `/SILENT`, so in-app updaters written for Inno keep showing progress. |
| `/uninstall`, `/u` | Remove the installed app instead of installing it. |
| `/dir <path>`, `/installdir <path>` | Install folder. Honoured only if `allowCustomDir` is true and the app is not yet installed. The engine validates it. |
| `/scope user\|machine` | Scope. Honoured only if the package allows that scope and the app is not yet installed. |
| `/desktop-shortcut yes\|no` | Desktop shortcut choice. Honoured only for `optionalOff`/`optionalOn`. |
| `/start-menu-shortcut yes\|no` | Start menu shortcut choice. Honoured only for `optionalOff`/`optionalOn`. |
| `/variant <id>` | Package variant to install (see *Variants*). |
| `/remove-user-data` | Uninstall: also delete `uninstall.userData`. |
| `/relaunch`, `/update` | After a quiet or passive install, start the app with `install.relaunchArguments` (for in-app updaters; `/update` is the NSIS-style spelling, `Setup.exe /S /UPDATE`). |
| `/check-update` | Installed app with an `update` source: exit code 18 if a newer version is published, 0 if not (see *update*). |
| `/install-update` | Download, verify and install the newest version (see *update*); with `/passive` or `/quiet` without questions. |
| Inno / NSIS switches | `/VERYSILENT`, `/SILENT` (passive), `/DIR=`, `/LOG=`, `/LANG=english`, `/TASKS=`, `/MERGETASKS=`, `/NOICONS`, `/CURRENTUSER`, `/ALLUSERS`, `/RELAUNCH=1`, `/S`, `/D=path` (last, may contain spaces) are understood; `/SUPPRESSMSGBOXES`, `/NORESTART`, `/CLOSEAPPLICATIONS`, `/SP-`, `/NCRC`, `_?=` are accepted and have no effect. Upgrades also pre-fill choices from an adopted Inno install's saved tasks. |
| `/option <id>=yes\|no` | Value of a package option. Can be repeated. Ids the package does not define are ignored. |
| `/lang en-US\|zh-CN\|ja-JP\|ko-KR` | UI language. Overrides `ui.defaultLanguage`. |
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
| 18 | UpdateAvailable | `/check-update`: a newer version is published (not a failure) |
| 19 | UpdateFailed | `/check-update`, `/install-update`: the update source could not be read, or the download was refused |
