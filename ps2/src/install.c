// Partition creation and the header write have no POSIX form, so this file
// calls fileXio directly.
#define NEWLIB_PORT_AWARE

#include "install.h"
#include "bench.h"
#include "disk.h"
#include "games.h"
#include "layout.h"
#include "wire.h"

#include <ctype.h>
#include <errno.h>
#include <hdd-ioctl.h>
#include <io_common.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include <fileXio_rpc.h>

#define CD_SECTOR_BYTES 2048
#define BYTES_PER_MB 1048576u
#define BYTES_PER_GB 1073741824ull
#define RATE_AFTER_MICROSECONDS 2000000u
#define DATA_IDLE_SECONDS 30

// The HDLoader header: 1024 bytes, 1 MB + 4 KB into the main partition, where
// Open PS2 Loader and other HDLoader tools look for it.
#define HEADER_OFFSET 0x100000
#define HEADER_BYTES 1024
#define HEADER_MAGIC 0xDEADFEEDu
#define HEADER_VERSION 1
#define HEADER_TITLE 8
#define HEADER_COMPAT_FLAGS 169
#define HEADER_TRANSFER_TYPE 170
#define HEADER_TRANSFER_MODE 171
#define HEADER_STARTUP 172
#define HEADER_LAYER1_START 232
#define HEADER_DISC_TYPE 236
#define HEADER_PART_COUNT 240
#define HEADER_PARTS 244
#define HEADER_PART_BYTES 12

// The disc types UPDATE_GAME may set, as the header stores them.
#define DISC_TYPE_CD 0x12
#define DISC_TYPE_CDDA 0x13
#define DISC_TYPE_DVD 0x14

// A main partition name keeps at most 14 characters of the title.
#define NAME_TITLE_CHARACTERS 14
#define NAME_MAX_LENGTH 32
#define DISC_ID_MAX_LENGTH 10

static int listener = -1;

// The open install. The header is built when the partitions are made and
// written only after the last byte is on the disk.
static int active;
static int partition = -1;
static int client = -1;
static char partition_name[NAME_MAX_LENGTH + 1];
static u64 total_bytes, received_bytes;
static int receive_error;
// Set once the data connection is accepted: an install takes no second one.
static int data_accepted;
static u32 reported_mb;
static u64 data_started;
static u8 header[HEADER_BYTES] __attribute__((aligned(64)));

// Where the game's bytes go, kept for the check, which reads them back the same way.
static struct disk_part plan[DISK_MAX_PARTS];
static u32 plan_count;

// The check: the PC sends one CRC-32C per MiB of the game after its last byte.
static int checking;
static u32 checksum_count;
static u32 checksum_bytes_received;
static u32 checksums[INSTALL_MAX_CHECKSUMS];
static u8 read_back[DISK_CHUNK_BYTES] __attribute__((aligned(64)));
static u32 crc_table[4][256];

int install_open(void)
{
    listener = wire_open_socket(SOCK_STREAM, INSTALL_DATA_PORT);
    return listener >= 0 ? 0 : -1;
}

// PP.<disc ID>.HDL.<title>, keeping the first 14 characters of the title and
// turning every one that is not a letter, a digit, or '_' into '_', the form
// other HDLoader tools use too. A character outside ASCII becomes one '_'.
static void make_name(const struct install_game *game, char *name)
{
    int length = snprintf(name, NAME_MAX_LENGTH + 1, "PP.%s.HDL.", game->disc_id);
    int characters = 0;
    for (int i = 0; i < INSTALL_TITLE_BYTES && game->title[i] != '\0' && characters < NAME_TITLE_CHARACTERS; i++)
    {
        unsigned char c = (unsigned char)game->title[i];
        // UTF-8 continuation bytes belong to the character before them.
        if ((c & 0xC0) == 0x80)
        {
            continue;
        }

        name[length++] = c < 0x80 && (isalnum(c) || c == '_') ? (char)c : '_';
        characters++;
    }

    name[length] = '\0';
}

