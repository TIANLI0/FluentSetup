# FluentSetup.MSBuild

Builds a native Windows 11-style installer of your .NET app as part of `dotnet publish`, with
[FluentSetup](https://github.com/TIANLI0/FluentSetup). The package brings its own `fluentsetup` CLI and installer
runtime: no global tool to install.

```xml
<ItemGroup>
  <PackageReference Include="FluentSetup.MSBuild" Version="1.2.1" />
</ItemGroup>
<PropertyGroup>
  <FluentSetupProject>installer\fluentsetup.json</FluentSetupProject>
</PropertyGroup>
```

In `installer\fluentsetup.json`, take the version and the files from the publish:

```json
"app": { "id": "Contoso.MyApp", "name": "My App", "publisher": "Contoso", "version": "$(Version)", "mainExecutable": "MyApp.exe" },
"files": { "source": "$(PublishDir)" }
```

```powershell
dotnet publish -c Release -r win-x64     # -> installer\dist\My App-<Version>-Setup.exe
```

The target passes `-D Version=$(Version)`, `-D PublishDir=<publish folder>` and the architecture of the
`RuntimeIdentifier` (`win-x64`, `win-arm64`). Diagnostics appear as build errors and warnings with their FS codes, and
a failed installer build fails the publish.

| Property | Meaning |
|---|---|
| `FluentSetupProject` | The installer's `fluentsetup.json`, relative to the project. Nothing happens without it. |
| `FluentSetupOutputDirectory` | Folder for Setup.exe (default: `output.directory` of the project). |
| `FluentSetupArchitecture` | `x64` or `arm64` (default: from `RuntimeIdentifier`, else `output.architecture`). |
| `FluentSetupDefines` | More `$(Name)` values: `Channel=stable;Edition=Pro`. |
| `FluentSetupArguments` | More `fluentsetup build` arguments, e.g. `--sign` or `--verbose`. |
| `FluentSetupEnabled` | `false` skips the installer. |
| `FluentSetupCliPath` | Another `fluentsetup.dll` or `fluentsetup.exe` to run. |

Needs the .NET 10 SDK or newer on Windows 10 1809 or later. Documentation:
[MSBuild integration](https://github.com/TIANLI0/FluentSetup/blob/main/docs/msbuild.md),
[project file reference](https://github.com/TIANLI0/FluentSetup/blob/main/docs/project-file.md). MIT license.
