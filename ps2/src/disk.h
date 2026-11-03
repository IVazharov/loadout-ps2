// Raw writes to partitions on the PS2's hard disk: game data, and the speed
// test's test partition.
//
// The test partition is LOADOUT.SPEEDTEST, typed PFS but never formatted, so
// Open PS2 Loader does not list it as a game. A run removes a leftover test
// partition, creates a new one just big enough, and writes the data after the
// partition's 4 MB header area, the way game data is stored.

#ifndef LOADOUT_DISK_H
#define LOADOUT_DISK_H

#include <tamtypes.h>

#define DISK_TEST_PARTITION "LOADOUT.SPEEDTEST"
#define DISK_SECTOR_BYTES 512
// Bytes per write. A multiple of the sector size that fits in IOP memory.
#define DISK_CHUNK_BYTES (256 * 1024)
// Buffers in the write ring: 16 MB in all.
#define DISK_BUFFERS 64

// Checks for a usable hard disk once the drivers are loaded. Returns 0 when it is ready.
int disk_init(void);

int disk_ready(void);

// Disk size in whole GB, or 0 when there is no usable disk.
unsigned int disk_size_gb(void);

// Total 512-byte sectors, and the free sectors the driver can still hand out
// as new partitions (APA partition sizes are powers of two, so this can be less
// than the unused space).
// Returns 0, -ENODEV without a usable disk, or -EIO.
int disk_space(u64 *total_sectors, u64 *free_sectors);

// Puts the free space on the screen. Without an answer from the disk, the screen keeps what it had.
void disk_show_free(void);

// Creates the test partition for total_bytes of data. Each block is written to
// the disk in write_unit_bytes pieces: a multiple of the sector size that
// divides DISK_CHUNK_BYTES, or 0 for whole blocks. Returns 0 or a negative errno.
int disk_begin(u64 total_bytes, u32 write_unit_bytes);

// Waits for queued writes, closes the test partition, and removes it unless keep is set.
int disk_end(int keep);

// One stretch of a partition that data fills, in order: part i is
// sub-partition i (0 is the main partition), written from first_sector on for
// sectors sectors of 512 bytes.
struct disk_part
{
    u32 first_sector;
    u32 sectors;
};

// The most parts one write run spreads over: a main partition and its sub-partitions.
#define DISK_MAX_PARTS 65

// Starts a write run onto an open partition, filling parts in turn. The caller
// keeps the partition open until disk_stop. Returns 0 or a negative errno.
int disk_start(int fd, const struct disk_part *parts, int count);

// Bytes of the current run that are on the disk.
u64 disk_written(void);

// The run's first write error, or 0.
int disk_error(void);

// Waits for queued writes, flushes the disk's cache, and ends the run, leaving
// the partition open. Returns the first write error, or 0.
int disk_stop(void);

// Reads sectors (512 bytes each, at most DISK_CHUNK_BYTES in all) of
// sub-partition sub of an open partition, from sector on, into dest in EE
// memory: 64-byte aligned, and a multiple of 64 bytes long. Only between write
// runs, since it shares the write path's IOP buffer. Returns 0 or a negative errno.
int disk_read(int fd, u32 sub, u32 sector, u32 sectors, u8 *dest);

// Writes go through a ring of DISK_BUFFERS buffers: a writer thread, one
// priority step below the caller, writes the oldest while the caller fills the
// next, so receiving and writing overlap. Buffers are taken and submitted in
// turn, and every taken buffer must be submitted.

// Waits for a free buffer of DISK_CHUNK_BYTES and returns it.
u8 *disk_next_buffer(void);

// Queues a buffer from disk_next_buffer with bytes to write: a multiple of the
// sector size, or 0 to hand it back unwritten. Returns the first write error
// so far, or 0.
int disk_submit(u8 *buffer, u32 bytes);

// Waits until every queued buffer is written. Returns the first write error, or 0.
int disk_drain(void);

#endif