static int valid_disc_id(const char *disc_id)
{
    size_t length = strnlen(disc_id, INSTALL_DISC_ID_BYTES);
    if (length == 0 || length > DISC_ID_MAX_LENGTH)
    {
        return 0;
    }

    for (size_t i = 0; i < length; i++)
    {
        if (!isalnum((unsigned char)disc_id[i]) && disc_id[i] != '-' && disc_id[i] != '_')
        {
            return 0;
        }
    }

    return 1;
}

// The driver returns sector numbers in an int, so a disk over 1 TiB comes back
// negative. Its errors are small negative errno values.
static int is_driver_error(int result)
{
    return result < 0 && result > -4096;
}

// Creates the main partition and its sub-partitions: each part takes
// layout_choose's size, or one size smaller each time the disk has no room for
// it. Returns the open partition or a negative errno.
static int create_partitions(const char *name, u32 sectors)
{
    int max_part_sectors = fileXioDevctl("hdd0:", HDIOC_MAXSECTOR, NULL, 0, NULL, 0);
    if (is_driver_error(max_part_sectors) || max_part_sectors == 0)
    {
        return -EIO;
    }

    int size = layout_choose(sectors, (unsigned int)max_part_sectors, 0);
    int fd;
    for (;;)
    {
        char path[64];
        snprintf(path, sizeof(path), "hdd0:%s,,,%s,HDL", name, layout_size_name(size));
        fd = fileXioOpen(path, FIO_O_RDWR | FIO_O_CREAT);
        if (fd != -ENOSPC || size == 0)
        {
            break;
        }

        size--;
    }

    if (fd < 0)
    {
        return fd;
    }

    u32 left = sectors - (layout_capacity(size, 0) < sectors ? layout_capacity(size, 0) : sectors);
    int status = 0;
    for (int part = 1; left > 0 && status >= 0; part++)
    {
        size = layout_choose(left, (unsigned int)max_part_sectors, part);
        for (;;)
        {
            const char *size_name = layout_size_name(size);
            status = fileXioIoctl2(fd, HIOCADDSUB, (void *)size_name, strlen(size_name) + 1, NULL, 0);
            if (status != -ENOSPC || size == 0)
            {
                break;
            }

            size--;
        }

        left -= layout_capacity(size, part) < left ? layout_capacity(size, part) : left;
    }

    if (status < 0)
    {
        fileXioClose(fd);
        char path[48];
        snprintf(path, sizeof(path), "hdd0:%s", name);
        fileXioRemove(path);
        return status;
    }

    return fd;
}

// Fills the header and the write plan from the partitions as the driver made
// them: each part's first game sector, its first data sector on the disk, and
// the bytes of game data it holds.
static int plan_parts(int fd, const struct install_game *game, struct disk_part *parts, u32 *count)
{
    u32 sectors = game->layer0_sectors + game->layer1_sectors;
    int subs = fileXioIoctl2(fd, HIOCNSUB, NULL, 0, NULL, 0);
    if (subs < 0 || subs + 1 > DISK_MAX_PARTS)
    {
        return subs < 0 ? subs : -EFBIG;
    }

    memset(header, 0, sizeof(header));
    wire_write_u32(header, HEADER_MAGIC);
    header[6] = HEADER_VERSION;
    memcpy(header + HEADER_TITLE, game->title, INSTALL_TITLE_BYTES);
    header[HEADER_COMPAT_FLAGS] = game->compat_flags;
    header[HEADER_TRANSFER_TYPE] = game->transfer_type;
    header[HEADER_TRANSFER_MODE] = game->transfer_mode;
    memcpy(header + HEADER_STARTUP, game->startup, INSTALL_STARTUP_BYTES);
    wire_write_u32(header + HEADER_LAYER1_START, game->layer1_sectors > 0 ? game->layer0_sectors - 16 : 0);
    wire_write_u32(header + HEADER_DISC_TYPE, game->disc_type);
    wire_write_u32(header + HEADER_PART_COUNT, (u32)subs + 1);

    u32 placed = 0;
    for (u32 i = 0; i <= (u32)subs; i++)
    {
        int start = fileXioIoctl2(fd, HIOCGETPARTSTART, &i, sizeof(i), NULL, 0);
        int length = fileXioIoctl2(fd, HIOCGETSIZE, &i, sizeof(i), NULL, 0);
        if (is_driver_error(start) || is_driver_error(length))
        {
            return -EIO;
        }

        u32 reserved = layout_reserved_sectors((int)i);
        u32 in_part = ((u32)length - reserved) / 4;
        if (in_part > sectors - placed)
        {
            in_part = sectors - placed;
        }

        u8 *spec = header + HEADER_PARTS + i * HEADER_PART_BYTES;
        wire_write_u32(spec, placed);
        wire_write_u32(spec + 4, (u32)start + reserved);
        wire_write_u32(spec + 8, in_part * CD_SECTOR_BYTES);
        parts[i].first_sector = reserved;
        parts[i].sectors = in_part * 4;
        placed += in_part;
    }

    *count = (u32)subs + 1;
    return placed == sectors ? 0 : -ENOSPC;
}

