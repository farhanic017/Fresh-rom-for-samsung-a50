<#
.SYNOPSIS
    Build the SM-A505YN-compatible Fresh ROM zip from the official A50 release.

.DESCRIPTION
    The official Fresh ROM for the Samsung Galaxy A50 refuses to install on the
    Australian/NZ SM-A505YN because package.prop lists only six whitelisted models
    and fresh-tk's device_checker prefix-matches ro.boot.bootloader against them.

    This script performs a surgical, ZIP64-aware patch of exactly ONE entry
    (package.prop -> pkg.models += " SM-A505YN") and copies every other byte of
    the 4.4 GB archive verbatim, then verifies the result:
      * both zips open with System.IO.Compression (catches broken ZIP64 records)
      * same 151 entries in the same order
      * every entry byte-identical except package.prop (per-entry SHA256)
      * package.prop content, central-directory CRC32 and local-header CRC32 agree
      * device_checker simulation: A505YN + the original 6 models pass, others fail

    The original zip is never modified.

.PARAMETER Source
    Path to the official zip. Defaults to the well-known download name in the
    current directory, then the script dir, Downloads and D:\DOWNLOADS.

.PARAMETER Output
    Path of the patched zip. Default: source name with "-A505YN" inserted.

.PARAMETER Model
    Model to whitelist. Default: SM-A505YN.

.PARAMETER SkipHashVerify
    Skip the (slow, ~4.4 GB read) per-entry SHA256 comparison.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Apply-A505YNPatch.ps1

.EXAMPLE
    .\Apply-A505YNPatch.ps1 -Source "D:\DOWNLOADS\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-signed.zip"
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Output,
    [string]$Model = 'SM-A505YN',
    [switch]$SkipHashVerify
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Add-Type -AssemblyName System.IO.Compression.FileSystem

# SHA256 of the official build this patch was authored and tested against.
$ExpectedSourceSha256 = '5651C3E0E5D79CE7BAE28A40BB30B563BB312FE34503E7808628C79C64528A1B'

# ---------------------------------------------------------------- helpers ---
function Find-SourceZip {
    $name = 'FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-signed.zip'
    $candidates = @(
        (Join-Path (Get-Location) $name),
        (Join-Path $here $name),
        (Join-Path (Split-Path -Parent $here) $name),
        (Join-Path $env:USERPROFILE "Downloads\$name"),
        "D:\DOWNLOADS\$name"
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path } }
    return $null
}

function Get-EntryBytes {
    param($zip, [string]$name)
    $e = $zip.Entries | Where-Object { $_.FullName -eq $name }
    if (-not $e) { throw "entry '$name' not found" }
    $ms = New-Object System.IO.MemoryStream
    $st = $e.Open()
    try { $st.CopyTo($ms) } finally { $st.Close() }
    return , $ms.ToArray()   # unary comma: stop PowerShell unrolling byte[] to Object[]
}

function Get-StreamSha256 {
    param($entry)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $st = $entry.Open()
    try { $h = $sha.ComputeHash($st) } finally { $st.Dispose(); $sha.Dispose() }
    return , $h
}

function Test-DeviceChecker {
    # Mirrors fresh-tk device_checker(): the first model that prefixes
    # "SM-$bootloader" wins; no match at all = fail.
    param([string]$ModelsLine, [string]$Bootloader)
    $cand = "SM-$Bootloader"
    foreach ($m in ($ModelsLine -split '\s+')) {
        if ($m -and $cand.StartsWith($m)) { return $m }
    }
    return 'fail'
}

# ------------------------------------------------------------- locate src ---
if (-not $Source) { $Source = Find-SourceZip }
if (-not $Source -or -not (Test-Path $Source)) {
    throw "Official zip not found. Pass it explicitly: -Source <path>. Looked for FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-signed.zip in the current dir, script dir, Downloads and D:\DOWNLOADS."
}
$Source = (Resolve-Path $Source).Path

if (-not $Output) {
    $srcDir  = Split-Path -Parent $Source
    $srcBase = [System.IO.Path]::GetFileNameWithoutExtension($Source)
    if ($srcBase.EndsWith('-signed')) {
        $outBase = $srcBase.Substring(0, $srcBase.Length - '-signed'.Length) + '-A505YN-signed'
    } else {
        $outBase = "$srcBase-A505YN"
    }
    $Output = Join-Path $srcDir ($outBase + '.zip')
}
Write-Host "source : $Source" -ForegroundColor Cyan
Write-Host "output : $Output" -ForegroundColor Cyan
Write-Host "model  : $Model"   -ForegroundColor Cyan
if (Test-Path $Output) { throw "Output already exists: $Output (delete it first to rebuild)" }

