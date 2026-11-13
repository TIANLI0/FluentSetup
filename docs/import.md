# Importing Inno Setup and NSIS scripts

`fluentsetup import` turns an existing Inno Setup script (`.iss`) or NSIS script (`.nsi`, `.nsh`) into a FluentSetup
project. It writes two files:

- `fluentsetup.json`: the project, with every setting the script had that FluentSetup can express.
- `import-report.md`: a checklist, line by line, of what needs manual work, what behaves somewhat differently and
  what was mapped. Nothing in the script is dropped silently: every section, directive, entry and command ends up in
  the project, in the report, or in both.

```powershell
fluentsetup import installer\setup.iss                 # writes installer\fluentsetup.json and installer\import-report.md
fluentsetup import build\installer.nsi -o packaging    # writes into another folder; paths are made relative to it
fluentsetup import setup.iss --force --json            # overwrite an earlier import, print the result as JSON
```

| Option | Meaning |
|---|---|
| `-o`, `--output <folder>` | Folder for `fluentsetup.json` and `import-report.md` (created if needed). Default: the script's folder. Relative paths in the project (`files.source`, `app.icon`, `ui.license`, …) are relative to this folder. |
| `--force` | Overwrite an existing `fluentsetup.json` or `import-report.md`. Without it the command refuses (exit code 2). |
| `--json` | Print `{ format, script, project, report, app, counts, notes }` instead of the summary. |

The format is detected from the extension, else from the content. Exit codes: `0` imported (also when manual work is
left), `1` the script could not be read (I/O error, binary file), `2` usage error (missing or unrecognised script,
unknown option, existing files without `--force`).

The Builder and other tools use the same code through `FluentSetup.Builder.Core.Import.ProjectImporter`:
`Import(scriptPath, outputDirectory)` returns an `ImportResult` (`Project`, `Notes` with severity `Info`, `Warning` or
`Manual`, file and line), `Save(result, overwrite)` writes both files.

## After the import

1. Work through **To do by hand** and **To check** in `import-report.md`.
2. Build the app, so the folders the script installed from exist. A missing build output is only a note in the report.
3. `fluentsetup validate fluentsetup.json`, then `fluentsetup build fluentsetup.json`.
4. Test on a test PC or VM: a fresh install, an upgrade over the version the old installer installed (the project
   adopts it, see below), and the uninstall.

[thrm.md](thrm.md) describes four real migrations; their project files show where an imported project usually ends up.

## How values are carried over

