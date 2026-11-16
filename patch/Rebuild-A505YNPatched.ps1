Add-Type -Path 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.dll'
Add-Type -Path 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression.FileSystem\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.FileSystem.dll'

$src = 'D:\DOWNLOADS\FRESH ROM\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip'
$dst = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed-MODIFIED.zip'
if (Test-Path $dst) { Remove-Item $dst -Force }

$zip = [System.IO.Compression.ZipFile]::OpenRead($src)
$dest = [System.IO.Compression.ZipFile]::Open($dst, [System.IO.Compression.ZipArchiveMode]::Create)

foreach ($entry in $zip.Entries) {
    $newEntry = $dest.CreateEntry($entry.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
    $s = $entry.Open()
    $d = $newEntry.Open()
    if ($entry.FullName -eq 'META-INF/com/google/android/updater-script') {
        # write modified updater-script
        $mod = [IO.File]::ReadAllBytes('C:\Users\FARHAN\AppData\Local\Temp\opencode\frsh\META-INF\com\google\android\updater-script')
        $d.Write($mod, 0, $mod.Length)
        $s.Close()
        $d.Close()
        continue
    }
    $buffer = New-Object byte[] (4MB)
    while ($true) {
        $read = $s.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { break }
        $d.Write($buffer, 0, $read)
    }
    $s.Close()
    $d.Close()
}

# 3) Add Debloat.repo and GoogleMessages.apk
$repoPath = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\frsh\META-INF\io\github\freshroms\repository\fresh\Debloat.repo'
$repoBytes = [IO.File]::ReadAllBytes($repoPath)
$repoEntry = $dest.CreateEntry('META-INF/io/github/freshroms/repository/fresh/Debloat.repo', [System.IO.Compression.CompressionLevel]::Optimal)
$repoStream = $repoEntry.Open()
$repoStream.Write($repoBytes, 0, $repoBytes.Length)
$repoStream.Close()

$apkPath = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\GoogleMessages_5.2.062_xxhdpi_arm64.apk'
$apkBytes = [IO.File]::ReadAllBytes($apkPath)
$apkEntry = $dest.CreateEntry('GoogleMessages.apk', [System.IO.Compression.CompressionLevel]::Optimal)
$apkStream = $apkEntry.Open()
$apkStream.Write($apkBytes, 0, $apkBytes.Length)
$apkStream.Close()

$zip.Dispose()
$dest.Dispose()
Write-Output "Done"