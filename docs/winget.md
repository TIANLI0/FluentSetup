# winget and the Microsoft Store

A FluentSetup `Setup.exe` is an ordinary EXE installer: one offline file, silent switches, an Apps & features entry
named after `app.id`, documented exit codes, and its own UAC prompt for per-machine installs. That is what the Windows
Package Manager (winget) and the Microsoft Store expect from an EXE app, so neither needs anything special.

## `fluentsetup winget`

```
fluentsetup winget <Setup.exe> --url <https URL> | --github <owner/repo> [options]
    -o, --output <dir>            Root folder (default: the current folder)
    --identifier <Publisher.App>  PackageIdentifier (default: app.id if it has that form, else publisher + name)
    --license <name>              License, e.g. MIT (default: recognized from the license text, else Proprietary)
    --license-url <url>
    --short-description <text>    Default: app.description if it is one line of up to 256 characters, else its first sentence
    --publisher-url <url>
    --tags <a,b,...>              Search terms (lower case, spaces become "-", at most 16)
    --release-notes-url <url>
    --json                        Print the result (folder, files, URL, SHA-256, notes) as JSON
```

It reads the built package, computes the SHA-256 of the file and writes a multi-file manifest (schema 1.28.0) in the
layout of the [winget-pkgs](https://github.com/microsoft/winget-pkgs) repository:

```
<output>/manifests/c/Contoso/MyApp/1.2.0/
    Contoso.MyApp.yaml                 version manifest
    Contoso.MyApp.locale.en-US.yaml    default locale: publisher, name, license, descriptions, links, tags
    Contoso.MyApp.installer.yaml       installers, switches, return codes
```

Run it in a clone of your winget-pkgs fork (the default output folder is the current one) and the files land where the
pull request needs them. Upload Setup.exe to the URL first: winget and the winget-pkgs checks download it and compare
the SHA-256.

- `--url` is the https address users download this exact file from. It must never change its content later.
- `--github owner/repo` uses the GitHub release asset `https://github.com/<owner>/<repo>/releases/download/v<version>/<file>`
  (tag `v` + `app.version`, the file name of Setup.exe with spaces replaced by dots, as GitHub renames uploaded files).
  It also fills `PackageUrl` (if `app.website` is empty), `PublisherUrl` (`https://github.com/<owner>`) and
  `ReleaseNotesUrl` (the release page) unless given.
- The command prints what it guessed (identifier, license) and whether Setup.exe is signed. Exit code 0 on success,
  1 if the package or a value cannot be used (with the reason), 2 for usage errors.

### Where the values come from

| Manifest field | Value |
|---|---|
| `PackageIdentifier` | `--identifier`, else `app.id` when it has the form `Publisher.App` (2 to 8 parts of up to 32 characters, no spaces), else `<publisher>.<name>` without spaces and dots. |
| `PackageVersion` | `app.version` |
| `Publisher`, `PackageName` | `app.publisher` (required, at least 2 characters), `app.name` |
| `ShortDescription`, `Description` | `--short-description` or `app.description` (see above); `Description` is the full `app.description` when it is longer. |
| `PackageUrl` | `app.website` |
| `License` | `--license`; otherwise MIT, Apache-2.0, GPL, LGPL, AGPL, MPL-2.0, BSD, BSL-1.0 or Unlicense when the package's license text (`ui.license`) is one of them, else `Proprietary`. |
| `Architecture` | `output.architecture` of the build: `x64` or `arm64` |
| `Scope` | `install.scope`: `user` or `machine` give one installer; `userOrMachine` gives two (same file), one per scope. |
| `MinimumOSVersion` | `10.0.<install.minWindowsBuild>.0` (left out when the check is off) |
| `ProductCode`, `AppsAndFeaturesEntries` | The Apps & features entry Setup.exe writes: key `app.id`, `DisplayName` = `app.name`, `Publisher`, `DisplayVersion` = `app.version`. winget uses it to see that the app is installed and which version. |
| `InstallationMetadata.DefaultInstallLocation` | `install.userDir` / `install.machineDir` with winget's variables, e.g. `%LocalAppData%\Programs\My App`. |

### What the installer manifest tells winget

| Field | Value | Why |
|---|---|---|
| `InstallerType` | `exe` | |
| `InstallerSwitches.Silent` | `/quiet` | No UI at all (`winget install --silent`, and the default for upgrades). |
| `InstallerSwitches.SilentWithProgress` | `/passive` | Progress window only (winget's default mode). |
| `InstallerSwitches.InstallLocation` | `/dir "<INSTALLPATH>"` | `winget install --location`. Left out when `install.allowCustomDir` is false. |
| `InstallerSwitches.Log` | `/log "<LOGPATH>"` | winget's log folder gets the setup log. |
| `InstallerSwitches.Custom` | `/scope user` or `/scope machine` | Only for `userOrMachine`: `winget install --scope` picks the installer and Setup.exe gets the matching scope. |
| `ElevationRequirement` | `elevatesSelf` | Per-machine installers: Setup.exe shows the UAC prompt itself, also in quiet mode. |
| `UpgradeBehavior` | `install` | Setup.exe upgrades in place (`winget upgrade` runs the new Setup.exe). |
| `RepairBehavior`, `InstallerSwitches.Repair` | `installer`, `/repair` | `winget repair` runs this version's Setup.exe with `/repair`: the installed files are checked against the install record and missing or damaged ones are restored. |
| `InstallModes` | interactive, silent, silentWithProgress | |
| `Platform` | `Windows.Desktop` | |

`ExpectedReturnCodes` map Setup.exe's exit codes (see [diagnostics](diagnostics.md#1-exit-codes)) to winget's messages:

| Exit code | FluentSetup | winget `ReturnResponse` |
|---:|---|---|
| 2 | Cancelled | `cancelledByUser` |
| 3 | InvalidArguments | `invalidParameter` |
| 5 | UnsupportedSystem | `systemNotSupported` |
| 7 | DiskFull | `diskFull` |
| 8 | FileInUse | `fileInUse` |
| 10 | DowngradeBlocked | `downgrade` |
| 16 | AlreadyRunning | `installInProgress` |
| 17 | PrerequisiteMissing | `missingDependency` |
| 3010 | Installed; files are replaced at the next restart | `rebootRequiredToFinish` (a success) |

## Check the manifests on your PC

```powershell
winget validate --manifest manifests\c\Contoso\MyApp\1.2.0
```

`winget install --manifest <folder>` installs from the local manifest for real (it needs
`winget settings --enable LocalManifestFiles`, which asks for administrator rights). Try it in Windows Sandbox or a
virtual machine rather than on your own PC.

## Submit to winget-pkgs

1. Publish Setup.exe at its final address, for example as a GitHub release asset (tag `v<version>`).
2. In a clone of your fork of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), on a new branch:
   `fluentsetup winget path\to\Setup.exe --github owner/repo` (or `--url ...`).
3. `winget validate --manifest manifests\...\<version>`, commit the folder and open a pull request (title
   `New package: Contoso.MyApp version 1.2.0`, or `New version: ...` for an update). Alternatively let
   [wingetcreate](https://github.com/microsoft/winget-create) open it: `wingetcreate submit --token <PAT> manifests\...\<version>`.
4. The winget-pkgs pipeline downloads the installer, scans it and installs it silently in a VM. A moderator merges the
   pull request; from then on `winget install Contoso.MyApp` works.

For every new version, run the command again with the new Setup.exe: the identifier stays, only the version folder is
new. In a release workflow (after the GitHub release exists):

```yaml
- name: Submit to winget-pkgs
  shell: pwsh
  env:
    WINGET_TOKEN: ${{ secrets.WINGET_TOKEN }}
  run: |
    $m = fluentsetup winget "dist/My App-$env:VERSION-Setup.exe" --github $env:GITHUB_REPOSITORY -o winget --json | ConvertFrom-Json
    Invoke-WebRequest https://aka.ms/wingetcreate/latest -OutFile wingetcreate.exe
    ./wingetcreate.exe submit --token $env:WINGET_TOKEN $m.folder
```

`WINGET_TOKEN` is a GitHub personal access token with the `public_repo` scope of the account that owns the fork.

Notes for a smooth review:

- Keep the scope stable between versions and the `app.id` unchanged: winget matches installed apps by the product code.
- `allowDowngrade: false` (the default) makes `winget install --version <older>` fail with "downgrade", which is what
  winget expects.
- An unsigned Setup.exe is accepted, but SmartScreen warns users who download it. Signing it (`signing.enabled`) is
  recommended.

## Microsoft Store (EXE app)

Partner Center accepts Win32 apps as an EXE installer hosted by you ("MSI or EXE app"). FluentSetup meets the package
requirements as follows:

| Store requirement | With FluentSetup |
|---|---|
| A single offline .exe, not a web installer | Setup.exe contains the whole app. A prerequisite with a `download` source is fetched at install time; for the Store, bundle its installer instead (`installer`, or `auto: "bundle"`) or keep it as a check. |
| Versioned https URL whose file never changes | Use a release URL per version, such as the GitHub asset `.../releases/download/v1.2.0/My.App-1.2.0-Setup.exe`. Submit a new URL for each update. |
| Silent install, UAC allowed | Installer parameters: `/quiet` (plus `/scope user` for a `userOrMachine` package whose `defaultScope` is `machine`, to install without UAC). Per-machine packages show only the UAC prompt. |
| The installer and the PE files it installs signed with a certificate from the Microsoft Trusted Root Program | `signing.enabled` signs Setup.exe, and `signing.signUninstaller` (on by default) the `uninstall.exe` it writes. Sign your app's own `.exe` and `.dll` files before building the package. |
| Apps & features entry | Written by every install: key `app.id` under `HKCU` or `HKLM\...\Uninstall`, with `DisplayName`, `Publisher`, `DisplayVersion`, `InstallLocation`, `UninstallString` and `QuietUninstallString` (`uninstall.exe /uninstall /quiet`). |
| Installer return codes ("Installer handling", optional) | Installation successful: `0`; Reboot required: `3010`; Installation cancelled by user: `2`; Installation already in progress: `16`; Disk space is full: `7`; Application already exists: `10` (a newer version is installed). For other failures, give the [diagnostics](diagnostics.md#1-exit-codes) page as the documentation URL. |
| Architecture | One package per architecture: build with `--arch x64` and `--arch arm64` and add both. |

The Store does not install updates of EXE apps by itself: submit each new version (new URL), and let installed copies
update through FluentSetup's built-in updates (`update` in the project) if you want them to update between Store
visits.
