# ps2sdk APA hard disk driver

The PS2 hard disk driver that Loadout Bay loads, built as `loadout-hdd.irx`.

- Source: [ps2sdk](https://github.com/ps2dev/ps2sdk) tag `v2.0.0` (commit `b12f8af37bd42ec13b1bafb7ab6e7bdcfb4b683b`), the same ps2sdk as the `ps2dev/ps2dev:v2.0.0` image that builds Loadout Bay.
- Files: `iop/hdd/apa/src/*` and `iop/hdd/libapa/src/*.c` and `iop/hdd/libapa/include/libapa.h`, copied into `src/` unchanged apart from line endings.
- Licence: AFL-2.0, in `LICENSE` (ps2sdk's licence file).

The build differs from ps2sdk's own `ps2hdd.irx` by two compile switches, set in the `Makefile`:

- `APA_SUPPORT_HDL` lets the driver create partitions of the HDLoader type (`0x1337`).
- `APA_SUPPORT_IOCTL_GETPARTSTART` lets the driver return each part's first sector on the disk, which the game header records.

HDLGameInstaller uses a driver with the same two features to lay out its game partitions. Loadout Bay installs games in that layout, so Open PS2 Loader and HDLGameInstaller can both read them.
