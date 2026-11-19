<#
.SYNOPSIS
  Packages FluentSetup itself (Builder app + fsetup CLI) with FluentSetup: publishes both into out/product/app
  (CLI in cli\, which the installer puts on PATH) and builds out/product/dist/FluentSetup-<version>-Setup.exe.
  Run scripts/build.ps1 first (it builds the installer runtime the package embeds).
  Release builds (signed in with TIANLI0/codesign) sign the published Builder and CLI binaries, then FluentSetup signs
  the Setup.exe and the uninstall.exe it installs (packaging\fluentsetup.json "signing").
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

# Authenticode: only when the TIANLI0/codesign action has signed in to the Certum service (release workflow; it sets
# CODESIGN_SIGN and CODESIGN_THUMBPRINT). Local and CI builds stay unsigned. Only FluentSetup's own binaries.
function Invoke-CodeSign([string] $Root, [string[]] $Path) {
    if ($env:CODESIGN_SIGN) {
        & $env:CODESIGN_SIGN -Root $Root -Path $Path -Description 'FluentSetup' -DescriptionUrl 'https://github.com/TIANLI0/FluentSetup'
    }
}
$app = Join-Path $root 'out\product\app'
Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
dotnet publish src\Builder\FluentSetup.Builder.csproj -c Release -p:Platform=x64 -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publishing the Builder app failed' }
dotnet publish src\CLI\FluentSetup.CLI.csproj -c Release -o (Join-Path $app 'cli') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed' }
Invoke-CodeSign $app @('FluentSetup.Builder.exe', 'FluentSetup.Builder.dll', 'FluentSetup.Builder.Core.dll',
                       'cli/fsetup.exe', 'cli/fluentsetup.exe', 'cli/fsetup.dll', 'cli/FluentSetup.Builder.Core.dll')
$signArgs = if ($env:CODESIGN_SIGN) { @('--sign', '-D', "CodesignThumbprint=$env:CODESIGN_THUMBPRINT") } else { @() }
& (Join-Path $app 'cli\fsetup.exe') build packaging\fluentsetup.json @signArgs
if ($LASTEXITCODE -ne 0) { throw 'Building the FluentSetup installer failed' }