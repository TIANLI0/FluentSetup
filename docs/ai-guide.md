# FluentSetup authoring guide

This guide is for anyone, human or AI, who writes or changes a FluentSetup project (`fluentsetup.json`). It is
embedded in the CLI: `fluentsetup guide` prints it, `fluentsetup schema` prints the JSON Schema with every property,
type, default and limit. When the guide and the schema disagree, the schema and `fluentsetup validate` win.

## 1. What FluentSetup does

A project file describes an app. `fluentsetup build` turns it into a single `Setup.exe` that installs, upgrades,
repairs and uninstalls the app: native, no .NET or other runtime needed on the target PC, WinUI 3 interface with a
Win32 fallback, transactional (any failure rolls back), Apps & features entry, self-removing `uninstall.exe`.

## 2. Workflow

1. Look at the app's build output (the folder you would zip). Note the main `.exe`, subfolders, runtimes it needs
   (.NET, Windows App Runtime, WebView2, VC++), drivers or services it installs, and anything it registers at run time
   (scheduled tasks, Run entries, data folders).
2. `fluentsetup init <folder> --name "App" --id Company.App --source <build output>` writes a complete project with
   every default spelled out. Edit it; keep only what differs from the defaults if you prefer.
3. `fluentsetup validate <project.json> --json` → fix every `Error` (each has a code, a field path and a message;
   see §9). Warnings are worth reading, infos are hints.
4. `fluentsetup build <project.json>` → `Setup.exe` in `output.directory`. `--variant id` builds a single-variant
   package, `--report r.json` writes a machine-readable report.
5. `fluentsetup inspect <Setup.exe> --verify --json` → what is inside, sizes, integrity.
6. Test without a UI: `Setup.exe /quiet /log <file>` then `"<install dir>\uninstall.exe" /uninstall /quiet`. Exit code
   0 = success (§8). Per-user packages need no administrator rights; a per-machine quiet install started without them
   shows the UAC prompt (start it elevated for unattended tests).

Relative paths in the project are relative to the project file. Absolute paths work but make the project
machine-specific.

## 3. Decisions to make

| Question | Setting |
|---|---|
| Install for the current user only, everyone, or let the user choose? | `install.scope`: `user` (no admin), `machine` (UAC), `userOrMachine` + `defaultScope` |
| Which files? | `files.source` + `include`/`exclude` globs (§5). Anchor top-level names with `./` (`"./App.exe"`), because a bare `App.exe` matches in **every** subfolder. |
| User data inside the install folder? | `files.preserve` globs: installed only if missing, never overwritten or removed. Better: keep user data in `%LocalAppData%`. |
| Runtimes the app needs | `requirements` (§6): `netFramework`, `dotNet[]`, `windowsAppRuntime`, `webView2`, each with a minimum version and a source (bundled installer or verified download). |
| Ship self-contained and/or framework-dependent? | `files.variants` (§7): both builds in one package, the user picks; or `output.variants`/`--variant` for separate packages. |
| Optional extras (autostart, a driver, …) | `options[]`: checkboxes on the first page. `required: true` = always installed, shown locked. `startup` = Run entry while checked. `detect` = on updates, start from whether a Run value / scheduled task exists (the app can toggle it itself). `quietDefault` = value in unattended first installs. |
| Programs to run after install / before uninstall | `actions[]`: only `.exe` files from the package itself, no shell. |
| App running while updating? | `install.closeProcesses` (image names) and `install.closeCommand` (e.g. `App.exe --exit` for tray apps). |
| Replacing another installer (NSIS, Inno, MSI-less)? | `install.adoptExistingInstall` (same uninstall key = app id) or `install.legacyUninstallKeys` (old key names, e.g. Inno `"{GUID}_is1"`), plus `install.cleanup` for old tasks/Run values/shortcuts (`"Folder\\Old.lnk"` for a Start menu folder) and `install.cleanup.removeFiles` for the old uninstaller (`"unins*.exe"`, `"unins*.dat"`). Keep the old Start menu folder with `install.startMenuFolder`, the asset file name the app's updater looks for with `output.fileName`, and its command line (§11). |
| Version (or other values) set by the release pipeline | `"variables": { "Version": "1.0.0" }`, `"version": "$(Version)"`, then `fluentsetup build p.json -D Version=1.2.3`. |
| Command-line tool? | `install.addToPath: ["cli"]` (folder relative to the install folder; `""` = the folder itself): user PATH for per-user installs, machine PATH for per-machine; removed on uninstall. |
| Settings/data to ask about on uninstall | `uninstall.userData: { "paths": ["{LocalAppData}\\App"], "default": false }`: a checkbox on the uninstall page; silent uninstalls keep the data unless `/remove-user-data`. |
| Things the app did with administrator rights (ETW sessions, service permissions, scheduled tasks, ProgramData files) | `uninstall.elevated` (§8): undone with one UAC prompt during an interactive per-user uninstall, directly in a per-machine one. |
| The app updates itself by running the new Setup.exe silently | It can keep its Inno Setup / NSIS command line (§11). `install.relaunchArguments` = arguments when it passes `/relaunch` (Inno `/RELAUNCH=1`). |
| License / agreement | `ui.license` or per-language files; `ui.licenseMode: "always"` to show it on every update. |
| Languages | Built in: en-US, zh-CN, ja-JP, ko-KR. Any other language: a localization file with a `strings` table (§10). |

