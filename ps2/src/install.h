// Installs a game from the PC onto the hard disk, in the layout HDLGameInstaller
// uses, so Open PS2 Loader and HDLGameInstaller both list and boot it.
//
// INSTALL_BEGIN creates the partitions, the game's sectors arrive on the data
// port, and INSTALL_END writes the game's header once every sector is on the
// disk. A copy that stops early has no header, so it is never taken for a whole
// game. docs/bay-protocol.md describes the commands and the data connection.

#ifndef LOADOUT_INSTALL_H
#define LOADOUT_INSTALL_H

#include <ps2ip.h>
#include <tamtypes.h>

#include "status.h"

#define INSTALL_DATA_PORT 45061

// INSTALL_BEGIN flags: read the game back and compare it before the header is written.
#define INSTALL_FLAG_CHECK 0x1u

// The check compares one CRC-32C per MiB of the game, so it covers games up to 16 GiB.
#define INSTALL_CHECK_BLOCK_BYTES (1024u * 1024u)
#define INSTALL_MAX_CHECKSUMS 16384

#define INSTALL_DISC_ID_BYTES 16
#define INSTALL_STARTUP_BYTES 60
#define INSTALL_TITLE_BYTES 160

// What the PC sends in INSTALL_BEGIN. Text fields are NUL-padded, and the title is UTF-8.
struct install_game
{
    char disc_id[INSTALL_DISC_ID_BYTES];
    char startup[INSTALL_STARTUP_BYTES];
    char title[INSTALL_TITLE_BYTES];
    u32 layer0_sectors;
    u32 layer1_sectors;
    u8 disc_type;
    u8 compat_flags;
    u8 transfer_type;
    u8 transfer_mode;
    u32 flags;
};

// Opens the data port. Returns 0, or -1 if it cannot be opened.
int install_open(void);

// Creates the game's partitions and waits for its data. Fills name (33 bytes)
// with the partition name and parts with the number of partitions. Returns 0,
// -EBUSY while another install is open, -EEXIST when the name or the disc ID is
// already on the disk, -ENOSPC when the partitions do not fit, -EINVAL for a
// request that cannot be a game, or another negative errno.
int install_begin(const struct install_game *game, char *name, u32 *parts);

// Adds the data sockets to a select set while an install waits for data, and
// returns the highest one, or -1.
int install_add_fds(fd_set *set);

// When select found a data socket readable, accepts the data connection or
// receives what has arrived on it.
void install_serve_ready(fd_set *readable, status_report report);

// The open install's size in bytes, the bytes received, and the bytes on the
// disk. Returns 0, the first write error, or -ENOENT when no install is open.
int install_status(u64 *total, u64 *received, u64 *written);

// Receives the rest of the data, waits until it is on the disk, then writes the
// game's header and closes the install. With INSTALL_FLAG_CHECK, the game is
// first read back from the disk and each MiB compared with the CRC-32C the PC
// sent after the data. Returns 0 when the game is installed, -EPIPE when the
// data connection ended before the last byte or checksum, -EILSEQ when the
// disk holds something else (bad_block is then the first MiB that differs),
// -ENOENT when no install is open, or the first disk error. Without a 0, no
// header is written, and the game's partitions are removed: removed is then 1
// when that worked.
int install_end(status_report report, u32 *bad_block, int *removed);

// Closes an open install without writing the header and removes its
// partitions, as when Loadout disconnects.
void install_abort(void);

// True while an install is open, writing to the disk.
int install_busy(void);

// Removes an incomplete install: a game partition named PP.<disc ID>.HDL.<title>
// whose header area is all zeros, which is how a copy that stopped early is
// left. Returns 0, -ENOENT when no game partition has the name, -EPERM when the
// name is not in that form or the partition holds a header, -EBUSY while the
// game is being installed, or another negative errno, such as when the header
// cannot be read. Nothing is removed unless it returns 0.
int install_remove_incomplete(const char *name);

// Deletes a whole game: a game partition named PP.<disc ID>.HDL.<title> whose
// header has the HDLoader magic, with its sub-partitions. Returns 0, -ENOENT
// when no game partition has the name, -EPERM when the name is not in that form
// or the header is not a game's, -EBUSY while an install is open, or another
// negative errno. Nothing is removed unless it returns 0.
int install_delete(const char *name);

// What the PC sends in UPDATE_GAME: the header fields it may change. The title
// is UTF-8, NUL-padded, and must end inside its space.
struct install_edit
{
    char title[INSTALL_TITLE_BYTES];
    u8 compat_flags;
    u8 transfer_type;
    u8 transfer_mode;
    u8 disc_type;
};

// Rewrites a whole game's header with the edit's fields, keeping every other
// byte, and fills entry (GAMES_ENTRY_BYTES) with the game as the disk now holds
// it. The disc type must be CD, CDDA, DVD, or the one the header has. Returns 0,
// -ENOENT, -EPERM as for install_delete, -EBUSY while an install is open,
// -EINVAL for an edit that cannot be a game's, -EIO when the header read back
// differs, or another negative errno.
int install_update(const char *name, const struct install_edit *edit, u8 *entry);

// Reads sectors (512 bytes each, at most INSTALL_READ_MAX_SECTORS) of a game
// partition's part sub, from sector on, through the same path the check reads
// the game back, into out. For finding out what the disk holds. Returns 0,
// -ENOENT when no game partition has the name, -EBUSY during an install, or
// another negative errno.
#define INSTALL_READ_MAX_SECTORS 128
int install_read_sectors(const char *name, u32 sub, u32 sector, u32 sectors, u8 *out);

// Takes the CRC-32C of sectors of a game partition's part sub, read the way
// the check reads a game back, chunk sectors at a time (at most 512). Returns
// 0 and sets crc, or a negative errno.
int install_checksum_sectors(const char *name, u32 sub, u32 sector, u32 sectors, u32 chunk, u32 *crc);

#endif