// Writes HEADER_BYTES at the header's place in an open main partition.
static int write_header_area(int fd, const u8 *data)
{
    int result = fileXioLseek(fd, HEADER_OFFSET, SEEK_SET);
    if (result >= 0)
    {
        result = fileXioWrite(fd, (void *)data, HEADER_BYTES);
    }

    return result < 0 ? result : result == HEADER_BYTES ? 0 : -EIO;
}

// A connection left waiting from before this install would otherwise be taken for its data.
static void refuse_waiting_connections(void)
{
    for (;;)
    {
        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(listener, &readable);
        struct timeval none = {0, 0};
        if (lwip_select(listener + 1, &readable, NULL, NULL, &none) <= 0)
        {
            return;
        }

        int waiting = lwip_accept(listener, NULL, NULL);
        if (waiting < 0)
        {
            return;
        }

        lwip_close(waiting);
    }
}

static void close_install(void)
{
    if (client >= 0)
    {
        lwip_close(client);
        client = -1;
    }

    if (partition >= 0)
    {
        disk_stop();
        fileXioClose(partition);
        partition = -1;
    }

    active = 0;
}

int install_begin(const struct install_game *game, char *name, u32 *parts)
{
    if (active)
    {
        return -EBUSY;
    }

    if (!disk_ready())
    {
        return -ENODEV;
    }

    u64 sectors = (u64)game->layer0_sectors + game->layer1_sectors;
    if (!valid_disc_id(game->disc_id) || game->startup[0] == '\0' || game->title[0] == '\0' || sectors == 0
        || sectors > 0xFFFFFFFFu || (game->layer1_sectors > 0 && game->layer0_sectors <= 16))
    {
        return -EINVAL;
    }

    u64 bytes = sectors * CD_SECTOR_BYTES;
    u32 blocks = (u32)((bytes + INSTALL_CHECK_BLOCK_BYTES - 1) / INSTALL_CHECK_BLOCK_BYTES);
    if ((game->flags & INSTALL_FLAG_CHECK) != 0 && blocks > INSTALL_MAX_CHECKSUMS)
    {
        return -EFBIG;
    }

    make_name(game, partition_name);
    int status = games_check_new(partition_name, game->disc_id);
    if (status < 0)
    {
        return status;
    }

    partition = create_partitions(partition_name, (u32)sectors);
    if (partition < 0)
    {
        status = partition;
        partition = -1;
        return status;
    }

    status = plan_parts(partition, game, plan, parts);
    plan_count = *parts;
    // A game removed from this place can have left its header behind. It is
    // cleared now, so the partition never shows a header until this one is written.
    static u8 zeros[HEADER_BYTES] __attribute__((aligned(64)));
    if (status == 0)
    {
        status = write_header_area(partition, zeros);
    }

    if (status == 0)
    {
        status = disk_start(partition, plan, (int)*parts);
    }

    if (status < 0)
    {
        fileXioClose(partition);
        partition = -1;
        char path[48];
        snprintf(path, sizeof(path), "hdd0:%s", partition_name);
        fileXioRemove(path);
        return status;
    }

    refuse_waiting_connections();
    total_bytes = bytes;
    checking = (game->flags & INSTALL_FLAG_CHECK) != 0;
    checksum_count = checking ? blocks : 0;
    checksum_bytes_received = 0;
    received_bytes = 0;
    receive_error = 0;
    data_accepted = 0;
    reported_mb = 0;
    active = 1;
    snprintf(name, NAME_MAX_LENGTH + 1, "%s", partition_name);
    return 0;
}

