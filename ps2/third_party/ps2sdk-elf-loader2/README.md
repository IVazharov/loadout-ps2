# ps2sdk ELF loader 2, buffer functions

The declarations of ps2sdk's `libelf-loader2` that start an ELF from a buffer in memory. Loadout Bay uses them to start a new version of itself that Loadout sent over the network. The library is ps2sdk's own and unchanged; only its public header leaves these functions out.

- Source: [ps2sdk](https://github.com/ps2dev/ps2sdk) tag `v2.0.0` (commit `b12f8af37bd42ec13b1bafb7ab6e7bdcfb4b683b`), the same ps2sdk as the `ps2dev/ps2dev:v2.0.0` image that builds Loadout Bay.
- File: `ee/elf-loader2/src/include/elf_loader_common.h`, unchanged.
- Licence: AFL-2.0, in `LICENSE` (ps2sdk's licence file).

`elf_loader_exec_elf_prepare_loadinfo` checks the ELF and lists the moves that put its segments in place, `elf_loader_exec_elf_prepare_arginfo` stores its arguments, and `elf_loader_exec_elf` copies a small loader to 0x84000 and starts it with `ExecPS2`; the loader makes the moves and jumps to the ELF. As in ps2sdk's own `LoadELFFromFile`, the IOP is reset first.
