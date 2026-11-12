# Changelog

All notable changes to FluentSetup are listed here. Versions follow [Semantic Versioning](https://semver.org/); the
release workflow publishes the section of the pushed tag as the release notes.

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
