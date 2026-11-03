// The partition transfer and device controls have no POSIX form, so this file
// calls fileXio directly.
#define NEWLIB_PORT_AWARE

#include "disk.h"
#include "ui.h"

#include <errno.h>
#include <fcntl.h>
#include <hdd-ioctl.h>
#include <iopheap.h>
#include <kernel.h>
#include <sifrpc.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include <fileXio_rpc.h>

#define PARTITION_PATH "hdd0:" DISK_TEST_PARTITION
// The first 4 MB of a main partition hold its header and attributes. Data goes after.
#define DATA_START_SECTOR 0x2000
#define HEADER_BYTES (DATA_START_SECTOR * DISK_SECTOR_BYTES)

#define WRITER_STACK_BYTES (16 * 1024)

extern void *_gp;

static int ready;
static unsigned int size_gb;
static void *iop_buffer;

// The current write run: the partition, its parts, and where the next write goes.
static int target = -1;
static struct disk_part parts[DISK_MAX_PARTS];
static int part_count;
static int part_index;
static u32 part_sector;
static u32 unit_bytes = DISK_CHUNK_BYTES;
static volatile u64 written_bytes;

// The speed test's own partition, open while a test run writes to it.
static int test_partition = -1;

// A ring of buffers used in strict turn: the caller fills them while the
// writer thread writes the oldest. free_slots counts buffers the caller may
// fill; full_slots counts buffers waiting for the writer. A deep ring lets the
// network keep going while a slow write catches up.
static u8 buffers[DISK_BUFFERS][DISK_CHUNK_BYTES] __attribute__((aligned(64)));
static u32 queued_bytes[DISK_BUFFERS];
static int fill_index, write_index;
static int free_slots = -1, full_slots = -1;
static volatile int write_error;
static u8 writer_stack[WRITER_STACK_BYTES] __attribute__((aligned(16)));

static int write_block(const void *data, u32 bytes);

static void writer_thread(void *arg)
{
    (void)arg;
    for (;;)
    {
        WaitSema(full_slots);
        int index = write_index;
        write_index = (write_index + 1) % DISK_BUFFERS;
        // An empty buffer only hands the slot back, after a connection ended mid-block.
        if (write_error == 0 && queued_bytes[index] > 0)
        {
            int result = write_block(buffers[index], queued_bytes[index]);
            if (result < 0)
            {
                write_error = result;
            }
        }

        SignalSema(free_slots);
    }
}

static int start_writer(void)
{
    ee_sema_t free_sema = {.init_count = DISK_BUFFERS, .max_count = DISK_BUFFERS};
    ee_sema_t full_sema = {.init_count = 0, .max_count = DISK_BUFFERS};
    free_slots = CreateSema(&free_sema);
    full_slots = CreateSema(&full_sema);
    if (free_slots < 0 || full_slots < 0)
    {
        return -ENOMEM;
    }

    // The writer runs one step below the caller, so receiving always comes first
    // and the writer uses the time the caller spends waiting for the network.
    ee_thread_status_t status;
    ReferThreadStatus(GetThreadId(), &status);
    ee_thread_t thread = {
        .func = writer_thread,
        .stack = writer_stack,
        .stack_size = sizeof(writer_stack),
        .gp_reg = &_gp,
        .initial_priority = status.current_priority + 1,
    };
    int id = CreateThread(&thread);
    if (id < 0 || StartThread(id, NULL) < 0)
    {
        return -ENOMEM;
    }

    return 0;
}

// The driver returns sector counts in an int, so a disk over 1 TiB comes back
// negative. Its errors are small negative errno values.
static int is_driver_error(int result)
{
    return result < 0 && result > -4096;
}

int disk_init(void)
{
    if (fileXioDevctl("hdd0:", HDIOC_STATUS, NULL, 0, NULL, 0) != 0)
    {
        return -ENODEV;
    }

    int sectors = fileXioDevctl("hdd0:", HDIOC_TOTALSECTOR, NULL, 0, NULL, 0);
    if (sectors == 0 || is_driver_error(sectors))
    {
        return -ENODEV;
    }

    if (start_writer() < 0)
    {
        return -ENOMEM;
    }

    size_gb = (unsigned int)((u64)(u32)sectors * DISK_SECTOR_BYTES / (1024u * 1024u * 1024u));
    ready = 1;
    return 0;
}

