// Starting an ELF from memory has no POSIX form, so this file calls ps2sdk's
// ELF loader directly. The file is saved with POSIX calls, which reach USB
// drives and the hard disk's mounted partition through fileXio.

#include "upgrade.h"

#include "config.h"
#include "files.h"

#include <elf_loader_common.h>
#include <errno.h>
#include <fcntl.h>
#include <iopcontrol.h>
#include <kernel.h>
#include <loadfile.h>
#include <malloc.h>
#include <sifrpc.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#define TAG_ARGUMENT "--run-tag="
#define PATH_BYTES 256
#define READ_BACK_BYTES (64 * 1024)

static char start_path[PATH_BYTES];
static char start_tag[UPGRADE_TAG_BYTES];
static u8 *elf;
static u32 elf_size;
static u32 elf_received;
static char pending_tag[UPGRADE_TAG_BYTES + 1];
static int pending;
static elf_loader_execinfo_t execinfo;
static u8 read_back[READ_BACK_BYTES] __attribute__((aligned(64)));

static int tag_character(char c)
{
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-';
}

void upgrade_init(int argc, char *argv[])
{
    if (argc >= 1 && argv[0] != NULL)
    {
        snprintf(start_path, sizeof(start_path), "%s", argv[0]);
    }

    for (int i = 1; i < argc; i++)
    {
        if (argv[i] != NULL && strncmp(argv[i], TAG_ARGUMENT, strlen(TAG_ARGUMENT)) == 0)
        {
            const char *tag = argv[i] + strlen(TAG_ARGUMENT);
            memcpy(start_tag, tag, strnlen(tag, sizeof(start_tag)));
        }
    }
}

const char *upgrade_tag(void)
{
    return start_tag;
}

int upgrade_begin(u32 size)
{
    free(elf);
    elf = NULL;
    elf_size = elf_received = 0;
    pending = 0;
    if (size == 0 || size > UPGRADE_MAX_BYTES)
    {
        return -EINVAL;
    }

    elf = memalign(64, size);
    if (elf == NULL)
    {
        return -ENOMEM;
    }

    elf_size = size;
    return 0;
}

int upgrade_data(u32 offset, const u8 *data, u32 length)
{
    if (elf == NULL)
    {
        return -ENOENT;
    }

    if (offset != elf_received || length == 0 || length > elf_size - elf_received)
    {
        return -EINVAL;
    }

    memcpy(elf + offset, data, length);
    elf_received += length;
    return 0;
}

// The whole ELF is here, and ps2sdk's loader can place its segments.
static int check_elf(void)
{
    if (elf == NULL)
    {
        return -ENOENT;
    }

    if (elf_received != elf_size)
    {
        return -EINVAL;
    }

    return elf_loader_is_elf_valid_for_loading(elf, elf_size) == 0 ? 0 : -ENOEXEC;
}

int upgrade_prepare_run(const u8 *tag)
{
    int status = check_elf();
    if (status < 0)
    {
        return status;
    }

    memset(pending_tag, 0, sizeof(pending_tag));
    for (int i = 0; i < UPGRADE_TAG_BYTES && tag[i] != '\0'; i++)
    {
        if (!tag_character((char)tag[i]))
        {
            return -EINVAL;
        }

        pending_tag[i] = (char)tag[i];
    }

    if (pending_tag[0] == '\0' || start_path[0] == '\0')
    {
        return -EINVAL;
    }

    pending = 1;
    return 0;
}

int upgrade_run_pending(void)
{
    return pending;
}

void upgrade_run(void)
{
    char argument[sizeof(TAG_ARGUMENT) + UPGRADE_TAG_BYTES];
    snprintf(argument, sizeof(argument), TAG_ARGUMENT "%s", pending_tag);
    char *arguments[] = {argument};
    pending = 0;
    if (elf_loader_exec_elf_prepare_loadinfo(&execinfo, elf, elf_size) < 0
        || elf_loader_exec_elf_prepare_arginfo(&execinfo, start_path, NULL, 1, arguments) < 0)
    {
        return;
    }

    // As ps2sdk's LoadELFFromFile does. The reset also stops the network
    // adapter's driver writing frames into memory that the new ELF takes.
    while (!SifIopReset(NULL, 0))
    {
    }
    while (!SifIopSync())
    {
    }
    sceSifInitRpc(0);
    SifLoadFileInit();
    SifLoadModule("rom0:SIO2MAN", 0, NULL);
    SifLoadModule("rom0:MCMAN", 0, NULL);
    SifLoadModule("rom0:MCSERV", 0, NULL);
    SifLoadFileExit();
    sceSifExitRpc();
    elf_loader_exec_elf(&execinfo);
}

static int write_file(const char *path)
{
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0666);
    if (fd < 0)
    {
        return -errno;
    }

    int written = (int)write(fd, elf, elf_size);
    int error = written < 0 ? -errno : (u32)written != elf_size ? -EIO : 0;
    if (close(fd) < 0 && error == 0)
    {
        error = -errno;
    }

    return error;
}

static int same_as_elf(const char *path)
{
    int fd = open(path, O_RDONLY);
    if (fd < 0)
    {
        return -errno;
    }

    int status = 0;
    for (u32 offset = 0; offset < elf_size && status == 0;)
    {
        u32 wanted = elf_size - offset < READ_BACK_BYTES ? elf_size - offset : READ_BACK_BYTES;
        int got = (int)read(fd, read_back, wanted);
        status = got < 0 ? -errno : (u32)got != wanted || memcmp(read_back, elf + offset, wanted) != 0 ? -EIO : 0;
        offset += wanted;
    }

    close(fd);
    return status;
}

static int rename_file(const char *from, const char *to)
{
    return rename(from, to) < 0 ? -errno : 0;
}

// Writes the ELF beside the file at elf_path as .part, checks it, and swaps it in, keeping the old one as .bak.
static int replace_elf(const char *elf_path)
{
    char part[PATH_BYTES + 8];
    char backup[PATH_BYTES + 8];
    snprintf(part, sizeof(part), "%s.part", elf_path);
    snprintf(backup, sizeof(backup), "%s.bak", elf_path);
    remove(part);
    int status = write_file(part);
    if (status == 0)
    {
        status = same_as_elf(part);
    }

    if (status < 0)
    {
        remove(part);
        return status;
    }

    remove(backup);
    status = rename_file(elf_path, backup);
    if (status < 0 && status != -ENOENT)
    {
        remove(part);
        return status;
    }

    status = rename_file(part, elf_path);
    if (status < 0)
    {
        // Put the old file back under its name; the new one stays as .part.
        rename_file(backup, elf_path);
    }

    return status;
}

int upgrade_save(void)
{
    int status = check_elf();
    if (status < 0)
    {
        return status;
    }

    // Only the ELF file this copy runs from, on a USB drive or the hard disk.
    char partition[40];
    char inner[PATH_BYTES];
    if (config_split_hdd_path(start_path, partition, sizeof(partition), inner, sizeof(inner)))
    {
        status = files_mount_partition(partition, 1);
        if (status < 0)
        {
            return status;
        }

        char path[PATH_BYTES + 8];
        snprintf(path, sizeof(path), "pfs0:%s", inner);
        status = replace_elf(path);
        files_close();
        return status;
    }

    if (strncmp(start_path, "mass", 4) != 0)
    {
        return -EPERM;
    }

    return replace_elf(start_path);
}