## 4. Project file at a glance

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/shared/schemas/fluentsetup.project.schema.json",
  "schemaVersion": 1,
  "app": { "id": "Company.App", "name": "App", "publisher": "Company", "version": "1.2.0",
           "description": "", "website": "https://…", "icon": "app.ico", "mainExecutable": "App.exe" },
  "files": { "source": "bin/publish", "include": ["**/*"], "exclude": ["**/*.pdb"], "preserve": [],
             "extra": [{ "source": "../redist/thing.exe", "target": "redist" }], "emptyDirectories": [], "variants": [] },
  "install": { "scope": "user", "defaultScope": "user", "userDir": "{LocalAppData}\\Programs\\{AppName}",
               "machineDir": "{ProgramFiles}\\{AppName}", "allowCustomDir": true,
               "desktopShortcut": "optionalOff", "startMenuShortcut": "on", "launchAfterInstall": "optionalOn",
               "allowDowngrade": false, "minWindowsBuild": 17763,
               "closeProcesses": [], "closeCommand": null, "adoptExistingInstall": false, "legacyUninstallKeys": [],
               "ngen": [], "cleanup": { "removeScheduledTasks": [], "removeRunValues": [], "removeShortcuts": [], "removeUninstallKeys": [] } },
  "requirements": { "netFramework": "", "dotNet": [], "windowsAppRuntime": { "required": false }, "webView2": { "required": false } },
  "options": [],
  "actions": [],
  "uninstall": { "removeData": [], "removeScheduledTasks": [], "removeRunValues": [], "removeShortcuts": [] },
  "ui": { "license": "", "licenseMode": "firstInstall", "localization": "", "defaultLanguage": "auto", "strings": {},
          "theme": "system", "backdrop": "mica", "useSystemAccent": true },
  "output": { "directory": "dist", "fileName": "{AppName}-{Version}-Setup.exe", "architecture": "x64",
              "compression": "normal", "variants": [], "includeWinUI": true },
  "signing": { "enabled": false }
}
```

Key rules:

- `app.id`: stable forever (it is the uninstall key and the upgrade identity). Letters, digits, `.`, `-`, `_`.
- `app.version`: 1–4 numbers (each < 2³²) plus optional `-tag`. Upgrades compare it; an older package is refused
  unless `install.allowDowngrade`.
- Offers (`desktopShortcut`, `startMenuShortcut`, `launchAfterInstall`): `off`, `on`, `optionalOff`, `optionalOn`.
  `launchAfterInstall` cannot be `on`.
- Directory templates: `{LocalAppData}`, `{UserProgramFiles}`, `{AppData}`, `{ProgramFiles}`, `{ProgramData}`,
  `{AppName}`, `{Publisher}`, `{AppId}`. Per-user scope cannot use `{ProgramFiles}`; per-machine cannot use AppData.
- `uninstall.removeData`: `{CommonAppData}\X`, `{LocalAppData}\X` or `{AppData}\X` only.
- `output.compression`: `fast` | `normal` | `max` (zstd 3/12/19; `max` adds long-distance matching).

## 5. Files

- Globs: `*` within a folder, `**` any depth, `?` one character, case-insensitive. A pattern **without** `/` matches
  the file name in any folder; a leading `./` anchors it to `source`; a pattern with `/` is anchored at `source`; a
  trailing `/` means everything below.
- `extra[]`: `{ "source": file-or-folder, "target": "sub/folder" }` adds files from elsewhere (installers, redist).
  `target: ""` = the install root.
- Reserved names in the install root: `uninstall.exe`, `uninstall.json`, `.fsetup`.
- Links (symlinks, junctions) in the source are never followed.

## 6. Requirements (prerequisites)

Each requirement is checked before anything changes. Missing or too old → the first page says so, and setup:

1. runs a bundled installer (`installer`: a path **among the installed files**, usually added with `files.extra`), or
2. downloads one (`download`: `{ "url": "https://…", "sha256": "<64 hex>", "size": <bytes or 0> }`; https only, size
   capped, hash verified before it runs; downloaded before any change), or
3. stops with exit code 17 and shows `helpUrl`.

After the installer ran the requirement is checked again: `onFailure: "warn"` (default) keeps the app installed and
reports it; `"fail"` rolls the whole installation back. `variants: [ids]` limits a requirement to some variants.
Installers that need administrator rights get a UAC prompt even in a per-user install.

| Requirement | Fields | Detection |
|---|---|---|
| `netFramework: "4.8"` + `netFrameworkSetup: {source}` | 4.5 … 4.8.1 | `NDP\v4\Full\Release` |
| `dotNet: [{ runtime: "core"\|"desktop"\|"aspnetcore", version: "8.0.0", rollForward: "minor", …source }]` | default args `/install /quiet /norestart` | `%ProgramFiles%\dotnet\shared\<runtime>\<version>` for the package architecture |
| `windowsAppRuntime: { required, version: "2.5.1", …source }` | default args `--quiet`; Microsoft's `WindowsAppRuntimeInstall-x64.exe` | framework package family `Microsoft.WindowsAppRuntime.<major>` (1.x: `<major.minor>`) |
| `webView2: { required, minVersion, …source }` | default args `/silent /install` | EdgeUpdate `pv` (machine or user) |

Which runtime does a .NET app need? Read `<App>.runtimeconfig.json`: `Microsoft.NETCore.App` → `core`,
`Microsoft.WindowsDesktop.App` (WPF/WinForms) → `desktop`. A WinUI 3 app published framework-dependent needs `core`
**and** `windowsAppRuntime` (version = the `Microsoft.WindowsAppSDK` package version). Self-contained builds need neither.

Getting a download hash: download the exact file once, check it (Authenticode signature; for .NET compare with the
SHA-512 in `https://builds.dotnet.microsoft.com/dotnet/release-metadata/<channel>/releases.json`), then
`Get-FileHash <file> -Algorithm SHA256` and `(Get-Item <file>).Length`. Use versioned URLs: a "latest" URL changes
content and breaks the hash.

