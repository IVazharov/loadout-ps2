// OPL's own files on the hard disk: the ART, CFG, VMC, and APPS/Loadout-Bay
// folders in the partition Open PS2 Loader keeps them in. Loadout sends
// artwork, game settings, virtual memory cards, and Loadout Bay itself here.
//
// The partition is found the way OPL finds it: hdd0:__common/OPL/conf_hdd.cfg
// names it in hdd_partition, and without that file it is +OPL. A name that
// starts with '+' holds OPL's folders at its root; any other name holds them in
// an OPL folder. The partition is mounted for the first file command and
// unmounted by files_close. docs/bay-protocol.md describes the commands.

#ifndef LOADOUT_FILES_H
#define LOADOUT_FILES_H

#include <tamtypes.h>

#define FILES_FOLDER_ART 0
#define FILES_FOLDER_CFG 1
// Virtual memory cards: only names ending in .bin, and a commit never replaces a card.
#define FILES_FOLDER_VMC 2
// Loadout Bay itself, as an OPL app: APPS/Loadout-Bay with title.cfg and the ELF.
#define FILES_FOLDER_APP 3

// A file name: letters, digits, '_', '.' and '-', 1 to 64 bytes, in a 68-byte NUL-padded field.
#define FILES_NAME_MAX 64
#define FILES_NAME_BYTES 68

// The most bytes one FILE_READ or FILE_WRITE carries.
#define FILES_CHUNK_MAX (64 * 1024)

// One FILE_LIST entry: the name field, then the size in bytes.
#define FILES_ENTRY_BYTES (FILES_NAME_BYTES + 4)
#define FILES_LIST_PAGE 512

// Fills out with up to FILES_LIST_PAGE entries for the regular files in the
// folder, from the first-th on. Returns 0 or a negative errno.
int files_list(u32 folder, u32 first, u8 *out, u32 *total, u32 *count);

// Reads up to length bytes (at most FILES_CHUNK_MAX) of a file from offset on.
// Fewer bytes than asked means the file ends there. Returns the bytes read or a
// negative errno.
int files_read(u32 folder, const char *name, u32 offset, u32 length, u8 *out);

// Writes a chunk into <name>.part at offset, which must be the .part file's
// size so far; offset 0 starts it again. Returns 0 or a negative errno.
int files_write(u32 folder, const char *name, u32 offset, const u8 *data, u32 length);

// When <name>.part holds exactly size bytes, puts it in place of the file.
// Returns 0, -EPIPE when the size differs, -EEXIST for a card already in the
// VMC folder, or a negative errno.
int files_commit(u32 folder, const char *name, u32 size);

// The number of block runs the file's PFS inode lists, its own block included.
// OPL refuses a VMC as fragmented when this is 11 or more. Returns 0 or a
// negative errno.
int files_runs(u32 folder, const char *name, u32 *runs);

// Deletes a file. Returns 0, -ENOENT, or a negative errno.
int files_delete(u32 folder, const char *name);

// Unmounts OPL's partition, if a file command mounted it.
void files_close(void);

// Mounts a hard disk partition at pfs0:, closing OPL's first, so a path on it
// reads "pfs0:<path>". files_close unmounts it. Returns 0 or a negative errno.
int files_mount_partition(const char *partition, int writable);

#endif
