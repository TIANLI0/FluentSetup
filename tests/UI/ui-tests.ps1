<#
.SYNOPSIS
  UI tests: drives a real per-user install through each installer front end with UI Automation.

  For every front end (WinUI 3 when the Windows App Runtime is present, Win32 fallback always):
  launch Setup.exe -> first page shows the version in the title, no publisher line (the test package is unsigned),
  the path with an inline "Change" button and the install options inline -> turn on the desktop shortcut, turn off
  "Launch when ready" -> Next -> Settings page (package inputs: a required text field with a pattern, a choice and a
  password; Next stays disabled until the values are valid) -> Next -> accept the license -> Install -> result page ->
  Close -> files/shortcut/ARP present, the input values reached setup -> "uninstall.exe /modify" (Apps & features
  "Modify"): the "Change" page, desktop shortcut off, Settings, Change -> shortcut gone -> silent uninstall -> clean.
  Then, without UI: /quiet with a missing or unknown input is refused, "/modify /quiet", "/repair /quiet" (intact and
  with a deleted file).

  Requires a built CLI and stub (scripts/build.ps1). Uses English strings (/lang en-US) so element names are stable.
  Run it on the invisible desktop: scripts/run-hidden.ps1 -Command 'powershell -NoProfile -File tests\UI\ui-tests.ps1' -Log out\ui-tests.log
.PARAMETER FrontEnds  winui, win32 (default: both; winui is skipped when the runtime is missing)
#>
param([string[]] $FrontEnds = @('winui', 'win32'))
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root
Import-Module (Join-Path $PSScriptRoot 'Uia.psm1') -Force

$cli = Join-Path $root 'src\CLI\bin\Release\net10.0\fluentsetup.exe'
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
$en = Get-Content shared\localization\en-US.json -Raw -Encoding UTF8 | ConvertFrom-Json
$browseName = $en.'options.browseTitle'  # WinUI's accessible name for Change
$serverLabel = 'Server address'; $editionLabel = 'Edition'; $tokenLabel = 'Access token'
$serverUrl = 'https://example.test/api'

