# MAME's CHD format

Loadout reads and writes chd files with code in `src/Loadout.Core/Images/Chd`, partly ported from and partly written after these files of [MAME](https://github.com/mamedev/mame), which are under the BSD 3-Clause licence: `src/lib/util/chd.cpp`, `chdcodec.cpp`, `huffman.cpp` and `cdrom.cpp`, and `src/tools/chdman.cpp` for the files it writes and how it writes them.

- Licence: BSD-3-Clause, in `LICENSE`, which also covers those parts of Loadout's code. MAME as a whole is under the GPL, and no other part of it is used.
- The LZMA decoder follows the LZMA specification by Igor Pavlov, which is in the public domain. The LZMA encoder is ported from his LZMA SDK: see `../LzmaSdk`.
- The deflate decoder is written after puff: see `../Puff`.
- The FLAC decoder is written from the FLAC format's specification.
