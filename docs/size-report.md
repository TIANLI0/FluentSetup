# Installer size

FluentSetup's budget: everything a `Setup.exe` carries **besides the app's own compressed payload** must stay at or
under **5 MiB**. The budget is checked automatically by [`scripts/size-report.ps1`](../scripts/size-report.ps1), which
CI runs on every push. The script exits with code 1 when the overhead is above the budget.

```powershell
scripts/size-report.ps1                                   # builds samples/DemoApp and reports on it
scripts/size-report.ps1 -Setup out\thrm\THRM-3.8.0-Setup.exe -Json out\size.json
```

Sizes come from the built files, the stub's linker map (`out/bin/x64/Release/FluentSetup.Stub.map`) and the package
container (`fluentsetup inspect --json`). The package-level numbers (stub PE, front end, metadata, payload) are exact.
The per-component split of the stub is attributed from the linker map by symbol address; it shows which component
costs what. Its rows can add up to slightly more than the PE size because padding between symbols is counted against
the preceding symbol.

## Results (x64 Release, November 2026)

### samples/DemoApp (`compression: normal`)

| Component | KiB |
|---|---:|
| **Stub PE** (one native exe, includes everything below) | **634.5** |
| &nbsp;&nbsp;Bootstrapper (args, controller, elevation, localization, runtime probe, WinUI host) | 106.0 |
| &nbsp;&nbsp;Win32 fallback UI | 49.1 |
| &nbsp;&nbsp;Install engine + elevated worker | 132.9 |
| &nbsp;&nbsp;Container + zstd decoder | 62.0 |
| &nbsp;&nbsp;Common (JSON, IPC, crypto, file system) | 58.0 |
| &nbsp;&nbsp;C/C++ runtime (statically linked VC runtime; UCRT comes from Windows) | 174.5 |
| &nbsp;&nbsp;Strings (en-US + zh-CN) + default icon | 26.7 |
| **WinUI 3 front end** (zstd-compressed; 1,029 KiB raw) | **331.7** |
| **Package metadata** (header, footer, alignment) | **9.4** |
| **Total installer overhead** | **975.6** (19.1 % of 5 MiB) |
| App payload (not counted) | 14.4 |

### THRM 3.8.0 (`compression: max`)

| Component | KiB |
|---|---:|
| Stub PE incl. THRM's multi-resolution icon and version resources | 988.0 |
| WinUI 3 front end (compressed) | 353.7 |
| Package metadata | 7.0 |
| **Total installer overhead** | **1,348.7** (26.3 % of 5 MiB) |
| App payload (22 files, 70.0 MB raw) | 28,126.3 |

The difference to the DemoApp stub is almost entirely THRM's icon (the stub carries every size of the product icon,
up to 256 px, so Explorer and the taskbar show it sharply).

## What is not shipped

- **Windows App Runtime** — a framework package that is already present on most Windows 11 PCs. It is used only by
  the WinUI 3 front end. When it is missing, too old, or the wrong architecture, Setup.exe uses the built-in Win32
  front end. Nothing is downloaded.
- **UCRT** — part of Windows 10 and 11.
- **.NET** — not needed by Setup.exe at all. Only the builder tools (CLI, Builder GUI) are .NET applications.

## Where the bytes go, and how to keep them down

- **C/C++ runtime (~175 KiB)**: the stub links the VC runtime statically and uses the system UCRT ("hybrid CRT"). That
  avoids a VC++ redistributable dependency. The STL (`std::wstring`, `std::map`, `std::function`) accounts for most of it.
- **WinUI front end (~330 KiB compressed)**: `FluentSetup.UI.exe` links the CRT statically for the same reason.
  `Microsoft.WindowsAppRuntime.Bootstrap.dll` is bundled because unpackaged apps must load it locally.
- **zstd decoder (~60 KiB)**: only the decompressor is compiled in (`third_party/zstd`), without legacy formats or
  dictionary building.
- The front end is stored once and extracted to a private temp folder only when the WinUI path is taken. A project
  can set `output.includeWinUI: false` to ship only the Win32 front end (about 330 KiB smaller).
- Payload compression: `fast`, `normal` and `max` map to zstd levels. `max` uses level 19 with long-distance matching
  (window log 27, 128 MiB). For THRM, the FluentSetup package is 28.8 MB against 21.4 MB for the previous NSIS
  installer (LZMA with the BCJ x86 filter), because zstd has no executable-code filter. Adding a BCJ-style filter
  for PE files is the most promising way to close that gap.
