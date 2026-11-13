<#
.SYNOPSIS
  Packs the NuGet packages (run scripts\build.ps1 first: each package carries the installer runtime):
    FluentSetup.Builder.Core.<version>.nupkg (+ .snupkg)   library: project model, validation, PackageBuilder
    FluentSetup.Cli.<version>.nupkg                        .NET tool: dotnet tool install -g FluentSetup.Cli
    FluentSetup.MSBuild.<version>.nupkg                    "dotnet publish" also builds Setup.exe
  Checks that every package carries the installer runtime and prints the package sizes. See docs/msbuild.md.
.PARAMETER Output     Folder for the packages (default: out\nuget).
.PARAMETER NativeOut  Folder with the native build output (default: out\bin), e.g. another checkout's out\bin.
#>
param(
    [string] $Output = 'out\nuget',
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string] $NativeOut
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
# Relative -NativeOut: from the caller's folder; relative -Output: from the repository root.
$native = if ($NativeOut) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($NativeOut).TrimEnd('\') + '\' } else { Join-Path $root 'out\bin\' }
Set-Location $root
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (-not (Test-Path (Join-Path $native 'x64\Release\FluentSetup.Stub.exe'))) { throw "The installer runtime is missing in $native (run scripts\build.ps1 first)." }
New-Item -ItemType Directory -Force $Output | Out-Null
# Ends with "/": a trailing backslash would escape the closing quote of the argument.
$common = @('-c', $Configuration, '-o', $Output, '-nologo', '-v', 'q', "-p:FsNativeOut=$($native.TrimEnd('\'))/")

function Invoke-Pack([string] $project) {
    Write-Host "==> dotnet pack $project" -ForegroundColor Cyan
    dotnet pack $project @common
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed (exit code $LASTEXITCODE)" }
}

Invoke-Pack src\Builder.Core\FluentSetup.Builder.Core.csproj
# A .NET tool needs a TargetFramework without a platform (net10.0, not net10.0-windows...): NETSDK1146 otherwise.
$platform = (dotnet msbuild src\CLI\FluentSetup.CLI.csproj -nologo -getProperty:TargetPlatformIdentifier).Trim()
if ($platform) {
    Write-Host "::warning::FluentSetup.Cli skipped: the CLI targets a $platform-specific framework, which a .NET tool cannot (NETSDK1146)."
} else {
    Invoke-Pack src\CLI\FluentSetup.CLI.csproj
}
Invoke-Pack src\MSBuild\FluentSetup.MSBuild.csproj

# Every package must be able to build an installer on its own.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$props = [xml](Get-Content Directory.Build.props -Raw)
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$expected = [ordered]@{
    'FluentSetup.Builder.Core' = @('lib/*/FluentSetup.Builder.Core.dll', 'buildTransitive/FluentSetup.Builder.Core.targets', 'runtime/x64/FluentSetup.Stub.exe', 'runtime/localization/en-US.json', 'runtime/default-app.ico')
    'FluentSetup.Cli'          = @('tools/*/any/fluentsetup.dll', 'tools/*/any/DotnetToolSettings.xml', 'tools/*/any/runtime/x64/FluentSetup.Stub.exe', 'tools/*/any/runtime/localization/en-US.json')
    'FluentSetup.MSBuild'      = @('build/FluentSetup.MSBuild.props', 'build/FluentSetup.MSBuild.targets', 'tools/cli/fluentsetup.dll', 'tools/cli/runtime/x64/FluentSetup.Stub.exe', 'tools/cli/runtime/localization/en-US.json')
}
foreach ($id in $expected.Keys) {
    $file = Join-Path $Output "$id.$version.nupkg"
    if (-not (Test-Path $file)) {
        if ($id -eq 'FluentSetup.Cli' -and $platform) { continue }
        throw "$file was not created"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $names = $zip.Entries | ForEach-Object FullName
        foreach ($pattern in $expected[$id] + @('README.md', 'icon.png')) {
            if (-not ($names | Where-Object { $_ -like $pattern })) { throw "$id has no $pattern" }
        }
    } finally { $zip.Dispose() }
}
Get-ChildItem $Output -File | Where-Object { $_.Name -like "*.$version.*nupkg" } | Sort-Object Name |
    Format-Table Name, @{ Label = 'Size'; Expression = { '{0:N0} KB' -f ($_.Length / 1KB) }; Align = 'Right' } -AutoSize
