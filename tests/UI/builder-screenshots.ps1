<#
.SYNOPSIS
  Screenshots of every Builder page (all cards expanded, scrolled screen by screen) for a project that uses every
  setting, in the given themes. Drives the app through UI Automation only: no mouse or keyboard input.
  Needs the Builder built (dotnet build src\Builder -c Release -p:Platform=x64) and the Windows App Runtime 2.5.
  Run it on a CI runner, or locally with scripts\run-hidden.ps1 (screenshots of an invisible desktop are blank).
.EXAMPLE
  tests\UI\builder-screenshots.ps1 -Out out\builder-shots -Themes light,dark
#>
param(
    [string] $Out = 'out\builder-shots',
    [string[]] $Themes = @('light', 'dark'),
    [int] $Width = 1160,
    [int] $Height = 900
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Import-Module (Join-Path $PSScriptRoot 'Uia.psm1') -Force
$exe = Get-ChildItem (Join-Path $root 'src\Builder\bin\x64\Release') -Recurse -Filter FluentSetup.Builder.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'Build the Builder first: dotnet build src\Builder -c Release -p:Platform=x64' }
$capture = Join-Path $root 'scripts\capture-window.ps1'
$Out = [IO.Path]::GetFullPath((Join-Path $root $Out))
Remove-Item -Recurse -Force $Out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Out | Out-Null

# A project that touches every page: two variants, prerequisites (every kind of source), options, actions, shortcuts,
# file types, URL schemes, registry, environment, services, firewall, inputs, config files, banner, upgrade and uninstall.
$work = Join-Path $Out 'project'
foreach ($v in 'full', 'shared') {
    New-Item -ItemType Directory -Force (Join-Path $work "$v\redist") | Out-Null
    Copy-Item "$env:WINDIR\System32\notepad.exe" (Join-Path $work "$v\App.exe")
    Copy-Item "$env:WINDIR\System32\notepad.exe" (Join-Path $work "$v\redist\driver-setup.exe")
    'readme' | Set-Content (Join-Path $work "$v\readme.txt")
}
'MIT License' | Set-Content (Join-Path $work 'LICENSE.txt')
# A 960 x 240 banner (ui.banner) for the Appearance page's preview.
Add-Type -AssemblyName System.Drawing
$banner = New-Object System.Drawing.Bitmap 960, 240
$g = [System.Drawing.Graphics]::FromImage($banner)
$fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 960, 240),
    [System.Drawing.Color]::FromArgb(0, 95, 184), [System.Drawing.Color]::FromArgb(120, 60, 200))
