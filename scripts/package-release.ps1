<#
.SYNOPSIS
  Builds the release assets into out\release (run scripts\build.ps1 first):
    fluentsetup-<version>-win-x64.zip   the fluentsetup CLI (self-contained: no .NET needed) with the installer runtime
    FluentSetup-<version>-Setup.exe     Builder app + CLI, packaged with FluentSetup itself
    SHA256SUMS.txt                      "<sha256>  <file>" for both (sha256sum format)
.PARAMETER Version
  Expected version (e.g. from the release tag, without "v"). Fails if the sources carry another version.
#>
param([string] $Version)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

# One version everywhere: managed assemblies, native stub, self-package.
$props = [xml](Get-Content Directory.Build.props -Raw)
$managed = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$native = [regex]::Match((Get-Content src\Bootstrapper\main.cpp -Raw), 'kProductVersion\[\] = L"([^"]+)"').Groups[1].Value
$package = (Get-Content packaging\fluentsetup.json -Raw | ConvertFrom-Json).app.version
if ($managed -ne $native -or $managed -ne $package) {
    throw "Version mismatch: Directory.Build.props $managed, src\Bootstrapper\main.cpp $native, packaging\fluentsetup.json $package"
}
if ($Version -and $Version -ne $managed) { throw "The tag says $Version but the sources are $managed" }
$v = $managed
if (-not (Test-Path out\bin\x64\Release\FluentSetup.Stub.exe)) { throw 'Run scripts\build.ps1 first (the installer runtime is missing).' }

$release = Join-Path $root 'out\release'
Remove-Item -Recurse -Force $release -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $release | Out-Null

# 1. CLI: self-contained single file, the runtime folder next to it.
$name = "fluentsetup-$v-win-x64"
$stage = Join-Path $release "stage\$name"
dotnet publish src\CLI\FluentSetup.CLI.csproj -c Release -r win-x64 --self-contained true -o $stage --nologo -v q `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed' }
Copy-Item LICENSE, THIRD-PARTY-NOTICES.md, README.md $stage
if (-not (Test-Path (Join-Path $stage 'runtime\x64\FluentSetup.Stub.exe'))) { throw 'The published CLI has no installer runtime' }
& (Join-Path $stage 'fluentsetup.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'The published CLI does not start' }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $release "$name.zip") -CompressionLevel Optimal

# 2. FluentSetup's own installer (Builder app + CLI on PATH).
& (Join-Path $PSScriptRoot 'package-fluentsetup.ps1')
Copy-Item "out\product\dist\FluentSetup-$v-Setup.exe" $release

# 3. Checksums.
Remove-Item -Recurse -Force (Join-Path $release 'stage')
$sums = Get-ChildItem $release -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $sums)
Get-ChildItem $release | Format-Table Name, Length -AutoSize