## 7. Variants

```jsonc
"files": {
  "include": ["**/*"], "exclude": ["**/*.pdb"],
  "variants": [
    { "id": "selfContained", "label": { "en-US": "Self-contained (includes runtimes)" }, "default": true, "source": "publish/sc" },
    { "id": "shared", "label": { "en-US": "Shared runtimes (smaller)" }, "source": "publish/fd" }
  ]
}
```

- Each variant has its own `source`; `include`/`exclude`/`preserve`/`extra` apply to all. `files.source` is unused.
- Identical files (path + content) are stored once; the rest per variant. The size shown per variant is exact.
- The user picks on the first page (radio buttons, only when the package has ≥ 2 variants); `Setup.exe /variant id`
  in scripts. Updates keep the installed variant unless another is chosen; switching removes the other's files.
- `output.variants` / `--variant` choose which variants go into a package (one → no choice shown).
- .NET publish commands: self-contained `dotnet publish -c Release -r win-x64 -o publish/sc` (with
  `SelfContained=true`, `WindowsAppSDKSelfContained=true`); shared `... -p:SelfContained=false
  -p:WindowsAppSDKSelfContained=false -o publish/fd`.

## 8. Options, actions, cleanup

```jsonc
"options": [
  { "id": "driver", "label": { "en-US": "Install the XYZ driver (required)" }, "required": true },
  { "id": "autostart", "label": { "en-US": "Start with Windows" }, "default": false,
    "startup": { "name": "App", "arguments": ["--background"] } }
],
"actions": [
  { "id": "driver", "when": "afterInstall", "run": "drivers/setup.exe", "arguments": ["/S"],
    "ifOption": "driver", "timeoutSeconds": 120, "successExitCodes": [0, 3010], "onFailure": "warn",
    "skipIfInstalled": { "hive": "HKLM", "key": "SOFTWARE\\…\\Uninstall\\XYZ", "value": "DisplayVersion", "version": "2.0" },
    "fallbackArguments": ["/interactive"],
    "name": { "en-US": "XYZ driver" } }
]
```

