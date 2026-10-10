# Bundled SNES emulator modules

These are console ARM modules for a genuine SNES Mini/Super Famicom running
compatible Hakchi firmware. They do not run on the Linux host. No ROMs or BIOS
files are included.

| Module | Purpose |
| --- | --- |
| `_km_retroarch_1910_rgui.hmod` | KMFD RetroArch 1.9.10 with RGUI and Classic launchers |
| `_km_snes9x_xtreme.hmod` | KMFD Snes9x cores and launchers, including `/bin/snes9x` |

Both packages come from [KMFDManic's upstream module repository](https://github.com/KMFDManic/NESC-SNESC-Modifications/tree/73587ffc2371d24ca1499f24b0be17dfe239209f),
at commit `73587ffc2371d24ca1499f24b0be17dfe239209f`. `manifest.json` records
their original directory names and each vendored archive's SHA-256.
The upstream contents, including scripts and readmes, are unchanged. Each archive
contains its `.hmod` directory's contents at the archive root. Original module
names are retained for Hakchi's installed-module tracking.

CLI builds and AppImages include these files locally. Select **Install RetroArch + Snes9x** in the desktop, or run:

```sh
hakchi --host CONSOLE_IP snes-modules-install --yes
```

This checks both local hashes before connecting, installs RetroArch first and
Snes9x second, then checks the console's executable launchers and Snes9x core.
Set up Hakchi and save your stock-kernel backup first. Installation replaces
these modules' emulator files and default configuration. If installation fails,
successfully installed modules are retained; correct the error and retry.
Use `snes9x` for ordinary `.sfc`/`.smc` games. Specialty Satellaview and Sufami
Turbo games may need user-supplied BIOS files; see the upstream readme.

To reproduce the archives from a checkout of that exact upstream commit:

```sh
LC_ALL=C tar --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner \
  -C UPSTREAM/_km_retroarch_1910_rgui.hmod -cf - . | gzip -n -9 > _km_retroarch_1910_rgui.hmod
LC_ALL=C tar --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner \
  -C UPSTREAM/_km_snes9x_xtreme.hmod -cf - . | gzip -n -9 > _km_snes9x_xtreme.hmod
```

Credits: KMFDManic, madmonkey, the RetroArch/Libretro contributors and the Snes9x
Team. Keep the upstream readmes and `licenses/` notices when redistributing.
The upstream module repository is LGPL-3.0; included components retain their own
licenses. RetroArch is GPL-3.0, and Snes9x has a noncommercial license; this
repository's GPL license does not replace those terms.
Notices come from [KMFDManic](https://github.com/KMFDManic/NESC-SNESC-Modifications/blob/73587ffc2371d24ca1499f24b0be17dfe239209f/LICENSE),
[RetroArch 1.9.10](https://github.com/libretro/RetroArch/blob/v1.9.10/COPYING)
and [Snes9x](https://github.com/libretro/snes9x/blob/master/LICENSE).
Upstream emulator sources include [RetroArch](https://github.com/libretro/RetroArch),
[Snes9x](https://github.com/libretro/snes9x),
[Snes9x 2005](https://github.com/libretro/snes9x2005) and
[Snes9x 2010](https://github.com/libretro/snes9x2010).
