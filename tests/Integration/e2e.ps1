<#
.SYNOPSIS
  End-to-end test of FluentSetup with the DemoApp sample, using real installs on this machine (per-user scope).

  build DemoApp 1.0/2.0/3.0 -> package -> install 1.0 -> verify files/shortcut/ARP -> reinstall same version
  -> upgrade to 2.0 (obsolete files removed, user data kept) -> downgrade blocked -> corrupted package refused
  -> injected failure during 3.0 upgrade rolls back to 2.0 -> custom path with spaces + CJK -> many small files
  -> delete Setup.exe -> uninstall through the registered QuietUninstallString -> environment restored.

.PARAMETER Large   Also test a 600 MB payload file (slower).
.PARAMETER KeepArtifacts  Do not delete the generated packages and logs.
#>
param([switch] $Large, [switch] $KeepArtifacts)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root
$msbuild = & (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$cli = Join-Path $root 'src\CLI\bin\Release\net10.0\fluentsetup.exe'
if (-not (Test-Path $cli)) { throw "Build the CLI first (scripts/build.ps1)." }

$work = Join-Path $root 'out\e2e'
$logs = Join-Path $work 'logs'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, $logs | Out-Null

$appId = 'FluentSetup.E2E'
$appName = 'FluentSetup E2E'
$installDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$arpKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$startLink = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$results = [System.Collections.Generic.List[object]]::new()

function Check([string] $name, [scriptblock] $condition) {
    $ok = $false
    try { $ok = [bool](& $condition) } catch { $ok = $false; $name += " (exception: $($_.Exception.Message))" }
    $results.Add([pscustomobject]@{ Test = $name; Passed = $ok })
    Write-Host ("  [{0}] {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name) -ForegroundColor ($(if ($ok) { 'Green' } else { 'Red' }))
}

function Run-Setup([string] $exe, [string[]] $arguments, [string] $logName) {
    $log = Join-Path $logs "$logName.log"
    $p = Start-Process -FilePath $exe -ArgumentList (@($arguments) + @('/log', "`"$log`"")) -Wait -PassThru
    return $p.ExitCode
}

function Installed-Version {
    $exe = Join-Path $installDir 'DemoApp.exe'
    if (-not (Test-Path $exe)) { return $null }
    $marker = Join-Path $installDir 'version-check.txt'
    Remove-Item $marker -ErrorAction SilentlyContinue
    Start-Process -FilePath $exe -ArgumentList '--version' -Wait
    $v = (Get-Content $marker -Raw -Encoding UTF8).Trim()
    Remove-Item $marker -ErrorAction SilentlyContinue
    return $v
}

function New-Package([string] $version, [scriptblock] $populate, [hashtable] $overrides = @{}, [hashtable] $rootProps = @{}) {
    $stage = Join-Path $work "stage-$version"
    $bin = Join-Path $work "bin-$version"
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    & $msbuild samples\DemoApp\DemoApp.vcxproj /p:Configuration=Release /p:Platform=x64 "/p:DemoVersion=$version" "/p:DemoOut=$bin" /nologo /v:q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "DemoApp $version build failed" }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item (Join-Path $bin 'DemoApp.exe') $stage
    & $populate $stage
    $project = Get-Content samples\DemoApp\fluentsetup.json -Raw | ConvertFrom-Json
    $project.app.id = $appId
    $project.app.name = $appName
    $project.app.version = $version
    $project.files.source = "stage-$version"
    $project.ui.license = '..\..\samples\DemoApp\LICENSE.txt'
    $project.output.directory = 'dist'
    foreach ($k in $overrides.Keys) { $project.install | Add-Member -NotePropertyName $k -NotePropertyValue $overrides[$k] -Force }
    foreach ($k in $rootProps.Keys) { $project | Add-Member -NotePropertyName $k -NotePropertyValue $rootProps[$k] -Force }
    $projectPath = Join-Path $work "project-$version.json"
    [IO.File]::WriteAllText($projectPath, ($project | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    & $cli build $projectPath --report (Join-Path $work "report-$version.json") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Packaging $version failed" }
    return (Join-Path $work "dist\$appName-$version-Setup.exe")
}

function Remove-Existing {
    if (Test-Path $arpKey) {
        $quiet = (Get-ItemProperty $arpKey).QuietUninstallString
        if ($quiet -match '^"([^"]+)"\s*(.*)$') { Start-Process $Matches[1] -ArgumentList $Matches[2] -Wait }
    }
    Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue
}

Write-Host '==> Preparing packages' -ForegroundColor Cyan
Remove-Existing
$v1 = New-Package '1.0.0' {
    param($s)
    New-Item -ItemType Directory -Force "$s\config", "$s\data\sub", "$s\资源 文件" | Out-Null
    'readme 1.0' | Set-Content "$s\readme.txt"
    '{"theme":"default"}' | Set-Content "$s\config\user-settings.json"
    'legacy' | Set-Content "$s\data\legacy.dat"
    1..300 | ForEach-Object { "item $_" | Set-Content "$s\data\sub\item$_.txt" }
    '中文内容' | Set-Content "$s\资源 文件\说明.txt" -Encoding UTF8
}
$v2 = New-Package '2.0.0' {
    param($s)
    New-Item -ItemType Directory -Force "$s\config", "$s\data\sub", "$s\资源 文件", "$s\plugins" | Out-Null
    'readme 2.0' | Set-Content "$s\readme.txt"
    '{"theme":"default"}' | Set-Content "$s\config\user-settings.json"
    1..300 | ForEach-Object { "item $_ v2" | Set-Content "$s\data\sub\item$_.txt" }
    '中文内容 2' | Set-Content "$s\资源 文件\说明.txt" -Encoding UTF8
    'plugin' | Set-Content "$s\plugins\new-plugin.dll"
}
$v3 = New-Package '3.0.0' {
    param($s)
    New-Item -ItemType Directory -Force "$s\data" | Out-Null
    1..400 | ForEach-Object { "v3 $_" | Set-Content "$s\data\f$_.txt" }
}
Write-Host ("    v1 {0:N0} bytes" -f (Get-Item $v1).Length)

Write-Host '==> Install 1.0.0 (silent, per-user)' -ForegroundColor Cyan
$code = Run-Setup $v1 @('/quiet') 'install-v1'
Check 'exit code 0' { $code -eq 0 }
Check 'main executable installed' { Test-Path "$installDir\DemoApp.exe" }
Check 'installed version is 1.0.0' { (Installed-Version) -eq '1.0.0' }
Check 'all 305 payload files present' { (Get-ChildItem -Recurse -File $installDir | Where-Object { $_.Name -notin 'uninstall.exe', 'uninstall.json' }).Count -eq 305 }
Check 'CJK path installed' { (Get-Content "$installDir\资源 文件\说明.txt" -Encoding UTF8) -eq '中文内容' }
Check 'empty directory created' { Test-Path "$installDir\logs" -PathType Container }
Check 'uninstaller present' { Test-Path "$installDir\uninstall.exe" }
Check 'Start menu shortcut created' { Test-Path $startLink }
Check 'Apps & features entry' { (Get-ItemProperty $arpKey).DisplayVersion -eq '1.0.0' -and (Get-ItemProperty $arpKey).DisplayName -eq $appName }
Check 'no work directory left behind' { -not (Test-Path "$installDir\.fsetup") }
Check 'no pending marker' { -not (Test-Path "HKCU:\Software\FluentSetup\Pending\$appId") }

'{"theme":"user-choice"}' | Set-Content "$installDir\config\user-settings.json"
'app created' | Set-Content "$installDir\logs\runtime.log"

Write-Host '==> Reinstall same version' -ForegroundColor Cyan
$code = Run-Setup $v1 @('/quiet') 'reinstall-v1'
Check 'exit code 0' { $code -eq 0 }
Check 'preserved user data kept on reinstall' { (Get-Content "$installDir\config\user-settings.json") -match 'user-choice' }

Write-Host '==> Upgrade to 2.0.0' -ForegroundColor Cyan
$code = Run-Setup $v2 @('/quiet') 'upgrade-v2'
Check 'exit code 0' { $code -eq 0 }
Check 'installed version is 2.0.0' { (Installed-Version) -eq '2.0.0' }
Check 'obsolete file removed' { -not (Test-Path "$installDir\data\legacy.dat") }
Check 'new file added' { Test-Path "$installDir\plugins\new-plugin.dll" }
Check 'changed file updated' { (Get-Content "$installDir\readme.txt") -eq 'readme 2.0' }
Check 'preserved user data kept on upgrade' { (Get-Content "$installDir\config\user-settings.json") -match 'user-choice' }
Check 'app-created file untouched' { Test-Path "$installDir\logs\runtime.log" }
Check 'ARP shows 2.0.0' { (Get-ItemProperty $arpKey).DisplayVersion -eq '2.0.0' }

Write-Host '==> Downgrade is blocked' -ForegroundColor Cyan
$code = Run-Setup $v1 @('/quiet') 'downgrade-v1'
Check 'exit code 10 (DowngradeBlocked)' { $code -eq 10 }
Check 'still 2.0.0' { (Installed-Version) -eq '2.0.0' }

Write-Host '==> Corrupted package is refused' -ForegroundColor Cyan
$corrupt = Join-Path $work 'corrupt-Setup.exe'
Copy-Item $v2 $corrupt
$bytes = [IO.File]::ReadAllBytes($corrupt)
$report = Get-Content (Join-Path $work 'report-2.0.0.json') -Raw | ConvertFrom-Json
$target = [int]($report.sizes.stub + $report.sizes.winUiFrontEnd + 64)   # inside the first payload stream
$bytes[$target] = $bytes[$target] -bxor 0xFF
[IO.File]::WriteAllBytes($corrupt, $bytes)
$code = Run-Setup $corrupt @('/quiet') 'corrupt'
Check 'exit code 4 (PackageCorrupt)' { $code -eq 4 }
Check 'installation untouched (2.0.0)' { (Installed-Version) -eq '2.0.0' }
Check 'no staging leftovers' { -not (Test-Path "$installDir\.fsetup") }
$truncated = Join-Path $work 'truncated-Setup.exe'
# Keep the PE image intact (Windows must be able to start it) and cut the appended package data.
$cut = [int]($report.sizes.stub + $report.sizes.winUiFrontEnd + 4096)
[IO.File]::WriteAllBytes($truncated, $bytes[0..($cut - 1)])
$code = Run-Setup $truncated @('/quiet') 'truncated'
Check 'truncated package refused (exit 4)' { $code -eq 4 }

Write-Host '==> Failure during upgrade rolls back' -ForegroundColor Cyan
$hashesBefore = Get-ChildItem -Recurse -File $installDir | Sort-Object FullName | Get-FileHash | ForEach-Object Hash
$env:FLUENTSETUP_TEST_FAULT_AFTER = '120'
$code = Run-Setup $v3 @('/quiet') 'faulted-v3'
Remove-Item Env:\FLUENTSETUP_TEST_FAULT_AFTER
Check 'exit code 12 (IoError, injected)' { $code -eq 12 }
Check 'version restored to 2.0.0' { (Installed-Version) -eq '2.0.0' }
$hashesAfter = Get-ChildItem -Recurse -File $installDir | Sort-Object FullName | Get-FileHash | ForEach-Object Hash
Check 'every file restored byte-for-byte' { -not (Compare-Object $hashesBefore $hashesAfter) }
Check 'ARP still 2.0.0' { (Get-ItemProperty $arpKey).DisplayVersion -eq '2.0.0' }
Check 'rollback left no work directory' { -not (Test-Path "$installDir\.fsetup") }

Write-Host '==> Original Setup.exe deleted, uninstall from Apps & features' -ForegroundColor Cyan
Remove-Item (Join-Path $work 'dist') -Recurse -Force
$quiet = (Get-ItemProperty $arpKey).QuietUninstallString
$null = $quiet -match '^"([^"]+)"\s*(.*)$'
$p = Start-Process $Matches[1] -ArgumentList ($Matches[2] + " /log `"$logs\uninstall.log`"") -Wait -PassThru
Check 'uninstall exit code 0' { $p.ExitCode -eq 0 }
Start-Sleep -Seconds 2
Check 'ARP entry removed' { -not (Test-Path $arpKey) }
Check 'shortcut removed' { -not (Test-Path $startLink) }
Check 'program files removed' { -not (Test-Path "$installDir\DemoApp.exe") -and -not (Test-Path "$installDir\data") -and -not (Test-Path "$installDir\plugins") }
Check 'uninstaller removed itself' { -not (Test-Path "$installDir\uninstall.exe") -and -not (Test-Path "$installDir\uninstall.json") }
Check 'user data kept' { Test-Path "$installDir\config\user-settings.json" }
Check 'app-created data kept' { Test-Path "$installDir\logs\runtime.log" }
Check 'no pending marker' { -not (Test-Path "HKCU:\Software\FluentSetup\Pending\$appId") }
Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue

Write-Host '==> Custom location with spaces and CJK characters' -ForegroundColor Cyan
$v2b = New-Package '2.1.0' { param($s) 'x' | Set-Content "$s\readme.txt"; New-Item -ItemType Directory "$s\config" | Out-Null; '{}' | Set-Content "$s\config\user-settings.json" }
$custom = Join-Path $env:LOCALAPPDATA 'FluentSetup 测试 目录\Demo App'
Remove-Item -Recurse -Force (Split-Path $custom) -ErrorAction SilentlyContinue
$code = Run-Setup $v2b @('/quiet', '/dir', "`"$custom`"") 'custom-dir'
Check 'exit code 0' { $code -eq 0 }
Check 'installed into the custom folder' { Test-Path "$custom\DemoApp.exe" }
Check 'ARP InstallLocation is the custom folder' { (Get-ItemProperty $arpKey).InstallLocation -eq $custom }
$code = Run-Setup "$custom\uninstall.exe" @('/uninstall', '/quiet') 'custom-uninstall'
Start-Sleep -Seconds 2
Check 'custom-folder uninstall exit code 0' { $code -eq 0 }
Check 'custom folder cleaned (only user data left)' { -not (Test-Path "$custom\DemoApp.exe") }
Remove-Item -Recurse -Force (Split-Path $custom) -ErrorAction SilentlyContinue

Write-Host '==> Unsafe locations are rejected' -ForegroundColor Cyan
$v2c = New-Package '2.2.0' { param($s) 'x' | Set-Content "$s\readme.txt"; New-Item -ItemType Directory "$s\config" | Out-Null; '{}' | Set-Content "$s\config\user-settings.json" }
$code = Run-Setup $v2c @('/quiet', '/dir', "`"$env:WINDIR\FluentSetupTest`"") 'dir-windows'
Check 'install into the Windows folder refused (exit 15)' { $code -eq 15 -and -not (Test-Path "$env:WINDIR\FluentSetupTest") }
$code = Run-Setup $v2c @('/quiet', '/dir', "`"$env:USERPROFILE`"") 'dir-profile'
Check 'install into the profile root refused (exit 15)' { $code -eq 15 }
$junctionTarget = Join-Path $work 'junction-target'
$junction = Join-Path $env:LOCALAPPDATA 'FluentSetupJunctionTest'
New-Item -ItemType Directory -Force $junctionTarget | Out-Null
Remove-Item $junction -Force -ErrorAction SilentlyContinue
cmd /c mklink /J "$junction" "$junctionTarget" | Out-Null
$code = Run-Setup $v2c @('/quiet', '/dir', "`"$junction\App`"") 'dir-junction'
Check 'install through a junction refused (exit 15)' { $code -eq 15 -and -not (Test-Path "$junctionTarget\App") }
cmd /c rmdir "$junction" | Out-Null
$code = Run-Setup $v2c @('/quiet', '/bogus') 'bad-args'
Check 'unknown argument refused (exit 3)' { $code -eq 3 }

Write-Host '==> Many small files' -ForegroundColor Cyan
$many = New-Package '4.0.0' {
    param($s)
    New-Item -ItemType Directory -Force "$s\config" | Out-Null; '{}' | Set-Content "$s\config\user-settings.json"
    foreach ($d in 1..40) {
        $dir = New-Item -ItemType Directory -Force "$s\assets\d$d"
        foreach ($f in 1..125) { [IO.File]::WriteAllText("$dir\a$f.txt", "asset $d/$f") }
    }
}
$sw = [Diagnostics.Stopwatch]::StartNew()
$code = Run-Setup $many @('/quiet') 'many-files'
$installSeconds = $sw.Elapsed.TotalSeconds
Check ("5,000+ files installed (exit 0, {0:N1}s)" -f $installSeconds) { $code -eq 0 -and (Get-ChildItem -Recurse -File "$installDir\assets").Count -eq 5000 }

if ($Large) {
    Write-Host '==> Large file' -ForegroundColor Cyan
    $big = New-Package '5.0.0' {
        param($s)
        New-Item -ItemType Directory -Force "$s\config" | Out-Null; '{}' | Set-Content "$s\config\user-settings.json"
        $buffer = New-Object byte[] (1MB); $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        $fs = [IO.File]::Create("$s\big.bin")
        for ($i = 0; $i -lt 600; $i++) { $rng.GetBytes($buffer); $fs.Write($buffer, 0, $buffer.Length) }
        $fs.Dispose()
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $code = Run-Setup $big @('/quiet') 'large'
    Check ("600 MB file installed and verified (exit 0, {0:N1}s)" -f $sw.Elapsed.TotalSeconds) { $code -eq 0 -and (Get-Item "$installDir\big.bin").Length -eq 600MB }
}

Write-Host '==> Options and actions (afterInstall / beforeUninstall), uninstall through uninstall.exe' -ForegroundColor Cyan
Remove-Existing
$marker = Join-Path $installDir 'version-check.txt'
$withActions = New-Package '5.0.0' { param($s) } @{} @{
    options = @(@{ id = 'marker'; label = @{ 'en-US' = 'Write a marker'; 'zh-CN' = 'Write a marker' }; default = $true })
    actions = @(
        @{ when = 'afterInstall'; run = 'DemoApp.exe'; arguments = @('--version'); ifOption = 'marker'; onFailure = 'fail' },
        @{ when = 'beforeUninstall'; run = 'DemoApp.exe'; arguments = @('--version'); ifOption = 'marker'; onFailure = 'fail' })
}
$code = Run-Setup $withActions @('/quiet', '/option', 'marker=no') 'actions-off'
Check 'option off: install exit 0, action skipped' { $code -eq 0 -and -not (Test-Path $marker) }
Remove-Existing
$code = Run-Setup $withActions @('/quiet') 'actions-on'
Check 'option on (default): afterInstall action ran' { $code -eq 0 -and (Test-Path $marker) }
Remove-Item $marker -ErrorAction SilentlyContinue
$quiet = (Get-ItemProperty $arpKey).QuietUninstallString
$code = -1
if ($quiet -match '^"([^"]+)"\s*(.*)$') { $code = (Start-Process $Matches[1] -ArgumentList $Matches[2] -Wait -PassThru).ExitCode }
Check 'uninstall.exe of a package with actions runs (exit 0)' { $code -eq 0 }
Check 'beforeUninstall action honoured the recorded choice' { Test-Path $marker }
Check 'uninstalled' { -not (Test-Path $arpKey) -and -not (Test-Path (Join-Path $installDir 'DemoApp.exe')) }

Write-Host '==> Legacy install adopted, install cleanup, required option, action fallbacks' -ForegroundColor Cyan
Remove-Existing
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# A fresh account (CI runner) may not have the Run key yet. Create it only when missing: New-Item -Force would
# replace an existing key and lose its values.
if (-not (Test-Path $runKey)) { New-Item $runKey | Out-Null }
$legacyKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FluentSetup.E2E.Legacy'
$legacyDir = Join-Path $env:LOCALAPPDATA 'Programs\FluentSetup E2E Legacy'
$oldLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'FluentSetup E2E Old.lnk'
Remove-Item -Recurse -Force $legacyDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $legacyDir | Out-Null
'old build' | Set-Content (Join-Path $legacyDir 'DemoApp.exe')
New-Item -Force $legacyKey | Out-Null  # an older installer's entry without InstallLocation: found through UninstallString
Set-ItemProperty $legacyKey -Name DisplayName -Value 'FluentSetup E2E (old name)'
Set-ItemProperty $legacyKey -Name DisplayVersion -Value '0.9.0'
Set-ItemProperty $legacyKey -Name UninstallString -Value "`"$legacyDir\uninst.exe`""
Set-ItemProperty $runKey -Name 'FluentSetupE2E-Old' -Value 'old.exe'
'' | Set-Content $oldLink
$legacyPkg = New-Package '6.0.0' { param($s) } @{
    legacyUninstallKeys = @('FluentSetup.E2E.Legacy')
    cleanup = @{ removeRunValues = @('FluentSetupE2E-Old'); removeShortcuts = @('FluentSetup E2E Old.lnk') }
} @{
    options = @(@{ id = 'core'; label = @{ 'en-US' = 'Core component (required)' }; required = $true })
    actions = @(
        @{ id = 'retry'; when = 'afterInstall'; run = 'DemoApp.exe'; arguments = @('--exit-code', '5'); fallbackArguments = @('--version'); ifOption = 'core'; onFailure = 'fail' },
        @{ when = 'afterInstall'; run = 'DemoApp.exe'; arguments = @('--exit-code', '7'); onFailure = 'warn'; fallbackRunValue = @{ name = 'FluentSetupE2E-Auto'; arguments = @('--autostart') } })
    uninstall = @{ removeRunValues = @('FluentSetupE2E-Auto') }
}
$code = Run-Setup $legacyPkg @('/quiet', '/option', 'core=no') 'legacy'
$legacyMarker = Join-Path $legacyDir 'version-check.txt'
Check 'legacy: install exit 0' { $code -eq 0 }
Check 'legacy: older install found through its uninstall key and upgraded in place' { (Get-ItemProperty $arpKey).InstallLocation -eq $legacyDir -and (Get-Item (Join-Path $legacyDir 'DemoApp.exe')).Length -gt 1KB }
Check 'legacy: old uninstall entry removed' { -not (Test-Path $legacyKey) }
Check 'cleanup: old Run value and shortcut removed' { $null -eq (Get-ItemProperty $runKey).'FluentSetupE2E-Old' -and -not (Test-Path $oldLink) }
Check 'required option cannot be switched off; fallback arguments ran after the failure' { Test-Path $legacyMarker }
Check 'fallbackRunValue registered after the action failed' { (Get-ItemProperty $runKey).'FluentSetupE2E-Auto' -like '*DemoApp.exe*--autostart' }
$quiet = (Get-ItemProperty $arpKey).QuietUninstallString
$code = -1
if ($quiet -match '^"([^"]+)"\s*(.*)$') { $code = (Start-Process $Matches[1] -ArgumentList $Matches[2] -Wait -PassThru).ExitCode }
Check 'legacy: uninstall exit 0, Run value cleaned up' { $code -eq 0 -and $null -eq (Get-ItemProperty $runKey).'FluentSetupE2E-Auto' }
Remove-ItemProperty $runKey -Name 'FluentSetupE2E-Old', 'FluentSetupE2E-Auto' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $legacyDir, $legacyKey, $oldLink -ErrorAction SilentlyContinue

Write-Host '==> Variants (shared vs. self-contained builds), option startup entry, close command' -ForegroundColor Cyan
Remove-Existing
$vbin = Join-Path $work 'bin-variants'
& $msbuild samples\DemoApp\DemoApp.vcxproj /p:Configuration=Release /p:Platform=x64 "/p:DemoVersion=8.0.0" "/p:DemoOut=$vbin\" /nologo /v:q | Out-Null
$va = Join-Path $work 'variant-a'; $vb = Join-Path $work 'variant-b'
New-Item -ItemType Directory -Force $va, $vb | Out-Null
foreach ($v in $va, $vb) { Copy-Item (Join-Path $vbin 'DemoApp.exe') $v; 'same in both' | Set-Content (Join-Path $v 'common.txt') }
'runtime A' | Set-Content (Join-Path $va 'only-a.txt'); 'runtime B' | Set-Content (Join-Path $vb 'only-b.dll')
'built for A' | Set-Content (Join-Path $va 'differs.txt'); 'built for B (longer)' | Set-Content (Join-Path $vb 'differs.txt')
$vproject = Get-Content samples\DemoApp\fluentsetup.json -Raw | ConvertFrom-Json
$vproject.app.id = $appId; $vproject.app.name = $appName; $vproject.app.version = '8.0.0'
$vproject.files.source = ''
$vproject.files | Add-Member -Force -NotePropertyName variants -NotePropertyValue @(
    @{ id = 'a'; label = @{ 'en-US' = 'Build A' }; default = $true; source = 'variant-a' },
    @{ id = 'b'; label = @{ 'en-US' = 'Build B' }; source = 'variant-b' })
$vproject.ui.license = '..\..\samples\DemoApp\LICENSE.txt'
$vproject.output.directory = 'dist'
$vproject.install | Add-Member -Force -NotePropertyName closeCommand -NotePropertyValue @{ run = 'DemoApp.exe'; arguments = @('--version') }
$vproject.install | Add-Member -Force -NotePropertyName closeProcesses -NotePropertyValue @('DemoApp.exe')  # whatever ignores the command
$vproject | Add-Member -Force -NotePropertyName requirements -NotePropertyValue @{ dotNet = @(@{ runtime = 'core'; version = '99.0.0'; variants = @('b') }) }
$vproject | Add-Member -Force -NotePropertyName options -NotePropertyValue @(@{ id = 'startup'; label = @{ 'en-US' = 'Start with Windows' }; default = $true; startup = @{ name = 'FluentSetupE2E-Startup'; arguments = @('--background') } })
$vpath = Join-Path $work 'project-variants.json'
[IO.File]::WriteAllText($vpath, ($vproject | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
& $cli build $vpath | Out-Null
$vsetup = Join-Path $work "dist\$appName-8.0.0-Setup.exe"
$header = & $cli inspect $vsetup --json | ConvertFrom-Json
Check 'variants: package built' { Test-Path $vsetup }
$code = Run-Setup $vsetup @('/quiet', '/variant', 'b') 'variant-b'
Check 'variant b: its prerequisite (missing .NET 99) stops setup (exit 17)' { $code -eq 17 -and -not (Test-Path $arpKey) }
$code = Run-Setup $vsetup @('/quiet') 'variant-default'
Check 'default variant a: installed with shared + a files only' {
    $code -eq 0 -and (Test-Path "$installDir\common.txt") -and (Test-Path "$installDir\only-a.txt") -and -not (Test-Path "$installDir\only-b.dll") -and
    (Get-Content "$installDir\differs.txt") -eq 'built for A' -and (Get-ItemProperty $arpKey).FluentSetupVariant -eq 'a' }
Check 'option startup entry registered for the main executable' { (Get-ItemProperty $runKey).'FluentSetupE2E-Startup' -like "*$appName\DemoApp.exe*--background" }
$running = Start-Process (Join-Path $installDir 'DemoApp.exe') -PassThru  # running app: the update must close it (close command, then closeProcesses)
for ($i = 0; $i -lt 50 -and -not $running.HasExited -and $running.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 200; $running.Refresh() }
$vproject.requirements.dotNet[0].variants = @('a')    # now b has no missing prerequisite
[IO.File]::WriteAllText($vpath, ($vproject | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
& $cli build $vpath | Out-Null
$code = Run-Setup $vsetup @('/quiet', '/variant', 'b') 'variant-switch'
$switchLog = Get-Content (Join-Path $logs 'variant-switch.log') -Raw
Check 'switch to variant b: a-only files removed, b files installed, shared kept' {
    $code -eq 0 -and -not (Test-Path "$installDir\only-a.txt") -and (Test-Path "$installDir\only-b.dll") -and (Test-Path "$installDir\common.txt") -and
    (Get-Content "$installDir\differs.txt") -eq 'built for B (longer)' -and (Get-ItemProperty $arpKey).FluentSetupVariant -eq 'b' }
Check 'close command ran against the running app before files were replaced' { $switchLog -match 'Asking the app to exit' -and -not (Get-Process DemoApp -ErrorAction SilentlyContinue) }
Remove-Existing
Check 'uninstall removed the startup entry' { $null -eq (Get-ItemProperty $runKey).'FluentSetupE2E-Startup' }
Get-Process DemoApp -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '==> Inno/NSIS command lines, upgrade keeps earlier choices, user data, elevated cleanup' -ForegroundColor Cyan
Remove-Existing
$dataDir = Join-Path $env:LOCALAPPDATA 'FluentSetupE2E-Data'
$innoDir = Join-Path $env:LOCALAPPDATA 'Programs\FluentSetup E2E Inno Dir'
$nsisDir = Join-Path $env:LOCALAPPDATA 'Programs\FluentSetup E2E NSIS Dir'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$compat = New-Package '10.0.0' { param($s) } @{ relaunchArguments = @('--version') } @{
    options = @(@{ id = 'autostart'; label = @{ 'en-US' = 'Start with Windows' }; default = $true; startup = @{ name = 'FluentSetupE2E-Auto2'; arguments = @() } })
    uninstall = @{ userData = @{ paths = @('{LocalAppData}\FluentSetupE2E-Data') }; elevated = @{ ifExists = @('{LocalAppData}\FluentSetupE2E-Data\marker'); traceSessions = @('FluentSetupE2E-NoSuchSession') } }
}
# Inno: what EncoDeck's in-app updater passes, plus /DIR= /TASKS= /LOG=
$innoLog = Join-Path $logs 'inno.log'
$p = Start-Process $compat -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS', '/SP-', "/DIR=`"$innoDir`"", '/TASKS="desktopicon"', '/RELAUNCH=1', "/LOG=`"$innoLog`"" -Wait -PassThru
Start-Sleep -Seconds 2
Check 'Inno switches: installed into /DIR, exit 0' { $p.ExitCode -eq 0 -and (Test-Path "$innoDir\DemoApp.exe") }
Check 'Inno /TASKS: desktop icon on, autostart (not listed) off' { (Test-Path $desktopLink) -and $null -eq (Get-ItemProperty $runKey).'FluentSetupE2E-Auto2' }
Check 'Inno /RELAUNCH=1: app started again with relaunchArguments' { Test-Path "$innoDir\version-check.txt" }
# Upgrade: a plain quiet run keeps the earlier choices (desktop icon on, autostart off) and the folder
$code = Run-Setup $compat @('/quiet') 'prefill'
Check 'update keeps earlier choices (desktop icon, autostart off, folder)' { $code -eq 0 -and (Test-Path $desktopLink) -and $null -eq (Get-ItemProperty $runKey).'FluentSetupE2E-Auto2' -and (Get-ItemProperty $arpKey).InstallLocation -eq $innoDir }
# Uninstall keeps user data by default; elevated cleanup is skipped in silent mode
New-Item -ItemType Directory -Force $dataDir | Out-Null; 'x' | Set-Content "$dataDir\marker"
$quiet = (Get-ItemProperty $arpKey).QuietUninstallString
$code = -1
if ($quiet -match '^"([^"]+)"\s*(.*)$') { $code = (Start-Process $Matches[1] -ArgumentList "$($Matches[2]) /log `"$(Join-Path $logs 'uninst-keep.log')`"" -Wait -PassThru).ExitCode }
Check 'silent uninstall keeps user data' { $code -eq 0 -and (Test-Path $dataDir) }
$uninstKeepLog = Get-ChildItem $logs -Filter 'uninst-keep*' | Get-Content -Raw
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Already elevated (e.g. a CI runner): the uninstall undoes the elevated changes itself, no prompt involved.
    Check 'elevated cleanup runs directly when uninstall is already elevated' { $uninstKeepLog -match 'Undoing changes the app made with administrator rights' }
} else {
    Check 'elevated cleanup skipped in silent mode (needs UAC)' { $uninstKeepLog -match 'Skipping the administrator cleanup' }
}
# NSIS: /S /D=<path with spaces> (last, unquoted); then uninstall with /remove-user-data
$p = Start-Process $compat -ArgumentList "/S /D=$nsisDir" -Wait -PassThru
Check 'NSIS /S /D=path with spaces' { $p.ExitCode -eq 0 -and (Test-Path "$nsisDir\DemoApp.exe") }
$quiet = (Get-ItemProperty $arpKey).QuietUninstallString
$code = -1
if ($quiet -match '^"([^"]+)"\s*(.*)$') { $code = (Start-Process $Matches[1] -ArgumentList "$($Matches[2]) /remove-user-data" -Wait -PassThru).ExitCode }
Check 'uninstall /remove-user-data deletes the user data' { $code -eq 0 -and -not (Test-Path $dataDir) }
Remove-Item -Recurse -Force $innoDir, $nsisDir, $dataDir -ErrorAction SilentlyContinue

Write-Host '==> Replacing an Inno Setup install: passive /SILENT, leftovers, Start menu folder, detected options, /UPDATE' -ForegroundColor Cyan
Remove-Existing
$isKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F5E2E000-0000-4000-8000-00000000E2E0}_is1'
$isDir = Join-Path $env:LOCALAPPDATA 'Programs\FluentSetup E2E Inno App'
$programs = [Environment]::GetFolderPath('Programs')
$group = Join-Path $programs 'FluentSetup E2E Group'
$vendorKey = 'HKCU:\Software\FluentSetupE2E'
Remove-Item -Recurse -Force $isDir, $group -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $isDir, $group, $vendorKey | Out-Null
'old build' | Set-Content "$isDir\DemoApp.exe"
'inno uninstaller' | Set-Content "$isDir\unins000.exe"
'inno data' | Set-Content "$isDir\unins000.dat"
'user file' | Set-Content "$isDir\keep.txt"
'' | Set-Content "$group\Uninstall FluentSetup E2E.lnk"
New-Item -Force $isKey | Out-Null
Set-ItemProperty $isKey -Name DisplayVersion -Value '0.5.0'
Set-ItemProperty $isKey -Name InstallLocation -Value "$isDir\"
Set-ItemProperty $isKey -Name 'Inno Setup: Selected Tasks' -Value ''
Set-ItemProperty $runKey -Name 'FluentSetupE2E-Managed' -Value 'set by the app itself'   # the app turned autostart on
Set-ItemProperty $vendorKey -Name InstallDir -Value $isDir
$migrate = New-Package '11.0.0' { param($s) } @{
    legacyUninstallKeys = @('{F5E2E000-0000-4000-8000-00000000E2E0}_is1')
    startMenuFolder = 'FluentSetup E2E Group'
    relaunchArguments = @('--version')
    cleanup = @{ removeFiles = @('unins*.exe', 'unins???.dat'); removeShortcuts = @('FluentSetup E2E Group\Uninstall FluentSetup E2E.lnk') }
} @{
    options = @(
        @{ id = 'autostart'; label = @{ 'en-US' = 'Start with Windows' }; default = $false; startup = @{ name = 'FluentSetupE2E-Managed'; arguments = @('--background') } },
        @{ id = 'enable'; label = @{ 'en-US' = 'Enable now' }; default = $true; quietDefault = $false; detect = @{ runValue = 'FluentSetupE2E-NotThere' } })
    actions = @(@{ when = 'afterInstall'; run = 'DemoApp.exe'; arguments = @('--version'); ifOption = 'enable'; onFailure = 'fail' })
    uninstall = @{ removeData = @('{InstallDir}\logs'); removeRegistryKeys = @('HKCU\Software\FluentSetupE2E') }
}
# What an Inno-based in-app updater runs: /SILENT is passive here (progress window, closes itself after success).
$log = Join-Path $logs 'passive.log'
$p = Start-Process $migrate -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -PassThru
if (-not $p.WaitForExit(180000)) { $p.Kill(); Write-Host '  passive setup did not close itself' -ForegroundColor Red }
$passiveLog = Get-Content $log -Raw
Check 'passive /SILENT: installed and closed itself, exit 0' { $p.HasExited -and $p.ExitCode -eq 0 -and $passiveLog -match 'Front end: (WinUI|Win32)|falling back to Win32' }
Check 'Inno install adopted in place, its uninstall key removed' { (Get-ItemProperty $arpKey).InstallLocation -eq $isDir -and -not (Test-Path $isKey) }
Check 'removeFiles: unins000.exe/.dat removed, other files kept' { -not (Test-Path "$isDir\unins000.exe") -and -not (Test-Path "$isDir\unins000.dat") -and (Test-Path "$isDir\keep.txt") }
Check 'startMenuFolder: shortcut in the group, old group entry removed' { (Test-Path "$group\$appName.lnk") -and -not (Test-Path "$group\Uninstall FluentSetup E2E.lnk") -and -not (Test-Path (Join-Path $programs "$appName.lnk")) }
Check 'startup option detected on (app-managed Run value), rewritten quoted' { (Get-ItemProperty $runKey).'FluentSetupE2E-Managed' -eq "`"$isDir\DemoApp.exe`" --background" }
Check 'detect: option off when its Run value is missing, action skipped' { -not (Test-Path "$isDir\version-check.txt") -and $passiveLog -match 'Option enable detected as off' }
New-Item -ItemType Directory -Force "$isDir\logs" | Out-Null; 'log' | Set-Content "$isDir\logs\app.log"
Remove-Existing
Check 'uninstall: {InstallDir}\logs, registry key, Start menu group and Run value removed' {
    -not (Test-Path "$isDir\logs") -and -not (Test-Path $vendorKey) -and -not (Test-Path $group) -and $null -eq (Get-ItemProperty $runKey).'FluentSetupE2E-Managed' }
# A scripted first install does not do what "Enable now" offers (quietDefault false); NSIS-style /S /UPDATE relaunches.
$code = Run-Setup $migrate @('/quiet', '/dir', "`"$isDir`"") 'quiet-first'
Check 'quietDefault: unattended first install skips the "Enable now" action' { $code -eq 0 -and -not (Test-Path "$isDir\version-check.txt") }
$p = Start-Process $migrate -ArgumentList '/S', '/UPDATE' -Wait -PassThru
Start-Sleep -Seconds 2
Check 'NSIS-style /S /UPDATE starts the app again with relaunchArguments' { $p.ExitCode -eq 0 -and (Test-Path "$isDir\version-check.txt") }
Remove-Existing
Remove-ItemProperty $runKey -Name 'FluentSetupE2E-Managed' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $isDir, $group, $isKey, $vendorKey -ErrorAction SilentlyContinue

Write-Host '==> A language the installer does not ship, supplied by the package' -ForegroundColor Cyan
Remove-Existing
New-Item -ItemType Directory -Force (Join-Path $work 'locales') | Out-Null
[IO.File]::WriteAllText((Join-Path $work 'locales\de-DE.json'), '{ "strings": { "button.install": "Installieren", "state.Validating": "Wird geprüft" } }', [Text.UTF8Encoding]::new($false))
$german = New-Package '9.0.0' { param($s) } @{} @{ ui = @{ license = '..\..\samples\DemoApp\LICENSE.txt'; localization = 'locales' } }
$code = Run-Setup $german @('/quiet', '/lang', 'de-DE') 'lang-de'
Check 'package-supplied de-DE strings are used (/lang de-DE)' { $code -eq 0 -and (Get-Content (Join-Path $logs 'lang-de.log') -Raw) -match 'UI language de-DE' }
Remove-Existing
$code = Run-Setup $german @('/quiet', '/lang', 'de-AT') 'lang-de-at'
Check 'de-AT falls back to the package''s de-DE' { $code -eq 0 -and (Get-Content (Join-Path $logs 'lang-de-at.log') -Raw) -match 'UI language de-DE' }
Remove-Existing
$code = Run-Setup $german @('/quiet', '/lang', 'fr-FR') 'lang-fr'
Check 'a language nobody supplies falls back to the Windows display language' { $code -eq 0 -and (Get-Content (Join-Path $logs 'lang-fr.log') -Raw) -match 'UI language (en-US|zh-CN|ja-JP|ko-KR) ' }
Remove-Existing

Write-Host '==> Prerequisites: .NET version checks, missing runtime, verified download' -ForegroundColor Cyan
Remove-Existing
$netcore = Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.NETCore.App" -Directory -ErrorAction SilentlyContinue |
    Where-Object Name -match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } | Select-Object -Last 1
if ($netcore) {
    $present = New-Package '7.0.0' { param($s) } @{} @{ requirements = @{ dotNet = @(@{ runtime = 'core'; version = "$(([version]$netcore.Name).Major).0.0" }) } }
    $code = Run-Setup $present @('/quiet') 'prereq-present'
    Check ".NET $($netcore.Name) satisfies the requirement" { $code -eq 0 }
    Remove-Existing
}
$missing = New-Package '7.0.1' { param($s) } @{} @{ requirements = @{ dotNet = @(@{ runtime = 'core'; version = '99.0.0'; helpUrl = 'https://dotnet.microsoft.com/download' }) } }
$code = Run-Setup $missing @('/quiet') 'prereq-missing'
Check 'missing .NET 99 without a source stops setup (exit 17), nothing installed' { $code -eq 17 -and -not (Test-Path $arpKey) }
# WebView2 older than the package's minimum: the bundled "updater" (here DemoApp, which changes nothing) runs, the
# runtime is still too old -> onFailure fail rolls everything back; onFailure warn installs with a warning.
$wv = @{ required = $true; minVersion = '999.0.0.0'; installer = 'DemoApp.exe'; arguments = @('--version') }
$strict = New-Package '7.0.4' { param($s) } @{} @{ requirements = @{ webView2 = $wv + @{ onFailure = 'fail' } } }
$code = Run-Setup $strict @('/quiet') 'webview2-strict'
Check 'WebView2 below minVersion, onFailure fail: exit 17 and rolled back' { $code -eq 17 -and -not (Test-Path $arpKey) -and -not (Test-Path (Join-Path $installDir 'DemoApp.exe')) }
$lenient = New-Package '7.0.5' { param($s) } @{} @{ requirements = @{ webView2 = $wv + @{ onFailure = 'warn' } } }
$code = Run-Setup $lenient @('/quiet') 'webview2-warn'
Check 'WebView2 below minVersion, onFailure warn: installed, reported in the log' { $code -eq 0 -and (Test-Path $arpKey) -and (Get-Content (Join-Path $logs 'webview2-warn.log') -Raw) -match 'WebView2' }
Remove-Existing
# A real https download, verified by SHA-256 (the NuGet CLI the build script uses; it exits 0 for "help").
$nuget = Join-Path $root 'out\tools\nuget.exe'
if (Test-Path $nuget) {
    $download = @{ url = 'https://dist.nuget.org/win-x86-commandline/v6.14.0/nuget.exe'; sha256 = (Get-FileHash $nuget).Hash; size = (Get-Item $nuget).Length }
    $bad = New-Package '7.0.2' { param($s) } @{} @{ requirements = @{ dotNet = @(@{ runtime = 'core'; version = '99.0.0'; arguments = @('help'); download = @{ url = $download.url; sha256 = ('0' * 64); size = 0 } }) } }
    $code = Run-Setup $bad @('/quiet') 'prereq-badhash'
    $log = Get-Content (Join-Path $logs 'prereq-badhash.log') -Raw
    if ($log -match 'Cannot connect|Download failed|Download interrupted|server answered HTTP') {
        Write-Host '  [SKIP] download checks (no internet access)' -ForegroundColor Yellow
    } else {
        Check 'download with the wrong SHA-256 is refused before anything is installed' { $code -eq 17 -and $log -match 'SHA-256' -and -not (Test-Path $arpKey) }
        $good = New-Package '7.0.3' { param($s) } @{} @{ requirements = @{ dotNet = @(@{ runtime = 'core'; version = '99.0.0'; arguments = @('help'); download = $download }) } }
        $code = Run-Setup $good @('/quiet') 'prereq-download'
        $log = Get-Content (Join-Path $logs 'prereq-download.log') -Raw
        Check 'verified download runs; still-missing runtime becomes a warning' { $code -eq 0 -and $log -match 'Installing prerequisite' -and (Test-Path $arpKey) }
    }
}

Write-Host '==> Built-in updates: /check-update, /install-update (verified download, refused when it is not this app)' -ForegroundColor Cyan
Remove-Existing
$uninstaller = Join-Path $installDir 'uninstall.exe'
function Run-Update([string[]] $arguments, [string] $logName) {
    $log = Join-Path $logs "$logName.log"
    $out = Join-Path $logs "$logName.out"
    $p = Start-Process -FilePath $uninstaller -ArgumentList (@($arguments) + @('/log', "`"$log`"")) -RedirectStandardOutput $out -Wait -PassThru
    return [pscustomobject]@{ Code = $p.ExitCode; Log = "$(Get-Content $log -Raw -ErrorAction SilentlyContinue)"; Out = "$(Get-Content $out -Raw -ErrorAction SilentlyContinue)" }
}
$offline = 'Cannot connect|Download failed|Download interrupted|server answered HTTP|rate limit'
$plain = New-Package '12.0.0' { param($s) }
$code = Run-Setup $plain @('/quiet') 'update-none-install'
$r = Run-Update @('/check-update') 'update-none'
Check 'no update source in the package: /check-update exits 3' { $code -eq 0 -and $r.Code -eq 3 }
Remove-Existing

# Feeds served from this repository (tests/fixtures): the download is the LICENSE file, not a setup package.
$fixtures = 'https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/tests/fixtures'
$feedPkg = New-Package '12.0.1' { param($s) } @{} @{ update = @{ feed = "$fixtures/update-feed.json" } }
$code = Run-Setup $feedPkg @('/quiet') 'update-feed-setup'
$r = Run-Update @('/check-update') 'update-feed-check'
if ($r.Code -eq 19 -and $r.Log -match $offline) {
    Write-Host '  [SKIP] update feed checks (no internet access, or the fixtures are not on GitHub yet)' -ForegroundColor Yellow
} else {
    $json = $r.Out | ConvertFrom-Json
    Check 'feed: /check-update exits 18 and prints the versions as JSON' { $r.Code -eq 18 -and $json.updateAvailable -and $json.latestVersion -eq '99.0.0' -and $json.installedVersion -eq '12.0.1' }
    $r = Run-Update @('/install-update', '/quiet') 'update-feed-install'
    Check 'feed: a download that is not a setup package is refused (exit 19), the app is unchanged' {
        $r.Code -eq 19 -and $r.Log -match 'not a valid setup package' -and (Get-ItemProperty $arpKey).DisplayVersion -eq '12.0.1' }
    Check 'feed: the refused download was removed' { -not (Get-ChildItem $env:TEMP -Directory -Filter 'FluentSetup-upd-*' | Get-ChildItem -File) }
    Remove-Existing
    $badPkg = New-Package '12.0.2' { param($s) } @{} @{ update = @{ feed = "$fixtures/update-feed-badhash.json" } }
    $code = Run-Setup $badPkg @('/quiet') 'update-badhash-setup'
    $r = Run-Update @('/install-update', '/quiet') 'update-badhash'
    Check 'feed: a download with another SHA-256 is refused (exit 19)' { $r.Code -eq 19 -and $r.Log -match 'SHA-256' }
    Remove-Existing
    $newer = New-Package '100.0.0' { param($s) } @{} @{ update = @{ feed = "$fixtures/update-feed.json" } }
    $code = Run-Setup $newer @('/quiet') 'update-newer-setup'
    $r = Run-Update @('/check-update') 'update-newer-check'
    Check 'feed: installed version newer than the feed: /check-update exits 0' { $r.Code -eq 0 -and -not ($r.Out | ConvertFrom-Json).updateAvailable }
    $r = Run-Update @('/install-update', '/quiet') 'update-newer-install'
    Check 'feed: /install-update with nothing newer exits 0 without downloading' { $r.Code -eq 0 -and $r.Log -notmatch 'Downloading' }
}
Remove-Existing

# GitHub releases of FluentSetup itself: found and checked, then refused because it is another app.
$ghPkg = New-Package '0.5.0' { param($s) } @{} @{ update = @{ github = 'TIANLI0/FluentSetup'; asset = 'FluentSetup-{Version}-Setup.exe'; checksums = 'SHA256SUMS.txt' } }
$code = Run-Setup $ghPkg @('/quiet') 'update-github-setup'
$r = Run-Update @('/check-update') 'update-github-check'
if ($r.Code -eq 19 -and $r.Log -match $offline) {
    Write-Host '  [SKIP] GitHub update checks (no internet access or API rate limit)' -ForegroundColor Yellow
} else {
    Check 'GitHub: /check-update finds the latest FluentSetup release (exit 18)' { $r.Code -eq 18 -and ($r.Out | ConvertFrom-Json).latestVersion -match '^\d+\.\d+\.\d+' }
    if ($env:CI) {
        # Downloads the whole FluentSetup Setup.exe, so only on CI.
        $r = Run-Update @('/install-update', '/quiet') 'update-github-install'
        Check 'GitHub: the release passes SHA256SUMS.txt and the asset digest, then is refused as another app (exit 19)' {
            $r.Code -eq 19 -and $r.Log -match 'another app: FluentSetup' }
    }
}
Remove-Existing
Check 'environment restored (no ARP entry)' { -not (Test-Path $arpKey) }

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host ("{0} checks, {1} passed, {2} failed" -f $results.Count, ($results.Count - $failed.Count), $failed.Count) -ForegroundColor ($(if ($failed.Count) { 'Red' } else { 'Green' }))
$results | ConvertTo-Json | Set-Content (Join-Path $work 'e2e-results.json')
if (-not $KeepArtifacts -and $failed.Count -eq 0) { Get-ChildItem $work -Directory | Where-Object Name -notin 'logs' | Remove-Item -Recurse -Force }
exit $failed.Count
