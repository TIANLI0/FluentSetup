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
| `update` | object | | [Built-in updates](#update) |
| `shortcuts` | array | `[]` | [More shortcuts](#shortcuts) |
| `associations` | array | `[]` | [File types](#associations) |
| `protocols` | array | `[]` | [URL schemes](#protocols) |
| `registry` | array | `[]` | [Registry values](#registry) |
| `environment` | array | `[]` | [Environment variables](#environment) |
| `services` | array | `[]` | [Windows services](#services) |
| `firewall` | array | `[]` | [Firewall rules](#firewall) |
| `inputs` | array | `[]` | [Values entered during setup](#inputs) |
| `configFiles` | array | `[]` | [JSON and INI files setup writes](#configfiles) |
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
| `appUserModelId` | string | `""` | AppUserModelID set on the app's Start menu and desktop shortcuts, e.g. `Contoso.Notes` (letters, digits, `.`, `-`, `_`; max 128, no spaces). Unpackaged apps need it for toast notifications. |
| `toastActivatorClsid` | string | `""` | CLSID of the app's toast activator COM server, `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`, set on the same shortcuts (with `appUserModelId`). |

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
| `appPaths` | bool | `false` | Registers `app.mainExecutable` under `App Paths` (per user or for the PC), so Win+R and other programs start it by its file name. |
| `restartManager` | `auto` \| `off` | `"auto"` | `auto`: before files are replaced or removed, programs that use them (an Explorer extension, an editor, the app itself) are closed through Windows Restart Manager and, if they registered for it, restarted afterwards. The first page lists them. `off`: only `closeProcesses` and `closeCommand`. |
| `lockedFiles` | `fail` \| `reboot` | `"fail"` | A file that is still in use after that: `fail` stops and rolls back (exit code 8). `reboot` (per-machine installs; setup needs administrator rights for it) writes the new file next to it and replaces it at the next restart; uninstall deletes such files at the next restart. Setup.exe then exits with 3010 and the result page asks for a restart. |

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
fails until the app is uninstalled. A per-machine install in `/quiet` mode started without administrator rights
shows the UAC prompt (the only window quiet mode ever shows, as with Inno Setup and NSIS); if it is declined,
Setup.exe exits with code 6. Deployment tools that run elevated never see it.

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
| `vcRedist.required` | bool | `false` | The app needs the Microsoft Visual C++ 2015–2022 (v14) Redistributable (`VisualStudio\14.0\VC\Runtimes\<arch>`). |
| `vcRedist.minVersion` | string | `""` | Minimum version, e.g. `"14.40"`; empty = any v14 runtime. |
| `vcRedist.architecture` | `""` \| `x64` \| `x86` \| `arm64` | `""` | Empty = the package architecture (an x64 app with a 32-bit helper can need `x86` too: use a custom prerequisite for the second one). |
| `vcRedist.*` | source | | `installer`, `arguments` (default `["/install", "/quiet", "/norestart"]`), `auto`, `download`, `helpUrl`, `onFailure`, `variants`. |
| `custom[]` | object[] | `[]` | Up to 8 other prerequisites: `id`, `name` (shown to the user), `detect` (see below) and a source (`installer`, `download`, `helpUrl`, `onFailure`; no `auto`). |
| `custom[].detect.registry` | object | | Present when this value exists, or holds at least `version`: `{ "hive": "HKLM", "key": "SOFTWARE\\Contoso\\Driver", "value": "Version", "version": "2.0" }` (both registry views are checked; empty `value` with no version = the key itself). |
| `custom[].detect.file` | object | | Present when the file exists (and its version resource is at least `version`): `{ "path": "{System}\\drivers\\contoso.sys", "version": "2.0" }`. `{ProgramFiles}`, `{ProgramFilesX86}`, `{System}`, `{CommonAppData}`, `{LocalAppData}` or `{AppData}` first. |

### Prerequisite sources

| Property | Type | Default | Meaning |
|---|---|---|---|
| `auto` | `""` \| `bundle` \| `download` | `""` | Microsoft's official installer, without adding it yourself (see [official installers](#official-installers)). `bundle`: downloaded when the package is built, stored in Setup.exe and extracted only if the prerequisite is missing (never installed into the app folder). `download`: downloaded by setup when needed. |
| `installer` | string | `""` | Installer **among the installed files** (add it with `files.extra`). Runs after the app's files are placed, visible, up to 30 minutes. Wins over `auto`. |
| `arguments` | string[] | see above | Installer arguments. |
| `download` | object | `null` | `{ "url": "https://…", "sha256": "<64 hex>", "size": <bytes or 0>, "signer": "…" }`. `signer` (optional): the file must also carry a valid Authenticode signature with a trusted chain issued to this name; with a `signer`, `sha256` may be left empty for vendor links that always serve the newest installer. Used when no installer is bundled. Downloaded **before anything is changed** (progress is shown), over TLS 1.2+, https only (no redirect to http), into a private temporary folder, size-capped (exact `size`, else 512 MiB) and verified against `sha256` before it runs. A mismatch or network error stops setup with exit code 17 and nothing installed. |
| `helpUrl` | string | `""` | https link shown when the prerequisite is missing and setup cannot install it. |
| `onFailure` | `warn` \| `fail` | `"warn"` | After the installer ran, the prerequisite is checked again. `warn`: the app stays installed and the result page lists the problem. `fail`: the whole installation is rolled back (exit code 17). |

With neither `installer` nor `download`, a missing prerequisite stops setup before anything is changed (exit code 17,
`PrerequisiteMissing`, with `helpUrl` in the message).

### Official installers

`"auto": "bundle"` and `"auto": "download"` use these links. They always serve Microsoft's current installer, so an
installer that setup downloads is accepted by its signature (a valid, trusted Authenticode signature issued to
"Microsoft Corporation"), not by a SHA-256. For `bundle` the builder downloads the file, checks the same signature (on
Windows) and keeps it for a day under `%LocalAppData%\FluentSetup\cache\installers` (`fluentsetup build
--refresh-installers` downloads it again; an offline build uses the last copy). The build report lists every bundled
installer with its SHA-256.

| Prerequisite | Installer | Silent arguments |
|---|---|---|
| WebView2 | Evergreen bootstrapper `https://go.microsoft.com/fwlink/p/?LinkId=2124703` (about 2 MB; it downloads the runtime, so it needs the internet) | `/silent /install` |
| .NET Framework | 4.8 offline installer (`linkid=2088631`) for 4.5–4.8, 4.8.1 offline installer (`linkid=2203305`) for 4.8.1 | `/q /norestart` |
| .NET | `https://aka.ms/dotnet/<M.m>/<windowsdesktop\|dotnet\|aspnetcore>-runtime-win-<x64\|arm64>.exe` (latest patch of the channel of `version`) | `/install /quiet /norestart` |
| Windows App Runtime | `https://aka.ms/windowsappsdk/<M.m>/latest/windowsappruntimeinstall-<x64\|arm64>.exe` | `--quiet` |
| VC++ Redistributable | `https://aka.ms/vs/17/release/vc_redist.<x64\|x86\|arm64>.exe` | `/install /quiet /norestart` |

`arguments` overrides the silent arguments. The Builder app and `fluentsetup versions <webview2|windowsappruntime|dotnet|vcredist>`
list current versions from Microsoft (Edge Stable for WebView2, NuGet for the Windows App SDK, the .NET release index).

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

## Placeholders

These placeholders are replaced at install time in `shortcuts[].arguments`, `associations[].arguments`,
`protocols[].arguments`, `actions[].arguments`, `registry[].values[].data`, `environment[].value` and
`configFiles[].values[].value`:

| Placeholder | Value |
|---|---|
| `{InstallDir}` | The install folder |
| `{MainExecutable}` | Full path of `app.mainExecutable` |
| `{AppVersion}` | `app.version` |
| `{AppId}` | `app.id` |
| `{Input:<id>}` | The value of an [input](#inputs); an unknown id is error FS1998 |

Every item of the sections below can carry `ifOption` (an `options[].id`): it is only applied while that option is
selected, and the Change page of an installed app adds or removes it when the option is switched.

## shortcuts

More shortcuts besides the app's own (at most 16): other programs of the app, the app with arguments, the Startup
folder. They are recorded like the app's own shortcuts: removed on uninstall and when an update no longer wants them.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | | **Required.** File name without `.lnk` (max 128). |
| `target` | string | `""` | Relative path of a file among the installed files; empty = `app.mainExecutable`. |
| `arguments` | string[] | `[]` | Arguments (placeholders allowed). |
| `description` | string | `""` | Tooltip; empty = `app.description`. |
| `icon`, `iconIndex` | string, int | `""`, `0` | `.ico`, `.exe` or `.dll` among the installed files; empty = the target. |
| `workingDirectory` | string | `""` | Relative folder; empty = the target's folder. |
| `location` | `startMenu` \| `desktop` \| `startup` | `"startMenu"` | Per-user folders for per-user installs, all-users folders for per-machine installs. |
| `folder` | string | `""` | Start menu sub-folder; empty = `install.startMenuFolder`. |
| `appUserModelId` | string | `""` | AppUserModelID of this shortcut. |
| `ifOption` | string | `""` | See [placeholders](#placeholders). |

## associations

File types the app opens (at most 16). Registered under `HKCU\Software\Classes` for per-user installs and
`HKLM\Software\Classes` for per-machine installs: the ProgID with its icon and open command, the extensions'
"Open with" lists, the extension's default handler where no other app is the default yet (Windows asks the user
otherwise), and a Default apps entry (`Software\RegisteredApplications` → `Software\FluentSetup\Capabilities\<app.id>`).
Everything is journaled (a rollback restores the previous registry state) and removed on uninstall; an extension's
default handler is only removed while it is still this app's.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `extensions` | string[] | | **Required**, 1–16, e.g. `[".cnote"]` (`.` + 1–32 letters, digits, `-`, `_`). Program and shell types such as `.exe`, `.lnk`, `.bat`, `.ps1`, `.url` are refused (FS1922). |
| `progId` | string | | **Required.** e.g. `Contoso.Notes.Document`: letters and digits in dot-separated parts (at least one dot), max 39, not `Microsoft.*`, `Windows.*`, `AppX*`. |
| `description` | string | `""` | File type name in Explorer; empty = `app.name`. |
| `icon`, `iconIndex` | string, int | `""`, `0` | Icon file among the installed files; empty = the target. |
| `target` | string | `""` | Program (`.exe`) that opens the files; empty = `app.mainExecutable`. |
| `arguments` | string[] | `["%1"]` | Command line; `"%1"` is the file and is always quoted. |
| `contentType` | string | `""` | MIME type, set on the extension when it has none. |
| `ifOption` | string | `""` | See [placeholders](#placeholders). |

## protocols

URL schemes the app handles (at most 16), registered next to the file types.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `scheme` | string | | **Required.** Lowercase, 2–32 characters (`a-z`, digits, `+`, `-`, `.`), e.g. `contoso-notes`. Schemes of Windows and browsers (`http`, `https`, `file`, `mailto`, `ms-*`, `windows*`, …) and shell class names (`exefile`, `folder`, …) are refused; an existing key that is not a URL scheme is never taken over. |
| `description` | string | `""` | e.g. "Contoso Notes link". |
| `icon`, `iconIndex`, `target`, `ifOption` | | | As for file types. |
| `arguments` | string[] | `["%1"]` | `"%1"` is the URL. |

## registry

Registry keys with values (at most 32 keys, 32 values each), below `HKCU` for per-user installs and `HKLM` for
per-machine installs. Journaled: a rollback gives changed values their old data back and removes keys setup created.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `key` | string | | **Required.** `Software\<Vendor>\...`, e.g. `Software\Contoso\Notes`. Not `Microsoft`, `Classes`, `Policies`, `Wow6432Node`, `RegisteredApplications`, `Clients` or `FluentSetup` (use associations and protocols for classes). |
| `values[].name` | string | `""` | Empty = the key's default value. |
| `values[].type` | `string` \| `expandString` \| `dword` \| `qword` \| `multiString` | `"string"` | |
| `values[].data` | string | `""` | Text (placeholders allowed); numbers in decimal or `0x` hex; `multiString` lines separated by `\n`. |
| `uninstall` | `values` \| `key` \| `keep` | `"values"` | `values`: remove the values written, then the key and its parents up to `Software\<Vendor>` once empty. `key`: remove the whole key. `keep`: leave it. |
| `ifOption` | string | `""` | See [placeholders](#placeholders). |

`install.appPaths` registers the main executable under `Software\Microsoft\Windows\CurrentVersion\App Paths`.

## environment

Environment variables (at most 16): the user's for per-user installs, the machine's for per-machine installs.
Written after the install is committed (like `install.addToPath`), removed on uninstall and by updates that no longer
declare them, in both cases only while the variable still holds the value setup wrote. Running programs are told
(`WM_SETTINGCHANGE`).

| Property | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | | **Required.** Letters, digits, `_ . ( ) -`. Not `Path` (use `install.addToPath`) or a Windows variable (`TEMP`, `SystemRoot`, `ProgramFiles`, `PROCESSOR_*`, …). |
| `value` | string | `""` | Placeholders allowed. |
| `expand` | bool | `false` | Store as `REG_EXPAND_SZ` (the value refers to `%OTHER%` variables). |
| `ifOption` | string | `""` | See [placeholders](#placeholders). |

## services

Windows services run from the app's programs (at most 8; per-machine installs only, FS1966; with `userOrMachine`
only when the user chooses "All users"). Before files are replaced or removed, the app's running services are
stopped (a rollback starts them again). After the install is committed each service is created or updated (binary
path, start type, account, description, recovery) and started; services an update no longer declares are deleted.
Uninstall stops and deletes them.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | | **Required.** Service name: letters, digits, `.`, `-`, `_` (max 80). |
| `displayName`, `description` | string | `""` | Shown in Services; empty display name = `name`. |
| `run` | string | | **Required.** The service `.exe` among the installed files. |
| `arguments` | string[] | `[]` | |
| `startType` | `auto` \| `delayedAuto` \| `manual` \| `disabled` | `"auto"` | |
| `account` | `localSystem` \| `localService` \| `networkService` | `"localSystem"` | Built-in accounts only (no passwords in packages). |
| `start` | bool | `true` | Start after installing (a service that ran before an update is started again in any case). |
| `dependencies` | string[] | `[]` | Services this one needs. |
| `restartOnFailure` | bool | `true` | Recovery: restart after 5 s, 30 s and 60 s. |

## firewall

Windows Defender Firewall allow rules for the app's programs (at most 16; per-machine installs only). All rules
carry the group `FluentSetup:<app.id>`: after the install is committed the group is replaced with the declared rules,
uninstall removes it. A rule name that another program's rule also uses is never removed.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | | **Required.** Rule name (max 128, no `\|`). |
| `program` | string | `""` | `.exe` among the installed files; empty = `app.mainExecutable`. |
| `direction` | `in` \| `out` | `"in"` | |
| `protocol` | `any` \| `tcp` \| `udp` | `"any"` | |
| `localPorts` | string | `""` | tcp/udp only: `8080`, `8000-8010,9000`; empty = any port. |
| `profiles` | string[] | `[]` | `domain`, `private`, `public`; empty = all. |
| `ifOption` | string | `""` | See [placeholders](#placeholders). |

## inputs

Values the user enters during setup (at most 8), for example a server address or a licence key. They are shown on
a *Settings* page after the first page (on updates only when a required value is missing) and on the Change page,
and come from `/input <id>=<value>` in scripted installs. Use them as `{Input:<id>}`.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `id` | string | | **Required.** Letters, digits, `-`, `_` (max 32). |
| `label`, `description` | object | `{}` | Text per language (label required). |
| `type` | `text` \| `password` \| `choice` | `"text"` | |
| `default` | string | `""` | |
| `required` | bool | `false` | |
| `pattern` | string | `""` | Regular expression the value must match (use `^` and `$` to match it whole), e.g. `^https://`. Checked by the builder (.NET, ECMAScript mode) and by setup (C++ `std::regex` ECMAScript): keep to their common subset. |
| `choices` | object[] | `[]` | `type` choice: `{ "value": "fast", "label": { "en-US": "Fast" } }`. |
| `remember` | bool | `true` | Store the value in the install record and pre-fill it on updates and on the Change page. Never for passwords. |

A value setup does not accept (missing, not a choice, not matching `pattern`) stops a scripted install with exit
code 3 before anything is changed.

## configFiles

JSON or INI files setup writes values into (at most 16). Existing files keep their other settings.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `path` | string | | **Required.** Relative to the install folder (written before the install is committed and journaled: a rollback restores the old file; an edited payload file is recorded with its new hash), or `{LocalAppData}\X`, `{AppData}\X`, `{CommonAppData}\X` (written after the commit; a failure is a warning). |
| `format` | `json` \| `ini` | `"json"` | |
| `values[].key` | string | | json: dotted path such as `server.url` (missing objects are created); ini: the key. |
| `values[].section` | string | `""` | ini: the section (required). |
| `values[].value` | string | `""` | Placeholders allowed. |
| `values[].type` | `string` \| `number` \| `bool` | `"string"` | json only. |
| `ifOption` | string | `""` | See [placeholders](#placeholders). |

New INI files are created as UTF-16, so values in any language survive.

## Changing and repairing an installation

The Apps & features entry has **Modify**, which starts `uninstall.exe /modify`: a *Change* page with the shortcut
and option choices and the inputs. *Change* applies the new choices without touching the app's files: shortcuts,
startup values, file types, URL schemes, registry values, environment variables, config files and firewall rules that
depend on options are added or removed, and the `afterInstall` actions of options that were just turned on run; it is
journaled like an install and rewrites the install record. *Repair* checks every installed file against the install
record (size and SHA-256). If something is missing or damaged it reinstalls the same version with `/passive`, from
the original Setup.exe if it is still where it was run (identified by its header digest) or else from the update
source (GitHub release `v<version>`/`<version>`, or a feed that advertises the installed version). Without either it
says to run Setup.exe again. Scripts: `uninstall.exe /modify /quiet [/option …] [/desktop-shortcut …] [/input …]`,
`uninstall.exe /repair [/quiet]`.

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
| `defaultLanguage` | `auto` \| language tag | `"auto"` | `auto` follows the user's Windows display languages (first one the installer supports; else en-US). A tag pins the language: a built-in one, a regional tag of one (`de-CH` → `de-DE`) or a language the package brings strings for; see *Localization*. `/lang` overrides this setting. |
| `banner` | string | `""` | Image (`.png`, `.jpg` or `.bmp`, up to 1 MB; about 4:1, e.g. 960×240) shown at the top of the first page in both interfaces. Stored with the front-end files (also when `output.includeWinUI` is false). |
| `strings` | object | `{}` | Per-language overrides of built-in strings, e.g. `{ "zh-CN": { "welcome.body": "…" } }`. Keys are listed in `shared/localization/en-US.json`. Values longer than 4096 bytes are ignored. |

### Localization

The installer's own text ships in eleven languages: en-US, zh-CN, zh-TW (Traditional Chinese), ja-JP, ko-KR, de-DE,
fr-FR, es-ES, pt-BR, ru-RU and it-IT (one file each in `shared/localization`; `scripts/check-localization.ps1` checks
them against `en-US.json`). Regional tags use the table of their language: `zh-TW`, `zh-HK`, `zh-MO` and `zh-Hant-*`
→ zh-TW, other Chinese (`zh-SG`, `zh-Hans-*`) → zh-CN; `de-AT`, `de-CH` → de-DE; `fr-CA`, `fr-BE` → fr-FR; `es-*` →
es-ES; `pt-PT` and other Portuguese → pt-BR; `ru-*` → ru-RU; `it-CH` → it-IT; `en-*` → en-US. **Any other language**
can be added by a package: give its localization file (named with any BCP-47 tag, e.g. `nl-NL.json`, `pl.json`,
`pt-PT.json`) a `strings` table with the installer's texts. `fluentsetup locale add <tag> <project.json>` creates that
file with every key (to translate, starting from the installer's table of the same language, e.g. pt-BR for `pt-PT`,
else English) and the package's option, action and variant ids. Language choice: `/lang`, else `ui.defaultLanguage`,
else the user's Windows display languages in order; the first tag that matches wins, checked as: the package's
strings for exactly that tag, else a built-in table (exact tag, then script or region, then the same primary
language), else the package's strings for the same primary language (`nl-BE` → the package's `nl-NL`). Nothing
matches: English. Keys missing from a language the installer does not ship show in English; `validate` reports the
coverage (FS1327). The package's own text (option labels, action names, license, string overrides) can be kept
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

Setup.exe is signed after it is written (Authenticode, SHA-256 file digest); the build fails if the file carries no
signature afterwards. `provider` picks the tool:

| Provider | Builds on | Signs with |
|---|---|---|
| `signtool` (default) | Windows | `signtool.exe` and a certificate from the current user's store (`certificateThumbprint`) or a `.pfx` file (`certificateFile`) |
| `trustedSigning` | Windows | Azure Artifact Signing (formerly Trusted Signing): `signtool.exe` with Microsoft's `Azure.CodeSigning.Dlib.dll` |
| `command` | Windows, Linux, macOS | Any signing tool: osslsigncode, jsign, AzureSignTool, the SignPath client, a script of your own |

| Property | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `false` | Sign Setup.exe. `build --sign` and `build --no-sign` override it; with `--no-sign` the signing settings are not checked either. |
| `provider` | `signtool` \| `trustedSigning` \| `command` | `"signtool"` | See above. Empty means `signtool`. |
| `certificateThumbprint` | string | `""` | signtool: SHA-1 thumbprint (40 hex digits) of a certificate in the current user's store. Spaces and the invisible direction marks the Windows certificate dialog copies are removed. Takes precedence over `certificateFile`. |
| `certificateFile` | string | `""` | signtool: `.pfx` file. |
| `passwordEnvironmentVariable` | string | `""` | signtool: name of an environment variable that holds the PFX password (see [Secrets](#secrets)). |
| `timestampUrl` | string | `"http://timestamp.digicert.com"` | RFC 3161 timestamp server. Empty means no timestamp. For Artifact Signing use `http://timestamp.acs.microsoft.com`. The command provider gets it as `{timestampUrl}`. |
| `signToolPath` | string | `""` | signtool, trustedSigning: path of `signtool.exe`. Empty means the Windows 10/11 SDK and `PATH` are searched. |
| `signUninstaller` | bool | `true` | Also sign the `uninstall.exe` that setup writes into the install folder, with the same provider, so UAC and SmartScreen show the publisher when the app is removed. The builder produces the exact uninstaller setup will write, signs it, and stores its certificate table in the package (a few KB); setup appends it and checks the result against the recorded SHA-256 (else it writes an unsigned uninstaller and logs why). The signing tool must only append the certificate table (signtool, osslsigncode and jsign do). |
| `trustedSigning` | object | `null` | Settings of the trustedSigning provider ([below](#trustedsigning)). |
| `command` | object | `null` | Settings of the command provider ([below](#command)). |

### trustedSigning

| Property | Type | Meaning |
|---|---|---|
| `endpoint` | string | **Required.** The account's regional endpoint, e.g. `https://eus.codesigning.azure.net`. A region mismatch fails with 403 Forbidden. |
| `accountName` | string | **Required.** Artifact Signing account name. |
| `certificateProfile` | string | **Required.** Certificate profile name. |
| `dlibPath` | string | **Required.** `Azure.CodeSigning.Dlib.dll` for the architecture of `signtool.exe`: NuGet package `Microsoft.ArtifactSigning.Client`, folder `bin/x64`. Use a `$(Name)` variable if it differs between machines. |
| `correlationId` | string | Optional value attached to the signing requests, e.g. a build number. |
| `excludeCredentials` | string[] | `DefaultAzureCredential` sources to skip, e.g. `["ManagedIdentityCredential", "AzureCliCredential"]`. |

The builder writes the dlib's `metadata.json` into a temporary folder and runs
`signtool sign /fd SHA256 /tr <timestampUrl> /td SHA256 /dlib <dlibPath> /dmdf <metadata.json> <file>`. That needs
signtool 10.0.22621.755 or later and the .NET 8 runtime. Authentication is `DefaultAzureCredential`: for example
`AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` in the environment (CI secrets), or `az login` on a
developer machine. The identity needs the *Artifact Signing Certificate Profile Signer* role.

```json
"signing": {
  "enabled": true,
  "provider": "trustedSigning",
  "timestampUrl": "http://timestamp.acs.microsoft.com",
  "trustedSigning": {
    "endpoint": "https://eus.codesigning.azure.net",
    "accountName": "contoso",
    "certificateProfile": "contoso-public",
    "dlibPath": "tools/Microsoft.ArtifactSigning.Client/bin/x64/Azure.CodeSigning.Dlib.dll",
    "excludeCredentials": ["ManagedIdentityCredential", "WorkloadIdentityCredential"]
  }
}
```

### command

| Property | Type | Default | Meaning |
|---|---|---|---|
| `run` | string | | **Required.** The program: a name found on `PATH` (`osslsigncode`) or a path relative to the project file. |
| `arguments` | string[] | | **Required.** One argument per item, passed as written: no shell, no quoting, no `*` or `$VAR` expansion. Must contain `{file}`. |
| `environment` | object | `{}` | Extra environment variables for the program, e.g. `{ "JAVA_HOME": "/usr/lib/jvm/java-21" }`. Not for secrets. |
| `timeoutSeconds` | integer | `600` | The program is stopped and the build fails after this time (10–3600). |

Placeholders in `arguments`:

| Placeholder | Value |
|---|---|
| `{file}` | The file to sign, absolute path. The program signs it in place, unless `{output}` is used. |
| `{output}` | A temporary path next to `{file}` for tools that write the signed copy elsewhere (osslsigncode); it then replaces `{file}`. |
| `{description}` | The signature's description: the app name (`<app name> Uninstall` for the uninstaller). |
| `{timestampUrl}` | `signing.timestampUrl`. |

The program runs in the project folder and inherits the build's environment. It must exit with code 0 and leave an
Authenticode signature in the file. Its output goes to the build log (`build -v`).

```json
"signing": {
  "enabled": true,
  "provider": "command",
  "command": {
    "run": "jsign",
    "arguments": ["--storetype", "PKCS12", "--keystore", "certs/codesign.pfx", "--storepass", "env:PFX_PASSWORD",
                  "--alg", "SHA-256", "--tsaurl", "{timestampUrl}", "--name", "{description}", "{file}"]
  }
}
```

More examples (osslsigncode, jsign with Artifact Signing or a cloud key, AzureSignTool, SignPath):
[Building on Linux and macOS](cross-platform.md#signing).

### Secrets

- Passwords and keys never go into the project file. `passwordEnvironmentVariable` names the variable; the command
  provider's program inherits the environment, so CI secrets reach it directly.
- signtool accepts a `.pfx` password only on its command line (`/p`), where other processes on the machine,
  process-creation auditing and endpoint-protection logs can read it. FluentSetup never passes it there: it imports
  the `.pfx` into a temporary certificate store of the current user (`CurrentUser\FluentSetup-Signing-<random>`),
  signs with `/s <store> /sha1 <thumbprint>`, and then deletes the store and the imported private key. A store left
  behind by a build that was killed while signing (`FluentSetup-Signing-<process id>-<random>`) is removed, key
  included, by the next signing run.
- A `.pfx` without a password is passed with `/f`.
- The log shows the commands without secrets. With the command provider, keep secrets out of `arguments`: use the
  tool's own way of reading them from the environment or a file (jsign `env:NAME`, osslsigncode `-readpass`).

### Validation codes

| Code | Meaning |
|---|---|
| FS1501 | signtool: neither a thumbprint nor a certificate file |
| FS1502 | signtool: certificate file not found |
| FS1503 | `timestampUrl` is not an http(s) URL |
| FS1504 | Unknown `provider` |
| FS1505–FS1508 | trustedSigning: `endpoint` (https), `accountName`, `certificateProfile`, `dlibPath` missing or not found |
| FS1509 | trustedSigning: unknown `excludeCredentials` entry (warning) |
| FS1510 | command: `run` missing |
| FS1511 | command: no `{file}` in `arguments` |
| FS1512 | command: unknown placeholder |
| FS1513 | command: `run` is a path that does not exist |
| FS1514 | command: `run` not found on `PATH` (warning) |
| FS1515 | command: invalid `environment` variable name |
| FS1516 | command: `timeoutSeconds` outside 10–3600 |
| FS1517 | Settings of another provider are ignored (info) |
| FS1518 | signtool: the thumbprint is not 40 hex digits |
| FS1519 | signtool or trustedSigning on Linux or macOS: a warning from `validate`, an error when the build signs |

## CLI: `fluentsetup`

The CLI runs on Windows, Linux and macOS; the result is the same Windows `Setup.exe` on each. Release downloads:
`fluentsetup-<version>-win-x64.zip`, `-linux-x64.tar.gz` and `-osx-arm64.tar.gz` (self-contained, with the installer
runtime in `runtime/`). On Linux and macOS, sign with the `command` provider; `update` (installing a newer FluentSetup)
works on Windows only. See [Building on Linux and macOS](cross-platform.md).

```
fluentsetup build <project.json> [options]
    -o, --output <dir>      Output folder (default: output.directory)
    --arch x64|arm64        Target architecture (default: output.architecture)
    --sign | --no-sign      Override signing.enabled (--no-sign also skips the signing checks)
    --report <file.json>    Also write a machine-readable build report
    --runtime <dir>         Use another installer runtime folder
    -D, --define <n>=<v>    Value for $(n) in the project (repeatable, overrides "variables")
    -v, --verbose           Detailed log (also prints info diagnostics)
    --json                  Print the build report as JSON instead of text
    --non-interactive       Accepted for CI scripts (the CLI never prompts)
    --refresh-installers    Download official prerequisite installers ("auto": "bundle") again
    --msbuild               Diagnostics in MSBuild's format (used by the FluentSetup.MSBuild targets)
fluentsetup validate <project.json> [--json] [-D <name>=<value> ...]
fluentsetup inspect <Setup.exe> [--verify] [--json]
fluentsetup init [folder] --name <name> --id <app.id> --source <files folder> [--publisher <name>]
fluentsetup winget <Setup.exe> --url <https URL> | --github <owner/repo> [-o <dir>] [--identifier <Publisher.App>]
                   [--license <name>] [--license-url <url>] [--short-description <text>] [--publisher-url <url>]
                   [--tags <a,b>] [--release-notes-url <url>] [--json]
fluentsetup versions <webview2|windowsappruntime|dotnet|vcredist> [--json]
fluentsetup locale add <language> <project.json> [--force]
fluentsetup guide | schema
fluentsetup update [--check] [--prerelease] [--json]
fluentsetup import <setup.iss|script.nsi> [-o <folder>] [--force] [--json]
fluentsetup --version | --help
```

- `validate` runs all project checks and collects the files, the same steps `build` performs before compressing, and prints diagnostics as `severity CODE [field]: message`. With `--json` it prints `{ valid, files, bytes, diagnostics }`.
- `inspect` prints the header summary, sizes and signature state of a built Setup.exe. `--verify` also checks every stream's SHA-256.
- `versions` lists versions for a prerequisite's version field, newest first, as the Builder's version pickers offer them: `webview2` = the current Microsoft Edge Stable and Beta versions for Windows (`edgeupdates.microsoft.com`), `windowsappruntime` = stable Windows App SDK releases from NuGet in the project's format (`1.6` for a 1.x channel, `2.5.1` from 2.x on), `dotnet` = one `M.m.0` minimum per .NET channel with its latest runtime, LTS/STS and support phase (`builds.dotnet.microsoft.com` release metadata), `vcredist` = common minimums (there is no feed; Microsoft's official installer is always the latest). Lists are cached for a day under `%LocalAppData%\FluentSetup\cache\versions`; offline, the saved (or a built-in) list is printed with a warning. `--json` prints `{ product, versions[], source, retrievedAt, feedUrl, error }`.
- `init` creates `<folder>\fluentsetup.json` (default folder: the current one) and refuses to overwrite an existing file. `mainExecutable` is set to the first `*.exe` found directly in the source folder.
- `winget` writes winget manifests for a built Setup.exe into `<dir>\manifests\<letter>\<Publisher>\<App>\<version>\` (default `<dir>`: the current folder), the layout of the winget-pkgs repository. Everything else comes from the package. See [winget.md](winget.md); [msbuild.md](msbuild.md) covers the NuGet packages (MSBuild targets, .NET tool, library).
- `locale add` creates `<ui.localization>/<language>.json` with every text to translate (installer strings, option, action, variant and input ids), for a language the installer does not ship.
- `guide` prints the complete authoring guide ([ai-guide.md](ai-guide.md)), `schema` the JSON Schema of the project file.
- `update` updates FluentSetup itself (Builder and CLI) from its GitHub releases; `--check` only reports.
- `import` converts an Inno Setup or NSIS script into `<folder>\fluentsetup.json` (default folder: the script's) plus `import-report.md`, a checklist of what was mapped and what needs manual work. It refuses to overwrite either file without `--force`. See [import.md](import.md).
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
| `/modify` | Change the installed app's choices (Apps & features "Modify", which runs `"<dir>\uninstall.exe" /modify`): the "Change <App>" page with the shortcut and option toggles, the Settings page for inputs, and a Repair button. With `/quiet`: applies the defaults (the installation's current state) plus `/desktop-shortcut`, `/start-menu-shortcut`, `/option` and `/input`, without UI. Needs the installed version's package: `uninstall.exe` always is; a `Setup.exe` of another version refuses it (exit code 3). |
| `/repair` | Check the installed files against the install record (size and SHA-256) and, if any is missing or damaged, reinstall the same version: from the Setup.exe that installed it (if it is still there unchanged) or from the `update` source (the release of exactly the installed version). Exit code 0 when nothing was damaged or the repair succeeded, 1 when no copy of the installed version's setup is available, 19 when the update source could not be used. Interactive: the "Change" page starts the repair at once; with `/quiet` no UI (the reinstall runs with `/quiet` too). |
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
| `/input <id>=<value>` | Value of a package input (`inputs`, the Settings page); everything after the first `=` is the value and may be empty. Can be repeated (at most 8 ids). Install and `/modify` only. An id the package does not define is an error (exit code 3); the value is checked like on the Settings page (required, choices, pattern), and a quiet or passive install with a missing or invalid value fails with exit code 3 and the input named in the log. Without `/input` an update uses the value remembered from the previous install (never for passwords), else the default. Quote values with spaces: `"/input=note=two words"`. |
| `/lang <tag>` | UI language: a built-in one (`en-US`, `zh-CN`, `zh-TW`, `ja-JP`, `ko-KR`, `de-DE`, `fr-FR`, `es-ES`, `pt-BR`, `ru-RU`, `it-IT`), a regional tag of one (`zh-HK` → zh-TW, `de-CH` → de-DE) or a language the package brings strings for; see *Localization*. Overrides `ui.defaultLanguage`. |
| `/log <file>` | Write the log to this absolute file path. Default: `%TEMP%\FluentSetup\<appId>-install-<timestamp>.log` (or `-uninstall-`). |
| `/ui auto\|winui\|win32` | Front-end selection, for diagnostics. `win32` skips the WinUI front end. |
| `/help`, `/h`, `/?` | Show usage. |

`yes|no` also accepts `true`/`false` and `1`/`0`. `/uninstall`, `/modify` and `/repair` exclude each other. The
switches `--worker`, `--pipe`, `--parent-pid`, `--wait-pid`, `--origin-dir`, `--phase2`, `--origin-process`,
`--result-pipe` and `--ui-simulate` are used internally between FluentSetup processes; `--ui-simulate` (and the
`FLUENTSETUP_TEST_FAULT_AFTER` environment variable) only work in packages built with `fluentsetup build --test-hooks`.

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
| 19 | UpdateFailed | `/check-update`, `/install-update`: the update source could not be read, or the download was refused (also `/repair` when the update source could not be used) |
| 3010 | (ERROR_SUCCESS_REBOOT_REQUIRED) | Success, but files that were in use are replaced or removed when the PC restarts (`install.lockedFiles: reboot`); the result page says so. Deployment tools treat it as success with a pending restart, as for msiexec. |