int install_add_fds(fd_set *set)
{
    if (!active || (data_accepted && client < 0))
    {
        return -1;
    }

    int fd = client >= 0 ? client : listener;
    FD_SET(fd, set);
    return fd;
}

// "1.24 of 4.37 GB", with 1 GB = 1024 MB, and a suffix. Rounded, as Loadout rounds its own figures.
static const char *format_progress(char *text, int size, u32 mb, const char *suffix)
{
    u32 done = (mb * 100u + 512u) / 1024u;
    u32 total = (u32)((total_bytes * 100u + BYTES_PER_GB / 2u) / BYTES_PER_GB);
    snprintf(text, size, "%u.%02u of %u.%02u GB%s", (unsigned)(done / 100u), (unsigned)(done % 100u),
             (unsigned)(total / 100u), (unsigned)(total % 100u), suffix);
    return text;
}

static int permille(u32 mb)
{
    u32 total = (u32)(total_bytes / BYTES_PER_MB);
    return total == 0 ? 0 : (int)((u64)mb * 1000 / total);
}

static void report_progress(status_report report)
{
    u32 mb = (u32)(disk_written() / BYTES_PER_MB);
    if (mb == reported_mb && received_bytes < total_bytes)
    {
        return;
    }

    reported_mb = mb;
    // The average since the data began; the first moments would only give a wild number.
    char rate[24] = "";
    u64 elapsed = bench_now_microseconds() - data_started;
    if (elapsed >= RATE_AFTER_MICROSECONDS)
    {
        // Rounded, as Loadout rounds its own figure.
        u64 tenths = (disk_written() * 10u * 1000000u / BYTES_PER_MB + elapsed / 2) / elapsed;
        snprintf(rate, sizeof(rate), " \xC2\xB7 %u.%u MB/s", (unsigned)(tenths / 10u), (unsigned)(tenths % 10u));
    }

    char detail[48];
    report(STATUS_INSTALLING, format_progress(detail, sizeof(detail), mb, rate), permille(mb));
}

static void close_client(void)
{
    lwip_close(client);
    client = -1;
}

// After the game's last byte come its checksums, when the install is checked.
static int receive_checksums(void)
{
    u32 want = checksum_count * 4 - checksum_bytes_received;
    int count = lwip_recv(client, (u8 *)checksums + checksum_bytes_received, (int)want, 0);
    if (count <= 0)
    {
        close_client();
        return -1;
    }

    checksum_bytes_received += (u32)count;
    if (checksum_bytes_received == checksum_count * 4)
    {
        close_client();
        return -1;
    }

    return 0;
}

// Receives one block and queues it for the disk. A block is taken whole, as
// in the speed test: one select per block keeps the receive rate up, and the
// PC never holds the data back while it waits for a control reply, so a
// control request waits at most one block. Returns -1 once the data
// connection is closed.
static int receive_some(void)
{
    if (received_bytes >= total_bytes)
    {
        return receive_checksums();
    }

    u64 left = total_bytes - received_bytes;
    u32 want = left < DISK_CHUNK_BYTES ? (u32)left : DISK_CHUNK_BYTES;
    u8 *block = disk_next_buffer();
    u32 filled = 0;
    while (filled < want)
    {
        int count = lwip_recv(client, block + filled, (int)(want - filled), 0);
        if (count <= 0)
        {
            // A block cut short is not written: without its last bytes the game is incomplete anyway.
            disk_submit(block, 0);
            close_client();
            return -1;
        }

        filled += (u32)count;
        received_bytes += (u64)count;
    }

    receive_error = disk_submit(block, filled);

    // Every byte is here and no checksums follow, or the disk failed: nothing more is read.
    if ((received_bytes >= total_bytes && !checking) || receive_error != 0)
    {
        close_client();
        return -1;
    }

    return 0;
}

