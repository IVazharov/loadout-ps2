// How a game is split over APA partitions. The result is the layout Open PS2
// Loader reads: a main partition and sub-partitions, each listed in the header.
// Pure arithmetic, with no PS2 calls, so it can be checked on a PC.

#ifndef LOADOUT_LAYOUT_H
#define LOADOUT_LAYOUT_H

// APA partition sizes a game part can take: 128 MB up to 4 GB. Above 4 GB a
// part's byte count would overflow the header's 32-bit size field.
#define LAYOUT_SIZES 6

// Picks the size for the next part of a game: the size number, from 0 (128 MB)
// to LAYOUT_SIZES - 1 (4 GB). sectors_left counts the game's 2048-byte sectors
// not yet placed, part is 0 for the main partition, and max_part_sectors is the
// largest partition the disk allows (HDIOC_MAXSECTOR), in 512-byte sectors.
int layout_choose(unsigned int sectors_left, unsigned int max_part_sectors, int part);

// The size's name in an APA create path or HIOCADDSUB, such as "512M".
const char *layout_size_name(int size);

// Game sectors (2048 bytes) a part of this size holds, after its reserved area.
unsigned int layout_capacity(int size, int part);

// The 512-byte sectors at the start of a part that hold no game data: 4 MB in
// the main partition, and one 2048-byte sector in a sub-partition.
unsigned int layout_reserved_sectors(int part);

#endif
