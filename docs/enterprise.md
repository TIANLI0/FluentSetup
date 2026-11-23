# Deploying with Intune, Configuration Manager and scripts

A FluentSetup `Setup.exe` is one offline EXE with silent switches, an Apps & features entry named after `app.id`, and
documented exit codes. Deployment tools treat it like any other EXE installer; this page lists the commands, the
detection rule and the return codes to enter.

Before you start, look at the package: `fsetup inspect Setup.exe` prints the name, version, `app.id`, architecture and
install scope (`user`, `machine` or `userOrMachine`) of a built Setup.exe.

## Commands

| Purpose | Command |
|---|---|
| Install, per machine | `Setup.exe /quiet /scope machine /log C:\Windows\Temp\MyApp-install.log` |
| Install, per user | `Setup.exe /quiet /scope user /log C:\Users\Public\MyApp-install.log` (run as the user) |
| Uninstall | `"C:\Program Files\My App\uninstall.exe" /uninstall /quiet` (the `QuietUninstallString`) |
| Uninstall from the deployment's content | `Setup.exe /uninstall /quiet` |
| Update | Deploy the newer Setup.exe with the install command: it upgrades in place |

Notes on the switches (all of them: [Setup.exe command line](project-file.md#setupexe-command-line)):

- `/quiet` shows no UI at all (`/verysilent`, `/q` and `/s` are synonyms). `/passive` shows only a progress window.
  The app is never launched after a quiet install.
- `/scope machine|user` matters only for a `userOrMachine` package (otherwise its `install.defaultScope` is used); a
  package with a single scope uses that scope and ignores the switch. On an upgrade the installed scope and folder are
  kept whatever `/scope` says: to move an app to the other scope, uninstall it first.
- `/dir <path>` sets the install folder of a first install (only if the package has `install.allowCustomDir`, the
  default). Upgrades keep the existing folder.
- `/log <file>` takes an absolute path on a local drive; missing folders are created. Without it the log goes to
  `%TEMP%\FluentSetup\<AppId>-install-<date>-<time>.log` (`-uninstall-` for an uninstall), which for the SYSTEM account
  is `C:\Windows\SystemTemp\FluentSetup\`. See [logs](diagnostics.md#2-logs).
- Choices of the package: `/desktop-shortcut yes|no`, `/start-menu-shortcut yes|no`, `/option <id>=yes|no`,
  `/variant <id>` (packages with [variants](project-file.md#variants-self-contained-or-shared-runtimes)) and
  `/input <id>=<value>` for the settings page (for example a server address). A required input that is missing or
  invalid fails a quiet install with exit code 3; on an upgrade the previous value is kept (never for passwords).
- `/remove-user-data` with `/uninstall` also deletes the app's data folders (`uninstall.userData`).
- Inno Setup and NSIS switches (`/VERYSILENT`, `/SUPPRESSMSGBOXES`, `/NORESTART`, `/DIR=`, `/LOG=`, `/S`, `/D=`) are
  understood, so command lines from an earlier installer keep working. Setup.exe never restarts the PC.

A per-machine install started without administrator rights shows the UAC prompt, also with `/quiet`; Intune,
Configuration Manager and startup scripts run elevated as SYSTEM and never see it. Running the same Setup.exe again
over the same version reinstalls it; let the detection rule keep that from happening.

## Per machine or per user

| | Per machine | Per user |
|---|---|---|
| Runs as | SYSTEM or an administrator | The user who gets the app (no administrator rights needed) |
| Folder | `install.machineDir`, default `C:\Program Files\<App>` | `install.userDir`, default `%LocalAppData%\Programs\<App>` |
| Registered under | `HKLM` | `HKCU` |
| Intune "Install behavior" | System | User |
| Configuration Manager "Installation behavior" | Install for system | Install for user |

Do not install a per-user package as SYSTEM: it would be installed into the SYSTEM account's profile, where no user
sees it. The same goes for the uninstall: a per-user installation is only found by `uninstall.exe` running as that user.

## Detection rule

Every install writes the Apps & features entry under the key named after `app.id`, in the 64-bit registry view:

```
HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<app.id>    (per machine)
HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<app.id>     (per user)
```

| Value | Type | Content |
|---|---|---|
| `DisplayVersion` | REG_SZ | `app.version`, e.g. `1.2.0` |
| `FluentSetup` | REG_DWORD | `1`: the entry was written by FluentSetup |
| `DisplayName`, `Publisher` | REG_SZ | `app.name`, `app.publisher` |
| `InstallLocation` | REG_SZ | The install folder |
| `UninstallString` | REG_SZ | `"<folder>\uninstall.exe" /uninstall` |
| `QuietUninstallString` | REG_SZ | `"<folder>\uninstall.exe" /uninstall /quiet` |
| `ModifyPath` | REG_SZ | `"<folder>\uninstall.exe" /modify` |
| `FluentSetupVariant` | REG_SZ | The installed variant (packages with variants only) |

Use `DisplayVersion` with a version comparison "greater than or equal to" the package's version. In Intune and
Configuration Manager, leave "associated with a 32-bit app on 64-bit clients" off: the key is in the 64-bit view.
Version comparisons in these tools understand only numeric versions; for a pre-release such as `2.0.0-rc.1` compare
the string for equality instead. A file rule (the main program's file version in the install folder) works too.

## Exit codes

The codes are a fixed contract; the full list is in [diagnostics](diagnostics.md#1-exit-codes). For a deployment tool:

| Code | Meaning | Suggested type |
|---:|---|---|
| 0 | Success | Success |
| 3010 | Success; files in use are replaced at the next restart (`install.lockedFiles: "reboot"`) | Soft reboot |
| 16 | Another setup or uninstall of the same app is running | Retry (Configuration Manager: Fast retry) |
| 8 | A file is in use (the app could not be closed) | Retry |
| 2 | Cancelled (only with UI) | Failed |
| 3 | Bad command line, unknown switch, missing or invalid `/input` value | Failed |
| 6 | Administrator rights needed and not granted | Failed |
| 10 | A newer version is installed and the package does not allow downgrades | Failed |
| 11 | Uninstall: the app is not installed | Failed, or Success if you only want it gone |
| 17 | A required prerequisite is missing and the package does not bring its installer | Failed |

Intune and Configuration Manager already treat 0 as success and 3010 as a soft reboot; add 16 (and 8 if you like) as
retry. Any other code is a failure; the log says why.

## Microsoft Intune (Win32 app)

1. Put Setup.exe alone in a folder and wrap it with the
   [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool):
   `IntuneWinAppUtil.exe -c <folder> -s Setup.exe -o <output>`.
2. Program: the install and uninstall commands from [Commands](#commands); "Install behavior" System for a per-machine
   package, User for a per-user one; "Device restart behavior": "Determine behavior based on return codes".
3. Return codes: keep the defaults and add `16` as Retry.
4. Detection rules: Registry, key path `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<app.id>`
   (`HKEY_CURRENT_USER\…` for a per-user app), value name `DisplayVersion`, method "Version comparison", operator
   "Greater than or equal to", value: the package's version.
5. A newer version: a new Win32 app with the new Setup.exe and the new version in the detection rule, superseding the
   old one **without** "Uninstall previous version": Setup.exe upgrades in place and keeps the folder, the user's
   choices and the data.

## Configuration Manager

Create an application with a "Script Installer" deployment type:

- Installation program: `Setup.exe /quiet /scope machine /log C:\Windows\Temp\MyApp-install.log`
- Uninstallation program: `"C:\Program Files\My App\uninstall.exe" /uninstall /quiet`, or `Setup.exe /uninstall /quiet`
  with the content folder as the uninstall start-in folder.
- Detection method: Registry, hive `HKEY_LOCAL_MACHINE`, key `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<app.id>`,
  value `DisplayVersion`, data type Version, "Greater than or equal to" the package's version; leave "This registry key
  is associated with a 32-bit application on 64-bit systems" unchecked.
- User experience: "Install for system", whether or not a user is logged on, "Hidden".
- Return codes: add `16` as Fast retry.

A new version is a new deployment type (or application with supersedence) whose detection uses the new version; it
upgrades the installed one in place.

## Group Policy and scripts

Group Policy software installation accepts only MSI packages. Use a computer startup script instead (it runs as SYSTEM)
that installs only when the version is missing:

```powershell
$appId   = 'Contoso.MyApp'
$version = [version]'1.2.0'
$source  = '\\server\share\MyApp\MyApp-1.2.0-Setup.exe'

$key = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$installed = (Get-ItemProperty $key -ErrorAction SilentlyContinue).DisplayVersion
if ($installed -and [version]$installed -ge $version) { return }

$local = Join-Path $env:TEMP (Split-Path $source -Leaf)
Copy-Item $source $local -Force
$p = Start-Process $local -ArgumentList '/quiet', '/scope', 'machine', '/log', "$env:SystemRoot\Temp\MyApp-install.log" -Wait -PassThru
Remove-Item $local -ErrorAction SilentlyContinue
exit $p.ExitCode
```

PowerShell running as a 64-bit process reads the 64-bit registry view, which is the one Setup.exe writes; a 32-bit
host would need `[Microsoft.Win32.RegistryKey]::OpenBaseKey(..., 'Registry64')`. The same commands work in any other
deployment tool (PDQ, Ansible, a task sequence).

## Built-in updates

If the project has an [`update`](project-file.md#update) source, the installed app can update itself. FluentSetup has
no policy setting that turns this off, but nothing runs on its own either: an update starts only when the app runs
`uninstall.exe /install-update` (for example from a "Check for updates" menu item) or when a user clicks **Update** on
the "Change <App>" page (Apps & features > Modify). See [updates](updates.md).

- **Per-machine installs** can only be updated with administrator rights: the new Setup.exe shows the UAC prompt, and
  a standard user who cannot approve it gets exit code 6 and keeps the installed version. Your deployment stays in
  control of the version.
- **Per-user installs** are updated by the user without administrator rights.
- To control updates, ask the vendor whether the app has a setting for its update check, or for a package built without
  `update` (then `/check-update` and `/install-update` exit with code 3 and the Change page shows no update). Each
  update is verified before it runs: https only, the published SHA-256, the same `app.id` and architecture, a newer
  version, and the signer if the package pins one (`update.signer`).
- Updates you deploy yourself work the same way whether or not the package has an update source.
- A deployment tool can run `uninstall.exe /install-update /quiet` as SYSTEM or elevated: the verified download is
  copied into an admin-only folder under `%SystemRoot%\Temp` and the new Setup.exe runs from there, never from a
  folder users can write to. `/channel <name>` (also on the first `Setup.exe` run) puts a PC on another
  [update channel](project-file.md#update-channels), e.g. a beta ring; the install remembers it.
