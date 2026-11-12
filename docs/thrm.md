# Case study: packaging THRM

[THRM](https://github.com/TIANLI0/THRM) is a Wails (Go + WebView2) desktop app that previously shipped with an NSIS
installer. [`samples/THRM/fluentsetup.json`](../samples/THRM/fluentsetup.json) packages the same build output with
FluentSetup. It is the reference for the declarative install extensions, because THRM needs more than copying files.

> The sample reads THRM's build output from absolute paths (`D:/develop/BS2PRO-Controller/build/...`). Adjust
> `files.source`, `files.extra`, `app.icon` and `ui.licenses` to your checkout before building.

```powershell
src\CLI\bin\Release\net10.0-windows10.0.19041.0\fluentsetup.exe build samples\THRM\fluentsetup.json
# -> out\thrm\THRM-3.8.0-Setup.exe
```

## What the NSIS script did, and the FluentSetup equivalent

| NSIS installer | FluentSetup project |
|---|---|
| Per-machine install into `Program Files\THRM`, admin required | `install.scope: "machine"`. The UI shows the UAC shield on the install button. Only the elevated worker does privileged work. |
| Kill `THRM.exe`, `THRM Core.exe`, bridge processes before copying | `install.closeProcesses`. The first page lists the running ones ("... are running and will be closed"). They are closed in the Preparing step. |
| Upgrading over an existing NSIS install | `install.adoptExistingInstall: true`. An Apps & features entry with the same id that FluentSetup did not write is adopted. Its install folder is reused and its entry is replaced by a FluentSetup one. |
| Check .NET Framework 4.7.2 | `requirements.netFramework: "4.7.2"` (checks the registry `Release` value). |
| Install the WebView2 runtime if missing | `requirements.webView2` with the bundled `MicrosoftEdgeWebview2Setup.exe` (`/silent /install`). It runs only when the runtime is missing. |
| Install the PawnIO driver | `actions[0]`: runs `drivers/PawnIO/PawnIO_setup.exe -install -silent` after the files are in place. Exit code 0 or 183 counts as success. It is skipped when PawnIO 2.2.0.0 or newer is already registered. A failure becomes a warning on the result page. |
| "Start with Windows" checkbox | `options[autostart]` is shown as a checkbox on the first page. `actions[1]` runs `THRM Core.exe --install-autostart` only when it is ticked (`ifOption`). |
| Uninstall: remove scheduled task, Run value, app data | `uninstall.removeScheduledTasks: ["THRM"]`, `removeRunValues: ["THRM"]`, `removeData` (`{AppData}`/`{CommonAppData}` folders). |
| English and Chinese license pages | `ui.licenses` keyed by language. The one matching the UI language is shown. |
| THRM's in-app updater runs the installer with `/S` | `/S` is accepted as an alias of `/quiet`. |

Every action runs a file from the package's own payload. It runs with `CreateProcess`, without a shell, inside a job
object, with a timeout. See [project-file.md](project-file.md) and [security.md](security.md).

## Verified

On a PC that had THRM 3.8.0 installed by NSIS in `D:\BS2PRO-Controller`:

- The install adopted the existing entry and kept the folder. THRM's running processes were closed. The PawnIO action
  was skipped because the installed version was new enough. The autostart registration exited with 0.
- THRM was relaunched ("Launch when ready").
- The Apps & features entry is now FluentSetup's (`FluentSetup=1`). The `\THRM` scheduled task and the shortcuts are
  present.

## Not carried over (yet)

These parts of the NSIS script are intentionally not in the FluentSetup project:

- **Stopping `SpaceStationService`.** It is a third-party service. FluentSetup only closes processes that belong to
  the app being installed.
- **NGen of the bridge assemblies.** It is optional. .NET compiles them on first use.
- **Clean-up of the legacy "BS2PRO" product name** (old shortcuts, registry keys). If it is still needed, it can
  be added as an `afterInstall` action that runs a clean-up tool shipped in the payload.
- **Package size.** The FluentSetup package is 28.8 MB, the NSIS one 21.4 MB. LZMA with NSIS's BCJ filter compresses
  x86/x64 code better than zstd without a filter. See [size-report.md](size-report.md).
