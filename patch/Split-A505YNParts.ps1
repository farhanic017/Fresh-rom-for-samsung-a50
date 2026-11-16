$src = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed-MODIFIED.zip'
$dstDir = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\rom_parts_new'
if (Test-Path $dstDir) { Remove-Item $dstDir -Recurse -Force }
New-Item -ItemType Directory -Path $dstDir | Out-Null
$total = (Get-Item $src).Length
$part = [int64]([Math]::Ceiling($total / 3))
Write-Output "total size = $total, part size = $part"
$in = [System.IO.File]::OpenRead($src)
$buf = New-Object byte[] (4MB)
for ($i = 1; $i -le 3; $i++) {
    $outPath = Join-Path $dstDir "FRSH-A505YN.part$i"
    $out = [System.IO.File]::Create($outPath)
    $remaining = $part
    if ($i -eq 3) { $remaining = $total - ($part * 2) }
    while ($remaining -gt 0) {
        $read = $in.Read($buf, 0, [int][Math]::Min($buf.Length, $remaining))
        if ($read -le 0) { break }
        $out.Write($buf, 0, $read)
        $remaining -= $read
    }
    $out.Close()
    Write-Output "Written $outPath"
}
$in.Close()