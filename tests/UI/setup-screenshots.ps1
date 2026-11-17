<#
.SYNOPSIS
  Screenshots of the installer's first page and result page (WinUI and Win32 front ends, light and dark, English and
  Chinese) for a package with every kind of link (website, GitHub, release notes, support) and the inline license.
  Drives the setup through UI Automation only. Needs scripts/build.ps1 and, for WinUI, the Windows App Runtime 2.x.
  Run it on a CI runner (the setup-screenshots workflow); it installs per user and uninstalls again.
#>
param([string] $Out = 'out\setup-shots', [string[]] $FrontEnds = @('winui', 'win32'), [string[]] $Themes = @('light', 'dark'),
      [string[]] $Languages = @('zh-CN', 'en-US'))
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root
Import-Module (Join-Path $PSScriptRoot 'Uia.psm1') -Force
$cli = Join-Path $root 'src\CLI\bin\Release\net10.0\fluentsetup.exe'
if (-not (Test-Path $cli)) { throw 'Build first (scripts/build.ps1).' }
$capture = Join-Path $root 'scripts\capture-window.ps1'
$Out = [IO.Path]::GetFullPath((Join-Path $root $Out))
$work = Join-Path $Out 'work'
Remove-Item -Recurse -Force $Out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $work 'stage') | Out-Null
Copy-Item "$env:WINDIR\System32\notepad.exe" (Join-Path $work 'stage\Contoso.exe')
'MIT License' | Set-Content (Join-Path $work 'LICENSE.txt')

$appId = 'Contoso.Shots'
$appName = 'Contoso Notes'
$arpKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"

function Remove-Existing {
    if (Test-Path $arpKey) {
        $quiet = (Get-ItemProperty $arpKey).QuietUninstallString
        if ($quiet -match '^"([^"]+)"\s*(.*)$') { Start-Process $Matches[1] -ArgumentList $Matches[2] -Wait }
    }
}

function Wait-Window([System.Diagnostics.Process] $setup, [string] $ui) {
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        $tree = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $setup.Id })
        if ($ui -eq 'winui') {
            $child = $tree | Where-Object Name -eq 'FluentSetup.UI.exe' | Select-Object -First 1
            if ($child) { return @{ Window = (Get-UiaWindow -ProcessId $child.ProcessId); Pid = [int]$child.ProcessId } }
        } else {
            try { return @{ Window = (Get-UiaWindow -ProcessId $setup.Id -TimeoutSeconds 1); Pid = $setup.Id } } catch { }
        }
        if ($setup.HasExited) { throw "Setup exited early (code $($setup.ExitCode))" }
        Start-Sleep -Milliseconds 250
    }
    throw "The $ui front end did not start"
}

function Wait-Name($window, [string[]] $names, [int] $timeout = 120) {
    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        foreach ($n in $names) { if (Test-UiaElement -Root $window -Name $n) { return $n } }
        Start-Sleep -Milliseconds 300
    }
    throw "None of '$($names -join "', '")' appeared"
}

foreach ($theme in $Themes) {
    $project = [ordered]@{
        schemaVersion = 1
        app = [ordered]@{
            id = $appId; name = $appName; publisher = 'Contoso'; version = '2.4.1'; description = 'Notes with sync'
            website = 'https://example.com/notes'; mainExecutable = 'Contoso.exe'
            supportUrl = 'https://example.com/notes/help'; releaseNotesUrl = 'https://github.com/contoso/notes/releases'
            links = @([ordered]@{ url = 'https://github.com/contoso/notes' })
        }
        files = [ordered]@{ source = 'stage'; include = @('**/*') }
        install = [ordered]@{ scope = 'user'; launchAfterInstall = 'off' }
        ui = [ordered]@{ theme = $theme; license = 'LICENSE.txt'; licenseStyle = 'inline' }
        output = [ordered]@{ directory = 'dist'; fileName = "Setup-$theme.exe"; includeWinUI = $true }
    }
    $projectPath = Join-Path $work "project-$theme.json"
    [IO.File]::WriteAllText($projectPath, ($project | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    & $cli build $projectPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed ($theme)" }
    $setupExe = Join-Path $work "dist\Setup-$theme.exe"

    foreach ($ui in $FrontEnds) {
        foreach ($lang in $Languages) {
            $strings = Get-Content "shared\localization\$lang.json" -Raw -Encoding UTF8 | ConvertFrom-Json
            $tag = "$ui-$theme-$lang"
            Write-Host "==> $tag" -ForegroundColor Cyan
            Remove-Existing
            $setup = Start-Process -FilePath $setupExe -ArgumentList '/ui', $ui, '/lang', $lang -PassThru
            try {
                $w = Wait-Window $setup $ui
                Start-Sleep -Seconds 2
                & $capture -ProcessId $w.Pid -Path (Join-Path $Out "$tag-1-welcome.png")
                $install = Wait-Name $w.Window @($strings.'button.install', ('&' + $strings.'button.install')) 20
                Invoke-UiaButton -Root $w.Window -Name $install
                $close = Wait-Name $w.Window @($strings.'button.close', ('&' + $strings.'button.close')) 180
                Start-Sleep -Seconds 2
                & $capture -ProcessId $w.Pid -Path (Join-Path $Out "$tag-2-result.png")
                Invoke-UiaButton -Root $w.Window -Name $close
                $setup.WaitForExit(30000) | Out-Null
            } catch {
                Write-Host "  failed: $($_.Exception.Message)" -ForegroundColor Red
            } finally {
                if (-not $setup.HasExited) { Stop-Process -Id $setup.Id -Force -ErrorAction SilentlyContinue }
                Get-Process FluentSetup.UI -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
Remove-Existing
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Get-ChildItem $Out -Filter *.png | ForEach-Object Name
