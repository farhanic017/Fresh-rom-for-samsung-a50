# Fresh ROM for Samsung Galaxy A50 — SM-A505YN compatible build

The official **Fresh ROM** for the Samsung Galaxy A50 (`FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL`) refuses to install on the Australian/NZ **SM-A505YN**, aborting with:

```
Incompatible model recognised!
This Fresh build is only made for:
  SM-A505FM SM-A505FN SM-A505F SM-A505GT SM-A505GN SM-A505G
```

This repository contains a **surgical, fully verified patch** that whitelists the `SM-A505YN` so the ROM installs normally — plus everything needed to rebuild, verify, and flash it.

> **Why the ROM itself isn't a plain file in this repo:** GitHub rejects files over 100 MB, and the ROM is 4.4 GB. The zip is therefore attached to the [latest release](../../releases/latest) in **3 parts** (each under GitHub's 2 GB release-asset limit). The repo holds the patcher, the verification tooling and the docs.

---

## TL;DR — get the patched ROM

### Option A — download the release (easiest)

1. Grab the 3 parts from the [latest release](../../releases/latest).
2. Put them in one folder and reassemble (see below).
3. Verify the SHA256, then flash per [docs/INSTALL.md](docs/INSTALL.md).

```powershell
# reassemble (PowerShell) — run in the folder containing the 3 parts
cmd /c "copy /b FRSH-A505YN.part1 + FRSH-A505YN.part2 + FRSH-A505YN.part3 FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip"
Get-FileHash .\FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip -Algorithm SHA256
```

Expected: `8039c9d0b3cf2796e9709402f1c12b10957a975e2c68d6ea11fae742517e7a93`

### Option B — rebuild it yourself from the official zip (fully reproducible)

```powershell
# 1. download the OFFICIAL zip from https://freshroms.github.io/ (or use your copy)
# 2. run the whitelist patcher (Windows PowerShell 5.1+, no extra dependencies):
powershell -ExecutionPolicy Bypass -File .\patch\Apply-A505YNPatch.ps1
# 3. rebuild the ROM with system-app removals, Google Messages injector and the assistant choice:
powershell -ExecutionPolicy Bypass -File .\patch\Rebuild-A505YNPatched.ps1
# 4. split the rebuilt ROM into release parts:
powershell -ExecutionPolicy Bypass -File .\patch\Split-A505YNParts.ps1
```

The patcher finds the official zip, patches **one entry** (`package.prop`), and verifies everything. The rebuild step then strips Radio/Chrome/Samsung Messages, injects Google's own Messages APK, and adds the **Gemini / Bixby assistant choice** to the install wizard. It finishes with `Replaced / Added / Output` counts.

---

## What the patch actually changes

The installer runs `fresh-tk device_checker`, which prefix-matches the bootloader string against a whitelist:

```sh
# from META-INF/com/google/android/install-wizard/tools/fresh-tk
supported=($(file_getprop "$PACKAGE_PROP" 'pkg.models'))
for model in "${supported[@]}"; do
    if [[ SM-$(getprop 'ro.boot.bootloader') == "$model"* ]]; then
        file_setprop "$TOOLKIT_PROP" 'device.model' "$model"; break
    else
        file_setprop "$TOOLKIT_PROP" 'device.model' 'fail'
    fi
done
```

Your A505YN reports a bootloader like `A505YNDVS7CWA1` → candidate `SM-A505YNDVS7CWA1…` matches no whitelist prefix → `device.model=fail` → abort.

The patch appends the model to that one line:

```diff
-pkg.models=SM-A505FM SM-A505FN SM-A505F SM-A505GT SM-A505GN SM-A505G
+pkg.models=SM-A505FM SM-A505FN SM-A505F SM-A505GT SM-A505GN SM-A505G SM-A505YN
```

That is still **one part of the change**: `package.prop` grows 535 → 545 bytes.

Since the original release was built, the ROM has also been **debloated**: Radio (FM radio), Chrome, and Samsung Messages are removed via an install-time overlay whiteout (`META-INF/io/github/freshroms/repository/fresh/Debloat.repo`). In their place, Google's original Messages APK (`Pegasus_RC17_xxhdpi.arm64-v8a`, 5.2.062, SHA256 `02231de94dbdffb95d4d77cf5b37d85e3b8d5769c9f51b4a46c3e5ef1ab2f114`) is extracted into `/omr/my_bigball/product/app/GoogleMessages/` post-install so the overlay lowerdir exposes it even on the small A50 `/omr` partition.

## Assistant choice: Google Gemini or Samsung Bixby

The custom-install menu now offers an **assistant pick** (Samsung Bixby — default — or Google Gemini) *before* the Bixby Voice/Vision prompts:

