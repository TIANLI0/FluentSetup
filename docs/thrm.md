# Case studies: replacing NSIS and Inno Setup installers

Four apps moved from NSIS or Inno Setup to FluentSetup in one patch release each. Existing installations, including
updates started from inside the old versions, are replaced in place: same folder, same Apps & features entry, same
shortcuts, the user's autostart choice kept. Their project files are good references:

| App | Before | Project file | What it shows |
|---|---|---|---|
| [THRM](https://github.com/TIANLI0/THRM) 3.8.1 | NSIS, per-machine | [`build/windows/installer/fluentsetup.json`](https://github.com/TIANLI0/THRM/blob/main/build/windows/installer/fluentsetup.json) | Prerequisites, a driver (required option + action with fallbacks), autostart through a scheduled task, NGen, renamed products, `$(Version)` from CI |
| [EncoDeck](https://github.com/TIANLI0/EncoDeck) 1.15.1 | Inno Setup, per-user or per-machine | [`installer/fluentsetup.json`](https://github.com/TIANLI0/EncoDeck/blob/main/installer/fluentsetup.json) | Inno adoption, `/SILENT` in-app updates of a per-machine install (UAC), app-managed autostart, undoing what the app did with administrator rights |
| [PinyinFlux](https://github.com/TIANLI0/PinyinFlux) 0.3.3 | Inno Setup, per-user | [`installer/fluentsetup.json`](https://github.com/TIANLI0/PinyinFlux/blob/main/installer/fluentsetup.json) | An "Enable now" choice that follows the current state on updates (`detect`) and is off in scripted installs (`quietDefault`) |
| [MediaDuet](https://github.com/TIANLI0/MediaDuet) 1.10.3 | NSIS, per-user | [`windows/packaging/fluentsetup.json`](https://github.com/TIANLI0/MediaDuet/blob/main/windows/packaging/fluentsetup.json) | Same uninstall key (`adoptExistingInstall`), Start menu folder, `/S /UPDATE`, an uninstall step that must succeed |

What every migration needed:

1. **The same identity.** The old uninstall key is adopted: `adoptExistingInstall` when the new `app.id` equals it
   (NSIS `THRM`, `MediaDuet`), `legacyUninstallKeys` otherwise (Inno `"{GUID}_is1"`). The folder comes from the key, so
   custom folders are kept. `install.cleanup.removeFiles: ["unins*.exe", "unins*.dat", "unins*.msg"]` removes Inno's
   own uninstaller, which would otherwise stay behind.
2. **The same release asset name.** Shipped updaters look for exact names (`EncoDeck-Setup-<v>-win-x64.exe`,
   `THRM-amd64-installer.exe`, `MediaDuet-<v>-x64-setup.exe`); `output.fileName` keeps them.
3. **The same command line.** Inno `/SILENT … /RELAUNCH=1` (a progress window, then the app starts again), NSIS
   `/S /UPDATE`, elevated `/S`: all understood, see [project-file.md](project-file.md#setupexe-command-line).
4. **The version from the build.** `"version": "$(Version)"` with `fluentsetup build -D Version=<v>`; in GitHub
   Actions `uses: TIANLI0/FluentSetup@v1.0.1` provides the CLI.

## THRM: NSIS script → FluentSetup project

[THRM](https://github.com/TIANLI0/THRM) is a Wails v3 (Go + WebView2) desktop app with a separate core service, a
.NET Framework temperature bridge and the PawnIO driver. Up to 3.8.0 it shipped with an NSIS installer
(`build/windows/installer/project.nsi`). Since 3.8.1, `wails3 task package` builds it with:

```powershell
fluentsetup build build\windows\installer\fluentsetup.json -D Version=3.8.1 -D PawnIOVersion=2.2.0.0 -o build\bin
# -> build\bin\THRM-amd64-installer.exe
```

| NSIS installer | FluentSetup project |
|---|---|
| Per-machine install into `Program Files\THRM`, admin required | `install.scope: "machine"`; UAC shield on the install button; privileged work only in the elevated worker. |
| Languages: Simplified Chinese, English, Japanese, Korean | The installer UI ships all four (`shared/localization`); THRM's own texts are in `locales/<lang>.json`. |
| License page with "I accept" checkbox, shown on every install | `ui.licenseMode: "always"`, per-language license file in each locale file. |
| Components page: main program (required), "Start with Windows" (selected), "Install PawnIO" (required) | `options`: `pawnio` with `required: true` (shown checked and locked), `autostart` (checked by default). Shown on the first page. |
| `.NET Framework 4.7.2` check, abort with a message | `requirements.netFramework: "4.7.2"` with `netFrameworkSetup.helpUrl`; setup stops with exit code 17 and the link. |
| WebView2 bootstrapper when the runtime is missing, result ignored | `requirements.webView2` with the bundled `MicrosoftEdgeWebview2Setup.exe` and `onFailure: "warn"`. |
| `taskkill` of THRM, THRM Core, bridges, legacy BS2PRO executables and `SpaceStationService.exe` | `install.closeProcesses` (WM_CLOSE first, then terminate). |
| Detect an older install under the legacy uninstall keys (`BS2PRO-Controller`, `TIANLI0BS2PRO`, …) and upgrade it in place, then delete those keys | `install.legacyUninstallKeys`; the keys are removed after a successful install. The current `THRM` key is adopted through `adoptExistingInstall`. |
| Delete scheduled tasks `THRM`, `BS2PRO-Controller`, `BS2PRO-Core`; legacy Run values; legacy shortcuts | `install.cleanup` (runs before the autostart action re-registers the task). |
| PawnIO: skip if the installed version is ≥ the bundled one; silent install; exit code 183 = already installed; on failure retry interactively; a failure only warns | `actions[pawnio]`: `skipIfInstalled` with `$(PawnIOVersion)` from `pawnio.env`, `successExitCodes: [0, 183]`, `fallbackArguments: ["-install"]`, `onFailure: "warn"`. |
| Autostart: `THRM Core.exe --install-autostart`; on failure write `HKCU\…\Run\THRM = "THRM Core.exe" --autostart` | `actions[autostart]` with `ifOption: "autostart"` and `fallbackRunValue`. On updates `options[autostart].detect` keeps it as the user set it in THRM (NSIS re-enabled it every time). |
| `ngen install /queue` for the bridge; `ngen uninstall` before replacing or removing it | `install.ngen: ["bridge/THRM TempBridge.exe"]`. |
| Uninstall: close processes, delete tasks, Run values, startup shortcuts, WebView2 data, `RMDir /r` the install folder, ask about settings; keep PawnIO (shared driver) | `uninstall.removeScheduledTasks`, `removeRunValues`, `removeShortcuts`, `removeData` (including `{InstallDir}\logs`, `telemetry`, `records`); `uninstall.userData` asks about `{InstallDir}\config` and `themes`. PawnIO is not removed. |
| Downgrades allowed (nightly ↔ stable) | `install.allowDowngrade: true`. |
| THRM's in-app updater runs `THRM-amd64-installer.exe /S` elevated and starts THRM again once that process is gone | `/S` is quiet mode; an elevated quiet install runs in the same process, so the updater's wait works unchanged. |

Every action runs a file from the package's own payload, with `CreateProcess`, without a shell, inside a job object and
with a timeout. Prerequisite downloads (not used by THRM) are https-only and verified by SHA-256. See
[project-file.md](project-file.md) and [security.md](security.md).

## Differences that remain

- **`%TEMP%\THRM`** is not removed (temporary files; the elevated uninstaller would see the administrator's `%TEMP%`).
- **Config backup/restore** (`config.json`, `settings.ini` in the install folder) is not needed: FluentSetup only
  replaces files it ships and never deletes files it did not install.
- **Install folder detection without any registry entry** (NSIS also probed a few folder names) is not repeated.
- **Size.** The FluentSetup package is 19.8 MB (NSIS: 21.7 MB). Installed, FluentSetup keeps two files NSIS does not:
  the WebView2 bootstrapper under `redist\` (1.7 MB; NSIS runs it from a temporary folder) and a larger
  `uninstall.exe` (1.4 MB, it carries the WinUI front end). See [size-report.md](size-report.md).