void install_serve_ready(fd_set *readable, status_report report)
{
    if (!active)
    {
        return;
    }

    if (client < 0 && FD_ISSET(listener, readable))
    {
        client = lwip_accept(listener, NULL, NULL);
        if (client >= 0)
        {
            data_accepted = 1;
            data_started = bench_now_microseconds();
            report(STATUS_INSTALLING, NULL, 0);
        }

        return;
    }

    if (client >= 0 && FD_ISSET(client, readable))
    {
        receive_some();
        report_progress(report);
    }
}

int install_status(u64 *total, u64 *received, u64 *written)
{
    if (!active)
    {
        return -ENOENT;
    }

    *total = total_bytes;
    *received = received_bytes;
    *written = disk_written();
    return receive_error != 0 ? receive_error : disk_error();
}

// CRC-32C (Castagnoli, reflected 0x82F63B78), four bytes a step, the same
// sum the PC takes with its CPU's CRC32 instruction.
static void crc_init(void)
{
    if (crc_table[0][1] != 0)
    {
        return;
    }

    for (u32 i = 0; i < 256; i++)
    {
        u32 crc = i;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0);
        }

        crc_table[0][i] = crc;
    }

    for (u32 i = 0; i < 256; i++)
    {
        for (int t = 1; t < 4; t++)
        {
            crc_table[t][i] = (crc_table[t - 1][i] >> 8) ^ crc_table[0][crc_table[t - 1][i] & 0xFF];
        }
    }
}

// Updates a running CRC without the final inversion. data is 4-byte aligned
// and length a multiple of 4: the game is read in whole sectors.
static u32 crc_update(u32 crc, const u8 *data, u32 length)
{
    const u32 *words = (const u32 *)data;
    for (u32 i = 0; i < length / 4; i++)
    {
        crc ^= words[i];
        crc = crc_table[3][crc & 0xFF] ^ crc_table[2][(crc >> 8) & 0xFF] ^ crc_table[1][(crc >> 16) & 0xFF]
              ^ crc_table[0][crc >> 24];
    }

    return crc;
}

// Reads the game back from the disk, part by part as it was written, and
// compares the CRC-32C of each MiB with the one the PC sent.
static int check_copy(status_report report, u32 *bad_block)
{
    crc_init();
    u32 block = 0, in_block = 0, crc = 0xFFFFFFFFu;
    u64 read = 0;
    u32 reported = 0xFFFFFFFFu;
    for (u32 part = 0; part < plan_count; part++)
    {
        for (u32 done = 0; done < plan[part].sectors;)
        {
            u32 sectors = plan[part].sectors - done;
            if (sectors > DISK_CHUNK_BYTES / DISK_SECTOR_BYTES)
            {
                sectors = DISK_CHUNK_BYTES / DISK_SECTOR_BYTES;
            }

            int status = disk_read(partition, part, plan[part].first_sector + done, sectors, read_back);
            if (status < 0)
            {
                return status;
            }

            // A MiB can span two reads, and two parts.
            u32 bytes = sectors * DISK_SECTOR_BYTES;
            for (u32 at = 0; at < bytes;)
            {
                u32 take = bytes - at < INSTALL_CHECK_BLOCK_BYTES - in_block ? bytes - at : INSTALL_CHECK_BLOCK_BYTES - in_block;
                crc = crc_update(crc, read_back + at, take);
                at += take;
                in_block += take;
                if (in_block == INSTALL_CHECK_BLOCK_BYTES)
                {
                    if (~crc != checksums[block])
                    {
                        *bad_block = block;
                        return -EILSEQ;
                    }

                    block++;
                    in_block = 0;
                    crc = 0xFFFFFFFFu;
                }
            }

            done += sectors;
            read += bytes;
            u32 mb = (u32)(read / BYTES_PER_MB);
            if (mb / 16 != reported)
            {
                reported = mb / 16;
                char detail[48];
                report(STATUS_CHECKING, format_progress(detail, sizeof(detail), mb, " read back"), permille(mb));
            }
        }
    }

    if (in_block > 0 && ~crc != checksums[block])
    {
        *bad_block = block;
        return -EILSEQ;
    }

    return 0;
}

