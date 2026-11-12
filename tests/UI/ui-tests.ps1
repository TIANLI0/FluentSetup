<#
.SYNOPSIS
  UI tests: drives a real per-user install through each installer front end with UI Automation.

  For every front end (WinUI 3 when the Windows App Runtime is present, Win32 fallback always):
  launch Setup.exe -> first page shows the version in the title, the path with an inline "Change" button and the install
  options inline (no separate options page) -> turn on the desktop shortcut, turn off "Launch when ready" -> Next ->
  accept the license -> Install -> result page -> Close -> files/shortcut/ARP present -> silent uninstall -> clean.

  Requires a built CLI and stub (scripts/build.ps1). Uses English strings (/lang en-US) so element names are stable.
.PARAMETER FrontEnds  winui, win32 (default: both; winui is skipped when the runtime is missing)
#>
param([string[]] $FrontEnds = @('winui', 'win32'))
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root
Import-Module (Join-Path $PSScriptRoot 'Uia.psm1') -Force

$cli = Join-Path $root 'src\CLI\bin\Release\net10.0-windows10.0.19041.0\fluentsetup.exe'
if (-not (Test-Path $cli)) { throw 'Build the CLI first (scripts/build.ps1).' }
$msbuild = & (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1

$appId = 'FluentSetup.UITest'
$appName = 'FluentSetup UITest'
$version = '1.0.0'
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$arpKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$work = Join-Path $root 'out\ui-tests'
$results = [System.Collections.Generic.List[object]]::new()
$browseName = (Get-Content shared\localization\en-US.json -Raw -Encoding UTF8 | ConvertFrom-Json).'options.browseTitle'  # WinUI's accessible name for Change

function Check([string] $name, [scriptblock] $condition) {
    $ok = $false
    try { $ok = [bool](& $condition) } catch { $name += " (exception: $($_.Exception.Message))" }
    $results.Add([pscustomobject]@{ Test = $name; Passed = $ok })
    Write-Host ("  [{0}] {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name) -ForegroundColor ($(if ($ok) { 'Green' } else { 'Red' }))
}

function Remove-Existing {
    if (Test-Path $arpKey) {
        $quiet = (Get-ItemProperty $arpKey).QuietUninstallString
        if ($quiet -match '^"([^"]+)"\s*(.*)$') { Start-Process $Matches[1] -ArgumentList $Matches[2] -Wait }
    }
    Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue
    Remove-Item -Force $desktopLink -ErrorAction SilentlyContinue
}

# Win32 controls keep their mnemonic in the window text ("Create a &desktop shortcut"); try both spellings.
function Find-Any($window, [string[]] $names, [int] $timeout = 15) {
    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        foreach ($n in $names) { if (Test-UiaElement -Root $window -Name $n) { return $n } }
        Start-Sleep -Milliseconds 200
    }
    throw "None of '$($names -join "', '")' appeared within $timeout s"
}

function Wait-Window([System.Diagnostics.Process] $setup, [string] $ui) {
    if ($ui -eq 'win32') { return Get-UiaWindow -ProcessId $setup.Id }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($setup.Id)" | Where-Object Name -eq 'FluentSetup.UI.exe' | Select-Object -First 1
        if ($child) { return Get-UiaWindow -ProcessId $child.ProcessId }
        if ($setup.HasExited) { throw "Setup.exe exited early (code $($setup.ExitCode))" }
        Start-Sleep -Milliseconds 200
    }
    throw 'The WinUI front end did not start'
}