u8 *disk_next_buffer(void)
{
    WaitSema(free_slots);
    u8 *buffer = buffers[fill_index];
    fill_index = (fill_index + 1) % DISK_BUFFERS;
    return buffer;
}

int disk_submit(u8 *buffer, u32 bytes)
{
    int index = (int)((buffer - buffers[0]) / DISK_CHUNK_BYTES);
    queued_bytes[index] = bytes;
    SignalSema(full_slots);
    return write_error;
}

int disk_drain(void)
{
    if (!ready)
    {
        return write_error;
    }

    for (int i = 0; i < DISK_BUFFERS; i++)
    {
        WaitSema(free_slots);
    }

    for (int i = 0; i < DISK_BUFFERS; i++)
    {
        SignalSema(free_slots);
    }

    return write_error;
}

int disk_ready(void)
{
    return ready;
}

unsigned int disk_size_gb(void)
{
    return ready ? size_gb : 0;
}

int disk_space(u64 *total_sectors, u64 *free_sectors)
{
    if (!ready)
    {
        return -ENODEV;
    }

    int total = fileXioDevctl("hdd0:", HDIOC_TOTALSECTOR, NULL, 0, NULL, 0);
    // The free count comes back in the output buffer, not as the result.
    u32 free = 0;
    int status = fileXioDevctl("hdd0:", HDIOC_FREESECTOR, NULL, 0, &free, sizeof(free));
    if (total == 0 || is_driver_error(total) || status < 0)
    {
        return -EIO;
    }

    *total_sectors = (u32)total;
    *free_sectors = free;
    return 0;
}

void disk_show_free(void)
{
    u64 total, free;
    if (disk_space(&total, &free) == 0)
    {
        ui_set_disk_free((unsigned int)(free * DISK_SECTOR_BYTES / (1024u * 1024u * 1024u)));
    }
}

// The smallest size APA accepts that holds the header area and the data.
static const char *partition_size(u64 total_bytes)
{
    static const struct
    {
        const char *name;
        u32 megabytes;
    } sizes[] = {
        {"128M", 128}, {"256M", 256}, {"512M", 512}, {"1G", 1024}, {"2G", 2048},
        {"4G", 4096}, {"8G", 8192}, {"16G", 16384}, {"32G", 32768},
    };

    for (unsigned int i = 0; i < sizeof(sizes) / sizeof(sizes[0]); i++)
    {
        if (total_bytes + HEADER_BYTES <= (u64)sizes[i].megabytes * 1024u * 1024u)
        {
            return sizes[i].name;
        }
    }

    return NULL;
}

int disk_begin(u64 total_bytes, u32 write_unit_bytes)
{
    if (!ready)
    {
        return -ENODEV;
    }

    if (total_bytes == 0 || total_bytes % DISK_SECTOR_BYTES != 0)
    {
        return -EINVAL;
    }

    if (write_unit_bytes == 0)
    {
        write_unit_bytes = DISK_CHUNK_BYTES;
    }

    if (write_unit_bytes % DISK_SECTOR_BYTES != 0 || write_unit_bytes > DISK_CHUNK_BYTES
        || DISK_CHUNK_BYTES % write_unit_bytes != 0)
    {
        return -EINVAL;
    }

    const char *size = partition_size(total_bytes);
    if (size == NULL)
    {
        return -EFBIG;
    }

    // A test run that stopped part-way can leave the partition behind.
    fileXioRemove(PARTITION_PATH);

    char path[64];
    snprintf(path, sizeof(path), PARTITION_PATH ",,,%s,PFS", size);
    test_partition = fileXioOpen(path, O_RDWR | O_CREAT);
    if (test_partition < 0)
    {
        return test_partition;
    }

    struct disk_part part = {DATA_START_SECTOR, (u32)(total_bytes / DISK_SECTOR_BYTES)};
    int status = disk_start(test_partition, &part, 1);
    if (status < 0)
    {
        fileXioClose(test_partition);
        test_partition = -1;
        return status;
    }

    unit_bytes = write_unit_bytes;
    return 0;
}