- `startup` writes `HKCU\…\Run\<name> = "<mainExecutable>" <arguments>` while checked and removes it otherwise and on
  uninstall. Prefer it over an action for plain autostart.
- Actions: `when` = `afterInstall` (before the install is committed; `onFailure: "fail"` rolls back) or
  `beforeUninstall`. They run hidden, no shell, in a job object, with a timeout. `fallbackArguments` = visible retry;
  `fallbackRunValue` = register a Run value instead when it still fails. Effects of the program itself are never
  rolled back.
- `install.cleanup` removes leftovers of *older versions* during install (before actions). Never list the app's own
  shortcut names there. `uninstall.*` removes what the app creates at run time.
- `install.ngen`: .NET Framework assemblies to pre-compile (per-machine only).
- Updates pre-fill the user's earlier choices: shortcuts, every option, the variant, the folder and scope (from the
  install record; for an adopted Inno Setup install from its saved "Selected Tasks", mapping `desktopicon` and option ids).
  Explicit switches still win.

```jsonc
"uninstall": {
  "removeRunValues": ["App"],
  "removeData": ["{LocalAppData}\\App\\some-marker"],              // files or folders, always removed
  "userData": { "paths": ["{LocalAppData}\\App"], "default": false },  // asked on the uninstall page
  "elevated": {                                                    // changes made with administrator rights
    "ifExists": ["{CommonAppData}\\App\\Codec"],                   // only if one of these exists (empty = always)
    "traceSessions": ["App-Trace"],                                // stop + delete boot-time autologger
    "scheduledTasks": ["App\\Restore hook"],                       // folders allowed; an emptied folder is removed
    "serviceAccess": [{ "service": "SomeSvc", "rights": ["LCRPWP"] }], // drop ACEs granted to user accounts
    "removeData": ["{CommonAppData}\\App\\Codec"]
  }
}
```

Per-user uninstalls run without administrator rights: `uninstall.elevated` then asks once for UAC (interactive only;
silent uninstalls skip it and log a warning). Per-machine uninstalls already run elevated and do it directly.

Built-in updates (instead of writing an updater in the app):

```jsonc
"update": { "github": "owner/repo", "checksums": "SHA256SUMS.txt" }   // or "feed": "https://…/latest.json"
```

- The app runs `"<installDir>\uninstall.exe" /install-update /relaunch` for "Check for updates" (a small window asks
  first), or `/check-update` in the background (exit 18 = update available, 0 = none, 19 = no answer; one JSON line on
  stdout).
- GitHub: the release's Setup.exe is found by `update.asset` (default `output.fileName`; write `{Version}`, never the
  literal version). A SHA-256 is required: GitHub's asset digest and/or the `checksums` file.
- Feed: `{ "version", "url", "sha256", "size"?, "notes"? }` over https.
- The download must be the same `app.id`, the advertised (newer) version, and signed by `update.signer` if set.
- Do not add `update` for apps that keep their own updater; their `Setup.exe /SILENT` or `/S /UPDATE` calls keep working.

## 9. Validation codes

`fluentsetup validate --json` returns `{ valid, diagnostics: [{ severity, code, field, message }] }`.

| Range | Area |
|---|---|
| FS10xx | `app` |
| FS11xx | `files` (FS1101 source, FS1106 bad glob, FS1107–FS1109 bad/duplicate target paths, FS1110 main executable missing, FS1111–FS1116 variants) |
| FS12xx | `install` (scope, directory templates, offers, Windows build) |
| FS13xx | `ui` (FS1304 language, FS1306 license size, FS1311 licenseMode, FS1320–FS1328 localization) |
| FS14xx | `output` |
| FS15xx | `signing` (FS1501 no certificate, FS1504 provider, FS1505–FS1509 `trustedSigning`, FS1510–FS1516 `command`, FS1518 thumbprint, FS1519 provider needs Windows) |
| FS16xx | requirements, options, actions, cleanup, variants filters (FS1640–FS1642: a referenced file is not in the package) |
| FS17xx | `update` (FS1701 both sources, FS1702 repository, FS1703 feed URL, FS1704 asset name, FS1705 literal version in the name, FS1706 checksums, FS1707–FS1710 settings without effect, signer) |
| FS18xx | `variables` |
| FS2xxx | build (installer runtime missing, WinUI front end missing) |

