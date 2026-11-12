<#
.SYNOPSIS
  Builds FluentSetup: native runtime (stub + WinUI front end), Builder.Core, CLI, Builder GUI and tests.
.PARAMETER Configuration  Release (default) or Debug
.PARAMETER Platforms      x64 (default); add ARM64 to also build the ARM64 installer runtime.
.PARAMETER Skip           Components to skip: Native, WinUI, Managed, Builder, Tests
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string[]] $Platforms = @('x64'),
    [string[]] $Skip = @()
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($path) { return $path }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'MSBuild not found. Install Visual Studio 2022+ with the C++ desktop and Windows App SDK C++ workloads.'
}

function Invoke-Step([string] $name, [scriptblock] $action) {
    Write-Host "==> $name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)" }
    Write-Host ("    done in {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

$msbuild = Find-MSBuild
$nuget = Join-Path $root 'out\tools\nuget.exe'
if (-not (Test-Path $nuget)) {
    New-Item -ItemType Directory -Force (Split-Path $nuget) | Out-Null
    Invoke-WebRequest -UseBasicParsing 'https://dist.nuget.org/win-x86-commandline/v6.14.0/nuget.exe' -OutFile $nuget
}

if ($Skip -notcontains 'WinUI' -or $Skip -notcontains 'Native') {
    Invoke-Step 'Restore native NuGet packages' { & $nuget restore src\Setup.WinUI\packages.config -PackagesDirectory packages -NonInteractive -Verbosity quiet }
}

foreach ($platform in $Platforms) {
    if ($Skip -notcontains 'Native') {
        Invoke-Step "Native stub ($platform)" { & $msbuild src\Bootstrapper\FluentSetup.Stub.vcxproj /m /nologo /v:m "/p:Configuration=$Configuration" "/p:Platform=$platform" }
    }
    if ($Skip -notcontains 'WinUI') {
        Invoke-Step "WinUI front end ($platform)" { & $msbuild src\Setup.WinUI\FluentSetup.UI.vcxproj /m /nologo /v:m "/p:Configuration=$Configuration" "/p:Platform=$platform" }
    }
    if ($Skip -notcontains 'Tests') {
        Invoke-Step "Native tests ($platform)" { & $msbuild tests\Native\FluentSetup.Tests.Native.vcxproj /m /nologo /v:m "/p:Configuration=$Configuration" "/p:Platform=$platform" }
    }
}

if ($Skip -notcontains 'Managed') {
    Invoke-Step 'Builder.Core + CLI' { dotnet build src\CLI\FluentSetup.CLI.csproj -c $Configuration -nologo -v q }
}
if ($Skip -notcontains 'Builder') {
    Invoke-Step 'Builder (WinUI 3)' { dotnet build src\Builder\FluentSetup.Builder.csproj -c $Configuration -nologo -v q -p:Platform=x64 }
}
if ($Skip -notcontains 'Tests') {
    Invoke-Step 'Managed tests' { dotnet build tests\Builder.Core.Tests\FluentSetup.Builder.Core.Tests.csproj -c $Configuration -nologo -v q }
}
Write-Host 'Build complete.' -ForegroundColor Green
