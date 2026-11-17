# ps2sdk APA hard disk driver

The PS2 hard disk driver that Loadout Bay loads, built as `loadout-hdd.irx`.

- Source: [ps2sdk](https://github.com/ps2dev/ps2sdk) commit `47f6b2e276cba202999d3f24081f56c844ee30aa` (2026-11-16), the same ps2sdk as the `ps2dev/ps2dev` image that builds Loadout Bay (the image of that day, pinned by its digest in CI).
- Files: `iop/hdd/apa/src/*` and `iop/hdd/libapa/src/*.c` and `iop/hdd/libapa/include/libapa.h`, copied into `src/` unchanged apart from line endings.
- Licence: AFL-2.0, in `LICENSE` (ps2sdk's licence file).

The build differs from ps2sdk's own `ps2hdd.irx` by two compile switches, set in the `Makefile`:

- `APA_SUPPORT_HDL` lets the driver create partitions of the HDLoader type (`0x1337`).
- `APA_SUPPORT_IOCTL_GETPARTSTART` lets the driver return each part's first sector on the disk, which the game header records.

HDLGameInstaller uses a driver with the same two features to lay out its game partitions. Loadout Bay installs games in that layout, so Open PS2 Loader and HDLGameInstaller can both read them.