$srcHash = (Get-FileHash $Source -Algorithm SHA256).Hash
if ($srcHash -ne $ExpectedSourceSha256) {
    Write-Host "WARNING: source SHA256 does not match the tested build:" -ForegroundColor Yellow
    Write-Host "  expected $ExpectedSourceSha256" -ForegroundColor Yellow
    Write-Host "  actual   $srcHash" -ForegroundColor Yellow
    Write-Host "  Continuing - the patch is generic, but only the build above is verified." -ForegroundColor Yellow
} else {
    Write-Host "source SHA256 matches the tested build." -ForegroundColor DarkGray
}

# ------------------------------------------------- read + rewrite package.prop
$srcZip = [System.IO.Compression.ZipFile]::OpenRead($Source)
try {
    $propBytes  = Get-EntryBytes $srcZip 'package.prop'
    $entryCount = $srcZip.Entries.Count
} finally { $srcZip.Dispose() }

$text = [System.Text.Encoding]::UTF8.GetString($propBytes)
$modelsLine = ($text -split "`n" | Where-Object { $_ -match '^pkg\.models=' }) -replace "`r$", ''
if (-not $modelsLine) { throw "pkg.models line not found in package.prop" }
if (($modelsLine -split '\s+') -contains $Model) {
    throw "package.prop already contains $Model - nothing to do."
}
$newModels = "$modelsLine $Model"
if (-not $text.Contains($modelsLine)) { throw "internal error: models line not found verbatim" }
$newText  = $text.Replace($modelsLine, $newModels)   # line-ending agnostic, replaces once
$newBytes = [System.Text.Encoding]::UTF8.GetBytes($newText)