$g.FillRectangle($fill, 0, 0, 960, 240)
$font = New-Object System.Drawing.Font 'Segoe UI', 44
$g.DrawString('Contoso Notes', $font, [System.Drawing.Brushes]::White, 48, 72)
$g.Dispose(); $fill.Dispose(); $font.Dispose()
$banner.Save((Join-Path $work 'banner.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$banner.Dispose()
$project = @'
{
  "schemaVersion": 1,
  "variables": { "Version": "2.4.1", "DriverVersion": "1.2.0.0" },
  "app": { "id": "Contoso.Notes", "name": "Contoso Notes", "publisher": "Contoso", "version": "$(Version)",
           "description": "Notes with sync", "website": "https://example.com", "mainExecutable": "App.exe",
           "appUserModelId": "Contoso.Notes", "toastActivatorClsid": "{6F2C3D8A-7B41-4E5F-9A0C-1D2E3F405162}" },
  "files": {
    "include": ["**/*"], "exclude": ["**/*.pdb"], "preserve": ["config/*.json"], "emptyDirectories": ["logs"],
    "variants": [
      { "id": "full", "label": { "en-US": "Self-contained", "zh-CN": "独立安装" }, "default": true, "source": "full" },
      { "id": "shared", "label": { "en-US": "Shared runtime", "zh-CN": "共享运行时" }, "source": "shared" }
    ]
  },
  "install": {
    "scope": "userOrMachine", "defaultScope": "user", "startMenuFolder": "Contoso", "closeProcesses": ["App.exe"],
    "closeCommand": { "run": "App.exe", "arguments": ["--exit"] }, "relaunchArguments": ["--updated"],
    "legacyUninstallKeys": ["{11111111-2222-3333-4444-555555555555}_is1"], "addToPath": ["cli"],
    "cleanup": { "removeFiles": ["unins*.exe", "unins*.dat"], "removeShortcuts": ["Contoso\\Uninstall Contoso Notes.lnk"], "removeRunValues": ["ContosoOld"] },
    "appPaths": true, "restartManager": "auto", "lockedFiles": "reboot"
  },
  "requirements": {
    "netFramework": "4.8",
    "netFrameworkSetup": { "helpUrl": "https://dotnet.microsoft.com/download/dotnet-framework" },
    "webView2": { "required": true, "minVersion": "120.0.0.0", "auto": "download", "onFailure": "warn" },
    "windowsAppRuntime": { "required": true, "version": "2.5.1", "auto": "bundle", "variants": ["shared"] },
    "vcRedist": { "required": true, "minVersion": "14.40", "architecture": "", "auto": "bundle" },
    "dotNet": [{ "runtime": "desktop", "version": "8.0.0", "variants": ["shared"],
                 "download": { "url": "https://example.com/windowsdesktop-runtime-win-x64.exe", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "size": 0, "signer": "Microsoft Corporation" } }],
    "custom": [{ "id": "driver", "name": "Contoso Sync Driver 2",
                 "detect": { "registry": { "hive": "HKLM", "key": "SOFTWARE\\Contoso\\Driver", "value": "Version", "version": "2.0" } },
                 "installer": "redist\\driver-setup.exe", "arguments": ["/S"], "onFailure": "fail" }]
  },
  "options": [
    { "id": "driver", "label": { "en-US": "Install the sync driver (required)", "zh-CN": "安装同步驱动（必需）" }, "required": true },
    { "id": "autostart", "label": { "en-US": "Start with Windows", "zh-CN": "开机启动" }, "default": true, "quietDefault": false,
      "startup": { "name": "ContosoNotes", "arguments": ["--background"] } }
  ],
  "actions": [
    { "id": "driver", "when": "afterInstall", "run": "redist\\driver-setup.exe", "arguments": ["-install", "-silent"], "ifOption": "driver",
      "successExitCodes": [0, 183], "onFailure": "warn", "fallbackArguments": ["-install"],
      "skipIfInstalled": { "hive": "HKLM", "key": "SOFTWARE\\Contoso\\Driver", "value": "Version", "version": "$(DriverVersion)" },
      "name": { "en-US": "Sync driver", "zh-CN": "同步驱动" } },
    { "when": "beforeUninstall", "run": "App.exe", "arguments": ["--unregister"], "onFailure": "ignore" }
  ],
  "uninstall": {
    "removeData": ["{InstallDir}\\logs"], "removeRunValues": ["ContosoNotes"], "removeRegistryKeys": ["HKCU\\Software\\Contoso\\Notes"],
    "userData": { "paths": ["{LocalAppData}\\Contoso Notes"], "default": false },
    "elevated": { "ifExists": ["{CommonAppData}\\Contoso\\driver.state"], "scheduledTasks": ["Contoso\\Sync"], "removeRegistryKeys": ["HKLM\\Software\\Contoso\\Notes"] }
  },
  "update": { "github": "contoso/notes", "checksums": "SHA256SUMS.txt" },
  "ui": { "license": "LICENSE.txt", "licenseMode": "firstInstall", "defaultLanguage": "auto", "banner": "banner.png" },
  "output": { "directory": "dist", "fileName": "{AppName}-{Version}-Setup.exe", "compression": "max" },
  "signing": { "signUninstaller": true },
  "shortcuts": [
    { "name": "Contoso Notes (Safe mode)", "arguments": ["--safe-mode"], "description": "Start without add-ins", "location": "startMenu" },
    { "name": "Contoso Notes Sync", "target": "App.exe", "arguments": ["--sync", "--minimized"], "location": "startup", "ifOption": "autostart" }
  ],
  "associations": [
    { "extensions": [".cnote", ".cnotes"], "progId": "Contoso.Notes.Document", "description": "Contoso Notes document",
      "arguments": ["%1"], "contentType": "application/x-contoso-note" }
  ],
  "protocols": [{ "scheme": "contoso-notes", "description": "Contoso Notes link", "arguments": ["--open", "%1"] }],
  "registry": [
    { "key": "Software\\Contoso\\Notes", "uninstall": "values", "values": [
        { "name": "InstallDir", "type": "string", "data": "{InstallDir}" },
        { "name": "Server", "type": "string", "data": "{Input:server}" },
        { "name": "Telemetry", "type": "dword", "data": "0" } ] }
  ],
  "environment": [{ "name": "CONTOSO_NOTES_HOME", "value": "{InstallDir}" }],
  "services": [
    { "name": "ContosoNotesSync", "displayName": "Contoso Notes Sync", "description": "Keeps notes in sync in the background.",
      "run": "App.exe", "arguments": ["--service"], "startType": "delayedAuto", "account": "localService" }
  ],
  "firewall": [{ "name": "Contoso Notes sync", "direction": "in", "protocol": "tcp", "localPorts": "8765", "profiles": ["private"] }],
  "inputs": [
    { "id": "server", "label": { "en-US": "Server address", "zh-CN": "服务器地址" }, "description": { "en-US": "Where your notes are stored.", "zh-CN": "笔记的存储位置。" },
      "type": "text", "default": "https://notes.example.com", "required": true, "pattern": "^https://" },
    { "id": "edition", "label": { "en-US": "Edition", "zh-CN": "版本" }, "type": "choice", "default": "personal",
      "choices": [ { "value": "personal", "label": { "en-US": "Personal", "zh-CN": "个人版" } }, { "value": "team", "label": { "en-US": "Team", "zh-CN": "团队版" } } ] }
  ],
  "configFiles": [
    { "path": "config\\settings.json", "format": "json", "values": [
        { "key": "server.url", "value": "{Input:server}" }, { "key": "sync.enabled", "value": "true", "type": "bool" } ] },
    { "path": "{AppData}\\Contoso Notes\\notes.ini", "format": "ini", "values": [ { "section": "General", "key": "Edition", "value": "{Input:edition}" } ] }
  ]
}
'@
$projectPath = Join-Path $work 'fluentsetup.json'
[IO.File]::WriteAllText($projectPath, $project, [Text.UTF8Encoding]::new($false))

if (-not ('FsShots.Win' -as [type])) {
    Add-Type -Namespace FsShots -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);'
}
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$settingsDir = Join-Path $env:LOCALAPPDATA 'FluentSetup\Builder'
$settingsFile = Join-Path $settingsDir 'settings.json'
$savedSettings = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw } else { $null }

