# How it works — why the official ROM rejects the SM-A505YN, and what the patch does

This is the full reverse-engineering record for the patch shipped in this repo.
Target build: `FRSH-UP1A_a50_6.5.3.03R_25080301_OFFICIAL-signed.zip`
(4,436,931,507 bytes, ZIP64, 151 entries, SHA256 `5651C3E0…528A1B`).

## 1. The device gate

The zip is an AOSP-style OTA with an AROMA-style install wizard:

* `META-INF/com/google/android/update-binary` — AOSP updater binary
* `META-INF/com/google/android/updater-script` — edify script
* `META-INF/com/google/android/install-wizard-binary` + `install-wizard-config` — the interactive wizard
* `META-INF/com/google/android/install-wizard/tools/fresh-tk` — shell toolkit invoked by the edify script

The compatibility check happens at the very start of `updater-script`:

```edify
### Compatibility check
ui_print("- " + file_getprop("/tmp/lang.prop", "script.check.compatibility"));
run_program("/tmp/tools/fresh-tk", "device_checker");
run_program("/tmp/tools/fresh-tk", "part_checker");

ifelse(
  file_getprop("/tmp/toolkit.prop", "device.model") == "fail", (
    ui_print("   - " + file_getprop("/tmp/lang.prop", "script.check.device.fail1"));
    ui_print("     " + file_getprop("/tmp/lang.prop", "script.check.device.fail2"));
    ui_print("     " + file_getprop("/tmp/package.prop", "pkg.models"));
    sleep(4);
    abort(" ");
   ), (
    ui_print("   - " + file_getprop("/tmp/toolkit.prop", "device.model") + " " + file_getprop("/tmp/lang.prop", "script.check.device.success"));
   )
);
```

`device_checker` in `fresh-tk`:

```sh
device_checker() {
    local supported model
    supported=($(file_getprop "$PACKAGE_PROP" 'pkg.models'))

    for model in "${supported[@]}"; do
        if [[ SM-$(getprop 'ro.boot.bootloader') == "$model"* ]]; then
            file_setprop "$TOOLKIT_PROP" 'device.model' "$model"
            break
        else
            file_setprop "$TOOLKIT_PROP" 'device.model' 'fail'
        fi
    done
    return 0
}
```

### Why the A505YN fails

`ro.boot.bootloader` on the SM-A505YN starts with `A505YN` (e.g. `A505YNDVS7CWA1`,
the only official firmware for the device — Android 11, binary U7, region XSA).
The script builds the candidate string `SM-A505YNDVS7CWA1…` and prefix-matches it
against each whitelist entry:

| whitelist entry | candidate starts with it? |
|---|---|
| SM-A505FM | no (`SM-A505YN…` ≠ `SM-A505FM…`) |
| SM-A505FN | no |
| SM-A505F  | no |
| SM-A505GT | no |
| SM-A505GN | no |
| SM-A505G  | no |

No match → `device.model=fail` → *"Incompatible model recognised!"*, and the
script prints `pkg.models` dynamically from `package.prop` (so no language-file
edit is needed for a new model — the failure text self-updates).

### What is NOT gated

* **No other file in the 151-entry zip references model names.** `package.prop`
  (`pkg.models`) and `fresh-tk` (which reads it) are the only places.
* `part_checker` only verifies that required partitions exist under
  `/dev/block` (`system vendor product boot recovery up_param dtb …`). The A505YN
  has the same dynamic-partition layout as the supported models (all A50
  variants share it), so it passes unchanged.
* `install-wizard-binary`, `update-binary`, edify modules and the language files
  contain no model logic.
* The zip carries no OTA signature entries (`CERT.SF`/`META-INF/*.RSA` are
  absent; only `META-INF/com/android/otacert` exists), so changing one entry
  cannot invalidate a signature the installer would check.

## 2. The patch

One line in `package.prop` gets ` SM-A505YN` appended:

```diff
-pkg.models=SM-A505FM SM-A505FN SM-A505F SM-A505GT SM-A505GN SM-A505G
+pkg.models=SM-A505FM SM-A505FN SM-A505F SM-A505GT SM-A505GN SM-A505G SM-A505YN
```

Because the check is a **prefix match**, adding the new model cannot affect the
existing six: each existing bootloader candidate still matches exactly the same
entry as before (verified by simulation — see §3). `A515F…` (Galaxy A51) and
`A705F…` (Galaxy A70) still correctly fail.

### Why a surgical patcher instead of a normal repack

A repack (extract → modify → re-zip) would recompress **all 151 entries**,
changing every CRC/compressed size, taking tens of minutes and risking subtle
incompatibilities with the wizard's expectations (the entries use data
descriptors, `flags=0x0808`, and the archive is ZIP64). Instead,
`patch/ZipPatcher.cs`:

1. Locates the EOCD, the ZIP64 EOCD and the ZIP64 locator.
2. Parses all 151 central-directory records (incl. ZIP64 extended fields).
3. Rebuilds only the `package.prop` local record (deflate recompress of 545
   bytes: 230 → 235 bytes compressed, local record 288 → 293, **delta = +5**).
4. Rewrites that entry's CRC32/sizes in the central directory.
5. Shifts the local-header offsets of the 145 entries that sit *after* it by +5.
6. Copies **every other byte verbatim**: everything before the patched entry,
   and everything from the end of the patched record to the old central directory.
