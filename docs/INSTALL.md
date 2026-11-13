# Install guide — Fresh ROM on Samsung SM-A505YN

This is the standard Fresh ROM install flow (from the
[official Fresh wiki](https://freshroms.github.io/wiki/docs/installing-a-custom-firmware/installing-fresh/))
adapted for the **SM-A505YN** using the patched zip from this repo.

> ⚠️ **Warnings**
> * Unlocking the bootloader **erases the entire phone**. Back up photos/contacts/apps first.
> * Charge the phone to at least 50 %.
> * Use the files for the **A50 (a50)** only — this ROM is for the Galaxy A50 family.
> * Flashing is at your own risk. If anything fails, stock firmware can be restored with Odin (SamFW).

## What you need

| file | purpose |
|---|---|
| `FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip` | the patched ROM (this repo's release, or built with `patch/Apply-A505YNPatch.ps1`) |
| `Odin3_v3.13.1.exe` (or 3.14.1) | Samsung flash tool |
| Samsung USB drivers | PC ↔ phone |
| `twrp-3.7.0_11-0-a505f.img.tar` **or** `SHRP_a50.tar` | custom recovery |
| `vbmeta.tar` | verification-disabled vbmeta (flashed via Odin USERDATA) |
| `multidisabler-samsung-3.1.zip` *(optional)* | prevents forced encryption, recommended |

Put the **ROM zip on an SD card or USB-OTG** (the phone's internal storage is
wiped during install, so don't leave it only on internal memory).

## Step 1 — Enable OEM unlocking

1. `Settings → About phone → Software information` → tap **Build number** 7× to enable Developer options.
2. `Settings → Developer options` → enable **OEM unlocking** (and **USB debugging** for convenience).

If OEM unlocking is greyed out, the phone must be connected to the internet
once (Wi-Fi) and/or you need to sign in — then retry.

## Step 2 — Unlock the bootloader

1. Power the phone off.
2. Hold **Volume Down + Power** to enter Download mode, then read the warning screen.
3. Press **Volume Up** to unlock (this **wipes the device**).
4. The phone factory-resets and reboots. Complete the initial setup far enough
   to re-enable **Developer options → OEM unlocking** (it must show ON/toggleable).

## Step 3 — Flash recovery + vbmeta with Odin

1. Open **Odin3** on the PC (run as administrator).
2. Connect the phone in Download mode (Volume Down + Power, plug in USB).
3. In Odin:
   * **AP** = `twrp-3.7.0_11-0-a505f.img.tar` (or `SHRP_a50.tar`)
   * **USERDATA** = `vbmeta.tar`  ← this disables verification/dm-verity
   * leave everything else default (Auto Reboot **on** is fine; if the phone
     boots to system instead of recovery afterwards, re-run with Auto Reboot off
     and hold Vol Up + Power as soon as it restarts)
4. Press **Start** and wait for **PASS**.

## Step 4 — Format data

1. The phone should now boot into TWRP/SHRP. If it rebooted to system, power
   off and boot recovery: **Volume Up + Power** from powered-off state.
2. In recovery: **Wipe → Format Data → type `yes` → confirm**.
   (This is mandatory — the official wiki requires it.)
3. Optional but recommended: **Install → select `multidisabler-samsung-3.1.zip` → swipe to flash**.

## Step 5 — Flash the patched ROM

1. In recovery: **Install**.
2. Tap **Select Storage** → Micro SD card / USB-OTG.
3. Choose `FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-A505YN-signed.zip` → **swipe to confirm flash**.
4. The Fresh install wizard starts. At the compatibility step you should see:

   ```
   - SM-A505YN recognised.
   ```

   instead of `Incompatible model recognised!`. The wizard then runs the
   partition check and walks you through language/options — accept the defaults
   unless you know you want otherwise.
5. When it finishes, choose **Reboot system**.

First boot takes several minutes (the first boot after a clean flash includes
dex-opt/setup). If it takes more than ~15 minutes, boot to recovery and reflash
per the troubleshooting section.

## Troubleshooting

| symptom | fix |
|---|---|
| `Incompatible model recognised!` | You flashed the **unpatched** zip. Confirm the file name ends with `-A505YN-signed.zip` and its SHA256 is `17DFA6F0…A36D3C`. |
| Odin **FAIL** on AP | Re-download the recovery tar; try the other Odin version (3.13.1 vs 3.14.1-3B-PatcheD); try another USB port/cable (USB 2.0 port, short cable). |
| Phone boots to system instead of recovery | Re-flash with **Auto Reboot unchecked**; as soon as Odin says PASS, hold **Vol Up + Power** until the recovery logo appears. |
| Bootloop / stuck on logo | Boot recovery → Format Data `yes` → reflash the ROM zip. |
| `part.check=fail` in the wizard | A required partition wasn't found — this shouldn't happen on A505YN (same `super` layout); re-run after formatting data. |
| Want stock back | Flash official A505YN firmware (`A505YNDVS7CWA1`) with Odin (BL/AP/CP/CSC). |

## After installing

* `Settings → About phone` may show **SM-A505F** — expected. The ROM is one
  shared build for all supported A50 models; the installer gate is the only
  place the model matters (see [HOW-IT-WORKS.md](HOW-IT-WORKS.md#5-nfc)).
* NFC, radios, camera etc. behave exactly as on the supported SM-A505FN/GN —
  same image bytes.
* OTA updates from within the ROM are not used for this patched build — to
  update, grab the next official build and re-run `patch/Apply-A505YNPatch.ps1`.