int disk_start(int fd, const struct disk_part *new_parts, int count)
{
    if (!ready)
    {
        return -ENODEV;
    }

    if (count < 1 || count > DISK_MAX_PARTS)
    {
        return -EINVAL;
    }

    if (iop_buffer == NULL)
    {
        iop_buffer = SifAllocIopHeap(DISK_CHUNK_BYTES);
        if (iop_buffer == NULL)
        {
            return -ENOMEM;
        }
    }

    memcpy(parts, new_parts, count * sizeof(parts[0]));
    part_count = count;
    part_index = 0;
    part_sector = 0;
    unit_bytes = DISK_CHUNK_BYTES;
    written_bytes = 0;
    target = fd;
    // Every buffer is free between runs, so the two indexes can start over.
    fill_index = 0;
    write_index = 0;
    write_error = 0;
    return 0;
}

u64 disk_written(void)
{
    return written_bytes;
}

int disk_error(void)
{
    return write_error;
}

int disk_read(int fd, u32 sub, u32 sector, u32 sectors, u8 *dest)
{
    u32 bytes = sectors * DISK_SECTOR_BYTES;
    if (!ready || target >= 0)
    {
        return -EBUSY;
    }

    if (iop_buffer == NULL)
    {
        iop_buffer = SifAllocIopHeap(DISK_CHUNK_BYTES);
        if (iop_buffer == NULL)
        {
            return -ENOMEM;
        }
    }

    if (sectors == 0 || bytes > DISK_CHUNK_BYTES || ((u32)dest & 63) != 0)
    {
        return -EINVAL;
    }

    hddIoctl2Transfer_t request = {sub, sector, sectors, APA_IO_MODE_READ, iop_buffer};
    int result = fileXioIoctl2(fd, HIOCTRANSFER, &request, sizeof(request), NULL, 0);
    if (result < 0)
    {
        return result;
    }

    // The IOP copies its buffer into EE memory, behind the data cache: no line
    // may be written back over the new data, and none may still hold the old.
    SyncDCache(dest, dest + bytes - 1);
    SifRpcReceiveData_t receive;
    result = SifRpcGetOtherData(&receive, iop_buffer, dest, (int)bytes, 0);
    InvalidDCache(dest, dest + bytes - 1);
    return result < 0 ? -EIO : 0;
}

int disk_stop(void)
{
    int status = disk_drain();
    if (target >= 0)
    {
        fileXioIoctl2(target, HIOCFLUSH, NULL, 0, NULL, 0);
        target = -1;
    }

    return status;
}

static int write_block(const void *data, u32 bytes)
{
    if (target < 0)
    {
        return -EBADF;
    }

    if (bytes == 0 || bytes > DISK_CHUNK_BYTES || bytes % DISK_SECTOR_BYTES != 0)
    {
        return -EINVAL;
    }

    // Copy the block into IOP memory, where the hard disk driver reads it from.
    SifWriteBackDCache((void *)data, (int)bytes);
    SifDmaTransfer_t transfer = {(void *)data, iop_buffer, (int)bytes, 0};
    int id;
    while ((id = SifSetDma(&transfer, 1)) == 0)
    {
    }
    // Sleep between checks rather than spin, so no other thread is held up.
    while (SifDmaStat(id) >= 0)
    {
        usleep(100);
    }

    // A write stops at the end of a part, and the rest goes to the start of the
    // next. The speed test can also split a block into smaller writes. On the
    // console they were slower: each write has a fixed cost.
    for (u32 done = 0; done < bytes;)
    {
        if (part_index >= part_count)
        {
            return -ENOSPC;
        }

        const struct disk_part *part = &parts[part_index];
        u32 piece = bytes - done < unit_bytes ? bytes - done : unit_bytes;
        u32 room = (part->sectors - part_sector) * DISK_SECTOR_BYTES;
        if (piece > room)
        {
            piece = room;
        }

        hddIoctl2Transfer_t request = {(u32)part_index, part->first_sector + part_sector, piece / DISK_SECTOR_BYTES,
                                       APA_IO_MODE_WRITE, (u8 *)iop_buffer + done};
        int result = fileXioIoctl2(target, HIOCTRANSFER, &request, sizeof(request), NULL, 0);
        if (result < 0)
        {
            return result;
        }

        done += piece;
        written_bytes += piece;
        part_sector += piece / DISK_SECTOR_BYTES;
        if (part_sector == part->sectors)
        {
            part_index++;
            part_sector = 0;
        }
    }

    return 0;
}

int disk_end(int keep)
{
    disk_stop();
    if (test_partition >= 0)
    {
        fileXioClose(test_partition);
        test_partition = -1;
    }

    return keep ? 0 : fileXioRemove(PARTITION_PATH);
}
