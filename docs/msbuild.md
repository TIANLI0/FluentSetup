# MSBuild integration, .NET tool and library

FluentSetup is also published as three NuGet packages, with the same version number as FluentSetup itself:

| Package | What it is | Use it to |
|---|---|---|
| `FluentSetup.MSBuild` | MSBuild targets plus their own copy of the CLI | build Setup.exe as part of `dotnet publish` |
| `FluentSetup.Cli` | `fluentsetup` as a .NET tool | use the CLI where .NET is installed: `dotnet tool install -g FluentSetup.Cli` |
| `FluentSetup.Builder.Core` | The library behind the CLI and the Builder app | build, validate and inspect packages from your own .NET code |

Each package carries the installer runtime (setup stub, WinUI front end, installer strings, default icon), so none of
them needs a FluentSetup installation. They need the .NET 10 SDK or runtime, or newer.

## FluentSetup.MSBuild: Setup.exe from `dotnet publish`

Reference the package and point `FluentSetupProject` at the installer's project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Version>1.2.3</Version>
    <FluentSetupProject>installer\fluentsetup.json</FluentSetupProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentSetup.MSBuild" Version="1.3.0" />
  </ItemGroup>
</Project>
```

Take the version and the files from the publish in `installer\fluentsetup.json`. The `variables` are only defaults for
editing the project in the Builder app or running `fluentsetup validate` by hand; the build overrides them:

```json
{
  "schemaVersion": 1,
  "variables": { "Version": "1.0.0", "PublishDir": "../bin/Release/net10.0-windows/win-x64/publish" },
  "app": { "id": "Contoso.MyApp", "name": "My App", "publisher": "Contoso", "version": "$(Version)", "mainExecutable": "MyApp.exe" },
  "files": { "source": "$(PublishDir)" },
  "output": { "directory": "dist" }
}
```

```powershell
dotnet publish -c Release -r win-x64        # also writes installer\dist\My App-1.2.3-Setup.exe
```

After the `Publish` target, the package runs its bundled CLI with the `dotnet` that runs the build:

```
dotnet <package>\tools\cli\fluentsetup.dll build <FluentSetupProject> --msbuild
       -D Version=$(Version) -D PublishDir=<publish folder, absolute, without a trailing separator>
       [--arch x64|arm64] [-o <FluentSetupOutputDirectory>] [-D <FluentSetupDefines> ...] [FluentSetupArguments]
```

The CLI output appears in the build log. With `--msbuild` its diagnostics use MSBuild's format
(`installer\fluentsetup.json : error FS1110: [app.mainExecutable] Main executable 'MyApp.exe' is not among the files to
install.`), so they are build errors and warnings with their codes, listed in Visual Studio's error list. Any failure,
including a non-zero exit code without such a line, fails the publish.

| Property | Default | Meaning |
|---|---|---|
| `FluentSetupProject` | `""` | The installer's `fluentsetup.json`, relative to the project file. Without it the package does nothing. |
| `FluentSetupOutputDirectory` | `""` | Folder for Setup.exe, relative to the project file. Empty means `output.directory` of the FluentSetup project. |
| `FluentSetupArchitecture` | `""` | `x64` or `arm64`. Empty means: from `RuntimeIdentifier` (`win-x64`, `win-arm64`, ...), else `output.architecture`. |
| `FluentSetupDefines` | `""` | More `$(Name)` values, separated by semicolons: `Channel=stable;Edition=Pro`. |
| `FluentSetupArguments` | `""` | More arguments for `fluentsetup build`, e.g. `--sign` or `--verbose`. |
| `FluentSetupEnabled` | `true` | `false` skips the installer, e.g. `dotnet publish -p:FluentSetupEnabled=false`. |
| `FluentSetupCliPath` | the package's copy | Another `fluentsetup.dll` (run with `dotnet`) or `fluentsetup.exe`. |

Notes:

- Only `dotnet publish` (the `Publish` target) builds the installer, not `dotnet build`. A project with several target
  frameworks runs it after each one's publish: publish one framework (`-f net10.0-windows`).
- The package is a development dependency: it does not flow to projects that reference yours.
- In CI no other setup is needed (no GitHub Action, no global tool): `actions/setup-dotnet` and `dotnet publish`.
  For signing, set `signing` in the project and pass the password variable to the step.

## FluentSetup.Cli: the .NET tool

```powershell
dotnet tool install -g FluentSetup.Cli         # fluentsetup on PATH for this user
dotnet tool update -g FluentSetup.Cli          # later versions (fluentsetup update is for the Setup.exe installation)

