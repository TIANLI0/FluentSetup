<#
.SYNOPSIS
  End-to-end test of the NuGet packages. Nothing is installed or run from the built installers: the packages come from
  a local folder feed into a private package cache, the tool goes to a temporary --tool-path, and every Setup.exe is
  only inspected. Runs at idle priority, so it can run on a PC that is in use.

    FluentSetup.MSBuild       a new console app with <FluentSetupProject> gets a Setup.exe from "dotnet publish"
                              (version, files and architecture from the publish); a broken fluentsetup.json fails the
                              publish with its FS code; FluentSetupEnabled=false skips the installer
    FluentSetup.Cli           dotnet tool install --tool-path, then fluentsetup build and winget with the tool's runtime
    FluentSetup.Builder.Core  a console app builds a Setup.exe with PackageBuilder and the runtime the package copies

.PARAMETER Feed       Folder with the .nupkg files (default: packs them with scripts\pack-nuget.ps1 first).
.PARAMETER NativeOut  Native build output for scripts\pack-nuget.ps1 (default: out\bin).
.PARAMETER KeepArtifacts  Keep the temporary folder.
#>
param([string] $Feed, [string] $NativeOut, [switch] $KeepArtifacts)
$ErrorActionPreference = 'Stop'
(Get-Process -Id $PID).PriorityClass = 'Idle'   # inherited by dotnet and everything it starts
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if ($Feed) { $Feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Feed) }
if ($NativeOut) { $NativeOut = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($NativeOut) }
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

# Outside the repository, so its Directory.Build.props does not apply to the test apps.
$work = Join-Path ([IO.Path]::GetTempPath()) ('fluentsetup-nuget-e2e-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
if (-not $Feed) {
    $Feed = Join-Path $work 'feed'
    $packArgs = @{ Output = $Feed }
    if ($NativeOut) { $packArgs.NativeOut = $NativeOut }
    & (Join-Path $root 'scripts\pack-nuget.ps1') @packArgs | Out-Host
}
Copy-Item (Join-Path $root 'global.json') $work
# FluentSetup.* only from the local feed, everything else (ZstdSharp.Port) from nuget.org; a private package cache so
# that nothing stale is used and the user's cache is left alone.
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$Feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="FluentSetup.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content (Join-Path $work 'nuget.config') -Encoding UTF8
$env:NUGET_PACKAGES = Join-Path $work 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
$quiet = @('-nologo', '--disable-build-servers', '-p:UseSharedCompilation=false')

$results = [System.Collections.Generic.List[object]]::new()
function Check([string] $name, [scriptblock] $condition) {
    $ok = $false
    try { $ok = [bool](& $condition) } catch { $ok = $false; $name += " (exception: $($_.Exception.Message))" }
    $results.Add([pscustomobject]@{ Test = $name; Passed = $ok })
    Write-Host ("  [{0}] {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name) -ForegroundColor ($(if ($ok) { 'Green' } else { 'Red' }))
}
function Write-Utf8([string] $path, [string] $text) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
function Invoke-Dotnet([string[]] $arguments) {
    $out = & dotnet @arguments 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out -join "`n") }
}
function Inspect([string] $cli, [string] $setup) {
    $json = if ($cli.EndsWith('.dll')) { & dotnet $cli inspect $setup --verify --json } else { & $cli inspect $setup --verify --json }
    return ($json -join "`n") | ConvertFrom-Json
}

# ---- FluentSetup.MSBuild ---------------------------------------------------------------------------------------------
Write-Host "FluentSetup.MSBuild $version" -ForegroundColor Cyan
$app = Join-Path $work 'NuGetE2E'
Write-Utf8 (Join-Path $app 'NuGetE2E.csproj') @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Version>2.3.4</Version>
    <FluentSetupProject>installer\fluentsetup.json</FluentSetupProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentSetup.MSBuild" Version="$version" />
  </ItemGroup>
