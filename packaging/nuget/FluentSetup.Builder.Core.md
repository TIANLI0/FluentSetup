# FluentSetup.Builder.Core

The library behind [FluentSetup](https://github.com/TIANLI0/FluentSetup)'s `fluentsetup` CLI and Builder app: the
`fluentsetup.json` project model, validation with diagnostic codes, and the packager that turns a project into a single
native `Setup.exe` (install, upgrade, rollback, uninstall, Windows 11-style WinUI 3 interface).

The package carries the installer runtime (setup stub, WinUI front end, installer strings, default icon). It is copied
into your app's output and publish folders as `runtime\`, where the library looks for it, so nothing else needs to be
installed.

```csharp
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

var path = @"installer\fluentsetup.json";
var project = ProjectSerializer.Load(path);
var report = await new PackageBuilder().BuildAsync(project, path, new BuildOptions
{
    Defines = new() { ["Version"] = "1.2.3" },   // fills "$(Version)" in the project
});
Console.WriteLine(report.Success ? $"Built {report.OutputPath}" : $"Failed: {report.Error}");
foreach (var d in report.Diagnostics) Console.WriteLine(d);   // "error FS1101 [app.version]: ..."
```

- `ProjectValidator.Validate(project, projectDirectory)` checks a project without building it.
- `PackageInspector.Inspect(setupExe, verifyStreams: true)` reads and verifies a built `Setup.exe`.
- `WingetManifests.Create(setupExe, options)` writes winget manifests for it.
- `BuildOptions.RuntimeRoot` (or the `FLUENTSETUP_RUNTIME` environment variable) points at another runtime folder;
  set the MSBuild property `FluentSetupCopyRuntime` to `false` to leave the bundled one out of your output.

Setup.exe itself needs nothing at install time (no .NET, no VC++ runtime). Building one needs Windows 10 1809 or later.

Documentation: [project file reference](https://github.com/TIANLI0/FluentSetup/blob/main/docs/project-file.md),
[MSBuild, dotnet tool and library](https://github.com/TIANLI0/FluentSetup/blob/main/docs/msbuild.md).
MIT license.