- **Gemini chosen** → the Bixby Voice/Vision pages are not shown at all, Bixby Voice **and** Bixby Vision are removed via the same overlay-whiteout mechanism as the debloat pass, and **Gemini** (`com.google.android.apps.bard`, split-APK bundle: base + arm64/density/English splits, ~5.6 MB) is installed as a system app into `/omr/my_bigball/product/app/Gemini/` with the usual SELinux labels (`u:object_r:system_file:s0`, `0644`).
- **Bixby chosen** → behaviour is unchanged (Voice off / Vision on defaults, prompts shown as before).
- **Express install** keeps Bixby (writes `assistant.prop=1`), so the default one-tap flow is untouched.
- Upgrade/reinstall flashes migrate the previous choice (`fresh-tk migrate_addon_list`) and default to Bixby when no previous choice exists.
- The summary screen shows the selected assistant.

On first boot a small repair hook (`fresh-assist.rc` + `fresh-assist.sh`, shipped through the same `/omr` overlay) checks the secure `voice_interaction_service` setting: if it is unset or still points at the removed Bixby Voice package, it rebinds the system assistant to the Google app — Samsung's standard assistant host. Gemini itself cannot be bound as Android 11's voice-interaction service (the APK declares no `VoiceInteractionService`), so after the flash open **Google app → Settings → Google Assistant → Digital assistants from Google → Gemini** once (this is Google's official flow on every Samsung device). Fallback if the hook cannot run: **Settings → Apps → Choose default apps → Digital assistant app → Google**.

| | |
|---|---|
| Official zip SHA256 | `5651C3E0E5D79CE7BAE28A40BB30B563BB312FE34503E7808628C79C64528A1B` |
| Patched zip SHA256 | `8039c9d0b3cf2796e9709402f1c12b10957a975e2c68d6ea11fae742517e7a93` |
| Size | 4,436,931,507 → 4,463,010,029 bytes (+26,078,522) |
| Entries | 151 → 163, same order, updater-script replaced |
| `package.prop` CRC32 | `0x69429506` (content = central directory = local header) |
| GoogleMessages.apk | 5.2.062 (xxhdpi, arm64), 31,989,783 bytes, SHA256 `02231de94dbdffb95d4d77cf5b37d85e3b8d5769c9f51b4a46c3e5ef1ab2f114` |
| Gemini splits | base 5,351,117 + arm64_v8a/xxhdpi/xhdpi/hdpi/en splits (5,595,950 bytes total, `com.google.android.apps.bard` 1.0.995038376-345, min API 29) |

Full technical write-up, including the NFC investigation: **[docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md)**.

## NFC

NFC works exactly as on the supported SM-A505FN / SM-A505GN — there is **one shared system/vendor/product image for all 7 whitelisted models**, and it contains the complete NFC stack (`NfcNci.apk`, `android.hardware.nfc@1.2-service`, `libnfc-sec-vendor.conf`, …). The A505YN is hardware-based on the A505GN. The debloat pass only removes Radio / Chrome / Samsung Messages and does not touch the NFC stack, so NFC support is untouched. Details in [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md#nfc).

## Flashing

Step-by-step instructions for the SM-A505YN (Odin + TWRP + vbmeta, exactly like the official Fresh install flow): **[docs/INSTALL.md](docs/INSTALL.md)**.

> ⚠️ Unlocking the bootloader **erases the phone**. Back up first. This is unofficial software; flashing is at your own risk.

## Repository layout

```
patch/
  Apply-A505YNPatch.ps1   # one-command: patch official zip + full verification
  Rebuild-A505YNPatched.ps1  # debloat + Google Messages + Gemini/Bixby assistant choice (generalized replace/add table)
  Split-A505YNParts.ps1   # split the rebuilt ROM into 3 release parts
  ZipPatcher.cs           # surgical ZIP64-aware single-entry zip patcher (compiled by the script)
  package.prop.original   # the entry as shipped (535 bytes)
  package.prop.patched    # the entry after patch (545 bytes)
  Debloat.repo            # overlay whiteout paths for the debloat pass
docs/
  HOW-IT-WORKS.md         # reverse-engineering notes, verification results, NFC findings
  INSTALL.md              # flash guide for SM-A505YN
CHECKSUMS.txt             # SHA256 of everything, official and patched
```

## Credits & disclaimer

- The ROM itself is **Fresh ROM** by the [FreshROMs](https://github.com/FreshROMs) project — get official builds and their install wiki at [freshroms.github.io](https://freshroms.github.io/). This repo is an unofficial, community patch for one extra model and is not affiliated with the Fresh project.
- Flashing custom software voids warranties and can brick your device. Proceed at your own risk.
