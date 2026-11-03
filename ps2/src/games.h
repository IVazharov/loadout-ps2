// Reads the HDLoader game partitions on the hard disk. Nothing is written here:
// partitions are opened read-only, and the header is passed on as it is.

#ifndef LOADOUT_GAMES_H
#define LOADOUT_GAMES_H

#include <tamtypes.h>

// An entry is the partition name, NUL-padded, then the game's 1024-byte HDLoader header.
#define GAMES_NAME_BYTES 40
#define GAMES_HEADER_BYTES 1024
#define GAMES_ENTRY_BYTES (GAMES_NAME_BYTES + GAMES_HEADER_BYTES)
// The last byte of the name field holds flags; a name is at most 32 bytes.
#define GAMES_ENTRY_FLAGS 39
// The header could not be read, so the zeros sent in its place say nothing.
#define GAMES_FLAG_UNREADABLE 0x1

// Fills up to max entries, starting with game number first in the disk's
// partition order. Sets the total number of games and the number of entries
// written. A header that cannot be read is sent as zeros, with
// GAMES_FLAG_UNREADABLE set. Returns 0 or a negative errno.
int games_list(u32 first, u32 max, u8 *out, u32 *total, u32 *count);

// Fills one entry for the game partition with this name. Returns 0, -ENOENT
// when no game partition has that name, or another negative errno.
int games_read(const char *name, u8 *out);

// Checks that a new game can take this partition name: no partition has the
// name, and no game partition belongs to the same disc ID, whole or not.
// Returns 0, -EEXIST, or another negative errno.
int games_check_new(const char *name, const char *disc_id);

#endif
