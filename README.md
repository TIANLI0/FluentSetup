# FluentSetup

**A lightweight builder for native, Windows 11-style installers.** You describe your app in one JSON file, and
FluentSetup turns it into a single `Setup.exe`. That file installs, upgrades, rolls back and uninstalls your app,
with a Fluent WinUI 3 interface and no .NET, VC++ redistributable, WebView2 or network access needed at install time.

[中文说明](README.zh-CN.md)

![FluentSetup installer, WinUI 3 front end](docs/images/setup-winui.png)

## Highlights

- **Small.** Under 1 MiB of installer overhead for a typical app (stub + WinUI front end + metadata), checked
  against a 5 MiB budget in CI. See [docs/size-report.md](docs/size-report.md).
- **Native and self-contained.** A C++20 bootstrapper with a statically linked VC runtime and a built-in zstd
  decoder. The WinUI 3 front end is used when the Windows App Runtime is on the PC. Otherwise a Win32 front end with
  the same layout takes over, without restart loops or repeated side effects.
- **Safe installs.** A write-ahead journal and backups of replaced files give byte-for-byte rollback on failure or
  cancel. Unsafe install paths are refused. Per-file SHA-256 and Authenticode checks catch corrupted or tampered
  packages. Elevation goes through a separate worker that installs only its own embedded package.
- **Complete lifecycle.** Per-user or per-machine installs, upgrade, reinstall, downgrade protection, Apps & features
  registration, shortcuts, a self-removing `uninstall.exe`, silent mode, and adopting an existing install of the
  same app made by another installer.
- **Prerequisites with version checks.** .NET Framework, .NET (Core) runtimes and WebView2, each with a minimum
  version. A missing or outdated one is installed from a bundled installer or an optional https download (verified
  by SHA-256), or setup stops with a help link. With `onFailure: "fail"` the app is never left on an outdated runtime.
- **Declarative extensions.** Close running processes, required (locked) and optional components, payload programs
  after install or before uninstall with fallbacks, adoption of installs made under older product names, NGen,
  and cleanup of legacy tasks, Run entries, shortcuts and uninstall keys.
- **Variants.** One package can carry a self-contained and a framework-dependent build of the same app (shared files
  stored once); the user picks at install time, or `--variant` builds a single-variant package.
- **Accessible and localized.** English, Simplified Chinese, Japanese and Korean built in, following the Windows display
  language; any other language can be added by a package (`fluentsetup locale add fr-FR`); package texts live in one
  file per language. UI Automation names, keyboard navigation,
  light/dark/high-contrast themes, and per-monitor DPI.
- **Three ways to build:** the `fluentsetup` CLI, a WinUI 3 Builder app, or the `Builder.Core` library.
- **Made for AI-assisted authoring.** `fluentsetup guide` prints a complete authoring guide, `fluentsetup schema` the
  JSON Schema, and `fluentsetup validate --json` machine-readable diagnostics with codes and field paths.

## Quick start

Requirements for building: Windows 10 1809+ (Windows 11 recommended), Visual Studio 2022 or newer with
*Desktop development with C++*, and the .NET 10 SDK.

```powershell
git clone <this repository>
cd FluentSetup
.\scripts\build.ps1                      # native runtime, WinUI front end, CLI, Builder GUI, tests

$fs = '.\src\CLI\bin\Release\net10.0-windows10.0.19041.0\fluentsetup.exe'
& $fs init my-app --name "My App" --id Contoso.MyApp --source C:\build\MyApp\bin --publisher Contoso
& $fs validate my-app\fluentsetup.json
& $fs build my-app\fluentsetup.json      # -> my-app\dist\My App-1.0.0-Setup.exe
& $fs inspect "my-app\dist\My App-1.0.0-Setup.exe" --verify
& $fs guide                              # the full authoring guide (also for AI tools)
```

A minimal project file:

```json
{
  "$schema": "https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/shared/schemas/fluentsetup.project.schema.json",
  "schemaVersion": 1,
  "app": { "id": "Contoso.MyApp", "name": "My App", "publisher": "Contoso", "version": "1.0.0", "mainExecutable": "MyApp.exe" },
  "files": { "source": "bin", "include": ["**/*"], "exclude": ["**/*.pdb"] },
  "install": { "scope": "userOrMachine", "defaultScope": "user", "desktopShortcut": "optionalOff", "launchAfterInstall": "optionalOn" },
  "ui": { "license": "LICENSE.txt" },
  "output": { "fileName": "{AppName}-{Version}-Setup.exe", "compression": "normal" }
}
```

The full reference is in [docs/project-file.md](docs/project-file.md), and the JSON schema is in
[shared/schemas](shared/schemas/fluentsetup.project.schema.json). [samples/DemoApp](samples/DemoApp) is the test app.
[samples/THRM](samples/THRM) is a real-world project that uses prerequisites, actions and options
([write-up](docs/thrm.md)).

## Setup.exe command line

```
Setup.exe [/quiet | /S | /passive] [/uninstall] [/dir <path>] [/scope user|machine] [/relaunch]
          [/desktop-shortcut yes|no] [/start-menu-shortcut yes|no] [/option <id>=yes|no]
          [/log <file>] [/lang en-US|zh-CN|ja-JP|ko-KR] [/ui auto|winui|win32]
```

Exit code 0 means success. All other codes are listed in [docs/diagnostics.md](docs/diagnostics.md).

## Repository layout

| Path | What |
|---|---|
| `src/Bootstrapper` | `FluentSetup.Stub.exe`: entry point, controller, elevation, runtime probe, WinUI host |
| `src/Install.Core`, `src/Install.Worker` | Install engine (state machine, journal, rollback, uninstall, extensions), elevated worker |
| `src/Archive`, `src/IPC`, `src/Common` | Container reader + zstd, named-pipe IPC, JSON/crypto/path/security helpers |
| `src/Setup.WinUI`, `src/Setup.Win32` | C++/WinRT WinUI 3 front end, Win32 fallback front end |
| `src/Builder.Core`, `src/CLI`, `src/Builder` | C# packaging library, `fluentsetup` CLI, WinUI 3 Builder GUI |
| `shared` | Localization strings, JSON schema, IPC protocol |
| `tests` | Native unit tests, Builder.Core tests, end-to-end install tests, UI Automation tests |
| `docs` | Architecture, package format, security, diagnostics, size report |

## Documentation

- [Architecture](docs/architecture.md): components, process model, install state machine, fallback rules
- [Project file reference](docs/project-file.md)
- [Package format](docs/package-format.md): the `Setup.exe` container
- [UI/worker protocol](shared/protocols/ui-protocol.md)
- [Security](docs/security.md): threat model and mitigations
- [Diagnostics](docs/diagnostics.md): exit codes, logs, recovery
- [Size report](docs/size-report.md)

## Testing

```powershell
.\out\bin\x64\Release\FluentSetup.Tests.Native.exe        # native unit/security tests
dotnet test tests\Builder.Core.Tests -c Release           # builder tests
.\tests\Integration\e2e.ps1                               # real per-user installs: upgrade, rollback, uninstall, ...
.\tests\UI\ui-tests.ps1                                   # drives both front ends with UI Automation
.\scripts\size-report.ps1                                 # fails above the 5 MiB budget
```

The end-to-end and UI tests install and remove a test app for the current user only. They do not need administrator
rights.

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