Write-Host "pkg.models:" -ForegroundColor Yellow
Write-Host "  - $modelsLine"
Write-Host "  + $newModels" -ForegroundColor Green
Write-Host ("package.prop: {0} -> {1} bytes (+{2})" -f $propBytes.Length, $newBytes.Length, ($newBytes.Length - $propBytes.Length))
if ($newBytes.Length -ne ($propBytes.Length + $Model.Length + 1)) {
    throw ("unexpected size delta: wanted +{0}, got +{1} - aborting before writing." -f `
        ($Model.Length + 1), ($newBytes.Length - $propBytes.Length))
}

# ------------------------------------------------------------------- patch ---
if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetType('ZipPatcher') })) {
    Add-Type -Path (Join-Path $here 'ZipPatcher.cs')
}
$log = [ZipPatcher]::Patch($Source, $Output, 'package.prop', $newBytes)
Write-Host $log -ForegroundColor DarkGray

# ----------------------------------------------------------------- verify ----
Write-Host "`n=== verification ===" -ForegroundColor Cyan
$fail = @()

$dstZip = [System.IO.Compression.ZipFile]::OpenRead($Output)   # throws if ZIP64/CD broken
try {
    if ($dstZip.Entries.Count -ne $entryCount) {
        $fail += "entry count changed: $entryCount -> $($dstZip.Entries.Count)"
    }

    # 1. package.prop content round-trip
    $outProp = Get-EntryBytes $dstZip 'package.prop'
    if ([System.Text.Encoding]::UTF8.GetString($outProp) -ne $newText) {
        $fail += "package.prop content mismatch after patch"
    }
    if ($outProp.Length -ne $newBytes.Length) {
        $fail += "package.prop size mismatch: $($outProp.Length) != $($newBytes.Length)"
    }

    # 2. CRC32 consistency: content == central directory == local header
    $contentCrc = [ZipPatcher]::Crc32Of($newBytes)
    $crcs = [ZipPatcher]::ReadStoredCrcs($Output, 'package.prop')
    if ($crcs[0] -ne $contentCrc) {
        $fail += ("package.prop CD CRC mismatch:     content=0x{0:X8} stored=0x{1:X8}" -f $contentCrc, $crcs[0])
    }
    if ($crcs[1] -ne $contentCrc) {
        $fail += ("package.prop local-header CRC mismatch: content=0x{0:X8} stored=0x{1:X8}" -f $contentCrc, $crcs[1])
    }
    if (($crcs[0] -eq $contentCrc) -and ($crcs[1] -eq $contentCrc)) {
        Write-Host ("  package.prop CRC32 consistent: 0x{0:X8} (content = CD = local header)" -f $contentCrc) -ForegroundColor Green
    }

    # 3. names + order + lengths of every entry
    $s2 = [System.IO.Compression.ZipFile]::OpenRead($Source)
    try {
        $max = [Math]::Min($s2.Entries.Count, $dstZip.Entries.Count)
        for ($i = 0; $i -lt $max; $i++) {
            $a = $s2.Entries[$i]; $b = $dstZip.Entries[$i]
            if ($a.FullName -ne $b.FullName) { $fail += "entry $i renamed: $($a.FullName) -> $($b.FullName)"; break }
            if ($a.FullName -eq 'package.prop') { continue }
            if ($a.Length -ne $b.Length) { $fail += "$($a.FullName): length $($a.Length) -> $($b.Length)" }
        }

        # 4. per-entry SHA256 (the heavy proof that nothing else changed)
        if (-not $SkipHashVerify -and $fail.Count -eq 0) {
            Write-Host "  hashing all $entryCount entries on both sides (4.4 GB, please wait)..." -ForegroundColor DarkGray
            for ($i = 0; $i -lt $s2.Entries.Count; $i++) {
                $a = $s2.Entries[$i]; $b = $dstZip.Entries[$i]
                if ($a.FullName -eq 'package.prop') { continue }
                $h1 = Get-StreamSha256 $a
                $h2 = Get-StreamSha256 $b
                if ([BitConverter]::ToString($h1) -ne [BitConverter]::ToString($h2)) {
                    $fail += "$($a.FullName): SHA256 differs"
                }
            }
            if ($fail.Count -eq 0) { Write-Host "  all other $($entryCount - 1) entries byte-identical" -ForegroundColor Green }
        }
        elseif ($SkipHashVerify) {
            Write-Host "  (-SkipHashVerify: per-entry SHA256 comparison skipped)" -ForegroundColor Yellow
        }
    } finally { $s2.Dispose() }
} finally { $dstZip.Dispose() }

# 5. device_checker simulation (same semantics as fresh-tk)
$cases = @(
    @{ bl = 'A505YNDVS7CWA1'; want = 'SM-A505YN' },   # this device, latest official firmware
    @{ bl = 'A505YNDU1ASD1';  want = 'SM-A505YN' },   # this device, earlier firmware
    @{ bl = 'A505FNXXU6CSI2'; want = 'SM-A505FN' },
    @{ bl = 'A505GNXU3BSG1';  want = 'SM-A505GN' },
    @{ bl = 'A505FXXU9BWF1';  want = 'SM-A505F'  },
    @{ bl = 'A505GTXXU7BSF1'; want = 'SM-A505GT' },
    @{ bl = 'A515FDXU8GVE2';  want = 'fail' },        # Galaxy A51 must stay rejected
    @{ bl = 'A705FNXXU6CUJ1'; want = 'fail' }         # Galaxy A70 must stay rejected
)
foreach ($c in $cases) {
    $got = Test-DeviceChecker -ModelsLine $newModels -Bootloader $c.bl
    $ok  = ($got -eq $c.want)
    if (-not $ok) { $fail += "device_checker $($c.bl): want $($c.want), got $got" }
    $mark  = if ($ok) { 'PASS' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] bootloader {1,-16} -> {2}" -f $mark, $c.bl, $got) -ForegroundColor $color
}

# ------------------------------------------------------------------ report ---
$dstHash = (Get-FileHash $Output -Algorithm SHA256).Hash
$srcSize = (Get-Item $Source).Length
$dstSize = (Get-Item $Output).Length
Write-Host "`noriginal SHA256: $srcHash"
Write-Host "patched  SHA256: $dstHash"
Write-Host ("size: {0} -> {1} (+{2} bytes)" -f $srcSize, $dstSize, ($dstSize - $srcSize))

if ($fail.Count) {
    Write-Host "`nVERIFICATION FAILED:" -ForegroundColor Red
    $fail | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nVERIFICATION PASSED - $Model is whitelisted, all other bytes unchanged." -ForegroundColor Green
Write-Host "Flash it per docs\INSTALL.md" -ForegroundColor Green
exit 0
