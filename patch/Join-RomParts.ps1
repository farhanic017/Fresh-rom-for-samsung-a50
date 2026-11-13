<#
.SYNOPSIS
    Reassemble the 3 release parts into the patched SM-A505YN ROM zip and verify it.

.DESCRIPTION
    GitHub caps release assets at 2 GB, so the 4.4 GB patched ROM ships as
    FRSH-A505YN.part1/.part2/.part3. This script concatenates them in order and
    checks the result against the known SHA256 of the patched build.

.PARAMETER PartDir
    Folder containing FRSH-A505YN.part1, .part2, .part3. Defaults to the current
    directory.

.PARAMETER Output
    Output zip path. Default: PartDir\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Join-RomParts.ps1 -PartDir D:\Downloads
#>
[CmdletBinding()]
param(
    [string]$PartDir = (Get-Location).Path,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$ExpectedSha256 = '17DFA6F050D7AFC0B596BEA224E5EEAA16C4572378AEEC724A41796C06A36D3C'
$ExpectedSize   = 4436931512

$parts = 1..3 | ForEach-Object { Join-Path $PartDir "FRSH-A505YN.part$_" }
foreach ($p in $parts) {
    if (-not (Test-Path $p)) { throw "Missing part: $p" }
}
if (-not $Output) {
    $Output = Join-Path $PartDir 'FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip'
}
if (Test-Path $Output) { throw "Output already exists: $Output" }

Write-Host "concatenating 3 parts -> $Output" -ForegroundColor Cyan
$out = [System.IO.File]::Create($Output)
try {
    foreach ($p in $parts) {
        $in = [System.IO.File]::OpenRead($p)
        try { $in.CopyTo($out) } finally { $in.Close() }
        Write-Host "  + $(Split-Path $p -Leaf)"
    }
} finally { $out.Close() }

$size = (Get-Item $Output).Length
Write-Host "verifying SHA256..." -ForegroundColor Cyan
$hash = (Get-FileHash $Output -Algorithm SHA256).Hash

if ($size -ne $ExpectedSize) {
    Write-Host "FAIL: size $size != expected $ExpectedSize" -ForegroundColor Red
    exit 1
}
if ($hash -ne $ExpectedSha256) {
    Write-Host "FAIL: SHA256 $hash" -ForegroundColor Red
    Write-Host "      expected $ExpectedSha256" -ForegroundColor Red
    exit 1
}
Write-Host "OK: $hash" -ForegroundColor Green
Write-Host "Ready to flash - see docs\INSTALL.md" -ForegroundColor Green
exit 0
