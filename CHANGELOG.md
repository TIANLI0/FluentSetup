# Changelog

All notable changes to FluentSetup are listed here. Versions follow [Semantic Versioning](https://semver.org/); the
release workflow publishes the section of the pushed tag as the release notes.

## [Unreleased]

### Added

- Apps & features **Modify** ("Change <App>" page) checks the package's update source and, when a newer version is
  published, shows "A newer version (x) is available" with an **Update** button (runs
  `uninstall.exe /install-update /passive`). Both front ends, all 11 languages.

### Changed

- An unsigned (or untrusted) setup no longer shows "Unknown publisher" on the first page; a verified signature still
  shows "Verified publisher: <name>". Windows already warns about unsigned files in SmartScreen and UAC.

## [1.2.1] - 2026-11-14

### Fixed

- Texts and licenses a package provides per language fall back to the same language in another region before English
  (Traditional Chinese uses `zh-TW`/`zh-HK`, then `zh-CN`; `de-AT` uses `de-DE`). With the Traditional Chinese
  installer table added in 1.2.0, packages with only `zh-CN` labels showed English to Traditional Chinese users.
- Uninstall removes the `HKCU\Software\Classes\AppUserModelId\<id>` key an unpackaged app registers for the
  AppUserModelIDs the package declares.
- `uninstall.removeRegistryKeys` logs the keys it removed (it removed them but reported a failure).
- `fluentsetup <command> --help` prints the usage instead of an error.

### Added

- `uses: TIANLI0/FluentSetup@latest` follows the newest release (the release workflow moves the `latest` tag along
  with `v1`).
- `FLUENTSETUP_CACHE` moves the cache of bundled Microsoft installers, for isolated builds.
- Releases push the NuGet packages with nuget.org Trusted Publishing (no stored API key); a manual `nuget-only` run
  pushes the packages of an existing release.

## [1.2.0] - 2026-11-14

### Added

- Desktop integration, all declarative and removed on uninstall: `associations` (file types with icon, open command,
  "Open with" and Default apps entry), `protocols` (URL schemes), `shortcuts` (more shortcuts: other programs,
  arguments, desktop or Startup folder, per-shortcut AppUserModelID), `app.appUserModelId`/`toastActivatorClsid` on
  the app's own shortcuts, `registry` values below `Software\<Vendor>`, `environment` variables, `install.appPaths`,
  Windows `services` and `firewall` rules (per-machine installs). Registry changes are journaled and rolled back with
  the files; updates remove what a version no longer declares.
- `inputs`: a Settings page in both interfaces (text, password, choice; required and pattern checks; remembered for
  updates) and `/input id=value`, used as `{Input:id}` in registry values, environment variables, arguments and
  `configFiles` (JSON or INI files updated in place). Placeholders `{InstallDir}`, `{MainExecutable}`, `{AppVersion}`,
  `{AppId}`.
- Modify and Repair: Apps & features "Modify" runs `uninstall.exe /modify` (shortcuts, options and inputs can be
  changed later; the steps of options that were turned on run) and offers Repair (installed files checked against the
  install record; a damaged installation is reinstalled from the original Setup.exe or the update source).
  `/modify` and `/repair` work silently too.
- Prerequisites: `requirements.vcRedist` (Visual C++ 2015-2022 v14) and `requirements.custom` (detected by registry
  value or file). `"auto": "bundle"` stores Microsoft's official installer in Setup.exe at build time (downloaded once a
  day, signature checked, never installed into the app folder); `"auto": "download"` lets setup download it and accept
  it by its Microsoft signature. `download.signer` pins any vendor download to its publisher.
- Restart Manager (`install.restartManager`): programs that hold the app's files are closed before an update or
  uninstall and restarted afterwards; the first page lists them. `install.lockedFiles: "reboot"` replaces files that
  are still in use at the next restart (per-machine; exit code 3010).
- Signing: `signing.signUninstaller` (on by default) — the uninstall.exe setup writes carries the package's signature.
  Signing providers: signtool with a certificate, Azure Trusted Signing, or any signing command (also on Linux and
  macOS). The first page shows "Verified publisher: <name>" or "Unknown publisher".
- `ui.banner`: an image at the top of the first page in both interfaces.
- Installer languages: Traditional Chinese, German, French, Spanish, Portuguese (Brazil), Russian and Italian, in
  addition to English, Simplified Chinese, Japanese and Korean.
- CLI: `fluentsetup import <setup.iss|script.nsi>` turns an Inno Setup or NSIS script into a project plus a migration
  report; `fluentsetup winget` writes winget manifests for a built Setup.exe; `fluentsetup versions
  <webview2|windowsappruntime|dotnet|vcredist>` lists current versions from Microsoft; `build --refresh-installers`.
- NuGet packages: `FluentSetup.Builder.Core` (library), `FluentSetup.Cli` (.NET tool) and `FluentSetup.MSBuild`
  (`dotnet publish` produces Setup.exe). The CLI also builds installers on Linux and macOS
  (`fluentsetup-<version>-linux-x64.tar.gz`).
- Builder: pages for every new setting, version pickers filled from Microsoft (WebView2, Windows App SDK, .NET, VC++),
  and a preview with the banner, publisher line and Settings page.
- Release assets carry the ARM64 installer runtime too (`output.architecture: "arm64"` / `--arch arm64` now work with
  the released CLI and the GitHub Action). CI builds ARM64 on every push and runs the native tests and the
  end-to-end installs on an ARM64 runner.

### Changed