int install_end(status_report report, u32 *bad_block)
{
    if (!active)
    {
        return -ENOENT;
    }

    // The PC sends INSTALL_END after its last byte, which may still be on its way.
    while (client >= 0)
    {
        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(client, &readable);
        struct timeval wait = {DATA_IDLE_SECONDS, 0};
        if (lwip_select(client + 1, &readable, NULL, NULL, &wait) <= 0)
        {
            break;
        }

        receive_some();
        report_progress(report);
    }

    if (client >= 0)
    {
        lwip_close(client);
        client = -1;
    }

    report(STATUS_FINISHING, NULL, STATUS_NO_PROGRESS);
    int status = disk_stop();
    if (status == 0 && receive_error != 0)
    {
        status = receive_error;
    }

    if (status == 0 && (received_bytes < total_bytes || disk_written() < total_bytes
                        || checksum_bytes_received < checksum_count * 4))
    {
        status = -EPIPE;
    }

    if (status == 0 && checking)
    {
        status = check_copy(report, bad_block);
    }

    if (status == 0)
    {
        status = write_header_area(partition, header);
        if (status == 0)
        {
            fileXioIoctl2(partition, HIOCFLUSH, NULL, 0, NULL, 0);
        }
    }

    fileXioClose(partition);
    partition = -1;
    active = 0;
    report(status == 0 ? STATUS_INSTALLED : status == -EILSEQ ? STATUS_CHECK_FAILED : STATUS_INSTALL_FAILED, NULL,
           STATUS_NO_PROGRESS);
    return status;
}

int install_busy(void)
{
    return active;
}

void install_abort(void)
{
    if (active)
    {
        close_install();
    }
}

int install_read_sectors(const char *name, u32 sub, u32 sector, u32 sectors, u8 *out)
{
    if (active)
    {
        return -EBUSY;
    }

    if (sectors == 0 || sectors > INSTALL_READ_MAX_SECTORS)
    {
        return -EINVAL;
    }

    // Only a game partition, found by name the way READ_ENTRY finds one.
    static u8 entry[GAMES_ENTRY_BYTES];
    int status = games_read(name, entry);
    if (status < 0 && status != -EIO)
    {
        return status;
    }

    char path[48];
    snprintf(path, sizeof(path), "hdd0:%s", name);
    int fd = fileXioOpen(path, FIO_O_RDONLY);
    if (fd < 0)
    {
        return fd;
    }

    status = disk_read(fd, sub, sector, sectors, read_back);
    fileXioClose(fd);
    if (status == 0)
    {
        memcpy(out, read_back, sectors * DISK_SECTOR_BYTES);
    }

    return status;
}

int install_checksum_sectors(const char *name, u32 sub, u32 sector, u32 sectors, u32 chunk, u32 *crc)
{
    if (active)
    {
        return -EBUSY;
    }

    if (sectors == 0 || chunk == 0 || chunk > DISK_CHUNK_BYTES / DISK_SECTOR_BYTES)
    {
        return -EINVAL;
    }

    static u8 entry[GAMES_ENTRY_BYTES];
    int status = games_read(name, entry);
    if (status < 0 && status != -EIO)
    {
        return status;
    }

    char path[48];
    snprintf(path, sizeof(path), "hdd0:%s", name);
    int fd = fileXioOpen(path, FIO_O_RDONLY);
    if (fd < 0)
    {
        return fd;
    }

    crc_init();
    u32 sum = 0xFFFFFFFFu;
    for (u32 done = 0; done < sectors && status >= 0;)
    {
        u32 step = sectors - done < chunk ? sectors - done : chunk;
        status = disk_read(fd, sub, sector + done, step, read_back);
        if (status >= 0)
        {
            sum = crc_update(sum, read_back, step * DISK_SECTOR_BYTES);
        }

        done += step;
    }

    fileXioClose(fd);
    *crc = ~sum;
    return status < 0 ? status : 0;
}

// Only a name Loadout Bay or HDLGameInstaller gives a game: PP.<disc ID>.HDL.<title>.
static int is_game_name(const char *name)
{
    if (strncmp(name, "PP.", 3) != 0)
    {
        return 0;
    }

    const char *disc_id = name + 3;
    const char *end = strchr(disc_id, '.');
    if (end == NULL || strncmp(end, ".HDL.", 5) != 0 || end - disc_id > DISC_ID_MAX_LENGTH)
    {
        return 0;
    }

    char id[INSTALL_DISC_ID_BYTES];
    memset(id, 0, sizeof(id));
    memcpy(id, disc_id, (size_t)(end - disc_id));
    return valid_disc_id(id);
}