# --- package --------------------------------------------------------------------------------------------------------
Write-Host '==> Preparing the package' -ForegroundColor Cyan
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
$bin = Join-Path $work 'bin'; $stage = Join-Path $work 'stage'
New-Item -ItemType Directory -Force $stage, (Join-Path $work 'logs') | Out-Null
& $msbuild samples\DemoApp\DemoApp.vcxproj /p:Configuration=Release /p:Platform=x64 "/p:DemoVersion=$version" "/p:DemoOut=$bin\" /nologo /v:q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'DemoApp build failed' }
Copy-Item (Join-Path $bin 'DemoApp.exe') $stage
$project = Get-Content samples\DemoApp\fluentsetup.json -Raw | ConvertFrom-Json
$project.app.id = $appId
$project.app.name = $appName
$project.app.version = $version
$project.files.source = 'stage'
$project.ui.license = '..\..\samples\DemoApp\LICENSE.txt'
$project.output.directory = 'dist'
$projectPath = Join-Path $work 'project.json'
[IO.File]::WriteAllText($projectPath, ($project | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
& $cli build $projectPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed' }
$setupExe = Join-Path $work "dist\$appName-$version-Setup.exe"

$runtime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2*' -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq 'X64' }
foreach ($ui in $FrontEnds) {
    if ($ui -eq 'winui' -and -not $runtime) {
        Write-Host '==> winui: skipped (Windows App Runtime 2.x not installed; Setup.exe would fall back to Win32)' -ForegroundColor Yellow
        continue
    }
    Write-Host "==> $ui front end: interactive install" -ForegroundColor Cyan
    Remove-Existing
    $log = Join-Path $work "logs\$ui.log"
    $setup = Start-Process -FilePath $setupExe -ArgumentList '/ui', $ui, '/lang', 'en-US', '/log', "`"$log`"" -PassThru
    try {
        $w = Wait-Window $setup $ui
        Check "${ui}: title shows name and version" { Find-Any $w @("Install $appName ${version}?") }
        Check "${ui}: Change button next to the location" { Find-Any $w @('Change', '&Change', $browseName) }
        Check "${ui}: install scope inline" { (Find-Any $w @('Only me')) -and (Find-Any $w @('All users')) }
        $desktop = Find-Any $w @('Create a desktop shortcut', 'Create a &desktop shortcut')
        Check "${ui}: shortcut option inline on the first page" { $desktop }
        Set-UiaToggle -Root $w -Name $desktop -On $true
        Set-UiaToggle -Root $w -Name 'Launch when ready' -On $false
        Invoke-UiaButton -Root $w -Name 'Next'
        $accept = Find-Any $w @('I accept the terms of the license agreement', '&I accept the terms of the license agreement')
        Check "${ui}: license page" { $accept }
        Set-UiaToggle -Root $w -Name $accept -On $true
        Invoke-UiaButton -Root $w -Name 'Install'
        Check "${ui}: result page" { Find-Any $w @("$appName $version is installed") 120 }
        Invoke-UiaButton -Root $w -Name 'Close'
        Check "${ui}: Setup.exe exit code 0" { $setup.WaitForExit(30000) -and $setup.ExitCode -eq 0 }
    } finally {
        if (-not $setup.HasExited) {
            Get-CimInstance Win32_Process -Filter "ParentProcessId=$($setup.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
            Stop-Process -Id $setup.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Check "${ui}: app installed" { Test-Path (Join-Path $installDir 'DemoApp.exe') }
    Check "${ui}: desktop shortcut created (option honoured)" { Test-Path $desktopLink }
    Check "${ui}: registered in Apps & features" { (Get-ItemProperty $arpKey).DisplayVersion -eq $version }

    Write-Host "==> $ui front end: silent uninstall" -ForegroundColor Cyan
    $quiet = (Get-ItemProperty $arpKey).QuietUninstallString
    $code = -1
    if ($quiet -match '^"([^"]+)"\s*(.*)$') { $code = (Start-Process $Matches[1] -ArgumentList $Matches[2] -Wait -PassThru).ExitCode }
    Check "${ui}: uninstall exit code 0" { $code -eq 0 }
    Check "${ui}: environment restored" { -not (Test-Path $arpKey) -and -not (Test-Path $desktopLink) -and -not (Test-Path (Join-Path $installDir 'DemoApp.exe')) }
}

Remove-Existing
$failed = @($results | Where-Object { -not $_.Passed }).Count
Write-Host ''
Write-Host ("{0} checks, {1} passed, {2} failed" -f $results.Count, ($results.Count - $failed), $failed) -ForegroundColor ($(if ($failed) { 'Red' } else { 'Green' }))
if ($failed) { exit 1 }
