<#
.SYNOPSIS
  Measures everything a FluentSetup Setup.exe carries besides the app's own (compressed) payload, and checks it against
  the 5 MiB budget. Nothing is estimated: sizes come from the built files, the stub's linker map and the package itself.

.PARAMETER Setup     A Setup.exe built by fluentsetup (default: builds samples/DemoApp into out/size-report).
.PARAMETER Json      Also write the report as JSON to this path.
.EXAMPLE
  scripts/size-report.ps1
  scripts/size-report.ps1 -Setup out\thrm\THRM-3.8.0-Setup.exe -Json out\size.json
#>
param([string] $Setup, [string] $Json)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin = Join-Path $root 'out\bin\x64\Release'
$cli = Join-Path $root 'src\CLI\bin\Release\net10.0\fluentsetup.exe'
$budget = 5MB

if (-not $Setup) {
    # The sample's files are build output (not in git): build the demo app into them on a fresh checkout.
    $staging = Join-Path $root 'samples\DemoApp\out\staging'
    if (-not (Test-Path (Join-Path $staging 'DemoApp.exe'))) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        $msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        & $msbuild (Join-Path $root 'samples\DemoApp\DemoApp.vcxproj') /p:Configuration=Release /p:Platform=x64 "/p:DemoOut=$staging" /nologo /v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Building the DemoApp sample failed' }
        Copy-Item (Join-Path $root 'samples\DemoApp\LICENSE.txt') $staging
    }
    $out = Join-Path $root 'out\size-report'
    & $cli build (Join-Path $root 'samples\DemoApp\fluentsetup.json') --output $out | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Building the DemoApp package failed' }
    $Setup = Get-ChildItem $out -Filter *.exe | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}
$info = & $cli inspect $Setup --json | ConvertFrom-Json
if ($info.problems.Count) { throw "Package problems: $($info.problems -join '; ')" }

# --- package composition (exact, from the container) ---------------------------------------------------------------
$fileSize = (Get-Item $Setup).Length
$peImage = [long]$info.overlayStart                    # stub PE incl. the product's icon/version resources
$frontend = [long]$info.frontendStoredBytes            # compressed WinUI front end bundle
$payload = [long]$info.payloadStoredBytes              # the app itself (excluded from the budget)
$metadata = $fileSize - $peImage - $frontend - $payload # header + footer + alignment (+ signature if any)

# --- stub breakdown from the linker map ---------------------------------------------------------------------------
$map = Join-Path $bin 'FluentSetup.Stub.map'
$stubFile = Join-Path $bin 'FluentSetup.Stub.exe'
$groups = [ordered]@{ 'Bootstrapper' = 0L; 'Win32 fallback UI' = 0L; 'Install engine + elevated worker' = 0L; 'Container + zstd decoder' = 0L; 'Common (JSON, IPC, crypto, fs)' = 0L; 'C/C++ runtime (static)' = 0L; 'Import thunks' = 0L; 'Other code/data' = 0L }
if (Test-Path $map) {
    $symbols = foreach ($line in Get-Content $map) {
        if ($line -match '^\s*([0-9a-f]{4}):([0-9a-f]{8})\s+\S+\s+([0-9a-f]{16})\s+(?:f\s+)?(?:i\s+)?(\S+)\s*$') {
            [pscustomobject]@{ Section = $Matches[1]; Offset = [Convert]::ToInt64($Matches[2], 16); Object = $Matches[4] }
        }
    }
    $symbols = $symbols | Sort-Object Section, Offset
    for ($i = 0; $i -lt $symbols.Count - 1; $i++) {
        $s = $symbols[$i]; $n = $symbols[$i + 1]
        if ($n.Section -ne $s.Section) { continue }
        $size = $n.Offset - $s.Offset
        if ($size -le 0 -or $size -gt 1MB) { continue }
        $o = $s.Object
        $group = switch -Regex ($o) {
            '^FluentSetup\.Archive:' { 'Container + zstd decoder'; break }
            '^FluentSetup\.Install\.Core:|^worker\.obj$' { 'Install engine + elevated worker'; break }
            '^(fallback_ui|theme)\.obj$' { 'Win32 fallback UI'; break }
            '^FluentSetup\.Common:' { 'Common (JSON, IPC, crypto, fs)'; break }
            '^(main|args|controller|elevation|localization|runtime_probe|winui_host)\.obj$' { 'Bootstrapper'; break }
            '^(libcmt|libvcruntime|libcpmt|msvcprt|ucrt|libucrt|delayimp|uuid)[:.]|<common>|linker' { 'C/C++ runtime (static)'; break }
            '\.dll$' { 'Import thunks'; break }
            default { 'Other code/data' }
        }
        $groups[$group] += $size
    }
}
$stubSize = (Get-Item $stubFile).Length
$mapped = ($groups.Values | Measure-Object -Sum).Sum
$stubResources = $peImage - $stubSize    # difference = this product's icon/version resources vs. the stub's defaults
$rcData = 0L
$tables = @(Get-ChildItem (Join-Path $root 'shared\localization') -Filter '*.json')
foreach ($f in $tables) { $rcData += $f.Length }