int install_remove_incomplete(const char *name)
{
    if (active && strcmp(name, partition_name) == 0)
    {
        return -EBUSY;
    }

    if (!is_game_name(name))
    {
        return -EPERM;
    }

    // The header is read again here, so a list read earlier cannot remove a game
    // that has been installed since.
    static u8 entry[GAMES_ENTRY_BYTES];
    int status = games_read(name, entry);
    if (status < 0)
    {
        return status;
    }

    for (int i = GAMES_NAME_BYTES; i < GAMES_ENTRY_BYTES; i++)
    {
        if (entry[i] != 0)
        {
            return -EPERM;
        }
    }

    char path[48];
    snprintf(path, sizeof(path), "hdd0:%s", name);
    return fileXioRemove(path);
}

int install_delete(const char *name)
{
    // Partitions are not removed while an install is writing to the disk.
    if (active)
    {
        return -EBUSY;
    }

    if (!is_game_name(name))
    {
        return -EPERM;
    }

    static u8 entry[GAMES_ENTRY_BYTES];
    int status = games_read(name, entry);
    if (status < 0)
    {
        return status;
    }

    // Only a whole game. An incomplete install has its own command, with its own check.
    if (wire_read_u32(entry + GAMES_NAME_BYTES) != HEADER_MAGIC)
    {
        return -EPERM;
    }

    char path[48];
    snprintf(path, sizeof(path), "hdd0:%s", name);
    return fileXioRemove(path);
}

int install_update(const char *name, const struct install_edit *edit, u8 *entry)
{
    if (active)
    {
        return -EBUSY;
    }

    if (!is_game_name(name))
    {
        return -EPERM;
    }

    size_t title_length = strnlen(edit->title, INSTALL_TITLE_BYTES);
    if (title_length == 0 || title_length == INSTALL_TITLE_BYTES)
    {
        return -EINVAL;
    }

    int status = games_read(name, entry);
    if (status < 0)
    {
        return status;
    }

    const u8 *current = entry + GAMES_NAME_BYTES;
    if (wire_read_u32(current) != HEADER_MAGIC)
    {
        return -EPERM;
    }

    u32 disc_type = wire_read_u32(current + HEADER_DISC_TYPE);
    if (edit->disc_type != disc_type && edit->disc_type != DISC_TYPE_CD && edit->disc_type != DISC_TYPE_CDDA
        && edit->disc_type != DISC_TYPE_DVD)
    {
        return -EINVAL;
    }

    // Every other byte stays as it is, including the HDL compatibility flags,
    // which HDLGameInstaller's update clears.
    memcpy(header, current, HEADER_BYTES);
    memset(header + HEADER_TITLE, 0, INSTALL_TITLE_BYTES);
    memcpy(header + HEADER_TITLE, edit->title, title_length);
    header[HEADER_COMPAT_FLAGS] = edit->compat_flags;
    header[HEADER_TRANSFER_TYPE] = edit->transfer_type;
    header[HEADER_TRANSFER_MODE] = edit->transfer_mode;
    wire_write_u32(header + HEADER_DISC_TYPE, edit->disc_type);

    char path[48];
    snprintf(path, sizeof(path), "hdd0:%s", name);
    int fd = fileXioOpen(path, FIO_O_RDWR);
    if (fd < 0)
    {
        return fd;
    }

    status = write_header_area(fd, header);
    if (status == 0)
    {
        fileXioIoctl2(fd, HIOCFLUSH, NULL, 0, NULL, 0);
    }

    fileXioClose(fd);
    if (status < 0)
    {
        return status;
    }

    // The reply is the header as the disk now holds it.
    status = games_read(name, entry);
    if (status < 0)
    {
        return status;
    }

    return memcmp(entry + GAMES_NAME_BYTES, header, HEADER_BYTES) == 0 ? 0 : -EIO;
}