</Project>
"@
Write-Utf8 (Join-Path $app 'Program.cs') 'System.Console.WriteLine("Hello from the FluentSetup NuGet test");'
$projectJson = @'
{
  "schemaVersion": 1,
  "app": { "id": "FluentSetup.NuGetE2E", "name": "NuGet E2E", "publisher": "FluentSetup tests", "version": "$(Version)", "mainExecutable": "NuGetE2E.exe" },
  "files": { "source": "$(PublishDir)" },
  "output": { "directory": "dist", "compression": "fast" }
}
'@
Write-Utf8 (Join-Path $app 'installer\fluentsetup.json') $projectJson
Write-Utf8 (Join-Path $app 'installer\broken.json') $projectJson.Replace('"NuGetE2E.exe"', '"Missing.exe"')

$r = Invoke-Dotnet (@('publish', $app, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false') + $quiet)
$setup = Join-Path $app 'installer\dist\NuGet E2E-2.3.4-Setup.exe'
$cli = Join-Path $env:NUGET_PACKAGES "fluentsetup.msbuild\$version\tools\cli\fluentsetup.dll"
Check 'dotnet publish succeeds and runs the FluentSetup target' { $r.Code -eq 0 -and $r.Out -match 'FluentSetup: building the installer' }
if ($r.Code -ne 0) { Write-Host $r.Out }
Check 'Setup.exe in output.directory, named with $(Version) of the app' { Test-Path $setup }
$info = Inspect $cli $setup
Check 'the package is valid: version 2.3.4, x64 (from win-x64), the publish output as payload' {
    $info.problems.Count -eq 0 -and $info.header.app.version -eq '2.3.4' -and $info.header.app.architecture -eq 'x64' -and
    ($info.header.payload.files | Where-Object { $_.path -eq 'NuGetE2E.exe' }) -and ($info.header.payload.files | Where-Object { $_.path -eq 'NuGetE2E.dll' })
}

$out = Join-Path $work 'setup-out'
$r = Invoke-Dotnet (@('publish', $app, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:Version=2.3.5', "-p:FluentSetupOutputDirectory=$out") + $quiet)
Check 'FluentSetupOutputDirectory and -p:Version are honoured' { $r.Code -eq 0 -and (Test-Path (Join-Path $out 'NuGet E2E-2.3.5-Setup.exe')) }

$r = Invoke-Dotnet (@('publish', $app, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:FluentSetupProject=installer\broken.json') + $quiet)
Check 'a broken fluentsetup.json fails the publish with an FS error code' { $r.Code -ne 0 -and $r.Out -match 'broken\.json : error FS\d{4}: \[app\.mainExecutable\]' }

$before = (Get-Item $setup).LastWriteTimeUtc
$r = Invoke-Dotnet (@('publish', $app, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:FluentSetupEnabled=false') + $quiet)
Check 'FluentSetupEnabled=false publishes without building an installer' { $r.Code -eq 0 -and $r.Out -notmatch 'FluentSetup: building' -and (Get-Item $setup).LastWriteTimeUtc -eq $before }

if (Test-Path (Join-Path (Split-Path $cli) 'runtime\arm64\FluentSetup.Stub.exe')) {
    $r = Invoke-Dotnet (@('publish', $app, '-c', 'Release', '-r', 'win-arm64', '--self-contained', 'false', '-p:Version=2.3.6') + $quiet)
    $arm = Join-Path $app 'installer\dist\NuGet E2E-2.3.6-Setup.exe'
    Check 'win-arm64 publish gives an arm64 Setup.exe' { $r.Code -eq 0 -and (Inspect $cli $arm).header.app.architecture -eq 'arm64' }
}
$publishDir = Join-Path $app 'bin\Release\net10.0\win-x64\publish'

# ---- FluentSetup.Cli -------------------------------------------------------------------------------------------------
if (Test-Path (Join-Path $Feed "FluentSetup.Cli.$version.nupkg")) {
    Write-Host "FluentSetup.Cli $version" -ForegroundColor Cyan
    $tools = Join-Path $work 'tools'
    $r = Invoke-Dotnet @('tool', 'install', 'FluentSetup.Cli', '--version', $version, '--tool-path', $tools, '--configfile', (Join-Path $work 'nuget.config'))
    $tool = Join-Path $tools 'fluentsetup.exe'
    Check 'dotnet tool install --tool-path gives the fluentsetup command' { $r.Code -eq 0 -and (& $tool --version) -like "$version*" }
    if ($r.Code -ne 0) { Write-Host $r.Out }
    $toolOut = Join-Path $work 'tool-dist'
    $build = & $tool build (Join-Path $app 'installer\fluentsetup.json') -D Version=5.6.7 -D "PublishDir=$publishDir" -o $toolOut 2>&1 | ForEach-Object { "$_" }
    $toolSetup = Join-Path $toolOut 'NuGet E2E-5.6.7-Setup.exe'
    Check 'the tool builds Setup.exe with the runtime inside the tool package' { $LASTEXITCODE -eq 0 -and (Inspect $tool $toolSetup).problems.Count -eq 0 }
    if (-not (Test-Path $toolSetup)) { Write-Host ($build -join "`n") }
    & $tool winget $toolSetup --url 'https://example.com/NuGetE2E-Setup.exe' --license MIT -o (Join-Path $work 'winget') | Out-Null
    Check 'fluentsetup winget writes the three manifests' {
        $LASTEXITCODE -eq 0 -and @(Get-ChildItem (Join-Path $work 'winget\manifests\f\FluentSetup\NuGetE2E\5.6.7') -Filter *.yaml).Count -eq 3 }
    $r = Invoke-Dotnet @('tool', 'uninstall', 'FluentSetup.Cli', '--tool-path', $tools)
    Check 'dotnet tool uninstall --tool-path removes it again' { $r.Code -eq 0 -and -not (Test-Path $tool) }
} else {
    Write-Host '  [SKIP] FluentSetup.Cli (not in the feed: see scripts\pack-nuget.ps1)' -ForegroundColor Yellow
}

# ---- FluentSetup.Builder.Core ----------------------------------------------------------------------------------------
Write-Host "FluentSetup.Builder.Core $version" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Feed "FluentSetup.Builder.Core.$version.nupkg"))
$libTfm = ($zip.Entries | Where-Object { $_.FullName -like 'lib/*/FluentSetup.Builder.Core.dll' } | Select-Object -First 1).FullName.Split('/')[1]
$zip.Dispose()
# The app targets what the library targets (net10.0, or net10.0-windows10.0.19041 written as ...19041.0).
$tfm = $libTfm -replace '-windows(\d+\.\d+\.\d+)$', '-windows$1.0'
$lib = Join-Path $work 'LibE2E'
Write-Utf8 (Join-Path $lib 'LibE2E.csproj') @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$tfm</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentSetup.Builder.Core" Version="$version" />
  </ItemGroup>
</Project>
"@
Write-Utf8 (Join-Path $lib 'Program.cs') @'
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

var report = await new PackageBuilder().BuildAsync(ProjectSerializer.Load(args[0]), args[0], new BuildOptions
{
    Defines = new() { ["Version"] = "3.4.5", ["PublishDir"] = args[1] },
    OutputDirectory = args[2],
});
System.Console.WriteLine(report.Success ? $"Built {report.OutputPath}" : $"Failed: {report.Error}");
return report.Success ? 0 : 1;
'@
$r = Invoke-Dotnet (@('build', $lib, '-c', 'Release', '-o', (Join-Path $lib 'out')) + $quiet)
Check 'a project referencing the library builds and gets runtime\ next to it' { $r.Code -eq 0 -and (Test-Path (Join-Path $lib 'out\runtime\x64\FluentSetup.Stub.exe')) }
if ($r.Code -ne 0) { Write-Host $r.Out }
$libOut = Join-Path $work 'lib-dist'
$run = & (Join-Path $lib 'out\LibE2E.exe') (Join-Path $app 'installer\fluentsetup.json') $publishDir $libOut 2>&1 | ForEach-Object { "$_" }
Check 'PackageBuilder from the package builds a valid Setup.exe' { $LASTEXITCODE -eq 0 -and (Inspect $cli (Join-Path $libOut 'NuGet E2E-3.4.5-Setup.exe')).problems.Count -eq 0 }
if ($LASTEXITCODE -ne 0) { Write-Host ($run -join "`n") }

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host ("{0} checks, {1} passed, {2} failed" -f $results.Count, ($results.Count - $failed.Count), $failed.Count) -ForegroundColor ($(if ($failed.Count) { 'Red' } else { 'Green' }))
Remove-Item Env:NUGET_PACKAGES
if ($KeepArtifacts) { Write-Host "Kept $work" } else { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
exit $failed.Count