- **Variables.** Inno `#define Name "value"` and NSIS `!define NAME "value"` used in project text become
  [`variables`](project-file.md#variables): `AppVersion={#MyAppVersion}` gives `"version": "$(MyAppVersion)"` with
  `"variables": { "MyAppVersion": "2.4.1" }`, so a release build keeps passing the version:
  `fluentsetup build fluentsetup.json -D MyAppVersion=2.4.2`. A define built from others (`"App-" + Version`,
  `"App-${VERSION}"`) is written out with those as variables. Values the script computes when it is compiled
  (`GetVersionNumbersString(...)`, `!define /date`, `!searchparse`) get a placeholder and a manual note; a file version
  that can be read already is used as the starting value.
- **Paths.** Source paths (relative to the script) are rewritten relative to the project folder; variables they used
  stay (`dist\App-$(Version)-win-$(Arch)` keeps following `-D Version=…`). Paths in the install
  folder (`{app}\x`, `$INSTDIR\x`) become relative paths; in registry values, environment variables, config files and
  arguments `{app}`/`$INSTDIR` becomes `{InstallDir}`.
- **Identity.** FluentSetup upgrades the old installation in place through its uninstall key (see
  [`adoptExistingInstall` and `legacyUninstallKeys`](project-file.md#install)):

  | Script | Project |
  |---|---|
  | Inno `AppId={{GUID}` (or no `AppId`: the AppName) | `install.legacyUninstallKeys: ["{GUID}_is1"]`; `app.id` is the AppId if it is a valid id, else `Publisher.Name`; `install.cleanup.removeFiles: ["unins???.exe", "unins???.dat", "unins???.msg"]` removes Inno's uninstaller |
  | NSIS `WriteRegStr … "…\Uninstall\<key>"` with a valid id as key | `app.id: "<key>"` with `install.adoptExistingInstall: true` |
  | NSIS uninstall key that is not a valid id (spaces, braces) | `install.legacyUninstallKeys: ["<key>"]`, `app.id` from publisher and name |
  | NSIS `WriteUninstaller "$INSTDIR\uninst.exe"` | `install.cleanup.removeFiles: ["uninst.exe"]` (unless it is `uninstall.exe`, which FluentSetup replaces) |

- **Files.** The folder most entries install from (wildcards into the install folder weigh most: that is the build
  output) becomes `files.source`; its entries become `files.include` globs (`./App.exe`, `**/*`, `plugins/**/*.dll`),
  their exclusions `files.exclude`. Files from other folders become `files.extra`; a wildcard such as `*.txt` from
  another folder is expanded to the files that exist when importing, or reported. Files installed only if missing or
  kept on uninstall become `files.preserve`. FluentSetup's default exclusions (`**/*.pdb` …) stay.
- **Options.** Inno tasks and components, and NSIS sections on a components page, become `options` when something the
  project can make conditional depends on them: shortcuts, file types, protocols, registry values, environment
  variables, config files, actions or a Run value (`options[].startup`). A task or section that only switches the app's
  own desktop or Start menu shortcut becomes that shortcut's checkbox (`install.desktopShortcut: "optionalOff"` …). Files
  cannot depend on an option; they are always installed and the report says so.
- **Validation.** The importer validates the project. Errors that only come from files that do not exist yet (build
  output, icon, license) are notes; other problems are repaired (and reported) so the project loads in the Builder.

## Inno Setup

| Script | Project |
|---|---|
| `#define`, `{#Name}`, `#include` (relative, nested up to 8 levels), `#if`/`#ifdef`/`#ifndef`/`#ifexist`/`#elif`/`#else`, string functions (`StringChange`, `AddBackslash`, `Copy`, `Lowercase` …), `GetFileVersion`/`GetVersionNumbersString`/`GetStringFileInfo` | variables / evaluated text; `#sub`, `#for`, `#emit` and macros are reported |
| `AppName`, `AppVersion`, `AppPublisher`, `AppPublisherURL` (else `AppSupportURL`), `AppComments` | `app.name`, `app.version` (fixed if needed: `v1.2` → `1.2`), `app.publisher`, `app.website`, `app.description` |
| `AppId` | see *Identity* |
| `PrivilegesRequired` (`admin` is Inno's default) / `PrivilegesRequiredOverridesAllowed` | `lowest` → scope `user`; `admin` → `machine`; overrides allowed → `userOrMachine` with `defaultScope` from `PrivilegesRequired` |
| `DefaultDirName` | `{autopf}\X` → `userDir {LocalAppData}\Programs\X`, `machineDir {ProgramFiles}\X`; `{localappdata}`/`{userpf}` → `userDir`; `{pf}`/`{commonpf}` → `machineDir`; the app name and publisher become `{AppName}`/`{Publisher}` |
| `DefaultGroupName` | `install.startMenuFolder` (one level) |
| `DisableDirPage=yes` | `install.allowCustomDir: false` |
| `OutputBaseFilename`, `OutputDir` | `output.fileName` (`{Version}`/`{AppName}` when they come from the version and name variables), `output.directory` |
| `SetupIconFile`, `LicenseFile` (also per language in `[Languages]`) | `app.icon`, `ui.license` / `ui.licenses` (RTF is reported: FluentSetup shows plain text) |
| `ArchitecturesAllowed`, `ArchitecturesInstallIn64BitMode` | `output.architecture` (`x64`, or `arm64` for Arm64-only scripts; 32-bit scripts are reported) |
| `MinVersion` | `install.minWindowsBuild` (at least 17763) |
| `Compression` | `output.compression` (`ultra` → `max`, `zip`/`fast` → `fast`) |
| `CloseApplications=no`, `AppMutex` | `install.restartManager: "off"`; note (FluentSetup closes the app) |
| `SignTool`, `Password`, `Encryption`, `InfoBeforeFile`, `Uninstallable=no`, … | manual notes; wizard look and Inno bookkeeping (`WizardStyle`, `SolidCompression`, `UsePreviousAppDir` …) are listed as not needed |
| `[Languages]` | the four built-in languages are noted; others get a `fluentsetup locale add` note; a single non-English built-in language sets `ui.defaultLanguage` |
| `[CustomMessages]`, `{cm:…}` | option labels per language (built-in messages such as `CreateDesktopIcon` included) |
| `[Tasks]`, `[Components]` (`fixed` → `required`; default from the first `[Types]` entry) | `options` or shortcut checkboxes, see *Options* |
| `[Files]` (`recursesubdirs`, `Excludes`, `onlyifdoesntexist`/`uninsneveruninstall`/`confirmoverwrite` → preserve, `restartreplace` → `install.lockedFiles: "reboot"` for per-machine) | `files.*`; files for `{tmp}` are matched with `[Run]`; other targets outside `{app}`, `DestName`, `external`, `regserver`, fonts and GAC are reported |
| `[Dirs]` in `{app}` | `files.emptyDirectories` (`Permissions` are reported) |
| `[Icons]` | the shortcut named like the app to `app.mainExecutable` → `install.startMenuShortcut` (+ `startMenuFolder`, `AppUserModelID` → `app.appUserModelId`) and `install.desktopShortcut`; others → `shortcuts` (`{group}`, `{autoprograms}`, `{autodesktop}`, `{autostartup}`/`{userstartup}`/`{commonstartup}`; `Tasks` → `ifOption`); "Uninstall" shortcuts and web links are not created |
| `[Registry]` `Software\Classes\.ext` (default value or `OpenWithProgids`) + `Software\Classes\<ProgID>` (`DefaultIcon`, `shell\open\command`) | `associations` (ProgIDs FluentSetup does not accept are renamed and reported) |
| `[Registry]` `Software\Classes\<scheme>` with `URL Protocol` | `protocols` |
| `[Registry]` `…\App Paths\<main exe>` | `install.appPaths: true` |
| `[Registry]` `…\CurrentVersion\Run` value starting the main program | that task's `options[].startup` (an unconditional one becomes a new checked option) |
| `[Registry]` `Environment` / `…\Session Manager\Environment` | `environment`; `Path` with `{app}\…` → `install.addToPath` |
| `[Registry]` `Software\<Vendor>\…` (HKA, or HKCU/HKLM matching the scope) | `registry` (`uninsdeletekey` → `uninstall: "key"`, `uninsdeletevalue` → `"values"`, no flag → `"keep"`; Pascal hex `$1F` → `0x1F`); a key without values but `uninsdeletekey` → `uninstall.removeRegistryKeys` (HKCU) or `uninstall.elevated.removeRegistryKeys` (HKLM) |
| `[Registry]` elsewhere (`Policies`, `Microsoft`, context menus, COM, HKLM in a per-user install …) | manual notes |
| `[Run]` `postinstall` of the main program | `install.launchAfterInstall` (`unchecked` → `optionalOff`) |
| `[Run]` `postinstall` of another program of the app that is waited for ("Enable now") | an option (label from `Description`, `skipifsilent` → `quietDefault: false`) plus an `afterInstall` action with that `ifOption` |
| `[Run]` program of the app | `actions` (`afterInstall`, `onFailure: "warn"`: Inno did not check exit codes; `Tasks` → `ifOption`; `StatusMsg` → `name`) |
| `[Run]` main program with `nowait`, not `postinstall` (an in-app update starting the app again) | `install.relaunchArguments` (used with `/relaunch`, Inno `/RELAUNCH=1`); other `nowait` programs keep running and are reported instead of becoming actions |
| `[UninstallRun]` main program with `--exit`, `/quit`, `-close` … | `install.closeCommand` (also used before updates) |
| `[Run]` `vc_redist.*.exe`, `MicrosoftEdgeWebview2Setup.exe`, `*-runtime-<v>-win-<arch>.exe`, `ndp48-*.exe` | `requirements.vcRedist`/`webView2`/`dotNet`/`netFramework` with `auto: "bundle"` |
| `[Run]`/`[UninstallRun]` `taskkill /im X` / `schtasks /delete /tn X` | `install.closeProcesses` / `install.cleanup.removeScheduledTasks` or `uninstall.removeScheduledTasks`; `sc`, `netsh`, `regsvr32`, `cmd` … are reported with the matching section (`services`, `firewall` …) |
| `[UninstallRun]` program of the app | `actions` (`beforeUninstall`) |
| `[UninstallDelete]` `{app}\X`, `{localappdata}\X`, `{userappdata}\X`, `{commonappdata}\X` | `uninstall.removeData` (`{app}` itself and wildcards are reported) |
| `[InstallDelete]` files in `{app}` | `install.cleanup.removeFiles` |
| `[INI]` in `{app}` or the data folders | `configFiles` (`format: "ini"`) |
| `[Code]` | one manual note with the event functions, plus hints for what is recognised (custom pages → `inputs`, .NET/WebView2/VC++ checks → `requirements`, `Exec` → `actions`, deleting data → `uninstall.*` …) |
| `Check:`, `BeforeInstall:`, `AfterInstall:`, `Languages:` on entries | imported without the condition, reported |

## NSIS

| Script | Project |
|---|---|
| `!define` (`/ifndef`, `/math`), `${NAME}`, `!include` (relative, nested up to 8 levels; standard headers such as `MUI2.nsh` are known), `!ifdef`/`!ifndef`/`!if`/`!else`, `!macro`/`!insertmacro` of the script's own macros, `!getdllversion` | variables / expanded text |
| `Name`, `!define` of the version / `DisplayVersion` / `VIProductVersion`, publisher (`Publisher`, `VIAddVersionKey CompanyName`), website (`URLInfoAbout`, `HelpLink`), `VIAddVersionKey FileDescription` | `app.*` |
| `RequestExecutionLevel` (`admin` → `machine`, `user` → `user`, `highest` → `userOrMachine`); `MultiUser.nsh` | `install.scope` |
| `InstallDir` (`$PROGRAMFILES*`, `$LOCALAPPDATA\Programs`, `$LOCALAPPDATA`, `$APPDATA`) | `install.userDir` / `machineDir` |
| `OutFile` | `output.fileName`, `output.directory` |
| `Icon` / `MUI_ICON`, `LicenseData` / `MUI_PAGE_LICENSE` / `LicenseLangString` | `app.icon`, `ui.license` / `ui.licenses` |
| `MUI_FINISHPAGE_RUN` (`MUI_FINISHPAGE_RUN_NOTCHECKED`) | `install.launchAfterInstall` |
| `MUI_LANGUAGE`, `LangString` | language notes; section labels per language |
| `Section` names on a components page (`/o` → unchecked, `SectionIn RO` → `required`) | `options` (see *Options*); without a components page every section is always installed and `/o` sections are reported as never installed |
| `SetOutPath` + `File` (`/r`, `/x`, `SetOverwrite off` → preserve; `/oname` is reported) | `files.*` as for Inno Setup |
| `CreateDirectory "$INSTDIR\X"` | `files.emptyDirectories` |
| `CreateShortCut` in `$SMPROGRAMS`, `$DESKTOP`, `$SMSTARTUP` | the app's own shortcuts or `shortcuts`, as for Inno Setup |
| `WriteRegStr`/`WriteRegExpandStr`/`WriteRegDWORD` (`HKLM`, `HKCU`, `HKCR`, `SHCTX`) | the same rules as Inno `[Registry]`; `…\Uninstall\<key>` gives the identity |
| `WriteINIStr` | `configFiles` |
| `ExecWait`/`nsExec::Exec*` of a program of the app | `actions` (`afterInstall`, or `beforeUninstall` in the uninstall section; `onFailure: "warn"`, reported so you set exit codes); `Exec` of the main program → `install.relaunchArguments`, of others → reported; the main program with `--exit` … in the uninstall section → `install.closeCommand` |
| Prerequisite installers and system tools | as for Inno `[Run]` |
| Uninstall section: `RMDir /r` of a folder the script created in `$INSTDIR` (`logs`), `$APPDATA\X`, `$LOCALAPPDATA\X` | `uninstall.removeData`; in an optional `un.` section on an uninstall components page → `uninstall.userData` (the checkbox) |
| Uninstall section: `DeleteRegKey` of a key the script writes / `DeleteRegValue` of a Run value | `registry[].uninstall: "key"` / `uninstall.removeRunValues` (nothing for the startup option's own value) |
| Uninstall section: deleting the app's files, shortcuts and uninstall entry | not needed (listed once) |
| `DeleteRegKey` of another product's uninstall entry while installing; `Delete "$INSTDIR\old.dll"` | `install.cleanup.removeUninstallKeys`; `install.cleanup.removeFiles` |
| Functions (`.onInit`, `un.onInit`, page callbacks), `Call`, plug-ins, `Page custom` | manual notes with hints (admin checks, previous-version lookups, custom pages → `inputs` …) |
| LogicLib `${If}`/`${Else}`, `IfFileExists`, `StrCmp`, `Goto` | the first branch is imported, jumps are not evaluated; reported per section |

## Limitations

- Script logic is not run: Inno `[Code]`, ISPP `#sub`/`#for`, NSIS functions, jumps and plug-ins are reported with
  hints, not converted. Conditions on entries (`Check:`, `${If}`) are dropped and reported.
- Files are always installed: an option cannot make files conditional, so files that depended on a task, component or
  section are installed in every case.
- FluentSetup writes only below the install folder, `Software\<Vendor>` keys of the install scope's hive and its own
  declarative sections (file types, protocols, environment, PATH, App Paths). Writes into other folders or shared
  registry trees, COM registration, context-menu verbs, fonts, ACLs and RunOnce entries need another solution.
- Renamed files (`DestName`, `/oname`), web-link shortcuts and Quick Launch shortcuts are not supported.
- Values computed when the script is compiled (file versions of a build that does not exist yet, dates, environment
  variables) start as placeholders; set them in `variables` or with `-D`.
- 32-bit installations: packages are x64 or arm64 and install into the native Program Files.
- Wizard pages, texts and images are not imported; FluentSetup's interface is its own (`ui.*` changes its texts).
- Scripts in an ANSI code page other than Windows-1252 may show wrong non-English characters; save them as UTF-8.
