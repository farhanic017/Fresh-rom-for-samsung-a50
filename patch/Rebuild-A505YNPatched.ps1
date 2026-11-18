Add-Type -Path 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.dll'
Add-Type -Path 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression.FileSystem\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.FileSystem.dll'

$src   = 'D:\DOWNLOADS\FRESH ROM\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip'
$dst   = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed-MODIFIED.zip'
$frsh  = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\frsh'
$iw    = Join-Path $frsh 'META-INF\com\google\android\install-wizard'
$gemini= 'C:\Users\FARHAN\AppData\Local\Temp\opencode\gemini'

# Existing ZIP entries whose content is replaced by local (patched) files
$replacements = [ordered]@{
  'META-INF/com/google/android/updater-script'                                = "$frsh\META-INF\com\google\android\updater-script"
  'META-INF/com/google/android/install-wizard/modules/install_custom.edify'   = "$iw\modules\install_custom.edify"
  'META-INF/com/google/android/install-wizard/modules/install_express.edify'  = "$iw\modules\install_express.edify"
  'META-INF/com/google/android/install-wizard/tools/fresh-tk'                 = "$iw\tools\fresh-tk"
  'META-INF/com/google/android/install-wizard/tools/make-summary'             = "$iw\tools\make-summary"
  'META-INF/com/google/android/install-wizard/strings/en.lang'                = "$iw\strings\en.lang"
  'META-INF/com/google/android/install-wizard/strings/de.lang'                = "$iw\strings\de.lang"
  'META-INF/com/google/android/install-wizard/strings/fa_en.lang'             = "$iw\strings\fa_en.lang"
  'META-INF/com/google/android/install-wizard/strings/fi.lang'                = "$iw\strings\fi.lang"
  'META-INF/com/google/android/install-wizard/strings/fr.lang'                = "$iw\strings\fr.lang"
  'META-INF/com/google/android/install-wizard/strings/it.lang'                = "$iw\strings\it.lang"
  'META-INF/com/google/android/install-wizard/strings/ru.lang'                = "$iw\strings\ru.lang"
  'META-INF/com/google/android/install-wizard/strings/tr.lang'                = "$iw\strings\tr.lang"
  'META-INF/io/github/freshroms/repository/fresh/Debloat.repo'               = "$frsh\META-INF\io\github\freshroms\repository\fresh\Debloat.repo"
  'GoogleMessages.apk'                                                       = 'C:\Users\FARHAN\AppData\Local\Temp\opencode\GoogleMessages_5.2.062_xxhdpi_arm64.apk'
}

# New ZIP entries
$additions = [ordered]@{
  'META-INF/com/google/android/install-wizard/modules/addon_assistant.edify'  = "$iw\modules\addon_assistant.edify"
  'META-INF/com/google/android/install-wizard/themes/fresh/icon.assistant.png' = "$iw\themes\fresh\icon.assistant.png"
  'fresh-assist.rc'                                                           = "$frsh\fresh-assist.rc"
  'fresh-assist.sh'                                                           = "$frsh\fresh-assist.sh"
  'gemini/base.apk'                          = "$gemini\base.apk"
  'gemini/split_config.arm64_v8a.apk'        = "$gemini\split_config.arm64_v8a.apk"
  'gemini/split_config.xxhdpi.apk'           = "$gemini\split_config.xxhdpi.apk"
  'gemini/split_config.xhdpi.apk'            = "$gemini\split_config.xhdpi.apk"
  'gemini/split_config.hdpi.apk'             = "$gemini\split_config.hdpi.apk"
  'gemini/split_config.en.apk'               = "$gemini\split_config.en.apk"
}

# --- preflight ---
$errors = @()
foreach ($p in ($replacements.Values + $additions.Values)) {
  if (-not (Test-Path -LiteralPath $p)) { $errors += "MISSING LOCAL FILE: $p" }
}
if ($errors) { $errors | ForEach-Object { Write-Output $_ }; throw 'Preflight failed.' }

$zip = [System.IO.Compression.ZipFile]::OpenRead($src)
$srcNames = @{}
foreach ($e in $zip.Entries) { $srcNames[$e.FullName] = $true }
foreach ($k in $replacements.Keys) {
  if (-not $srcNames.ContainsKey($k)) { $zip.Dispose(); throw "REPLACEMENT NOT IN SRC ZIP: $k" }
}
# additions: refresh in place when the entry already exists (idempotent rebuilds),
# append otherwise
$toAppend = @()
foreach ($k in $additions.Keys) {
  if ($srcNames.ContainsKey($k)) { $toAppend += $null } else { $toAppend += $k }
}
$toAppend = @($toAppend | Where-Object { $_ })

if (Test-Path $dst) { Remove-Item $dst -Force }
$dest = [System.IO.Compression.ZipFile]::Open($dst, [System.IO.Compression.ZipArchiveMode]::Create)

$replacedCount = 0
$refreshedCount = 0
foreach ($entry in $zip.Entries) {
  $newEntry = $dest.CreateEntry($entry.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
  $d = $newEntry.Open()
  if ($replacements.Contains($entry.FullName)) {
    $bytes = [IO.File]::ReadAllBytes($replacements[$entry.FullName])
    $d.Write($bytes, 0, $bytes.Length)
    $replacedCount++
  } elseif ($additions.Contains($entry.FullName)) {
    $bytes = [IO.File]::ReadAllBytes($additions[$entry.FullName])
    $d.Write($bytes, 0, $bytes.Length)
    $refreshedCount++
  } else {
    $s = $entry.Open()
    $buffer = New-Object byte[] (4MB)
    while ($true) {
      $read = $s.Read($buffer, 0, $buffer.Length)
      if ($read -le 0) { break }
      $d.Write($buffer, 0, $read)
    }
    $s.Close()
  }
  $d.Close()
}

$addedCount = 0
foreach ($k in $toAppend) {
  $newEntry = $dest.CreateEntry($k, [System.IO.Compression.CompressionLevel]::Optimal)
  $d = $newEntry.Open()
  $bytes = [IO.File]::ReadAllBytes($additions[$k])
  $d.Write($bytes, 0, $bytes.Length)
  $d.Close()
  $addedCount++
}

$zip.Dispose()
$dest.Dispose()

$fi = Get-Item $dst
Write-Output "Replaced: $replacedCount / $($replacements.Count)"
Write-Output "Refreshed additions: $refreshedCount"
Write-Output "Added:    $addedCount (new entries)"
Write-Output "Output:   $($fi.FullName) ($($fi.Length) bytes)"
