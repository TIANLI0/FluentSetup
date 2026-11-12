# Case study: packaging THRM

[THRM](https://github.com/TIANLI0/THRM) is a Wails v3 (Go + WebView2) desktop app with a separate core service, a
.NET Framework temperature bridge and the PawnIO driver. It used to ship with an NSIS installer
(`build/windows/installer/project.nsi` in the THRM repository). [`samples/THRM`](../samples/THRM) packages the same build
output with FluentSetup and reproduces what the NSIS script does. It is the reference for the declarative install
extensions.

```
samples/THRM/
  fluentsetup.json       structure: files, install, requirements, options, actions, uninstall, ui
  locales/en-US.json     option labels, action names and license file per language
  locales/zh-CN.json
  locales/ja-JP.json
  locales/ko-KR.json
```

> The sample reads THRM's build output from absolute paths (`D:/develop/BS2PRO-Controller/build/...`). Adjust
> `files.source`, `files.extra`, `app.icon`, `ui.license` and the `license` entries in `locales/*.json` to your
> checkout. Build THRM first (`build_bridge.bat`, then `build.bat` in the THRM repository), then:

```powershell
src\CLI\bin\Release\net10.0-windows10.0.19041.0\fluentsetup.exe build samples\THRM\fluentsetup.json
# -> out\thrm\THRM-3.8.0-Setup.exe
```

## NSIS script → FluentSetup project

| NSIS installer | FluentSetup project |
|---|---|
| Per-machine install into `Program Files\THRM`, admin required | `install.scope: "machine"`; UAC shield on the install button; privileged work only in the elevated worker. |
| Languages: Simplified Chinese, English, Japanese, Korean | The installer UI ships all four (`shared/localization`); THRM's own texts are in `locales/<lang>.json`. |
| License page with "I accept" checkbox, shown on every install | `ui.licenseMode: "always"`, per-language license file in each locale file. |
| Components page: main program (required), "Start with Windows" (selected), "Install PawnIO" (required) | `options`: `pawnio` with `required: true` (shown checked and locked), `autostart` (checked by default). Shown on the first page. |
| `.NET Framework 4.7.2` check, abort with a message | `requirements.netFramework: "4.7.2"` with `netFrameworkSetup.helpUrl`; setup stops with exit code 17 and the link. |
| WebView2 bootstrapper when the runtime is missing | `requirements.webView2` with the bundled `MicrosoftEdgeWebview2Setup.exe`, plus `minVersion` and `onFailure: "fail"`: an outdated runtime is updated, and THRM is not installed on one that stays too old. |
| `taskkill` of THRM, THRM Core, bridges, legacy BS2PRO executables and `SpaceStationService.exe` | `install.closeProcesses` (WM_CLOSE first, then terminate). |
| Detect an older install under the legacy uninstall keys (`BS2PRO-Controller`, `TIANLI0BS2PRO`, …) and upgrade it in place, then delete those keys | `install.legacyUninstallKeys` (folder from InstallLocation, UninstallString or DisplayIcon); the keys are removed after a successful install. The current `THRM` key is adopted through `adoptExistingInstall`. |
| Delete scheduled tasks `THRM`, `BS2PRO-Controller`, `BS2PRO-Core`; legacy Run values; legacy shortcuts | `install.cleanup` (runs before the autostart action re-registers the task). |
| PawnIO: skip if the installed version is ≥ the bundled one; silent install; exit code 183 = already installed; on failure retry interactively; a failure only warns | `actions[pawnio]`: `skipIfInstalled` (64- and 32-bit registry views), `successExitCodes: [0, 183]`, `fallbackArguments: ["-install"]`, `onFailure: "warn"`. |
| Autostart: `THRM Core.exe --install-autostart`; on failure write `HKCU\…\Run\THRM = "THRM Core.exe" --autostart` | `actions[autostart]` with `ifOption: "autostart"` and `fallbackRunValue`. |
| `ngen install /queue` for the bridge; `ngen uninstall` before replacing or removing it | `install.ngen: ["bridge/THRM TempBridge.exe"]`. |
| Uninstall: close processes, delete tasks, Run values, startup shortcuts, `%AppData%\THRM.exe` (WebView2 data), `%AppData%\THRM`, `%LocalAppData%\THRM`; keep PawnIO (shared driver) | `uninstall.removeScheduledTasks`, `removeRunValues`, `removeShortcuts`, `removeData`; PawnIO is not removed. |
| THRM's in-app updater runs the installer with `/S` | `/S` is an alias of `/quiet`. |

Every action runs a file from the package's own payload, with `CreateProcess`, without a shell, inside a job object and
with a timeout. Prerequisite downloads (not used by THRM) are https-only and verified by SHA-256. See
[project-file.md](project-file.md) and [security.md](security.md).

## Differences that remain

- **WebView2 minimum version.** Neither THRM nor Wails v3.1.0 declares one; the sample uses `120.0.0.0` as a policy
  value. Raise it to whatever the app actually needs.
- **"Remove settings?" question on uninstall.** NSIS asks whether to delete `%AppData%\BS2PRO` and
  `%LocalAppData%\BS2PRO`. FluentSetup has no uninstall-time question; those legacy folders are kept.
- **`%TEMP%\THRM`** is not removed (temporary files; the elevated uninstaller would see the administrator's `%TEMP%`).
- **Config backup/restore** (`config.json`, `settings.ini` in the install folder) is not needed: FluentSetup only
  replaces files it ships and never deletes files it did not install.
- **Size.** The FluentSetup package is 19.7 MB (NSIS: 21.7 MB). Installed, FluentSetup keeps two files NSIS does not:
  the WebView2 bootstrapper under `redist\` (1.7 MB; NSIS runs it from a temporary folder) and a larger
  `uninstall.exe` (1.4 MB, it carries the WinUI front end). See [size-report.md](size-report.md).