## 10. Languages

- The installer follows the Windows display language; `/lang <tag>` overrides; `ui.defaultLanguage` pins one.
- Package texts per language live in `ui.localization` (a folder), one `<tag>.json` each:
  `{ "options": { id: text }, "actions": { id: text }, "variants": { id: { "label", "description" } },
  "license": "file relative to the folder", "strings": { key: text } }`. Inline texts in the project are the fallback.
- **Adding a language the installer does not ship** (e.g. `fr-FR`): `fluentsetup locale add fr-FR project.json`
  creates `locales/fr-FR.json` with every installer string in English plus the package's ids; translate the values
  (keep `{0}` placeholders and `&` mnemonics in `fallback.*` keys). The installer then offers French on French
  Windows. `validate` reports how many strings are still untranslated (FS1327).
- `strings` in a built-in language overrides single installer texts (keys: `fluentsetup locale add en-US` output or
  `shared/localization/en-US.json`).

## 11. Setup.exe command line and exit codes

`Setup.exe [/quiet | /S | /passive | /silent] [/uninstall] [/dir <path>] [/scope user|machine] [/variant <id>]
[/desktop-shortcut yes|no] [/start-menu-shortcut yes|no] [/option <id>=yes|no] [/remove-user-data] [/relaunch]
[/log <file>] [/lang <tag>] [/ui auto|winui|win32]`, and on the installed `uninstall.exe` also
`/check-update` and `/install-update [/passive | /quiet] [/relaunch]` (see section 8).

Compatible switches of other installers, so existing scripts and in-app updaters keep working:

| Inno Setup | NSIS | FluentSetup meaning |
|---|---|---|
| `/VERYSILENT` | `/S` | quiet (UAC prompt if a per-machine install needs it) |
| `/SILENT` | | passive: progress window only, closes after success |
| `/DIR="path"` | `/D=path` (last, unquoted, spaces allowed) | install folder |
| `/LOG="file"` (or `/LOG`) | | log file |
| `/LANG=english` (Inno names) | | language |
| `/TASKS="a,b"`, `/MERGETASKS="a,!b"` | | `desktopicon` = desktop shortcut, other names = option ids; `/TASKS` turns unlisted ones off |
| `/NOICONS`, `/CURRENTUSER`, `/ALLUSERS` | | no Start menu shortcut, per-user, per-machine |
| `/RELAUNCH=1` | `/UPDATE` (common custom switch) | start the app after a quiet or passive install with `install.relaunchArguments` |
| `/SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP- /NOCANCEL …` | `/NCRC`, `_?=` | accepted, no effect (setup never reboots and closes the app itself) |

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Failed (unclassified) |
| 2 | Cancelled by the user (rolled back) |
| 3 | Invalid arguments |
| 4 | Package corrupt or tampered |
| 5 | Unsupported system (Windows too old, wrong architecture) |
| 6 | Administrator rights needed but not granted |
| 7 | Disk full |
| 8 | File in use |
| 9 | Access denied |
| 10 | Downgrade blocked |
| 11 | Not installed (uninstall) |
| 12 | I/O error |
| 13 | Registry error |
| 14 | Internal error |
| 15 | Unsafe path (outside the install folder, through a link, protected location) |
| 16 | Another setup of this app is running |
| 17 | Prerequisite missing (and not installable, or `onFailure: "fail"`) |
| 18 | `/check-update`: a newer version is published |
| 19 | `/check-update`, `/install-update`: no answer from the update source, or the download was refused |

Uninstall entries: `"<dir>\uninstall.exe" /uninstall` (and `/quiet`). Logs default to
`%TEMP%\FluentSetup\<AppId>-install-<time>.log`.

## 12. Checklist before shipping

- `validate` clean; `build` then `inspect --verify`.
- Fresh install, update from the previous version, uninstall: all with `/quiet /log` and exit code 0.
- Per-machine: test the UAC path once interactively.
- `onFailure: "fail"` only for steps without which the app cannot run.
- Sign the package (`signing`): unsigned downloads trigger SmartScreen. `provider`: `signtool` (certificate store or
  `.pfx`), `trustedSigning` (Azure Artifact Signing), or `command` (any tool, e.g. osslsigncode or jsign; the only
  one that works when building on Linux or macOS).