- A rollback that cannot undo every step keeps only the remaining steps in its journal, so the next run never undoes a
  step twice (which could delete a file the first rollback had restored); transient file locks are retried.
- Revoked or explicitly distrusted signatures are refused (security gap G5). Processes an action leaves running end
  with it (G8). Duplicate JSON keys are refused (G14). The single-instance lock fails closed (G7). Test hooks only work
  in packages built for FluentSetup's own tests (G11). The PFX password no longer appears on signtool's command line (G9).
- The Windows App SDK version accepts 10.x and later. UI tests must pass in CI.
- An elevated Setup.exe with UAC turned off (or as the built-in Administrator) now starts the app after installing
  even when no desktop shell is running, for example on a build server; with UAC on it still never starts it elevated.

## [1.1.0] - 2026-11-13

### Added

- Built-in updates for packaged apps. A project `update` section names the source (`github` releases, or a JSON
  `feed`); the installed `uninstall.exe` then understands `/check-update` (exit code 18 = a newer version is
  published, one JSON line on stdout) and `/install-update` (a small window: check, ask, download with progress;
  `/passive` and `/quiet` ask nothing). The download must come over https, match the published SHA-256 (GitHub's asset
  digest and/or a checksums file such as `SHA256SUMS.txt`, or the feed's), be a FluentSetup package of the same app
  id and architecture with exactly the advertised, newer version, and carry the signature `update.signer` pins. The
  new Setup.exe then runs with `/passive` or `/quiet` and updates like any other upgrade (journaled, rolled back on
  failure, closes and with `/relaunch` restarts the app).
- Exit codes 18 (`UpdateAvailable`) and 19 (`UpdateFailed`); validation codes FS1701–FS1710.
- Builder: "Built-in updates" on the Upgrades page.
- FluentSetup's own installation has an update source, so its `uninstall.exe /install-update` works too.

### Changed

- Builder: the overview shows the publisher and version with `$(Name)` variables replaced.

## [1.0.1] - 2026-11-13

### Fixed

- GitHub Action: on PowerShell 7 the download of `SHA256SUMS.txt` was read as bytes, so `uses: TIANLI0/FluentSetup@v1.0.0`
  stopped with "SHA256SUMS.txt has no entry". CI now runs the action against the latest release.

## [1.0.0] - 2026-11-13

First public release.

### Installer runtime (`Setup.exe`)

- Native C++20 bootstrapper with a statically linked VC runtime and a built-in zstd decoder: no .NET, VC++
  redistributable, WebView2 or network access needed at install time. Under 1 MiB of overhead for a typical app.
- WinUI 3 front end in the style of the Windows App Installer, with a Win32 fallback that has the same layout and
  takes over without restarting anything. English, Simplified Chinese, Japanese and Korean built in; any other language
  from the package. Light, dark and high-contrast themes, per-monitor DPI, UI Automation.
- Per-user or per-machine installs, upgrade, reinstall, downgrade protection, Apps & features registration, shortcuts,
  a self-removing `uninstall.exe`. A write-ahead journal and backups give byte-for-byte rollback on failure or cancel.
- Per-file SHA-256 and Authenticode checks; per-machine work only in a separately elevated worker that installs its
  own embedded package; unsafe install paths refused.
- Prerequisites with version checks (.NET Framework, .NET runtimes, WebView2, Windows App Runtime), installed from a
  bundled installer or a verified https download.
- Declarative extensions: close running processes (or ask the app to exit), options and required components,
  actions from the app's own files with fallbacks, NGen, PATH, uninstall cleanup (scheduled tasks, Run values,
  shortcuts, data, registry keys, changes the app made with administrator rights), user data the user decides about.
- Variants: one package can carry a self-contained and a framework-dependent build; shared files are stored once.

### Replacing Inno Setup and NSIS installers

- Adopts an existing installation by its uninstall key (`adoptExistingInstall`, `legacyUninstallKeys`) and keeps its
  folder; upgrades pre-fill the choices an Inno Setup install recorded.
- Understands the command lines in-app updaters already use: Inno Setup `/SILENT` (progress window only, closes after
  success), `/VERYSILENT`, `/DIR=`, `/LOG=`, `/TASKS=`, `/RELAUNCH=1`, and NSIS `/S`, `/D=`, `/UPDATE`.
- A quiet per-machine update started without administrator rights shows the UAC prompt instead of failing.
- `install.cleanup.removeFiles` removes the old uninstaller (`unins000.exe`), `install.startMenuFolder` keeps a Start
  menu folder, `options[].detect` updates an app-managed autostart in its current state, `options[].quietDefault`
  keeps scripted first installs from doing what a checkbox offers.
- `{InstallDir}\…` in uninstall data paths for logs and caches the app writes into its own folder.

### Building packages

- `fluentsetup` CLI: `build`, `validate --json` (codes and field paths), `inspect --verify`, `init`, `locale add`,
  `guide` (the complete authoring guide, also for AI tools) and `schema`.
- `$(Name)` variables in the project file, set from CI with `fluentsetup build -D Version=1.2.3`.
- WinUI 3 Builder app that edits every setting of the project file, with live validation, file preview, installer
  preview, build history and package inspection.
- GitHub Action: `uses: TIANLI0/FluentSetup@v1` installs the CLI on a Windows runner.
- Updates of FluentSetup itself: the Builder checks GitHub Releases once a day (can be turned off in Settings) and
  installs a new version with a progress window, then opens again; `fluentsetup update [--check]` on the command line.
  The download must match `SHA256SUMS.txt` and GitHub's own digest.
