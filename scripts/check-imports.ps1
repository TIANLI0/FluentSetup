<#
.SYNOPSIS
  Fails when a shipped installer executable statically imports a DLL that the loader could pick up from the folder the
  program runs in.
.DESCRIPTION
  Setup.exe (FluentSetup.Stub.exe), the relocated uninstall.exe, the elevated worker (the same file) and the WinUI front
  end run from folders that other programs or downloads can write to. A static import is resolved before wWinMain, from
  the program's own folder first, unless it is a KnownDLL or an API set; everything else must be delay-loaded (it then
  loads after SetDefaultDllDirectories limited the search to System32). This reads the PE import table of each file and
  also checks that the image asks the loader to resolve dependencies from System32 only (/DEPENDENTLOADFLAG:0x800).
.PARAMETER Platforms      x64 (default) and/or ARM64: checks out/bin/<platform>/<Configuration>.
.PARAMETER Configuration  Release (default) or Debug.
.PARAMETER Path           Check these files instead.
#>
param(
    [string[]] $Platforms = @('x64'),
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string[]] $Path = @()
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# KnownDLLs on every supported Windows (10 1809 and later, x64 and ARM64): always mapped from System32. ntdll.dll is
# loaded into every process before anything else.
$known = @(
    'advapi32', 'clbcatq', 'combase', 'comdlg32', 'coml2', 'difxapi', 'gdi32', 'imagehlp', 'imm32', 'kernel32', 'msctf',
    'msvcrt', 'normaliz', 'nsi', 'ntdll', 'ole32', 'oleaut32', 'rpcrt4', 'sechost', 'setupapi', 'shcore', 'shell32',
    'shlwapi', 'user32', 'wldap32', 'ws2_32'
) | ForEach-Object { "$_.dll" }
$LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800

function Read-PeImports([string] $file) {
    $b = [IO.File]::ReadAllBytes($file)
    if ($b.Length -lt 0x40 -or [BitConverter]::ToUInt16($b, 0) -ne 0x5A4D) { throw "${file} is not a PE file" }
    $nt = [BitConverter]::ToInt32($b, 0x3C)
    if ([BitConverter]::ToUInt32($b, $nt) -ne 0x4550) { throw "${file} has no PE signature" }
    $sectionCount = [BitConverter]::ToUInt16($b, $nt + 6)
    $optSize = [BitConverter]::ToUInt16($b, $nt + 20)
    $opt = $nt + 24
    $magic = [BitConverter]::ToUInt16($b, $opt)
    $dirs = switch ($magic) { 0x20B { $opt + 112 } 0x10B { $opt + 96 } default { throw "${file}: unknown optional header 0x$('{0:X}' -f $magic)" } }
    $sections = for ($i = 0; $i -lt $sectionCount; $i++) {
        $s = $opt + $optSize + 40 * $i
        [pscustomobject]@{
            Va = [BitConverter]::ToUInt32($b, $s + 12); VSize = [BitConverter]::ToUInt32($b, $s + 8)
            Raw = [BitConverter]::ToUInt32($b, $s + 20); RawSize = [BitConverter]::ToUInt32($b, $s + 16)
        }
    }
    $toOffset = {
        param([uint32] $rva)
        foreach ($s in $sections) {
            if ($rva -ge $s.Va -and $rva -lt $s.Va + [Math]::Max($s.VSize, $s.RawSize)) { return [int]($rva - $s.Va + $s.Raw) }
        }
        throw "${file}: RVA 0x$('{0:X}' -f $rva) is outside every section"
    }
    $readName = {
        param([int] $at)
        $end = $at
        while ($b[$end] -ne 0) { $end++ }
        [Text.Encoding]::ASCII.GetString($b, $at, $end - $at)
    }

    $imports = @()
    $importRva = [BitConverter]::ToUInt32($b, $dirs + 8 * 1)
    if ($importRva -ne 0) {
        for ($d = & $toOffset $importRva; ; $d += 20) {
            $nameRva = [BitConverter]::ToUInt32($b, $d + 12)
            if ($nameRva -eq 0 -and [BitConverter]::ToUInt32($b, $d) -eq 0) { break }
            $imports += & $readName (& $toOffset $nameRva)
        }
    }
    $delayed = @()
    $delayRva = [BitConverter]::ToUInt32($b, $dirs + 8 * 13)
    if ($delayRva -ne 0) {
        for ($d = & $toOffset $delayRva; ; $d += 32) {
            $nameRva = [BitConverter]::ToUInt32($b, $d + 4)
            if ($nameRva -eq 0) { break }
            $delayed += & $readName (& $toOffset $nameRva)
        }
    }
    # IMAGE_LOAD_CONFIG_DIRECTORY.DependentLoadFlags: a WORD at 0x4E (PE32+) or 0x36 (PE32).
    $flags = $null
    $loadConfigRva = [BitConverter]::ToUInt32($b, $dirs + 8 * 10)
    if ($loadConfigRva -ne 0) {
        $lc = & $toOffset $loadConfigRva
        $at = if ($magic -eq 0x20B) { 0x4E } else { 0x36 }
        if ([BitConverter]::ToUInt32($b, $lc) -ge $at + 2) { $flags = [BitConverter]::ToUInt16($b, $lc + $at) }
    }
    [pscustomobject]@{ Imports = $imports; Delayed = $delayed; DependentLoadFlags = $flags }
}

if ($Path.Count -eq 0) {
    foreach ($platform in $Platforms) {
        $bin = Join-Path $root "out\bin\$platform\$Configuration"
        $stub = Join-Path $bin 'FluentSetup.Stub.exe'
        if (-not (Test-Path $stub)) { throw "$stub not found; build first" }
        $Path += $stub
        $Path += @(Get-ChildItem (Join-Path $bin 'ui') -Filter '*.exe' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    }
}

$problems = @()
foreach ($file in $Path) {
    $pe = Read-PeImports $file
    $bad = @($pe.Imports | Where-Object { $_ -notmatch '^(api|ext)-ms-' -and $known -notcontains $_.ToLowerInvariant() })
    $name = [IO.Path]::GetRelativePath($root, (Resolve-Path $file))
    Write-Host ("{0}: {1} static, {2} delay-loaded imports" -f $name, $pe.Imports.Count, $pe.Delayed.Count)
    foreach ($dll in $bad) { $problems += "${name}: statically imports $dll (not a KnownDLL): add it to DelayLoadDLLs" }
    if ($null -eq $pe.DependentLoadFlags -or ($pe.DependentLoadFlags -band $LOAD_LIBRARY_SEARCH_SYSTEM32) -eq 0) {
        $problems += "${name}: not linked with /DEPENDENTLOADFLAG:0x800"
    }
}
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "$($problems.Count) import problem(s)"
}
Write-Host 'Imports: only KnownDLLs and API sets are loaded before wWinMain.' -ForegroundColor Green