try {
    foreach ($theme in $Themes) {
        New-Item -ItemType Directory -Force $settingsDir | Out-Null
        [IO.File]::WriteAllText($settingsFile, (@{ RecentProjects = @(); Theme = $theme } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        $p = Start-Process -FilePath $exe -ArgumentList "`"$projectPath`"" -PassThru
        try {
            $w = Get-UiaWindow -ProcessId $p.Id -TimeoutSeconds 60
            Start-Sleep -Seconds 3
            [void][FsShots.Win]::SetWindowPos([IntPtr]$w.Current.NativeWindowHandle, [IntPtr]::Zero, 0, 0, $Width, $Height, 0x0004)
            Start-Sleep -Milliseconds 800
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            $items = @($w.FindAll($TS::Descendants, $cond) | Where-Object { $_.Current.ClassName -like '*NavigationViewItem' })
            Write-Host "${theme}: $(($items | ForEach-Object { $_.Current.Name }) -join ' | ')"
            $index = 0
            foreach ($item in $items) {
                $index++
                $sel = $null
                if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sel)) { $sel.Select() }
                else { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
                Start-Sleep -Milliseconds 1500
                $ec = New-Object System.Windows.Automation.PropertyCondition($AE::IsExpandCollapsePatternAvailableProperty, $true)
                foreach ($e in $w.FindAll($TS::Descendants, $ec)) {
                    if ($e.Current.ClassName -notlike '*Expander') { continue }
                    $pat = $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    if ($pat.Current.ExpandCollapseState -eq 'Collapsed') { try { $pat.Expand() } catch { } }
                }
                Start-Sleep -Milliseconds 800
                # The page's own ScrollViewer: the tallest vertically scrollable one.
                $sc = New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)
                $scroller = $w.FindAll($TS::Descendants, $sc) | Where-Object {
                    $_.Current.ClassName -like '*ScrollViewer' -and $_.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable
                } | Sort-Object { $_.Current.BoundingRectangle.Height } -Descending | Select-Object -First 1
                $pattern = if ($scroller) { $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } else { $null }
                $shot = 0
                do {
                    $shot++
                    $file = Join-Path $Out ("{0}-{1:D2}-{2}-{3}.png" -f $theme, $index, ($item.Current.Name -replace '[^\w]+', '_'), $shot)
                    & $capture -ProcessId $p.Id -Path $file | Out-Null
                    if (-not $pattern -or -not $pattern.Current.VerticallyScrollable -or $pattern.Current.VerticalScrollPercent -ge 99.5) { break }
                    $pattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement)
                    Start-Sleep -Milliseconds 500
                } while ($shot -lt 10)
            }
        } finally {
            if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        }
    }
} finally {
    if ($null -ne $savedSettings) { [IO.File]::WriteAllText($settingsFile, $savedSettings, [Text.UTF8Encoding]::new($false)) }
    else { Remove-Item $settingsFile -ErrorAction SilentlyContinue }
}
Write-Host "Screenshots: $((Get-ChildItem $Out -Filter *.png).Count) in $Out"
