# FluentSetup.Cli

`fluentsetup`, the command-line tool of [FluentSetup](https://github.com/TIANLI0/FluentSetup), as a .NET tool. It turns a
`fluentsetup.json` into a single native `Setup.exe` with a Windows 11-style interface that installs, upgrades, rolls
back and uninstalls your app. The installer runtime is part of the tool, so it works on its own.

```powershell
dotnet tool install -g FluentSetup.Cli          # or: dotnet tool install FluentSetup.Cli (local tool manifest)

fluentsetup init my-app --name "My App" --id Contoso.MyApp --source bin\Release\net10.0\publish --publisher Contoso
fluentsetup validate my-app\fluentsetup.json
fluentsetup build my-app\fluentsetup.json -D Version=1.2.3
fluentsetup inspect "my-app\dist\My App-1.2.3-Setup.exe" --verify
fluentsetup winget "my-app\dist\My App-1.2.3-Setup.exe" --github contoso/my-app
fluentsetup guide                                # complete authoring guide (also for AI assistants)
```

Needs the .NET 10 runtime (or newer) on Windows 10 1809 or later. The Setup.exe it builds needs nothing at install time.

To build the installer as part of `dotnet publish`, use the `FluentSetup.MSBuild` package instead. Documentation:
[project file and CLI](https://github.com/TIANLI0/FluentSetup/blob/main/docs/project-file.md),
[winget](https://github.com/TIANLI0/FluentSetup/blob/main/docs/winget.md). MIT license.