# --- WinUI front end, raw files ---------------------------------------------------------------------------------------
$uiDir = Join-Path $bin 'ui'
$uiFiles = Get-ChildItem $uiDir -File | Where-Object { $_.Extension -in '.exe', '.dll', '.pri', '.xbf' }
$uiRaw = ($uiFiles | Measure-Object Length -Sum).Sum

$rows = @(
    [pscustomobject]@{ Component = 'Stub PE (as embedded, incl. product icon + version)'; Bytes = $peImage; Note = 'one native exe: everything below' }
)
foreach ($k in $groups.Keys) { $rows += [pscustomobject]@{ Component = "  $k"; Bytes = $groups[$k]; Note = 'code + data, from linker map' } }
$rows += [pscustomobject]@{ Component = '  PE headers, imports, relocations, alignment'; Bytes = [Math]::Max(0, $stubSize - $mapped - $rcData - 10229); Note = 'remainder' }
$rows += [pscustomobject]@{ Component = "  Strings ($($tables.Count) languages) + default icon"; Bytes = $rcData + 10229; Note = 'RCDATA + ICON resources' }
$rows += [pscustomobject]@{ Component = '  Product icon/version delta'; Bytes = $stubResources; Note = 'difference to the default stub' }
$rows += [pscustomobject]@{ Component = 'WinUI 3 front end (compressed)'; Bytes = $frontend; Note = "raw $([Math]::Round($uiRaw / 1KB)) KB: $(($uiFiles | ForEach-Object { '{0} {1} KB' -f $_.Name, [Math]::Round($_.Length / 1KB) }) -join ', ')" }
$rows += [pscustomobject]@{ Component = 'Package metadata (header, footer, alignment, signature)'; Bytes = $metadata; Note = '' }
$overhead = $peImage + $frontend + $metadata
$rows += [pscustomobject]@{ Component = 'TOTAL installer overhead'; Bytes = $overhead; Note = ('{0:P1} of the 5 MiB budget' -f ($overhead / $budget)) }
$rows += [pscustomobject]@{ Component = 'App payload (compressed, not counted)'; Bytes = $payload; Note = "$($info.payloadFiles) files, $([Math]::Round($info.payloadRawBytes / 1MB, 2)) MB raw" }
$rows += [pscustomobject]@{ Component = 'Setup.exe'; Bytes = $fileSize; Note = (Split-Path $Setup -Leaf) }

$rows | Format-Table @{ n = 'Component'; e = { $_.Component } }, @{ n = 'Bytes'; e = { '{0,12:N0}' -f $_.Bytes } }, @{ n = 'KiB'; e = { '{0,9:N1}' -f ($_.Bytes / 1KB) } }, Note -AutoSize | Out-String -Width 220
'Not shipped, already on the user''s PC: Windows App Runtime (framework package, only needed for the WinUI front end),'
'UCRT (part of Windows 10/11). No network access, no downloads.'
if ($overhead -le $budget) { "RESULT: within budget ({0:N2} MiB <= 5 MiB)" -f ($overhead / 1MB) } else { "RESULT: OVER budget by {0:N0} bytes" -f ($overhead - $budget) }
if ($Json) {
    [pscustomobject]@{ setup = $Setup; budgetBytes = $budget; overheadBytes = $overhead; withinBudget = ($overhead -le $budget); rows = $rows } |
        ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $Json
}
if ($overhead -gt $budget) { exit 1 }