dotnet new tool-manifest                       # or per repository, pinned in .config/dotnet-tools.json:
dotnet tool install FluentSetup.Cli
dotnet tool run fluentsetup build installer\fluentsetup.json
```

The tool is the same `fluentsetup` as in the release zip and FluentSetup's own installer (all commands, see the
[CLI reference](project-file.md#cli-fluentsetup)); it needs the .NET runtime instead of being self-contained. Its
installer runtime is part of the tool's files.

## FluentSetup.Builder.Core: the library

```csharp
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;

var path = Path.GetFullPath(@"installer\fluentsetup.json");
var project = ProjectSerializer.Load(path);

// Validate only (what "fluentsetup validate" does before collecting files):
var problems = ProjectValidator.Validate(ProjectVariables.Expand(project, null), Path.GetDirectoryName(path)!);

var report = await new PackageBuilder().BuildAsync(project, path, new BuildOptions
{
    Defines = new() { ["Version"] = "1.2.3", ["PublishDir"] = @"C:\build\publish" },
    Architecture = "x64",
    Log = Console.WriteLine,
}, new Progress<BuildProgress>(p => Console.WriteLine(p.Message)));

foreach (var d in report.Diagnostics) Console.WriteLine(d);   // "warning FS1015 [app.mainExecutable]: ..."
if (report.Success) Console.WriteLine($"{report.OutputPath} ({report.Sizes.Total} bytes, SHA-256 {report.Sha256})");
```

| Type | Purpose |
|---|---|
| `ProjectSerializer` | Load, parse and save `fluentsetup.json` (unknown properties are kept). |
| `ProjectVariables` | `$(Name)` expansion with overrides, as `-D` does. |
| `ProjectValidator`, `FileCollector` | The checks and the file collection of `validate`, with `Diagnostic` codes and field paths. |
| `PackageBuilder`, `BuildOptions`, `BuildReport` | Build Setup.exe; the report has the sizes, the SHA-256 and the diagnostics. |
| `PackageInspector` | Read and verify a built Setup.exe (`fluentsetup inspect`). |
| `WingetManifests` | winget manifests for a built Setup.exe (`fluentsetup winget`, see [winget](winget.md)). |

The installer runtime is copied into the app's output and publish folders as `runtime\` (by the package's
`buildTransitive` targets), where `RuntimeAssets` looks for it by default. To use another runtime folder, set
`BuildOptions.RuntimeRoot` or the `FLUENTSETUP_RUNTIME` environment variable; the MSBuild property
`FluentSetupCopyRuntime` set to `false` then leaves the bundled copy out. The symbols are on NuGet's symbol server
(`.snupkg`) with Source Link to the exact sources on GitHub.

## Building the packages

```powershell
.\scripts\build.ps1                    # the native runtime the packages carry
.\scripts\pack-nuget.ps1               # -> out\nuget\*.nupkg, checks their contents
.\tests\MSBuild\msbuild-e2e.ps1        # end-to-end test of all three
```

`msbuild-e2e.ps1` packs the packages (or takes `-Feed <folder>`), then, in a temporary folder with a private package
cache: creates a console app that references `FluentSetup.MSBuild` and checks that `dotnet publish` produces a valid
Setup.exe (version, architecture and files from the publish; `FluentSetupOutputDirectory`; a broken project fails the
publish with its FS code; `FluentSetupEnabled=false`), installs `FluentSetup.Cli` into a temporary `--tool-path` and
builds with it, and builds a Setup.exe from a console app that references `FluentSetup.Builder.Core`. It installs
nothing on the PC and never runs the installers it builds.

The release workflow attaches the `.nupkg` files to the GitHub release (they are listed in `SHA256SUMS.txt`) and pushes
them to nuget.org. It uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) when the
repository variable `NUGET_USER` names the nuget.org account (with a policy for this repository and `release.yml`), so no
key is stored anywhere; otherwise a `NUGET_API_KEY` secret, if there is one.
