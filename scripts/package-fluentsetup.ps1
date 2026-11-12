<#
.SYNOPSIS
  Packages FluentSetup itself (Builder app + fluentsetup CLI) with FluentSetup: publishes both into out/product/app
  (CLI in cli\, which the installer puts on PATH) and builds out/product/dist/FluentSetup-<version>-Setup.exe.
  Run scripts/build.ps1 first (it builds the installer runtime the package embeds).
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root
$app = Join-Path $root 'out\product\app'
Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
dotnet publish src\Builder\FluentSetup.Builder.csproj -c Release -p:Platform=x64 -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publishing the Builder app failed' }
dotnet publish src\CLI\FluentSetup.CLI.csproj -c Release -o (Join-Path $app 'cli') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed' }
& (Join-Path $app 'cli\fluentsetup.exe') build packaging\fluentsetup.json
if ($LASTEXITCODE -ne 0) { throw 'Building the FluentSetup installer failed' }