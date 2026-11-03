// Network speed test: receives data from the PC into memory, or writes it to a
// test partition on the hard disk. docs/speed-test.md describes the wire format.

#ifndef LOADOUT_BENCH_H
#define LOADOUT_BENCH_H

#include <ps2ip.h>
#include <tamtypes.h>

#include "status.h"

#define BENCH_TCP_PORT 45070
#define BENCH_UDP_PORT 45071

// TCP header flags: write the data to the hard disk, and keep the test partition afterwards.
#define BENCH_FLAG_DISK 0x1u
#define BENCH_FLAG_KEEP 0x2u
// Bits 16-31: the size of each disk write in KiB, 0 for whole 256 KiB blocks.
#define BENCH_UNIT_SHIFT 16

// The TCP reply: bytes received, microseconds, and a status (0 or a negative errno).
#define BENCH_TCP_REPLY_BYTES 24

struct bench_result
{
    const char *transport;
    u64 bytes;
    u64 microseconds;
    // UDP only: packets that arrived and packets the PC says it sent.
    u32 packets_received;
    u32 packets_sent;
};

// Called with STATUS_BENCH while a run is going and with STATUS_BENCH_DONE or
// STATUS_BENCH_FAILED when it ends; detail may be NULL. result is NULL until the
// first run finishes.
typedef void (*bench_report)(enum status status, const char *detail, const struct bench_result *result);

// Opens the speed test ports. Returns 0, or -1 if they cannot be opened.
int bench_open(void);

// Adds the speed test sockets to a select set and returns the highest one.
int bench_add_fds(fd_set *set);

// Serves one TCP or UDP test run on each speed test socket that select found readable.
void bench_serve_ready(fd_set *readable, bench_report report);

// Formats bytes over microseconds as "12.34 MB/s", with 1 MB = 1048576 bytes.
void bench_format_rate(char *text, int size, u64 bytes, u64 microseconds);

u64 bench_now_microseconds(void);

#endif
