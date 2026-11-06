// Partition lookups and reads have no POSIX form, so this file calls fileXio directly.
#define NEWLIB_PORT_AWARE

#include "games.h"

#include <errno.h>
#include <hdd-ioctl.h>
#include <io_common.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include <fileXio_rpc.h>

// The HDLoader header sits 1 MB + 4 KB into the main partition. Reads on an
// open partition already start 4 KB in, after the APA header.
#define HEADER_OFFSET 0x100000
#define NAME_MAX_LENGTH 32

static u8 header[GAMES_HEADER_BYTES] __attribute__((aligned(64)));

// Game data spreads over a main partition and sub-partitions; only the main one is a game.
static int is_game(const iox_dirent_t *entry)
{
    return entry->stat.mode == APA_TYPE_HDL && (entry->stat.attr & APA_FLAG_SUB) == 0;
}

static int read_entry(const char *name, u8 *out)
{
    memset(out, 0, GAMES_ENTRY_BYTES);
    memcpy(out, name, strnlen(name, NAME_MAX_LENGTH));

    char path[48];
    snprintf(path, sizeof(path), "hdd0:%s", name);
    // fileXio takes iomanX flags, not newlib ones: newlib's O_RDONLY is 0,
    // which opens the partition for neither reading nor writing.
    int fd = fileXioOpen(path, FIO_O_RDONLY);
    if (fd < 0)
    {
        return fd;
    }

    int result = fileXioLseek(fd, HEADER_OFFSET, SEEK_SET);
    if (result >= 0)
    {
        result = fileXioRead(fd, header, sizeof(header));
    }

    fileXioClose(fd);
    if (result < 0)
    {
        return result;
    }

    if (result != GAMES_HEADER_BYTES)
    {
        return -EIO;
    }

    memcpy(out + GAMES_NAME_BYTES, header, GAMES_HEADER_BYTES);
    return 0;
}

int games_list(u32 first, u32 max, u8 *out, u32 *total, u32 *count)
{
    *total = 0;
    *count = 0;
    int dir = fileXioDopen("hdd0:");
    if (dir < 0)
    {
        return dir;
    }

    iox_dirent_t entry;
    while (fileXioDread(dir, &entry) > 0)
    {
        if (!is_game(&entry))
        {
            continue;
        }

        if (*total >= first && *count < max)
        {
            u8 *slot = out + *count * GAMES_ENTRY_BYTES;
            if (read_entry(entry.name, slot) < 0)
            {
                // The game is still listed, so the PC can show it as unreadable.
                memset(slot + GAMES_NAME_BYTES, 0, GAMES_HEADER_BYTES);
                slot[GAMES_ENTRY_FLAGS] = GAMES_FLAG_UNREADABLE;
            }

            (*count)++;
        }

        (*total)++;
    }

    fileXioDclose(dir);
    return 0;
}

int games_read(const char *name, u8 *out)
{
    // A comma would start the partition password in an hdd0: path.
    size_t length = strnlen(name, NAME_MAX_LENGTH + 1);
    if (length == 0 || length > NAME_MAX_LENGTH || strchr(name, ',') != NULL)
    {
        return -EINVAL;
    }

    int dir = fileXioDopen("hdd0:");
    if (dir < 0)
    {
        return dir;
    }

    int found = 0;
    iox_dirent_t entry;
    while (!found && fileXioDread(dir, &entry) > 0)
    {
        found = is_game(&entry) && strcmp(entry.name, name) == 0;
    }

    fileXioDclose(dir);
    return found ? read_entry(name, out) : -ENOENT;
}

int games_check_new(const char *name, const char *disc_id)
{
    // HDLGameInstaller names every game partition PP.<disc ID>.HDL.<title>.
    char prefix[48];
    int prefix_length = snprintf(prefix, sizeof(prefix), "PP.%s.", disc_id);
    int dir = fileXioDopen("hdd0:");
    if (dir < 0)
    {
        return dir;
    }

    int status = 0;
    iox_dirent_t entry;
    while (status == 0 && fileXioDread(dir, &entry) > 0)
    {
        if ((entry.stat.attr & APA_FLAG_SUB) != 0)
        {
            continue;
        }

        if (strcmp(entry.name, name) == 0 || (is_game(&entry) && strncmp(entry.name, prefix, prefix_length) == 0))
        {
            status = -EEXIST;
        }
    }

    fileXioDclose(dir);
    return status;
}

int games_find(const char *disc_id, char names[][GAMES_PARTITION_NAME_MAX + 1], int max)
{
    char prefix[48];
    int prefix_length = snprintf(prefix, sizeof(prefix), "PP.%s.", disc_id);
    int dir = fileXioDopen("hdd0:");
    if (dir < 0)
    {
        return dir;
    }

    int count = 0;
    iox_dirent_t entry;
    while (count < max && fileXioDread(dir, &entry) > 0)
    {
        if (is_game(&entry) && strncmp(entry.name, prefix, prefix_length) == 0)
        {
            snprintf(names[count], GAMES_PARTITION_NAME_MAX + 1, "%.32s", entry.name);
            count++;
        }
    }

    fileXioDclose(dir);
    return count;
}