7. Updates the central-directory offset in the ZIP64 EOCD **and** the ZIP64
   locator's pointer to the ZIP64 EOCD (both shift by +5), plus the classic
   EOCD's CD offset when it isn't saturated.

A bug in step 7 (forgetting the locator) was caught by verification — the first
build failed `ZipFile::OpenRead` with *"Zip 64 End of Central Directory Record
not where indicated"* and was fixed. This is why the script re-opens and
re-hashes the result rather than trusting the writer.

## 3. Verification results (all automated in `Apply-A505YNPatch.ps1`)

Run on the final artifact:

| check | result |
|---|---|
| Both zips open with `System.IO.Compression` (ZIP64 structure valid) | PASS |
| Entry count 151 → 151, same names, same order | PASS |
| Per-entry SHA256 of the other **150 entries identical** | PASS |
| `package.prop` content = expected patched text (545 bytes) | PASS |
| CRC32 `0x69429506` consistent in content = central directory = local header | PASS |
| Size 4,436,931,507 → 4,436,931,512 (+5) | PASS |

`device_checker` simulation (same semantics as the shell code):

| `ro.boot.bootloader` | result |
|---|---|
| `A505YNDVS7CWA1` (A505YN, latest official) | **SM-A505YN** ✅ |
| `A505YNDU1ASD1` (A505YN, earlier) | **SM-A505YN** ✅ |
| `A505FNXXU6CSI2` | SM-A505FN ✅ (regression) |
| `A505GNXU3BSG1` | SM-A505GN ✅ (regression) |
| `A505FXXU9BWF1` | SM-A505F ✅ (regression) |
| `A505GTXXU7BSF1` | SM-A505GT ✅ (regression) |
| `A515FDXU8GVE2` (Galaxy A51) | fail ✅ (must stay rejected) |
| `A705FNXXU6CUJ1` (Galaxy A70) | fail ✅ (must stay rejected) |

The patcher is **deterministic**: rebuilding from the same official zip
reproduces the exact same patched SHA256
(`17DFA6F050D7AFC0B596BEA224E5EEAA16C4572378AEEC724A41796C06A36D3C`).

## 4. Research notes on the SM-A505YN

* **SM-A505YN** = Samsung Galaxy A50, Australia/New Zealand variant (carriers
  AU/NZ; sold as SM-A505YN).
* It is **based on the SM-A505GN** (Southeast Asia/international dual-SIM
  variant) — same Exynos 9610 platform, same board family (`a50`), same dynamic
  partition layout. (phonedb.net device id 15697.)
* Its only/latest firmware is `A505YNDVS7CWA1` — Android 11 (R), 2023-01-01
  security patch, binary **U7** (samfw.com, region XSA). Binary U7 means it
  ships the same dynamic-partition (`super`) layout the Fresh build targets for
  the other A50 models — which is why `part_checker` passes and why a
  TWRP/vbmeta flash flow works identically.
* Fresh project: [github.com/FreshROMs](https://github.com/FreshROMs),
  downloads & wiki: [freshroms.github.io](https://freshroms.github.io/)
  (supported device list says "Samsung Galaxy A50 (International)" — the model
  whitelist in `package.prop` is what "International" concretely means).

## 5. NFC

Question this section answers: *"the YN has NFC — does using the shared images
(FN/GN) keep NFC?"* — **Yes, nothing about NFC is touched by the patch.**

Evidence gathered by decoding `system.new.dat.br` (3.65 GB) and
`product.new.dat.br` / `vendor.new.dat.br` from the **patched** zip (brotli →
raw EROFS image, then a byte-level scan of the images):

* **There are no per-model images.** The zip contains exactly one `system`,
  one `vendor` and one `product` image, shared by all seven whitelisted models
  — including the NFC-equipped SM-A505FN and SM-A505GN. The "GN/FN variant"
  the user might select does not exist; every supported A50 gets identical
  bytes.
* **The NFC stack is present in the shared images:**
  * system: `NfcNci.apk`, `com.android.nfc_extras.jar`, `NfcService`,
    `com.samsung.android.nfc.t4t.jar`
  * vendor: `android.hardware.nfc@1.2-service`, `libnfc-sec-vendor.conf`
* **Baked identity of the shared build** (from the system image's build props):
  `ro.product.system.model=SM-A505F`, `ro.product.system.name=a50dd`,
  built `Sat Aug 03 2025`, Android 14 (SDK 34, `UP1A.231005.007`). This is the
  normal situation for this ROM: every supported model boots this same image,
  so `Settings → About phone` may show `SM-A505F` rather than `SM-A505YN`.
  That is cosmetic — the model gate exists only at install time.
* **Hardware:** the A505YN is an A505GN-based device with NFC hardware; since
  the ROM is byte-identical to what GN/FN users run, NFC behaves the same.

Conclusion: the patch is whitelist-only (`+10 bytes` in `package.prop`); the
NFC stack, firmware identity and every partition image are exactly what the
already-supported NFC models receive.

## 6. Known limitations

* The system image reports `SM-A505F` as the product model (see §5) — cosmetic.
* This patch was authored and verified against build `25080301` only. A newer
  Fresh build will have a different `package.prop`/layout; the script warns if
  the source SHA256 doesn't match, though the patch logic is generic.
* No signature/OTA verification exists in this zip, so no re-signing is needed
  (and none is performed).
