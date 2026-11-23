# Updating from inside the app

With an [`update`](project-file.md#update) source in the project, the `uninstall.exe` that setup writes is also the
app's updater. The app needs no update code of its own: it starts `uninstall.exe` with `/check-update` to find out
whether a newer version is published, and with `/install-update` to install it. This page is the app side of that.

## Finding uninstall.exe

Setup writes `uninstall.exe` into the root of the install folder, next to the install record `uninstall.json`. When
`app.mainExecutable` is in that root (the usual case), it is next to the app's own program:

```csharp
var uninstaller = Path.Combine(AppContext.BaseDirectory, "uninstall.exe");
```

If the program is in a subfolder, read the folder from the Apps & features entry: `InstallLocation` under
`Software\Microsoft\Windows\CurrentVersion\Uninstall\<app.id>`, in `HKEY_CURRENT_USER` for a per-user install and
`HKEY_LOCAL_MACHINE` (64-bit view) for a per-machine one; setup looks in that order too. When the file is missing, the
app was not installed by setup (a debug build, a portable copy): hide the update command.

## `/check-update`

Checks the source, shows nothing and downloads nothing. With standard output redirected, it writes one line of JSON:

```json
{"installedVersion":"1.0.0","channel":"stable","updateAvailable":true,"latestVersion":"1.1.0","notes":"https://github.com/contoso/notes/releases/tag/v1.1.0"}
```

| Field | When | Content |
|---|---|---|
| `installedVersion` | Always | The installed version (`DisplayVersion` of the Apps & features entry). |
| `channel` | Always | The [update channel](#channels) the check followed: `stable`, or e.g. `beta`. |
| `updateAvailable` | The source answered | `true` if a newer version is published. |
| `latestVersion` | `updateAvailable` is `true` | The published version. |
| `notes` | `updateAvailable` is `true` and the source has one | https link to the release notes: the GitHub release page, or `notes` of a feed. |
| `error` | The source could not be used | Why, in English (also in the log). `updateAvailable` is then missing. |

| Exit code | Meaning | JSON line |
|---:|---|---|
| 0 | Up to date | yes |
| 18 | A newer version is published (`UpdateAvailable`, not a failure) | yes |
| 19 | The source did not answer, answered nonsense or refused (`UpdateFailed`; GitHub's rate limit too) | yes, with `error` |
| 3 | The package has no update source | no |
| 11 | The app is not installed (no Apps & features entry for its id) | no |

`uninstall.exe` asks for no administrator rights, also for a per-machine install, so a check never shows a UAC prompt.
The log is `%TEMP%\FluentSetup\<AppId>-update-<date>-<time>.log`.

## `/install-update`

| Command | What happens |
|---|---|
| `uninstall.exe /install-update` | A small window: checking, then "1.1.0 is available" with **Update** / **Not now** and the release notes link, then the download. |
| `uninstall.exe /install-update /passive` | Progress only, no question; closes by itself when there is no update. |
| `uninstall.exe /install-update /quiet` | No UI: downloads, verifies and starts the new `Setup.exe /quiet`. |

Add `/relaunch` to start the app again after the update, and `/lang <tag>` for the window's language. The download is
verified (https, published SHA-256, same `app.id` and architecture, the advertised and newer version, `update.signer`)
before the new Setup.exe runs with `/passive` (or `/quiet`). That setup closes the running app
(`install.closeProcesses`, `install.closeCommand`, Restart Manager) like any upgrade, so the app does not need to exit
first; it may simply keep running until it is closed. A per-machine install shows the UAC prompt at that point.

`uninstall.exe` returns once the new Setup.exe has started; it does not wait for the update to finish.

| Exit code | Meaning |
|---:|---|
| 0 | Up to date, or the new Setup.exe was started |
| 2 | The user chose **Not now** or cancelled |
| 19 | The update could not be checked, downloaded or verified (the log has the reason) |
| 16 | Another `/install-update` of this app is running (one at a time per app in a Windows session) |
| 3, 11 | No update source; the app is not installed |

`/install-update` writes no JSON. To know whether there is an update without any window, run `/check-update` first.

## Channels

With [update channels](project-file.md#update-channels) (a feed with `"channels"`, or GitHub pre-releases), an app can
offer "Get beta versions" by adding `/channel beta` to both commands:

| Command | What happens |
|---|---|
| `uninstall.exe /check-update /channel beta` | Checks the beta channel: the newer of the feed's beta entry and the stable release (on GitHub: pre-releases included). Records nothing. |
| `uninstall.exe /install-update /channel beta` | Installs the newest beta and passes `/channel beta` to the new Setup.exe, which records it: later checks and updates without `/channel` follow `beta`. |
| `uninstall.exe /install-update /channel stable` | Back to stable: records `stable` with the next stable release that is newer than the installed version (updates never go back to an older version). |

Without `/channel`, both commands follow the channel recorded at install time (`Setup.exe /channel beta`, or an
earlier `/install-update /channel …`), else `update.channel` of the project, else `stable`. The `channel` field of
`/check-update` says which one was used; `/channel` takes lower-case letters, digits and `-` (exit code 3 otherwise).

## C# example

A check at start-up and an "Update" command, with `System.Diagnostics.Process` and `System.Text.Json`:

```csharp
using System.Diagnostics;
using System.Text.Json;

public record UpdateInfo(string? InstalledVersion, string? Channel, bool UpdateAvailable, string? LatestVersion, string? Notes, string? Error);

public static class Updater
{
    static readonly string Uninstaller = Path.Combine(AppContext.BaseDirectory, "uninstall.exe");

    // null: not installed by setup, no update source, or no answer.
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (!File.Exists(Uninstaller)) return null;
        var psi = new ProcessStartInfo(Uninstaller, "/check-update")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var process = Process.Start(psi)!;
        var line = await process.StandardOutput.ReadLineAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode is not (0 or 18 or 19) || string.IsNullOrEmpty(line)) return null;
        return JsonSerializer.Deserialize<UpdateInfo>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    // Shows FluentSetup's update window; the new setup closes this app and, with /relaunch, starts it again.
    public static void Install() =>
        Process.Start(new ProcessStartInfo(Uninstaller, "/install-update /relaunch") { UseShellExecute = false });
}
```

`JsonSerializerDefaults.Web` matches the camelCase names. Show `LatestVersion` and a link to `Notes` when
`UpdateAvailable` is `true`; a check once per start or once a day stays well within GitHub's 60 unauthenticated API
requests per hour per address.

## C++ and other languages

Any language that can start a process works the same way. In C++, create an anonymous pipe with `CreatePipe` (an
inheritable write end), start `"<folder>\uninstall.exe" /check-update` with `CreateProcessW`, `STARTF_USESTDHANDLES`
and `hStdOutput` set to the write end, close the write end in the app, read the line with `ReadFile` until it ends,
then `WaitForSingleObject` and `GetExitCodeProcess`. The line is UTF-8. For `/install-update`, a plain
`CreateProcessW` (or `ShellExecuteExW`) without waiting is enough.
