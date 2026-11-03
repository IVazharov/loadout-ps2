// A new version of Loadout Bay, sent by Loadout over the network. It is started
// from memory, and once it runs, Loadout sends it again to be saved over the
// file this copy was started from. docs/bay-protocol.md describes the commands.

#ifndef LOADOUT_UPGRADE_H
#define LOADOUT_UPGRADE_H

#include <tamtypes.h>

#define UPGRADE_MAX_BYTES (8u * 1024u * 1024u)
// The run tag: up to 16 letters, digits, or dashes, NUL-padded on the wire.
#define UPGRADE_TAG_BYTES 16

// Remembers the file this copy was started from and the tag it was started with.
void upgrade_init(int argc, char *argv[]);

// The tag this copy was started with, NUL-padded to UPGRADE_TAG_BYTES; all
// zeros when a launcher started it.
const char *upgrade_tag(void);

// Makes room for an ELF of size bytes, dropping one received before.
int upgrade_begin(u32 size);

// Stores the next chunk; chunks come in order.
int upgrade_data(u32 offset, const u8 *data, u32 length);

// Checks that the whole ELF is here and can be started, and keeps the tag for
// upgrade_run.
int upgrade_prepare_run(const u8 *tag);

// Whether upgrade_prepare_run accepted an ELF that waits to be started.
int upgrade_run_pending(void);

// Resets the IOP and starts the ELF from memory with this copy's start path and
// the tag. Returns only when the ELF cannot be started, which leaves the IOP
// without its modules.
void upgrade_run(void);

// Writes the ELF over the file this copy was started from, keeping the old one
// as <file>.bak. Only a file on a USB drive or the hard disk.
int upgrade_save(void);

#endif
