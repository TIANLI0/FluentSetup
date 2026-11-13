<#
.SYNOPSIS
  Checks the installer's built-in string tables (shared/localization/*.json) against en-US.json: every file parses,
  has exactly the keys of en-US.json in the same order and blank-line grouping, keeps the {n} placeholders, the
  "&" mnemonics and the /switch names of each string, names its own language, and is wired into the stub
  (localization.rc2, resource.h, localization.cpp) and the builder (Localization.SupportedLanguages). Exit code 1
  lists the problems.
.EXAMPLE
  powershell -NoProfile -File scripts\check-localization.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dir = Join-Path $root 'shared\localization'
$problems = New-Object System.Collections.Generic.List[string]

function Read-Utf8([string] $path) { [IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false))) }

# One entry per line: a key, or '' for a blank line (the grouping). Duplicate keys are reported here, since
# ConvertFrom-Json would silently keep one of them.
function Get-Layout([string] $path) {
    $layout = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Read-Utf8 $path) -split "`r?`n") {
        if ($line -match '^\s*"([^"]+)"\s*:') { $layout.Add($Matches[1]) }
        elseif ($line.Trim() -eq '') { $layout.Add('') }
    }
    while ($layout.Count -gt 0 -and $layout[$layout.Count - 1] -eq '') { $layout.RemoveAt($layout.Count - 1) }
    return , $layout
}

function Get-Table([string] $path) {
    $table = [ordered]@{}
    $json = (Read-Utf8 $path) | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) { $table[$p.Name] = [string] $p.Value }
    return $table
}

function Get-Placeholders([string] $text) { (([regex]::Matches($text, '\{\d+\}') | ForEach-Object { $_.Value }) | Sort-Object) -join ' ' }
function Get-Mnemonics([string] $text) { ([regex]::Matches(($text -replace '&&', ''), '&')).Count }
function Get-Switches([string] $text) { (([regex]::Matches($text, '(?<![A-Za-z0-9/])/[A-Za-z][A-Za-z0-9-]*=?') | ForEach-Object { $_.Value }) | Sort-Object -Unique -CaseSensitive) -join ' ' }

$enPath = Join-Path $dir 'en-US.json'
$enLayout = Get-Layout $enPath
$en = Get-Table $enPath
$files = Get-ChildItem $dir -Filter '*.json' | Sort-Object Name
$tags = @($files | ForEach-Object { $_.BaseName })

foreach ($file in $files) {
    $tag = $file.BaseName
    $where = "$tag.json"
    try { $table = Get-Table $file.FullName } catch { $problems.Add("${where}: not valid JSON: $($_.Exception.Message)"); continue }
    $layout = Get-Layout $file.FullName

    $keys = @($layout | Where-Object { $_ -ne '' })
    foreach ($dup in ($keys | Group-Object -CaseSensitive | Where-Object Count -gt 1)) { $problems.Add("${where}: duplicate key '$($dup.Name)'") }
    foreach ($k in $en.Keys) { if (-not $table.Contains($k)) { $problems.Add("${where}: missing key '$k'") } }
    foreach ($k in $table.Keys) { if (-not $en.Contains($k)) { $problems.Add("${where}: key '$k' is not in en-US.json") } }
    if (($layout -join "`n") -ne ($enLayout -join "`n")) {
        $n = [Math]::Min($layout.Count, $enLayout.Count)
        $i = 0
        while ($i -lt $n -and $layout[$i] -eq $enLayout[$i]) { $i++ }
        $problems.Add("${where}: key order or blank-line grouping differs from en-US.json at entry $($i + 1) ('$(if ($i -lt $layout.Count) { $layout[$i] })' vs '$(if ($i -lt $enLayout.Count) { $enLayout[$i] })')")
    }
    if ($table['language'] -ne $tag) { $problems.Add("${where}: ""language"" is '$($table['language'])', expected '$tag'") }

    foreach ($k in $en.Keys) {
        if (-not $table.Contains($k)) { continue }
        $source = $en[$k]; $text = $table[$k]
        if ([string]::IsNullOrWhiteSpace($text)) { $problems.Add("${where}: '$k' is empty"); continue }
        if ((Get-Placeholders $source) -ne (Get-Placeholders $text)) {
            $problems.Add("${where}: '$k' has placeholders [$(Get-Placeholders $text)], en-US has [$(Get-Placeholders $source)]")
        }
        if ((Get-Mnemonics $source) -ne (Get-Mnemonics $text)) {
            $problems.Add("${where}: '$k' has $(Get-Mnemonics $text) '&' mnemonic(s), en-US has $(Get-Mnemonics $source)")
        }
        if ((Get-Switches $source) -ne (Get-Switches $text)) {
            $problems.Add("${where}: '$k' has switches [$(Get-Switches $text)], en-US has [$(Get-Switches $source)]")
        }
        if (($source -split "`n").Count -ne ($text -split "`n").Count) {
            $problems.Add("${where}: '$k' has $(($text -split "`n").Count) lines, en-US has $(($source -split "`n").Count)")
        }
    }
}

# Every table must be embedded in the stub under its own id, matched by localization.cpp and known to the builder.
$boot = Join-Path $root 'src\Bootstrapper'
$stubRc = Read-Utf8 (Join-Path $boot 'stub.rc')
$rc = Read-Utf8 (Join-Path $boot 'localization.rc2')
$header = Read-Utf8 (Join-Path $boot 'resource.h')
$cpp = Read-Utf8 (Join-Path $boot 'localization.cpp')
$cs = Read-Utf8 (Join-Path $root 'src\Builder.Core\Packaging\Localization.cs')
if ($stubRc -notmatch '#include\s+"localization\.rc2"') { $problems.Add('stub.rc: does not include localization.rc2') }
$ids = @([regex]::Matches($header, '#define\s+IDR_STRINGS_\w+\s+(\d+)') | ForEach-Object { $_.Groups[1].Value })
foreach ($dup in ($ids | Group-Object | Where-Object Count -gt 1)) { $problems.Add("resource.h: id $($dup.Name) is used twice") }
$supported = if ($cs -match 'SupportedLanguages\s*=\s*\[([^\]]*)\]') { @([regex]::Matches($Matches[1], '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) } else { @() }
foreach ($tag in $tags) {
    $id = 'IDR_STRINGS_' + $tag.ToUpperInvariant().Replace('-', '_')
    if ($header -notmatch ('#define\s+' + $id + '\s+\d+')) { $problems.Add("resource.h: no $id") }
    if ($rc -notmatch ('(?m)^' + $id + '\s+RCDATA\s+"[^"]*\\\\' + [regex]::Escape("$tag.json") + '"')) { $problems.Add("localization.rc2: no '$id RCDATA ...$tag.json' line") }
    if ($cpp -notmatch ('\{L"' + [regex]::Escape($tag) + '",\s*L"[^"]*",\s*' + $id + '\}')) { $problems.Add("localization.cpp: no kLanguages entry {L""$tag"", ..., $id}") }
    if ($supported -notcontains $tag) { $problems.Add("Localization.cs: $tag is not in SupportedLanguages") }
}
foreach ($tag in $supported) { if ($tags -notcontains $tag) { $problems.Add("Localization.cs: SupportedLanguages lists $tag, but shared/localization/$tag.json does not exist") } }

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host "$($problems.Count) problem(s) in $($files.Count) string tables." -ForegroundColor Red
    exit 1
}
Write-Host "$($files.Count) string tables OK ($($en.Count) keys each): $($tags -join ', ')." -ForegroundColor Green