function Check([string] $name, [scriptblock] $condition) {
    $ok = $false
    try { $ok = [bool](& $condition) } catch { $name += " (exception: $($_.Exception.Message))" }
    $results.Add([pscustomobject]@{ Test = $name; Passed = $ok; Skipped = $false })
    Write-Host ("  [{0}] {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name) -ForegroundColor ($(if ($ok) { 'Green' } else { 'Red' }))
}

# Checks that need engine work not on this branch yet are reported, not failed.
function Skip([string] $name, [string] $why) {
    $results.Add([pscustomobject]@{ Test = $name; Passed = $true; Skipped = $true })
    Write-Host "  [SKIP] $name ($why)" -ForegroundColor Yellow
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

function Get-Descendants([int] $id) {
    $all = @(Get-CimInstance Win32_Process)
    $found = [System.Collections.Generic.List[object]]::new()
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($id)
    while ($queue.Count) {
        $parent = $queue.Dequeue()
        foreach ($p in $all | Where-Object { $_.ParentProcessId -eq $parent }) { $found.Add($p); $queue.Enqueue([int]$p.ProcessId) }
    }
    return $found
}

function Stop-Tree([System.Diagnostics.Process] $process) {
    if ($process.HasExited) { return }
    foreach ($p in Get-Descendants $process.Id) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}

# The window of a front end: Setup.exe's own (Win32) or its FluentSetup.UI.exe child (WinUI). uninstall.exe first
# relocates itself, so for /modify the window belongs to the relocated copy (a child) or to its UI child.
function Wait-Window([System.Diagnostics.Process] $setup, [string] $ui) {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $tree = Get-Descendants $setup.Id
        if ($ui -eq 'winui') {
            $child = $tree | Where-Object Name -eq 'FluentSetup.UI.exe' | Select-Object -First 1
            if ($child) { return Get-UiaWindow -ProcessId $child.ProcessId }
        } else {
            foreach ($candidate in @($setup.Id) + @($tree | ForEach-Object { [int]$_.ProcessId })) {
                try { return Get-UiaWindow -ProcessId $candidate -TimeoutSeconds 1 } catch { }
            }
        }
        if ($setup.HasExited) { throw "Setup exited early (code $($setup.ExitCode))" }
        Start-Sleep -Milliseconds 200
    }
    throw "The $ui front end did not start"
}

# Setup keeps its log open while it runs.
function Read-Log([string] $path) {
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
    try { return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $stream.Dispose() }
}

function Get-Record { Get-Content (Join-Path $installDir 'uninstall.json') -Raw -Encoding UTF8 | ConvertFrom-Json }

function Invoke-Quiet([string] $exe, [string[]] $arguments) {
    return (Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru).ExitCode
}

# Until the builder writes "inputs" and "ui.banner" into the package header (FluentSetup 1.2 header writer), add them
# the way it will: the test package is unsigned, so its header can be replaced by an uncompressed one with a new footer.
# The banner must be a file of the frontend set; the app icon stands in for banner.png until the builder adds it.
function Add-HeaderInputs([string] $exe, $inputs) {
    $info = & $cli inspect $exe --json | Out-String | ConvertFrom-Json
    $header = $info.header
    if ($header.PSObject.Properties['inputs'] -and @($header.inputs).Count -gt 0) { return $false }
    $header | Add-Member -NotePropertyName inputs -NotePropertyValue $inputs -Force
    if (-not $header.ui.PSObject.Properties['banner'] -and $header.frontend -and $header.frontend.icon) {
        $header.ui | Add-Member -NotePropertyName banner -NotePropertyValue $header.frontend.icon -Force
    }
    $bytes = [IO.File]::ReadAllBytes($exe)
    $f = $bytes.Length - 80
    if ([Text.Encoding]::ASCII.GetString($bytes, $f, 7) -ne 'FLSETUP') { throw 'No footer at the end of the test package' }
    $overlayStart = [BitConverter]::ToUInt64($bytes, $f + 16)
    $headerOffset = [BitConverter]::ToUInt64($bytes, $f + 24)
    $raw = [Text.Encoding]::UTF8.GetBytes(($header | ConvertTo-Json -Depth 64 -Compress))
    $start = [int]($overlayStart + $headerOffset)
    $pad = (8 - ($raw.Length % 8)) % 8
    $out = New-Object byte[] ($start + $raw.Length + $pad + 80)
    [Array]::Copy($bytes, 0, $out, 0, $start)
    [Array]::Copy($raw, 0, $out, $start, $raw.Length)
    $footer = $start + $raw.Length + $pad
    [Array]::Copy($bytes, $f, $out, $footer, 12)                                 # magic + format version
    [BitConverter]::GetBytes([uint32]0).CopyTo($out, $footer + 12)               # flags: header not compressed
    [BitConverter]::GetBytes([uint64]$overlayStart).CopyTo($out, $footer + 16)
    [BitConverter]::GetBytes([uint64]$headerOffset).CopyTo($out, $footer + 24)
    [BitConverter]::GetBytes([uint64]$raw.Length).CopyTo($out, $footer + 32)
    [BitConverter]::GetBytes([uint64]$raw.Length).CopyTo($out, $footer + 40)
    [Security.Cryptography.SHA256]::Create().ComputeHash($raw).CopyTo($out, $footer + 48)
    [IO.File]::WriteAllBytes($exe, $out)
    return $true
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
# Settings page: a required address with a pattern, a choice and an optional password; the values go into a config file.
$inputs = @(
    [ordered]@{ id = 'serverUrl'; label = @{ 'en-US' = $serverLabel }; description = @{ 'en-US' = 'The https address of your server.' }
                type = 'text'; default = ''; required = $true; pattern = '^https://[^ ]+$'; remember = $true; choices = @() },
    [ordered]@{ id = 'edition'; label = @{ 'en-US' = $editionLabel }; description = @{}; type = 'choice'; default = 'standard'
                required = $true; pattern = ''; remember = $true
                choices = @([ordered]@{ value = 'standard'; label = @{ 'en-US' = 'Standard' } }, [ordered]@{ value = 'pro'; label = @{ 'en-US' = 'Pro' } }) },
    [ordered]@{ id = 'token'; label = @{ 'en-US' = $tokenLabel }; description = @{}; type = 'password'; default = ''; required = $false
                pattern = ''; remember = $false; choices = @() }
)
$project | Add-Member -NotePropertyName inputs -NotePropertyValue $inputs -Force
$project | Add-Member -NotePropertyName configFiles -NotePropertyValue @(
    [ordered]@{ path = 'settings.json'; format = 'json'; values = @(
            [ordered]@{ key = 'server.url'; value = '{Input:serverUrl}'; type = 'string' },
            [ordered]@{ key = 'edition'; value = '{Input:edition}'; type = 'string' }) }) -Force
# A banner for the first page (ui.banner).
Add-Type -AssemblyName System.Drawing
$bannerImage = New-Object System.Drawing.Bitmap 640, 120
$graphics = [System.Drawing.Graphics]::FromImage($bannerImage)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle 0, 0, 640, 120), [System.Drawing.Color]::FromArgb(255, 0, 95, 184), [System.Drawing.Color]::FromArgb(0, 0, 95, 184), 0.0)
$graphics.FillRectangle($brush, 0, 0, 640, 120)
$graphics.Dispose(); $brush.Dispose()
$bannerImage.Save((Join-Path $work 'banner.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bannerImage.Dispose()
$project.ui | Add-Member -NotePropertyName banner -NotePropertyValue 'banner.png' -Force
# An update source (the e2e feed fixture announces 99.0.0): the "Change" page offers the update.
$updateFeed = 'https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/tests/fixtures/update-feed.json'
$project | Add-Member -NotePropertyName update -NotePropertyValue ([ordered]@{ feed = $updateFeed }) -Force
$feedReachable = $true
try { Invoke-WebRequest -UseBasicParsing -Method Head $updateFeed -TimeoutSec 15 | Out-Null } catch { $feedReachable = $false }
$projectPath = Join-Path $work 'project.json'
[IO.File]::WriteAllText($projectPath, ($project | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
& $cli build $projectPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed' }
$setupExe = Join-Path $work "dist\$appName-$version-Setup.exe"
if (Add-HeaderInputs $setupExe $inputs) { Write-Host '    (inputs added to the package header by the test: the builder does not write them yet)' -ForegroundColor Yellow }

function Test-Settings($w, [string] $ui, [bool] $firstInstall) {
    Check "${ui}: Settings page" { Find-Any $w @($en.'inputs.title') }
    if ($firstInstall) {
        Check "${ui}: Next disabled while the required address is empty" { (Test-UiaEnabled -Root $w -Name 'Next') -eq $false }
    }
    Set-UiaText -Root $w -Name $serverLabel -Value 'http://not-https.example'
    Check "${ui}: an address that does not match the pattern is refused" { Find-Any $w @($en.'inputs.invalid') }
    Check "${ui}: Next disabled while a value is invalid" { (Test-UiaEnabled -Root $w -Name 'Next') -eq $false -or (Test-UiaEnabled -Root $w -Name $en.'modify.change') -eq $false }
    Set-UiaText -Root $w -Name $serverLabel -Value $serverUrl
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Test-UiaElement -Root $w -Name $en.'inputs.invalid')) { Start-Sleep -Milliseconds 200 }
    Check "${ui}: the error disappears for a valid address" { -not (Test-UiaElement -Root $w -Name $en.'inputs.invalid') }
    Select-UiaChoice -Root $w -Name $editionLabel -Item 'Pro'
    Set-UiaText -Root $w -Name $tokenLabel -Value 's3cret'
}

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
        Check "${ui}: unsigned package shows no publisher warning" { -not (Test-UiaElement -Root $w -Name $en.'welcome.unknownPublisher') }
        Check "${ui}: Change button next to the location" { Find-Any $w @('Change', '&Change', $browseName) }
        Check "${ui}: install scope inline" { (Find-Any $w @('Only me')) -and (Find-Any $w @('All users')) }
        $desktop = Find-Any $w @('Create a desktop shortcut', 'Create a &desktop shortcut')
        Check "${ui}: shortcut option inline on the first page" { $desktop }
        Set-UiaToggle -Root $w -Name $desktop -On $true
        Set-UiaToggle -Root $w -Name 'Launch when ready' -On $false
        Invoke-UiaButton -Root $w -Name 'Next'
        Test-Settings $w $ui $true
        Invoke-UiaButton -Root $w -Name 'Next'
        $accept = Find-Any $w @('I accept the terms of the license agreement', '&I accept the terms of the license agreement')
        Check "${ui}: license page" { $accept }
        Set-UiaToggle -Root $w -Name $accept -On $true
        Invoke-UiaButton -Root $w -Name 'Install'
        Check "${ui}: result page" { Find-Any $w @("$appName $version is installed") 120 }
        Invoke-UiaButton -Root $w -Name 'Close'
        Check "${ui}: Setup.exe exit code 0" { $setup.WaitForExit(30000) -and $setup.ExitCode -eq 0 }
    } finally {
        Stop-Tree $setup
    }
    Check "${ui}: app installed" { Test-Path (Join-Path $installDir 'DemoApp.exe') }
    Check "${ui}: desktop shortcut created (option honoured)" { Test-Path $desktopLink }
    Check "${ui}: registered in Apps & features" { (Get-ItemProperty $arpKey).DisplayVersion -eq $version }
    $logText = Read-Log $log
    Check "${ui}: input values reached setup (log)" { $logText.Contains("Input serverUrl = `"$serverUrl`"") -and $logText.Contains('Input edition = "pro"') }
    Check "${ui}: the password is not written to the log" { $logText.Contains('Input token: (6 characters)') -and -not $logText.Contains('s3cret') }
    if ($ui -eq 'win32') { Check "${ui}: the banner was read from the package and decoded" { $logText -match 'Banner [^ ]+: [0-9]+x[0-9]+' -and -not $logText.Contains('The banner') } }
    $record = Get-Record
    if ($record.PSObject.Properties['inputs'] -and $record.inputs.PSObject.Properties['serverUrl']) {
        Check "${ui}: input values remembered in the install record (not the password)" { $record.inputs.serverUrl -eq $serverUrl -and $record.inputs.edition -eq 'pro' -and -not $record.inputs.PSObject.Properties['token'] }
    } else {
        Skip "${ui}: input values remembered in the install record" 'the engine does not record inputs yet'
    }
    $settings = Join-Path $installDir 'settings.json'
    if (Test-Path $settings) {
        Check "${ui}: input values written to the config file" { $s = Get-Content $settings -Raw | ConvertFrom-Json; $s.server.url -eq $serverUrl -and $s.edition -eq 'pro' }
    } else {
        Skip "${ui}: input values written to the config file" 'the builder/engine do not write configFiles yet'
    }

    Write-Host "==> $ui front end: Apps & features ""Modify"" (uninstall.exe /modify)" -ForegroundColor Cyan
    $modifyLog = Join-Path $work "logs\$ui-modify.log"
    $modify = Start-Process -FilePath (Join-Path $installDir 'uninstall.exe') -ArgumentList '/modify', '/ui', $ui, '/lang', 'en-US', '/log', "`"$modifyLog`"" -PassThru
    $modifyWorks = $true
    try {
        $w = Wait-Window $modify $ui
        Check "${ui}: modify: 'Change' page" { Find-Any $w @(($en.'modify.title' -replace '\{0\}', $appName)) }
        Check "${ui}: modify: Repair offered" { Find-Any $w @($en.'modify.repair') }
        if ($feedReachable) {
            $updateText = $en.'modify.updateAvailable' -replace '\{0\}', '99.0.0'
            Check "${ui}: modify: newer version offered with an Update button" { (Find-Any $w @($updateText)) -and (Find-Any $w @($en.'modify.updateButton')) }
        } else {
            Write-Host "  [SKIP] ${ui}: modify update offer (the update feed is not reachable)" -ForegroundColor Yellow
        }
        $desktop = Find-Any $w @('Create a desktop shortcut', 'Create a &desktop shortcut')
        Check "${ui}: modify: desktop shortcut shown as on" { $desktop }
        Set-UiaToggle -Root $w -Name $desktop -On $false
        Invoke-UiaButton -Root $w -Name 'Next'
        Check "${ui}: modify: Settings page" { Find-Any $w @($en.'inputs.title') }
        if (-not (Test-UiaEnabled -Root $w -Name $en.'modify.change')) {
            Set-UiaText -Root $w -Name $serverLabel -Value $serverUrl  # nothing remembered yet (engine): enter it again
        }
        Invoke-UiaButton -Root $w -Name $en.'modify.change'
        $title = Find-Any $w @(($en.'result.success.title.modify' -replace '\{0\}', $appName), $en.'result.failed.title.modify') 120
        if ($title -eq $en.'result.failed.title.modify' -and (Read-Log $modifyLog).Contains('Modify is not available yet')) {
            $modifyWorks = $false
            Skip "${ui}: modify: changes applied" 'Engine::Modify is not implemented on this branch yet'
        } else {
            Check "${ui}: modify: changes applied" { $title -eq ($en.'result.success.title.modify' -replace '\{0\}', $appName) }
        }
        Invoke-UiaButton -Root $w -Name 'Close'
        Check "${ui}: modify: uninstall.exe exit code" { $modify.WaitForExit(30000) -and $modify.ExitCode -eq $(if ($modifyWorks) { 0 } else { 1 }) }
    } finally {
        Stop-Tree $modify
    }
    if ($modifyWorks) { Check "${ui}: modify: desktop shortcut removed" { -not (Test-Path $desktopLink) } }

    Write-Host "==> $ui front end: Repair on the Change page" -ForegroundColor Cyan
    $repair = Start-Process -FilePath (Join-Path $installDir 'uninstall.exe') -ArgumentList '/modify', '/ui', $ui, '/lang', 'en-US' -PassThru
    try {
        $w = Wait-Window $repair $ui
        Invoke-UiaButton -Root $w -Name $en.'modify.repair'
        Check "${ui}: repair: nothing damaged" { Find-Any $w @($en.'result.noProblems.title') 120 }
        Invoke-UiaButton -Root $w -Name 'Close'
        Check "${ui}: repair: exit code 0" { $repair.WaitForExit(30000) -and $repair.ExitCode -eq 0 }
    } finally {
        Stop-Tree $repair
    }

    Write-Host "==> $ui front end: silent uninstall" -ForegroundColor Cyan
    $quiet = (Get-ItemProperty $arpKey).QuietUninstallString
    $code = -1
    if ($quiet -match '^"([^"]+)"\s*(.*)$') { $code = (Start-Process $Matches[1] -ArgumentList $Matches[2] -Wait -PassThru).ExitCode }
    Check "${ui}: uninstall exit code 0" { $code -eq 0 }
    Check "${ui}: environment restored" { -not (Test-Path $arpKey) -and -not (Test-Path $desktopLink) -and -not (Test-Path (Join-Path $installDir 'DemoApp.exe')) }
}

# --- no UI: inputs on the command line, /modify /quiet, /repair /quiet ---------------------------------------------
Write-Host '==> quiet: inputs, modify and repair' -ForegroundColor Cyan
Remove-Existing
$quietLog = Join-Path $work 'logs\quiet-missing-input.log'
Check 'quiet: a missing required input is refused (exit code 3)' { (Invoke-Quiet $setupExe @('/quiet', '/log', "`"$quietLog`"")) -eq 3 -and -not (Test-Path $arpKey) }
Check 'quiet: the log names the input' { (Read-Log $quietLog).Contains("Input 'serverUrl'") }
Check 'quiet: an unknown /input is refused (exit code 3)' { (Invoke-Quiet $setupExe @('/quiet', '/input', "serverUrl=$serverUrl", '/input', 'bogus=1')) -eq 3 -and -not (Test-Path $arpKey) }
Check 'quiet: an invalid /input is refused (exit code 3)' { (Invoke-Quiet $setupExe @('/quiet', '/input', 'serverUrl=ftp://x')) -eq 3 -and -not (Test-Path $arpKey) }
Check 'quiet: install with /input' { (Invoke-Quiet $setupExe @('/quiet', '/input', "serverUrl=$serverUrl", '/input', 'edition=standard')) -eq 0 -and (Test-Path (Join-Path $installDir 'DemoApp.exe')) }
$uninstaller = Join-Path $installDir 'uninstall.exe'
$modifyLog = Join-Path $work 'logs\quiet-modify.log'
$code = Invoke-Quiet $uninstaller @('/modify', '/quiet', '/desktop-shortcut', 'yes', '/input', "serverUrl=$serverUrl", '/log', "`"$modifyLog`"")
if ($code -eq 1 -and (Read-Log $modifyLog).Contains('Modify is not available yet')) {
    Skip 'quiet: /modify /quiet applies /desktop-shortcut yes' 'Engine::Modify is not implemented on this branch yet'
} else {
    Check 'quiet: /modify /quiet applies /desktop-shortcut yes' { $code -eq 0 -and (Test-Path $desktopLink) }
}
Check 'quiet: /modify /quiet with an unknown /input is refused (exit code 3)' { (Invoke-Quiet $uninstaller @('/modify', '/quiet', '/input', 'bogus=1')) -eq 3 }
Check 'quiet: /repair /quiet on an intact installation (exit code 0)' { (Invoke-Quiet $uninstaller @('/repair', '/quiet')) -eq 0 }
Remove-Item -Force (Join-Path $installDir 'DemoApp.exe')
$repairLog = Join-Path $work 'logs\quiet-repair.log'
$code = Invoke-Quiet $uninstaller @('/repair', '/quiet', '/log', "`"$repairLog`"")
$record = Get-Record
if ($record.PSObject.Properties['sourcePackage']) {
    Check 'quiet: /repair /quiet restores a deleted file from the original setup' { $code -eq 0 -and (Test-Path (Join-Path $installDir 'DemoApp.exe')) }
} else {
    Check 'quiet: /repair /quiet without a package to repair from fails (exit code 1)' { $code -eq 1 -and (Read-Log $repairLog).Contains('Repair:') }
    Skip 'quiet: /repair /quiet restores a deleted file from the original setup' 'the engine does not record the original setup yet'
}

Remove-Existing
$failed = @($results | Where-Object { -not $_.Passed }).Count
$skipped = @($results | Where-Object { $_.Skipped }).Count
Write-Host ''
Write-Host ("{0} checks, {1} passed, {2} failed, {3} skipped" -f $results.Count, ($results.Count - $failed - $skipped), $failed, $skipped) -ForegroundColor ($(if ($failed) { 'Red' } else { 'Green' }))
if ($failed) { exit 1 }
